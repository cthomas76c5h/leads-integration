/// Publishing a lead (event + one outbox row per destination, in one
/// transaction) and sending outbox rows (webhook-lab dispatcher.ts).
module LeadIntegrations.Dispatch

open System
open System.Collections.Generic
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open LeadIntegrations.Db
open LeadIntegrations.Domain

type Options =
    { TimeScale: float
      TimeoutMs: int
      Address: Address.Policy
      UserAgent: string
      /// Where this API is reached from outside: the base of acknowledgement links.
      Origin: string }

/// The link a person at the firm follows to confirm a lead (GET shows a confirm button; POST records it).
let ackUrl (origin: string) (eventId: string) (token: string) =
    if isNull token then null else $"{origin.TrimEnd '/'}/ack/{eventId}?t={Uri.EscapeDataString token}"

let private setting (c: Connection) key =
    match c.Settings.TryGetValue key with
    | true, v -> v
    | _ -> ""

/// The setting a pause records (when, in ms): an operator stopped this destination on purpose.
/// A destination imported off in its source system has none, and holds nothing.
let pausedAtKey = "pausedAt"

/// Paused by an operator: its planned deliveries are held for the record, and the hand-off goes to the fallback at once.
let isPaused (c: Connection) = not c.Enabled && setting c pausedAtKey <> ""

/// The connection stopped working (401, 403, 410): held like a pause until the firm reconnects.
let needsReconnection (c: Connection) = c.Status = "needs-reconnection"

/// Why a planned delivery to this destination waits instead of being sent, if it does.
let holdReason (c: Connection) =
    if isPaused c then Some Store.heldReason
    elif needsReconnection c then Some Store.reconnectReason
    else None

let private newDelivery (event: LeadEvent) (c: Connection) status reason (decision: RoutingDecision) (nowMs: int64) : Delivery =
    { Id = Secrets.newId "dlv"
      EventId = event.Id
      ConnectionId = c.Id
      ConnectionName = c.Name
      Kind = c.Kind
      FirmId = c.FirmId
      FirmName = c.FirmName
      Test = event.Test
      Status = status
      Stage = ""
      Reason = reason
      Decision = decision
      NextAttemptAt = (if status = "pending" then Nullable nowMs else Nullable())
      ReplayOf = null
      Resend = Unchecked.defaultof<Resend>
      Resolution = Unchecked.defaultof<Resolution>
      CreatedAt = nowMs
      Attempts = [||]
      Diagnosis = null
      Evidence = null
      VerifiedAt = Nullable()
      VerifyOverdue = false
      Verifiable = false }

let private emitFor (s: Session) (name: string) (d: Delivery) = Store.emit s name d.FirmId d.EventId d.ConnectionId

/// The events a planned delivery starts with: planned, and held or not sent when so.
let private emitPlanned (s: Session) (d: Delivery) =
    match d.Status with
    | "skipped" -> emitFor s "NotSent" d
    | _ ->
        emitFor s "DeliveryPlanned" d
        if not d.NextAttemptAt.HasValue then emitFor s "Held" d

/// A delivery planned, or not sent, as the routing decision says. Planned for a paused
/// destination, it is held: pending with no next attempt, until the destination resumes.
let private decided (event: LeadEvent) (c: Connection) (decision: RoutingDecision) (nowMs: int64) =
    if decision.Outcome <> "planned" then newDelivery event c "skipped" decision.Reason decision nowMs
    else
        match holdReason c with
        | Some reason -> { newDelivery event c "pending" reason decision nowMs with NextAttemptAt = Nullable() }
        | None -> newDelivery event c "pending" null decision nowMs

/// What intake did with a lead from a channel: recorded it, or found it already recorded for the same
/// conversation (a channel repeating its event), and answered the same either way.
type Intake =
    { Event: LeadEvent
      Deliveries: Delivery[]
      /// The conversation was already recorded: nothing new was planned or sent.
      Duplicate: bool }

/// Record the lead, then one delivery per destination: production leads get a
/// routing decision at every live or paused connection (Routing.decide: planned,
/// or not sent with a reason; planned for a paused one is held); a test goes only
/// to `onlyConnection`, carrying `template` when given, and is not routed.
let private record (s: Session) (firmId: string) (conversationId: string) (lead: Lead) (test: bool) (onlyConnection: string option) (template: string option) =
        let firm = Store.firmOrFail s firmId
        let t = now ()
        let event =
            { Id = Secrets.newId "evt"
              FirmId = firmId
              Test = test
              CreatedAt = t
              Lead = lead }
        Store.insertEvent s event
        let targets =
            Store.connectionsOf s firmId
            |> Array.filter (fun row ->
                let c = row.Connection
                match onlyConnection with
                | Some id -> c.Id = id
                // A fallback takes only hand-offs that are running late (runFallbacks), never every lead.
                | None ->
                    (c.Enabled || isPaused c) && (c.Status = "active" || needsReconnection c)
                    && setting c "stage" <> "setup" && setting c "role" <> "fallback")
        if not (String.IsNullOrEmpty conversationId) then Store.setConversation s event.Id conversationId
        if not test then
            Store.emit s "ConversationHandled" firmId event.Id null
            Store.emit s "LeadRecorded" firmId event.Id null
        let deliveries =
            targets
            |> Array.map (fun row ->
                let d =
                    // A test is an operator's deliberate check: never held.
                    if test then newDelivery event row.Connection "pending" null Unchecked.defaultof<RoutingDecision> t
                    else decided event row.Connection (Routing.decide firm row lead) t
                Store.insertDelivery s d (if d.Status = "pending" then template else None)
                if not test then emitPlanned s d
                d.Id)
        event, deliveries

