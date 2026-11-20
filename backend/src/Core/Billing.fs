/// Billing in the lab (docs/billing/strategy.md): a versioned price book, accounts
/// with a cycle anchor, effective-dated subscriptions, handled conversations rated
/// from the same lead records as delivery, closed cycles locked as they were, and a
/// shadow comparison with the legacy minutes formula. Prices are illustrative.
module LeadIntegrations.Billing

open System
open LeadIntegrations.Db
open LeadIntegrations.Domain

let private minute = 60_000L
let private hour = 60L * minute
let private day = 24L * hour
/// Billing cycles follow the company's calendar (Zones.company), daylight saving included,
/// so every invoice shares one calendar whatever the firm's own zone.
let private calendar = lazy (Zones.find Zones.company)

// ----- Types -----

type Price =
    { Id: string
      Plan: string
      Version: int
      Tier: string
      Name: string
      MonthlyCents: int
      Included: int
      OverageCents: int
      EffectiveFrom: int64 }

type Subscription =
    { Id: string
      FirmId: string
      PriceId: string
      StartsAt: int64
      EndsAt: Nullable<int64>
      DiscountPct: int
      DiscountReason: string
      DiscountUntil: Nullable<int64> }

type Account =
    { FirmId: string
      FirmName: string
      CycleDay: int
      LegacyPlanMinutes: Nullable<int>
      LegacyPlanCents: Nullable<int>
      LegacyOverage: string
      LegacyWrapup: float }

/// One conversation as billing sees it.
type Conversation =
    { EventId: string
      At: int64
      Category: string
      Seconds: int
      Billable: bool
      /// The rule that made it free, or null.
      Rule: string
      Units: int }

type Line = { Label: string; Quantity: int; UnitCents: int; AmountCents: int }
type Segment = { PlanName: string; Tier: string; From: int64; To: int64; FeeCents: int; Included: int; Note: string }
type RuleCount = { Rule: string; Label: string; Count: int }
type CategoryCount = { Category: string; Billable: int; Free: int }
type Check = { Code: string; Severity: string; Message: string }

type Legacy =
    { Minutes: int
      NoChargeMinutes: int
      NoChargeAllowance: int
      Wrapup: float
      BilledMinutes: int
      PlanMinutes: Nullable<int>
      OverageMinutes: int
      OverageRateCents: Nullable<int>
      TotalCents: Nullable<int>
      Problems: string[] }

type Value =
    { NewMatters: int
      OnTime: int
      Expired: int
      HandOffs: int }

type Statement =
    { FirmId: string
      FirmName: string
      CycleFrom: int64
      /// Exclusive: the next cycle's first day.
      CycleTo: int64
      /// The calendar the cycle's dates are on (IANA; Zones.company).
      TimeZone: string
      InProgress: bool
      /// Set when the cycle closed and its statement was kept as it was.
      LockedAt: Nullable<int64>
      Segments: Segment[]
      Conversations: int
      Billable: int
      Free: int
      Units: int
      Included: int
      Overage: int
      ByCategory: CategoryCount[]
      FreeByRule: RuleCount[]
      Lines: Line[]
      TotalCents: int
      Value: Value
      Legacy: Legacy
      Checks: Check[] }

// ----- Non-billable rules: named and versioned; every free conversation records one -----

let rules =
    [ "solicitation@1", "Solicitation or vendor"
      "system@1", "System entry or test"
      "hang-up-20s@1", "Hang-up under 20 seconds, no details"
      "repeat-10m@1", "Repeat call within 10 minutes" ]

let private ruleLabel id = rules |> List.tryFind (fst >> (=) id) |> Option.map snd |> Option.defaultValue id

/// Synthetic talk time: stable per lead, by kind of call (the lab holds no call recordings).
let seconds (e: LeadEvent) (category: string) =
    let h = Convert.ToInt32(e.Id.Substring(e.Id.Length - 5), 16)
    let between lo hi = lo + h % (hi - lo)
    // A caller who hung up before giving details: a short call.
    if String.IsNullOrWhiteSpace e.Lead.FirstName then between 5 19
    else
        match category with
        | "new-matter" -> between 180 540
        | "emergency"
        | "jail-custody" -> between 150 420
        | "existing-client" -> between 90 300
        | "vendor" -> between 25 90
        | "system"
        | "line-handling" -> between 10 60
        | _ -> between 60 240

