/// A week of delivery history for the lab, so every state the Delivery Center,
/// the delivery graph and the Leads page can show has real rows: a platform
/// outage that retries recover, mail-relay timeouts, a dead endpoint that gets
/// switched off, a hook that is gone, expired authorization, a redirect fixed
/// and replayed, a paused destination, routing skips, tests, and a few
/// deliveries still retrying now (the API's worker finishes those).
///
/// It rewrites delivery history only. Existing leads keep their content and are
/// spread over the window; the firms with CRM destinations are topped up with
/// synthetic leads (Seed.lead) so the CRM paths have traffic. Nothing is sent:
/// each attempt is decided by the real rules (Routing.decide,
/// Payloads, FakeCrm, Policy, Dispatch.transition), at real time (no TIME_SCALE).
module LeadIntegrations.Simulate

open System
open System.Collections.Generic
open LeadIntegrations.Domain

type Options =
    { Days: int
      RandomSeed: int
      /// Each firm with an enabled CRM destination is topped up to this many leads.
      CrmLeads: int
      Origin: string
      Now: int64 }

let defaults =
    { Days = 7
      RandomSeed = 7
      CrmLeads = 40
      Origin = "http://localhost:5080"
      Now = 0L }

/// How a receiver answers when it is not its normal self.
type Answer =
    | Http of status: int * verdict: string * retryAfter: string
    | NoAnswer of error: string * waitedMs: int64

type Scenario =
    { Name: string
      Story: string
      ConnectionIds: string[]
      /// The answer at time t, or None for the receiver's normal behaviour.
      Answer: int64 -> Answer option
      /// Receiver mode left on the URL, so a Replay from the UI behaves the same; "" for none.
      Mode: string }

type ScenarioReport =
    { Name: string
      Story: string
      Where: string }

type Summary =
    { Leads: int
      AddedLeads: int
      Tests: int
      Deliveries: int
      ByStatus: (string * int) list
      Attempts: int
      Replays: int
      Disabled: (string * string) list
      Scenarios: ScenarioReport list
      From: int64
      To: int64 }

type private Sim =
    { mutable D: Delivery
      Attempts: ResizeArray<Attempt>
      Template: string option }

type private Action =
    /// The lead, and the one connection when it is a test.
    | Publish of LeadEvent * string option
    | Send of Sim
    | Pause of string
    | Reactivate of string
    /// A third of the lead's window to act has passed: no hand-off yet means the firm's fallback.
    | HandOffCheck of LeadEvent
    /// Replay up to n failed deliveries that match.
    | Replay of (Sim -> bool) * int

let private minute = 60_000L
let private hour = 60L * minute
let private day = 24L * hour
/// Firms' hours are US Eastern (standard time, November).
let private eastern = -5L * hour

let private setting (c: Connection) key =
    match c.Settings.TryGetValue key with
    | true, v -> v
    | _ -> ""

/// Leads the simulator adds; a rerun removes them first, so runs are repeatable.
let private simPrefix = "evt_sim"

let private receiverBase (origin: string) (url: string) =
    if not (isNull url) && url.StartsWith(origin + "/receiver/") then Some(url.Split('?')[0]) else None

