/// Correcting a lead's classification (RFC-008 §6.2). A correction is a new record that supersedes
/// the one before; nothing is recalled or resent. The destinations the lead now matches are offered
/// to an operator. Billing follows the current classification while the cycle is open; after it
/// closes, a correction that lowers the charge is a credit in the next cycle, and never a new charge.
module LeadIntegrations.Corrections

open System
open LeadIntegrations.Db
open LeadIntegrations.Domain

type Record =
    { Seq: int
      Category: string
      PracticeArea: string
      /// intake | reviewer | firm-contact
      Source: string
      By: string
      At: int64
      TaxonomyVersion: int }

type View =
    { Category: string
      PracticeArea: string
      /// Every record, oldest first; the last is current.
      Records: Record[]
      /// Destinations the lead now matches that never got it: offered to an operator, never sent automatically.
      Offered: string[] }

type BillingView =
    { /// billable | free | credited-next-cycle
      Is: string
      /// The rule that made it free, or null.
      Rule: string
      /// Set once its cycle closed and its usage was kept.
      RatedAt: Nullable<int64> }

let private recordsOf (s: Session) (e: LeadEvent) : Record[] =
    let rows =
        s.Query
            "SELECT seq, category, practice_area, source, by, at, taxonomy_version FROM classifications WHERE event_id=$1 ORDER BY seq"
            [ e.Id ]
            (fun r ->
                { Seq = Read.int r 0; Category = Read.str r 1; PracticeArea = Read.str r 2; Source = Read.str r 3
                  By = Read.str r 4; At = Read.int64 r 5; TaxonomyVersion = Read.int r 6 })
    if rows.Length > 0 then rows
    else
        // A lead recorded before classification records were kept: its intake classification, derived.
        let m = Categories.ofLead e.Lead
        [| { Seq = 1; Category = m.Category; PracticeArea = m.PracticeArea; Source = "intake"; By = null; At = e.CreatedAt
             TaxonomyVersion = Categories.version () } |]

let private eventOrFail (s: Session) (id: string) =
    match Store.tryEvent s id with
    | Some e when not e.Test -> e
    | Some _ -> fail 422 "a test lead has no classification to correct"
    | None -> fail 404 "no such lead"

/// The firm's usual destinations the lead now matches, under its current classification, that never
/// had a planned delivery of it.
let private offered (s: Session) (e: LeadEvent) (current: Record) =
    let firm = Store.firmOrFail s e.FirmId
    let m = Store.correctedMapping (Categories.ofLead e.Lead) current.Category current.PracticeArea
    let had =
        s.Query "SELECT connection_id FROM deliveries WHERE event_id=$1 AND status <> 'skipped'" [ e.Id ] (fun r -> Read.str r 0)
        |> set
    Store.connectionsOf s e.FirmId
    |> Array.filter (fun row ->
        let c = row.Connection
        c.Enabled && c.Status = "active" && not (had.Contains c.Id)
        && (match c.Settings.TryGetValue "role" with | true, "fallback" -> false | _ -> true)
        && (match c.Settings.TryGetValue "stage" with | true, "setup" -> false | _ -> true)
        && (Routing.decideAs m firm row e.Lead).Outcome = "planned")
    |> Array.map _.Connection.Id

let viewIn (s: Session) (eventId: string) : View =
    let e = eventOrFail s eventId
    let records = recordsOf s e
    let current = Array.last records
    { Category = current.Category
      PracticeArea = current.PracticeArea
      Records = records
      Offered = (if records.Length > 1 then offered s e current else [||]) }

let view (db: Database) (eventId: string) = read db (fun s -> viewIn s eventId)

let private billingIn (s: Session) (e: LeadEvent) : BillingView =
    match s.TryOne "SELECT billable, rule, rated_at FROM usage WHERE event_id=$1" [ e.Id ] (fun r -> Read.bool r 0, Read.str r 1, Read.int64 r 2) with
    | Some(billable, rule, at) ->
        let credited = s.TryOne "SELECT 1 FROM credits WHERE event_id=$1" [ e.Id ] (fun _ -> true) |> Option.isSome
        { Is = (if credited then "credited-next-cycle" elif billable then "billable" else "free"); Rule = rule; RatedAt = Nullable at }
    | None ->
        let c = Billing.conversationOf s e
        { Is = (if c.Billable then "billable" else "free"); Rule = c.Rule; RatedAt = Nullable() }

let billing (db: Database) (eventId: string) = read db (fun s -> billingIn s (eventOrFail s eventId))

/// Correct a lead's classification. `source`: reviewer | firm-contact.
let correct (db: Database) (eventId: string) (category: string) (practiceArea: string) (source: string) (by: string) : View =
    let categories = Categories.taxonomy.Value.Categories |> Array.map _.Id |> set
    let areas = Categories.taxonomy.Value.PracticeAreas |> Array.map _.Id |> set
    if String.IsNullOrWhiteSpace category || not (categories.Contains category) then fail 422 $"category: \"{category}\" is not a call category"
    if not (String.IsNullOrWhiteSpace practiceArea) && not (areas.Contains practiceArea) then fail 422 $"practiceArea: \"{practiceArea}\" is not a practice area"
    let source = if source = "firm-contact" then "firm-contact" else "reviewer"
    write db (fun s ->
        let e = eventOrFail s eventId
        let records = recordsOf s e
        let current = Array.last records
        if current.Category = category && current.PracticeArea = (if category = "new-matter" then practiceArea else null) then
            fail 409 $"the lead is already classified as {category}"
        // The intake record of a lead from before records were kept is written first, so the history is whole.
        if records.Length = 1 && isNull (s.TryOne "SELECT 1 FROM classifications WHERE event_id=$1" [ e.Id ] (fun _ -> box true) |> Option.toObj) then
            s.Execute
                "INSERT INTO classifications (event_id, seq, category, practice_area, source, by, at, taxonomy_version) VALUES ($1,1,$2,$3,'intake',NULL,$4,$5)"
                [ e.Id; current.Category; current.PracticeArea; current.At; current.TaxonomyVersion ]
            |> ignore
        let before = billingIn s e
        s.Execute
            "INSERT INTO classifications (event_id, seq, category, practice_area, source, by, at, taxonomy_version) VALUES ($1,$2,$3,$4,$5,$6,$7,$8)"
            [ e.Id; current.Seq + 1; category; (if category = "new-matter" then practiceArea else null); source; by; now (); Categories.version () ]
        |> ignore
        Store.emit s "ClassificationCorrected" e.FirmId e.Id null
        // After its cycle closed: a lower charge is credited next cycle; a higher one is never charged.
        match s.TryOne "SELECT cycle_until FROM usage WHERE event_id=$1" [ e.Id ] (fun r -> Read.int64 r 0) with
        | Some cycleUntil when before.Is = "billable" && not (Billing.conversationOf s e).Billable ->
            s.Execute
                "INSERT INTO credits (event_id, cycle_from, reason, at) VALUES ($1,$2,$3,$4)"
                [ e.Id; cycleUntil; $"corrected to {category} after the cycle closed"; now () ]
            |> ignore
            Store.emit s "UsageCredited" e.FirmId e.Id null
        | _ -> ()
        viewIn s eventId)