/// Each production lead as a conversation, with the rule that made it free, oldest first. `corrected`:
/// a lead's current category where it was corrected (Store.corrected); else its category at intake.
let conversationsWith (corrected: Map<string, string>) (events: LeadEvent[]) : Conversation[] =
    let ordered = events |> Array.sortBy _.CreatedAt
    ordered
    |> Array.mapi (fun i e ->
        let category =
            match corrected.TryFind e.Id with
            | Some c -> c
            | None -> (Categories.classify e.Lead.CallType).Category
        let secs = seconds e category
        let hangUp = String.IsNullOrWhiteSpace e.Lead.FirstName && secs < 20
        let repeat =
            not (String.IsNullOrWhiteSpace e.Lead.Phone)
            && ordered[.. i - 1] |> Array.exists (fun p -> p.Lead.Phone = e.Lead.Phone && e.CreatedAt - p.CreatedAt <= 10L * minute)
        let rule =
            if category = "vendor" then "solicitation@1"
            elif category = "system" || category = "line-handling" then "system@1"
            elif hangUp then "hang-up-20s@1"
            elif repeat then "repeat-10m@1"
            else null
        { EventId = e.Id
          At = e.CreatedAt
          Category = category
          Seconds = secs
          Billable = isNull rule
          Rule = rule
          // Fair use: a conversation over 20 minutes counts as two.
          Units = (if isNull rule then (if secs > 1200 then 2 else 1) else 0) })

let conversations (events: LeadEvent[]) = conversationsWith Map.empty events

// ----- Cycles -----

/// The cycle containing t for an account billed on `cycleDay`: [from, to) in UTC ms, from
/// midnight on the company calendar (daylight saving included) to the same day next month.
let cycleOf (cycleDay: int) (t: int64) : int64 * int64 =
    let zone = calendar.Value
    let local = Zones.toLocal zone t
    let anchor =
        let thisMonth = DateTime(local.Year, local.Month, cycleDay)
        if local.Day >= cycleDay then thisMonth else thisMonth.AddMonths(-1)
    Zones.fromLocal zone anchor, Zones.fromLocal zone (anchor.AddMonths 1)

// ----- Reading -----

let private readPrice (r: System.Data.Common.DbDataReader) : Price =
    { Id = Read.str r 0; Plan = Read.str r 1; Version = Read.int r 2; Tier = Read.str r 3; Name = Read.str r 4
      MonthlyCents = Read.int r 5; Included = Read.int r 6; OverageCents = Read.int r 7; EffectiveFrom = Read.int64 r 8 }

let private priceCols = "id, plan, version, tier, name, monthly_cents, included, overage_cents, effective_from"

let priceBook (s: Session) = s.Query $"SELECT {priceCols} FROM price_book ORDER BY version, tier, included" [] readPrice

let price (s: Session) (id: string) =
    s.TryOne $"SELECT {priceCols} FROM price_book WHERE id=$1" [ id ] readPrice |> Option.defaultWith (fun () -> fail 404 "no such plan")

let private readAccount (r: System.Data.Common.DbDataReader) : Account =
    { FirmId = Read.str r 0; FirmName = Read.str r 1; CycleDay = Read.int r 2; LegacyPlanMinutes = Read.intN r 3
      LegacyPlanCents = Read.intN r 4; LegacyOverage = Read.str r 5; LegacyWrapup = r.GetDouble 6 }

let private accountSql =
    "SELECT a.firm_id, f.name, a.cycle_day, a.legacy_plan_minutes, a.legacy_plan_cents, a.legacy_overage, a.legacy_wrapup FROM billing_accounts a JOIN firms f ON f.id = a.firm_id"

let accounts (s: Session) = s.Query $"{accountSql} ORDER BY f.name" [] readAccount

let account (s: Session) (firmId: string) =
    s.TryOne $"{accountSql} WHERE a.firm_id=$1" [ firmId ] readAccount
    |> Option.defaultWith (fun () -> fail 404 "this firm has no billing account")