/// Run the simulation and replace the delivery history with it.
let run (db: Db.Database) (opts: Options) : Summary =
    let nowMs = if opts.Now > 0L then opts.Now else now ()
    let rnd = Random opts.RandomSeed
    // Midnight Eastern today, as UTC ms.
    let today = ((nowMs + eastern) / day) * day - eastern
    let isWeekend (midnight: int64) =
        let dow = DateTimeOffset.FromUnixTimeMilliseconds(midnight + eastern + 12L * hour).DayOfWeek
        dow = DayOfWeek.Saturday || dow = DayOfWeek.Sunday
    // `at n h m`: h:m Eastern, n business days ago, so scenarios land on days with traffic.
    let at (businessDaysAgo: int) (h: int) (m: int) =
        let mutable d = today
        let mutable left = businessDaysAgo
        while left > 0 do
            d <- d - day
            if not (isWeekend d) then left <- left - 1
        d + int64 h * hour + int64 m * minute
    let windowStart = nowMs - int64 opts.Days * day

    Db.write db (fun s ->
        // ----- Baseline: undo an earlier run and anything the worker switched off -----
        let gapExisting =
            Store.allConnections s |> Array.tryFind (fun r -> setting r.Connection "simulated" = "mapping-gap") |> Option.map _.Connection.Id
        // The fallback destination this simulation adds is recreated each run (it has no setup history to keep).
        let fallbackExisting =
            Store.allConnections s |> Array.filter (fun r -> setting r.Connection "simulated" = "fallback") |> Array.map _.Connection.Id
        for row in Store.allConnections s do
            let c = row.Connection
            let wasPaused = setting c "simulated" = "paused"
            let settings = Dictionary<string, string>(c.Settings)
            settings.Remove "simulated" |> ignore
            if wasPaused then settings.Remove Dispatch.pausedAtKey |> ignore
            let url = receiverBase opts.Origin c.DeliveryUrl |> Option.defaultValue c.DeliveryUrl
            if c.Status <> "active" || c.ConsecutiveFailures <> 0 || wasPaused || url <> c.DeliveryUrl || c.Settings.ContainsKey "simulated" then
                Store.updateConnection
                    s
                    { c with
                        Status = "active"
                        DisabledReason = null
                        ConsecutiveFailures = 0
                        Enabled = c.Enabled || wasPaused
                        Settings = settings
                        DeliveryUrl = url }
        for sql in [ "DELETE FROM attempts"; "DELETE FROM deliveries"; "DELETE FROM received"; "DELETE FROM events WHERE test=1"; $"DELETE FROM events WHERE id LIKE '{simPrefix}%%'" ] do
            s.Execute sql [] |> ignore
        for id in fallbackExisting do
            s.Execute "DELETE FROM connections WHERE id=$1" [ id ] |> ignore

        // ----- A webhook whose live mapping lacks a field its receiver requires -----
        // Approved and live 2 business days ago without Phone; the receiver needs Phone,
        // so every lead since is refused: "Edit mapping" has a real case. Approvals and
        // history are append-only, so a rerun resets this setup instead of recreating it.
        let gapRows =
            [| { From = "first_name"; To = "First name" }
               { From = "last_name"; To = "Last name" }
               { From = "email"; To = "Email" }
               { From = "message"; To = "Note" } |]
        let gapLiveAt = at 2 9 0
        let gapConnection =
            let active = Store.allFirms s |> Array.filter _.Active |> Array.map _.Id |> Set.ofArray
            Store.allConnections s
            |> Array.map _.Connection
            |> Array.filter (fun c -> c.Enabled && c.Status = "active" && active.Contains c.FirmId && (Catalog.tryFind c.Kind |> Option.map _.Category) = Some "crm")
            |> Array.countBy _.FirmId
            |> Array.sortByDescending snd
            |> Array.tryHead
            |> Option.map (fun (firmId, _) ->
                let f = Store.firmOrFail s firmId
                let url = $"{opts.Origin}/receiver/crm/webhook?require=Phone"
                let template = Payloads.rowsToTemplate gapRows
                let settings = Dictionary<string, string>(dict [ ("simulated", "mapping-gap") ])
                let live: ActiveRevision = { Version = 1; Recipe = "create-contact"; Kind = "webhook"; Rows = gapRows; At = gapLiveAt }
                match gapExisting with
                | Some id ->
                    let c = (Store.connectionOrFail s id).Connection
                    Store.updateConnection s { c with Enabled = true; Status = "active"; DisabledReason = null; ConsecutiveFailures = 0; DeliveryUrl = url; Settings = settings }
                    Store.setConnectionTemplate s id template
                    s.Execute "UPDATE connections SET created_at=$2 WHERE id=$1" [ id; gapLiveAt ] |> ignore
                    Store.listSetups s
                    |> Array.tryFind (fun x -> x.ConnectionId = id)
                    |> Option.iter (fun x ->
                        Store.saveSetup s { x with Rows = gapRows; Recipe = "create-contact"; Version = 1; SentVersion = Nullable 1; TestDeliveryId = null; ClientConfirmedAt = Nullable gapLiveAt; Active = live }
                        Store.appendEvent s x.Id "operator" "Reset to live version 1 by the lab simulation")
                    id
                | None ->
                    let c: Connection =
                        { Id = Secrets.newId "con"
                          FirmId = f.Id
                          FirmName = f.Name
                          Kind = "webhook"
                          Name = "Intake webhook"
                          Source = Source.manual
                          SourceRef = null
                          Credentials = [||]
                          Settings = settings
                          Enabled = true
                          Status = "active"
                          DisabledReason = null
                          ConsecutiveFailures = 0
                          DeliveryUrl = url
                          Issues = [||]
                          CreatedAt = gapLiveAt }
                    Store.insertConnection s c (Secrets.newSigningSecret ()) template
                    let setup: Setup =
                        { Id = Secrets.newId "set"
                          FirmId = f.Id
                          Token = Secrets.newToken ()
                          Kind = "webhook"
                          Recipe = "create-contact"
                          Rows = gapRows
                          Version = 1
                          LastVersion = 1
                          SentVersion = Nullable 1
                          ConnectionId = c.Id
                          TestDeliveryId = null
                          ClientConfirmedAt = Nullable gapLiveAt
                          Active = live
                          Approvals = [||]
                          History = [||]
                          CreatedAt = gapLiveAt - day }
                    Store.saveSetup s setup
                    Store.appendApproval s setup.Id { Version = 1; Decision = "approved"; By = "Firm contact"; Comment = "Looks right."; At = gapLiveAt - 2L * hour }
                    Store.appendEvent s setup.Id "operator" "Went live on approved version 1 (lab simulation)"
                    c.Id)

        // ----- Leads: the existing ones, plus a top-up for firms with a CRM -----
        let existing =
            s.Query "SELECT id FROM events WHERE test=0 ORDER BY created_at, id" [] (fun r -> Db.Read.str r 0)
            |> Array.choose (Store.tryEvent s)
        let firms = Dictionary<string, Store.FirmRow>()
        let firm id =
            match firms.TryGetValue id with
            | true, f -> f
            | _ ->
                let f = Store.firmOrFail s id
                firms[id] <- f
                f
        let rows = Dictionary<string, Store.ConnectionRow>()
        let conns = Dictionary<string, Connection>()
        let firmConns = Dictionary<string, string[]>()
        let connectionsOf firmId =
            match firmConns.TryGetValue firmId with
            | true, ids -> ids
            | _ ->
                let rs = Store.connectionsOf s firmId
                for r in rs do
                    rows[r.Connection.Id] <- r
                    conns[r.Connection.Id] <- r.Connection
                let ids = rs |> Array.map _.Connection.Id
                firmConns[firmId] <- ids
                ids
        let isCrm (c: Connection) = (Catalog.tryFind c.Kind |> Option.map _.Category) = Some "crm"
        let crmFirms =
            Store.allFirms s
            |> Array.filter (fun f ->
                f.Active
                && connectionsOf f.Id |> Array.exists (fun id -> let c = conns[id] in isCrm c && c.Enabled && c.Status = "active"))

        // Times: weekdays busier than weekends, most calls in office hours, Eastern.
        let sampleTime () =
            let rec go () =
                let d = rnd.Next opts.Days
                let midnight = today - int64 d * day
                let weekend = isWeekend midnight
                let h = if rnd.NextDouble() < 0.78 then 8.0 + rnd.NextDouble() * 10.0 else rnd.NextDouble() * 24.0
                let t = midnight + int64 (h * float hour)
                if t > nowMs - 15L * minute || t < windowStart || (weekend && rnd.NextDouble() > 0.35) then go () else t
            go ()
        let times = Array.init existing.Length (fun _ -> sampleTime ()) |> Array.sort
        let leads = existing |> Array.mapi (fun i e -> { e with CreatedAt = times[i] })

        let seedOpts = { Seed.defaults with RandomSeed = opts.RandomSeed }
        let added =
            [| for f in crmFirms do
                   let have = leads |> Array.filter (fun e -> e.FirmId = f.Id) |> Array.length
                   for _ in 1 .. max 0 (opts.CrmLeads - have) do
                       let l, _ = Seed.lead rnd seedOpts f
                       ({ Id = Secrets.newId simPrefix; FirmId = f.Id; Test = false; CreatedAt = sampleTime (); Lead = l }: LeadEvent) |]

        // ----- Scenarios, picked from the data -----
        let all = Array.append leads added
        let byKind kind = conns.Values |> Seq.filter (fun c -> c.Kind = kind) |> Seq.map _.Id |> Array.ofSeq
        // Load every connection of every firm with a lead.
        for e in all do connectionsOf e.FirmId |> ignore
        let showcase =
            crmFirms
            |> Array.sortByDescending (fun f -> connectionsOf f.Id |> Array.filter (fun id -> isCrm conns[id]) |> Array.length)
            |> Array.tryHead
        let showcaseConn kind =
            showcase
            |> Option.bind (fun f -> connectionsOf f.Id |> Array.tryFind (fun id -> conns[id].Kind = kind && conns[id].Enabled))
        let busiestWith kind (skip: Set<string>) =
            all
            |> Array.countBy _.FirmId
            |> Array.sortByDescending snd
            |> Array.tryPick (fun (fid, _) ->
                if skip.Contains fid then None
                else connectionsOf fid |> Array.tryFind (fun id -> conns[id].Kind = kind && conns[id].Enabled))
        let window (from: int64) (until: int64) answer = fun t -> if t >= from && t < until then Some(answer t) else None
        let deadEmail = busiestWith "email" Set.empty
        let deadFirm = deadEmail |> Option.map (fun id -> conns[id].FirmId) |> Option.toList |> Set.ofList
        let pausedEmail = busiestWith "email" (deadFirm |> Set.union (showcase |> Option.map _.Id |> Option.toList |> Set.ofList))
        let outageStart, outageEnd = at 2 13 30, at 2 16 0
        // Every route to the firm without a fallback is down for longer than the 72-hour window.
        let longStart, longEnd = at 4 9 0, at 1 9 0

        // Two firms nothing can reach: one agreed a fallback (an SMS to its intake team), and its
        // email fails during the Answering Legal outage; the other agreed none, and every route
        // to it is down for days.
        let pausedFirm = pausedEmail |> Option.map (fun id -> conns[id].FirmId) |> Option.toList |> Set.ofList
        let handOffFirms =
            let skip = deadFirm |> Set.union pausedFirm |> Set.union (showcase |> Option.map _.Id |> Option.toList |> Set.ofList)
            let has fid kind = connectionsOf fid |> Array.exists (fun id -> conns[id].Kind = kind && conns[id].Enabled && conns[id].Status = "active")
            all
            |> Array.countBy _.FirmId
            |> Array.sortByDescending snd
            |> Array.map fst
            |> Array.filter (fun fid -> not (skip.Contains fid) && (firm fid).Active && has fid "email" && has fid "answering-legal")
            |> Array.truncate 2
        let withFallback, withoutFallback =
            match handOffFirms with
            | [| a; b |] -> Some a, Some b
            | [| a |] -> Some a, None
            | _ -> None, None
        let fallbackOf = Dictionary<string, string>()
        withFallback
        |> Option.iter (fun fid ->
            let f = firm fid
            let c: Connection =
                { Id = Secrets.newId "con"
                  FirmId = f.Id
                  FirmName = f.Name
                  Kind = "sms"
                  Name = "Intake team SMS"
                  Source = Source.manual
                  SourceRef = null
                  Credentials = [||]
                  // An invented 555 number: the lab never holds a real one.
                  Settings = Dictionary<string, string>(dict [ ("role", "fallback"); ("to", "+15555550142"); ("simulated", "fallback") ])
                  Enabled = true
                  Status = "active"
                  DisabledReason = null
                  ConsecutiveFailures = 0
                  DeliveryUrl = opts.Origin + "/receiver/crm/sms"
                  Issues = [||]
                  CreatedAt = windowStart }
            Store.insertConnection s c (Secrets.newSigningSecret ()) null
            let row = Store.connectionOrFail s c.Id
            rows[c.Id] <- row
            conns[c.Id] <- row.Connection
            firmConns[fid] <- Array.append (connectionsOf fid) [| c.Id |]
            fallbackOf[fid] <- c.Id)
        let emailOf fid = connectionsOf fid |> Array.tryFind (fun id -> conns[id].Kind = "email")
        let relayStart, relayEnd = at 1 9 0, at 1 10 30
        let redirectStart, redirectEnd = at 4 13 0, at 3 9 0
        let incidentStart = nowMs - 4L * hour
        let scenarios: Scenario list =
            [ { Name = "platform-outage"
                Story = "Answering Legal was down for 2½ hours: rate-limited (429, Retry-After 120 s) for 20 minutes, then 503. Retries delivered every lead once it came back."
                ConnectionIds = byKind "answering-legal"
                Answer =
                  window outageStart outageEnd (fun t ->
                      if t < outageStart + 20L * minute then Http(429, "rate limited", "120") else Http(503, "unavailable (outage)", null))
                Mode = "" }
              { Name = "relay-timeouts"
                Story = "The email relay stopped answering for 90 minutes: attempts timed out after 10 s and were retried."
                ConnectionIds = byKind "email"
                Answer = window relayStart relayEnd (fun _ -> NoAnswer("no answer in 10000 ms", 10_000L))
                Mode = "" }
              match withFallback |> Option.bind emailOf with
              | Some id ->
                  { Name = "both-routes-down"
                    Story = "During the Answering Legal outage, this firm's email failed too, so nothing could reach it but its fallback."
                    ConnectionIds = [| id |]
                    Answer = window outageStart outageEnd (fun _ -> Http(503, "unavailable", null))
                    Mode = "" }
              | None -> ()
              match withoutFallback with
              | Some fid ->
                  { Name = "long-outage"
                    Story = "Every route to this firm was down for days, longer than its 72-hour window, and it had agreed no fallback."
                    ConnectionIds = connectionsOf fid
                    Answer = window longStart longEnd (fun _ -> Http(503, "unavailable (long outage)", null))
                    Mode = "" }
              | None -> ()
              match deadEmail with
              | Some id ->
                  { Name = "dead-endpoint"
                    Story = "This firm's email endpoint has answered 503 since the start of the week. Each lead was retried for about 69 hours, then failed; after 5 in a row the connection was switched off."
                    ConnectionIds = [| id |]
                    Answer = window (windowStart + 12L * hour) Int64.MaxValue (fun _ -> Http(503, "unavailable (down)", null))
                    Mode = "down" }
              | None -> ()
              match showcaseConn "clio-grow" with
              | Some id ->
                  { Name = "auth-expired"
                    Story = "The Clio Grow access token expired 3 business days ago: the first 401 moved it to needs reconnection, and every lead since is held for it until the firm reconnects."
                    ConnectionIds = [| id |]
                    Answer = window (at 3 0 0) Int64.MaxValue (fun _ -> Http(401, "unauthorized: access token expired", null))
                    Mode = "reject" }
              | None -> ()
              match showcaseConn "lead-docket" with
              | Some id ->
                  { Name = "hook-gone"
                    Story = "The Lead Docket intake URL was revoked 2 business days ago: the first lead after got 410 Gone, so it needs reconnection, and later leads are held for it."
                    ConnectionIds = [| id |]
                    Answer = window (at 2 11 0) Int64.MaxValue (fun _ -> Http(410, "gone: hook removed", null))
                    Mode = "gone" }
              | None -> ()
              match showcaseConn "mycase" with
              | Some id ->
                  { Name = "redirect"
                    Story = "MyCase moved its endpoint for a day (301, never followed). 5 refusals in a row switched it off; once fixed, an operator reactivated it and replayed the refused leads, which were accepted."
                    ConnectionIds = [| id |]
                    Answer = window redirectStart redirectEnd (fun _ -> Http(301, "moved permanently", null))
                    Mode = "" }
              | None -> ()
              match showcaseConn "practicepanther" with
              | Some id ->
                  { Name = "incident-now"
                    Story = "PracticePanther has answered 503 for the last 4 hours: its leads are still retrying. The API's worker sends them when due; the receiver is left flaky, so they recover."
                    ConnectionIds = [| id |]
                    Answer = window incidentStart Int64.MaxValue (fun _ -> Http(503, "unavailable", "60"))
                    Mode = "flaky" }
              | None -> () ]
        let scenarioOf = Dictionary<string, Scenario>()
        // Later, narrower scenarios win over the platform-wide ones.
        for sc in scenarios do
            for id in sc.ConnectionIds do
                scenarioOf[id] <- sc
        let platformWide = set [ "platform-outage"; "relay-timeouts" ]
        let answerAt (c: Connection) (t: int64) =
            scenarios
            |> List.filter (fun sc -> Array.contains c.Id sc.ConnectionIds)
            |> List.sortBy (fun sc -> if platformWide.Contains sc.Name then 1 else 0)
            |> List.tryPick (fun sc -> sc.Answer t |> Option.map (fun a -> sc, a))

        // The incident needs leads in its window that PracticePanther takes.
        let incidentLeads =
            match showcase, showcaseConn "practicepanther" with
            | Some f, Some pp ->
                [| for ago in [ 210L; 70L; 12L ] ->
                       let rec make tries =
                           let l, _ = Seed.lead rnd seedOpts f
                           let l = { l with Webchat = false; Source = "Answering AI" }
                           if tries > 0 && (l.FirstName = "" || (Routing.decide f { rows[pp] with Connection = conns[pp] } l).Outcome <> "planned") then make (tries - 1) else l
                       ({ Id = Secrets.newId simPrefix; FirmId = f.Id; Test = false; CreatedAt = nowMs - ago * minute; Lead = make 50 }: LeadEvent) |]
            | _ -> [||]
        let added = Array.append added incidentLeads

        // New matters at both firms while nothing reaches them.
        let handOffLeads =
            [| for fid in handOffFirms do
                   let f = firm fid
                   let start = if Some fid = withoutFallback then longStart else outageStart
                   for after in [ 15L; 45L ] do
                       let rec make tries =
                           let l, category = Seed.lead rnd seedOpts f
                           let l = { l with Webchat = false; Source = "Answering AI" }
                           if tries > 0 && (l.FirstName = "" || category <> "new-matter") then make (tries - 1) else l
                       yield ({ Id = Secrets.newId simPrefix; FirmId = fid; Test = false; CreatedAt = start + after * minute; Lead = make 80 }: LeadEvent) |]
        let added = Array.append added handOffLeads

        // Tests: the showcase firm checked each destination when it was set up, and Clio again after the token expired.
        let testLead =
            Categories.referenceLeads.Value
            |> Array.find (fun r -> r.Mapped.Category = "new-matter" && r.Lead.FirstName <> "")
            |> _.Lead
        let tests =
            match showcase with
            | None -> [||]
            | Some f ->
                let test t : LeadEvent = { Id = Secrets.newId simPrefix; FirmId = f.Id; Test = true; CreatedAt = t; Lead = testLead }
                [| for i, id in connectionsOf f.Id |> Array.filter (fun id -> conns[id].Enabled) |> Array.indexed do
                       yield test (at 4 10 (i * 3)), id
                   match showcaseConn "clio-grow" with
                   | Some id -> yield test (at 3 11 15), id
                   | None -> () |]

        // ----- The event loop -----
        let events = Dictionary<string, LeadEvent>()
        let sims = ResizeArray<Sim>()
        let received = ResizeArray<ReceivedRequest>()
        let queue = PriorityQueue<Action, struct (int64 * int)>()
        let mutable seq = 0
        let enqueue (t: int64) a =
            seq <- seq + 1
            queue.Enqueue(a, struct (t, seq))
        let latency () = 150L + int64 (rnd.Next 700)
        let schedule (sim: Sim) (t: int64) =
            sim.D <- { sim.D with NextAttemptAt = Nullable t }
            enqueue t (Send sim)
        for e in Array.append leads added do
            events[e.Id] <- e
            enqueue e.CreatedAt (Publish(e, None))
            if fallbackOf.ContainsKey e.FirmId then
                enqueue (e.CreatedAt + Urgency.fallbackAfterMs (Categories.classify e.Lead.CallType).Category) (HandOffCheck e)
        for e, id in tests do
            events[e.Id] <- e
            enqueue e.CreatedAt (Publish(e, Some id))
        pausedEmail |> Option.iter (fun id -> enqueue (at 2 17 0) (Pause id))
        // The redirect is fixed: replay what it refused.
        showcaseConn "mycase" |> Option.iter (fun id -> enqueue (at 3 9 30) (Reactivate id))
        enqueue (at 3 9 40) (Replay((fun x -> x.D.Kind = "mycase" && x.D.Status = "failed" && not (isNull x.D.Reason) && x.D.Reason.StartsWith "HTTP 301"), 50))
        // The long outage ends: an operator reactivates the firm's destinations and resends what failed.
        withoutFallback
        |> Option.iter (fun fid ->
            for id in connectionsOf fid do
                enqueue (longEnd + 30L * minute) (Reactivate id)
            enqueue (longEnd + 40L * minute) (Replay((fun x -> x.D.FirmId = fid && x.D.Status = "failed"), 200)))

        let newSim (e: LeadEvent) (c: Connection) status reason (decision: RoutingDecision) (t: int64) replayOf template =
            let sim =
                { D =
                    { Id = Secrets.newId "dlv"
                      EventId = e.Id
                      ConnectionId = c.Id
                      ConnectionName = c.Name
                      Kind = c.Kind
                      FirmId = c.FirmId
                      FirmName = c.FirmName
                      Test = e.Test
                      Status = status
                      Stage = ""
                      Reason = reason
                      Decision = decision
                      NextAttemptAt = Nullable()
                      ReplayOf = replayOf
                      CreatedAt = t
                      Attempts = [||]
                      Diagnosis = null }
                  Attempts = ResizeArray()
                  Template = template }
            sims.Add sim
            // Held (its destination is paused): waits, unscheduled, as Dispatch plans it.
            if status = "pending" && (isNull reason || not (reason.StartsWith Diagnose.heldPrefix)) then schedule sim (t + latency ())

        let send (sim: Sim) (t: int64) =
            let c = conns[sim.D.ConnectionId]
            let stop status reason = sim.D <- { sim.D with Status = status; Reason = reason; NextAttemptAt = Nullable() }
            // As Dispatch.attempt: held while its destination needs reconnection.
            if Dispatch.needsReconnection c && not sim.D.Test then stop "pending" Store.reconnectReason
            elif c.Status <> "active" then stop "cancelled" $"connection disabled: {c.DisabledReason}"
            // As Dispatch.attempt: paused since it was planned, so it is held, unsent.
            elif not c.Enabled && not sim.D.Test then stop "pending" Store.heldReason
            else
                let e = events[sim.D.EventId]
                match Dispatch.buildFor { rows[c.Id] with Connection = c } sim.Template e.Id e.Lead with
                | Error errors -> stop "failed" ("body: " + String.Join("; ", errors))
                | Ok built ->
                    let n = sim.Attempts.Count + 1
                    let path =
                        match Uri.TryCreate(c.DeliveryUrl, UriKind.Absolute) with
                        | true, u -> u.AbsolutePath
                        | _ -> c.DeliveryUrl
                    let scenario = answerAt c t
                    let mode = scenario |> Option.map (fun (sc, _) -> if sc.Mode = "" then sc.Name else sc.Mode) |> Option.defaultValue "ok"
                    let status, error, retryAfter, verdict, waited =
                        match scenario with
                        | Some(_, Http(code, verdict, ra)) -> Some code, null, ra, verdict, 20L + int64 (rnd.Next 150)
                        | Some(_, NoAnswer(err, waited)) -> None, err, null, null, waited
                        | None ->
                            match FakeCrm.checkWith (FakeCrm.requiredByUrl c.DeliveryUrl) c.Kind built.ContentType built.Body with
                            | [] -> Some 201, null, null, "accepted", 60L + int64 (rnd.Next 400)
                            | problems -> Some 422, null, null, String.Join("; ", problems), 40L + int64 (rnd.Next 200)
                    let outcome = Policy.classify status error retryAfter t
                    let tr = Dispatch.transition c sim.D.Test n outcome (fun ra -> Policy.nextDelayMs n 1.0 ra rnd.NextDouble)
                    sim.Attempts.Add
                        { N = n
                          At = t
                          DurationMs = waited
                          Url = c.DeliveryUrl
                          Status = (match status with Some x -> Nullable x | None -> Nullable())
                          Error = error
                          Outcome = tr.Outcome
                          RequestBody = built.Body
                          ResponseSnippet = (if isNull verdict then "" else Json.serialize {| result = verdict |})
                          NextAttemptInMs = (match tr.NextInMs with Some x -> Nullable x | None -> Nullable()) }
                    if status.IsSome then
                        received.Add
                            { ReceivedRequest.At = t
                              Path = path + (if mode = "ok" || scenario.IsNone then "" else $"?mode={mode}")
                              Kind = c.Kind
                              Mode = mode
                              Answered = status.Value
                              Verdict = verdict
                              Body = (if built.Body.Length > 4000 then built.Body.Substring(0, 4000) else built.Body) }
                    sim.D <- { sim.D with Status = tr.Status; Reason = tr.Reason; NextAttemptAt = Nullable() }
                    tr.NextInMs |> Option.iter (fun delay -> schedule sim (t + waited + delay))
                    tr.Connection |> Option.iter (fun x -> conns[x.Id] <- x)
                    // As Dispatch.attempt: the connection needs reconnection, so its other waiting leads are held.
                    if tr.HoldPending then
                        for o in sims do
                            if o.D.ConnectionId = c.Id && o.D.Status = "pending" && o.D.Id <> sim.D.Id && not o.D.Test then
                                o.D <- { o.D with Reason = Store.reconnectReason; NextAttemptAt = Nullable() }

        let mutable replays = 0
        let mutable stop = false
        while not stop && queue.Count > 0 do
            let mutable action = Unchecked.defaultof<Action>
            let mutable prio = struct (0L, 0)
            queue.TryPeek(&action, &prio) |> ignore
            let struct (t, _) = prio
            if t > nowMs then stop <- true
            else
                queue.Dequeue() |> ignore
                match action with
                | Publish(e, only) ->
                    let f = firm e.FirmId
                    for id in connectionsOf e.FirmId do
                        let c = conns[id]
                        let take =
                            match only with
                            | Some only -> c.Id = only && c.Status = "active"
                            | None ->
                                (c.Enabled || Dispatch.isPaused c) && (c.Status = "active" || Dispatch.needsReconnection c)
                                && setting c "stage" <> "setup" && not (Dispatch.isFallback c)
                        // A destination added in the app gets only leads from after it existed.
                        let existed = not (c.Source = Source.manual && c.CreatedAt > e.CreatedAt)
                        if take && existed then
                            if e.Test then newSim e c "pending" null Unchecked.defaultof<RoutingDecision> e.CreatedAt null None
                            else
                                let decision = Routing.decide f { rows[id] with Connection = c } e.Lead
                                if decision.Outcome <> "planned" then newSim e c "skipped" decision.Reason decision e.CreatedAt null None
                                else
                                    match Dispatch.holdReason c with
                                    | Some reason -> newSim e c "pending" reason decision e.CreatedAt null None
                                    | None -> newSim e c "pending" null decision e.CreatedAt null None
                    // As Dispatch.runFallbacks: every delivery that could reach the firm is held, so the fallback goes at once.
                    if only.IsNone && fallbackOf.ContainsKey e.FirmId then
                        let mine = sims |> Seq.filter (fun x -> x.D.EventId = e.Id && x.D.Status <> "skipped") |> Array.ofSeq
                        let held (x: Sim) = x.D.Status = "pending" && not x.D.NextAttemptAt.HasValue
                        if mine.Length > 0 && mine |> Array.forall held then enqueue t (HandOffCheck e)
                // Only the send it was scheduled for: a held delivery's earlier send no longer applies.
                | Send sim -> if sim.D.Status = "pending" && sim.D.NextAttemptAt = Nullable t then send sim t
                | Pause id ->
                    let settings = Dictionary<string, string>(conns[id].Settings)
                    settings[Dispatch.pausedAtKey] <- string t
                    conns[id] <- { conns[id] with Enabled = false; Settings = settings }
                | Reactivate id ->
                    conns[id] <- { conns[id] with Status = "active"; DisabledReason = null; ConsecutiveFailures = 0 }
                    // As Store.releaseHeld: what waited for the firm to reconnect is sent now.
                    for o in sims do
                        if o.D.ConnectionId = id && o.D.Status = "pending" && not o.D.NextAttemptAt.HasValue then
                            o.D <- { o.D with Reason = null }
                            schedule o (t + latency ())
                | HandOffCheck e ->
                    // As Dispatch.runFallbacks: nothing accepted yet, so the agreed fallback takes the hand-off.
                    let handedOff =
                        sims |> Seq.exists (fun x -> x.D.EventId = e.Id && x.Attempts |> Seq.exists (fun a -> a.Outcome = "delivered"))
                    // As Dispatch.runFallbacks: never for a lead the firm's routing sends nowhere on purpose.
                    let mine = sims |> Seq.filter (fun x -> x.D.EventId = e.Id) |> Array.ofSeq
                    let excludedByRouting =
                        mine.Length > 0 && mine |> Array.forall (fun x -> x.D.Status = "skipped" && not (Diagnose.isLeadProblem x.D.Reason))
                    match fallbackOf.TryGetValue e.FirmId with
                    | true, id when not handedOff && not excludedByRouting && conns[id].Status = "active"
                                    && not (mine |> Array.exists (fun x -> x.D.ConnectionId = id)) ->
                        newSim e conns[id] "pending" null Unchecked.defaultof<RoutingDecision> t null None
                    | _ -> ()
                | Replay(pick, limit) ->
                    let chosen = sims |> Seq.filter (fun x -> x.D.CreatedAt < t && pick x) |> Seq.truncate limit |> Array.ofSeq
                    for old in chosen do
                        let c = conns[old.D.ConnectionId]
                        if c.Status = "active" then
                            replays <- replays + 1
                            newSim events[old.D.EventId] c "pending" null old.D.Decision t old.D.Id old.Template

        // ----- Write the result -----
        for e in leads do
            s.Execute "UPDATE events SET created_at=$2 WHERE id=$1" [ e.Id; e.CreatedAt ] |> ignore
        for e in added do Store.insertEvent s e
        for e, _ in tests do Store.insertEvent s e
        for sim in sims do
            Store.insertDelivery s sim.D sim.Template
            for a in sim.Attempts do Store.insertAttempt s sim.D.Id a
        for r in received |> Seq.sortBy _.At |> Seq.rev |> Seq.truncate 500 |> Seq.rev do
            Store.insertReceived s r
        let touched = Dictionary<string, string>()
        for sc in scenarios do
            if not (platformWide.Contains sc.Name) then
                for id in sc.ConnectionIds do touched[id] <- sc.Mode
        pausedEmail |> Option.iter (fun id -> touched[id] <- "")
        for KeyValue(id, c) in conns do
            let original = rows[id].Connection
            let mode = match touched.TryGetValue id with | true, m -> m | _ -> ""
            let url =
                match receiverBase opts.Origin c.DeliveryUrl with
                | Some b when mode <> "" -> $"{b}?mode={mode}"
                | _ -> c.DeliveryUrl
            let settings = Dictionary<string, string>(c.Settings)
            if touched.ContainsKey id then
                settings["simulated"] <- (if Some id = pausedEmail then "paused" else (scenarioOf[id]).Name)
            let final = { c with DeliveryUrl = url; Settings = settings }
            if final.Status <> original.Status || final.Enabled <> original.Enabled || final.ConsecutiveFailures <> original.ConsecutiveFailures
               || final.DeliveryUrl <> original.DeliveryUrl || touched.ContainsKey id then
                Store.updateConnection s final

        // ----- Billing and terms for the same week (docs/billing, docs/terms) -----
        Billing.seedPriceBook s nowMs
        let hasDestination (fid: string) kinds =
            connectionsOf fid |> Array.exists (fun id -> conns[id].Enabled && List.contains ((Catalog.tryFind conns[id].Kind |> Option.map _.Category) |> Option.defaultValue "") kinds)
        let tierOf (fid: string) =
            if showcase |> Option.exists (fun f -> f.Id = fid) then "insight"
            elif hasDestination fid [ "crm"; "automation" ] then "deliver"
            else "answer"
        Billing.seedAccounts s nowMs tierOf withFallback (pausedEmail |> Option.map (fun id -> conns[id].FirmId))
        Terms.seed s nowMs

        let describe id = let c = conns[id] in $"{c.FirmName} · {c.Name}"
        { Leads = leads.Length + added.Length
          AddedLeads = added.Length
          Tests = tests.Length
          Deliveries = sims.Count
          ByStatus = sims |> Seq.countBy _.D.Status |> Seq.sortByDescending snd |> List.ofSeq
          Attempts = sims |> Seq.sumBy _.Attempts.Count
          Replays = replays
          Disabled = conns.Values |> Seq.filter (fun c -> c.Status <> "active") |> Seq.map (fun c -> describe c.Id, c.DisabledReason) |> List.ofSeq
          Scenarios =
            [ for sc in scenarios do
                  let where =
                      if sc.ConnectionIds.Length = 0 then "no connection"
                      elif platformWide.Contains sc.Name then $"all {sc.ConnectionIds.Length} {conns[sc.ConnectionIds[0]].Kind} connections"
                      else describe sc.ConnectionIds[0]
                  yield { Name = sc.Name; Story = sc.Story; Where = where }
              match gapConnection with
              | Some id ->
                  yield
                      { Name = "mapping-gap"
                        Story = "A webhook went live on an approved mapping without Phone; its receiver requires Phone, so every lead since was refused until 5 in a row switched it off. Fix: Edit mapping (a draft revision the firm approves)."
                        Where = describe id }
              | None -> ()
              match withFallback with
              | Some fid ->
                  yield
                      { Name = "fallback"
                        Story = "Nothing reached this firm during the outage, but it had agreed an SMS fallback: each new matter reached its intake team within the hour."
                        Where = (firm fid).Name }
              | None -> ()
              match withoutFallback with
              | Some fid ->
                  yield
                      { Name = "no-fallback"
                        Story = "Every route down for days and no fallback: retries ran out, its destinations were switched off, and nothing reached the firm until an operator reactivated them and resent its leads, days late and past their 72-hour window."
                        Where = (firm fid).Name }
              | None -> ()
              match pausedEmail with
              | Some id -> yield { Name = "paused"; Story = "Paused 2 business days ago at 5 pm: later leads are held for it, and sent when it resumes."; Where = describe id }
              | None -> () ]
          From = windowStart
          To = nowMs })
