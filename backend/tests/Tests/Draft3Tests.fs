/// Milestone 3, delivery's side: the draft-3 lead the call center publishes (intake owns the
/// contract; examples from intake's conformance suite, under canonical firm ids). What must hold:
/// idempotency on the lead's content only, so a retry with new publication metadata is never a
/// conflict; the window to act from the interaction's completion (suite S-5); a late publication
/// is late; refusals change nothing; a receiver that is down is retried.
module LeadIntegrations.Tests.Draft3Tests

open System
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.TestHost
open LeadIntegrations
open LeadIntegrations.Domain
open LeadIntegrations.Tests.Support

let private example (name: string) =
    File.ReadAllText(Path.Combine(Db.repoRoot, "docs", "contracts", "intake-lead", "examples", name + ".json"))

/// The AI's call: a new criminal matter, published by the center for a provisioned firm.
let private aiCall () = example "draft-3-ai-new-criminal-matter"
let private canonicalId = "frm-508bd602aeff"
let private route = "/api/ingest/intake-lead/draft-3"

let private serverWith (db: Db.Database) =
    let builder = WebApplication.CreateBuilder()
    builder.WebHost.UseTestServer() |> ignore
    let config: Api.Config.Config =
        { Port = 0; DbPath = db.Path; Origin = origin; TimeScale = 1_000_000.0; DeliveryTimeoutMs = 5000
          AllowLocalTargets = true; WorkerEnabled = false; FrontendDist = "does-not-exist"; TestControl = false }
    let app = Api.App.build builder config db
    app.StartAsync().Wait()
    app, app.GetTestClient()

/// A provisioned firm with one webhook destination at the fake receiver (`mode` for its answers).
let private provisioned (db: Db.Database) (mode: string) =
    let firm, _ = Db.write db (fun s -> Provisioning.provision s canonicalId { Name = "Example Firm"; TimeZone = null; AccountId = null; Adopt = null } (now ()))
    Db.write db (fun s ->
        Store.insertConnection
            s
            { Id = Secrets.newId "con"; FirmId = firm.FirmId; FirmName = "Example Firm"; Kind = "webhook"; Name = "Webhook"
              Source = Source.manual; SourceRef = null; Credentials = [||]; Settings = Collections.Generic.Dictionary()
              Enabled = true; Status = "active"; DisabledReason = null; ConsecutiveFailures = 0
              DeliveryUrl = $"{origin}/receiver/crm/webhook?mode={mode}&dest=d3-{Guid.NewGuid():N}"; Issues = [||]; CreatedAt = now () }
            (Secrets.newSigningSecret ())
            null)
    firm.FirmId

let private publish (client: HttpClient) (body: string) (headers: (string * string) list) =
    use req = new HttpRequestMessage(HttpMethod.Post, route, Content = new StringContent(body, Encoding.UTF8, "application/json"))
    for name, value in headers do
        req.Headers.TryAddWithoutValidation(name, value) |> ignore
    client.SendAsync(req).Result

let private json (res: HttpResponseMessage) = JsonDocument.Parse(res.Content.ReadAsStringAsync().Result).RootElement.Clone()

let private drain (db: Db.Database) (client: HttpClient) =
    let opts: Dispatch.Options = { TimeScale = 1_000_000.0; TimeoutMs = 5000; Address = { AllowLocal = true }; UserAgent = "test"; Origin = origin }
    let mutable sent = 1
    while sent > 0 do
        sent <- (Dispatch.runDue db client opts (fun () -> 0.5) 50).Result

let private endedAt () =
    DateTimeOffset.Parse(JsonDocument.Parse(aiCall ()).RootElement.GetProperty("conversation").GetProperty("endedAt").GetString()).ToUnixTimeMilliseconds()

/// The payload with its interaction completed `ago` milliseconds before now.
let private completedAgo (ago: int64) =
    let root = Nodes.JsonNode.Parse(aiCall ()).AsObject()
    let iso (ms: int64) = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
    let completed = now () - ago
    let shift = completed - endedAt ()
    let conversation = root["conversation"].AsObject()
    let move (node: Nodes.JsonObject) (key: string) =
        node[key] <- Nodes.JsonValue.Create(iso (DateTimeOffset.Parse(node[key].GetValue<string>()).ToUnixTimeMilliseconds() + shift))
    move conversation "startedAt"
    move conversation "endedAt"
    move root "readyAt"
    move (root["provenance"].AsObject()) "evaluatedAt"
    root.ToJsonString()

