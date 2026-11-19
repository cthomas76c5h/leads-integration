module LeadIntegrations.Tests.PayloadTests

open System.Collections.Generic
open Expecto
open LeadIntegrations
open LeadIntegrations.Tests.Support

let private build kind present =
    let settings = Dictionary<string, string>(dict [ "recipients", "ops@example.com"; "to", "+15555550100" ])
    Payloads.build kind settings present null "evt_1" "Test Firm" (sampleLead ())

[<Tests>]
let tests =
    testList
        "payloads"
        [ // The consensus check: every kind we list builds a body its own receiver accepts.
          testList
              "every catalog kind builds a body its receiver accepts"
              [ for k in Catalog.kinds ->
                    test k.Kind {
                        match build k.Kind (fun _ -> true) with
                        | Error e -> failtestf "no body: %A" e
                        | Ok b -> Expect.equal (FakeCrm.check k.Kind b.ContentType b.Body) [] "receiver accepts it"
                    } ]

          test "Clio Grow gets inbox_lead with N/A for empty fields (api-2 ClioSerializer)" {
              let lead = { sampleLead () with Email = "" }
              match Payloads.build "clio-grow" (Dictionary()) (fun _ -> true) null "evt_1" "F" lead with
              | Ok b ->
                  Expect.stringContains b.Body "\"inbox_lead\"" "wrapped"
                  Expect.stringContains b.Body "\"from_email\":\"N/A\"" "N/A default"
                  Expect.stringContains b.Body "Date of accident?: " "intake answers in the message"
              | Error e -> failtestf "%A" e
          }

          test "Lead Docket is form-encoded with capitalised fields" {
              match build "lead-docket" (fun _ -> true) with
              | Ok b ->
                  Expect.equal b.ContentType "application/x-www-form-urlencoded" "form"
                  Expect.stringStarts b.Body "First=" "First"
              | Error e -> failtestf "%A" e
          }

          test "Smokeball without a staff id on file is refused by the receiver" {
              match build "smokeball" (fun n -> n <> "api_key") with
              | Ok b -> Expect.contains (FakeCrm.check "smokeball" b.ContentType b.Body) "missing task.staffId" "refused"
              | Error e -> failtestf "%A" e
          }

          test "a sparse lead (hang-up) is refused by CRMs that require a name" {
              let lead = Synthetic.lead "F" "sparse" 0 null false [||]
              match Payloads.build "mycase" (Dictionary()) (fun _ -> true) null "evt_1" "F" lead with
              | Ok b -> Expect.contains (FakeCrm.check "mycase" b.ContentType b.Body) "missing first_name" "refused"
              | Error e -> failtestf "%A" e
          }

          test "webhook mapping renders custom answers by slug" {
              let rows: Domain.MapRow[] = [| { From = "full_name"; To = "Name" }; { From = "custom.date_of_accident"; To = "Accident" } |]
              let t = Payloads.rowsToTemplate rows
              match Payloads.build "webhook" (Dictionary()) (fun _ -> true) t "evt_1" "F" (sampleLead ()) with
              | Ok b ->
                  Expect.stringContains b.Body "\"Name\":\"Jane Doe\"" "name"
                  Expect.stringContains b.Body "\"Accident\":\"2026-11-12\"" "custom answer"
              | Error e -> failtestf "%A" e
          }

          test "slug" { Expect.equal (Payloads.slug "  Date of accident? ") "date_of_accident" "slug" } ]
