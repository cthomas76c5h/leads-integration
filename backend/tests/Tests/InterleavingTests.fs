/// Every interleaving, up to a bound, of two workers, an operator and an adversarial receiver acting on
/// one lead, run against the real Store and Dispatch code on a real SQLite file, with the invariants of
/// docs/lead-delivery/concurrency.md §9 checked after every step.
///
/// How: a depth-first search over actions. Each state is a private in-memory copy of the database
/// (cloned with SQLite's backup API) plus what each worker holds in memory and what the receiver kept;
/// an action runs on a fresh clone of its parent, so every branch starts from exactly the state it names. States already seen (same rows, same
/// worker stages, same receiver record) are not explored twice. The bound is the number of actions
/// (INTERLEAVING_DEPTH, default 8); the receiver, time and operator actions are limited per run.
module LeadIntegrations.Tests.InterleavingTests

open System
open System.Collections.Generic
open System.IO
open Expecto
open Microsoft.Data.Sqlite
open LeadIntegrations
open LeadIntegrations.Domain

// ----- The model -----

/// What a worker holds in memory between its steps: lost when it crashes.
type private Stage =
    | Idle
    | Claimed of id: string * version: int64 * recovered: bool
    | Started of id: string * version: int64 * n: int * connectionId: string * at: int64
    | Sent of id: string * version: int64 * n: int * connectionId: string * at: int64 * kept: bool

/// What we hear back. Whether the receiver kept the lead is decided when the request arrives (Send),
/// as at a real receiver; an answer must agree with it: 201 only if kept, 503 only if not.
type private Reply =
    | Accepted
    | Timeout
    | Refused503

type private Node =
    { Db: Db.Database
      /// Keeps the in-memory database alive; disposed when the state has been explored.
      Anchor: SqliteConnection
      Clock: int64
      Workers: Map<string, Stage>
      /// Each request the receiver got: the delivery, and whether it kept the lead.
      Received: (string * bool) list
      TimeLeft: int
      OperatorLeft: int
      Trace: string list }

type private Action =
    | Claim of string
    | Recover of string
    | Begin of string
    | Send of string * kept: bool
    | Answer of string * Reply
    | Crash of string
    | CrashAfterSend of string
    | TimePasses
    | Pause
    | Resume
    | Resend
    | Close

let private describe =
    function
    | Claim w -> $"{w} claims"
    | Recover w -> $"{w} recovers"
    | Begin w -> $"{w} begins the attempt"
    | Send(w, kept) -> $"""{w} sends (the receiver {if kept then "keeps it" else "loses it"})"""
    | Answer(w, r) -> $"{w} gets the answer: {r}"
    | Crash w -> $"{w} crashes"
    | CrashAfterSend w -> $"{w} crashes after sending"
    | TimePasses -> "6 minutes pass"
    | Pause -> "operator pauses"
    | Resume -> "operator resumes"
    | Resend -> "operator resends"
    | Close -> "operator closes the unknown outcome"

