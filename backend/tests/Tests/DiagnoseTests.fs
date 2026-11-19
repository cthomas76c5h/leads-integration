module LeadIntegrations.Tests.DiagnoseTests

open System
open Expecto
open LeadIntegrations
open LeadIntegrations.Domain

let private attempt (status: int option) (error: string) (snippet: string) : Attempt =
    { N = 1
      At = 0L
      DurationMs = 10L
      Url = "http://localhost/receiver/crm/x"
      Status = (match status with Some s -> Nullable s | None -> Nullable())
      Error = error
      Outcome = ""
      RequestBody = "{}"
      ResponseSnippet = snippet
      NextAttemptInMs = Nullable() }

let private said (words: string) = Json.serialize {| result = words |}
let private named = lazy (Some(Support.sampleLead ()))
let private hangUp = lazy (Some { Support.sampleLead () with FirstName = ""; LastName = "" })
let private noLead = lazy None

let private check kind status reason attempts lead (cls: string) (cause: string) =
    let d = Diagnose.diagnose kind status reason attempts lead
    Expect.isNotNull d $"{cls}: diagnosed"
    Expect.equal d.Class cls "class"
    Expect.stringContains d.Cause cause "cause"

[<Tests>]
let tests =
    testList
        "diagnose"
        [ test "accepted, or not sent yet, needs nothing" {
              Expect.isNull (Diagnose.diagnose "mycase" "delivered" null [| attempt (Some 201) null (said "accepted") |] noLead) "accepted"
              Expect.isNull (Diagnose.diagnose "mycase" "pending" null [||] noLead) "queued"
          }

          test "the receiver's own words head the problem" {
              check "lawmatics-api" "failed" "HTTP 422: the receiver refused the body" [| attempt (Some 422) null (said "Phone is required") |] named
                  "configuration" "Phone is required"
              Expect.equal (Diagnose.receiverWords """{"error":"bad","result":"x"}""") (Some "bad") "error before result"
              Expect.equal (Diagnose.receiverWords "<html>") None "not JSON"
          }

          test "a hang-up refused for its missing name is the lead's problem, not the setup's" {
              let refused = [| attempt (Some 422) null (said "missing first_name; missing last_name") |]
              check "answering-legal" "failed" "HTTP 422" refused hangUp "lead" "No caller name"
              // The same refusal for a lead that has a name means the mapping drops it.
              check "answering-legal" "failed" "HTTP 422" refused named "configuration" "missing first_name"
          }

          test "sign-in and removed hooks are connection problems" {
              check "clio-grow" "failed" "HTTP 401" [| attempt (Some 401) null (said "unauthorized: access token expired") |] named
                  "connection" "access token expired"
              check "lead-docket" "failed" "gone" [| attempt (Some 410) null (said "gone: hook removed") |] named "connection" "hook removed"
          }

          test "retrying and gave up are transient; redirects and bodies we cannot build are not" {
              let d = Diagnose.diagnose "email" "pending" "no answer" [| attempt None "no answer in 10000 ms" "" |] noLead
              Expect.equal (d.Class, d.Retrying) ("transient", true) "still retrying"
              let g = Diagnose.diagnose "email" "failed" "gave up after 8 attempts: HTTP 503" [| attempt (Some 503) null (said "unavailable") |] noLead
              Expect.equal (g.Class, g.Retrying) ("transient", false) "gave up"
              check "mycase" "failed" "HTTP 301" [| attempt (Some 301) null "" |] named "configuration" "redirects are not followed"
              check "webhook" "failed" "body: unknown field {{nope}}" [||] named "ours" "unknown field"
          }

          test "routing and stopped destinations explain themselves" {
              check "mycase" "skipped" "chat leads are switched off for this destination" [||] noLead "routing" "chat leads"
              check "email" "cancelled" "connection disabled: 5 leads in a row failed" [| attempt (Some 503) null "" |] noLead "stopped" "5 leads in a row"
          }

          test "a simulated week: every delivery that is not accepted has a class and a cause" {
              use t = new Support.TempDb()
              let conns =
                  [ Support.conn "10" "answering-legal" [ ("api_key", "x") ]
                    Support.conn "11" "clio-grow" [ ("token", "x"); ("refresh_token", "y") ]
                    Support.conn "12" "lead-docket" [ ("api_key", "https://x") ]
                    Support.conn "14" "practicepanther" [ ("token", "x"); ("refresh_token", "y") ] ]
              let firm =
                  { Support.firm "1" "Showcase" "k" conns with
                      ContactTypes = [| { Name = "*New Client Calls"; Active = true }; { Name = "*Court Calls"; Active = false } |] }
              Import.apply t.Db Support.origin (Support.batch Domain.Source.api2 [ firm ]) |> ignore
              Seed.run t.Db { Seed.defaults with Count = 120; RandomSeed = 3 } |> ignore
              let now = DateTimeOffset(2026, 11, 19, 17, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()
              Simulate.run t.Db { Simulate.defaults with Origin = Support.origin; Now = now; CrmLeads = 0 } |> ignore
              let all = Db.read t.Db (fun s -> Store.listDeliveries s { Status = null; FirmId = null; ConnectionId = null; Test = Nullable(); Limit = 1000; Offset = 0 }) |> Array.map _.Delivery
              for d in all do
                  if d.Status <> "delivered" then
                      Expect.isNotNull d.Diagnosis $"{d.Status} {d.Reason} has a diagnosis"
                      Expect.isFalse (String.IsNullOrWhiteSpace d.Diagnosis.Cause) "and a cause"
              let classes = all |> Array.choose (fun (d: Delivery) -> Option.ofObj d.Diagnosis) |> Array.map _.Class |> Set.ofArray
              for c in [ "lead"; "connection"; "transient"; "routing" ] do
                  Expect.contains classes c $"the week has a {c} problem"
          } ]
