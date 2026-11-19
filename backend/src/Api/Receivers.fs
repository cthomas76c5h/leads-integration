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

let modes = set [ "ok"; "reject"; "flaky"; "gone"; "slow"; "down"; "expired-auth" ]

/// How one destination's receiver answers in a scenario run, set through test
/// control (docs/api.md) for requests carrying `?dest={key}`. Empty outside test runs.
type Override =
    { /// One of `modes`; ignored when Status or NoAnswer is set.
      Mode: string
      /// An exact answer, sent without checking the body.
      Status: int option
      Body: string
      RetryAfter: string
      /// Hold the request until the caller gives up: an unknown outcome.
      NoAnswer: bool
      /// Fields required on top of the kind's own.
      Requires: string list }

let overrides = ConcurrentDictionary<string, Override>()

// Flaky: two failures, then a success, per path and destination: a retry visibly recovers.
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

let private byMode (mode: string) (problems: string list) (key: string) =
    match problems with
    | _ :: _ when mode <> "gone" && mode <> "down" && mode <> "expired-auth" -> 422, String.Join("; ", problems), None
    | _ ->
        match mode with
        | "reject" -> 422, "refused (reject mode)", None
        | "gone" -> 410, "gone: hook removed", None
        | "down" -> 503, "unavailable (down)", None
        | "expired-auth" -> 401, "unauthorized: access token expired", None
        | "flaky" ->
            let n = flakyCounts.AddOrUpdate(key, 1, fun _ v -> v + 1)
            if n % 3 = 0 then 201, "accepted (flaky, third try)", None
            else 503, "unavailable (flaky)", Some "1"
        | _ -> 201, "accepted", None

let handle (db: Db.Database) (kind: string) (mode: string) : EndpointHandler =
    fun ctx ->
        task {
            let! body = Http.readBody ctx
            let dest = Http.query ctx "dest"
            let over =
                match (if isNull dest then None else Some dest) with
                | Some d ->
                    match overrides.TryGetValue d with
                    | true, o -> Some o
                    | _ -> None
                | None -> None
            let mode =
                match over with
                | Some o when modes.Contains o.Mode -> o.Mode
                | _ -> if modes.Contains mode then mode else "ok"
            let required = FakeCrm.requiredByQuery ctx.Request.QueryString.Value @ (over |> Option.map _.Requires |> Option.defaultValue [])
            let problems = if isNull kind then [] else FakeCrm.checkWith required kind ctx.Request.ContentType body
            let key = ctx.Request.Path.Value + "|" + (if isNull dest then "" else dest)
            match over with
            | Some o when o.NoAnswer ->
                // Hold until the caller times out and goes away; it never learns what happened.
                try
                    do! Task.Delay(TimeSpan.FromSeconds 30.0, ctx.RequestAborted)
                with :? OperationCanceledException -> ()
                record db ctx kind "no-answer" 0 "no answer (held until the caller gave up)" body
            | _ ->
                let status, verdict, retryAfter =
                    match over with
                    | Some { Status = Some s; Body = b; RetryAfter = ra } -> s, (if isNull b then "" else b), (if isNull ra then None else Some ra)
                    | _ -> byMode mode problems key
                if mode = "slow" && (over |> Option.forall (fun o -> o.Status.IsNone)) then do! Task.Delay(TimeSpan.FromSeconds 15.0)
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
