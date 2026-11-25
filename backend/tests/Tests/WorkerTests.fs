/// The worker's throughput and fairness (concurrency.md §5, §7): a slow destination delays only its own
/// deliveries; no destination or firm takes every send slot; and two writers with separate locks (two
/// processes, as far as SQLite is concerned) share one database file without a lost or doubled send.
module LeadIntegrations.Tests.WorkerTests

open System
open System.Collections.Concurrent
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Expecto
open LeadIntegrations
open LeadIntegrations.Domain

/// A receiver that accepts every request, slowly for any URL carrying `slow`.
type private Receivers() =
    inherit HttpMessageHandler()
    override _.SendAsync(req: HttpRequestMessage, ct: CancellationToken) =
        task {
            if req.RequestUri.Query.Contains "slow" then do! Task.Delay(3000, ct)
            return new HttpResponseMessage(HttpStatusCode.Created, Content = new StringContent("{}"))
        }

let private opts: Dispatch.Options =
    { TimeScale = 1.0; TimeoutMs = 10_000; Address = { AllowLocal = true }; UserAgent = "worker-tests"; Origin = "http://localhost" }

/// Firms, each with one email destination at the local receiver; returns firm ids and connection ids.
let private firms (t: Support.TempDb) (names: string list) =
    let imported = names |> List.mapi (fun i n -> Support.firm (string (i + 1)) n $"k{i}" [ Support.conn (string (10 + i)) "email" [] ])
    Import.apply t.Db Support.origin (Support.batch Source.legacy imported) |> ignore
    Db.read t.Db (fun s ->
        [ for n in names do
              let f = Store.allFirms s |> Array.find (fun f -> f.Name = n)
              f.Id, (Store.connectionsOf s f.Id |> Array.head).Connection.Id ])

let private statusOf (db: Db.Database) (eventId: string) =
    Db.read db (fun s -> (Store.leadOrFail s eventId).Deliveries |> Array.head) |> _.Status

let private waitFor (timeoutMs: int) (cond: unit -> bool) =
    let watch = Diagnostics.Stopwatch.StartNew()
    while not (cond ()) && watch.ElapsedMilliseconds < int64 timeoutMs do
        Thread.Sleep 25
    cond ()

let private emergency () = { Support.sampleLead () with CallType = "*Emergency Calls" }
let private routine () = { Support.sampleLead () with CallType = "*New Client Calls" }

