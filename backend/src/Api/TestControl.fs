/// The test-control routes (docs/api.md, "Test control"): the clock, the worker
/// run on demand, and how a fake receiver answers. Mounted only when the API
/// starts with TEST_CONTROL=true, which Config refuses against the lab's own
/// database. The scenario runner (tools/run_scenarios.py) drives the lab
/// through these and the public routes, nothing else.
module LeadIntegrations.Api.TestControl

open System
open System.Collections.Generic
open System.Net.Http
open Oxpecker
open LeadIntegrations
open LeadIntegrations.Api.Http
open LeadIntegrations.Api.Handlers
open LeadIntegrations.Domain

[<CLIMutable>]
type ClockBody = { At: int64 }

[<CLIMutable>]
type AdvanceBody = { To: int64 }

[<CLIMutable>]
type ReceiverBody =
    { Mode: string
      Status: Nullable<int>
      Body: string
      RetryAfter: string
      NoAnswer: bool
      Requires: string[] }

[<CLIMutable>]
type ConnectionBody = { Settings: Dictionary<string, string>; Mapping: string[] }

[<CLIMutable>]
type ContactTypeBody = { Name: string; Active: bool }

/// The labels a scenario names a webhook's mapping with, and the lead field each carries.
let private mappingFields =
    dict [ "First name", "first_name"; "Last name", "last_name"; "Email", "email"; "Phone", "phone"; "Note", "message" ]

/// The longest single step of `advance`: fallbacks are checked at least this often.
let private stepMs = 60_000L

let private http = lazy (Dispatch.newHttpClient ())

/// One worker pass at the current time: fallbacks, then every due delivery.
let private pass (c: Ctx) =
    task {
        let opts = Config.dispatchOptions c.Cfg
        Dispatch.runFallbacks c.Db (now ()) |> ignore
        let mutable sent = 0
        let mutable more = true
        while more do
            // Jitter fixed at zero (0.5 in Policy.nextDelayMs), so a run repeats exactly.
            let! n = Dispatch.runDue c.Db http.Value opts (fun () -> 0.5) 50
            sent <- sent + n
            more <- n > 0 && sent < 10_000
        return sent
    }

let private setClock: EndpointHandler =
    fun ctx ->
        task {
            let! b = body { At = 0L } ctx
            if b.At <= 0L then fail 422 "at is required (Unix ms)"
            Domain.setClock b.At
            return! ok {| now = now () |} ctx
        }

let private advance (c: Ctx) : EndpointHandler =
    fun ctx ->
        task {
            let! b = body { To = 0L } ctx
            let start = now ()
            if b.To < start then fail 422 "to is before the clock"
            let! _ = pass c
            let mutable t = start
            let mutable passes = 1
            while t < b.To do
                let next =
                    match Db.read c.Db Store.nextDueAt with
                    | Some d when d > t -> min d (t + stepMs)
                    | _ -> t + stepMs
                t <- min b.To next
                Domain.setClock t
                let! _ = pass c
                passes <- passes + 1
            return! ok {| now = now (); passes = passes |} ctx
        }

let private setReceiver (key: string) : EndpointHandler =
    fun ctx ->
        task {
            let! b =
                body
                    { Mode = null
                      Status = Nullable()
                      Body = null
                      RetryAfter = null
                      NoAnswer = false
                      Requires = [||] }
                    ctx
            let mode = if String.IsNullOrWhiteSpace b.Mode then "ok" else b.Mode
            if not b.NoAnswer && not b.Status.HasValue && not (Receivers.modes.Contains mode) then
                fail 422 $"mode {mode} is not one the lab's receivers implement"
            Receivers.overrides[key] <-
                { Mode = mode
                  Status = (if b.Status.HasValue then Some b.Status.Value else None)
                  Body = b.Body
                  RetryAfter = b.RetryAfter
                  NoAnswer = b.NoAnswer
                  Requires = (if isNull b.Requires then [] else List.ofArray b.Requires) }
            return! ok {| ok = true |} ctx
        }

let private setConnection (c: Ctx) (id: string) : EndpointHandler =
    fun ctx ->
        task {
            let! b = body { Settings = null; Mapping = null } ctx
            Db.write c.Db (fun s ->
                let x = (Store.connectionOrFail s id).Connection
                if not (isNull b.Settings) then
                    for kv in b.Settings do
                        x.Settings[kv.Key] <- kv.Value
                    Store.updateConnection s x
                if not (isNull b.Mapping) then
                    let rows =
                        b.Mapping
                        |> Array.map (fun label ->
                            match mappingFields.TryGetValue label with
                            | true, field -> { From = field; To = label }
                            | _ -> fail 422 $"no mapping field for label {label}")
                    Store.setConnectionTemplate s id (Payloads.rowsToTemplate rows))
            return! ok (Db.read c.Db (fun s -> (Store.connectionOrFail s id).Connection)) ctx
        }

let private setContactTypes (c: Ctx) (firmId: string) : EndpointHandler =
    fun ctx ->
        task {
            let! b = body ([||]: ContactTypeBody[]) ctx
            Db.write c.Db (fun s ->
                let f = Store.firmOrFail s firmId
                Store.updateFirm s { f with ContactTypes = b |> Array.map (fun t -> { Name = t.Name; Active = t.Active }) })
            return! ok (Db.read c.Db (fun s -> Store.firmDetail s firmId [||])) ctx
        }

let endpoints (c: Ctx) : Endpoint list =
    let g = guard
    [ POST [ route "/api/test/clock" (g setClock); route "/api/test/advance" (g (advance c)) ]
      PUT
          [ routef "/api/test/receivers/{%s}" (fun key -> g (setReceiver key))
            routef "/api/test/connections/{%s}" (fun id -> g (setConnection c id))
            routef "/api/test/firms/{%s}/contact-types" (fun id -> g (setContactTypes c id)) ] ]
