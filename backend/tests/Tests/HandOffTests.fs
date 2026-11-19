module LeadIntegrations.Tests.HandOffTests

open System
open System.Collections.Generic
open Expecto
open LeadIntegrations
open LeadIntegrations.Domain

let private minute = 60_000L
let private hour = 60L * minute
/// 90 minutes ahead of the real clock: Dispatch.publish stamps leads with the real time,
/// so a lead it publishes is past its 1-hour fallback delay by `now` on any day the tests run.
let private now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 90L * minute

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
        [ test "the window to act and the fallback delay follow the call category, on separate clocks" {
              Expect.equal (Urgency.windowMs "new-matter") (72L * hour) "new matter: 72 hours"
              Expect.equal (Urgency.windowMs "billing") (72L * hour) "everything else: the 72-hour default"
              Expect.equal (Urgency.windowMs "jail-custody") (24L * hour) "custody: 24 hours"
              Expect.equal (Urgency.windowMs "emergency") (24L * hour) "emergency: 24 hours"
              Expect.equal (Urgency.fallbackAfterMs "new-matter") hour "the fallback takes over after an hour"
              Expect.equal (Urgency.fallbackAfterMs "jail-custody") (15L * minute) "and after 15 minutes for custody"
              Expect.isLessThan (Urgency.fallbackAfterMs "new-matter") (Urgency.windowMs "new-matter") "long before the window ends"
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
              let late = record t firmId (now - 65L * minute) (lead "*New Client Calls")
              Expect.equal (Dispatch.runFallbacks t.Db now) 2 "the late one, and the published lead that went nowhere"
              let toFallback (e: LeadEvent) =
                  Db.read t.Db (fun s -> s.Query "SELECT COUNT(*) FROM deliveries WHERE event_id=$1 AND connection_id=$2" [ e.Id; fallbackId ] (fun r -> r.GetInt32 0))
                  |> Array.head
              Expect.equal (toFallback late) 1 "past its 1-hour fallback delay: to the fallback"
              Expect.equal (toFallback fresh) 0 "5 minutes in: not yet"
              Expect.equal (Dispatch.runFallbacks t.Db now) 0 "never twice"
          }

          test "a lead that reached the firm after its window is expired, and says when and how" {
              let t, firmId, _ = firmWithFallback ()
              use _ = t
              let e = record t firmId (now - 100L * hour) (lead "*New Client Calls")
              let c: Connection = Db.read t.Db (fun s -> (Store.connectionsOf s firmId |> Array.find (fun r -> r.Connection.Kind = "email")).Connection)
              Db.write t.Db (fun s ->
                  let d: Delivery =
                      { Id = Secrets.newId "dlv"; EventId = e.Id; ConnectionId = c.Id; ConnectionName = c.Name; Kind = c.Kind; FirmId = firmId
                        FirmName = c.FirmName; Test = false; Status = "delivered"; Stage = ""; Reason = null; NextAttemptAt = Nullable()
                        ReplayOf = null; CreatedAt = e.CreatedAt; Attempts = [||]; Diagnosis = null }
                  Store.insertDelivery s d None
                  Store.insertAttempt s d.Id
                      { N = 1; At = e.CreatedAt + 80L * hour; DurationMs = 10L; Url = c.DeliveryUrl; Status = Nullable 201; Error = null
                        Outcome = "delivered"; RequestBody = "{}"; ResponseSnippet = ""; NextAttemptInMs = Nullable() })
              let l = Db.read t.Db (fun s -> Store.leadOrFail s e.Id)
              Expect.equal l.Outcome "expired" "expired comes first"
              Expect.equal l.HandOff.State "expired" "80 hours into a 72-hour window"
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

          test "simulated outages: the firm with a fallback is reached within the hour; the one without, days late" {
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
              Expect.contains names "no-fallback" "one does not"
              let firmOf name = sum.Scenarios |> List.find (fun x -> x.Name = name) |> _.Where
              let leadsOf firmName =
                  let id = Db.read t.Db (fun s -> (Store.allFirms s |> Array.find (fun f -> f.Name = firmName)).Id)
                  Db.read t.Db (fun s -> Store.listLeads s { Q = null; FirmId = id; Category = null; Outcome = null; Test = Nullable false; Limit = 500; Offset = 0 } |> snd)
              let byFallback = leadsOf (firmOf "fallback") |> Array.filter (fun l -> l.HandOff.ViaFallback)
              Expect.isNonEmpty byFallback "reached by its fallback"
              for l in byFallback do
                  Expect.isLessThanOrEqual (l.HandOff.At.Value - l.CreatedAt) (hour + 5L * minute) "within about an hour of the call"
              let late = leadsOf (firmOf "no-fallback") |> Array.filter (fun l -> l.HandOff.State = "expired")
              Expect.isNonEmpty late "without one, leads miss their 72-hour window"
              Expect.isTrue (late |> Array.exists (fun l -> l.HandOff.At.HasValue)) "and some reach the firm only when resent, days late"
          } ]