let publish (db: Database) (firmId: string) (lead: Lead) (test: bool) (onlyConnection: string option) (template: string option) =
    write db (fun s -> record s firmId null lead test onlyConnection template)
    |> fun (event, ids) -> event, ids |> Array.map (fun id -> read db (fun s -> (Store.deliveryOrFail s id).Delivery))

/// A lead from a channel (RFC-008 §4.1). Keyed on the channel's conversation id: the check and the
/// insert are one transaction (and the id is unique per firm), so a channel repeating its event, even
/// at the same moment, gets the lead already recorded, with nothing planned or sent again.
let intake (db: Database) (firmId: string) (conversationId: string) (lead: Lead) : Intake =
    let eventId, ids, duplicate =
        write db (fun s ->
            match (if String.IsNullOrWhiteSpace conversationId then None else Store.eventForConversation s firmId conversationId) with
            | Some id -> id, [||], true
            | None ->
                let e, ids = record s firmId conversationId lead false None None
                e.Id, ids, false)
    read db (fun s ->
        let e = (Store.tryEvent s eventId).Value
        let deliveries =
            if duplicate then (Store.leadOrFail s eventId).Deliveries
            else ids |> Array.map (fun id -> (Store.deliveryOrFail s id).Delivery)
        { Event = e; Deliveries = deliveries; Duplicate = duplicate })

let testConnection (db: Database) (connectionId: string) (lead: Lead) (template: string option) =
    let c = read db (fun s -> (Store.connectionOrFail s connectionId).Connection)
    if c.Status <> "active" then fail 409 $"the connection is disabled: {c.DisabledReason}"
    let _, ds = publish db c.FirmId lead true (Some connectionId) template
    ds[0]

/// Send an event to a connection again as a new delivery (same event id, so the receiver can dedupe).
/// An operator settles an unknown outcome after checking the destination (RFC-008 §4.2): `found` (the
/// lead is there: accepted, on the operator's word) or `closed` (left unsent, with the reason). A resend
/// is the third way (Dispatch.replay). The resolution stands against any answer that arrives later.
let resolve (db: Database) (deliveryId: string) (kind: string) (by: string) (why: string) =
    if kind <> "found" && kind <> "closed" then fail 422 "resolution: found or closed (or resend the lead)"
    if String.IsNullOrWhiteSpace by then fail 422 "by: who checked the destination"
    write db (fun s ->
        let d = (Store.deliveryOrFail s deliveryId).Delivery
        if d.Status <> "unknown" then fail 409 $"only an unknown outcome is resolved; this delivery is {d.Status}"
        if not (isNull (box d.Resolution)) then fail 409 $"already resolved ({d.Resolution.Kind})"
        let t = now ()
        Store.setResolution s d.Id { Kind = kind; By = by; Why = why; At = t }
        match kind with
        | "found" ->
            Store.setDeliveryState s d.Id "delivered" $"found at the destination by {by}" (Nullable())
            emitFor s "DeliveryAccepted" d
        | _ -> Store.setDeliveryState s d.Id "unknown" $"closed by {by}: {why}" (Nullable())
        emitFor s "DeliveryResolved" d)
    read db (fun s -> (Store.deliveryOrFail s deliveryId).Delivery)

/// A routing change waiting for the firm's approval, kept on the destination until approved.
type RoutingDraft = { Categories: string[]; PracticeAreas: string[]; By: string }

let routingDraftKey = "routingDraft"

/// An operator prepares a new routing rule for a destination (RFC-009). Nothing changes until the
/// firm contact approves it: leads keep the live rule. Returns the revision it would become.
let proposeRouting (db: Database) (connectionId: string) (categories: string[]) (practiceAreas: string[]) (by: string) =
    let known = Categories.taxonomy.Value.Categories |> Array.map _.Id |> set
    let areas = Categories.taxonomy.Value.PracticeAreas |> Array.map _.Id |> set
    let categories = if isNull categories then [||] else categories
    let practiceAreas = if isNull practiceAreas then [||] else practiceAreas
    if categories.Length = 0 then fail 422 "categories: name at least one call category this destination takes"
    match categories |> Array.tryFind (known.Contains >> not), practiceAreas |> Array.tryFind (areas.Contains >> not) with
    | Some c, _ -> fail 422 $"categories: \"{c}\" is not a call category"
    | _, Some a -> fail 422 $"practiceAreas: \"{a}\" is not a practice area"
    | _ -> ()
    write db (fun s ->
        let c = (Store.connectionOrFail s connectionId).Connection
        let settings = Dictionary<string, string>(c.Settings)
        settings[routingDraftKey] <- Json.serialize { Categories = categories; PracticeAreas = practiceAreas; By = by }
        Store.updateConnection s { c with Settings = settings }
        Store.emit s "RevisionDrafted" c.FirmId null c.Id
        Routing.revisionOf c + 1)

