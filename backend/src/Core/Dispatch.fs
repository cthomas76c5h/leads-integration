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
      UserAgent: string }

/// "Personal Injury" and "personal-injury" are the same contact type (api-2
/// strips punctuation and case before matching).
let normalizeType (s: string) =
    (if isNull s then "" else s).ToLowerInvariant() |> String.filter Char.IsLetterOrDigit

let private setting (c: Connection) key =
    match c.Settings.TryGetValue key with
    | true, v -> v
    | _ -> ""

/// Why a production lead does not go to this connection, or None to send it.
let routingExclusion (firm: Store.FirmRow) (c: Connection) (lead: Lead) : string option =
    let k = Catalog.tryFind c.Kind
    let activeTypes = firm.ContactTypes |> Array.filter _.Active |> Array.map (fun t -> normalizeType t.Name)
    if lead.Webchat && setting c "sendWebchats" = "false" then Some "chat leads are switched off for this destination"
    elif k.IsSome && k.Value.Category = "crm" && activeTypes.Length > 0 && not (Array.contains (normalizeType lead.CallType) activeTypes) then
        Some $"call type \"{lead.CallType}\" is not routed: it matches none of the firm's active contact types"
    else None

let private newDelivery (event: LeadEvent) (c: Connection) status reason (nowMs: int64) : Delivery =
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
      NextAttemptAt = (if status = "pending" then Nullable nowMs else Nullable())
      ReplayOf = null
      CreatedAt = nowMs
      Attempts = [||]
      Diagnosis = null }

/// Record the lead, then one delivery per destination: production leads go to
/// every enabled, active, live connection (routing may mark one skipped); a
/// test goes only to `onlyConnection`, carrying `template` when given.
let publish (db: Database) (firmId: string) (lead: Lead) (test: bool) (onlyConnection: string option) (template: string option) =
    write db (fun s ->
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
            |> Array.map _.Connection
            |> Array.filter (fun c ->
                match onlyConnection with
                | Some id -> c.Id = id
                // A fallback takes only hand-offs that are running late (runFallbacks), never every lead.
                | None -> c.Enabled && c.Status = "active" && setting c "stage" <> "setup" && setting c "role" <> "fallback")
        let deliveries =
            targets
            |> Array.map (fun c ->
                let excluded = if test then None else routingExclusion firm c lead
                let d =
                    match excluded with
                    | Some reason -> newDelivery event c "skipped" reason t
                    | None -> newDelivery event c "pending" null t
                Store.insertDelivery s d (if d.Status = "pending" then template else None)
                d.Id)
        event, deliveries)
    |> fun (event, ids) -> event, ids |> Array.map (fun id -> read db (fun s -> (Store.deliveryOrFail s id).Delivery))

let testConnection (db: Database) (connectionId: string) (lead: Lead) (template: string option) =
    let c = read db (fun s -> (Store.connectionOrFail s connectionId).Connection)
    if c.Status <> "active" then fail 409 $"the connection is disabled: {c.DisabledReason}"
    let _, ds = publish db c.FirmId lead true (Some connectionId) template
    ds[0]

/// Send an event to a connection again as a new delivery (same event id, so the receiver can dedupe).
let replay (db: Database) (deliveryId: string) =
    let id =
        write db (fun s ->
            let old = Store.deliveryOrFail s deliveryId
            let c = (Store.connectionOrFail s old.Delivery.ConnectionId).Connection
            if c.Status <> "active" then fail 409 "the connection is disabled; reactivate it first"
            let t = now ()
            let fresh =
                { old.Delivery with
                    Id = Secrets.newId "dlv"
                    Status = "pending"
                    Reason = null
                    NextAttemptAt = Nullable t
                    ReplayOf = old.Delivery.Id
                    CreatedAt = t
                    Attempts = [||]
                    Diagnosis = null }
            Store.insertDelivery s fresh old.Template
            fresh.Id)
    read db (fun s -> (Store.deliveryOrFail s id).Delivery)

