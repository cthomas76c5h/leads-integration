module LeadIntegrations.Tests.HandOffTests

open System
open System.Collections.Generic
open Expecto
open LeadIntegrations
open LeadIntegrations.Domain

let private minute = 60_000L
/// Half an hour ahead of the real clock: Dispatch.publish stamps leads with the real time,
/// so a lead it publishes is past a third of its hour by `now` on any day the tests run.
let private now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 30L * minute

/// A firm with an email destination and an agreed SMS fallback.
let private firmWithFallback () =
    let t = new Support.TempDb()
    let f = { Support.firm "1" "Fallback Firm" "k" [ Support.conn "10" "email" [] ] with ContactTypes = [| { Name = "*New Client Calls"; Active = true } |] }
    Import.apply t.Db Support.origin (Support.batch Domain.Source.api2 [ f ]) |> ignore
    let firmId = Db.read t.Db (fun s -> (Store.allFirms s |> Array.head).Id)
    let fallback: Connection =
        { Id = Secrets.newId "con"; FirmId = firmId; FirmName = "Fallback Firm"; Kind = "sms"; Name = "Intake SMS"
          Source = Source.manual; SourceRef = null; Credentials = [||]
          Settings = Dictionary(dict [ ("role", "fallback"); ("to", "+15555550142") ])
          Enabled = true; Status = "active"; DisabledReason = null; ConsecutiveFailures = 0
          DeliveryUrl = Support.origin + "/receiver/crm/sms"; Issues = [||]; CreatedAt = 0L }
    Db.write t.Db (fun s -> Store.insertConnection s fallback (Secrets.newSigningSecret ()) null)
    t, firmId, fallback.Id

let private lead callType = { Support.sampleLead () with CallType = callType }

let private record (t: Support.TempDb) firmId (createdAt: int64) (l: Lead) =
    let e: LeadEvent = { Id = Secrets.newId "evt"; FirmId = firmId; Test = false; CreatedAt = createdAt; Lead = l }
    Db.write t.Db (fun s -> Store.insertEvent s e)
    e