/// The firm contact approves the routing change: it goes live as the next revision (RevisionWentLive).
/// A delivery is routed once, under the revision it was planned with (RFC-008 §5.2). Only when the
/// firm chooses `applyToWaiting` are its deliveries still waiting decided again under the new rule:
/// a lead the new rule excludes is cancelled with the reason; one sending now is left alone.
/// Returns the new revision and how many waiting deliveries were cancelled.
let approveRouting (db: Database) (connectionId: string) (by: string) (applyToWaiting: bool) =
    write db (fun s ->
        let row = Store.connectionOrFail s connectionId
        let c = row.Connection
        let draft =
            match setting c routingDraftKey with
            | "" -> fail 409 "there is no routing change waiting for approval"
            | json -> Json.deserialize<RoutingDraft> json
        let revision = Routing.revisionOf c + 1
        let settings = Dictionary<string, string>(c.Settings)
        settings.Remove routingDraftKey |> ignore
        settings[Routing.categoriesKey] <- String.Join(",", draft.Categories)
        if draft.PracticeAreas.Length = 0 then settings.Remove Routing.practiceAreasKey |> ignore
        else settings[Routing.practiceAreasKey] <- String.Join(",", draft.PracticeAreas)
        settings[Routing.revisionKey] <- string revision
        let live = { c with Settings = settings }
        Store.updateConnection s live
        Store.emit s "RevisionWentLive" c.FirmId null c.Id
        let mutable cancelled = 0
        if applyToWaiting then
            let firm = Store.firmOrFail s c.FirmId
            let waiting =
                s.Query "SELECT id FROM deliveries WHERE connection_id=$1 AND status='pending' AND test=0" [ c.Id ] (fun r -> Read.str r 0)
            for id in waiting do
                let d = (Store.deliveryOrFail s id).Delivery
                match Store.tryEvent s d.EventId with
                | None -> ()
                | Some e ->
                    let decision = Routing.decide firm { row with Connection = live } e.Lead
                    Store.setDecision s d.Id decision
                    if decision.Outcome <> "planned" then
                        Store.setDeliveryState s d.Id "cancelled" $"routing changed in revision {revision}: {decision.Reason}" (Nullable())
                        emitFor s "DeliveryCancelled" d
                        cancelled <- cancelled + 1
        {| revision = revision; cancelled = cancelled; approvedBy = by |})

/// Resending a lead its destination already accepted can put a duplicate in the CRM: only a senior
/// operator may, with the reason (answers.md, "Who may resend a lead that was already accepted?").
/// Any operator may resend one that was not accepted. A test lead is an operator's own check.
let resendAllowed (accepted: bool) (test: bool) (r: Resend) : Result<unit, string> =
    let role = if isNull (box r) || isNull r.Role then "operator" else r.Role
    let why = if isNull (box r) then null else r.Why
    if not accepted || test then Ok()
    elif role <> "senior-operator" then Error "this lead was accepted: only a senior operator may resend it, once the firm confirms the record is missing"
    elif String.IsNullOrWhiteSpace why then Error "say why: resending an accepted lead needs the firm's reason (for example, the record is missing)"
    else Ok()

let replay (db: Database) (deliveryId: string) (resend: Resend) =
    let id =
        write db (fun s ->
            let old = Store.deliveryOrFail s deliveryId
            let c = (Store.connectionOrFail s old.Delivery.ConnectionId).Connection
            if c.Status <> "active" then fail 409 "the connection is disabled; reactivate it first"
            let accepted =
                s.TryOne
                    "SELECT 1 FROM deliveries WHERE event_id=$1 AND connection_id=$2 AND status='delivered'"
                    [ old.Delivery.EventId; old.Delivery.ConnectionId ]
                    (fun _ -> true)
                |> Option.isSome
            // The lead is still on its way there: a resend now would send it twice (concurrency.md, known limits).
            let inFlight =
                s.TryOne
                    "SELECT status FROM deliveries WHERE event_id=$1 AND connection_id=$2 AND status IN ('pending', 'delivering') LIMIT 1"
                    [ old.Delivery.EventId; old.Delivery.ConnectionId ]
                    (fun r -> Read.str r 0)
            match inFlight with
            | Some "delivering" -> fail 409 "this lead is being sent to that destination right now: wait for its answer"
            | Some _ -> fail 409 "this lead is still waiting to go to that destination (queued, retrying or held): resend only once it has settled"
            | None -> ()
            match resendAllowed accepted old.Delivery.Test resend with
            | Error reason -> fail 403 reason
            | Ok() -> ()
            let t = now ()
            let fresh =
                { old.Delivery with
                    Id = Secrets.newId "dlv"
                    Status = "pending"
                    Reason = null
                    NextAttemptAt = Nullable t
                    ReplayOf = old.Delivery.Id
                    Resend = (if isNull (box resend) then { By = null; Role = "operator"; Why = null } else resend)
                    Resolution = Unchecked.defaultof<Resolution>
                    CreatedAt = t
                    Attempts = [||]
                    Diagnosis = null }
            Store.insertDelivery s fresh old.Template
            // The one it replaces had an unknown outcome: settled as resent, so a late answer cannot change it.
            if old.Delivery.Status = "unknown" && isNull (box old.Delivery.Resolution) then
                let who = if isNull (box resend) || isNull resend.By then "an operator" else resend.By
                Store.setResolution s old.Delivery.Id { Kind = "resent"; By = who; Why = (if isNull (box resend) then null else resend.Why); At = t }
                emitFor s "DeliveryResolved" old.Delivery
            if not fresh.Test then emitFor s "LeadResent" fresh
            fresh.Id)
    read db (fun s -> (Store.deliveryOrFail s id).Delivery)

/// Missed leads (recorded while the destination was paused or stopped) get a
/// delivery now, oldest first, with routing applied as it would have been.
let sendMissed (db: Database) (connectionId: string) =
    write db (fun s ->
        let row = Store.connectionOrFail s connectionId
        let c = row.Connection
        if c.Status <> "active" || not c.Enabled || setting c "stage" = "setup" then
            fail 409 "the destination is not live: reactivate or resume it first"
        let firm = Store.firmOrFail s c.FirmId
        let t = now ()
        let mutable queued = 0
        let mutable notSent = 0
        for e in Store.missedEvents s connectionId do
            let d = decided e c (Routing.decide firm row e.Lead) t
            Store.insertDelivery s d None
            emitPlanned s d
            if d.Status = "pending" then queued <- queued + 1 else notSent <- notSent + 1
        {| queued = queued; notSent = notSent |})

/// Is this the firm's fallback: the destination it agreed takes the hand-off when the usual one cannot in time.
let isFallback (c: Connection) = setting c "role" = "fallback"

