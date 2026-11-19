/// Request and response helpers: JSON with the shared options, query values,
/// and turning Domain.Fail into its status with { error }.
module LeadIntegrations.Api.Http

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Oxpecker
open LeadIntegrations

let writeJson (status: int) (value: obj) (ctx: HttpContext) : Task =
    ctx.Response.StatusCode <- status
    ctx.Response.ContentType <- "application/json; charset=utf-8"
    ctx.Response.WriteAsync(JsonSerializer.Serialize(value, Json.options))

let ok (value: 'T) : EndpointHandler = fun ctx -> writeJson 200 (box value) ctx
let created (value: 'T) : EndpointHandler = fun ctx -> writeJson 201 (box value) ctx
let accepted (value: 'T) : EndpointHandler = fun ctx -> writeJson 202 (box value) ctx

let error (status: int) (message: string) (ctx: HttpContext) =
    writeJson status (box {| error = message |}) ctx

let readBody (ctx: HttpContext) : Task<string> =
    task {
        use reader = new StreamReader(ctx.Request.Body)
        return! reader.ReadToEndAsync()
    }

/// The JSON body as 'T; an empty body is `empty`, invalid JSON is a 422.
let body<'T> (empty: 'T) (ctx: HttpContext) : Task<'T> =
    task {
        let! text = readBody ctx
        if String.IsNullOrWhiteSpace text then return empty
        else
            try
                let v = JsonSerializer.Deserialize<'T>(text, Json.options)
                return (if isNull (box v) then empty else v)
            with :? JsonException as e ->
                return Domain.fail 422 $"invalid JSON body: {e.Message}"
    }

let query (ctx: HttpContext) (key: string) : string =
    match ctx.Request.Query.TryGetValue key with
    | true, v when not (String.IsNullOrWhiteSpace(v.ToString())) -> v.ToString().Trim()
    | _ -> null

let queryInt (ctx: HttpContext) key (fallback: int) =
    match Int32.TryParse(query ctx key) with
    | true, v -> v
    | _ -> fallback

/// Runs a handler; a Domain.Fail becomes its status with { error }.
let guard (handler: EndpointHandler) : EndpointHandler =
    fun ctx ->
        task {
            try
                do! handler ctx
            with
            | Domain.Fail(status, message) when not ctx.Response.HasStarted -> do! error status message ctx
        }
