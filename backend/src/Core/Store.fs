/// Repositories over the SQLite schema in Db.fs, plus the read models the API
/// returns (FirmSummary, FirmDetail, Overview). Every write runs in Db.write.
module LeadIntegrations.Store

open System
open System.Collections.Generic
open System.Data.Common
open LeadIntegrations.Db
open LeadIntegrations.Domain

// ----- Firms -----

type FirmRow =
    { Id: string
      Name: string
      KeyHash: string
      KeyFingerprint: string
      Active: bool
      ContactTypes: ContactType[]
      CustomLabels: string[]
      CreatedAt: int64
      /// IANA name: the firm's office hours and the dates it reads (Zones.fs).
      TimeZone: string }

let private firmCols = "id, name, key_hash, key_fingerprint, active, contact_types, custom_labels, created_at, time_zone"

let private readFirm (r: DbDataReader) =
    { Id = Read.str r 0
      Name = Read.str r 1
      KeyHash = Read.str r 2
      KeyFingerprint = Read.str r 3
      Active = Read.bool r 4
      ContactTypes = Json.deserializeOr [||] (Read.str r 5)
      CustomLabels = Json.deserializeOr [||] (Read.str r 6)
      CreatedAt = Read.int64 r 7
      TimeZone = (match Read.str r 8 with null | "" -> Zones.company | z -> z) }

let insertFirm (s: Session) (f: FirmRow) =
    s.Execute
        $"INSERT INTO firms ({firmCols}) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9)"
        [ f.Id; f.Name; f.KeyHash; f.KeyFingerprint; f.Active; Json.serialize f.ContactTypes; Json.serialize f.CustomLabels; f.CreatedAt
          (if isNull f.TimeZone then Zones.company else f.TimeZone) ]
    |> ignore

let updateFirm (s: Session) (f: FirmRow) =
    s.Execute
        "UPDATE firms SET name=$2, key_hash=$3, key_fingerprint=$4, active=$5, contact_types=$6, custom_labels=$7, time_zone=$8 WHERE id=$1"
        [ f.Id; f.Name; f.KeyHash; f.KeyFingerprint; f.Active; Json.serialize f.ContactTypes; Json.serialize f.CustomLabels
          (if isNull f.TimeZone then Zones.company else f.TimeZone) ]
    |> ignore

let tryFirm (s: Session) (id: string) =
    s.TryOne $"SELECT {firmCols} FROM firms WHERE id=$1" [ id ] readFirm

let firmOrFail (s: Session) id =
    tryFirm s id |> Option.defaultWith (fun () -> fail 404 "no such firm")

let allFirms (s: Session) =
    s.Query $"SELECT {firmCols} FROM firms ORDER BY id" [] readFirm

/// The one firm holding this key. A key held by several firms identifies none of them.
let tryFirmByKeyHash (s: Session) (keyHash: string) =
    if isNull keyHash then None
    else
        match s.Query $"SELECT {firmCols} FROM firms WHERE key_hash=$1 LIMIT 2" [ keyHash ] readFirm with
        | [| one |] -> Some one
        | _ -> None

let tryFirmBySource (s: Session) (system: string) (sourceId: string) =
    s.TryOne
        $"SELECT {firmCols} FROM firms WHERE id = (SELECT firm_id FROM firm_sources WHERE system=$1 AND source_id=$2)"
        [ system; sourceId ]
        readFirm

let upsertFirmSource (s: Session) (firmId: string) (src: FirmSource) =
    s.Execute
        """INSERT INTO firm_sources (system, source_id, firm_id, name, active) VALUES ($1,$2,$3,$4,$5)
           ON CONFLICT(system, source_id) DO UPDATE SET firm_id=excluded.firm_id, name=excluded.name, active=excluded.active"""
        [ src.System; src.SourceId; firmId; src.Name; src.Active ]
    |> ignore

let private readSource (r: DbDataReader) =
    { System = Read.str r 0
      SourceId = Read.str r 1
      Name = Read.str r 2
      Active = Read.bool r 3 }

let sourcesOf (s: Session) (firmId: string) =
    s.Query "SELECT system, source_id, name, active FROM firm_sources WHERE firm_id=$1 ORDER BY system" [ firmId ] readSource

// ----- Connections -----

// ----- Domain events: an append-only log of what happened (docs/contracts/events.json) -----

let emit (s: Session) (name: string) (firmId: string) (eventId: string) (connectionId: string) =
    s.Execute
        "INSERT INTO domain_events (at, name, firm_id, event_id, connection_id) VALUES ($1,$2,$3,$4,$5)"
        [ now (); name; firmId; eventId; connectionId ]
    |> ignore

type DomainEvent =
    { Seq: int64
      At: int64
      Name: string
      FirmId: string
      EventId: string
      ConnectionId: string }

let domainEvents (s: Session) : DomainEvent[] =
    s.Query "SELECT seq, at, name, firm_id, event_id, connection_id FROM domain_events ORDER BY seq" [] (fun r ->
        { Seq = Read.int64 r 0
          At = Read.int64 r 1
          Name = Read.str r 2
          FirmId = Read.str r 3
          EventId = Read.str r 4
          ConnectionId = Read.str r 5 })

type ConnectionRow =
    { Connection: Connection
      SigningSecret: string
      Template: string }

let private connCols =
    "c.id, c.firm_id, f.name, c.kind, c.name, c.source, c.source_ref, c.credentials, c.settings, c.enabled, c.status, c.disabled_reason, c.consecutive_failures, c.delivery_url, c.created_at, c.signing_secret, c.template"

let private readConn (r: DbDataReader) : ConnectionRow =
    let kind = Read.str r 3
    let creds: CredentialField[] = Json.deserializeOr [||] (Read.str r 7)
    let settings: Dictionary<string, string> = Json.deserializeOr (Dictionary()) (Read.str r 8)
    let enabled = Read.bool r 9
    { Connection =
        { Id = Read.str r 0
          FirmId = Read.str r 1
          FirmName = Read.str r 2
          Kind = kind
          Name = Read.str r 4
          Source = Read.str r 5
          SourceRef = Read.str r 6
          Credentials = creds
          Settings = settings
          Enabled = enabled
          Status = Read.str r 10
          DisabledReason = Read.str r 11
          ConsecutiveFailures = Read.int r 12
          DeliveryUrl = Read.str r 13
          Issues = Issues.compute kind creds settings enabled
          CreatedAt = Read.int64 r 14 }
      SigningSecret = Read.str r 15
      Template = Read.str r 16 }

let private connFrom = "FROM connections c JOIN firms f ON f.id = c.firm_id"

let tryConnection (s: Session) (id: string) =
    s.TryOne $"SELECT {connCols} {connFrom} WHERE c.id=$1" [ id ] readConn

let connectionOrFail (s: Session) id =
    tryConnection s id |> Option.defaultWith (fun () -> fail 404 "no such connection")

let connectionsOf (s: Session) (firmId: string) =
    s.Query $"SELECT {connCols} {connFrom} WHERE c.firm_id=$1 ORDER BY c.created_at" [ firmId ] readConn

let allConnections (s: Session) =
    s.Query $"SELECT {connCols} {connFrom} ORDER BY f.name, c.created_at" [] readConn

