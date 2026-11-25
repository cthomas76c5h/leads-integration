/// RFC-011 step 3: what must hold when writers race, on SQLite and on PostgreSQL. Each racer has a
/// `Database` of its own, as a separate process would (no shared in-process lock), and all start at
/// once. On PostgreSQL the writers really run concurrently; the guarantees rest on conditional
/// updates and unique indexes, with a lost race run again (`Db.write`).
module LeadIntegrations.Tests.ConcurrencyTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open LeadIntegrations
open LeadIntegrations.Domain
open LeadIntegrations.Tests.Support

let private racers = 8

/// Runs `f` on `racers` threads, each with its own handle on the database, all released at once.
/// Returns each racer's result, or the exception it ended with.
let private race (t: TempDb) (f: Db.Database -> int -> 'T) : Result<'T, exn>[] =
    let gate = new ManualResetEventSlim(false)
    let tasks =
        [| for i in 1..racers ->
               Task.Run(fun () ->
                   let db = Db.Database(t.Db.Path)
                   gate.Wait()
                   try Ok(f db i) with e -> Error e) |]
    gate.Set()
    Task.WaitAll(tasks |> Array.map (fun t -> t :> Task), TimeSpan.FromMinutes 2.0) |> ignore
    tasks |> Array.map _.Result

let private oks results = results |> Array.choose (function Ok v -> Some v | Error _ -> None)
let private errors results = results |> Array.choose (function Error e -> Some e | Ok _ -> None)

let private count (db: Db.Database) (sql: string) =
    Db.read db (fun s -> s.Query sql [] (fun r -> r.GetInt64 0) |> Array.head)

/// A firm with one email destination, and the aliases the contract examples use.
let private firmWithEmail (t: TempDb) =
    let email = { conn "10" "email" [] with Settings = Collections.Generic.Dictionary(dict [ "recipients", "intake@example.com" ]) }
    Import.apply t.Db origin (batch Source.legacy [ firm "1" "Race Firm" "k" [ email ] ]) |> ignore
    let firmId = Db.read t.Db (fun s -> (Store.allFirms s |> Array.head).Id)
    Db.write t.Db (fun s -> Store.setFirmAlias s "injury-firm" firmId (now ()) |> ignore)
    firmId

let private personCall () =
    File.ReadAllText(Path.Combine(Db.repoRoot, "docs", "contracts", "intake-lead", "examples", "person-existing-client.json"))

let private describe (es: exn[]) = String.Join("; ", es |> Array.map _.Message)

[<Tests>]
let tests =
    testList "concurrency" [
        test "a channel's event sent by several writers at once is one lead, with one delivery" {
            use t = new TempDb()
            let firmId = firmWithEmail t
            let results = race t (fun db _ -> Dispatch.intake db firmId "call-race-1" (sampleLead ()))
            Expect.isEmpty (errors results) (describe (errors results))
            let leads = oks results
            Expect.equal (leads |> Array.map _.Event.Id |> Array.distinct).Length 1 "one lead"
            Expect.equal (leads |> Array.filter (fun r -> not r.Duplicate)).Length 1 "recorded once; every other writer was told it was a repeat"
            Expect.equal (count t.Db "SELECT COUNT(*) FROM events") 1L "one event"
            Expect.equal (count t.Db "SELECT COUNT(*) FROM deliveries") 1L "one delivery"
        }

        test "a contract lead published by several writers at once is one lead; a changed one is a conflict" {
            use t = new TempDb()
            firmWithEmail t |> ignore
            let same = race t (fun db _ -> Dispatch.ingest db (personCall ()))
            Expect.isEmpty (errors same) (describe (errors same))
            Expect.equal (oks same |> Array.map _.Intake.Event.Id |> Array.distinct).Length 1 "one lead"
            Expect.equal (oks same |> Array.filter (fun r -> not r.Intake.Duplicate)).Length 1 "recorded once"
            // Each racer sends its own version of another interaction: exactly one wins, the rest conflict.
            let changed = race t (fun db i -> Dispatch.ingest db (personCall().Replace("\"CC-1\"", "\"CC-race\"").Replace("555-0100", $"555-01{i:D2}")))
            Expect.equal (oks changed).Length 1 "one version recorded"
            Expect.isTrue (errors changed |> Array.forall (function Fail(409, _) -> true | _ -> false)) (describe (errors changed))
            Expect.equal (count t.Db "SELECT COUNT(*) FROM lead_contracts") 2L "two leads in all"
        }

        test "a firm provisioned by several writers at once is one firm" {
            use t = new TempDb()
            let request: Provisioning.Request = { Name = "Race Firm"; TimeZone = null; AccountId = "acct-race"; Adopt = null }
            let results = race t (fun db _ -> Db.write db (fun s -> Provisioning.provision s "frm-race-1" request (now ())))
            Expect.isEmpty (errors results) (describe (errors results))
            Expect.equal (oks results |> Array.map (fst >> _.FirmId) |> Array.distinct).Length 1 "one firm"
            Expect.equal (oks results |> Array.filter snd).Length 1 "created once"
            Expect.equal (count t.Db "SELECT COUNT(*) FROM firms") 1L "one row"
            Expect.equal (count t.Db "SELECT COUNT(*) FROM domain_events WHERE name = 'FirmProvisioned'") 1L "provisioned once"
        }

        test "a lead resent by several operators at once goes out once more, not once per operator" {
            // Dispatch.replay checks that nothing is still on its way to the destination, then adds a
            // delivery: a check then a write, with no unique index behind it. It holds only because
            // writes are serialisable (one writer on SQLite; SERIALIZABLE on PostgreSQL).
            use t = new TempDb()
            let firmId = firmWithEmail t
            let first = Dispatch.intake t.Db firmId "call-resend" (sampleLead ())
            let original = first.Deliveries[0].Id
            Db.write t.Db (fun s -> Store.setDeliveryState s original "failed" "refused for the test" (Nullable()))
            let results = race t (fun db i -> Dispatch.replay db original { By = $"operator-{i}"; Role = "operator"; Why = "the firm asked" })
            Expect.equal (oks results).Length 1 "one resend made"
            Expect.isTrue (errors results |> Array.forall (function Fail(409, _) -> true | _ -> false)) (describe (errors results))
            Expect.equal (count t.Db "SELECT COUNT(*) FROM deliveries") 2L "the original and one resend"
        }

        test "a pause can't commit between an attempt's check of its destination and the attempt" {
            // beginAttempt locks the destination's row before checking it (concurrency.md §7). Here a
            // transaction takes that lock and holds it; an operator's pause must wait until it commits.
            use t = new TempDb()
            firmWithEmail t |> ignore
            let connectionId = (Db.read t.Db Store.allConnections).[0].Connection.Id
            let locked = new ManualResetEventSlim(false)
            let release = new ManualResetEventSlim(false)
            let attempt =
                Task.Run(fun () ->
                    let db = Db.Database(t.Db.Path)
                    Db.write db (fun s ->
                        Store.lockConnection s connectionId
                        locked.Set()
                        release.Wait(TimeSpan.FromSeconds 10.0) |> ignore))
            Expect.isTrue (locked.Wait(TimeSpan.FromSeconds 10.0)) "the attempt's transaction holds the lock"
            let pause =
                Task.Run(fun () ->
                    let db = Db.Database(t.Db.Path)
                    Db.write db (fun s ->
                        let c = (Store.connectionOrFail s connectionId).Connection
                        Store.updateConnection s (Dispatch.setPaused s c false)))
            Expect.isFalse (pause.Wait(TimeSpan.FromMilliseconds 500.0)) "the pause waits while the attempt is being decided"
            release.Set()
            Expect.isTrue (attempt.Wait(TimeSpan.FromSeconds 10.0)) "the attempt commits"
            Expect.isTrue (pause.Wait(TimeSpan.FromSeconds 10.0)) "then the pause commits"
            Expect.isFalse (Db.read t.Db (fun s -> (Store.connectionOrFail s connectionId).Connection.Enabled)) "paused"
        }

        test "deliveries claimed by several workers at once are each claimed by one" {
            use t = new TempDb()
            let firmId = firmWithEmail t
            for i in 1..60 do
                Dispatch.intake t.Db firmId $"call-claim-{i}" (sampleLead ()) |> ignore
            // Everything is due now; each worker claims in small batches and finishes them, until nothing
            // is left. One destination holds at most Store.inFlightPerDestination leases at once.
            let later = now () + 3_600_000L
            let results =
                race t (fun db i ->
                    let mine = ResizeArray<string>()
                    let mutable batch = [| () |] |> Array.map (fun _ -> Unchecked.defaultof<Store.Claim>)
                    while batch.Length > 0 do
                        batch <- Db.writeConditional db (fun s -> Store.claimDue s $"worker-{i}" later Dispatch.leaseMs 3)
                        mine.AddRange(batch |> Array.map _.DeliveryId)
                        // Finished, as a worker that sent them would: the destination's send slots free up.
                        Db.write db (fun s ->
                            for c in batch do
                                s.Execute "UPDATE deliveries SET status='delivered' WHERE id=$1 AND lease_version=$2" [ c.DeliveryId; c.Version ] |> ignore)
                    mine.ToArray())
            Expect.isEmpty (errors results) (describe (errors results))
            let claimed = oks results |> Array.concat
            Expect.equal claimed.Length 60 "every delivery claimed"
            Expect.equal (claimed |> Array.distinct).Length 60 "none claimed twice"
            Expect.equal (count t.Db "SELECT COUNT(*) FROM deliveries WHERE lease_version = 1") 60L "each lease taken once"
        }
    ]