/// Leads with no accepted delivery go to the firm's fallback, once each: at once when every
/// delivery that could reach the firm is held (its destinations are paused), otherwise once
/// the fallback delay has passed (1 hour by default, 15 minutes for emergencies and custody).
/// Pre-agreed in setup and always visible (RFC-001: never a silent reroute). Returns how many were queued.
let runFallbacks (db: Database) (nowMs: int64) : int =
    write db (fun s ->
        let candidates =
            s.Query
                """SELECT e.id, c.id,
                          -- Held, and anything else that could still reach the firm (neither held nor not sent).
                          (SELECT COUNT(*) FROM deliveries x WHERE x.event_id = e.id AND x.status = 'pending' AND x.next_attempt_at IS NULL),
                          (SELECT COUNT(*) FROM deliveries x WHERE x.event_id = e.id AND x.status <> 'skipped'
                             AND NOT (x.status = 'pending' AND x.next_attempt_at IS NULL))
                   FROM events e
                   JOIN connections c ON c.firm_id = e.firm_id AND c.enabled = 1 AND c.status = 'active'
                        AND c.settings LIKE '%"role":"fallback"%'
                   WHERE e.test = 0 AND e.created_at > $1
                     AND NOT EXISTS (SELECT 1 FROM attempts a JOIN deliveries x ON x.id = a.delivery_id
                                      WHERE x.event_id = e.id AND a.outcome = 'delivered')
                     AND NOT EXISTS (SELECT 1 FROM deliveries x WHERE x.event_id = e.id AND x.connection_id = c.id)
                     -- Not a lead the firm's routing sends nowhere on purpose: the fallback never overrides routing.
                     -- A lead problem is not routing: that lead is still owed a hand-off.
                     AND NOT (EXISTS (SELECT 1 FROM deliveries x WHERE x.event_id = e.id)
                              AND NOT EXISTS (SELECT 1 FROM deliveries x WHERE x.event_id = e.id
                                                AND NOT (x.status = 'skipped' AND COALESCE(x.reason, '') NOT LIKE 'lead problem:%')))"""
                [ nowMs - 2L * 86_400_000L ]
                (fun r -> Read.str r 0, Read.str r 1, Read.int r 2, Read.int r 3)
        let mutable queued = 0
        for eventId, connectionId, held, other in candidates do
            match Store.tryEvent s eventId with
            | Some e ->
                // The fallback's own clock (Urgency.fallbackAfterMs), not a share of the window to act,
                // or at once when the usual destinations are paused: waiting cannot deliver.
                let allHeld = held > 0 && other = 0
                if allHeld || nowMs - e.CreatedAt >= Urgency.fallbackAfterMs (Categories.classify e.Lead.CallType).Category then
                    let c = (Store.connectionOrFail s connectionId).Connection
                    Store.insertDelivery s (newDelivery e c "pending" null Unchecked.defaultof<RoutingDecision> nowMs) None
                    Store.emit s "FallbackUsed" e.FirmId e.Id c.Id
                    queued <- queued + 1
            | None -> ()
        queued)

/// A lead is still owed a hand-off unless the firm's routing sends it nowhere on purpose
/// (every delivery skipped by a rule; a lead problem still owes one). SQL over `e`.
let private owedSql =
    """NOT (EXISTS (SELECT 1 FROM deliveries x WHERE x.event_id = e.id)
             AND NOT EXISTS (SELECT 1 FROM deliveries x WHERE x.event_id = e.id
                               AND NOT (x.status = 'skipped' AND COALESCE(x.reason, '') NOT LIKE 'lead problem:%')))"""

/// The hand-off's own clocks (RFC-008 §3.2), checked with the fallbacks:
/// - an emergency or custody lead with no acknowledgement within its fallback delay escalates to an
///   operator who calls the firm, once (HandOffEscalated);
/// - a lead with nothing accepted when its window to act closes has expired (HandOffExpired), once.
/// Returns how many hand-offs changed.
let runHandOffs (db: Database) (nowMs: int64) : int =
    write db (fun s ->
        let mutable changed = 0
        let waiting =
            s.Query
                ("""SELECT e.id, e.firm_id, e.lead, e.created_at FROM events e
                   WHERE e.test = 0 AND e.ack_required = 1 AND e.acknowledged_at IS NULL AND e.escalated_at IS NULL
                     AND e.created_at > $1 AND """ + owedSql)
                [ nowMs - 2L * 86_400_000L ]
                (fun r -> Read.str r 0, Read.str r 1, Read.str r 2, Read.int64 r 3)
        for id, firmId, lead, createdAt in waiting do
            let category = (Categories.classify (Json.deserialize<Lead> lead).CallType).Category
            if nowMs - createdAt >= Urgency.fallbackAfterMs category then
                s.Execute "UPDATE events SET escalated_at=$2 WHERE id=$1" [ id; nowMs ] |> ignore
                Store.emit s "HandOffEscalated" firmId id null
                changed <- changed + 1
        let expired =
            s.Query
                ("""SELECT e.id, e.firm_id FROM events e
                   WHERE e.test = 0 AND e.handoff_due_at IS NOT NULL AND e.handoff_due_at < $1 AND e.expired_at IS NULL
                     AND NOT EXISTS (SELECT 1 FROM attempts a JOIN deliveries x ON x.id = a.delivery_id
                                      WHERE x.event_id = e.id AND a.outcome = 'delivered' AND a.at <= e.handoff_due_at)
                     AND """ + owedSql)
                [ nowMs ]
                (fun r -> Read.str r 0, Read.str r 1)
        for id, firmId in expired do
            s.Execute "UPDATE events SET expired_at=$2 WHERE id=$1" [ id; nowMs ] |> ignore
            Store.emit s "HandOffExpired" firmId id null
            changed <- changed + 1
        changed)

/// The body (and headers) a connection would receive for a lead.
let buildFor (c: Store.ConnectionRow) (template: string option) (eventId: string) (lead: Lead) =
    let present name = c.Connection.Credentials |> Array.exists (fun f -> f.Name = name && f.Present)
    let template = defaultArg template c.Template
    Payloads.build c.Connection.Kind c.Connection.Settings present template eventId c.Connection.FirmName lead