let tryConnectionBySource (s: Session) (source: string) (sourceRef: string) =
    s.TryOne $"SELECT {connCols} {connFrom} WHERE c.source=$1 AND c.source_ref=$2" [ source; sourceRef ] readConn

let insertConnection (s: Session) (c: Connection) (signingSecret: string) (template: string) =
    s.Execute
        """INSERT INTO connections (id, firm_id, kind, name, source, source_ref, credentials, settings, enabled, status,
             disabled_reason, consecutive_failures, delivery_url, signing_secret, template, created_at)
           VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16)"""
        [ c.Id; c.FirmId; c.Kind; c.Name; c.Source; c.SourceRef; Json.serialize c.Credentials; Json.serialize c.Settings
          c.Enabled; c.Status; c.DisabledReason; c.ConsecutiveFailures; c.DeliveryUrl; signingSecret; template; c.CreatedAt ]
    |> ignore

/// Writes the mutable parts of a connection (everything but identity and secret).
let updateConnection (s: Session) (c: Connection) =
    s.Execute
        """UPDATE connections SET firm_id=$2, kind=$3, name=$4, credentials=$5, settings=$6, enabled=$7, status=$8,
             disabled_reason=$9, consecutive_failures=$10, delivery_url=$11 WHERE id=$1"""
        [ c.Id; c.FirmId; c.Kind; c.Name; Json.serialize c.Credentials; Json.serialize c.Settings; c.Enabled; c.Status
          c.DisabledReason; c.ConsecutiveFailures; c.DeliveryUrl ]
    |> ignore

let setConnectionTemplate (s: Session) (id: string) (template: string) =
    s.Execute "UPDATE connections SET template=$2 WHERE id=$1" [ id; template ] |> ignore

// ----- Events -----

/// A lead is classified at intake: whether its hand-off needs acknowledgement, and when its window
/// to act closes, are fixed then. A test lead owes no hand-off, and has no acknowledgement link.
let insertEvent (s: Session) (e: LeadEvent) =
    let m = Categories.classify e.Lead.CallType
    let category = m.Category
    s.Execute
        """INSERT INTO events (id, firm_id, test, lead, created_at, ack_token, ack_required, handoff_due_at)
           VALUES ($1,$2,$3,$4,$5,$6,$7,$8)"""
        [ e.Id; e.FirmId; e.Test; Json.serialize e.Lead; e.CreatedAt
          (if e.Test then null else Secrets.newToken ())
          (not e.Test && Urgency.needsAcknowledgement category)
          (if e.Test then null else box (e.CreatedAt + Urgency.windowMs category)) ]
    |> ignore
    s.Execute
        "INSERT INTO classifications (event_id, seq, category, practice_area, source, by, at, taxonomy_version) VALUES ($1,1,$2,$3,'intake',NULL,$4,$5)"
        [ e.Id; category; m.PracticeArea; e.CreatedAt; Categories.version () ]
    |> ignore

/// Each lead's current category where it was corrected (RFC-008 §6.2), for the firm's leads.
let corrected (s: Session) (firmId: string) : Map<string, string> =
    s.Query
        """SELECT k.event_id, k.category FROM classifications k JOIN events e ON e.id = k.event_id
           WHERE e.firm_id = $1 AND k.source <> 'intake'
             AND k.seq = (SELECT MAX(x.seq) FROM classifications x WHERE x.event_id = k.event_id)"""
        [ firmId ]
        (fun r -> Read.str r 0, Read.str r 1)
    |> Map.ofArray

/// A mapping with its category corrected: the taxonomy's CRM default for the new category.
let correctedMapping (m: Categories.CallMapping) (category: string) (practiceArea: string) : Categories.CallMapping =
    let crm = Categories.taxonomy.Value.Categories |> Array.tryFind (fun c -> c.Id = category) |> Option.map _.Crm |> Option.toObj
    { m with Category = category; PracticeArea = (if category = "new-matter" then practiceArea else null); Crm = crm }

/// The lead already recorded for a channel's conversation, if any (RFC-008 §4.1).
let eventForConversation (s: Session) (firmId: string) (conversationId: string) : string option =
    s.TryOne "SELECT id FROM events WHERE firm_id=$1 AND conversation_id=$2" [ firmId; conversationId ] (fun r -> Read.str r 0)

let setConversation (s: Session) (eventId: string) (conversationId: string) =
    s.Execute "UPDATE events SET conversation_id=$2 WHERE id=$1" [ eventId; conversationId ] |> ignore

/// The lead's acknowledgement link token, for the message to the firm only. Null for a test lead.
let ackToken (s: Session) (eventId: string) : string =
    s.TryOne "SELECT ack_token FROM events WHERE id=$1" [ eventId ] (fun r -> Read.str r 0) |> Option.toObj

/// A person at the firm confirmed receipt through the lead's link (RFC-008 §3.1). None: not a link we
/// sent. Some false: already acknowledged (the first one counts).
let acknowledge (s: Session) (eventId: string) (token: string) (via: string) (nowMs: int64) : bool option =
    let same (a: string) (b: string) =
        not (isNull a || isNull b)
        && Security.Cryptography.CryptographicOperations.FixedTimeEquals(Text.Encoding.UTF8.GetBytes a, Text.Encoding.UTF8.GetBytes b)
    match s.TryOne "SELECT ack_token, acknowledged_at, firm_id FROM events WHERE id=$1 AND test=0" [ eventId ] (fun r -> Read.str r 0, Read.int64N r 1, Read.str r 2) with
    | Some(stored, at, firmId) when same stored token ->
        if at.HasValue then Some false
        else
            s.Execute "UPDATE events SET acknowledged_at=$2, acknowledged_via=$3 WHERE id=$1" [ eventId; nowMs; via ] |> ignore
            emit s "HandOffAcknowledged" firmId eventId null
            Some true
    | _ -> None

/// The simulator's record of what happened to a hand-off (Simulate.fs), written with its history.
/// Also fixes the intake facts, for leads written before they existed.
let setHandOff (s: Session) (e: LeadEvent) (acknowledgedAt: Nullable<int64>) (escalatedAt: Nullable<int64>) (expiredAt: Nullable<int64>) =
    let n (v: Nullable<int64>) = if v.HasValue then box v.Value else null
    let category = (Categories.classify e.Lead.CallType).Category
    s.Execute
        """UPDATE events SET ack_token=COALESCE(ack_token, $5), ack_required=$6, handoff_due_at=$7,
                  acknowledged_at=$2, acknowledged_via=CASE WHEN $2 IS NULL THEN NULL ELSE 'link' END, escalated_at=$3, expired_at=$4
           WHERE id=$1"""
        [ e.Id; n acknowledgedAt; n escalatedAt; n expiredAt; Secrets.newToken (); Urgency.needsAcknowledgement category
          e.CreatedAt + Urgency.windowMs category ]
    |> ignore

let tryEvent (s: Session) (id: string) =
    s.TryOne "SELECT id, firm_id, test, lead, created_at FROM events WHERE id=$1" [ id ] (fun r ->
        { Id = Read.str r 0
          FirmId = Read.str r 1
          Test = Read.bool r 2
          Lead = Json.deserialize (Read.str r 3)
          CreatedAt = Read.int64 r 4 })

// ----- Deliveries -----

type DeliveryRow =
    { Delivery: Delivery
      /// Some template: the body under test (a draft revision); None: the connection's own.
      Template: string option }

