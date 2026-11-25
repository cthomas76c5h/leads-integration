/// Milestone 1's seam: a lead in the intake-lead/draft-2 contract, as the call center publishes it,
/// taken by delivery and sent to a destination (docs/contracts/intake-lead/README.md). The payloads
/// are the contract's examples: leads the call center's and the voice agent's simulators produced.
module LeadIntegrations.Tests.IngestTests

open System
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Data.Sqlite
open LeadIntegrations
open LeadIntegrations.Domain
open LeadIntegrations.Tests.Support

let private example (name: string) =
    File.ReadAllText(Path.Combine(Db.repoRoot, "docs", "contracts", "intake-lead", "examples", name + ".json"))

/// A person's call: an existing client put through (no category: unmapped).
let private personCall () = example "person-existing-client"
/// The AI's call: a new criminal matter, caller in custody (category new-matter, criminal).
let private aiCall () = example "ai-new-criminal-matter"

/// The payload with one change, at a path of property names.
let private changed (raw: string) (path: string list) (value: JsonNode) =
    let root = JsonNode.Parse raw
    let rec go (node: JsonNode) path =
        match path with
        | [ last ] -> node.AsObject()[(last: string)] <- value
        | p :: rest -> go (node.AsObject()[(p: string)]) rest
        | [] -> ()
    go root path
    root.ToJsonString()

let private str (v: string) : JsonNode = JsonValue.Create v
let private num (v: int) : JsonNode = JsonValue.Create v

/// A firm with one email destination (takes every call), and the aliases the examples use.
let private firmWithEmail () =
    let t = new TempDb()
    let email = { conn "10" "email" [] with Settings = Collections.Generic.Dictionary(dict [ "recipients", "intake@example.com" ]) }
    Import.apply t.Db origin (batch Source.legacy [ firm "1" "Contract Firm" "k" [ email ] ]) |> ignore
    let firmId = Db.read t.Db (fun s -> (Store.allFirms s |> Array.head).Id)
    Db.write t.Db (fun s ->
        Store.setFirmAlias s "injury-firm" firmId (now ()) |> ignore
        Store.setFirmAlias s "defence-practice" firmId (now ()) |> ignore)
    t, firmId

