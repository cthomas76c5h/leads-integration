/// The /api handlers. Each one is thin: parse, call Core, answer.
module LeadIntegrations.Api.Handlers

open System
open System.Collections.Generic
open Microsoft.AspNetCore.Http
open Oxpecker
open LeadIntegrations
open LeadIntegrations.Api.Http
open LeadIntegrations.Domain

type Ctx =
    { Db: Db.Database
      Cfg: Config.Config }

// ----- Bodies -----

[<CLIMutable>]
type NewFirm = { Name: string }

[<CLIMutable>]
type NewLead = { Variant: string; CallType: string; Webchat: bool; ReferenceLead: string }

[<CLIMutable>]
type NewConnection = { Kind: string; Name: string; DeliveryUrl: string }

[<CLIMutable>]
type ConnectionPatch =
    { Enabled: Nullable<bool>
      DeliveryUrl: string
      Reactivate: Nullable<bool> }

[<CLIMutable>]
type PlanChange = { PriceId: string }

[<CLIMutable>]
type NewTerms = { Title: string; Summary: string; Body: string; ChangeKind: string; Require: string; EffectiveAt: int64; Source: string }

[<CLIMutable>]
type Acceptance = { FirmId: string; Role: string; Version: int }

[<CLIMutable>]
type NewSetup = { FirmId: string; Kind: string; Recipe: string; CloneFrom: string }

[<CLIMutable>]
type SetupPatch = { Recipe: string; Rows: MapRow[] }

[<CLIMutable>]
type ConnectBody = { Url: string }

[<CLIMutable>]
type DecisionBody = { Decision: string; By: string; Comment: string; Version: int }

let private emptyLead = { Variant = "full"; CallType = null; Webchat = false; ReferenceLead = null }

// ----- Catalog, overview, firms -----

let catalog: EndpointHandler = ok Catalog.kinds

let overview (c: Ctx) : EndpointHandler = fun ctx -> ok (Db.read c.Db Store.overview) ctx

let listFirms (c: Ctx) : EndpointHandler =
    fun ctx ->
        let q: Store.FirmQuery =
            { Q = query ctx "q"
              Kind = query ctx "kind"
              Source = query ctx "source"
              IssuesOnly = query ctx "issues" = "1"
              Limit = queryInt ctx "limit" 50
              Offset = queryInt ctx "offset" 0 }
        let total, items = Db.read c.Db (fun s -> Store.listFirms s q)
        ok {| total = total; items = items |} ctx

let private firmSetups (c: Ctx) (firmId: string) =
    Onboarding.list c.Db |> Array.filter (fun s -> s.FirmId = firmId) |> Array.map box

let firm (c: Ctx) (id: string) : EndpointHandler =
    fun ctx -> ok (Db.read c.Db (fun s -> Store.firmDetail s id (firmSetups c id))) ctx

let createFirm (c: Ctx) : EndpointHandler =
    fun ctx ->
        task {
            let! b = body { Name = null } ctx
            if String.IsNullOrWhiteSpace b.Name then fail 422 "name is required"
            let id = Secrets.newId "firm"
            Db.write c.Db (fun s ->
                Store.insertFirm
                    s
                    { Id = id
                      Name = b.Name.Trim()
                      KeyHash = null
                      KeyFingerprint = null
                      Active = true
                      ContactTypes = [||]
                      CustomLabels = [||]
                      CreatedAt = now () }
                Store.upsertFirmSource s id { System = Source.manual; SourceId = id; Name = b.Name.Trim(); Active = true })
            return! created (Db.read c.Db (fun s -> Store.firmDetail s id [||])) ctx
        }

let sendLead (c: Ctx) (firmId: string) : EndpointHandler =
    fun ctx ->
        task {
            let! b = body emptyLead ctx
            let firm = Db.read c.Db (fun s -> Store.firmOrFail s firmId)
            let lead =
                if String.IsNullOrWhiteSpace b.ReferenceLead then
                    Synthetic.lead firm.Name (if b.Variant = "sparse" then "sparse" else "full") (Random.Shared.Next 1000) b.CallType b.Webchat firm.CustomLabels
                else
                    // The reference lead as written; the firm's own name for the call type may replace its call type.
                    let r = (Categories.referenceLead b.ReferenceLead).Lead
                    if String.IsNullOrWhiteSpace b.CallType then r else { r with CallType = b.CallType }
            let event, deliveries = Dispatch.publish c.Db firmId lead false None None
            return! created {| event = event; deliveries = deliveries; mapping = Categories.classify lead.CallType |} ctx
        }

// ----- Call categories -----

let callCategories: EndpointHandler = fun ctx -> ok Categories.taxonomy.Value ctx

let classifyName: EndpointHandler = fun ctx -> ok (Categories.classify (query ctx "name")) ctx