/// Sending, or waiting to be sent again: retrying. A held delivery (pending, no next attempt) is not.
let sendingOrRetrying = "(d.status = 'delivering' OR (d.status = 'pending' AND d.next_attempt_at IS NOT NULL))"

let private stageOf status (nextAttemptAt: Nullable<int64>) (attempts: Attempt[]) =
    match status with
    // Waiting for its paused destination to resume: no next attempt (releaseHeld sets one).
    | "pending" when not nextAttemptAt.HasValue -> "held"
    | "delivered" -> "accepted"
    | "skipped" -> "not-sent"
    | "unknown" -> "unknown"
    | "cancelled" when attempts.Length = 0 -> "not-sent"
    | "failed"
    | "cancelled" -> "refused"
    | _ -> "queued"

let private readAttempt (r: DbDataReader) =
    { N = Read.int r 0
      At = Read.int64 r 1
      DurationMs = Read.int64 r 2
      Url = Read.str r 3
      Status = Read.intN r 4
      Error = Read.str r 5
      Outcome = Read.str r 6
      RequestBody = Read.str r 7
      ResponseSnippet = Read.str r 8
      NextAttemptInMs = Read.int64N r 9 }

let attemptsOf (s: Session) (deliveryId: string) =
    s.Query
        "SELECT n, at, duration_ms, url, status, error, outcome, request_body, response_snippet, next_attempt_in_ms FROM attempts WHERE delivery_id=$1 ORDER BY n"
        [ deliveryId ]
        readAttempt

let private deliveryCols =
    """d.id, d.event_id, d.connection_id, c.name, c.kind, f.id, f.name, d.test, d.status, d.reason, d.next_attempt_at, d.replay_of, d.created_at, d.template, d.has_template, d.decision, d.resend,
     d.verified_at, d.verify_overdue_at, c.settings LIKE '%"duplicates":"lookup"%'"""

let private deliveryFrom =
    "FROM deliveries d JOIN connections c ON c.id = d.connection_id JOIN firms f ON f.id = c.firm_id"

let private readDelivery (s: Session) (r: DbDataReader) : DeliveryRow =
    let id = Read.str r 0
    let status = Read.str r 8
    { Delivery =
        { Id = id
          EventId = Read.str r 1
          ConnectionId = Read.str r 2
          ConnectionName = Read.str r 3
          Kind = Read.str r 4
          FirmId = Read.str r 5
          FirmName = Read.str r 6
          Test = Read.bool r 7
          Status = status
          Stage = ""
          Reason = Read.str r 9
          Decision =
            match Read.str r 15 with
            | null -> Unchecked.defaultof<RoutingDecision>
            | json -> Json.deserialize json
          NextAttemptAt = Read.int64N r 10
          ReplayOf = Read.str r 11
          Resend =
            match Read.str r 16 with
            | null -> Unchecked.defaultof<Resend>
            | json -> Json.deserialize json
          CreatedAt = Read.int64 r 12
          Attempts = [||]
          Diagnosis = null
          Evidence = null
          VerifiedAt = Read.int64N r 17
          VerifyOverdue = not (r.IsDBNull 18) && r.IsDBNull 17
          Verifiable = Read.bool r 19 }
      Template = if Read.bool r 14 then Some(Read.str r 13) else None }

let private withAttempts (s: Session) (d: DeliveryRow) =
    let attempts = attemptsOf s d.Delivery.Id
    { d with
        Delivery =
            { d.Delivery with
                Attempts = attempts
                Stage = stageOf d.Delivery.Status d.Delivery.NextAttemptAt attempts
                Evidence =
                    if d.Delivery.VerifiedAt.HasValue then "verified"
                    elif d.Delivery.Status = "delivered" then "accepted"
                    elif attempts.Length > 0 then "submitted"
                    else "none"
                Diagnosis =
                    Diagnose.diagnose
                        d.Delivery.Kind
                        d.Delivery.Status
                        d.Delivery.Reason
                        attempts
                        (lazy (tryEvent s d.Delivery.EventId |> Option.map _.Lead)) } }

let tryDelivery (s: Session) (id: string) =
    s.TryOne $"SELECT {deliveryCols} {deliveryFrom} WHERE d.id=$1" [ id ] (readDelivery s)
    |> Option.map (withAttempts s)

let deliveryOrFail s id =
    tryDelivery s id |> Option.defaultWith (fun () -> fail 404 "no such delivery")

type DeliveryFilter =
    { Status: string
      FirmId: string
      ConnectionId: string
      Test: Nullable<bool>
      Limit: int
      Offset: int }

/// The newest delivery for each (lead, destination): a resend replaces what it resent.
let newestPerPair =
    """NOT EXISTS (SELECT 1 FROM deliveries n WHERE n.event_id = d.event_id AND n.connection_id = d.connection_id
                     AND (n.created_at > d.created_at OR (n.created_at = d.created_at AND n.id > d.id)))"""

/// A production delivery not accepted this long after its lead is late (docs/lead-delivery).
let lateAfterMs = 15L * 60_000L

let private needsAttention (d: DeliveryRow) =
    isNull d.Delivery.Diagnosis || d.Delivery.Diagnosis.Class <> "lead"

/// `Status` may also be 'attention' (the newest delivery per lead and destination
/// was refused or gave up, and it is not a lead problem) or 'late' (still
/// retrying more than 15 minutes after it was created).
let listDeliveries (s: Session) (f: DeliveryFilter) =
    let where = ResizeArray<string>()
    let args = ResizeArray<obj>()
    let add (clause: string) (v: obj) =
        args.Add v
        where.Add(clause.Replace("?", $"${args.Count}"))
    match f.Status with
    | null
    | "" -> ()
    | "attention" ->
        where.Add "d.status IN ('failed', 'unknown')"
        where.Add newestPerPair
    | "late" ->
        where.Add sendingOrRetrying
        add "d.created_at < ?" (box (now () - lateAfterMs))
    | status -> add "d.status = ?" status
    if not (String.IsNullOrEmpty f.FirmId) then add "f.id = ?" f.FirmId
    if not (String.IsNullOrEmpty f.ConnectionId) then add "c.id = ?" f.ConnectionId
    if f.Test.HasValue then add "d.test = ?" (box f.Test.Value)
    let whereSql = if where.Count = 0 then "" else "WHERE " + String.Join(" AND ", where)
    let limit = max 1 (min 500 f.Limit)
    let offset = max 0 f.Offset
    if f.Status = "attention" then
        // Lead problems are told apart only with the lead in hand, so this one pages after reading.
        s.Query $"SELECT {deliveryCols} {deliveryFrom} {whereSql} ORDER BY d.created_at DESC, d.id" (List.ofSeq args) (readDelivery s)
        |> Array.map (withAttempts s)
        |> Array.filter needsAttention
        |> fun rows -> rows |> Array.skip (min rows.Length offset) |> Array.truncate limit
    else
        args.Add(box limit)
        let limitArg = args.Count
        args.Add(box offset)
        s.Query
            $"SELECT {deliveryCols} {deliveryFrom} {whereSql} ORDER BY d.created_at DESC, d.id LIMIT ${limitArg} OFFSET ${args.Count}"
            (List.ofSeq args)
            (readDelivery s)
        |> Array.map (withAttempts s)

