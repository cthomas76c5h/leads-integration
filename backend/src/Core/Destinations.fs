/// Every destination with its state and health, most urgent first: the
/// Destinations view (docs/lead-delivery/operations.md). Counts are over
/// production deliveries only, newest per lead, so a resend replaces what it resent.
module LeadIntegrations.Destinations

open System
open System.Collections.Generic
open LeadIntegrations.Db
open LeadIntegrations.Domain

type Week =
    { Accepted: int
      Refused: int
      NotSent: int
      Retrying: int }

/// Its newest refused production delivery, when that explains the health. A class, so none is null in JSON.
[<AllowNullLiteral>]
type Problem(deliveryId: string, at: int64, diagnosis: Diagnosis) =
    member _.DeliveryId = deliveryId
    member _.At = at
    member _.Diagnosis = diagnosis

type Row =
    { ConnectionId: string
      Name: string
      Kind: string
      FirmId: string
      FirmName: string
      FirmActive: bool
      State: string
      Health: string
      StoppedReason: string
      FailureStreak: int
      LastAcceptedAt: Nullable<int64>
      LastLeadAt: Nullable<int64>
      Week: Week
      RetryingSince: Nullable<int64>
      MissedLeads: int
      LastProblem: Problem }

type Query =
    { Q: string
      Health: string
      Kind: string
      /// "all" includes destinations of inactive firms.
      FirmActive: string
      Limit: int
      Offset: int }

/// Most urgent first.
let healthOrder =
    [ "stopped"; "failing"; "late"; "retrying"; "paused"; "setting-up"; "healthy"; "quiet"; "off" ]

let private weekMs = 7L * 24L * 3_600_000L