[<Tests>]
let tests =
    testList "worker" [
        test "a slow destination delays only its own deliveries" {
            use t = new Support.TempDb()
            let ids = firms t [ "Slow Firm"; "Quick Firm" ]
            let slowFirm, slowConn = ids[0]
            let quickFirm, _ = ids[1]
            Db.write t.Db (fun s ->
                let c = (Store.connectionOrFail s slowConn).Connection
                Store.updateConnection s { c with DeliveryUrl = c.DeliveryUrl + "?slow=1" })
            use http = new HttpClient(new Receivers())
            use stop = new CancellationTokenSource()
            let slow, _ = Dispatch.publish t.Db slowFirm (routine ()) false None None
            let worker = Dispatch.work t.Db http opts (fun () -> 0.5) (fun _ _ -> ()) stop.Token
            Expect.isTrue (waitFor 2000 (fun () -> statusOf t.Db slow.Id = "delivering")) "the slow send is on the wire"
            let quick, _ = Dispatch.publish t.Db quickFirm (routine ()) false None None
            Expect.isTrue (waitFor 1500 (fun () -> statusOf t.Db quick.Id = "delivered")) "the lead due meanwhile is not held behind it"
            Expect.equal (statusOf t.Db slow.Id) "delivering" "while the slow one is still waiting for its answer"
            Expect.isTrue (waitFor 5000 (fun () -> statusOf t.Db slow.Id = "delivered")) "and the slow one completes"
            stop.Cancel()
            worker.Wait(5000) |> ignore
        }

        test "one destination never holds more than its share of send slots" {
            use t = new Support.TempDb()
            let ids = firms t [ "Busy Firm"; "Other Firm" ]
            let busyFirm, busyConn = ids[0]
            let otherFirm, otherConn = ids[1]
            // The busy firm's 30 emergencies are all due before the other firm's one routine lead.
            for _ in 1..30 do Dispatch.publish t.Db busyFirm (emergency ()) false None None |> ignore
            let other, _ = Dispatch.publish t.Db otherFirm (routine ()) false None None
            let now' = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1_000L
            let claims = Db.write t.Db (fun s -> Store.claimDue s "wkr_a" now' Dispatch.leaseMs 20)
            let connOf (c: Store.Claim) = Db.read t.Db (fun s -> (Store.deliveryOrFail s c.DeliveryId).Delivery.ConnectionId)
            let busy = claims |> Array.filter (fun c -> connOf c = busyConn) |> Array.length
            Expect.equal busy Store.inFlightPerDestination "the busy destination gets its share"
            Expect.isTrue (claims |> Array.exists (fun c -> connOf c = otherConn)) "the other firm's lead is not hidden behind the backlog"
            Expect.equal (statusOf t.Db other.Id) "delivering" "and is being sent"
            // While those leases run, the busy destination gets nothing more.
            let again = Db.write t.Db (fun s -> Store.claimDue s "wkr_b" now' Dispatch.leaseMs 20)
            Expect.isEmpty again "its slots are full until a send finishes"
        }

        test "writers in separate processes share one database: every lead sent once, nothing lost" {
            // MULTIWRITER_LEADS and MULTIWRITER_WRITERS raise the contention (default 40 leads, 2 writers).
            use t = new Support.TempDb()
            let ids = firms t [ "Firm One"; "Firm Two"; "Firm Three"; "Firm Four" ]
            let leads = match Environment.GetEnvironmentVariable "MULTIWRITER_LEADS" with | null | "" -> 40 | v -> int v
            let writers = match Environment.GetEnvironmentVariable "MULTIWRITER_WRITERS" with | null | "" -> 2 | v -> int v
            for i in 0 .. leads - 1 do
                let firmId, _ = ids[i % ids.Length]
                Dispatch.publish t.Db firmId (if i % 3 = 0 then emergency () else routine ()) false None None |> ignore
            Npgsql.NpgsqlConnection.ClearAllPools()
            // One Database value per writer on one database: no shared in-process state, so they meet only in
            // PostgreSQL, as separate processes would.
            let errors = ConcurrentBag<string>()
            let starts = ConcurrentDictionary<string, int>()
            let run (db: Db.Database) (name: string) =
                Task.Run(fun () ->
                    let mutable idle = 0
                    while idle < 20 do
                        try
                            let claims = Db.write db (fun s -> Store.claimDue s name (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1_000L) Dispatch.leaseMs 5)
                            if claims.Length = 0 then
                                idle <- idle + 1
                                Thread.Sleep 10
                            else
                                idle <- 0
                                for c in claims do
                                    match Dispatch.beginAttempt db opts.Origin c.DeliveryId c.Version with
                                    | Dispatch.Started(conn, _, _, n) ->
                                        starts.AddOrUpdate(c.DeliveryId, 1, fun _ k -> k + 1) |> ignore
                                        Thread.Sleep 2 // the request
                                        let ok: Dispatch.Answer =
                                            { Status = Some 201; Error = null; RetryAfter = null; Response = "{}"; Refused = false; TimedOut = false; DurationMs = 2L }
                                        Dispatch.complete db opts (fun () -> 0.5) c.DeliveryId c.Version conn.Connection.Id n (now ()) ok
                                    | Dispatch.Stopped -> ()
                        with ex -> errors.Add $"{name}: {ex.GetType().Name}: {ex.Message}")
            let watch = Diagnostics.Stopwatch.StartNew()
            Task.WaitAll([| for w in 1..writers -> run (Db.Database(t.Db.Path)) $"process-{w}" |], TimeSpan.FromSeconds 120.0) |> ignore
            printfn $"{writers} writers, {leads} leads: {watch.ElapsedMilliseconds} ms, {errors.Count} errors"
            let statuses = Db.read t.Db (fun s -> s.Query "SELECT status FROM deliveries" [] (fun r -> Db.Read.str r 0))
            Expect.isEmpty (List.ofSeq errors) "no write failed on the other writer's lock"
            Expect.equal statuses.Length leads $"{leads} deliveries"
            Expect.isTrue (statuses |> Array.forall ((=) "delivered")) "every one delivered"
            Expect.isTrue (starts.Values |> Seq.forall ((=) 1)) "each started exactly once"
            Expect.equal starts.Count leads "and none missed"
        }
    ]
