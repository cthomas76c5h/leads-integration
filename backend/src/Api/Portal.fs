/// The firm portal's API (docs/api.md, "Firm portal"): a firm's own people, signed in with a bearer
/// token, see and act on their own firm's leads only (Access.fs). The operator console's routes are
/// separate and unchanged.
module LeadIntegrations.Api.Portal

open System
open Microsoft.AspNetCore.Http
open Oxpecker
open LeadIntegrations
open LeadIntegrations.Api.Http
open LeadIntegrations.Api.Handlers
open LeadIntegrations.Domain

let private user (c: Ctx) (ctx: HttpContext) : Access.FirmUser =
    let header = ctx.Request.Headers.Authorization.ToString()
    let token = if header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) then header.Substring 7 else null
    match Access.signedIn c.Db token with
    | Some u -> u
    | None -> fail 401 "sign in: a firm user token is required"

let read (c: Ctx) (id: string) : EndpointHandler =
    fun ctx -> ok (Access.leadFor c.Db (user c ctx) "read" id) ctx

/// The firm's own leads. Asking for another firm's is refused, audited, never filtered into silence.
let list (c: Ctx) : EndpointHandler =
    fun ctx ->
        let u = user c ctx
        match query ctx "firmId" with
        | null -> ()
        | other when other = u.FirmId -> ()
        | other ->
            Access.refuse c.Db u "list" other null
            fail 403 "a firm user sees only its own firm's leads"
        let q: Store.LeadQuery =
            { Q = query ctx "q"; FirmId = u.FirmId; Category = query ctx "category"; Outcome = query ctx "outcome"
              Test = Nullable false; Limit = queryInt ctx "limit" 50; Offset = queryInt ctx "offset" 0 }
        let total, items = Db.read c.Db (fun s -> Store.listLeads s q)
        ok {| total = total; items = items |} ctx

/// One lead as a file to keep.
let export (c: Ctx) (id: string) : EndpointHandler =
    fun ctx ->
        let l = Access.leadFor c.Db (user c ctx) "export" id
        ctx.Response.Headers.ContentDisposition <- $"attachment; filename=\"lead-{id}.json\""
        ok l ctx

/// The firm asks for its lead to be sent again where it was not accepted. An accepted delivery is
/// never resent from here (only a senior operator may: answers.md).
let resend (c: Ctx) (id: string) : EndpointHandler =
    fun ctx ->
        let u = user c ctx
        let l = Access.leadFor c.Db u "resend" id
        let latest = l.Deliveries |> Array.distinctBy _.ConnectionId // newest first
        let resent =
            [| for d in latest do
                   if d.Status = "failed" || d.Status = "cancelled" || d.Status = "unknown" then
                       yield (Dispatch.replay c.Db d.Id { By = u.Name; Role = "firm-user"; Why = "the firm asked for it again" }).Id |]
        accepted {| resent = resent |} ctx

/// Operator: issue a firm user's sign-in token (shown once).
let issue (c: Ctx) (firmId: string) : EndpointHandler =
    fun ctx ->
        task {
            let! b = body {| name = ""; role = "member" |} ctx
            let u, token = Access.issue c.Db firmId b.name b.role
            return! created {| user = u; token = token |} ctx
        }

let refusals (c: Ctx) : EndpointHandler = fun ctx -> ok (Access.refusals c.Db) ctx