let referenceLeads: EndpointHandler = fun ctx -> ok Categories.referenceLeads.Value ctx

// ----- Connections -----

let listConnections (c: Ctx) : EndpointHandler =
    fun ctx ->
        let kind = query ctx "kind"
        let status = query ctx "status"
        Db.read c.Db Store.allConnections
        |> Array.map _.Connection
        |> Array.filter (fun x -> (isNull kind || x.Kind = kind) && (isNull status || x.Status = status))
        |> fun xs -> ok xs ctx

let private connection (c: Ctx) id = Db.read c.Db (fun s -> (Store.connectionOrFail s id).Connection)

let createConnection (c: Ctx) (firmId: string) : EndpointHandler =
    fun ctx ->
        task {
            let! b = body { Kind = null; Name = null; DeliveryUrl = null } ctx
            let k = Catalog.find b.Kind
            let firm = Db.read c.Db (fun s -> Store.firmOrFail s firmId)
            let url =
                if String.IsNullOrWhiteSpace b.DeliveryUrl then $"{c.Cfg.Origin}/receiver/crm/{k.Kind}"
                else
                    match Address.checkShape (Config.addressPolicy c.Cfg) b.DeliveryUrl with
                    | Ok uri -> uri.ToString()
                    | Error reason -> fail 422 $"address: {reason}"
            let conn =
                { Id = Secrets.newId "con"
                  FirmId = firm.Id
                  FirmName = firm.Name
                  Kind = k.Kind
                  Name = (if String.IsNullOrWhiteSpace b.Name then k.Label else b.Name.Trim())
                  Source = Source.manual
                  SourceRef = null
                  Credentials = k.CredentialFields |> Array.map (fun f -> Import.credential k.Kind f null)
                  Settings = Dictionary()
                  Enabled = true
                  Status = "active"
                  DisabledReason = null
                  ConsecutiveFailures = 0
                  DeliveryUrl = url
                  Issues = [||]
                  CreatedAt = now () }
            Db.write c.Db (fun s -> Store.insertConnection s conn (Secrets.newSigningSecret ()) null)
            return! created (connection c conn.Id) ctx
        }

let patchConnection (c: Ctx) (id: string) : EndpointHandler =
    fun ctx ->
        task {
            let! b = body { Enabled = Nullable(); DeliveryUrl = null; Reactivate = Nullable() } ctx
            Db.write c.Db (fun s ->
                let x = (Store.connectionOrFail s id).Connection
                let x = if b.Enabled.HasValue then { x with Enabled = b.Enabled.Value } else x
                let x =
                    if String.IsNullOrWhiteSpace b.DeliveryUrl then x
                    else
                        match Address.checkShape (Config.addressPolicy c.Cfg) b.DeliveryUrl with
                        | Ok uri -> { x with DeliveryUrl = uri.ToString() }
                        | Error reason -> fail 422 $"address: {reason}"
                let x =
                    if b.Reactivate.HasValue && b.Reactivate.Value then
                        { x with Status = "active"; DisabledReason = null; ConsecutiveFailures = 0 }
                    else x
                Store.updateConnection s x)
            return! ok (connection c id) ctx
        }

let private sampleFor (c: Ctx) (firmId: string) (variant: string) =
    let firm = Db.read c.Db (fun s -> Store.firmOrFail s firmId)
    Synthetic.lead firm.Name (if variant = "sparse" then "sparse" else "full") 0 null false firm.CustomLabels

let preview (c: Ctx) (id: string) : EndpointHandler =
    fun ctx ->
        let row = Db.read c.Db (fun s -> Store.connectionOrFail s id)
        let lead = sampleFor c row.Connection.FirmId (query ctx "variant")
        let eventId = "evt_preview"
        match Dispatch.buildFor row None eventId lead with
        | Ok built ->
            let headers = Dispatch.headersFor row built.ContentType eventId built.Body "AnsweringAI-Webhooks/0.1" (now ())
            ok
                {| url = row.Connection.DeliveryUrl
                   headers = Dispatch.previewHeaders headers
                   body = (if built.ContentType = "application/json" then Json.indent built.Body else built.Body)
                   errors = FakeCrm.checkWith (FakeCrm.requiredByUrl row.Connection.DeliveryUrl) row.Connection.Kind built.ContentType built.Body |> Array.ofList |}
                ctx
        | Error errors ->
            ok
                {| url = row.Connection.DeliveryUrl
                   headers = Dictionary<string, string>()
                   body = ""
                   errors = errors |}
                ctx

let testConnection (c: Ctx) (id: string) : EndpointHandler =
    fun ctx ->
        let conn = connection c id
        accepted (Dispatch.testConnection c.Db id (sampleFor c conn.FirmId "full") None) ctx

// ----- Deliveries -----

