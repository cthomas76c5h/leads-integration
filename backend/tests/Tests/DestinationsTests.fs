module LeadIntegrations.Tests.DestinationsTests

open System
open Expecto
open LeadIntegrations

let private now = DateTimeOffset(2026, 11, 19, 17, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()

/// One showcase firm with a CRM for most scenarios, through a simulated week.
let private week () =
    let t = new Support.TempDb()
    let conns =
        [ Support.conn "10" "answering-legal" [ ("api_key", "x") ]
          Support.conn "11" "clio-grow" [ ("token", "x"); ("refresh_token", "y") ]
          Support.conn "12" "lead-docket" [ ("api_key", "https://x") ]
          Support.conn "13" "mycase" [ ("api_key", "x") ]
          Support.conn "14" "practicepanther" [ ("token", "x"); ("refresh_token", "y") ]
          { Support.conn "15" "smokeball" [ ("api_key", "x") ] with Enabled = false } ]
    let firm =
        { Support.firm "1" "Showcase" "k" conns with
            ContactTypes = [| { Name = "*New Client Calls"; Active = true }; { Name = "*Court Calls"; Active = false } |] }
    Import.apply t.Db Support.origin (Support.batch Domain.Source.legacy [ firm ]) |> ignore
    Seed.run t.Db { Seed.defaults with Count = 120; RandomSeed = 3 } |> ignore
    Simulate.run t.Db { Simulate.defaults with Origin = Support.origin; Now = now; CrmLeads = 0 } |> ignore
    t

let private query: Destinations.Query = { Q = null; Health = null; Kind = null; FirmActive = "true"; Limit = 50; Offset = 0 }
let private rows (t: Support.TempDb) = Db.read t.Db (fun s -> Destinations.all s now)
let private byKind (t: Support.TempDb) kind = rows t |> Array.find (fun r -> r.Kind = kind)

[<Tests>]
let tests =
    testList
        "destinations"
        [ test "state and health come from what happened to its deliveries" {
              use t = week ()
              let docket = byKind t "lead-docket"
              Expect.equal (docket.State, docket.Health) ("needs-reconnection", "stopped") "410: only the firm can reconnect"
              let clio = byKind t "clio-grow"
              Expect.equal clio.State "needs-reconnection" "a 401: a connection problem, not a setup one"
              Expect.equal clio.LastProblem.Diagnosis.Class "connection" "and says so"
              let pp = byKind t "practicepanther"
              Expect.equal pp.Health "late" "retrying for hours"
              Expect.isTrue pp.RetryingSince.HasValue "since when"
              let al = byKind t "answering-legal"
              Expect.isTrue (al.Health = "healthy" || al.Health = "late" || al.Health = "retrying") $"hang-ups refused are lead problems, not failing: {al.Health}"
              Expect.isGreaterThan al.Week.Accepted 0 "counts this week"
              let off = byKind t "smokeball"
              Expect.equal (off.State, off.Health) ("off", "off") "off in its source and never used here: not a pause"
          }

          test "the list puts what needs a person first, and counts cover the filter" {
              use t = week ()
              let r = Db.read t.Db (fun s -> Destinations.list s query now)
              Expect.equal r.items[0].Health "stopped" "stopped first"
              Expect.equal (r.counts.Values |> Seq.sum) r.total "every destination counted once"
              let only = Db.read t.Db (fun s -> Destinations.list s { query with Health = "stopped" } now)
              Expect.isTrue (only.items |> Array.forall (fun x -> x.Health = "stopped")) "health filters"
              Expect.equal only.counts["stopped"] r.counts["stopped"] "counts ignore the health filter"
          }

          test "missed leads are found, sent only when live, and then no longer missed" {
              use t = week ()
              Expect.equal (byKind t "lead-docket").MissedLeads 0 "needs reconnection holds its leads: none missed"
              // Switched off (a failure streak): leads recorded meanwhile get no delivery to it.
              let pp = byKind t "practicepanther"
              let switchOff (status: string) =
                  Db.write t.Db (fun s ->
                      let c = (Store.connectionOrFail s pp.ConnectionId).Connection
                      Store.updateConnection s { c with Status = status; DisabledReason = (if status = "active" then null else "5 leads in a row failed"); ConsecutiveFailures = 0 })
              switchOff "disabled"
              for _ in 1..2 do
                  Dispatch.publish t.Db pp.FirmId { Support.sampleLead () with CallType = "*New Client Calls" } false None None |> ignore
              let missed = (byKind t "practicepanther").MissedLeads
              Expect.isGreaterThanOrEqual missed 2 "leads since it was switched off have no delivery to it"
              Expect.throws (fun () -> Dispatch.sendMissed t.Db pp.ConnectionId |> ignore) "not live: refused"
              switchOff "active"
              let sent = Dispatch.sendMissed t.Db pp.ConnectionId
              Expect.equal (sent.queued + sent.notSent) missed "every missed lead got a decision"
              Expect.equal (byKind t "practicepanther").MissedLeads 0 "none missed now"
          }

          test "the Delivery Center totals cover every delivery, not a page" {
              use t = week ()
              let sum = Db.read t.Db (fun s -> Store.deliverySummary s false null (now - 7L * 86_400_000L) now)
              let attention =
                  Db.read t.Db (fun s ->
                      Store.listDeliveries s { Status = "attention"; FirmId = null; ConnectionId = null; Test = Nullable false; Limit = 500; Offset = 0 })
              Expect.equal sum.NeedsAttention attention.Length "the tile and the list agree"
              Expect.isTrue (attention |> Array.forall (fun d -> d.Delivery.Status = "failed" && d.Delivery.Diagnosis.Class <> "lead")) "no lead problems in it"
              // Hang-ups are decided at planning now: not sent, as lead problems, so no CRM refuses one.
              Expect.equal sum.LeadProblems 0 "no refusals for what the lead lacks"
              let notSent =
                  Db.read t.Db (fun s ->
                      Store.listDeliveries s { Status = "skipped"; FirmId = null; ConnectionId = null; Test = Nullable false; Limit = 500; Offset = 0 })
              Expect.isTrue (notSent |> Array.exists (fun d -> d.Delivery.Diagnosis.Class = "lead" && d.Delivery.Attempts.Length = 0)) "hang-ups not sent, as lead problems"
              Expect.isGreaterThan sum.Late 0 "the incident is late"
              Expect.isGreaterThan sum.Accepted 0 "accepted this week"
              Expect.isGreaterThan sum.StoppedDestinations 0 "stopped destinations"
              // A resend that is accepted takes its refusal off the list.
              let first = attention[0].Delivery
              Db.write t.Db (fun s ->
                  Store.insertDelivery s { first with Id = Secrets.newId "dlv"; Status = "delivered"; Reason = null; ReplayOf = first.Id; CreatedAt = now } None)
              let after = Db.read t.Db (fun s -> Store.deliverySummary s false null 0L now)
              Expect.equal after.NeedsAttention (sum.NeedsAttention - 1) "resolved by resend"
          } ]
