/// The whole path in one process: the API under TestServer, imported
/// connections pointing at its own fake receivers, the dispatcher sending
/// through the TestServer's HttpClient.
module LeadIntegrations.Tests.ApiTests

open System
open System.Net.Http
open System.Text
open System.Text.Json
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open LeadIntegrations
open LeadIntegrations.Tests.Support

let private cfg (db: Db.Database) : Api.Config.Config =
    { Port = 0
      DbPath = db.Path
      Origin = origin
      TimeScale = 1_000_000.0 // retries are immediate
      DeliveryTimeoutMs = 5000
      AllowLocalTargets = true
      WorkerEnabled = false
      FrontendDist = "does-not-exist"
      TestControl = false }

let private serverWith (config: Api.Config.Config) (db: Db.Database) =
    let builder = WebApplication.CreateBuilder()
    builder.WebHost.UseTestServer() |> ignore
    let app = Api.App.build builder config db
    app.StartAsync().Wait()
    app, app.GetTestClient()

let private server (db: Db.Database) = serverWith (cfg db) db

let private json (res: HttpResponseMessage) =
    let text = res.Content.ReadAsStringAsync().Result
    JsonDocument.Parse(text).RootElement.Clone()

let private post (client: HttpClient) (path: string) (body: string) =
    client.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json")).Result

let private drain (db: Db.Database) (client: HttpClient) =
    let opts: Dispatch.Options =
        { TimeScale = 1_000_000.0
          TimeoutMs = 5000
          Address = { AllowLocal = true }
          UserAgent = "test"
          Origin = "http://localhost" }
    let mutable sent = 1
    while sent > 0 do
        sent <- (Dispatch.runDue db client opts (fun () -> 0.5) 50).Result