let headersFor (c: Store.ConnectionRow) (contentType: string) (eventId: string) (body: string) (userAgent: string) (nowMs: int64) =
    [ "content-type", contentType
      "user-agent", userAgent
      "x-integration-kind", c.Connection.Kind
      yield! Signing.headers c.SigningSecret eventId body nowMs ]

let private snippet (s: string) = if isNull s then "" elif s.Length > 500 then s.Substring(0, 500) else s

/// What one attempt's outcome does to its delivery and connection. The worker
/// and the lab simulator (Simulate.fs) both apply it, so they cannot drift.
type Transition =
    { /// The attempt's outcome text.
      Outcome: string
      NextInMs: int64 option
      Status: string
      Reason: string
      /// Some when the connection changes.
      Connection: Connection option
      /// True: the connection needs reconnection; hold its other waiting deliveries.
      HoldPending: bool }

/// `delayFor retryAfter`: the wait before the next attempt, or None when out of attempts.
let transition (current: Connection) (test: bool) (n: int) (outcome: Policy.Outcome) (delayFor: int64 option -> int64 option) : Transition =
    let failed reason =
        let connection =
            if test then None
            else
                let failures = current.ConsecutiveFailures + 1
                if failures >= Policy.disableAfterConsecutiveFailures then
                    Some
                        { current with
                            ConsecutiveFailures = failures
                            Status = "disabled"
                            DisabledReason = $"{failures} leads in a row failed" }
                else Some { current with ConsecutiveFailures = failures }
        { Outcome = $"failed: {reason}"; NextInMs = None; Status = "failed"; Reason = reason; Connection = connection; HoldPending = false }
    match outcome with
    | Policy.Delivered ->
        { Outcome = "delivered"
          NextInMs = None
          Status = "delivered"
          Reason = null
          Connection = (if current.ConsecutiveFailures <> 0 then Some { current with ConsecutiveFailures = 0 } else None)
          HoldPending = false }
    | Policy.NeedsReconnection reason ->
        // Not the destination failing: its streak is untouched. A production lead is held for the
        // record until the firm reconnects; a test lead is an operator's check, and shows the refusal.
        { Outcome = $"needs reconnection: {reason}"
          NextInMs = None
          Status = (if test then "failed" else "pending")
          Reason = (if test then reason else $"{Store.reconnectReason} ({reason})")
          Connection = Some { current with Status = "needs-reconnection"; DisabledReason = reason }
          HoldPending = true }
    | Policy.Retry(reason, retryAfterMs) ->
        match delayFor retryAfterMs with
        | Some delay -> { Outcome = $"retry: {reason}"; NextInMs = Some delay; Status = "pending"; Reason = reason; Connection = None; HoldPending = false }
        | None -> failed $"gave up after {n} attempts: {reason}"
    | Policy.Failed reason -> failed reason
    | Policy.Unknown reason ->
        // Not a failure of the destination, and not retried: the streak is untouched.
        { Outcome = "unknown: no answer"; NextInMs = None; Status = "unknown"; Reason = reason; Connection = None; HoldPending = false }

/// How long a lease lasts: longer than any one attempt (the request timeout), so a live worker
/// does not lose it mid-send.
let leaseMs = 5L * 60_000L

/// This process's worker identity, on every lease it takes.
let workerId = Secrets.newId "wkr"

/// How a destination handles a lead it may have seen (RFC-008 §4.3): the destination's own
/// `duplicates` setting where its receiver was confirmed, else its kind's receiver contract.
let duplicatesOf (c: Connection) =
    match setting c "duplicates" with
    | "" -> if Catalog.harmlessDuplicates.Contains c.Kind then "harmless" else "unknown"
    | d -> d

/// After an unknown outcome, send again only where a second copy cannot do harm (RFC-008 §4.3).
/// Everywhere else an operator checks the destination first.
let retryAfterUnknown (duplicates: string) = duplicates = "harmless" || duplicates = "idempotency-key"

/// Why a delivery waits as unknown, for the operator.
let unknownReason (duplicates: string) =
    $"Sent, but no answer was recorded, and this destination cannot be trusted to ignore a second copy ({duplicates} duplicate handling). Check whether it has the lead, then resend or close it"

/// The answer's outcome, given the destination. A request that timed out may have arrived (RFC-008 §4.2):
/// retried as transient only where a second copy is harmless, otherwise unknown. A refused connection,
/// a DNS or TLS failure sent nothing, so it stays transient.
let outcomeOf (c: Connection) (timedOut: bool) (classified: Policy.Outcome) =
    match classified with
    | Policy.Retry(reason, _) when timedOut && not (retryAfterUnknown (duplicatesOf c)) ->
        Policy.Unknown(unknownReason (duplicatesOf c) + $" ({reason})")
    | o -> o

/// Faults the scenario runner injects through test control (RFC-008 §10). Empty unless test control set one.
module Faults =
    open System.Collections.Concurrent

    /// (lead, destination) whose next claim stalls just before its attempt starts.
    let stallBeforeStart = ConcurrentDictionary<struct (string * string), bool>()
    /// Stalled workers, by delivery: the lease version each still believes it holds.
    let stalled = ConcurrentDictionary<string, int64>()
    /// (lead, destination) whose worker dies after sending, before recording the answer.
    let crashAfterSend = ConcurrentDictionary<struct (string * string), bool>()
    /// Destinations whose next answer is held: the attempt is on the wire until released.
    let holdNextAnswer = ConcurrentDictionary<string, bool>()
    /// (lead, destination) whose worker records the attempt as started, then stops before sending.
    let stallAfterStart = ConcurrentDictionary<struct (string * string), bool>()
    /// Those workers, by delivery: what they do when they wake (send, then record the answer).
    let stalledSends = ConcurrentDictionary<string, unit -> Task<unit>>()
    let heldAnswers = ConcurrentDictionary<string, ResizeArray<unit -> unit>>()