type DeliverySummary =
    { Since: int64
      /// Deliveries created since `Since`, newest per lead and destination.
      Accepted: int
      Refused: int
      NotSent: int
      /// Waiting on an operator, at any age.
      NeedsAttention: int
      /// Refused for what the lead lacks: not counted as needing attention.
      LeadProblems: int
      Retrying: int
      Late: int
      StoppedDestinations: int
      /// Production leads in the last 7 days whose window to act passed before any hand-off (filled by the handler).
      ExpiredLeads: int }

/// Totals for the Delivery Center tiles, over every matching delivery (not a page).
let deliverySummary (s: Session) (test: bool) (firmId: string) (since: int64) (nowMs: int64) : DeliverySummary =
    // $1 is always `test`; the firm, when given, is the last argument.
    let scoped (sql: string) (args: obj list) =
        if String.IsNullOrEmpty firmId then sql, args
        else sql + $" AND c.firm_id = ${args.Length + 1}", args @ [ box firmId ]
    let count (sql: string) (args: obj list) =
        let sql, args = scoped sql args
        s.Query sql args (fun r -> Read.int r 0) |> Array.head
    let accepted, refused, notSent =
        let sql, args =
            scoped
                $"""SELECT COALESCE(SUM(d.status = 'delivered'), 0), COALESCE(SUM(d.status IN ('failed', 'cancelled', 'unknown')), 0),
                           COALESCE(SUM(d.status = 'skipped'), 0)
                    FROM deliveries d JOIN connections c ON c.id = d.connection_id
                    WHERE d.test = $1 AND d.created_at >= $2 AND {newestPerPair}"""
                [ box test; box since ]
        s.Query sql args (fun r -> Read.int r 0, Read.int r 1, Read.int r 2) |> Array.head
    let refusedNow =
        let sql, args = scoped $"SELECT {deliveryCols} {deliveryFrom} WHERE d.test = $1 AND d.status IN ('failed', 'unknown') AND {newestPerPair}" [ box test ]
        s.Query sql args (readDelivery s) |> Array.map (withAttempts s)
    let attention = refusedNow |> Array.filter needsAttention |> Array.length
    let retrying = $"FROM deliveries d JOIN connections c ON c.id = d.connection_id WHERE d.test = $1 AND {sendingOrRetrying}"
    { Since = since
      Accepted = accepted
      Refused = refused
      NotSent = notSent
      NeedsAttention = attention
      LeadProblems = refusedNow.Length - attention
      Retrying = count $"SELECT COUNT(*) {retrying}" [ box test ]
      Late = count $"SELECT COUNT(*) {retrying} AND d.created_at < $2" [ box test; box (nowMs - lateAfterMs) ]
      StoppedDestinations = count "SELECT COUNT(*) FROM connections c WHERE c.status <> 'active'" []
      ExpiredLeads = 0 }

// ----- Leads (events with their delivery outcome) -----

type LeadCounts =
    { Total: int
      Delivered: int
      Refused: int
      Skipped: int
      /// Queued, retrying or sending: not held.
      Pending: int
      /// Waiting for a paused destination to resume.
      Held: int }

/// When a lead first reached the firm (its first accepted delivery, anywhere), against its window to act.
type HandOff =
    { WindowMs: int64
      DueAt: int64
      /// When the first delivery was accepted; null while none has been.
      At: Nullable<int64>
      /// The destination that took it first.
      By: string
      /// Taken by the firm's fallback destination.
      ViaFallback: bool
      /// on-time | expired | waiting
      State: string
      /// The strongest evidence (RFC-008 §3.1): acknowledged | accepted | submitted | none.
      Evidence: string
      /// Emergencies and custody: a person at the firm must confirm receipt (RFC-008 §3.2).
      NeedsAcknowledgement: bool
      AcknowledgedAt: Nullable<int64>
      /// No acknowledgement within the fallback delay: an operator calls the firm.
      EscalatedAt: Nullable<int64>
      Escalated: bool }

type LeadSummary =
    { Id: string
      FirmId: string
      FirmName: string
      Test: bool
      CreatedAt: int64
      Caller: string
      Phone: string
      CallType: string
      Webchat: bool
      Mapping: Categories.CallMapping
      Counts: LeadCounts
      Outcome: string
      /// Null for test leads.
      HandOff: HandOff
      /// The channel's conversation it came from; null when the channel gave none (an operator's send).
      ConversationId: string }

type LeadDetail =
    { Id: string
      FirmId: string
      FirmName: string
      Test: bool
      CreatedAt: int64
      Caller: string
      Phone: string
      CallType: string
      Webchat: bool
      Mapping: Categories.CallMapping
      Counts: LeadCounts
      Outcome: string
      HandOff: HandOff
      ConversationId: string
      Lead: Lead
      Deliveries: Delivery[] }

type LeadQuery =
    { Q: string
      FirmId: string
      Category: string
      Outcome: string
      Test: Nullable<bool>
      Limit: int
      Offset: int }

let private outcomeOf (c: LeadCounts) (handOff: HandOff) =
    // Expired comes first: the lead's window to act passed before it reached anyone.
    if not (isNull (box handOff)) && handOff.State = "expired" then "expired"
    elif c.Total = 0 then "none"
    elif c.Pending > 0 then "pending"
    elif c.Held > 0 then "held"
    elif c.Refused > 0 then "refused"
    elif c.Skipped = c.Total then "skipped"
    else "delivered"

/// Counts over the latest delivery per connection: a replay replaces what it replays.
let private leadSql =
    """SELECT e.id, e.firm_id, f.name, e.test, e.lead, e.created_at,
              COUNT(d.id),
              COALESCE(SUM(d.status = 'delivered'), 0),
              COALESCE(SUM(d.status IN ('failed', 'cancelled', 'unknown')), 0),
              COALESCE(SUM(d.status = 'skipped'), 0),
              COALESCE(SUM(d.status = 'delivering' OR (d.status = 'pending' AND d.next_attempt_at IS NOT NULL)), 0),
              (SELECT MIN(a.at) FROM attempts a JOIN deliveries x ON x.id = a.delivery_id
                WHERE x.event_id = e.id AND a.outcome = 'delivered'),
              (SELECT c2.name || CASE WHEN c2.settings LIKE '%"role":"fallback"%' THEN '|fallback' ELSE '' END
                 FROM attempts a JOIN deliveries x ON x.id = a.delivery_id JOIN connections c2 ON c2.id = x.connection_id
                WHERE x.event_id = e.id AND a.outcome = 'delivered'
                ORDER BY a.at, CASE WHEN c2.kind IN ('email', 'sms', 'slack') THEN 0 ELSE 1 END LIMIT 1),
              -- Not sent because the rule excludes it; a lead problem still owes the firm a hand-off.
              COALESCE(SUM(d.status = 'skipped' AND COALESCE(d.reason, '') NOT LIKE 'lead problem:%'), 0),
              COALESCE(SUM(d.status = 'pending' AND d.next_attempt_at IS NULL), 0),
              e.ack_required, e.acknowledged_at, e.escalated_at,
              EXISTS (SELECT 1 FROM attempts a JOIN deliveries x ON x.id = a.delivery_id WHERE x.event_id = e.id),
              e.conversation_id,
              (SELECT k.category FROM classifications k WHERE k.event_id = e.id AND k.source <> 'intake' ORDER BY k.seq DESC LIMIT 1),
              (SELECT k.practice_area FROM classifications k WHERE k.event_id = e.id AND k.source <> 'intake' ORDER BY k.seq DESC LIMIT 1)
       FROM events e
       JOIN firms f ON f.id = e.firm_id
       LEFT JOIN deliveries d ON d.event_id = e.id
            AND NOT EXISTS (SELECT 1 FROM deliveries n
                            WHERE n.event_id = d.event_id AND n.connection_id = d.connection_id
                              AND (n.created_at > d.created_at OR (n.created_at = d.created_at AND n.id > d.id)))"""