let private handoffDue (db: Db.Database) (eventId: string) =
    Db.read db (fun s -> s.Query "SELECT handoff_due_at FROM events WHERE id=$1" [ eventId ] (fun r -> r.GetInt64 0) |> Array.head)

[<Tests>]
let tests =
    testList "draft-3" [
        test "a draft-3 lead for a provisioned firm is recorded with its publication, and delivered" {
            use t = new TempDb()
            let firmId = provisioned t.Db "ok"
            let app, client = serverWith t.Db
            try
                let res = publish client (aiCall ()) [ "Lead-Published-At", "2026-11-21T07:02:40Z"; "Lead-Publication-Attempt", "1" ]
                Expect.equal (int res.StatusCode) 201 "recorded"
                let body = json res
                let eventId = body.GetProperty("event").GetProperty("id").GetString()
                Expect.equal (body.GetProperty("event").GetProperty("firmId").GetString()) firmId "the firm under its canonical id"
                let kept = json (client.GetAsync($"/api/leads/{eventId}/contract").Result)
                Expect.equal (kept.GetProperty("contract").GetString()) "intake-lead/draft-3" "the contract"
                let p = kept.GetProperty("publication")
                Expect.equal (p.GetProperty("firstPublishedAt").GetInt64()) (DateTimeOffset.Parse("2026-11-21T07:02:40Z").ToUnixTimeMilliseconds()) "first publication"
                Expect.equal (p.GetProperty("attempts").GetInt32()) 1 "attempt 1"
                drain t.Db client
                Expect.equal (Db.read t.Db (fun s -> Store.leadOrFail s eventId)).Deliveries[0].Status "delivered" "delivered"
            finally
                app.StopAsync().Wait()
        }

        test "a retry with new publication metadata is the same lead, never a conflict" {
            use t = new TempDb()
            provisioned t.Db "ok" |> ignore
            let app, client = serverWith t.Db
            try
                let first = json (publish client (aiCall ()) [ "Lead-Published-At", "2026-11-21T07:02:40Z"; "Lead-Publication-Attempt", "1" ])
                // The answer was lost; the center's outbox sends the stored lead again, later.
                let retry = publish client (aiCall ()) [ "Lead-Published-At", "2026-11-21T07:07:40Z"; "Lead-Publication-Attempt", "2" ]
                Expect.equal (int retry.StatusCode) 200 "already recorded, not 409"
                let again = json retry
                Expect.isTrue (again.GetProperty("duplicate").GetBoolean()) "a duplicate"
                let eventId = first.GetProperty("event").GetProperty("id").GetString()
                Expect.equal (again.GetProperty("event").GetProperty("id").GetString()) eventId "the same lead"
                let p = (json (client.GetAsync($"/api/leads/{eventId}/contract").Result)).GetProperty("publication")
                Expect.equal (p.GetProperty("firstPublishedAt").GetInt64()) (DateTimeOffset.Parse("2026-11-21T07:02:40Z").ToUnixTimeMilliseconds()) "first kept"
                Expect.equal (p.GetProperty("lastPublishedAt").GetInt64()) (DateTimeOffset.Parse("2026-11-21T07:07:40Z").ToUnixTimeMilliseconds()) "latest kept"
                Expect.equal (p.GetProperty("attempts").GetInt32()) 2 "highest attempt"
                // An earlier attempt arriving last changes neither.
                publish client (aiCall ()) [ "Lead-Published-At", "2026-11-21T07:02:45Z"; "Lead-Publication-Attempt", "1" ] |> ignore
                let p2 = (json (client.GetAsync($"/api/leads/{eventId}/contract").Result)).GetProperty("publication")
                Expect.equal (p2.GetProperty("lastPublishedAt").GetInt64()) (p.GetProperty("lastPublishedAt").GetInt64()) "latest unchanged"
                Expect.equal (p2.GetProperty("attempts").GetInt32()) 2 "attempts unchanged"
                // A changed lead under the same interaction is still a conflict.
                let changed = aiCall().Replace("\"Rosa\"", "\"Rose\"")
                Expect.equal (int (publish client changed []).StatusCode) 409 "content differs: conflict"
                drain t.Db client
                Expect.equal ((json (client.GetAsync("/api/received").Result)).GetArrayLength()) 1 "sent once"
            finally
                app.StopAsync().Wait()
        }

        test "the window to act starts at the interaction's completion, not at receipt" {
            use t = new TempDb()
            provisioned t.Db "ok" |> ignore
            let ago = 10L * 60_000L
            let r = Dispatch.ingestAs t.Db LeadContract.draft3 (completedAgo ago) Dispatch.noPublication
            let category = (Categories.ofLead r.Intake.Event.Lead).Category
            let completed = now () - ago
            Expect.isLessThan (abs (handoffDue t.Db r.Intake.Event.Id - (completed + Urgency.windowMs category))) 2_000L "due a window after completion"
            Expect.isLessThan (handoffDue t.Db r.Intake.Event.Id) (r.Intake.Event.CreatedAt + Urgency.windowMs category) "earlier than from receipt"
            Expect.isLessThan (abs (Db.read t.Db (fun s -> Store.clockStart s r.Intake.Event.Id category)) - completed) 2_000L "the fallback clock starts there too"
        }

        test "a lead published after its window closed is late: its hand-off expires" {
            use t = new TempDb()
            provisioned t.Db "down" |> ignore
            let category = (Categories.ofLead (fst (LeadContract.parseAs LeadContract.draft3 (aiCall ()))).Lead).Category
            let r = Dispatch.ingestAs t.Db LeadContract.draft3 (completedAgo (Urgency.windowMs category + 60_000L)) Dispatch.noPublication
            Expect.isLessThan (handoffDue t.Db r.Intake.Event.Id) (now ()) "already past due when received"
            Dispatch.runHandOffs t.Db (now ()) |> ignore
            let expired = Db.read t.Db (fun s -> s.Query "SELECT expired_at FROM events WHERE id=$1" [ r.Intake.Event.Id ] (fun x -> not (x.IsDBNull 0)) |> Array.head)
            Expect.isTrue expired "expired, not counted as in time"
        }

        test "a draft-3 lead on the draft-2 route, a draft-2 lead here, or a malformed header is refused, and nothing is recorded" {
            use t = new TempDb()
            provisioned t.Db "ok" |> ignore
            let app, client = serverWith t.Db
            try
                let onDraft2 = client.PostAsync("/api/ingest/intake-lead/draft-2", new StringContent(aiCall (), Encoding.UTF8, "application/json")).Result
                Expect.equal (int onDraft2.StatusCode) 422 "draft 3 on the draft-2 route"
                Expect.equal (int (publish client (example "ai-new-criminal-matter") []).StatusCode) 422 "draft 2 on the draft-3 route"
                Expect.equal (int (publish client (aiCall ()) [ "Lead-Published-At", "yesterday" ]).StatusCode) 422 "a time that isn't one"
                Expect.equal (int (publish client (aiCall ()) [ "Lead-Publication-Attempt", "0" ]).StatusCode) 422 "attempts count from 1"
                Expect.equal (Db.read t.Db (fun s -> s.Query "SELECT COUNT(*) FROM events" [] (fun r -> r.GetInt64 0) |> Array.head)) 0L "nothing recorded"
            finally
                app.StopAsync().Wait()
        }

        test "a receiver that is down is retried until it accepts the lead" {
            use t = new TempDb()
            provisioned t.Db "flaky" |> ignore
            let app, client = serverWith t.Db
            try
                let eventId = (json (publish client (aiCall ()) [])).GetProperty("event").GetProperty("id").GetString()
                drain t.Db client
                let d = (Db.read t.Db (fun s -> Store.leadOrFail s eventId)).Deliveries[0]
                Expect.equal d.Status "delivered" "recovered"
                let full = json (client.GetAsync($"/api/deliveries/{d.Id}").Result)
                Expect.equal (full.GetProperty("attempts").GetArrayLength()) 3 "two 503s, then accepted"
            finally
                app.StopAsync().Wait()
        }
    ]