type private Begun =
    | Started of Store.ConnectionRow * LeadEvent * Payloads.Built * int
    | Stopped

/// Start an attempt (RFC-008 §5.3): in one transaction, check the lease and eligibility, and record
/// the attempt as started. The request is sent only after it commits. A pause or reconnection
/// committed before this denies it; one committed after cannot recall it. SQLite's single writer
/// serialises this with every change to the destination; a database with concurrent writers would
/// lock the destination's row here.
let private beginAttempt (db: Database) (origin: string) (deliveryId: string) (version: int64) : Begun =
    write db (fun s ->
        if not (Store.holdsLease s deliveryId version) then Stopped
        else
            let row = Store.deliveryOrFail s deliveryId
            let d = row.Delivery
            let stop status reason =
                Store.setDeliveryState s d.Id status reason (Nullable())
                // Pending with no next attempt is held, until the destination resumes or is reconnected.
                if status = "pending" then emitFor s "Held" d
                elif status = "cancelled" then emitFor s "DeliveryCancelled" d
                Stopped
            match Store.tryConnection s d.ConnectionId, Store.tryEvent s d.EventId with
            | None, _ -> stop "cancelled" "connection removed"
            | _, None -> stop "cancelled" "event missing"
            | Some c, _ when needsReconnection c.Connection && not d.Test -> stop "pending" Store.reconnectReason
            | Some c, _ when c.Connection.Status <> "active" -> stop "cancelled" $"connection disabled: {c.Connection.DisabledReason}"
            // Paused since it was planned: held, unsent, until the destination resumes (Store.releaseHeld).
            // A test lead is an operator's deliberate check, so it still goes.
            | Some c, _ when not c.Connection.Enabled && not d.Test -> stop "pending" Store.heldReason
            | Some c, Some e ->
                match buildFor c row.Template e.Id e.Lead with
                | Error errors ->
                    Store.setDeliveryState s d.Id "failed" ("body: " + String.Join("; ", errors)) (Nullable()) // never fixes itself on retry
                    Stopped
                | Ok built ->
                    let built = if d.Test then built else Payloads.withAcknowledgement c.Connection.Kind (ackUrl origin e.Id (Store.ackToken s e.Id)) built
                    let n = d.Attempts.Length + 1
                    Store.startAttempt s d.Id n (now ()) c.Connection.DeliveryUrl built.Body
                    Started(c, e, built, n))

type private Answer =
    { Status: int option
      Error: string
      RetryAfter: string
      Response: string
      Refused: bool
      TimedOut: bool
      DurationMs: int64 }

let private send (http: HttpClient) (opts: Options) (c: Store.ConnectionRow) (e: LeadEvent) (built: Payloads.Built) (started: int64) =
    task {
        let watch = Diagnostics.Stopwatch.StartNew()
        let url = c.Connection.DeliveryUrl
        let! status, error, retryAfter, response, refused, timedOut =
            task {
                match Address.checkShape opts.Address url with
                | Error reason -> return None, $"address refused: {reason}", null, "", true, false
                | Ok uri ->
                    match! Address.checkResolves opts.Address uri with
                    | Error reason -> return None, $"address refused: {reason}", null, "", true, false
                    | Ok uri ->
                        try
                            use req = new HttpRequestMessage(HttpMethod.Post, uri)
                            req.Content <- new StringContent(built.Body, Encoding.UTF8)
                            req.Content.Headers.ContentType <- Headers.MediaTypeHeaderValue.Parse built.ContentType
                            for k, v in headersFor c built.ContentType e.Id built.Body opts.UserAgent started do
                                if k <> "content-type" then req.Headers.TryAddWithoutValidation(k, v) |> ignore
                            use cts = new CancellationTokenSource(opts.TimeoutMs)
                            use! res = http.SendAsync(req, cts.Token)
                            let! text = res.Content.ReadAsStringAsync()
                            let retryAfter =
                                match res.Headers.RetryAfter with
                                | null -> null
                                | ra when ra.Delta.HasValue -> string ra.Delta.Value.TotalSeconds
                                | ra when ra.Date.HasValue -> ra.Date.Value.ToString "R"
                                | _ -> null
                            return Some(int res.StatusCode), null, retryAfter, snippet text, false, false
                        with
                        | :? OperationCanceledException -> return None, $"no answer in {opts.TimeoutMs} ms", null, "", false, true
                        | :? HttpRequestException as ex -> return None, ex.Message, null, "", false, false
            }
        return
            { Status = status
              Error = error
              RetryAfter = retryAfter
              Response = response
              Refused = refused
              TimedOut = timedOut
              DurationMs = watch.ElapsedMilliseconds }
    }