/// Missed leads (recorded while the destination was paused or stopped) get a
/// delivery now, oldest first, with routing applied as it would have been.
let sendMissed (db: Database) (connectionId: string) =
    write db (fun s ->
        let c = (Store.connectionOrFail s connectionId).Connection
        if c.Status <> "active" || not c.Enabled || setting c "stage" = "setup" then
            fail 409 "the destination is not live: reactivate or resume it first"
        let firm = Store.firmOrFail s c.FirmId
        let t = now ()
        let mutable queued = 0
        let mutable notSent = 0
        for e in Store.missedEvents s connectionId do
            match routingExclusion firm c e.Lead with
            | Some reason ->
                Store.insertDelivery s (newDelivery e c "skipped" reason t) None
                notSent <- notSent + 1
            | None ->
                Store.insertDelivery s (newDelivery e c "pending" null t) None
                queued <- queued + 1
        {| queued = queued; notSent = notSent |})

/// Is this the firm's fallback: the destination it agreed takes the hand-off when the usual one cannot in time.
let isFallback (c: Connection) = setting c "role" = "fallback"

/// Leads with no accepted delivery once their fallback delay has passed (1 hour
/// by default, 15 minutes for emergencies and custody) go to the firm's fallback, once each. Pre-agreed in setup and always
/// visible (RFC-001: never a silent reroute). Returns how many were queued.
let runFallbacks (db: Database) (nowMs: int64) : int =
    write db (fun s ->
        let candidates =
            s.Query
                """SELECT e.id, c.id FROM events e
                   JOIN connections c ON c.firm_id = e.firm_id AND c.enabled = 1 AND c.status = 'active'
                        AND c.settings LIKE '%"role":"fallback"%'
                   WHERE e.test = 0 AND e.created_at > $1
                     AND NOT EXISTS (SELECT 1 FROM attempts a JOIN deliveries x ON x.id = a.delivery_id
                                      WHERE x.event_id = e.id AND a.outcome = 'delivered')
                     AND NOT EXISTS (SELECT 1 FROM deliveries x WHERE x.event_id = e.id AND x.connection_id = c.id)
                     -- Not a lead the firm's routing sends nowhere on purpose: the fallback never overrides routing.
                     AND NOT (EXISTS (SELECT 1 FROM deliveries x WHERE x.event_id = e.id)
                              AND NOT EXISTS (SELECT 1 FROM deliveries x WHERE x.event_id = e.id AND x.status <> 'skipped'))"""
                [ nowMs - 2L * 86_400_000L ]
                (fun r -> Read.str r 0, Read.str r 1)
        let mutable queued = 0
        for eventId, connectionId in candidates do
            match Store.tryEvent s eventId with
            | Some e ->
                // The fallback's own clock (Urgency.fallbackAfterMs), not a share of the window to act.
                if nowMs - e.CreatedAt >= Urgency.fallbackAfterMs (Categories.classify e.Lead.CallType).Category then
                    let c = (Store.connectionOrFail s connectionId).Connection
                    Store.insertDelivery s (newDelivery e c "pending" null nowMs) None
                    queued <- queued + 1
            | None -> ()
        queued)

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
      /// Some reason: cancel the connection's other pending deliveries.
      CancelPending: string option }

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
        { Outcome = $"failed: {reason}"; NextInMs = None; Status = "failed"; Reason = reason; Connection = connection; CancelPending = None }
    match outcome with
    | Policy.Delivered ->
        { Outcome = "delivered"
          NextInMs = None
          Status = "delivered"
          Reason = null
          Connection = (if current.ConsecutiveFailures <> 0 then Some { current with ConsecutiveFailures = 0 } else None)
          CancelPending = None }
    | Policy.Disable reason ->
        { Outcome = "connection disabled"
          NextInMs = None
          Status = "failed"
          Reason = reason
          Connection = Some { current with Status = "disabled"; DisabledReason = reason }
          CancelPending = Some $"connection disabled: {reason}" }
    | Policy.Retry(reason, retryAfterMs) ->
        match delayFor retryAfterMs with
        | Some delay -> { Outcome = $"retry: {reason}"; NextInMs = Some delay; Status = "pending"; Reason = reason; Connection = None; CancelPending = None }
        | None -> failed $"gave up after {n} attempts: {reason}"
    | Policy.Failed reason -> failed reason