let private readLead (r: DbDataReader) : LeadSummary * Lead =
    let lead: Lead = Json.deserialize (Read.str r 4)
    let counts =
        { Total = Read.int r 6
          Delivered = Read.int r 7
          Refused = Read.int r 8
          Skipped = Read.int r 9
          Pending = Read.int r 10
          Held = Read.int r 14 }
    let createdAt = Read.int64 r 5
    // The window to act stands as judged at intake; everything else reads the current classification.
    let atIntake = Categories.classify lead.CallType
    let mapping =
        match Read.str r 20 with
        | null -> atIntake
        | category -> correctedMapping atIntake category (Read.str r 21)
    // No hand-off is owed for a test lead, nor for one the firm's routing sends nowhere on purpose
    // (every destination excluded it): it cannot be late or missed, and is not in the in-time numbers.
    let excludedByRouting = counts.Total > 0 && Read.int r 13 = counts.Total
    let handOff =
        if Read.bool r 3 || excludedByRouting then Unchecked.defaultof<HandOff>
        else
            let window = Urgency.windowMs atIntake.Category
            let at = Read.int64N r 11
            let by = Read.str r 12
            let viaFallback = not (isNull by) && by.EndsWith "|fallback"
            { WindowMs = window
              DueAt = createdAt + window
              At = at
              By = (if viaFallback then by.Substring(0, by.Length - "|fallback".Length) else by)
              ViaFallback = viaFallback
              State = Urgency.handOffState createdAt window (if at.HasValue then Some at.Value else None) (now ())
              Evidence = Urgency.evidence (not (r.IsDBNull 16)) at.HasValue (Read.bool r 18)
              NeedsAcknowledgement = Read.bool r 15
              AcknowledgedAt = Read.int64N r 16
              EscalatedAt = Read.int64N r 17
              Escalated = not (r.IsDBNull 17) }
    { Id = Read.str r 0
      FirmId = Read.str r 1
      FirmName = Read.str r 2
      Test = Read.bool r 3
      CreatedAt = createdAt
      Caller = $"{lead.FirstName} {lead.LastName}".Trim()
      Phone = lead.Phone
      CallType = lead.CallType
      Webchat = lead.Webchat
      Mapping = mapping
      Counts = counts
      Outcome = outcomeOf counts handOff
      HandOff = handOff
      ConversationId = Read.str r 19 },
    lead

/// Firm and test filter in SQL; search, category and outcome in memory (the
/// mapping is computed, not stored). Fine for lab volumes, tens of thousands of leads.
let listLeads (s: Session) (q: LeadQuery) : int * LeadSummary[] =
    let where = ResizeArray<string>()
    let args = ResizeArray<obj>()
    let add (clause: string) (v: obj) =
        args.Add v
        where.Add(clause.Replace("?", $"${args.Count}"))
    if not (String.IsNullOrEmpty q.FirmId) then add "e.firm_id = ?" q.FirmId
    if q.Test.HasValue then add "e.test = ?" (box q.Test.Value)
    let whereSql = if where.Count = 0 then "" else "WHERE " + String.Join(" AND ", where)
    let text = if String.IsNullOrWhiteSpace q.Q then null else q.Q.Trim()
    let has (v: string) = not (isNull v) && v.Contains(text, StringComparison.OrdinalIgnoreCase)
    let rows =
        s.Query $"{leadSql} {whereSql} GROUP BY e.id ORDER BY e.created_at DESC, e.id DESC" (List.ofSeq args) readLead
        |> Array.map fst
        |> Array.filter (fun l ->
            (isNull text || has l.Caller || has l.Phone || has l.CallType || has l.FirmName)
            && (String.IsNullOrEmpty q.Category || l.Mapping.Category = q.Category)
            && (String.IsNullOrEmpty q.Outcome || l.Outcome = q.Outcome))
    rows.Length, rows |> Array.skip (min rows.Length (max 0 q.Offset)) |> Array.truncate (max 1 (min 500 q.Limit))

let leadOrFail (s: Session) (id: string) : LeadDetail =
    match s.Query $"{leadSql} WHERE e.id = $1 GROUP BY e.id" [ id ] readLead |> Array.tryHead with
    | None -> fail 404 "no such lead"
    | Some(l, lead) ->
        let deliveries =
            s.Query $"SELECT {deliveryCols} {deliveryFrom} WHERE d.event_id = $1 ORDER BY d.created_at DESC, d.id DESC" [ id ] (readDelivery s)
            |> Array.map (fun d -> (withAttempts s d).Delivery)
        { Id = l.Id
          FirmId = l.FirmId
          FirmName = l.FirmName
          Test = l.Test
          CreatedAt = l.CreatedAt
          Caller = l.Caller
          Phone = l.Phone
          CallType = l.CallType
          Webchat = l.Webchat
          Mapping = l.Mapping
          Counts = l.Counts
          Outcome = l.Outcome
          HandOff = l.HandOff
          ConversationId = l.ConversationId
          Lead = lead
          Deliveries = deliveries }

let insertDelivery (s: Session) (d: Delivery) (template: string option) =
    s.Execute
        """INSERT INTO deliveries (id, event_id, connection_id, test, status, reason, template, has_template, next_attempt_at, replay_of, created_at, decision, resend)
           VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13)"""
        [ d.Id; d.EventId; d.ConnectionId; d.Test; d.Status; d.Reason; Option.toObj template; template.IsSome
          (if d.NextAttemptAt.HasValue then box d.NextAttemptAt.Value else null); d.ReplayOf; d.CreatedAt
          (if isNull (box d.Decision) then null else Json.serialize d.Decision)
          (if isNull (box d.Resend) then null else Json.serialize d.Resend) ]
    |> ignore

/// Missed leads: the firm's production leads since this destination's first
/// production delivery that have no delivery to it (recorded while it was
/// paused or stopped). Oldest first.
let missedEvents (s: Session) (connectionId: string) : LeadEvent[] =
    s.Query
        """SELECT e.id FROM events e
           JOIN connections c ON c.id = $1 AND e.firm_id = c.firm_id
           WHERE e.test = 0
             AND e.created_at > (SELECT MIN(created_at) FROM deliveries WHERE connection_id = $1 AND test = 0)
             AND NOT EXISTS (SELECT 1 FROM deliveries d WHERE d.event_id = e.id AND d.connection_id = $1)
           ORDER BY e.created_at, e.id"""
        [ connectionId ]
        (fun r -> Read.str r 0)
    |> Array.choose (tryEvent s)

/// A worker's lease on one delivery (RFC-008 §5.3). `Recovered`: the previous lease had run out.
type Claim =
    { DeliveryId: string
      Version: int64
      Recovered: bool }