/// Record the answer (RFC-008 §5.3). While this lease version still holds the delivery, the answer
/// decides it. A worker that lost its lease keeps the answer as evidence on its own attempt, and never
/// changes the delivery a newer lease owns; an accepted late answer settles a delivery left unknown.
let private complete (db: Database) (opts: Options) (random: unit -> float) (deliveryId: string) (version: int64) (connectionId: string) (n: int) (started: int64) (a: Answer) =
    write db (fun s ->
        let d = (Store.deliveryOrFail s deliveryId).Delivery
        let current = (Store.connectionOrFail s connectionId).Connection
        let outcome =
            if a.Refused then Policy.Failed a.Error
            else Policy.classify a.Status a.Error a.RetryAfter (now ()) |> outcomeOf current a.TimedOut
        let t = transition current d.Test n outcome (fun ra -> Policy.nextDelayMs n opts.TimeScale ra random)
        let toNullable = function Some (v: int64) -> Nullable v | None -> Nullable()
        let record outcomeText (next: Nullable<int64>) =
            { N = n
              At = started
              DurationMs = a.DurationMs
              Url = current.DeliveryUrl
              Status = (match a.Status with Some code -> Nullable code | None -> Nullable())
              Error = a.Error
              Outcome = outcomeText
              RequestBody = ""
              ResponseSnippet = a.Response
              NextAttemptInMs = next }
        if Store.holdsLease s deliveryId version then
            Store.finishAttempt s d.Id (record t.Outcome (toNullable t.NextInMs))
            // Before this delivery's own state, so its reason (with the receiver's answer) is kept.
            if t.HoldPending then Store.holdPendingFor s current.Id
            Store.setDeliveryState s d.Id t.Status t.Reason (t.NextInMs |> Option.map (fun delay -> now () + delay) |> toNullable)
            t.Connection |> Option.iter (Store.updateConnection s)
            emitFor s "AttemptMade" d
            match t.Status with
            | "delivered" -> emitFor s "DeliveryAccepted" d
            | "failed" when t.Reason <> null && t.Reason.StartsWith "gave up" -> emitFor s "DeliveryGaveUp" d
            | "failed" -> emitFor s "DeliveryRefused" d
            | "pending" when t.NextInMs.IsNone -> emitFor s "Held" d
            | "unknown" -> emitFor s "DeliveryUnknown" d
            | _ -> ()
        else
            Store.finishAttempt s d.Id (record $"unknown, then a late answer: {t.Outcome}" (Nullable()))
            if outcome = Policy.Delivered then
                // Settles an unknown outcome nobody has resolved. After an operator's resolution (or while another
                // worker holds the delivery) it is evidence only: the destination may now hold a second copy.
                if d.Status = "unknown" && isNull (box d.Resolution) then
                    Store.setDeliveryState s d.Id "delivered" null (Nullable())
                    emitFor s "DeliveryAccepted" d
                else emitFor s "LateAcceptance" d)

/// Start, send and record one attempt with this lease version. Faults, when test control set one,
/// stop it where a crash or a held answer would.
let private startAndSend (db: Database) (http: HttpClient) (opts: Options) (random: unit -> float) (deliveryId: string) (version: int64) : Task<unit> =
    task {
        match beginAttempt db opts.Origin deliveryId version with
        | Stopped -> ()
        | Started(c, e, built, n) when fst (Faults.stallAfterStart.TryRemove(struct (e.Id, c.Connection.Id))) ->
            // Recorded as started, then nothing: the worker wakes later (worker-continues) and sends anyway,
            // with the lease version it still holds in memory.
            Faults.stalledSends[deliveryId] <-
                fun () ->
                    task {
                        let started = now ()
                        let! answer = send http opts c e built started
                        complete db opts random deliveryId version c.Connection.Id n started answer
                    }
        | Started(c, e, built, n) ->
            let started = now ()
            let! answer = send http opts c e built started
            let finish () = complete db opts random deliveryId version c.Connection.Id n started answer
            if fst (Faults.crashAfterSend.TryRemove(struct (e.Id, c.Connection.Id))) then
                () // The worker died after sending: the attempt stays started, the lease runs out, recovery decides.
            elif fst (Faults.holdNextAnswer.TryRemove c.Connection.Id) then
                Faults.heldAnswers.GetOrAdd(c.Connection.Id, fun _ -> ResizeArray()).Add finish
            else finish ()
    }

/// A lease ran out and another worker took it (RFC-008 §5.3). With no attempt started, nothing can
/// have been sent: carry on. With one started and unanswered, it is unknown; send again only where a
/// second copy is harmless, otherwise wait for an operator. Returns whether to go on and send.
let private recover (db: Database) (claim: Store.Claim) : bool =
    write db (fun s ->
        let d = (Store.deliveryOrFail s claim.DeliveryId).Delivery
        emitFor s "LeaseRecovered" d
        match Store.openAttempt s d.Id with
        | None -> true
        | Some n ->
            Store.markAttemptUnknown s d.Id n
            emitFor s "AttemptMade" d
            emitFor s "DeliveryUnknown" d
            let c = (Store.connectionOrFail s d.ConnectionId).Connection
            let duplicates = duplicatesOf c
            if retryAfterUnknown duplicates then true
            else
                Store.setDeliveryState s d.Id "unknown" (unknownReason duplicates) (Nullable())
                false)

let private runClaim (db: Database) (http: HttpClient) (opts: Options) (random: unit -> float) (claim: Store.Claim) : Task<unit> =
    task {
        if not claim.Recovered || recover db claim then
            let d = read db (fun s -> (Store.deliveryOrFail s claim.DeliveryId).Delivery)
            if fst (Faults.stallBeforeStart.TryRemove(struct (d.EventId, d.ConnectionId))) then
                Faults.stalled[d.Id] <- claim.Version // holds the lease, starts nothing
            else
                do! startAndSend db http opts random claim.DeliveryId claim.Version
    }

/// Claim and send everything due. Returns how many were claimed.
let runDue (db: Database) (http: HttpClient) (opts: Options) (random: unit -> float) (limit: int) : Task<int> =
    task {
        let claims = write db (fun s -> Store.claimDue s workerId (now ()) leaseMs limit)
        let! _ = Task.WhenAll(claims |> Array.map (runClaim db http opts random))
        return claims.Length
    }

/// Where a destination is looked up by our lead reference. The lab's fake CRMs answer at
/// `{delivery path}/lookup?ref=`; a real CRM's lookup is its own API, built per kind once its
/// sandbox confirms one (RFC-008 §4.3).
let lookupUrl (deliveryUrl: string) (reference: string) =
    let u = UriBuilder(Uri deliveryUrl)
    u.Path <- u.Path.TrimEnd('/') + "/lookup"
    let q = u.Query.TrimStart('?')
    u.Query <- (if q = "" then "" else q + "&") + "ref=" + Uri.EscapeDataString reference
    u.Uri.AbsoluteUri

