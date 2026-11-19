/// The lab receivers. /receiver/{mode} behaves one way for anything;
/// /receiver/crm/{kind} checks the body like that CRM would (FakeCrm.fs) and
/// then behaves as ?mode= says. Every request is recorded for the UI.
module LeadIntegrations.Api.Receivers

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Oxpecker
open LeadIntegrations
open LeadIntegrations.Domain

let modes = set [ "ok"; "reject"; "flaky"; "gone"; "slow"; "down" ]

// Flaky: two failures, then a success, per path: a retry visibly recovers.
let private flakyCounts = ConcurrentDictionary<string, int>()

let private record (db: Db.Database) (ctx: HttpContext) (kind: string) (mode: string) (status: int) (verdict: string) (body: string) =
    Db.write db (fun s ->
        Store.insertReceived
            s
            { At = now ()
              Path = ctx.Request.Path.Value + ctx.Request.QueryString.Value
              Kind = kind
              Mode = mode
              Answered = status
              Verdict = verdict
              Body = (if body.Length > 4000 then body.Substring(0, 4000) else body) })

let private answer (status: int) (text: string) (ctx: HttpContext) : Task =
    ctx.Response.StatusCode <- status
    ctx.Response.ContentType <- "application/json"
    ctx.Response.WriteAsync(Json.serialize {| result = text |})

let handle (db: Db.Database) (kind: string) (mode: string) : EndpointHandler =
    fun ctx ->
        task {
            let! body = Http.readBody ctx
            let mode = if modes.Contains mode then mode else "ok"
            let problems =
                if isNull kind then [] else FakeCrm.checkWith (FakeCrm.requiredByQuery ctx.Request.QueryString.Value) kind ctx.Request.ContentType body
            let key = ctx.Request.Path.Value
            let status, verdict, retryAfter =
                match problems with
                | _ :: _ when mode <> "gone" && mode <> "down" -> 422, String.Join("; ", problems), None
                | _ ->
                    match mode with
                    | "reject" -> 422, "refused (reject mode)", None
                    | "gone" -> 410, "gone: hook removed", None
                    | "down" -> 503, "unavailable (down)", None
                    | "flaky" ->
                        let n = flakyCounts.AddOrUpdate(key, 1, fun _ v -> v + 1)
                        if n % 3 = 0 then 201, "accepted (flaky, third try)", None
                        else 503, "unavailable (flaky)", Some "1"
                    | _ -> 201, "accepted", None
            if mode = "slow" then do! Task.Delay(TimeSpan.FromSeconds 15.0)
            record db ctx kind mode status verdict body
            retryAfter |> Option.iter (fun ra -> ctx.Response.Headers.RetryAfter <- ra)
            do! answer status verdict ctx
        }

let endpoints (db: Db.Database) : Endpoint list =
    [ POST
          [ routef "/receiver/crm/{%s}" (fun kind ctx ->
                let mode = Http.query ctx "mode"
                handle db kind (if isNull mode then "ok" else mode) ctx)
            routef "/receiver/{%s}" (fun mode -> handle db null mode) ] ]