/// Claimable: waiting and due, or leased with the lease run out (or none recorded: an older claim).
let private claimable =
    "((status = 'pending' AND next_attempt_at IS NOT NULL AND next_attempt_at <= $2) OR (status = 'delivering' AND COALESCE(lease_until, 0) <= $2))"

/// Claim one delivery for `worker` if it is claimable: one conditional write, so of two workers
/// only one can win, and the lease version every later write checks goes up by one.
let tryClaim (s: Session) (worker: string) (nowMs: int64) (leaseMs: int64) (id: string) : Claim option =
    match s.TryOne "SELECT status FROM deliveries WHERE id=$1" [ id ] (fun r -> Read.str r 0) with
    | None -> None
    | Some status ->
        let n =
            s.Execute
                $"""UPDATE deliveries SET status='delivering', lease_owner=$3, lease_until=$4, lease_version=lease_version+1
                    WHERE id=$1 AND {claimable}"""
                [ id; nowMs; worker; nowMs + leaseMs ]
        if n <> 1 then None
        else
            let version = s.TryOne "SELECT lease_version FROM deliveries WHERE id=$1" [ id ] (fun r -> Read.int64 r 0) |> Option.get
            Some { DeliveryId = id; Version = version; Recovered = (status = "delivering") }

/// Claim what is due for `worker`, the nearest hand-off deadline first, so emergency and custody
/// leads are not queued behind routine records (RFC-008 §5.3).
let claimDue (s: Session) (worker: string) (nowMs: int64) (leaseMs: int64) (limit: int) : Claim[] =
    s.Query
        $"""SELECT d.id, e.created_at, e.lead FROM deliveries d JOIN events e ON e.id = d.event_id
            WHERE $1 = $1 AND {claimable.Replace("status", "d.status").Replace("next_attempt_at", "d.next_attempt_at").Replace("lease_until", "d.lease_until")}
            ORDER BY COALESCE(d.next_attempt_at, d.lease_until, 0), d.id LIMIT $3"""
        [ box 0; box nowMs; box (limit * 4) ]
        (fun r ->
            let lead: Lead = Json.deserialize (Read.str r 2)
            Read.str r 0, Read.int64 r 1 + Urgency.windowMs (Categories.classify lead.CallType).Category)
    |> Array.mapi (fun i (id, deadline) -> (deadline, i), id)
    |> Array.sortBy fst
    |> Array.truncate limit
    |> Array.choose (fun (_, id) -> tryClaim s worker nowMs leaseMs id)

/// Does this lease version still hold the delivery? Every start and finish of an attempt checks it.
let holdsLease (s: Session) (id: string) (version: int64) =
    s.TryOne "SELECT 1 FROM deliveries WHERE id=$1 AND status='delivering' AND lease_version=$2" [ id; version ] (fun _ -> true)
    |> Option.defaultValue false

/// Test control: a lease runs out now.
let expireLease (s: Session) (id: string) (nowMs: int64) =
    s.Execute "UPDATE deliveries SET lease_until=$2 WHERE id=$1 AND status='delivering'" [ id; nowMs ] |> ignore

/// An attempt starts: recorded before the request is sent (RFC-008 §5.3).
let startAttempt (s: Session) (deliveryId: string) (n: int) (at: int64) (url: string) (body: string) =
    s.Execute
        """INSERT INTO attempts (delivery_id, n, at, duration_ms, url, status, error, outcome, request_body, response_snippet, next_attempt_in_ms)
           VALUES ($1,$2,$3,0,$4,NULL,NULL,'started',$5,'',NULL)"""
        [ deliveryId; n; at; url; body ]
    |> ignore

/// The attempt's answer, recorded on the attempt that started it.
let finishAttempt (s: Session) (deliveryId: string) (a: Attempt) =
    s.Execute
        """UPDATE attempts SET duration_ms=$3, status=$4, error=$5, outcome=$6, response_snippet=$7, next_attempt_in_ms=$8
           WHERE delivery_id=$1 AND n=$2"""
        [ deliveryId; a.N; a.DurationMs; (if a.Status.HasValue then box a.Status.Value else null); a.Error; a.Outcome
          a.ResponseSnippet; (if a.NextAttemptInMs.HasValue then box a.NextAttemptInMs.Value else null) ]
    |> ignore

/// The attempt that started and has no answer recorded, if any.
let openAttempt (s: Session) (deliveryId: string) : int option =
    s.TryOne "SELECT n FROM attempts WHERE delivery_id=$1 AND outcome='started' ORDER BY n DESC LIMIT 1" [ deliveryId ] (fun r -> Read.int r 0)

/// The attempt was sent and its answer will never be recorded: unknown (RFC-008 §4.2).
let markAttemptUnknown (s: Session) (deliveryId: string) (n: int) =
    s.Execute
        "UPDATE attempts SET outcome='unknown: the worker lost its lease before recording an answer' WHERE delivery_id=$1 AND n=$2"
        [ deliveryId; n ]
    |> ignore

/// When the next waiting delivery falls due, or a lease runs out, if any: where test control's advance stops next.
let nextDueAt (s: Session) : int64 option =
    s.Query
        """SELECT MIN(t) FROM (
             SELECT next_attempt_at AS t FROM deliveries WHERE status='pending' AND next_attempt_at IS NOT NULL
             UNION ALL SELECT lease_until FROM deliveries WHERE status='delivering' AND lease_until IS NOT NULL)"""
        []
        (fun r -> Read.int64N r 0)
    |> Array.tryHead
    |> Option.bind (fun v -> if v.HasValue then Some v.Value else None)

/// A waiting delivery decided again under a newer revision (Dispatch.approveRouting, applyToWaiting).
let setDecision (s: Session) (id: string) (decision: RoutingDecision) =
    s.Execute "UPDATE deliveries SET decision=$2 WHERE id=$1" [ id; Json.serialize decision ] |> ignore

let setDeliveryState (s: Session) (id: string) (status: string) (reason: string) (nextAttemptAt: Nullable<int64>) =
    s.Execute
        "UPDATE deliveries SET status=$2, reason=$3, next_attempt_at=$4, lease_owner=NULL, lease_until=NULL WHERE id=$1"
        [ id; status; reason; (if nextAttemptAt.HasValue then box nextAttemptAt.Value else null) ]
    |> ignore

let insertAttempt (s: Session) (deliveryId: string) (a: Attempt) =
    s.Execute
        """INSERT INTO attempts (delivery_id, n, at, duration_ms, url, status, error, outcome, request_body, response_snippet, next_attempt_in_ms)
           VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)"""
        [ deliveryId; a.N; a.At; a.DurationMs; a.Url; (if a.Status.HasValue then box a.Status.Value else null); a.Error; a.Outcome
          a.RequestBody; a.ResponseSnippet; (if a.NextAttemptInMs.HasValue then box a.NextAttemptInMs.Value else null) ]
    |> ignore

let cancelPendingFor (s: Session) (connectionId: string) (reason: string) =
    s.Execute
        "UPDATE deliveries SET status='cancelled', reason=$2, next_attempt_at=NULL WHERE connection_id=$1 AND status='pending'"
        [ connectionId; reason ]
    |> ignore

/// Why a waiting delivery is not being sent: its destination is paused. A stand-in for
/// the model's Held state (docs/lead-delivery/README.md §4): pending, with no next attempt.
let heldReason = "held: destination paused"