/// How often an accepted record not yet found is looked up again.
let lookupEveryMs = 60_000L

/// How many lookups one sweep makes at most: they run at once, so a sweep takes at most one request timeout.
let lookupBatch = 20

/// Verification by lookup (RFC-008 §3.1): each accepted delivery to a destination that can be looked
/// up is checked until its record is found (verified, DeliveryVerified). Not found within the time to
/// verify, it is flagged once (VerificationOverdue), and still looked up for a day, in case it appears.
/// A lookup that fails says nothing either way. At most `lookupBatch` per sweep, the longest unchecked
/// first, sent at once and outside any transaction; each result is then written only if no other sweep
/// already did (conditional updates), so the events are emitted once. Returns how many deliveries changed.
let runVerifications (db: Database) (http: HttpClient) (opts: Options) (nowMs: int64) : Task<int> =
    task {
        let due =
            read db (fun s ->
                s.Query
                    """SELECT d.id, d.event_id, d.connection_id, c.delivery_url, c.firm_id,
                              (SELECT MIN(a.at) FROM attempts a WHERE a.delivery_id = d.id AND a.outcome = 'delivered')
                       FROM deliveries d JOIN connections c ON c.id = d.connection_id
                       WHERE d.status = 'delivered' AND d.test = 0 AND d.verified_at IS NULL
                         AND c.settings LIKE '%"duplicates":"lookup"%'
                         AND (d.verify_checked_at IS NULL OR d.verify_checked_at <= $1)
                         AND EXISTS (SELECT 1 FROM attempts a WHERE a.delivery_id = d.id AND a.outcome = 'delivered' AND a.at >= $2)
                       ORDER BY COALESCE(d.verify_checked_at, 0), d.id
                       LIMIT $3"""
                    [ nowMs - lookupEveryMs; nowMs - 86_400_000L; lookupBatch ]
                    (fun r -> Read.str r 0, Read.str r 1, Read.str r 2, Read.str r 3, Read.str r 4, Read.int64 r 5))
        let lookup (url: string) (eventId: string) =
            task {
                match Address.checkShape opts.Address (lookupUrl url eventId) with
                | Error _ -> return None
                | Ok uri ->
                    match! Address.checkResolves opts.Address uri with
                    | Error _ -> return None
                    | Ok uri ->
                        try
                            use cts = new CancellationTokenSource(opts.TimeoutMs)
                            use req = new HttpRequestMessage(HttpMethod.Get, uri)
                            req.Headers.TryAddWithoutValidation("user-agent", opts.UserAgent) |> ignore
                            use! res = http.SendAsync(req, cts.Token)
                            return
                                match int res.StatusCode with
                                | 200 -> Some true
                                | 404 -> Some false
                                | _ -> None
                        with
                        | :? OperationCanceledException
                        | :? HttpRequestException -> return None
            }
        let! found = Task.WhenAll(due |> Array.map (fun (_, eventId, _, url, _, _) -> lookup url eventId))
        if due.Length = 0 then return 0
        else
            return
                write db (fun s ->
                    let at = now ()
                    let mutable changed = 0
                    for (id, eventId, connectionId, _, firmId, acceptedAt), result in Array.zip due found do
                        match result with
                        | Some true ->
                            let n = s.Execute "UPDATE deliveries SET verified_at=$2, verify_checked_at=$2 WHERE id=$1 AND verified_at IS NULL" [ id; at ]
                            if n = 1 then
                                Store.emit s "DeliveryVerified" firmId eventId connectionId
                                changed <- changed + 1
                        | _ ->
                            s.Execute "UPDATE deliveries SET verify_checked_at=$2 WHERE id=$1" [ id; at ] |> ignore
                            if at - acceptedAt > Timing.verifyWithinMs () then
                                let n =
                                    s.Execute
                                        "UPDATE deliveries SET verify_overdue_at=$2 WHERE id=$1 AND verified_at IS NULL AND verify_overdue_at IS NULL"
                                        [ id; at ]
                                if n = 1 then
                                    Store.emit s "VerificationOverdue" firmId eventId connectionId
                                    changed <- changed + 1
                    changed)
    }

/// Test control: a stalled worker wakes and tries to start with the lease version it still holds.
let continueStalled (db: Database) (http: HttpClient) (opts: Options) (random: unit -> float) (deliveryId: string) : Task<unit> =
    task {
        match Faults.stalled.TryRemove deliveryId with
        | true, version -> do! startAndSend db http opts random deliveryId version
        | _ ->
            match Faults.stalledSends.TryRemove deliveryId with
            | true, resume -> do! resume ()
            | _ -> ()
    }

/// Test control: another worker tries to claim this delivery; it sends only if it wins the lease.
let claimAsAnotherWorker (db: Database) (http: HttpClient) (opts: Options) (random: unit -> float) (deliveryId: string) : Task<bool> =
    task {
        match write db (fun s -> Store.tryClaim s "wkr_second" (now ()) leaseMs deliveryId) with
        | Some claim ->
            do! runClaim db http opts random claim
            return true
        | None -> return false
    }

/// Test control: the held answers for a destination arrive.
let releaseAnswers (connectionId: string) =
    match Faults.heldAnswers.TryRemove connectionId with
    | true, finishes -> for f in finishes do f ()
    | _ -> ()

let newHttpClient () =
    new HttpClient(new SocketsHttpHandler(AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes 5.0), Timeout = Timeout.InfiniteTimeSpan)

let previewHeaders (headers: (string * string) list) =
    let d = Dictionary<string, string>()
    for k, v in headers do
        d[k] <- v
    d