let private t0 = DateTimeOffset(2026, 11, 16, 13, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()

let private opts: Dispatch.Options =
    { TimeScale = 1.0; TimeoutMs = 1000; Address = { AllowLocal = true }; UserAgent = "interleaving"; Origin = "http://localhost" }

// ----- Databases: a template file, then one in-memory clone per state -----

let private remove (path: string) =
    for suffix in [ ""; "-wal"; "-shm" ] do
        try File.Delete(path + suffix) with _ -> ()

let private newFile () = Path.Combine(Path.GetTempPath(), $"interleaving-{Guid.NewGuid():N}.db")

/// A clone of `source`'s database in memory, and the connection that keeps it alive.
let private clone (source: SqliteConnection) =
    let db = Db.Database.InMemory($"interleaving-{Guid.NewGuid():N}")
    let anchor = new SqliteConnection(db.ConnectionString)
    anchor.Open()
    source.BackupDatabase anchor
    db, anchor

/// One firm, one destination of `kind`, one production lead planned to it, at t0.
let private template (kind: string) =
    let path = newFile ()
    let db = Db.openDatabase path
    let creds = if kind = "email" then [] else [ ("api_key", "x") ]
    Import.apply db Support.origin (Support.batch Source.api2 [ Support.firm "1" "Interleaving Firm" "k" [ Support.conn "10" kind creds ] ]) |> ignore
    let firmId = Db.read db (fun s -> (Store.allFirms s |> Array.head).Id)
    setClock t0
    let _, ds = Dispatch.publish db firmId (Support.sampleLead ()) false None None
    if ds.Length <> 1 || ds[0].Status <> "pending" then failwith $"template: expected one planned delivery to {kind}"
    SqliteConnection.ClearAllPools()
    path

// ----- Reading a state -----

type private Row =
    { Id: string
      Status: string
      Reason: string
      NextAt: Nullable<int64>
      LeaseVersion: int64
      LeaseUntil: Nullable<int64>
      Resolution: string
      ReplayOf: string
      Started: int }

let private rows (db: Db.Database) =
    Db.read db (fun s ->
        s.Query
            """SELECT d.id, d.status, d.reason, d.next_attempt_at, d.lease_version, d.lease_until, d.resolution, d.replay_of,
                      (SELECT COUNT(*) FROM attempts a WHERE a.delivery_id = d.id)
               FROM deliveries d ORDER BY d.created_at, d.rowid"""
            []
            (fun r ->
                { Id = Db.Read.str r 0; Status = Db.Read.str r 1; Reason = Db.Read.str r 2; NextAt = Db.Read.int64N r 3
                  LeaseVersion = Db.Read.int64 r 4; LeaseUntil = Db.Read.int64N r 5; Resolution = Db.Read.str r 6
                  ReplayOf = Db.Read.str r 7; Started = Db.Read.int r 8 }))

let private connection (db: Db.Database) = Db.read db (fun s -> (Store.allConnections s |> Array.head).Connection)

let private outcomes (db: Db.Database) (id: string) =
    Db.read db (fun s -> s.Query "SELECT outcome FROM attempts WHERE delivery_id=$1 ORDER BY n" [ id ] (fun r -> Db.Read.str r 0))

/// What makes two states the same: the rows (by position, not by their random ids), the destination,
/// each worker's stage (by row position), what the receiver kept, and what is left to spend.
let private key (db: Db.Database) (n: Node) =
    let rs = rows db
    let index (id: string) = rs |> Array.findIndex (fun r -> r.Id = id)
    let due (v: Nullable<int64>) = if not v.HasValue then "-" elif v.Value <= n.Clock then "due" else "later"
    let rowKey (r: Row) =
        let kind = if isNull r.Resolution then "" else (Json.deserialize<Resolution> r.Resolution).Kind
        let outs = String.Join("/", outcomes db r.Id |> Array.map (fun o -> o.Split(':')[0]))
        $"{r.Status}|{due r.NextAt}|v{r.LeaseVersion}|{due r.LeaseUntil}|{kind}|{outs}"
    let stage =
        function
        | Idle -> "idle"
        | Claimed(id, v, rec') -> $"claimed {index id} v{v} {rec'}"
        | Started(id, v, k, _, _) -> $"started {index id} v{v} #{k}"
        | Sent(id, v, k, _, _, kept) -> $"sent {index id} v{v} #{k} {kept}"
    String.Join(
        ";",
        [ yield! rs |> Array.map rowKey
          yield $"enabled={(connection db).Enabled}"
          for KeyValue(w, st) in n.Workers do yield $"{w}:{stage st}"
          yield String.Join(",", n.Received |> List.map (fun (id, kept) -> $"{index id}{(if kept then 'k' else 'x')}"))
          yield $"time{n.TimeLeft} op{n.OperatorLeft}" ]
    )

// ----- Invariants -----

exception private Violated of string

/// The situations a run reached: a run that never reaches a race has not tested it.
let private reached = HashSet<string>()
let private saw (what: string) = reached.Add what |> ignore

let private check (cond: bool) (what: string) = if not cond then raise (Violated what)

/// The invariants of concurrency.md §9 that hold in every state.
let private checkState (db: Db.Database) (n: Node) (afterUnknown: string) =
    let rs = rows db
    // 1. At most one worker holds a valid lease on a delivery.
    for r in rs do
        let holders =
            n.Workers
            |> Map.toList
            |> List.filter (fun (_, st) ->
                match st with
                | Claimed(id, v, _) | Started(id, v, _, _, _) | Sent(id, v, _, _, _, _) when id = r.Id ->
                    Db.read db (fun s -> Store.holdsLease s id v)
                | _ -> false)
        check (holders.Length <= 1) $"two workers hold the lease on one delivery: {holders |> List.map fst}"
    // 4. Where a second copy is not safe, our automatic sends never leave two copies at the destination.
    //    (An operator's resend is a deliberate decision after checking, and is not counted.)
    if afterUnknown <> "resend" then
        let automatic = rs |> Array.filter (fun r -> isNull r.ReplayOf) |> Array.map _.Id |> set
        let kept = n.Received |> List.filter (fun (id, k) -> k && automatic.Contains id) |> List.length
        check (kept <= 1) $"the destination kept {kept} copies from automatic sends"
    // 5. Accepted means the destination has it (or an operator found it there).
    for r in rs do
        if r.Status = "delivered" then
            let found = not (isNull r.Resolution) && (Json.deserialize<Resolution> r.Resolution).Kind = "found"
            check (found || n.Received |> List.exists snd) "delivered, but the destination never kept the lead"
    // 6. Every started attempt that is not open in a worker has an outcome; nothing is stuck 'started' unseen
    //    except while its lease can still be recovered.
    for r in rs do
        if r.Status = "delivering" then check r.LeaseUntil.HasValue "a leased delivery with no expiry"

// ----- Running one action -----

let private run (n: Node) (a: Action) : Node option =
    let db, anchor = clone n.Anchor
    setClock n.Clock
    let before = rows db
    let rowOf (rs: Row[]) id = rs |> Array.find (fun r -> r.Id = id)
    let set w st = { n with Workers = n.Workers.Add(w, st) }
    let next: Node option =
        match a with
        | Claim w ->
            match Db.write db (fun s -> Store.claimDue s w n.Clock Dispatch.leaseMs 1) |> Array.tryHead with
            | Some c -> Some(set w (Claimed(c.DeliveryId, c.Version, c.Recovered)))
            | None -> None
        | Recover w ->
            match n.Workers[w] with
            | Claimed(id, v, true) ->
                let goOn = Dispatch.recover db { DeliveryId = id; Version = v; Recovered = true }
                if (rows db |> Array.find (fun r -> r.Id = id)).Status = "unknown" then saw "recovery leaves an unknown outcome for an operator"
                Some(set w (if goOn then Claimed(id, v, false) else Idle))
            | _ -> None
        | Begin w ->
            match n.Workers[w] with
            | Claimed(id, v, false) ->
                let paused = not (connection db).Enabled
                let stale = not (Db.read db (fun s -> Store.holdsLease s id v))
                let result = Dispatch.beginAttempt db opts.Origin id v
                let after = rowOf (rows db) id
                if stale then saw "a stale worker begins"
                if paused then saw "a worker begins on a paused destination"
                // 3. Once a pause has committed, no attempt starts.
                if paused then check (after.Started = (rowOf before id).Started) "an attempt started on a paused destination"
                // 2. A worker whose lease no longer holds writes nothing.
                if stale then check (after = rowOf before id) "a stale worker changed the delivery when beginning"
                match result with
                | Dispatch.Started(c, _, _, k) -> Some(set w (Started(id, v, k, c.Connection.Id, n.Clock)))
                | Dispatch.Stopped -> Some(set w Idle)
            | _ -> None
        | Send(w, kept) ->
            match n.Workers[w] with
            | Started(id, v, k, c, at) -> Some { set w (Sent(id, v, k, c, at, kept)) with Received = (id, kept) :: n.Received }
            | _ -> None
        | Answer(w, reply) ->
            match n.Workers[w] with
            | Sent(_, _, _, _, _, kept) when (reply = Accepted && not kept) || (reply = Refused503 && kept) -> None
            | Sent(id, v, k, c, at, _) ->
                let stale = not (Db.read db (fun s -> Store.holdsLease s id v))
                let answer: Dispatch.Answer =
                    match reply with
                    | Accepted -> { Status = Some 201; Error = null; RetryAfter = null; Response = "accepted"; Refused = false; TimedOut = false; DurationMs = 20L }
                    | Refused503 -> { Status = Some 503; Error = null; RetryAfter = null; Response = "down"; Refused = false; TimedOut = false; DurationMs = 20L }
                    | Timeout -> { Status = None; Error = "no answer in 1000 ms"; RetryAfter = null; Response = ""; Refused = false; TimedOut = true; DurationMs = 1000L }
                Dispatch.complete db opts (fun () -> 0.5) id v c k at answer
                let after = rowOf (rows db) id
                if stale then
                    let b = rowOf before id
                    // The one write a stale answer may make: an accepted answer settles a delivery not yet settled
                    // and not resolved by an operator (concurrency.md §4.1).
                    let settles =
                        reply = Accepted && List.contains b.Status [ "unknown"; "delivering"; "pending" ] && isNull b.Resolution
                        && after.Status = "delivered"
                    check (settles || after = b) "a stale worker changed the delivery with its answer"
                    saw "a stale worker's answer arrives"
                    if settles then saw "a late answer settles the delivery"
                Some(set w Idle)
            | _ -> None
        | Crash w ->
            match n.Workers[w] with
            | Idle -> None
            | Sent _ -> None // crashing after the send is CrashAfterSend
            | _ -> Some(set w Idle)
        | CrashAfterSend w ->
            match n.Workers[w] with
            | Sent _ -> Some(set w Idle)
            | _ -> None
        | TimePasses when n.TimeLeft > 0 -> Some { n with Clock = n.Clock + 6L * 60_000L; TimeLeft = n.TimeLeft - 1 }
        | TimePasses -> None
        | Pause
        | Resume when n.OperatorLeft > 0 ->
            let c = connection db
            let enable = (a = Resume)
            if c.Enabled = enable then None
            else
                Db.write db (fun s ->
                    let x = Dispatch.setPaused s c enable
                    Store.updateConnection s x
                    if x.Enabled && x.Status = "active" then Store.releaseHeld s x.Id n.Clock |> ignore)
                Some { n with OperatorLeft = n.OperatorLeft - 1 }
        | Resend
        | Close when n.OperatorLeft > 0 ->
            let newest = rows db |> Array.last
            try
                if a = Resend then Dispatch.replay db newest.Id { By = "Op"; Role = "operator"; Why = "checked" } |> ignore
                else
                    Dispatch.resolve db newest.Id "closed" "Op" "checked" |> ignore
                    saw "an operator closes an unknown outcome"
                Some { n with OperatorLeft = n.OperatorLeft - 1 }
            with Fail _ -> None // refused: not a step that happened
        | Pause | Resume | Resend | Close -> None
    match next with
    | None ->
        anchor.Dispose()
        None
    | Some m -> Some { m with Db = db; Anchor = anchor; Trace = describe a :: n.Trace }

let private actions (workers: string list) =
    [ for w in workers do
          yield Claim w
          yield Recover w
          yield Begin w
          yield Send(w, true)
          yield Send(w, false)
          for r in [ Accepted; Timeout; Refused503 ] do yield Answer(w, r)
          yield Crash w
          yield CrashAfterSend w
      yield TimePasses
      yield Pause
      yield Resume
      yield Resend
      yield Close ]

type private Result =
    { States: int
      Transitions: int
      Violation: (string * string list) option
      Reached: Set<string>
      AfterUnknown: string }

/// Explore every interleaving from the template up to `depth` actions.
let private explore (kind: string) (depth: int) : Result =
    let afterUnknown =
        let path = Path.Combine(Db.repoRoot, "docs", "contracts", "receivers", $"{kind}.json")
        use doc = Text.Json.JsonDocument.Parse(File.ReadAllText path)
        doc.RootElement.GetProperty("afterUnknown").GetString()
    let workers = [ "A"; "B" ]
    let file = template kind
    let rootDb, rootAnchor =
        use source = new SqliteConnection($"Data Source={file}")
        source.Open()
        clone source
    SqliteConnection.ClearAllPools()
    remove file
    let root =
        { Db = rootDb; Anchor = rootAnchor; Clock = t0; Workers = workers |> List.map (fun w -> w, Idle) |> Map.ofList; Received = []
          TimeLeft = 2; OperatorLeft = 2; Trace = [] }
    /// Each state seen, with the most actions it had left when seen: one reached again with more left is explored again.
    let seen = Dictionary<string, int>()
    reached.Clear()
    let mutable transitions = 0
    let mutable violation = None
    let all = actions workers
    let rec visit (n: Node) (left: int) =
        if violation.IsNone && left > 0 then
            for a in all do
                if violation.IsNone then
                    let child =
                        try run n a
                        with Violated what ->
                            violation <- Some(what, List.rev (describe a :: n.Trace))
                            None
                    match child with
                    | None -> ()
                    | Some c ->
                        transitions <- transitions + 1
                        try
                            try
                                checkState c.Db c afterUnknown
                                let k = key c.Db c
                                match seen.TryGetValue k with
                                | true, had when had >= left - 1 -> ()
                                | _ ->
                                    seen[k] <- left - 1
                                    visit c (left - 1)
                            with Violated what -> violation <- Some(what, List.rev c.Trace)
                        finally
                            c.Anchor.Dispose()
    try
        seen[key root.Db root] <- depth
        visit root depth
    finally
        root.Anchor.Dispose()
        resetClock ()
    { States = seen.Count; Transitions = transitions; Violation = violation; Reached = Set.ofSeq reached; AfterUnknown = afterUnknown }

let private depth =
    match Environment.GetEnvironmentVariable "INTERLEAVING_DEPTH" with
    | null | "" -> 8
    | v -> int v

let private proves (kind: string) =
    test $"every interleaving to depth {depth}: {kind}" {
        let r = explore kind depth
        match r.Violation with
        | Some(what, trace) ->
            let steps = trace |> List.mapi (fun i s -> $"  {i + 1}. {s}") |> String.concat "\n"
            failtest $"{what}, after:\n{steps}"
        | None -> ()
        Expect.isGreaterThan r.States 50 $"explored {r.States} states, {r.Transitions} transitions"
        // The races this run must have reached for its "no invariant broken" to mean anything.
        let must =
            [ "a stale worker begins"; "a stale worker's answer arrives"; "a worker begins on a paused destination"; "a late answer settles the delivery"
              if r.AfterUnknown <> "resend" then
                  yield! [ "recovery leaves an unknown outcome for an operator"; "an operator closes an unknown outcome" ] ]
        for m in must do
            Expect.isTrue (r.Reached.Contains m) $"never reached: {m} (raise INTERLEAVING_DEPTH)"
        printfn $"interleavings ({kind}, depth {depth}): {r.States} distinct states, {r.Transitions} transitions, no invariant broken"
    }

/// The first interleaving the search found that sent a CRM the lead twice, kept as a plain regression test:
/// A sends and its lease runs out; B claims as a recovery; A's answer (no answer, or accepted) arrives before
/// B recovers. B must not conclude that nothing was sent.
let private lateAnswerDuringTakeover (reply: Reply) =
    test $"a late answer between a takeover and its recovery never makes a second send ({reply})" {
        let file = template "mycase"
        let db = Db.Database(file)
        try
            let id = (rows db |> Array.head).Id
            let a = Db.write db (fun s -> Store.claimDue s "A" t0 Dispatch.leaseMs 1) |> Array.head
            let n, connectionId =
                match Dispatch.beginAttempt db opts.Origin id a.Version with
                | Dispatch.Started(c, _, _, n) -> n, c.Connection.Id
                | Dispatch.Stopped -> failtest "A could not begin"
            let later = t0 + Dispatch.leaseMs + 60_000L
            setClock later
            let b = Db.write db (fun s -> Store.claimDue s "B" later Dispatch.leaseMs 1) |> Array.head
            Expect.isTrue b.Recovered "B takes over an expired lease"
            let answer: Dispatch.Answer =
                match reply with
                | Accepted -> { Status = Some 201; Error = null; RetryAfter = null; Response = ""; Refused = false; TimedOut = false; DurationMs = 20L }
                | _ -> { Status = None; Error = "no answer in 1000 ms"; RetryAfter = null; Response = ""; Refused = false; TimedOut = true; DurationMs = 1000L }
            Dispatch.complete db opts (fun () -> 0.5) id a.Version connectionId n t0 answer
            Expect.isFalse (Dispatch.recover db b) "B does not go on to send"
            let expected = if reply = Accepted then "delivered" else "unknown"
            Expect.equal (rows db |> Array.head).Status expected "settled by the answer, or left for an operator"
        finally
            SqliteConnection.ClearAllPools()
            remove file
            resetClock ()
    }

[<Tests>]
let tests =
    // Sequenced: the search sets the shared clock.
    testSequenced (
        testList "interleavings" [
            lateAnswerDuringTakeover Accepted
            lateAnswerDuringTakeover Timeout
            proves "mycase"
            proves "email"
        ]
    )