let listDeliveries (c: Ctx) : EndpointHandler =
    fun ctx ->
        let test =
            match query ctx "test" with
            | "true" | "1" -> Nullable true
            | "false" | "0" -> Nullable false
            | _ -> Nullable()
        let f: Store.DeliveryFilter =
            { Status = query ctx "status"
              FirmId = query ctx "firmId"
              ConnectionId = query ctx "connectionId"
              Test = test
              Limit = queryInt ctx "limit" 100
              Offset = queryInt ctx "offset" 0 }
        ok (Db.read c.Db (fun s -> Store.listDeliveries s f |> Array.map _.Delivery)) ctx

let firmReport (c: Ctx) (id: string) : EndpointHandler =
    fun ctx -> ok (Db.read c.Db (fun s -> Report.build s id (now ()))) ctx

// ----- Billing -----

let priceBook (c: Ctx) : EndpointHandler = fun ctx -> ok (Db.read c.Db Billing.priceBook) ctx

let billingAccounts (c: Ctx) : EndpointHandler = fun ctx -> ok (Db.write c.Db (fun s -> Billing.accountRows s (now ()))) ctx

let statement (c: Ctx) (firmId: string) : EndpointHandler =
    fun ctx ->
        let which = if query ctx "cycle" = "previous" then "previous" else "current"
        // Writing: the first read of a closed cycle locks its statement.
        ok (Db.write c.Db (fun s -> Billing.statement s firmId which (now ()))) ctx

let changePlan (c: Ctx) (firmId: string) : EndpointHandler =
    fun ctx ->
        task {
            let! b = body { PriceId = null } ctx
            let t = now ()
            let startsAt = Db.write c.Db (fun s -> Billing.changePlan s firmId b.PriceId t)
            // The current cycle is always rated live, so it shows the change at once.
            return! ok {| startsAt = startsAt; statement = Db.write c.Db (fun s -> Billing.statement s firmId "current" t) |} ctx
        }

// ----- Terms of service -----

let terms (c: Ctx) : EndpointHandler =
    fun ctx -> ok (Db.read c.Db (fun s -> {| versions = Terms.versions s; coverage = Terms.coverage s |})) ctx

let publishTerms (c: Ctx) : EndpointHandler =
    fun ctx ->
        task {
            let! b = body { Title = "Terms of Service"; Summary = null; Body = null; ChangeKind = "editorial"; Require = null; EffectiveAt = 0L; Source = "text" } ctx
            let require = if String.IsNullOrEmpty b.Require then Terms.defaultRequire b.ChangeKind else b.Require
            let v = Db.write c.Db (fun s -> Terms.publish s b.Title b.Summary b.Body b.ChangeKind require b.EffectiveAt b.Source (now ()))
            return! ok {| version = v |} ctx
        }

let termsAcceptances (c: Ctx) (version: int) : EndpointHandler =
    fun ctx -> ok (Db.read c.Db (fun s -> Terms.acceptances s version (queryInt ctx "limit" 50))) ctx

/// The lab's portal users are synthetic: one admin, one member, and a new member who has never signed in.
let private portalUser (firmId: string) (role: string) =
    match role with
    | "admin" -> $"{firmId}:admin", "admin"
    | "new" -> $"{firmId}:new", "member"
    | _ -> $"{firmId}:member", "member"

let termsGate (c: Ctx) : EndpointHandler =
    fun ctx ->
        let firmId = query ctx "firm"
        let user, role = portalUser firmId (query ctx "role")
        ok (Db.read c.Db (fun s -> Terms.gate s firmId user role (now ()))) ctx

let acceptTerms (c: Ctx) : EndpointHandler =
    fun ctx ->
        task {
            let! b = body { FirmId = null; Role = "member"; Version = 0 } ctx
            let user, role = portalUser b.FirmId b.Role
            let t = now ()
            let gate = Db.write c.Db (fun s -> Terms.accept s b.Version b.FirmId user role t; Terms.gate s b.FirmId user role t)
            return! ok gate ctx
        }

// ----- Destinations -----

let listDestinations (c: Ctx) : EndpointHandler =
    fun ctx ->
        let q: Destinations.Query =
            { Q = query ctx "q"
              Health = query ctx "health"
              Kind = query ctx "kind"
              FirmActive = (match query ctx "firmActive" with | "all" -> "all" | _ -> "true")
              Limit = queryInt ctx "limit" 50
              Offset = queryInt ctx "offset" 0 }
        ok (Db.read c.Db (fun s -> Destinations.list s q (now ()))) ctx

let sendMissed (c: Ctx) (id: string) : EndpointHandler = fun ctx -> ok (Dispatch.sendMissed c.Db id) ctx