let private failsWith (status: int) (f: unit -> 'a) =
    try
        f () |> ignore
        failtest $"expected a {status}"
    with Fail(s, message) ->
        Expect.equal s status message
        message

let private counts (db: Db.Database) =
    Db.read db (fun s ->
        let one sql = s.Query sql [] (fun r -> r.GetInt64 0) |> Array.head
        one "SELECT COUNT(*) FROM events", one "SELECT COUNT(*) FROM deliveries", one "SELECT COUNT(*) FROM lead_contracts")

// ----- The API, in one process: the dispatcher sends to its own fake receivers -----

let private cfg (db: Db.Database) : Api.Config.Config =
    { Port = 0
      DbPath = db.Path
      Origin = origin
      TimeScale = 1_000_000.0
      DeliveryTimeoutMs = 5000
      AllowLocalTargets = true
      WorkerEnabled = false
      FrontendDist = "does-not-exist"
      TestControl = false }

let private server (db: Db.Database) =
    let builder = WebApplication.CreateBuilder()
    builder.WebHost.UseTestServer() |> ignore
    let app = Api.App.build builder (cfg db) db
    app.StartAsync().Wait()
    app, app.GetTestClient()

let private post (client: HttpClient) (path: string) (body: string) =
    client.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json")).Result

let private put (client: HttpClient) (path: string) (body: string) =
    client.PutAsync(path, new StringContent(body, Encoding.UTF8, "application/json")).Result

let private json (res: HttpResponseMessage) = JsonDocument.Parse(res.Content.ReadAsStringAsync().Result).RootElement.Clone()

let private drain (db: Db.Database) (client: HttpClient) =
    let opts: Dispatch.Options =
        { TimeScale = 1_000_000.0; TimeoutMs = 5000; Address = { AllowLocal = true }; UserAgent = "test"; Origin = origin }
    let mutable sent = 1
    while sent > 0 do
        sent <- (Dispatch.runDue db client opts (fun () -> 0.5) 50).Result

let private ingestPath = "/api/ingest/intake-lead/draft-2"

[<Tests>]
let tests =
    testList "ingest" [
        test "a valid lead is recorded with its payload, policy revision and ids kept exactly as sent" {
            let t, firmId = firmWithEmail ()
            use _ = t
            let raw = personCall ()
            let r = Dispatch.ingest t.Db raw
            Expect.isFalse r.Intake.Duplicate "new"
            Expect.equal r.Intake.Event.FirmId firmId "the alias's firm"
            Expect.equal r.Intake.Deliveries.Length 1 "one delivery, planned with the lead"
            Expect.equal r.Intake.Deliveries[0].Status "pending" "the email destination takes every call"
            let kept = Db.read t.Db (fun s -> Store.tryLeadContract s r.Intake.Event.Id) |> Option.get
            Expect.equal kept.Contract "intake-lead/draft-2" "the contract"
            Expect.equal kept.LeadId "CC-1" "the sender's lead id"
            Expect.equal kept.InteractionId "CC-1" "the interaction"
            Expect.equal kept.SenderFirmId "injury-firm" "the sender's firm id, as written"
            Expect.equal kept.PolicyRevision 1 "the policy revision the call ran on"
            Expect.isTrue kept.PolicyApproved "approved"
            let stored = Db.read t.Db (fun s -> s.Query "SELECT payload FROM lead_contracts" [] (fun r -> r.GetString 0)) |> Array.head
            Expect.equal stored raw "the original payload, byte for byte"
            let lead = r.Intake.Event.Lead
            Expect.equal (lead.FirstName, lead.LastName, lead.Phone) ("Jordan", "Example", "555-0100") "contact, from fullName"
            Expect.equal lead.Category "unmapped" "no category from the sender: unmapped, never read from the name"
            Expect.equal lead.Message "Existing client. Put through to intake." "no message fact: what happened, in words"
        }

        test "the same interaction sent again is the lead already recorded: nothing is planned or sent again" {
            let t, _ = firmWithEmail ()
            use _ = t
            let first = Dispatch.ingest t.Db (personCall ())
            // The same meaning, written differently: other whitespace and key order.
            let reordered = JsonNode.Parse(personCall ()).ToJsonString(JsonSerializerOptions(WriteIndented = true))
            let again = Dispatch.ingest t.Db reordered
            Expect.isTrue again.Intake.Duplicate "a repeat"
            Expect.equal again.Intake.Event.Id first.Intake.Event.Id "the same lead"
            Expect.equal (counts t.Db) (1L, 1L, 1L) "one lead, one delivery, one payload"
            let recorded = Db.read t.Db Store.domainEvents |> Array.filter (fun e -> e.Name = "LeadRecorded")
            Expect.equal recorded.Length 1 "recorded once"
        }

        test "a different payload for the same interaction, or the same lead id for another, is a conflict and changes nothing" {
            let t, _ = firmWithEmail ()
            use _ = t
            Dispatch.ingest t.Db (personCall ()) |> ignore
            let other = changed (personCall ()) [ "contact"; "phone" ] (str "555-0199")
            let m = failsWith 409 (fun () -> Dispatch.ingest t.Db other)
            Expect.stringContains m "interaction CC-1" "names the interaction"
            let sameLeadId = changed (personCall ()) [ "conversation"; "id" ] (str "CC-1-other")
            failsWith 409 (fun () -> Dispatch.ingest t.Db sameLeadId) |> ignore
            Expect.equal (counts t.Db) (1L, 1L, 1L) "nothing changed"
        }

        test "an unknown firm, another contract, an invalid payload or an unknown category is refused, and nothing is recorded" {
            let t, _ = firmWithEmail ()
            use _ = t
            let unknownFirm = changed (personCall ()) [ "firm"; "id" ] (str "nobody")
            Expect.stringContains (failsWith 422 (fun () -> Dispatch.ingest t.Db unknownFirm)) "not a known firm" "unknown firm"
            let draft1 = changed (personCall ()) [ "contract" ] (str "intake-lead/draft-1")
            Expect.stringContains (failsWith 422 (fun () -> Dispatch.ingest t.Db draft1)) "intake-lead/draft-1" "another contract"
            let badEnum = changed (personCall ()) [ "conversation"; "channel" ] (str "fax")
            Expect.stringContains (failsWith 422 (fun () -> Dispatch.ingest t.Db badEnum)) "$.conversation.channel" "the schema's enum"
            let badType = changed (personCall ()) [ "policy"; "revision" ] (str "one")
            Expect.stringContains (failsWith 422 (fun () -> Dispatch.ingest t.Db badType)) "$.policy.revision: expected integer" "the schema's type"
            let extra = changed (personCall ()) [ "surprise" ] (num 1)
            Expect.stringContains (failsWith 422 (fun () -> Dispatch.ingest t.Db extra)) "unexpected property surprise" "no extra fields"
            let missing =
                let root = JsonNode.Parse(personCall ()).AsObject()
                root.Remove "followUp" |> ignore
                root.ToJsonString()
            Expect.stringContains (failsWith 422 (fun () -> Dispatch.ingest t.Db missing)) "missing followUp" "required"
            let category = changed (personCall ()) [ "classification"; "category" ] (str "pizza")
            Expect.stringContains (failsWith 422 (fun () -> Dispatch.ingest t.Db category)) "not in the call-category taxonomy" "taxonomy"
            failsWith 422 (fun () -> Dispatch.ingest t.Db "{not json") |> ignore
            Expect.equal (counts t.Db) (0L, 0L, 0L) "nothing recorded"
        }

        test "the lead, its deliveries and its payload are written together or not at all" {
            let t, _ = firmWithEmail ()
            use _ = t
            // The payload's insert fails after the lead and its delivery were written in the same transaction.
            let sql (text: string) =
                use c = new SqliteConnection($"Data Source={t.Db.Path}")
                c.Open()
                use cmd = c.CreateCommand()
                cmd.CommandText <- text
                cmd.ExecuteNonQuery() |> ignore
            sql "CREATE TRIGGER refuse_payload BEFORE INSERT ON lead_contracts BEGIN SELECT RAISE(ABORT, 'refused for the test'); END"
            Expect.throws (fun () -> Dispatch.ingest t.Db (personCall ()) |> ignore) "the transaction fails"
            Expect.equal (counts t.Db) (0L, 0L, 0L) "no lead, no delivery, no payload"
            Expect.isEmpty (Db.read t.Db Store.domainEvents) "nothing emitted"
            sql "DROP TRIGGER refuse_payload"
            Expect.isFalse (Dispatch.ingest t.Db (personCall ())).Intake.Duplicate "recorded when sent again, as new"
            Expect.equal (counts t.Db) (1L, 1L, 1L) "all three, once"
        }

        test "a lead the database could not take is not recorded, and is recorded once when sent again" {
            let t, _ = firmWithEmail ()
            use _ = t
            t.Db.WritesFail <- true
            Expect.throws (fun () -> Dispatch.ingest t.Db (personCall ()) |> ignore) "the write fails"
            t.Db.WritesFail <- false
            Expect.equal (counts t.Db) (0L, 0L, 0L) "nothing recorded"
            Expect.isFalse (Dispatch.ingest t.Db (personCall ())).Intake.Duplicate "recorded now"
        }

        test "the category the sender set routes the lead; the call type's name is not read" {
            let t, firmId = firmWithEmail ()
            use _ = t
            // The email destination takes new matters only.
            Db.write t.Db (fun s ->
                let c = (Store.connectionsOf s firmId |> Array.head).Connection
                let settings = Collections.Generic.Dictionary<string, string>(c.Settings)
                settings[Routing.categoriesKey] <- "new-matter"
                Store.updateConnection s { c with Settings = settings })
            // A new criminal matter, under a call-type name that reads as an existing client.
            let misleading = changed (aiCall ()) [ "classification"; "callType"; "name" ] (str "Existing client")
            let r = Dispatch.ingest t.Db misleading
            Expect.equal r.Intake.Deliveries[0].Status "pending" "routed as a new matter"
            let mapping = Categories.ofLead r.Intake.Event.Lead
            Expect.equal (mapping.Category, mapping.PracticeArea) ("new-matter", "criminal") "as the sender set it"
            // The person's call carries no category: unmapped, so a new-matters destination does not take it.
            let p = Dispatch.ingest t.Db (personCall ())
            Expect.equal p.Intake.Deliveries[0].Status "skipped" "not routed on its name"
        }

        test "end to end: a simulated call's lead reaches the fake receiver, once, through the API" {
            let t, _ = firmWithEmail ()
            use _ = t
            let app, client = server t.Db
            try
                let res = post client ingestPath (aiCall ())
                Expect.equal (int res.StatusCode) 201 "created"
                let body = json res
                Expect.equal (body.GetProperty("leadId").GetString()) "VA-1" "the sender's lead id"
                let eventId = body.GetProperty("event").GetProperty("id").GetString()
                drain t.Db client
                let d = (json (client.GetAsync($"/api/leads/{eventId}").Result)).GetProperty("deliveries").Item(0)
                Expect.equal (d.GetProperty("status").GetString()) "delivered" "delivered"
                Expect.equal (d.GetProperty("stage").GetString()) "accepted" "accepted by the receiver, never 'in the CRM'"
                let received = json (client.GetAsync("/api/received").Result)
                Expect.equal (received.GetArrayLength()) 1 "the receiver saw it once"
                Expect.stringContains (received.Item(0).GetProperty("body").GetString()) "Rosa" "the caller's lead"
                // Sent again: the lead already recorded, nothing new sent.
                let again = post client ingestPath (aiCall ())
                Expect.equal (int again.StatusCode) 200 "a repeat"
                Expect.isTrue ((json again).GetProperty("duplicate").GetBoolean()) "duplicate"
                drain t.Db client
                Expect.equal ((json (client.GetAsync("/api/received").Result)).GetArrayLength()) 1 "still once"
                // The payload as it came.
                let kept = json (client.GetAsync($"/api/leads/{eventId}/contract").Result)
                Expect.equal (kept.GetProperty("policyRevision").GetInt32()) 1 "policy revision"
                Expect.equal (kept.GetProperty("payload").GetProperty("leadId").GetString()) "VA-1" "payload"
                // Refusals, as statuses.
                Expect.equal (int (post client ingestPath (changed (aiCall ()) [ "contact"; "phone" ] (str "555-0102"))).StatusCode) 409 "conflict"
                Expect.equal (int (post client ingestPath (changed (aiCall ()) [ "firm"; "id" ] (str "nobody"))).StatusCode) 422 "unknown firm"
                Expect.equal (int (post client ingestPath "[]").StatusCode) 422 "not a lead"
                Expect.equal (int (client.GetAsync("/api/leads/evt_nope/contract").Result.StatusCode)) 404 "no contract"
            finally
                app.StopAsync().Wait()
        }

        test "aliases are set by hand, listed, and never moved to another firm" {
            let t, firmId = firmWithEmail ()
            use _ = t
            let app, client = server t.Db
            try
                Expect.equal (int (put client "/api/firm-aliases/general-firm" $$"""{"firmId":"{{firmId}}"}""").StatusCode) 200 "set"
                Expect.equal (int (put client "/api/firm-aliases/general-firm" $$"""{"firmId":"{{firmId}}"}""").StatusCode) 200 "again: unchanged"
                Expect.equal (int (put client "/api/firm-aliases/x" """{"firmId":"firm_nope"}""").StatusCode) 404 "unknown firm"
                let other = (json (post client "/api/firms" """{"name":"Other Firm"}""")).GetProperty("id").GetString()
                Expect.equal (int (put client "/api/firm-aliases/general-firm" $$"""{"firmId":"{{other}}"}""").StatusCode) 409 "not moved"
                let all = json (client.GetAsync("/api/firm-aliases").Result)
                Expect.equal (all.GetArrayLength()) 3 "the examples' two, and this one"
            finally
                app.StopAsync().Wait()
        }

        test "a receiver that is down is retried until it accepts" {
            let t, firmId = firmWithEmail ()
            use _ = t
            Db.write t.Db (fun s ->
                let c = (Store.connectionsOf s firmId |> Array.head).Connection
                Store.updateConnection s { c with DeliveryUrl = $"{origin}/receiver/crm/email?mode=flaky&dest=ingest-{Guid.NewGuid():N}" })
            let app, client = server t.Db
            try
                let eventId = (json (post client ingestPath (aiCall ()))).GetProperty("event").GetProperty("id").GetString()
                drain t.Db client
                let d = (Db.read t.Db (fun s -> Store.leadOrFail s eventId)).Deliveries[0]
                let full = json (client.GetAsync($"/api/deliveries/{d.Id}").Result)
                Expect.equal (full.GetProperty("status").GetString()) "delivered" "recovered"
                Expect.equal (full.GetProperty("attempts").GetArrayLength()) 3 "two 503s, then accepted"
            finally
                app.StopAsync().Wait()
        }

        test "a lead accepted before a restart is delivered after it, and is still recognised as sent" {
            let t, _ = firmWithEmail ()
            use _ = t
            let app, client = server t.Db
            let eventId =
                try
                    (json (post client ingestPath (aiCall ()))).GetProperty("event").GetProperty("id").GetString()
                finally
                    app.StopAsync().Wait()
            // A new process: the same database file, opened again; nothing in memory survives.
            SqliteConnection.ClearAllPools()
            let reopened = Db.openDatabase t.Db.Path
            let app2, client2 = server reopened
            try
                Expect.equal (Db.read reopened (fun s -> Store.leadOrFail s eventId)).Deliveries[0].Status "pending" "still owed"
                drain reopened client2
                Expect.equal (Db.read reopened (fun s -> Store.leadOrFail s eventId)).Deliveries[0].Status "delivered" "delivered after the restart"
                let again = post client2 ingestPath (aiCall ())
                Expect.equal (int again.StatusCode) 200 "the same lead, recognised after the restart"
            finally
                app2.StopAsync().Wait()
        }
    ]