let subscriptions (s: Session) (firmId: string) =
    s.Query
        "SELECT id, firm_id, price_id, starts_at, ends_at, discount_pct, discount_reason, discount_until FROM subscriptions WHERE firm_id=$1 ORDER BY starts_at"
        [ firmId ]
        (fun r ->
            { Id = Read.str r 0; FirmId = Read.str r 1; PriceId = Read.str r 2; StartsAt = Read.int64 r 3; EndsAt = Read.int64N r 4
              DiscountPct = Read.int r 5; DiscountReason = Read.str r 6; DiscountUntil = Read.int64N r 7 })

let private eventsBetween (s: Session) (firmId: string) (from: int64) (until: int64) =
    s.Query "SELECT id FROM events WHERE firm_id=$1 AND test=0 AND created_at >= $2 AND created_at < $3" [ firmId; from; until ] (fun r -> Read.str r 0)
    |> Array.choose (Store.tryEvent s)

// ----- The legacy formula, for the shadow comparison -----

/// Minutes rounded up per call; no-charge by label (system entries), capped at 10% of the
/// plan; (minutes − no-charge deducted) × wrap-up factor; overage at the legacy rate,
/// which is free text there; a fixed carrier recovery fee.
let legacy (a: Account) (cs: Conversation[]) : Legacy =
    let minutes = cs |> Array.sumBy (fun c -> (c.Seconds + 59) / 60)
    let noCharge = cs |> Array.filter (fun c -> c.Rule = "system@1") |> Array.sumBy (fun c -> (c.Seconds + 59) / 60)
    let allowance = if a.LegacyPlanMinutes.HasValue then a.LegacyPlanMinutes.Value / 10 else 0
    let billed = int (Math.Round(float (minutes - min noCharge allowance) * a.LegacyWrapup))
    let rate =
        match Double.TryParse((if isNull a.LegacyOverage then "" else a.LegacyOverage.Trim().TrimStart('$')), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
        | true, v -> Nullable(int (Math.Round(v * 100.0)))
        | _ -> Nullable()
    let problems =
        [| if not a.LegacyPlanMinutes.HasValue then "Plan minutes are not set (read from the plan's name in the legacy system)"
           if not rate.HasValue then $"Overage rate is not a number: \"{a.LegacyOverage}\""
           if not a.LegacyPlanCents.HasValue then "Plan price is not set" |]
    let overage = if a.LegacyPlanMinutes.HasValue then max 0 (billed - a.LegacyPlanMinutes.Value) else 0
    { Minutes = minutes
      NoChargeMinutes = noCharge
      NoChargeAllowance = allowance
      Wrapup = a.LegacyWrapup
      BilledMinutes = billed
      PlanMinutes = a.LegacyPlanMinutes
      OverageMinutes = overage
      OverageRateCents = rate
      TotalCents =
        (if problems.Length = 0 then Nullable(a.LegacyPlanCents.Value + 2150 + overage * rate.Value) else Nullable())
      Problems = problems }

// ----- Rating: a pure function of the cycle's conversations, subscriptions and prices -----

let rate (s: Session) (a: Account) (from: int64) (until: int64) (nowMs: int64) : Statement =
    let cycleMs = float (until - from)
    let subs =
        subscriptions s a.FirmId
        |> Array.filter (fun x -> x.StartsAt < until && (not x.EndsAt.HasValue || x.EndsAt.Value > from))
    let segments =
        subs
        |> Array.map (fun x ->
            let p = price s x.PriceId
            let f = max from x.StartsAt
            let t = if x.EndsAt.HasValue then min until x.EndsAt.Value else until
            let share = float (t - f) / cycleMs
            let full = share > 0.999
            let discount = if x.DiscountPct > 0 && (not x.DiscountUntil.HasValue || x.DiscountUntil.Value > f) then x.DiscountPct else 0
            { PlanName = p.Name
              Tier = p.Tier
              From = f
              To = t
              FeeCents = int (Math.Round(float p.MonthlyCents * share * float (100 - discount) / 100.0))
              Included = int (Math.Round(float p.Included * share))
              Note =
                [ (if full then null else $"prorated, {Math.Round(share * 100.0)}%% of the cycle")
                  (if discount > 0 then $"{discount}%% {x.DiscountReason}" else null) ]
                |> List.filter (isNull >> not)
                |> String.concat "; " },
            p)
    let cs = conversationsWith (Store.corrected s a.FirmId) (eventsBetween s a.FirmId from (min until nowMs))
    let billable = cs |> Array.filter _.Billable
    let units = billable |> Array.sumBy _.Units
    let included = segments |> Array.sumBy (fun (x, _) -> x.Included)
    let overage = max 0 (units - included)
    let overageRate = segments |> Array.tryLast |> Option.map (fun (_, p) -> p.OverageCents) |> Option.defaultValue 0
    let lines =
        [| for x, _ in segments do
               { Label = $"{x.PlanName}" + (if x.Note = "" then "" else $" ({x.Note})"); Quantity = 1; UnitCents = x.FeeCents; AmountCents = x.FeeCents }
           if overage > 0 then
               { Label = "Conversations beyond the included ones"; Quantity = overage; UnitCents = overageRate; AmountCents = overage * overageRate } |]
    let leads =
        Store.listLeads s { Q = null; FirmId = a.FirmId; Category = null; Outcome = null; Test = Nullable false; Limit = 500; Offset = 0 }
        |> snd
        |> Array.filter (fun l -> l.CreatedAt >= from && l.CreatedAt < until)
    let state (l: Store.LeadSummary) = if isNull (box l.HandOff) then "" else l.HandOff.State
    let free = cs.Length - billable.Length
    let checks =
        [| if segments.Length = 0 then { Code = "no-plan"; Severity = "bad"; Message = "No subscription on the price book for this cycle" }
           if included > 0 && units > 2 * included then { Code = "usage-spike"; Severity = "warn"; Message = $"{units} billable conversations against {included} included: more than double" }
           if cs.Length >= 10 && free * 100 / cs.Length > 40 then
               { Code = "free-share"; Severity = "warn"; Message = $"{free * 100 / cs.Length}%% of conversations were free: check the rules are not misfiring" }
           for x in subs do
               if x.DiscountPct > 0 && x.DiscountUntil.HasValue && x.DiscountUntil.Value < nowMs then
                   { Code = "discount-expired"; Severity = "warn"; Message = $"A {x.DiscountPct}%% discount ({x.DiscountReason}) ended; confirm it is no longer applied" } |]
    { FirmId = a.FirmId
      FirmName = a.FirmName
      CycleFrom = from
      CycleTo = until
      TimeZone = Zones.company
      InProgress = nowMs < until
      LockedAt = Nullable()
      Segments = segments |> Array.map fst
      Conversations = cs.Length
      Billable = billable.Length
      Free = free
      Units = units
      Included = included
      Overage = overage
      ByCategory =
        cs
        |> Array.groupBy _.Category
        |> Array.map (fun (c, xs) -> { Category = c; Billable = xs |> Array.filter _.Billable |> Array.length; Free = xs |> Array.filter (fun x -> not x.Billable) |> Array.length })
        |> Array.sortByDescending (fun x -> x.Billable + x.Free)
      FreeByRule =
        cs
        |> Array.filter (fun c -> not c.Billable)
        |> Array.countBy _.Rule
        |> Array.map (fun (r, n) -> { Rule = r; Label = ruleLabel r; Count = n })
      Lines = lines
      TotalCents = lines |> Array.sumBy _.AmountCents
      Value =
        { NewMatters = leads |> Array.filter (fun l -> l.Mapping.Category = "new-matter") |> Array.length
          OnTime = leads |> Array.filter (fun l -> state l = "on-time") |> Array.length
          Expired = leads |> Array.filter (fun l -> state l = "expired") |> Array.length
          HandOffs = leads |> Array.filter (fun l -> state l <> "") |> Array.length }
      Legacy = legacy a cs
      Checks = checks }

/// One conversation rated now from its current classification, with the 10 minutes before it
/// (the repeat rule's reach) as context.
let conversationOf (s: Session) (e: LeadEvent) : Conversation =
    let context = eventsBetween s e.FirmId (e.CreatedAt - 10L * minute) (e.CreatedAt + 1L)
    conversationsWith (Store.corrected s e.FirmId) context |> Array.find (fun c -> c.EventId = e.Id)

/// Close a cycle's usage (RFC-003): each conversation before `until` not yet rated is rated from its
/// current classification and kept as it was (UsageRated). Called when a cycle's statement is locked;
/// test control closes one early. Returns how many were rated.
let closeCycle (s: Session) (firmId: string) (until: int64) : int =
    let unrated =
        s.Query
            "SELECT id FROM events WHERE firm_id=$1 AND test=0 AND created_at < $2 AND id NOT IN (SELECT event_id FROM usage)"
            [ firmId; until ]
            (fun r -> Read.str r 0)
        |> Array.choose (Store.tryEvent s)
    let cs = conversationsWith (Store.corrected s firmId) unrated
    for c in cs do
        s.Execute "INSERT INTO usage (event_id, cycle_until, billable, rule, rated_at) VALUES ($1,$2,$3,$4,$5)" [ c.EventId; until; c.Billable; c.Rule; now () ] |> ignore
        Store.emit s "UsageRated" firmId c.EventId null
    cs.Length

/// "current" or "previous". A closed cycle is rated once and kept: its statement never
/// changes with later plan or price changes.
let statement (s: Session) (firmId: string) (which: string) (nowMs: int64) : Statement =
    let a = account s firmId
    let from, until =
        let cur = cycleOf a.CycleDay nowMs
        if which = "previous" then cycleOf a.CycleDay (fst cur - day) else cur
    if until > nowMs then rate s a from until nowMs
    else
        match s.TryOne "SELECT statement, locked_at FROM invoices WHERE firm_id=$1 AND cycle_from=$2" [ firmId; from ] (fun r -> Read.str r 0, Read.int64 r 1) with
        | Some(json, at) -> { Json.deserialize<Statement> json with LockedAt = Nullable at }
        | None ->
            let st = rate s a from until nowMs
            closeCycle s firmId until |> ignore
            s.Execute "INSERT INTO invoices (firm_id, cycle_from, cycle_to, statement, locked_at) VALUES ($1,$2,$3,$4,$5)" [ firmId; from; until; Json.serialize st; nowMs ] |> ignore
            { st with LockedAt = Nullable nowMs }

/// A plan change: an upgrade applies at once, prorated by day; a downgrade from the next cycle.
let changePlan (s: Session) (firmId: string) (priceId: string) (nowMs: int64) =
    let a = account s firmId
    let next = price s priceId
    let current = subscriptions s firmId |> Array.tryFind (fun x -> x.StartsAt <= nowMs && (not x.EndsAt.HasValue || x.EndsAt.Value > nowMs))
    let starts =
        match current with
        | Some c when (price s c.PriceId).MonthlyCents > next.MonthlyCents -> snd (cycleOf a.CycleDay nowMs)
        | _ -> nowMs
    // Anything already scheduled to start later is replaced.
    s.Execute "DELETE FROM subscriptions WHERE firm_id=$1 AND starts_at > $2" [ firmId; nowMs ] |> ignore
    current |> Option.iter (fun c -> s.Execute "UPDATE subscriptions SET ends_at=$2 WHERE id=$1" [ c.Id; starts ] |> ignore)
    s.Execute "INSERT INTO subscriptions (id, firm_id, price_id, starts_at) VALUES ($1,$2,$3,$4)" [ Secrets.newId "sub"; firmId; priceId; starts ] |> ignore
    starts

// ----- The accounts list: readiness, the current cycle, and the shadow comparison -----

type AccountRow =
    { FirmId: string
      FirmName: string
      CycleDay: int
      Plan: string
      Tier: string
      Ready: bool
      Problems: string[]
      Conversations: int
      Billable: int
      TotalCents: int
      LegacyCents: Nullable<int>
      Checks: Check[] }

let accountRows (s: Session) (nowMs: int64) : AccountRow[] =
    accounts s
    |> Array.map (fun a ->
        let st = statement s a.FirmId "current" nowMs
        { FirmId = a.FirmId
          FirmName = a.FirmName
          CycleDay = a.CycleDay
          Plan = st.Segments |> Array.tryLast |> Option.map _.PlanName |> Option.toObj
          Tier = st.Segments |> Array.tryLast |> Option.map _.Tier |> Option.toObj
          Ready = st.Legacy.Problems.Length = 0 && st.Segments.Length > 0
          Problems = st.Legacy.Problems
          Conversations = st.Conversations
          Billable = st.Billable
          TotalCents = st.TotalCents
          LegacyCents = st.Legacy.TotalCents
          Checks = st.Checks })

// ----- Lab seed: illustrative prices, every firm with leads gets an account -----

let private tiers = [ "answer", "Answer", 0; "deliver", "Deliver", 10_000; "insight", "Insight", 16_000 ]
let private volumes = [ 50, 14_900, 350; 100, 27_900, 325; 250, 64_900, 300; 500, 119_900, 275 ]

/// Version 1 since long ago; version 2 (+5%) from the first of next month: the pricing
/// change the terms of service announce.
let seedPriceBook (s: Session) (nowMs: int64) =
    // Subscriptions and locked statements refer to the price book: they go first.
    for sql in [ "DELETE FROM invoices"; "DELETE FROM subscriptions"; "DELETE FROM price_book" ] do
        s.Execute sql [] |> ignore
    let nextMonth =
        let local = Zones.toLocal calendar.Value nowMs
        Zones.fromLocal calendar.Value (DateTime(local.Year, local.Month, 1).AddMonths 1)
    for version, from, factor in [ 1, 0L, 1.0; 2, nextMonth, 1.05 ] do
        for tier, label, add in tiers do
            for included, cents, over in volumes do
                let plan = $"{tier}-{included}"
                s.Execute
                    $"INSERT INTO price_book ({priceCols}) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9)"
                    [ $"{plan}@{version}"; plan; version; tier; $"{label} {included}"
                      int (Math.Round(float (cents + add) * factor)); included; int (Math.Round(float over * factor)); from ]
                |> ignore

/// Every firm with production leads this week gets an account: a plan sized to its
/// volume, a cycle on the 1st or the 15th, and legacy terms for the shadow comparison
/// (a few with the legacy system's data problems). `tierOf` picks each firm's tier;
/// `upgraded` moved up a tier three days ago (prorated); `discounted` had a launch offer that ended.
let seedAccounts (s: Session) (nowMs: int64) (tierOf: string -> string) (upgraded: string option) (discounted: string option) =
    for sql in [ "DELETE FROM invoices"; "DELETE FROM subscriptions"; "DELETE FROM billing_accounts" ] do
        s.Execute sql [] |> ignore
    let firms =
        s.Query "SELECT firm_id, COUNT(*) FROM events WHERE test=0 AND created_at >= $1 GROUP BY firm_id ORDER BY firm_id" [ nowMs - 7L * day ] (fun r ->
            Read.str r 0, Read.int r 1)
    for i, (firmId, week) in Array.indexed firms do
        let monthly = week * 30 / 7
        let included, cents, _ = volumes |> List.tryFind (fun (v, _, _) -> float v >= float monthly * 0.9) |> Option.defaultValue (List.last volumes)
        let tier = if Some firmId = upgraded then "answer" else tierOf firmId
        let h = Convert.ToInt32(firmId.Substring(firmId.Length - 4), 16)
        let cycleDay = if h % 2 = 0 then 15 else 1
        // Legacy terms were set account by account: prices from 70% to 115% of the new
        // plan's, overage rates from $1.95 to $3.95 a minute.
        s.Execute
            "INSERT INTO billing_accounts (firm_id, cycle_day, legacy_plan_minutes, legacy_plan_cents, legacy_overage, legacy_wrapup, created_at) VALUES ($1,$2,$3,$4,$5,$6,$7)"
            [ firmId
              cycleDay
              (if i % 13 = 5 then null else box (included * 3))
              box (cents * (70 + h % 46) / 100)
              (if i % 11 = 3 then "see contract" else (1.95 + float (h % 5) * 0.5).ToString("0.00", Globalization.CultureInfo.InvariantCulture))
              1.33
              nowMs - 400L * day ]
        |> ignore
        let discount, reason, until = if Some firmId = discounted then 15, "launch offer", box (nowMs - 7L * day) else 0, null, null
        let ends = if Some firmId = upgraded then box (nowMs - 3L * day) else null
        s.Execute
            "INSERT INTO subscriptions (id, firm_id, price_id, starts_at, ends_at, discount_pct, discount_reason, discount_until) VALUES ($1,$2,$3,$4,$5,$6,$7,$8)"
            [ Secrets.newId "sub"; firmId; $"{tier}-{included}@1"; nowMs - 400L * day; ends; discount; reason; until ]
        |> ignore
        if Some firmId = upgraded then
            s.Execute "INSERT INTO subscriptions (id, firm_id, price_id, starts_at) VALUES ($1,$2,$3,$4)" [ Secrets.newId "sub"; firmId; $"deliver-{included}@1"; nowMs - 3L * day ] |> ignore