[<Tests>]
let tests =
    testList
        "hand-off"
        [ test "the window to act follows the call category" {
              Expect.equal (Urgency.windowMs "emergency") (15L * minute) "emergency: 15 minutes"
              Expect.equal (Urgency.windowMs "new-matter") (60L * minute) "new matter: an hour"
              Expect.isGreaterThan (Urgency.windowMs "billing") (Urgency.windowMs "existing-client") "billing can wait longest"
              Expect.equal (Urgency.handOffState 0L 100L (Some 90L) 1000L) "on-time" "in time"
              Expect.equal (Urgency.handOffState 0L 100L (Some 150L) 1000L) "expired" "reached the firm, but too late"
              Expect.equal (Urgency.handOffState 0L 100L None 50L) "waiting" "still inside the window"
              Expect.equal (Urgency.handOffState 0L 100L None 150L) "expired" "the window passed"
          }

          test "a fallback gets no leads directly, only hand-offs running late, once each" {
              let t, firmId, fallbackId = firmWithFallback ()
              use _ = t
              let _, ds = Dispatch.publish t.Db firmId (lead "*New Client Calls") false None None
              Expect.isFalse (ds |> Array.exists (fun d -> d.ConnectionId = fallbackId)) "not published to the fallback"
              let fresh = record t firmId (now - 5L * minute) (lead "*New Client Calls")
              let late = record t firmId (now - 25L * minute) (lead "*New Client Calls")
              Expect.equal (Dispatch.runFallbacks t.Db now) 2 "the late one, and the published lead that went nowhere"
              let toFallback (e: LeadEvent) =
                  Db.read t.Db (fun s -> s.Query "SELECT COUNT(*) FROM deliveries WHERE event_id=$1 AND connection_id=$2" [ e.Id; fallbackId ] (fun r -> r.GetInt32 0))
                  |> Array.head
              Expect.equal (toFallback late) 1 "past a third of its hour: to the fallback"
              Expect.equal (toFallback fresh) 0 "5 minutes into its hour: not yet"
              Expect.equal (Dispatch.runFallbacks t.Db now) 0 "never twice"
          }

          test "a lead that reached the firm after its window is expired, and says when and how" {
              let t, firmId, _ = firmWithFallback ()
              use _ = t
              let e = record t firmId (now - 3L * 60L * minute) (lead "*New Client Calls")
              let c: Connection = Db.read t.Db (fun s -> (Store.connectionsOf s firmId |> Array.find (fun r -> r.Connection.Kind = "email")).Connection)
              Db.write t.Db (fun s ->
                  let d: Delivery =
                      { Id = Secrets.newId "dlv"; EventId = e.Id; ConnectionId = c.Id; ConnectionName = c.Name; Kind = c.Kind; FirmId = firmId
                        FirmName = c.FirmName; Test = false; Status = "delivered"; Stage = ""; Reason = null; NextAttemptAt = Nullable()
                        ReplayOf = null; CreatedAt = e.CreatedAt; Attempts = [||]; Diagnosis = null }
                  Store.insertDelivery s d None
                  Store.insertAttempt s d.Id
                      { N = 1; At = e.CreatedAt + 2L * 60L * minute; DurationMs = 10L; Url = c.DeliveryUrl; Status = Nullable 201; Error = null
                        Outcome = "delivered"; RequestBody = "{}"; ResponseSnippet = ""; NextAttemptInMs = Nullable() })
              let l = Db.read t.Db (fun s -> Store.leadOrFail s e.Id)
              Expect.equal l.Outcome "expired" "expired comes first"
              Expect.equal l.HandOff.State "expired" "two hours into a one-hour window"
              Expect.equal l.HandOff.By c.Name "and by which destination"
              Expect.isFalse l.HandOff.ViaFallback "not the fallback"
          }

          test "the firm report tells the week in categories, with hand-off and delivery" {
              let t, firmId, _ = firmWithFallback ()
              use _ = t
              record t firmId (now - 60L * minute) (lead "*New Client Calls") |> ignore
              record t firmId (now - 30L * minute) (lead "*Billing/Payment Calls") |> ignore
              let r = Db.read t.Db (fun s -> Report.build s firmId now)
              Expect.equal r.Calls 2 "this week's calls"
              Expect.equal r.NewMatters 1 "one new matter"
              Expect.isNonEmpty r.ByCategory "by category"
              Expect.isTrue (r.Destinations |> Array.exists _.Fallback) "the fallback is listed as such"
              Expect.equal (r.Labels |> Array.sumBy _.Count) 2 "the firm's own words, counted"
          }

          test "a simulated outage: the firm with a fallback is reached in time, the one without expires" {
              use t = new Support.TempDb()
              let firm id name =
                  { Support.firm id name $"k{id}" [ Support.conn $"{id}0" "answering-legal" [ ("api_key", "x") ]; Support.conn $"{id}1" "email" [] ] with
                      ContactTypes = [| { Name = "*New Client Calls"; Active = true } |] }
              // Four firms: the simulator also takes the two busiest for its dead-endpoint and paused scenarios.
              Import.apply t.Db Support.origin (Support.batch Domain.Source.api2 [ firm "1" "Alpha Law"; firm "2" "Beta Law"; firm "3" "Gamma Law"; firm "4" "Delta Law" ]) |> ignore
              Seed.run t.Db { Seed.defaults with Count = 120; RandomSeed = 5 } |> ignore
              let sum = Simulate.run t.Db { Simulate.defaults with Origin = Support.origin; Now = now; CrmLeads = 0 }
              let names = sum.Scenarios |> List.map _.Name
              Expect.contains names "fallback" "one firm has a fallback"
              Expect.contains names "expired" "one does not"
              let firmOf name = sum.Scenarios |> List.find (fun x -> x.Name = name) |> _.Where
              let leadsOf firmName =
                  let id = Db.read t.Db (fun s -> (Store.allFirms s |> Array.find (fun f -> f.Name = firmName)).Id)
                  Db.read t.Db (fun s -> Store.listLeads s { Q = null; FirmId = id; Category = null; Outcome = null; Test = Nullable false; Limit = 500; Offset = 0 } |> snd)
              Expect.isTrue (leadsOf (firmOf "fallback") |> Array.exists (fun l -> l.HandOff.ViaFallback && l.HandOff.State = "on-time")) "reached in time by fallback"
              Expect.isTrue (leadsOf (firmOf "expired") |> Array.exists (fun l -> l.HandOff.State = "expired")) "expired without one"
          } ]