/// Why a waiting delivery is held because its destination needs reconnection (401, 403, 410).
let reconnectReason = Diagnose.reconnectPrefix

/// The destination needs reconnection: its other waiting production deliveries are held, not
/// cancelled, until the firm reconnects (releaseHeld). Test deliveries are left to be cancelled.
let holdPendingFor (s: Session) (connectionId: string) =
    let held =
        s.Query
            "SELECT d.event_id, c.firm_id FROM deliveries d JOIN connections c ON c.id = d.connection_id WHERE d.connection_id=$1 AND d.status='pending' AND d.test=0"
            [ connectionId ]
            (fun r -> Read.str r 0, Read.str r 1)
    for eventId, firmId in held do
        emit s "Held" firmId eventId connectionId
    s.Execute
        "UPDATE deliveries SET next_attempt_at=NULL, reason=$2 WHERE connection_id=$1 AND status='pending' AND test=0"
        [ connectionId; reconnectReason ]
    |> ignore

/// A paused destination resumed: its held deliveries (pending, no next attempt) fall due now.
let releaseHeld (s: Session) (connectionId: string) (nowMs: int64) : int =
    s.Execute
        "UPDATE deliveries SET next_attempt_at=$2, reason=NULL WHERE connection_id=$1 AND status='pending' AND next_attempt_at IS NULL"
        [ connectionId; nowMs ]


// ----- Received (fake receivers) -----

/// A fake CRM keeps a record it accepted, under our lead reference (Receivers.fs).
let keepRecord (s: Session) (kind: string) (dest: string) (reference: string) (at: int64) =
    s.Execute "INSERT INTO receiver_records (kind, dest, ref, at) VALUES ($1,$2,$3,$4)" [ kind; (if isNull dest then "" else dest); reference; at ] |> ignore

/// Whether a fake CRM has a record with our lead reference: its lookup.
let hasRecord (s: Session) (kind: string) (dest: string) (reference: string) =
    s.TryOne "SELECT 1 FROM receiver_records WHERE kind=$1 AND dest=$2 AND ref=$3" [ kind; (if isNull dest then "" else dest); reference ] (fun _ -> true)
    |> Option.isSome

let insertReceived (s: Session) (r: ReceivedRequest) =
    s.Execute
        "INSERT INTO received (at, path, kind, mode, answered, verdict, body) VALUES ($1,$2,$3,$4,$5,$6,$7)"
        [ r.At; r.Path; r.Kind; r.Mode; r.Answered; r.Verdict; r.Body ]
    |> ignore
    // Keep the newest 500.
    s.Execute "DELETE FROM received WHERE id <= (SELECT MAX(id) - 500 FROM received)" [] |> ignore

let listReceived (s: Session) (limit: int) =
    s.Query
        "SELECT at, path, kind, mode, answered, verdict, body FROM received ORDER BY id DESC LIMIT $1"
        [ max 1 (min 500 limit) ]
        (fun r ->
            { At = Read.int64 r 0
              Path = Read.str r 1
              Kind = Read.str r 2
              Mode = Read.str r 3
              Answered = Read.int r 4
              Verdict = Read.str r 5
              Body = Read.str r 6 })

// ----- Setups -----

let private setupCols =
    "id, firm_id, token, kind, recipe, rows, version, last_version, sent_version, connection_id, test_delivery_id, client_confirmed_at, active, created_at"

let private readSetup (s: Session) (r: DbDataReader) : Setup =
    let id = Read.str r 0
    { Id = id
      FirmId = Read.str r 1
      Token = Read.str r 2
      Kind = Read.str r 3
      Recipe = Read.str r 4
      Rows = Json.deserializeOr [||] (Read.str r 5)
      Version = Read.int r 6
      LastVersion = Read.int r 7
      SentVersion = Read.intN r 8
      ConnectionId = Read.str r 9
      TestDeliveryId = Read.str r 10
      ClientConfirmedAt = Read.int64N r 11
      Active = Json.deserializeOr Unchecked.defaultof<ActiveRevision> (Read.str r 12)
      Approvals = [||]
      History = [||]
      CreatedAt = Read.int64 r 13 }

let private withLog (s: Session) (setup: Setup) =
    { setup with
        Approvals =
            s.Query
                "SELECT version, decision, by_name, comment, at FROM approvals WHERE setup_id=$1 ORDER BY at DESC, rowid DESC"
                [ setup.Id ]
                (fun r ->
                    { Version = Read.int r 0
                      Decision = Read.str r 1
                      By = Read.str r 2
                      Comment = Read.str r 3
                      At = Read.int64 r 4 })
        History =
            s.Query
                "SELECT at, who, what FROM setup_events WHERE setup_id=$1 ORDER BY at DESC, rowid DESC"
                [ setup.Id ]
                (fun r -> { At = Read.int64 r 0; Who = Read.str r 1; What = Read.str r 2 }) }

let trySetup (s: Session) (id: string) =
    s.TryOne $"SELECT {setupCols} FROM setups WHERE id=$1" [ id ] (readSetup s) |> Option.map (withLog s)

let trySetupByToken (s: Session) (token: string) =
    s.TryOne $"SELECT {setupCols} FROM setups WHERE token=$1" [ token ] (readSetup s) |> Option.map (withLog s)

let listSetups (s: Session) =
    s.Query $"SELECT {setupCols} FROM setups ORDER BY created_at" [] (readSetup s) |> Array.map (withLog s)

/// Saves the setup's own columns. Approvals and history are appended separately.
let saveSetup (s: Session) (x: Setup) =
    s.Execute
        $"""INSERT INTO setups ({setupCols}) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14)
           ON CONFLICT(id) DO UPDATE SET kind=excluded.kind, recipe=excluded.recipe, rows=excluded.rows, version=excluded.version,
             last_version=excluded.last_version, sent_version=excluded.sent_version, connection_id=excluded.connection_id,
             test_delivery_id=excluded.test_delivery_id, client_confirmed_at=excluded.client_confirmed_at, active=excluded.active"""
        [ x.Id; x.FirmId; x.Token; x.Kind; x.Recipe; Json.serialize x.Rows; x.Version; x.LastVersion
          (if x.SentVersion.HasValue then box x.SentVersion.Value else null); x.ConnectionId; x.TestDeliveryId
          (if x.ClientConfirmedAt.HasValue then box x.ClientConfirmedAt.Value else null)
          (if isNull (box x.Active) then null else Json.serialize x.Active); x.CreatedAt ]
    |> ignore

let appendApproval (s: Session) (setupId: string) (a: Approval) =
    s.Execute "INSERT INTO approvals (setup_id, version, decision, by_name, comment, at) VALUES ($1,$2,$3,$4,$5,$6)" [ setupId; a.Version; a.Decision; a.By; a.Comment; a.At ]
    |> ignore

let appendEvent (s: Session) (setupId: string) (who: string) (what: string) =
    s.Execute "INSERT INTO setup_events (setup_id, at, who, what) VALUES ($1,$2,$3,$4)" [ setupId; now (); who; what ] |> ignore

// ----- Import runs -----

let insertImportRun (s: Session) (r: ImportRun) =
    s.Execute
        "INSERT INTO import_runs (id, system, at, firms, connections, skipped, notes) VALUES ($1,$2,$3,$4,$5,$6,$7)"
        [ r.Id; r.System; r.At; r.Firms; r.Connections; r.Skipped; Json.serialize r.Notes ]
    |> ignore