[<Tests>]
let tests =
    testList
        "api"
        [ test "a synthetic lead reaches every imported CRM's fake receiver; routing skips what the firm's rule excludes" {
              use t = new TempDb()
              let imported =
                  { firm "1" "Rose & Associates" "k" [ conn "10" "clio-grow" [ ("api_key", "x") ]; conn "11" "lead-docket" [ ("api_key", "https://x") ] ] with
                      ContactTypes = [| { Name = "Personal Injury"; Active = true } |] }
              Import.apply t.Db origin (batch Domain.Source.legacy [ imported ]) |> ignore
              let app, client = server t.Db
              try
                  let items = (json (client.GetAsync("/api/firms").Result)).GetProperty("items")
                  let firmId = items[0].GetProperty("id").GetString()
                  // Routed: "personal-injury" matches "Personal Injury".
                  let res = post client $"/api/firms/{firmId}/leads" """{"callType":"personal-injury"}"""
                  Expect.equal (int res.StatusCode) 201 "created"
                  drain t.Db client
                  let ds = json (client.GetAsync($"/api/deliveries?firmId={firmId}").Result)
                  Expect.equal (ds.GetArrayLength()) 2 "one per connection"
                  for d in ds.EnumerateArray() do
                      Expect.equal (d.GetProperty("status").GetString()) "delivered" (d.GetProperty("kind").GetString())
                      Expect.equal (d.GetProperty("stage").GetString()) "accepted" "accepted, never 'in CRM'"
                  let rec' = json (client.GetAsync("/api/received").Result)
                  Expect.equal (rec'.GetArrayLength()) 2 "the receivers saw both"
                  // Not routed: the rule seeded from the firm's call types takes new matters, not court calls.
                  let res = json (post client $"/api/firms/{firmId}/leads" """{"callType":"*Court Calls"}""")
                  let skipped = res.GetProperty("deliveries").EnumerateArray() |> Seq.map (fun d -> d.GetProperty("status").GetString()) |> Seq.toList
                  Expect.equal skipped [ "skipped"; "skipped" ] "skipped, with a reason"
                  let decision = res.GetProperty("deliveries").Item(0).GetProperty("decision")
                  Expect.equal (decision.GetProperty("outcome").GetString()) "not-sent-routing" "the decision is recorded"
                  Expect.equal (decision.GetProperty("rule").GetString()) "new-matter" "with the rule it used"
              finally
                  app.StopAsync().Wait()
          }

          test "a firm's time zone: the company's by default, set by an IANA name, anything else refused" {
              use t = new TempDb()
              let app, client = server t.Db
              try
                  let created = json (post client "/api/firms" """{"name":"Coast Law"}""")
                  Expect.equal (created.GetProperty("timeZone").GetString()) "America/New_York" "the company's by default"
                  let id = created.GetProperty("id").GetString()
                  let patch (body: string) =
                      let req = new HttpRequestMessage(HttpMethod.Patch, $"/api/firms/{id}")
                      req.Content <- new StringContent(body, Encoding.UTF8, "application/json")
                      client.SendAsync(req).Result
                  let ok = patch """{"timeZone":"America/Los_Angeles"}"""
                  Expect.equal (int ok.StatusCode) 200 "set"
                  Expect.equal ((json ok).GetProperty("timeZone").GetString()) "America/Los_Angeles" "and kept"
                  Expect.equal (int (patch """{"timeZone":"PST"}""").StatusCode) 422 "an abbreviation is refused"
                  Expect.equal (int (post client "/api/firms" """{"name":"X","timeZone":"-05:00"}""").StatusCode) 422 "so is a fixed offset"
              finally
                  app.StopAsync().Wait()
          }

          test "test control does not exist unless the API starts with it" {
              use t = new TempDb()
              let app, client = server t.Db
              try
                  let res = post client "/api/test/clock" """{"at":1}"""
                  Expect.equal (int res.StatusCode) 404 "no test control in a normal run"
              finally
                  app.StopAsync().Wait()
          }

          test "test control sets how one destination's receiver answers, and only that one" {
              use t = new TempDb()
              let app, client = serverWith { cfg t.Db with TestControl = true } t.Db
              let key = "dest-" + Guid.NewGuid().ToString("N")
              try
                  let put = client.PutAsync($"/api/test/receivers/{key}", new StringContent("""{"status":503,"body":"down for the test","retryAfter":"30"}""", Encoding.UTF8, "application/json")).Result
                  Expect.equal (int put.StatusCode) 200 "set"
                  let hit = post client $"/receiver/crm/email?dest={key}" "{}"
                  Expect.equal (int hit.StatusCode) 503 "answers exactly as set, without checking the body"
                  Expect.equal (hit.Headers.RetryAfter.ToString()) "30" "with its Retry-After"
                  let other = post client "/receiver/crm/email?dest=someone-else" """{"subject":"s","to":"a@example.com","body":"b"}"""
                  Expect.notEqual (int other.StatusCode) 503 "another destination is untouched"
                  let bad = client.PutAsync($"/api/test/receivers/{key}", new StringContent("""{"mode":"silent-drop"}""", Encoding.UTF8, "application/json")).Result
                  Expect.equal (int bad.StatusCode) 422 "a mode the lab does not implement is refused, not ignored"
              finally
                  Api.Receivers.overrides.TryRemove key |> ignore
                  app.StopAsync().Wait()
          }

          test "a gone receiver disables the connection and its replay is refused" {
              use t = new TempDb()
              Import.apply t.Db origin (batch Domain.Source.legacy [ firm "1" "A" "k" [ conn "10" "clio-grow" [ ("api_key", "x") ] ] ]) |> ignore
              let app, client = server t.Db
              try
                  let c = (Db.read t.Db Store.allConnections).[0].Connection
                  let patched = post client $"/api/connections/{c.Id}" "{}" // POST is not a route here
                  Expect.equal (int patched.StatusCode) 405 "method matters"
                  let req = new HttpRequestMessage(HttpMethod.Patch, $"/api/connections/{c.Id}")
                  req.Content <- new StringContent($"""{{"deliveryUrl":"{origin}/receiver/crm/clio-grow?mode=gone"}}""", Encoding.UTF8, "application/json")
                  Expect.equal (int (client.SendAsync(req).Result.StatusCode)) 200 "patched"
                  let d = json (post client $"/api/connections/{c.Id}/test" "")
                  drain t.Db client
                  let after = (Db.read t.Db (fun s -> Store.connectionOrFail s c.Id)).Connection
                  Expect.equal after.Status "needs-reconnection" "410: only the firm can reconnect"
                  let replay = post client $"""/api/deliveries/{d.GetProperty("id").GetString()}/replay""" ""
                  Expect.equal (int replay.StatusCode) 409 "replay refused while disabled"
                  Expect.stringContains ((json replay).GetProperty("error").GetString()) "reactivate" "says what to do"
              finally
                  app.StopAsync().Wait()
          }

          test "a delivery waiting when its destination is paused is held, not sent, and goes on resume" {
              use t = new TempDb()
              Import.apply t.Db origin (batch Domain.Source.legacy [ firm "1" "A" "k" [ conn "10" "clio-grow" [ ("api_key", "x") ] ] ]) |> ignore
              let app, client = server t.Db
              let patch (id: string) (body: string) =
                  let req = new HttpRequestMessage(HttpMethod.Patch, $"/api/connections/{id}")
                  req.Content <- new StringContent(body, Encoding.UTF8, "application/json")
                  Expect.equal (int (client.SendAsync(req).Result.StatusCode)) 200 "patched"
              let received () = (json (client.GetAsync("/api/received").Result)).GetArrayLength()
              try
                  let c = (Db.read t.Db Store.allConnections).[0].Connection
                  let firmId = (json (client.GetAsync("/api/firms").Result)).GetProperty("items").[0].GetProperty("id").GetString()
                  // Recorded while live: planned, due, not yet sent. Then the operator pauses.
                  let lead = json (post client $"/api/firms/{firmId}/leads" """{"callType":"personal-injury"}""")
                  let deliveryId = lead.GetProperty("deliveries").[0].GetProperty("id").GetString()
                  patch c.Id """{"enabled":false}"""
                  drain t.Db client
                  Expect.equal (received ()) 0 "nothing reaches a paused destination"
                  let held = (Db.read t.Db (fun s -> Store.deliveryOrFail s deliveryId)).Delivery
                  Expect.equal held.Status "pending" "still waiting"
                  Expect.equal held.Reason Store.heldReason "held, and says why"
                  Expect.isFalse held.NextAttemptAt.HasValue "with no next attempt"
                  // An operator's test lead still goes: it is a deliberate check, not production traffic.
                  post client $"/api/connections/{c.Id}/test" "" |> ignore
                  drain t.Db client
                  Expect.equal (received ()) 1 "the test lead was sent"
                  Expect.equal (Db.read t.Db (fun s -> Store.deliveryOrFail s deliveryId)).Delivery.Status "pending" "the production lead is still held"
                  // Resume: the held delivery goes at once.
                  patch c.Id """{"enabled":true}"""
                  drain t.Db client
                  Expect.equal (received ()) 2 "sent on resume"
                  Expect.equal (Db.read t.Db (fun s -> Store.deliveryOrFail s deliveryId)).Delivery.Status "delivered" "and accepted"
              finally
                  app.StopAsync().Wait()
          }

          test "a flaky receiver is retried until it accepts" {
              use t = new TempDb()
              Import.apply t.Db origin (batch Domain.Source.legacy [ firm "1" "A" "k" [ conn "10" "mycase" [ "token", "t"; "refresh_token", "r" ] ] ]) |> ignore
              let app, client = server t.Db
              try
                  let c = (Db.read t.Db Store.allConnections).[0].Connection
                  Db.write t.Db (fun s -> Store.updateConnection s { c with DeliveryUrl = $"{origin}/receiver/crm/mycase?mode=flaky" })
                  let d = json (post client $"/api/connections/{c.Id}/test" "")
                  drain t.Db client
                  let after = json (client.GetAsync($"""/api/deliveries/{d.GetProperty("id").GetString()}""").Result)
                  Expect.equal (after.GetProperty("status").GetString()) "delivered" "recovered"
                  Expect.equal (after.GetProperty("attempts").GetArrayLength()) 3 "two 503s, then 201"
                  Expect.equal (after.GetProperty("event").GetProperty("lead").GetProperty("firstName").GetString()) "Jane" "event included"
              finally
                  app.StopAsync().Wait()
          }

          test "unknown ids are 404 with an error message" {
              use t = new TempDb()
              let app, client = server t.Db
              try
                  let res = client.GetAsync("/api/firms/nope").Result
                  Expect.equal (int res.StatusCode) 404 "404"
                  Expect.equal ((json res).GetProperty("error").GetString()) "no such firm" "message"
              finally
                  app.StopAsync().Wait()
          }

          test "an imported CRM routes by category: the firm's own label for it does not matter" {
              use t = new TempDb()
              let imported =
                  { firm "1" "A" "k" [ conn "10" "clio-grow" [ ("api_key", "x") ] ] with
                      ContactTypes = [| { Name = "*New PI Calls"; Active = true }; { Name = "*Court Calls"; Active = false } |] }
              Import.apply t.Db origin (batch Domain.Source.legacy [ imported ]) |> ignore
              let app, client = server t.Db
              try
                  let firmId = (json (client.GetAsync("/api/firms").Result)).GetProperty("items").Item(0).GetProperty("id").GetString()
                  let detail = json (client.GetAsync($"/api/firms/{firmId}").Result)
                  let m = detail.GetProperty("contactTypes").Item(0).GetProperty("mapping")
                  Expect.equal (m.GetProperty("category").GetString()) "new-matter" "contact types carry their mapping"
                  Expect.equal (m.GetProperty("practiceArea").GetString()) "personal-injury" "practice area"
                  // The rule seeded from the firm's call types is "new matters": the reference lead's own label is one.
                  let asIs = json (post client $"/api/firms/{firmId}/leads" """{"referenceLead":"nm-personal-injury"}""")
                  Expect.equal (asIs.GetProperty("deliveries").Item(0).GetProperty("status").GetString()) "pending" "routed as written"
                  Expect.equal (asIs.GetProperty("mapping").GetProperty("category").GetString()) "new-matter" "mapping returned"
                  // Under the firm's own name for the same category: sent, with the reference lead's intake.
                  let res = json (post client $"/api/firms/{firmId}/leads" """{"referenceLead":"nm-personal-injury","callType":"*New PI Calls"}""")
                  Expect.equal (res.GetProperty("deliveries").Item(0).GetProperty("status").GetString()) "pending" "routed"
                  Expect.equal (res.GetProperty("event").GetProperty("lead").GetProperty("lastName").GetString()) "Whitfield" "the reference lead"
                  let unknown = post client $"/api/firms/{firmId}/leads" """{"referenceLead":"nope"}"""
                  Expect.equal (int unknown.StatusCode) 404 "unknown reference lead"
              finally
                  app.StopAsync().Wait()
          }

          test "firm search puts active firms with live destinations first" {
              use t = new TempDb()
              let quiet = { firm "1" "Law Group Quiet" "k1" [] with Active = false }
              let busy = firm "2" "Rose Law" "k2" [ conn "20" "mycase" [ ("api_key", "x") ] ]
              let off = firm "3" "Law Office Off" "k3" [ { conn "30" "mycase" [ ("api_key", "x") ] with Enabled = false } ]
              Import.apply t.Db origin (batch Domain.Source.legacy [ quiet; busy; off ]) |> ignore
              let _, found = Db.read t.Db (fun s -> Store.listFirms s { Q = "law"; Kind = null; Source = null; IssuesOnly = false; Limit = 10; Offset = 0 })
              Expect.equal (found |> Array.map _.Name) [| "Rose Law"; "Law Office Off"; "Law Group Quiet" |] "live destinations first, then active, then inactive"
              Expect.equal (found |> Array.map _.LiveDestinations) [| 1; 0; 0 |] "and how many are live"
          }

          test "the delivery summary route is not taken for a delivery id" {
              use t = new TempDb()
              let app, client = server t.Db
              try
                  let sum = json (client.GetAsync("/api/deliveries/summary?since=1").Result)
                  Expect.equal (sum.GetProperty("needsAttention").GetInt32()) 0 "a summary, not a 404"
                  Expect.equal (sum.GetProperty("since").GetInt64()) 1L "since as sent"
              finally
                  app.StopAsync().Wait()
          }

          test "the taxonomy, classify and reference-lead routes answer" {
              use t = new TempDb()
              let app, client = server t.Db
              try
                  let tax = json (client.GetAsync("/api/call-categories").Result)
                  Expect.isGreaterThan (tax.GetProperty("categories").GetArrayLength()) 5 "categories"
                  let m = json (client.GetAsync("/api/call-categories/classify?name=AH%20NO%20PATCH").Result)
                  Expect.equal (m.GetProperty("category").GetString()) "line-handling" "classify"
                  Expect.equal (m.GetProperty("crm").GetString()) "never" "crm default"
                  let refs = json (client.GetAsync("/api/reference-leads").Result)
                  for r in refs.EnumerateArray() do
                      Expect.isTrue (r.GetProperty("matches").GetBoolean()) (r.GetProperty("id").GetString())
              finally
                  app.StopAsync().Wait()
          }

          test "leads page, filter, and show the outcome of the latest delivery per connection" {
              use t = new TempDb()
              Import.apply t.Db origin (batch Domain.Source.legacy [ firm "1" "A" "k" [ conn "10" "clio-grow" [ ("api_key", "x") ] ] ]) |> ignore
              let app, client = server t.Db
              try
                  Seed.run t.Db { Seed.defaults with Count = 12; RandomSeed = 3 } |> ignore
                  drain t.Db client
                  let page (qs: string) = json (client.GetAsync($"/api/leads?{qs}").Result)
                  let all = page "limit=5"
                  Expect.equal (all.GetProperty("total").GetInt32()) 12 "total counts every lead"
                  Expect.equal (all.GetProperty("items").GetArrayLength()) 5 "a page"
                  let last = page "limit=5&offset=10"
                  Expect.equal (last.GetProperty("items").GetArrayLength()) 2 "the last page"
                  let first = all.GetProperty("items").Item(0)
                  Expect.equal (first.GetProperty("outcome").GetString()) "delivered" "the fake receiver accepted it"
                  // Category filter: every item has that category, and the totals add up.
                  let nm = page "category=new-matter&limit=500"
                  for i in nm.GetProperty("items").EnumerateArray() do
                      Expect.equal (i.GetProperty("mapping").GetProperty("category").GetString()) "new-matter" "filtered"
                  // A refused delivery, then a successful replay: the lead counts as delivered.
                  let c = (Db.read t.Db Store.allConnections).[0].Connection
                  Db.write t.Db (fun s -> Store.updateConnection s { c with DeliveryUrl = $"{origin}/receiver/crm/clio-grow?mode=reject" })
                  let sent = json (post client $"/api/firms/{c.FirmId}/leads" """{"referenceLead":"nm-unspecified"}""")
                  let eventId = sent.GetProperty("event").GetProperty("id").GetString()
                  drain t.Db client
                  Expect.equal ((page "outcome=refused").GetProperty("total").GetInt32()) 1 "refused"
                  Db.write t.Db (fun s -> Store.updateConnection s { c with DeliveryUrl = $"{origin}/receiver/crm/clio-grow" })
                  let failed = sent.GetProperty("deliveries").Item(0).GetProperty("id").GetString()
                  post client $"/api/deliveries/{failed}/replay" "" |> ignore
                  drain t.Db client
                  let detail = json (client.GetAsync($"/api/leads/{eventId}").Result)
                  Expect.equal (detail.GetProperty("outcome").GetString()) "delivered" "the replay replaces the refusal"
                  Expect.equal (detail.GetProperty("counts").GetProperty("total").GetInt32()) 1 "one connection"
                  Expect.equal (detail.GetProperty("deliveries").GetArrayLength()) 2 "both deliveries listed"
                  Expect.equal (detail.GetProperty("lead").GetProperty("lastName").GetString()) "Barrett" "the lead itself"
                  Expect.equal ((page "outcome=refused").GetProperty("total").GetInt32()) 0 "no longer refused"
                  Expect.equal (int (client.GetAsync("/api/leads/nope").Result.StatusCode)) 404 "unknown lead"
                  // Deliveries page too.
                  let d1 = json (client.GetAsync("/api/deliveries?limit=3").Result)
                  let d2 = json (client.GetAsync("/api/deliveries?limit=3&offset=3").Result)
                  Expect.notEqual (d1.Item(0).GetProperty("id").GetString()) (d2.Item(0).GetProperty("id").GetString()) "offset moves the window"
              finally
                  app.StopAsync().Wait()
          }

          test "a firm user signs in with a token and sees only its own firm's leads; every refusal is audited" {
              use t = new TempDb()
              Import.apply t.Db origin (batch Domain.Source.legacy [ firm "1" "Firm A" "ka" [ conn "10" "email" [] ]; firm "2" "Firm B" "kb" [] ]) |> ignore
              let firmA, firmB =
                  Db.read t.Db (fun s ->
                      let fs = Store.allFirms s
                      (fs |> Array.find (fun f -> f.Name = "Firm A")).Id, (fs |> Array.find (fun f -> f.Name = "Firm B")).Id)
              let app, client = server t.Db
              try
                  let issued = json (post client $"/api/firms/{firmB}/users" """{"name":"Blake","role":"member"}""")
                  let tokenB = issued.GetProperty("token").GetString()
                  let tokenA = (json (post client $"/api/firms/{firmA}/users" """{"name":"Avery"}""")).GetProperty("token").GetString()
                  Expect.isFalse (Db.read t.Db (fun (s: Db.Session) -> s.TryOne "SELECT 1 FROM firm_users WHERE token_hash=$1" [ tokenB ] (fun _ -> true)) |> Option.isSome) "the token itself is never stored"
                  let leadA = (json (post client $"/api/firms/{firmA}/leads" "{}")).GetProperty("event").GetProperty("id").GetString()
                  let as' (token: string) (method: HttpMethod) (path: string) =
                      use req = new HttpRequestMessage(method, path)
                      if not (isNull token) then req.Headers.Authorization <- Headers.AuthenticationHeaderValue("Bearer", token)
                      int (client.SendAsync(req).Result.StatusCode)
                  Expect.equal (as' null HttpMethod.Get $"/api/portal/leads/{leadA}") 401 "no token, no answer"
                  Expect.equal (as' "fut_wrong" HttpMethod.Get $"/api/portal/leads/{leadA}") 401 "an unknown token"
                  Expect.equal (as' tokenA HttpMethod.Get $"/api/portal/leads/{leadA}") 200 "its own firm's lead"
                  Expect.equal (as' tokenB HttpMethod.Get $"/api/portal/leads/{leadA}") 404 "another firm's lead does not exist for it"
                  Expect.equal (as' tokenB HttpMethod.Get "/api/portal/leads/evt_nonexistent") 404 "the same answer as a lead that does not exist"
                  Expect.equal (as' tokenB HttpMethod.Get $"/api/portal/leads?firmId={firmA}") 403 "nor another firm's list"
                  Expect.equal (as' tokenB HttpMethod.Get $"/api/portal/leads/{leadA}/export") 404 "nor its export"
                  Expect.equal (as' tokenB HttpMethod.Post $"/api/portal/leads/{leadA}/resend") 404 "nor a resend"
                  let refused = Access.refusals t.Db
                  Expect.equal (refused |> Array.map _.Boundary) [| "read"; "read"; "list"; "export"; "resend" |] "every refusal audited"
                  Expect.isTrue (refused |> Array.forall (fun r -> r.FirmId = firmB)) "as the asking firm"
                  Expect.equal (Db.read t.Db (fun s -> (Store.leadOrFail s leadA).Deliveries.Length)) 1 "the refused resend made nothing"
              finally
                  app.StopAsync().Wait()
          } ]
