/// Milestone 2: canonical firm identity through the provisioning boundary (suite S-2). A firm is an
/// operational tenant with an opaque canonical id issued outside delivery; one billing account may
/// hold several firms; a contract lead names its firm by the canonical id.
module LeadIntegrations.Tests.ProvisioningTests

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

let private request name : Provisioning.Request = { Name = name; TimeZone = null; AccountId = null; Adopt = null }

let private provision (db: Db.Database) (id: string) (r: Provisioning.Request) =
    Db.write db (fun s -> Provisioning.provision s id r (now ()))

let private failsWith (status: int) (f: unit -> 'a) =
    try
        f () |> ignore
        failtest $"expected a {status}"
    with Fail(s, message) ->
        Expect.equal s status message
        message

let private firmCount (db: Db.Database) = (Db.read db Store.allFirms).Length

let private provisionedEvents (db: Db.Database) =
    Db.read db Store.domainEvents |> Array.filter (fun e -> e.Name = "FirmProvisioned") |> Array.length

let private example () =
    File.ReadAllText(Path.Combine(Db.repoRoot, "docs", "contracts", "intake-lead", "examples", "person-existing-client.json"))

/// The example lead, addressed to a firm id.
let private leadFor (firmId: string) =
    let root = Nodes.JsonNode.Parse(example ())
    root["firm"].AsObject()["id"] <- Nodes.JsonValue.Create firmId
    root.ToJsonString()

[<Tests>]
let tests =
    testList "provisioning" [
        test "a new canonical id creates the firm; the same request again changes nothing" {
            use t = new TempDb()
            let first, isNew = provision t.Db "frm-01hx7" { request "Example Firm" with TimeZone = "America/Chicago"; AccountId = "acct-1" }
            Expect.isTrue isNew "created"
            Expect.equal (first.Name, first.TimeZone, first.AccountId) ("Example Firm", "America/Chicago", "acct-1") "as provisioned"
            Expect.stringStarts first.FirmId "firm_" "the firm's own id here is delivery's, apart from the canonical one"
            let again, isNewAgain = provision t.Db "frm-01hx7" { request "Example Firm" with TimeZone = "America/Chicago"; AccountId = "acct-1" }
            Expect.isFalse isNewAgain "not new"
            Expect.equal again first "unchanged"
            Expect.equal (firmCount t.Db) 1 "one firm"
            Expect.equal (provisionedEvents t.Db) 1 "provisioned once"
        }

        test "provisioning is the source of truth for the name, time zone and account" {
            use t = new TempDb()
            provision t.Db "frm-01hx7" (request "Example Firm") |> ignore
            let updated, _ = provision t.Db "frm-01hx7" { request "Example Firm LLP" with TimeZone = "America/Denver"; AccountId = "acct-9" }
            Expect.equal (updated.Name, updated.TimeZone, updated.AccountId) ("Example Firm LLP", "America/Denver", "acct-9") "updated"
            let detail = Db.read t.Db (fun s -> Store.firmDetail s updated.FirmId [||])
            Expect.equal (detail.CanonicalId, detail.AccountId, detail.Name) ("frm-01hx7", "acct-9", "Example Firm LLP") "the firm here shows it"
            Expect.equal (firmCount t.Db) 1 "still one firm"
        }

        test "one billing account holds several firms" {
            use t = new TempDb()
            let a, _ = provision t.Db "frm-office-a" { request "Example Firm, north office" with AccountId = "acct-shared" }
            let b, _ = provision t.Db "frm-office-b" { request "Example Firm, south office" with AccountId = "acct-shared" }
            Expect.notEqual a.FirmId b.FirmId "two tenants"
            Expect.equal a.AccountId b.AccountId "one account"
        }

        test "an existing firm is adopted under a canonical id, once" {
            use t = new TempDb()
            Import.apply t.Db origin (batch Source.legacy [ firm "1" "Imported Firm" "k" [ conn "10" "email" [] ] ]) |> ignore
            let imported = (Db.read t.Db Store.allFirms).[0].Id
            let adopted, isNew = provision t.Db "frm-legacy-1" { request "Imported Firm" with Adopt = imported }
            Expect.isTrue isNew "linked now"
            Expect.equal adopted.FirmId imported "the same firm, not a copy"
            Expect.equal (firmCount t.Db) 1 "no new firm"
            Expect.contains (Db.read t.Db (fun s -> Store.firmDetail s imported [||])).Sources Source.legacy "its legacy source kept"
            Expect.stringContains (failsWith 409 (fun () -> provision t.Db "frm-legacy-2" { request "Imported Firm" with Adopt = imported })) "already provisioned as frm-legacy-1" "never two canonical ids"
            let other, _ = provision t.Db "frm-other" (request "Other Firm")
            Expect.stringContains (failsWith 409 (fun () -> provision t.Db "frm-legacy-1" { request "Imported Firm" with Adopt = other.FirmId })) "cannot adopt" "never moved to another firm"
        }

        test "an id of the wrong shape, no name, or an unknown time zone is refused, and nothing is created" {
            use t = new TempDb()
            failsWith 422 (fun () -> provision t.Db "has space" (request "A")) |> ignore
            failsWith 422 (fun () -> provision t.Db (String('x', 65)) (request "A")) |> ignore
            failsWith 422 (fun () -> provision t.Db "-leading-dash" (request "A")) |> ignore
            failsWith 422 (fun () -> provision t.Db "frm-1" (request " ")) |> ignore
            failsWith 422 (fun () -> provision t.Db "frm-1" { request "A" with TimeZone = "EST" }) |> ignore
            failsWith 422 (fun () -> provision t.Db "frm-1" { request "A" with AccountId = "acct 1" }) |> ignore
            failsWith 404 (fun () -> provision t.Db "frm-1" { request "A" with Adopt = "firm_nope" }) |> ignore
            Expect.equal (firmCount t.Db) 0 "nothing created"
        }

        test "a lead names its firm by the canonical id; an alias never shadows one" {
            use t = new TempDb()
            let provisioned, _ = provision t.Db "frm-01hx7" (request "Example Firm")
            Db.write t.Db (fun s ->
                Store.insertConnection
                    s
                    { Id = Secrets.newId "con"; FirmId = provisioned.FirmId; FirmName = "Example Firm"; Kind = "webhook"; Name = "Webhook"
                      Source = Source.manual; SourceRef = null; Credentials = [||]; Settings = Collections.Generic.Dictionary()
                      Enabled = true; Status = "active"; DisabledReason = null; ConsecutiveFailures = 0
                      DeliveryUrl = $"{origin}/receiver/crm/webhook"; Issues = [||]; CreatedAt = now () }
                    (Secrets.newSigningSecret ())
                    null)
            let r = Dispatch.ingest t.Db (leadFor "frm-01hx7")
            Expect.equal r.Intake.Event.FirmId provisioned.FirmId "the provisioned firm"
            Expect.equal r.Intake.Deliveries.Length 1 "to its destination"
            Expect.stringContains (failsWith 422 (fun () -> Dispatch.ingest t.Db (leadFor "frm-nobody"))) "no firm was provisioned under it" "an unknown canonical id"
            // An alias equal to a canonical id would be ambiguous: refused both ways.
            failsWith 422 (fun () -> Db.write t.Db (fun s -> Store.setFirmAlias s "frm-01hx7" provisioned.FirmId (now ()))) |> ignore
            Db.write t.Db (fun s -> Store.setFirmAlias s "general-firm" provisioned.FirmId (now ())) |> ignore
            failsWith 422 (fun () -> provision t.Db "general-firm" (request "Another Firm")) |> ignore
        }

        test "the boundary over HTTP: 201 when new, 200 when the same, 404 when unknown" {
            use t = new TempDb()
            let builder = WebApplication.CreateBuilder()
            builder.WebHost.UseTestServer() |> ignore
            let config: Api.Config.Config =
                { Port = 0; DbPath = t.Db.Path; Origin = origin; TimeScale = 1.0; DeliveryTimeoutMs = 5000
                  AllowLocalTargets = true; WorkerEnabled = false; FrontendDist = "does-not-exist"; TestControl = false }
            let app = Api.App.build builder config t.Db
            app.StartAsync().Wait()
            let client = app.GetTestClient()
            try
                let put (body: string) = client.PutAsync("/api/provisioning/firms/frm-http-1", new StringContent(body, Encoding.UTF8, "application/json")).Result
                Expect.equal (int (put """{"name":"Example Firm","accountId":"acct-1"}""").StatusCode) 201 "created"
                Expect.equal (int (put """{"name":"Example Firm","accountId":"acct-1"}""").StatusCode) 200 "the same"
                let got = JsonDocument.Parse(client.GetAsync("/api/provisioning/firms/frm-http-1").Result.Content.ReadAsStringAsync().Result).RootElement
                Expect.equal (got.GetProperty("accountId").GetString()) "acct-1" "read back"
                let firmId = got.GetProperty("firmId").GetString()
                let detail = JsonDocument.Parse(client.GetAsync($"/api/firms/{firmId}").Result.Content.ReadAsStringAsync().Result).RootElement
                Expect.equal (detail.GetProperty("canonicalId").GetString()) "frm-http-1" "the firm shows its canonical id"
                Expect.equal (int (client.GetAsync("/api/provisioning/firms/frm-nope").Result.StatusCode)) 404 "unknown"
                Expect.equal (int (put """{"name":"Example Firm","timeZone":"Mars/Olympus"}""").StatusCode) 422 "a bad zone"
            finally
                app.StopAsync().Wait()
        }
    ]
