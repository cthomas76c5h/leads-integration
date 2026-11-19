module LeadIntegrations.Tests.SimulateTests

open System
open Expecto
open LeadIntegrations

/// Thursday 19 Nov 2026, noon Eastern: a fixed clock so the week is the same every run.
let private thursdayNoon = DateTimeOffset(2026, 11, 19, 17, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()

let private setup () =
    let t = new Support.TempDb()
    let conns =
        [ Support.conn "10" "answering-legal" [ ("api_key", "x") ]
          Support.conn "11" "clio-grow" [ ("token", "x"); ("refresh_token", "y") ]
          Support.conn "12" "lead-docket" [ ("api_key", "https://x") ]
          Support.conn "13" "mycase" [ ("api_key", "x") ]
          Support.conn "14" "practicepanther" [ ("token", "x"); ("refresh_token", "y") ] ]
    let showcase =
        { Support.firm "1" "Showcase" "k" conns with
            ContactTypes = [| { Name = "*New Client Calls"; Active = true }; { Name = "*Court Calls"; Active = false } |] }
    Import.apply t.Db Support.origin (Support.batch Domain.Source.api2 [ showcase ]) |> ignore
    // Existing leads to spread over the week (published now; the simulation rewrites their history).
    Seed.run t.Db { Seed.defaults with Count = 120; RandomSeed = 3 } |> ignore
    t

let private opts = { Simulate.defaults with Origin = Support.origin; Now = thursdayNoon; CrmLeads = 0 }

let private statuses (t: Support.TempDb) =
    Db.read t.Db (fun s -> s.Query "SELECT status, COUNT(*) FROM deliveries GROUP BY status ORDER BY status" [] (fun r -> r.GetString 0, r.GetInt64 1))

[<Tests>]
let tests =
    testList
        "simulate"
        [ test "a simulated week reaches every delivery state through the real rules" {
              use t = setup ()
              let r = Simulate.run t.Db opts
              let by = statuses t |> dict
              for s in [ "delivered"; "failed"; "skipped"; "pending" ] do
                  Expect.isTrue (by.ContainsKey s) $"some delivery is {s}"
              let conns = Db.read t.Db (fun s -> Store.allConnections s) |> Array.map _.Connection
              let byKind k = conns |> Array.find (fun c -> c.Kind = k)
              Expect.equal (byKind "lead-docket").Status "needs-reconnection" "410 Gone: the firm must reconnect the hook"
              Expect.stringContains (byKind "lead-docket").DisabledReason "410" "and says why"
              Expect.equal (byKind "clio-grow").Status "needs-reconnection" "the first 401: the firm must sign in again"
              Expect.equal (byKind "clio-grow").ConsecutiveFailures 0 "not counted as the destination failing"
              Expect.equal (byKind "mycase").Status "active" "the redirect was fixed and the connection reactivated"
              Expect.stringEnds (byKind "practicepanther").DeliveryUrl "?mode=flaky" "the live incident recovers when the worker sends"
              Expect.isGreaterThan r.Replays 0 "an operator replayed"
              let replayedOk =
                  Db.read t.Db (fun s -> s.Query "SELECT COUNT(*) FROM deliveries WHERE replay_of IS NOT NULL AND status='delivered'" [] (fun r -> r.GetInt64 0))
              Expect.isGreaterThan replayedOk[0] 0L "a replay was accepted"
              let urls = Db.read t.Db (fun s -> s.Query "SELECT DISTINCT url FROM attempts" [] (fun r -> r.GetString 0))
              for u in urls do
                  Expect.stringStarts u (Support.origin + "/receiver/") "only the local fake receivers"
              // The webhook whose approved mapping lacks Phone: refused for its setup, so "Edit mapping" applies.
              let hook = conns |> Array.find (fun c -> c.Kind = "webhook")
              Expect.equal hook.DisabledReason "5 leads in a row failed" "refused until switched off"
              let setups = Db.read t.Db Store.listSetups
              Expect.equal (setups |> Array.filter (fun x -> x.ConnectionId = hook.Id) |> Array.length) 1 "one live setup behind it"
              let refused =
                  Db.read t.Db (fun s ->
                      Store.listDeliveries s { Status = "failed"; FirmId = null; ConnectionId = hook.Id; Test = Nullable false; Limit = 10; Offset = 0 })
              Expect.isNonEmpty refused "it was refused"
              for d in refused do
                  Expect.equal d.Delivery.Diagnosis.Class "configuration" "a setup problem, not the lead's"
                  Expect.stringContains d.Delivery.Diagnosis.Cause "missing Phone" "in the receiver's words"
              let latest = Db.read t.Db (fun s -> s.Query "SELECT MAX(at) FROM attempts" [] (fun r -> r.GetInt64 0))
              Expect.isLessThanOrEqual latest[0] thursdayNoon "nothing is attempted in the future"
          }

          test "running it again gives the same week" {
              use t = setup ()
              let first = Simulate.run t.Db opts
              let before = statuses t
              let second = Simulate.run t.Db opts
              Expect.equal second.Leads first.Leads "no leads added twice"
              Expect.equal (statuses t) before "same outcomes"
              Expect.equal (Db.read t.Db Store.listSetups).Length 1 "the webhook's setup is reset, not added again"
          } ]