/// One attempt at one claimed delivery.
let attempt (db: Database) (http: HttpClient) (opts: Options) (random: unit -> float) (deliveryId: string) : Task<unit> =
    task {
        let row, conn, event =
            read db (fun s ->
                let d = Store.deliveryOrFail s deliveryId
                d, Store.tryConnection s d.Delivery.ConnectionId, Store.tryEvent s d.Delivery.EventId)
        let d = row.Delivery
        let finish status reason =
            write db (fun s -> Store.setDeliveryState s d.Id status reason (Nullable()))
        match conn, event with
        | None, _ -> finish "cancelled" "connection removed"
        | _, None -> finish "cancelled" "event missing"
        | Some c, _ when c.Connection.Status <> "active" -> finish "cancelled" $"connection disabled: {c.Connection.DisabledReason}"
        // Paused since this delivery was planned: hold it, unsent, until the destination resumes
        // (Store.releaseHeld). Checked here, at send time, not only when the lead was recorded.
        // A test lead is an operator's deliberate check, so it still goes.
        | Some c, _ when not c.Connection.Enabled && not d.Test -> finish "pending" Store.heldReason
        | Some c, Some event ->
            match buildFor c row.Template event.Id event.Lead with
            | Error errors -> finish "failed" ("body: " + String.Join("; ", errors)) // a broken body never fixes itself on retry
            | Ok built ->
                let n = d.Attempts.Length + 1
                let started = now ()
                let watch = Diagnostics.Stopwatch.StartNew()
                let url = c.Connection.DeliveryUrl
                let! status, error, retryAfter, response, refused =
                    task {
                        match Address.checkShape opts.Address url with
                        | Error reason -> return None, $"address refused: {reason}", null, "", true
                        | Ok uri ->
                            match! Address.checkResolves opts.Address uri with
                            | Error reason -> return None, $"address refused: {reason}", null, "", true
                            | Ok uri ->
                                try
                                    use req = new HttpRequestMessage(HttpMethod.Post, uri)
                                    req.Content <- new StringContent(built.Body, Encoding.UTF8)
                                    req.Content.Headers.ContentType <- Headers.MediaTypeHeaderValue.Parse built.ContentType
                                    for k, v in headersFor c built.ContentType event.Id built.Body opts.UserAgent started do
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
                                    return Some(int res.StatusCode), null, retryAfter, snippet text, false
                                with
                                | :? OperationCanceledException -> return None, $"no answer in {opts.TimeoutMs} ms", null, "", false
                                | :? HttpRequestException as e -> return None, e.Message, null, "", false
                    }
                let outcome =
                    if refused then Policy.Failed error else Policy.classify status error retryAfter (now ())
                let record outcomeText (next: Nullable<int64>) =
                    { N = n
                      At = started
                      DurationMs = watch.ElapsedMilliseconds
                      Url = url
                      Status = (match status with Some s -> Nullable s | None -> Nullable())
                      Error = error
                      Outcome = outcomeText
                      RequestBody = built.Body
                      ResponseSnippet = response
                      NextAttemptInMs = next }
                write db (fun s ->
                    let current = (Store.connectionOrFail s c.Connection.Id).Connection
                    let t = transition current d.Test n outcome (fun ra -> Policy.nextDelayMs n opts.TimeScale ra random)
                    let toNullable = function Some (v: int64) -> Nullable v | None -> Nullable()
                    Store.insertAttempt s d.Id (record t.Outcome (toNullable t.NextInMs))
                    Store.setDeliveryState s d.Id t.Status t.Reason (t.NextInMs |> Option.map (fun delay -> now () + delay) |> toNullable)
                    t.Connection |> Option.iter (Store.updateConnection s)
                    t.CancelPending |> Option.iter (Store.cancelPendingFor s current.Id))
    }

/// Claim and send everything due. Returns how many were sent.
let runDue (db: Database) (http: HttpClient) (opts: Options) (random: unit -> float) (limit: int) : Task<int> =
    task {
        let ids = write db (fun s -> Store.claimDue s (now ()) limit)
        let! _ = Task.WhenAll(ids |> Array.map (fun id -> attempt db http opts random id))
        return ids.Length
    }

let newHttpClient () =
    new HttpClient(new SocketsHttpHandler(AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes 5.0), Timeout = Timeout.InfiniteTimeSpan)

let previewHeaders (headers: (string * string) list) =
    let d = Dictionary<string, string>()
    for k, v in headers do
        d[k] <- v
    d
