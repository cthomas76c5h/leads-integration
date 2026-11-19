module LeadIntegrations.Tests.CategoryTests

open System.Collections.Generic
open Expecto
open LeadIntegrations

[<Tests>]
let tests =
    testList
        "call categories"
        [ // The same cases tools/call_categories.py checks: the F# rules and the Python rules agree.
          testList
              "every reference lead maps as it says"
              [ for r in Categories.referenceLeads.Value ->
                    test r.Id {
                        Expect.equal r.Mapped.Category r.Expect.Category $"{r.Lead.CallType}: category"
                        Expect.equal r.Mapped.PracticeArea r.Expect.PracticeArea $"{r.Lead.CallType}: practice area"
                        Expect.equal (set r.Mapped.Modifiers) (set r.Expect.Modifiers) $"{r.Lead.CallType}: modifiers"
                        Expect.equal r.Mapped.Crm r.Expect.Crm $"{r.Lead.CallType}: crm default"
                    } ]

          test "a new-matter reference lead builds a body every CRM receiver accepts" {
              let lead = (Categories.referenceLead "nm-personal-injury").Lead
              for k in Catalog.kinds |> Array.filter (fun k -> k.Category = "crm") do
                  let settings = Dictionary<string, string>()
                  match Payloads.build k.Kind settings (fun _ -> true) null "evt_1" "Test Firm" lead with
                  | Ok b -> Expect.equal (FakeCrm.check k.Kind b.ContentType b.Body) [] k.Kind
                  | Error e -> failtestf "%s: %A" k.Kind e
          }

          test "names api-2 strips the same way map the same way" {
              Expect.equal (Categories.classify "*New Personal Injury Case") (Categories.classify "new personal-injury case") "punctuation and case"
          }

          test "a name starting with '-' is a system entry unless it names a category" {
              Expect.equal (Categories.classify "-Some Schedule").Category "system" "system"
              Expect.equal (Categories.classify "-Insurance BH").Category "insurance-medical" "still insurance"
          }

          test "an unknown reference lead is a 404" {
              Expect.throws (fun () -> Categories.referenceLead "nope" |> ignore) "fails"
          }

          test "every category in the seed's call mix has a reference lead to build from" {
              for category, _ in Seed.callMix do
                  Expect.isTrue
                      (Categories.referenceLeads.Value |> Array.exists (fun r -> r.Mapped.Category = category && r.Lead.FirstName <> ""))
                      category
          }

          test "seeded leads: invented people, the firm's own call types, routed like production" {
              use t = new Support.TempDb()
              let imported =
                  { Support.firm "1" "A" "k" [ Support.conn "10" "clio-grow" [ ("api_key", "x") ] ] with
                      ContactTypes =
                          [| { Name = "*New Client Calls"; Active = true }
                             { Name = "*Court Calls"; Active = false }
                             { Name = "Pat Example"; Active = false } |] }
              let off = { Support.firm "2" "No destinations" "k2" [ { Support.conn "20" "mycase" [ ("token", "t") ] with Enabled = false } ] with Active = true }
              Import.apply t.Db Support.origin (Support.batch Domain.Source.api2 [ imported; off ]) |> ignore
              let s = Seed.run t.Db { Seed.defaults with Count = 60; RandomSeed = 7 }
              Expect.equal s.Firms 1 "only the firm with an enabled connection"
              Expect.equal s.Leads 60 "all published"
              let events = Db.read t.Db (fun x -> x.Query "SELECT lead FROM events" [] (fun r -> Json.deserialize<Domain.Lead> (r.GetString 0)))
              Expect.equal events.Length 60 "events stored"
              for l in events do
                  Expect.stringStarts l.Phone "+1555555" "555 numbers only"
                  Expect.isTrue (l.Email = "" || l.Email.EndsWith "@example.com") "example.com only"
                  Expect.notEqual l.CallType "Pat Example" "never a person's name as call type"
              Expect.isTrue (events |> Array.exists (fun l -> l.CallType = "*New Client Calls")) "the firm's own name is used"
              // Only "*New Client Calls" is active, so other categories are skipped for the CRM, as in production.
              Expect.isGreaterThan s.Skipped 0 "routing applied"
          }

          test "the taxonomy carries shares and agreement from coverage.json" {
              let t = Categories.taxonomy.Value
              let nm = t.Categories |> Array.find (fun c -> c.Id = "new-matter")
              Expect.isTrue nm.Share.HasValue "share"
              Expect.isGreaterThan t.Agreement.ActiveTypes 0 "agreement counts"
          } ]