let listImportRuns (s: Session) =
    s.Query "SELECT id, system, at, firms, connections, skipped, notes FROM import_runs ORDER BY at DESC" [] (fun r ->
        { Id = Read.str r 0
          System = Read.str r 1
          At = Read.int64 r 2
          Firms = Read.int r 3
          Connections = Read.int r 4
          Skipped = Read.int r 5
          Notes = Json.deserializeOr [||] (Read.str r 6) })

// ----- Read models -----

type FirmSummary =
    { Id: string
      Name: string
      Sources: string[]
      KeyFingerprint: string
      ConnectionKinds: string[]
      IssueCount: int
      /// Enabled, active destinations: the ones that receive leads.
      LiveDestinations: int
      Active: bool }

type ContactTypeView =
    { Name: string
      Active: bool
      Mapping: Categories.CallMapping }

type FirmDetail =
    { Id: string
      Name: string
      Sources: string[]
      KeyFingerprint: string
      ConnectionKinds: string[]
      IssueCount: int
      LiveDestinations: int
      Active: bool
      /// IANA name (Zones.fs).
      TimeZone: string
      SourcesDetail: FirmSource[]
      ContactTypes: ContactTypeView[]
      CustomLabels: string[]
      Connections: Connection[]
      Setups: obj[] }

type FirmQuery =
    { Q: string
      Kind: string
      Source: string
      IssuesOnly: bool
      Limit: int
      Offset: int }

let private countIssues (conns: Connection seq) =
    conns |> Seq.sumBy (fun c -> c.Issues |> Array.filter (fun i -> i.Severity <> "info") |> Array.length)

/// The firm list, filtered in memory: a few thousand firms is small.
let listFirms (s: Session) (q: FirmQuery) : int * FirmSummary[] =
    let firms = s.Query $"SELECT {firmCols} FROM firms ORDER BY name COLLATE NOCASE" [] readFirm
    let sources =
        s.Query "SELECT firm_id, system FROM firm_sources" [] (fun r -> Read.str r 0, Read.str r 1)
        |> Array.groupBy fst
        |> Array.map (fun (k, v) -> k, v |> Array.map snd |> Array.distinct |> Array.sort)
        |> dict
    let conns = allConnections s |> Array.map _.Connection |> Array.groupBy _.FirmId |> dict
    let summaries =
        firms
        |> Array.map (fun f ->
            let cs = match conns.TryGetValue f.Id with | true, v -> v | _ -> [||]
            { Id = f.Id
              Name = f.Name
              Sources = (match sources.TryGetValue f.Id with | true, v -> v | _ -> [||])
              KeyFingerprint = f.KeyFingerprint
              ConnectionKinds = cs |> Array.map _.Kind |> Array.distinct
              IssueCount = countIssues cs
              LiveDestinations = cs |> Array.filter (fun c -> c.Enabled && c.Status = "active") |> Array.length
              Active = f.Active })
        |> Array.filter (fun f ->
            (String.IsNullOrWhiteSpace q.Q || f.Name.Contains(q.Q.Trim(), StringComparison.OrdinalIgnoreCase))
            && (String.IsNullOrEmpty q.Kind || Array.contains q.Kind f.ConnectionKinds)
            && (String.IsNullOrEmpty q.Source || Array.contains q.Source f.Sources)
            && (not q.IssuesOnly || f.IssueCount > 0))
    // With a search, best matches first: active firms with live destinations, then names that start with it.
    let summaries =
        if String.IsNullOrWhiteSpace q.Q then summaries
        else
            let t = q.Q.Trim()
            summaries
            |> Array.sortBy (fun f ->
                (if f.Active && f.LiveDestinations > 0 then 0 elif f.Active then 1 else 2),
                (if f.Name.StartsWith(t, StringComparison.OrdinalIgnoreCase) then 0 else 1),
                f.Name.ToLowerInvariant())
    summaries.Length, summaries |> Array.skip (min summaries.Length (max 0 q.Offset)) |> Array.truncate (max 1 (min 500 q.Limit))

let firmDetail (s: Session) (id: string) (setups: obj[]) : FirmDetail =
    let f = firmOrFail s id
    let srcs = sourcesOf s id
    let conns = connectionsOf s id |> Array.map _.Connection
    { Id = f.Id
      Name = f.Name
      Sources = srcs |> Array.map _.System |> Array.distinct
      KeyFingerprint = f.KeyFingerprint
      ConnectionKinds = conns |> Array.map _.Kind |> Array.distinct
      IssueCount = countIssues conns
      LiveDestinations = conns |> Array.filter (fun c -> c.Enabled && c.Status = "active") |> Array.length
      Active = f.Active
      TimeZone = f.TimeZone
      SourcesDetail = srcs
      ContactTypes = f.ContactTypes |> Array.map (fun t -> { Name = t.Name; Active = t.Active; Mapping = Categories.classify t.Name })
      CustomLabels = f.CustomLabels
      Connections = conns
      Setups = setups }

type KindCount =
    { Kind: string
      Label: string
      Category: string
      Total: int
      Enabled: int
      WithIssues: int
      BySource: Dictionary<string, int> }

type IssueCount =
    { Code: string
      Severity: string
      Message: string
      Count: int }

type Overview =
    { Firms: int
      FirmsBySource: Dictionary<string, int>
      FirmsInSeveralSources: int
      Kinds: KindCount[]
      Issues: IssueCount[]
      Imports: ImportRun[] }

let overview (s: Session) : Overview =
    let firms = int (s.Scalar "SELECT COUNT(*) FROM firms" [])
    let bySource = Dictionary<string, int>()
    for sys in Source.all do
        bySource[sys] <- int (s.Scalar "SELECT COUNT(DISTINCT firm_id) FROM firm_sources WHERE system=$1" [ sys ])
    let several =
        int (s.Scalar "SELECT COUNT(*) FROM (SELECT firm_id FROM firm_sources GROUP BY firm_id HAVING COUNT(DISTINCT system) > 1)" [])
    let conns = allConnections s |> Array.map _.Connection
    let kinds =
        Catalog.kinds
        |> Array.map (fun k ->
            let cs = conns |> Array.filter (fun c -> c.Kind = k.Kind)
            let by = Dictionary<string, int>()
            for g in cs |> Array.groupBy _.Source do
                by[fst g] <- (snd g).Length
            { Kind = k.Kind
              Label = k.Label
              Category = k.Category
              Total = cs.Length
              Enabled = cs |> Array.filter _.Enabled |> Array.length
              WithIssues = cs |> Array.filter (fun c -> c.Issues |> Array.exists (fun i -> i.Severity <> "info")) |> Array.length
              BySource = by })
    let issues =
        conns
        |> Array.collect _.Issues
        |> Array.groupBy _.Code
        |> Array.map (fun (code, xs) ->
            { Code = code
              Severity = xs[0].Severity
              Message = xs[0].Message
              Count = xs.Length })
        |> Array.sortBy (fun i -> (match i.Severity with "bad" -> 0 | "warn" -> 1 | _ -> 2), -i.Count)
    { Firms = firms
      FirmsBySource = bySource
      FirmsInSeveralSources = several
      Kinds = kinds
      Issues = issues
      Imports = listImportRuns s }
