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

/// A fault inside our own system (RFC-008 §10), for one lead's delivery to one destination.
[<CLIMutable>]
type FaultBody = { Kind: string; EventId: string; ConnectionId: string }

/// The labels a scenario names a webhook's mapping with, and the lead field each carries.
let private mappingFields =
    dict [ "First name", "first_name"; "Last name", "last_name"; "Email", "email"; "Phone", "phone"; "Note", "message" ]

/// The longest single step of `advance`: fallbacks are checked at least this often.
let private stepMs = 60_000L

let private http = lazy (Dispatch.newHttpClient ())

/// One worker pass at the current time: fallbacks, then every due delivery.
let private passOnce (c: Ctx) =
    task {
        let opts = Config.dispatchOptions c.Cfg
        Dispatch.runFallbacks c.Db (now ()) |> ignore
        Dispatch.runHandOffs c.Db (now ()) |> ignore
        let! _ = Dispatch.runVerifications c.Db http.Value opts (now ())
        let mutable sent = 0
        let mutable more = true
        while more do
            // Jitter fixed at zero (0.5 in Policy.nextDelayMs), so a run repeats exactly.
            let! n = Dispatch.runDue c.Db http.Value opts (fun () -> 0.5) 50
            sent <- sent + n
            more <- n > 0 && sent < 10_000
        return sent
    }

/// While the database is down (fault injection), a worker pass fails and does nothing, as the worker's would.
let private pass (c: Ctx) =
    task {
        try
            return! passOnce c
        with Db.Unavailable _ ->
            return 0
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

/// The newest delivery of a lead to a destination: the one a fault acts on.
let private deliveryOf (c: Ctx) (eventId: string) (connectionId: string) =
    Db.read c.Db (fun s ->
        s.TryOne
            "SELECT id FROM deliveries WHERE event_id=$1 AND connection_id=$2 ORDER BY created_at DESC, id DESC LIMIT 1"
            [ eventId; connectionId ]
            (fun r -> r.GetString 0))
    |> Option.defaultWith (fun () -> fail 404 "no delivery of that lead to that destination")

let private fault (c: Ctx) : EndpointHandler =
    fun ctx ->
        task {
            let! b = body { Kind = null; EventId = null; ConnectionId = null } ctx
            let opts = Config.dispatchOptions c.Cfg
            let random () = 0.5
            let pair = struct (b.EventId, b.ConnectionId)
            match b.Kind with
            | "worker-stalls-before-send" -> Dispatch.Faults.stallBeforeStart[pair] <- true
            | "worker-crash-after-send" -> Dispatch.Faults.crashAfterSend[pair] <- true
            | "worker-continues" -> do! Dispatch.continueStalled c.Db http.Value opts random (deliveryOf c b.EventId b.ConnectionId)
            | "second-worker-claims" ->
                let! _ = Dispatch.claimAsAnotherWorker c.Db http.Value opts random (deliveryOf c b.EventId b.ConnectionId)
                ()
            | "lease-expires" -> Db.write c.Db (fun s -> Store.expireLease s (deliveryOf c b.EventId b.ConnectionId) (now ()))
            | "answer-held" -> Dispatch.Faults.holdNextAnswer[b.ConnectionId] <- true
            | "answer-released" -> Dispatch.releaseAnswers b.ConnectionId
            | "intake-event-repeated" -> ()
            | "database-unavailable" -> c.Db.WritesFail <- true
            | "database-available" -> c.Db.WritesFail <- false
            | other -> fail 422 $"fault {other} is not one the lab can inject"
            match b.Kind with
            | "intake-event-repeated" ->
                // The channel sends the same conversation's event again, as it first did.
                let e = Db.read c.Db (fun s -> Store.leadOrFail s b.EventId)
                if isNull e.ConversationId then fail 422 "that lead came with no conversation id: nothing to repeat"
                let r = Dispatch.intake c.Db e.FirmId e.ConversationId e.Lead
                return! ok {| ok = true; told = "recorded"; duplicate = r.Duplicate; eventId = r.Event.Id |} ctx
            | _ -> return! ok {| ok = true |} ctx
        }

/// The firm's billing cycle closes now: its conversations so far are rated and kept (Billing.closeCycle),
/// as when a cycle's statement is locked at its end. A scenario cannot wait a month.
let private closeCycle (c: Ctx) (firmId: string) : EndpointHandler =
    fun ctx -> ok {| rated = Db.write c.Db (fun s -> Billing.closeCycle s firmId (now ())) |} ctx

let private events (c: Ctx) : EndpointHandler = fun ctx -> ok (Db.read c.Db Store.domainEvents) ctx

let endpoints (c: Ctx) : Endpoint list =
    let g = guard
    [ GET [ route "/api/test/events" (g (events c)) ]
      POST [ route "/api/test/clock" (g setClock); route "/api/test/advance" (g (advance c)); route "/api/test/faults" (g (fault c))
             routef "/api/test/firms/{%s}/close-cycle" (fun id -> g (closeCycle c id)) ]
      PUT
          [ routef "/api/test/receivers/{%s}" (fun key -> g (setReceiver key))
            routef "/api/test/connections/{%s}" (fun id -> g (setConnection c id))
            routef "/api/test/firms/{%s}/contact-types" (fun id -> g (setContactTypes c id)) ] ]