let deliverySummary (c: Ctx) : EndpointHandler =
    fun ctx ->
        let t = now ()
        let since =
            match Int64.TryParse(query ctx "since") with
            | true, v when v > 0L -> v
            | _ -> t - 86_400_000L
        let test = query ctx "test" = "true" || query ctx "test" = "1"
        let firmId = query ctx "firmId"
        let summary =
            Db.read c.Db (fun s ->
                let sum = Store.deliverySummary s test firmId since t
                let _, expired =
                    Store.listLeads s { Q = null; FirmId = firmId; Category = null; Outcome = "expired"; Test = Nullable test; Limit = 500; Offset = 0 }
                { sum with ExpiredLeads = expired |> Array.filter (fun l -> l.CreatedAt >= t - 7L * 86_400_000L) |> Array.length })
        ok summary ctx

// ----- Leads -----

let private queryBool ctx key =
    match query ctx key with
    | "true" | "1" -> Nullable true
    | "false" | "0" -> Nullable false
    | _ -> Nullable()

let listLeads (c: Ctx) : EndpointHandler =
    fun ctx ->
        let q: Store.LeadQuery =
            { Q = query ctx "q"
              FirmId = query ctx "firmId"
              Category = query ctx "category"
              Outcome = query ctx "outcome"
              Test = queryBool ctx "test"
              Limit = queryInt ctx "limit" 50
              Offset = queryInt ctx "offset" 0 }
        let total, items = Db.read c.Db (fun s -> Store.listLeads s q)
        ok {| total = total; items = items |} ctx

let lead (c: Ctx) (id: string) : EndpointHandler = fun ctx -> ok (Db.read c.Db (fun s -> Store.leadOrFail s id)) ctx

let delivery (c: Ctx) (id: string) : EndpointHandler =
    fun ctx ->
        let d, e = Db.read c.Db (fun s -> let d = (Store.deliveryOrFail s id).Delivery in d, Store.tryEvent s d.EventId)
        // The Delivery fields plus the event, as one object.
        let o = Text.Json.Nodes.JsonNode.Parse(Json.serialize d).AsObject()
        o["event"] <- Text.Json.Nodes.JsonNode.Parse(match e with Some e -> Json.serialize e | None -> "null")
        ctx.Response.ContentType <- "application/json; charset=utf-8"
        ctx.Response.WriteAsync(o.ToJsonString())

let replay (c: Ctx) (id: string) : EndpointHandler = fun ctx -> accepted (Dispatch.replay c.Db id) ctx

let received (c: Ctx) : EndpointHandler =
    fun ctx -> ok (Db.read c.Db (fun s -> Store.listReceived s (queryInt ctx "limit" 50))) ctx

// ----- Setups -----

let listSetups (c: Ctx) : EndpointHandler = fun ctx -> ok (Onboarding.list c.Db) ctx

let createSetup (c: Ctx) : EndpointHandler =
    fun ctx ->
        task {
            let! b = body { FirmId = null; Kind = null; Recipe = null; CloneFrom = null } ctx
            if String.IsNullOrEmpty b.FirmId then fail 422 "firmId is required"
            let kind = if String.IsNullOrEmpty b.Kind then "zapier-webhook" else b.Kind
            return! created (Onboarding.create c.Db b.FirmId kind b.Recipe b.CloneFrom) ctx
        }

let patchSetup (c: Ctx) (id: string) : EndpointHandler =
    fun ctx ->
        task {
            let! b = body { Recipe = null; Rows = null } ctx
            return! ok (Onboarding.update c.Db id b.Recipe b.Rows) ctx
        }

let setupAction (c: Ctx) (action: string) (id: string) : EndpointHandler =
    fun ctx ->
        match action with
        | "send" -> ok (Onboarding.send c.Db id) ctx
        | "test" -> accepted (Onboarding.test c.Db id) ctx
        | "confirm" -> ok (Onboarding.confirm c.Db id) ctx
        | "go-live" -> ok (Onboarding.goLive c.Db id) ctx
        | "discard" -> ok (Onboarding.discard c.Db id) ctx
        | _ -> fail 404 "no such action"

let setupFields (c: Ctx) (id: string) : EndpointHandler = fun ctx -> ok (Onboarding.fields c.Db id) ctx

let clientSetup (c: Ctx) (token: string) : EndpointHandler =
    fun ctx -> ok (Onboarding.loadByToken c.Db token |> Onboarding.clientView c.Db) ctx

let clientConnect (c: Ctx) (token: string) : EndpointHandler =
    fun ctx ->
        task {
            let! b = body { Url = null } ctx
            Onboarding.connect c.Db (Config.addressPolicy c.Cfg) c.Cfg.Origin token b.Url
            return! ok {| ok = true |} ctx
        }

let clientDecision (c: Ctx) (token: string) : EndpointHandler =
    fun ctx ->
        task {
            let! b = body { Decision = null; By = null; Comment = null; Version = 0 } ctx
            Onboarding.decide c.Db token b.Decision b.By b.Comment b.Version
            return! ok {| ok = true |} ctx
        }