let private byConnection (s: Session) (sql: string) (args: obj list) (read: System.Data.Common.DbDataReader -> 'T) =
    let d = Dictionary<string, 'T>()
    for id, v in s.Query sql args (fun r -> Read.str r 0, read r) do
        d[id] <- v
    d

let private find (d: Dictionary<string, 'T>) (id: string) (fallback: 'T) =
    match d.TryGetValue id with
    | true, v -> v
    | _ -> fallback

/// One row per destination, unfiltered and unsorted.
let all (s: Session) (nowMs: int64) : Row[] =
    let firms = Store.allFirms s |> Array.map (fun f -> f.Id, f.Active) |> dict
    let week =
        byConnection
            s
            $"""SELECT d.connection_id, SUM(d.status = 'delivered'), SUM(d.status IN ('failed', 'cancelled')),
                       SUM(d.status = 'skipped'), SUM(d.status IN ('pending', 'delivering'))
                FROM deliveries d WHERE d.test = 0 AND d.created_at >= $1 AND {Store.newestPerPair}
                GROUP BY d.connection_id"""
            [ nowMs - weekMs ]
            (fun r -> { Accepted = Read.int r 1; Refused = Read.int r 2; NotSent = Read.int r 3; Retrying = Read.int r 4 })
    let lastLead =
        byConnection s "SELECT connection_id, MAX(created_at) FROM deliveries WHERE test = 0 GROUP BY connection_id" [] (fun r -> Read.int64 r 1)
    let lastAccepted =
        byConnection
            s
            """SELECT d.connection_id, MAX(a.at) FROM attempts a JOIN deliveries d ON d.id = a.delivery_id
               WHERE d.test = 0 AND a.outcome = 'delivered' GROUP BY d.connection_id"""
            []
            (fun r -> Read.int64 r 1)
    let retryingSince =
        byConnection
            s
            "SELECT connection_id, MIN(created_at) FROM deliveries WHERE test = 0 AND status IN ('pending', 'delivering') GROUP BY connection_id"
            []
            (fun r -> Read.int64 r 1)
    // The newest finished production delivery, and the newest refused one.
    let newest (statuses: string) =
        byConnection
            s
            $"""SELECT connection_id, id, status, created_at FROM (
                  SELECT d.connection_id, d.id, d.status, d.created_at,
                         ROW_NUMBER() OVER (PARTITION BY d.connection_id ORDER BY d.created_at DESC, d.id DESC) AS rn
                  FROM deliveries d WHERE d.test = 0 AND d.status IN ({statuses})) WHERE rn = 1"""
            []
            (fun r -> Read.str r 1, Read.str r 2, Read.int64 r 3)
    let finished = newest "'delivered', 'failed'"
    let refused = newest "'failed'"
    let missed =
        byConnection
            s
            """SELECT c.id, COUNT(e.id) FROM connections c
               JOIN (SELECT connection_id, MIN(created_at) AS first FROM deliveries WHERE test = 0 GROUP BY connection_id) f
                 ON f.connection_id = c.id
               JOIN events e ON e.firm_id = c.firm_id AND e.test = 0 AND e.created_at > f.first
               WHERE NOT EXISTS (SELECT 1 FROM deliveries d WHERE d.event_id = e.id AND d.connection_id = c.id)
               GROUP BY c.id"""
            []
            (fun r -> Read.int r 1)
    let none = { Accepted = 0; Refused = 0; NotSent = 0; Retrying = 0 }
    let noTime = Nullable<int64>()
    let opt (d: Dictionary<string, int64>) id =
        match d.TryGetValue id with
        | true, v -> Nullable v
        | _ -> noTime
    Store.allConnections s
    |> Array.map (fun row ->
        let c = row.Connection
        let setting key =
            match c.Settings.TryGetValue key with
            | true, v -> v
            | _ -> ""
        let lastProblem =
            match refused.TryGetValue c.Id with
            | true, (id, _, at) ->
                Store.tryDelivery s id
                |> Option.bind (fun d -> Option.ofObj d.Delivery.Diagnosis)
                |> Option.map (fun g -> Problem(id, at, g))
            | _ -> None
        let problemClass = lastProblem |> Option.map (fun p -> p.Diagnosis.Class)
        let state =
            if setting "stage" = "setup" then "setting-up"
            elif c.Status <> "active" then
                let reason = if isNull c.DisabledReason then "" else c.DisabledReason
                if reason.Contains "410" || problemClass = Some "connection" then "needs-reconnection" else "switched-off"
            // Switched off in its source system and never used here: not a pause anyone made.
            elif not c.Enabled then (if lastLead.ContainsKey c.Id || setting "simulated" = "paused" then "paused" else "off")
            else "live"
        let since = opt retryingSince c.Id
        let lastLeadAt = opt lastLead c.Id
        let health =
            match state with
            | "setting-up" -> "setting-up"
            | "needs-reconnection"
            | "switched-off" -> "stopped"
            | "paused" -> "paused"
            | "off" -> "off"
            | _ ->
                let failing =
                    match finished.TryGetValue c.Id with
                    // A lead problem is about that lead, not the destination.
                    | true, (_, "failed", _) -> problemClass <> Some "lead"
                    | _ -> false
                if since.HasValue && nowMs - since.Value > Store.lateAfterMs then "late"
                elif failing then "failing"
                elif since.HasValue then "retrying"
                elif not lastLeadAt.HasValue || nowMs - lastLeadAt.Value > weekMs then "quiet"
                else "healthy"
        { ConnectionId = c.Id
          Name = c.Name
          Kind = c.Kind
          FirmId = c.FirmId
          FirmName = c.FirmName
          FirmActive = (match firms.TryGetValue c.FirmId with | true, a -> a | _ -> false)
          State = state
          Health = health
          StoppedReason = (if state = "needs-reconnection" || state = "switched-off" then c.DisabledReason else null)
          FailureStreak = c.ConsecutiveFailures
          LastAcceptedAt = opt lastAccepted c.Id
          LastLeadAt = lastLeadAt
          Week = find week c.Id none
          RetryingSince = since
          MissedLeads = find missed c.Id 0
          // Shown when it explains the health; a healthy destination's old refusal is history.
          LastProblem = (if health = "healthy" || health = "quiet" then null else Option.toObj lastProblem) })

let private rank (r: Row) =
    let i = healthOrder |> List.findIndex ((=) r.Health)
    i, -r.MissedLeads, -(if r.LastLeadAt.HasValue then r.LastLeadAt.Value else 0L)

/// Filtered and sorted; counts cover the filter without `health`.
let list (s: Session) (q: Query) (nowMs: int64) =
    let has (text: string) =
        String.IsNullOrWhiteSpace q.Q || (not (isNull text) && text.Contains(q.Q.Trim(), StringComparison.OrdinalIgnoreCase))
    let scoped =
        all s nowMs
        |> Array.filter (fun r ->
            (q.FirmActive = "all" || r.FirmActive)
            && (String.IsNullOrEmpty q.Kind || r.Kind = q.Kind)
            && (has r.FirmName || has r.Name || has r.Kind))
    let counts = healthOrder |> List.map (fun h -> h, scoped |> Array.filter (fun r -> r.Health = h) |> Array.length) |> dict
    let missed = scoped |> Array.filter (fun r -> r.MissedLeads > 0) |> Array.length
    let matched =
        scoped
        |> Array.filter (fun r ->
            match q.Health with
            | null
            | "" -> true
            | "missed" -> r.MissedLeads > 0
            | h -> r.Health = h)
        |> Array.sortBy rank
    {| total = matched.Length
       counts = counts
       missed = missed
       items = matched |> Array.skip (min matched.Length (max 0 q.Offset)) |> Array.truncate (max 1 (min 200 q.Limit)) |}
