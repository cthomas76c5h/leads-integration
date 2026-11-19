/// Which leads a destination takes, and the decision for one lead (RFC-009).
/// Pure: the firm, the destination and the lead in, a RoutingDecision out.
/// Dispatch records it with the delivery; the simulator applies the same function,
/// so the two cannot drift.
///
/// Each destination's rule is evaluated on its own: no order, no first match, and
/// one destination's decision never changes another's.
module LeadIntegrations.Routing

open System
open LeadIntegrations.Domain

/// Settings keys holding a destination's rule: comma-separated taxonomy ids.
let categoriesKey = "routeCategories"
let practiceAreasKey = "routePracticeAreas"

type Rule =
    /// The lab's rule from before category routing, kept until a destination's rule is
    /// seeded: a CRM takes the firm's active call types (free text); other kinds take every call.
    | LegacyCallTypes
    /// The call categories it takes; practice areas narrow new matters ([] = every one).
    | ByCategory of categories: string list * practiceAreas: string list

/// "Personal Injury" and "personal-injury" are the same contact type (api-2
/// strips punctuation and case before matching).
let normalizeType (s: string) =
    (if isNull s then "" else s).ToLowerInvariant() |> String.filter Char.IsLetterOrDigit

let private setting (c: Connection) key =
    match c.Settings.TryGetValue key with
    | true, v when not (isNull v) -> v
    | _ -> ""

let private ids (s: string) =
    s.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries) |> List.ofArray

let private isCrm (c: Connection) =
    Catalog.tryFind c.Kind |> Option.exists (fun k -> k.Category = "crm")

let ruleOf (c: Connection) : Rule =
    match ids (setting c categoriesKey) with
    | [] -> LegacyCallTypes
    | categories -> ByCategory(categories, ids (setting c practiceAreasKey))

let takesChatLeads (c: Connection) = setting c "sendWebchats" <> "false"

let private joined (xs: string list) = String.Join(", ", xs)

/// A CRM's rule seeded from the firm's call types, so nothing it receives changes at
/// switch-over (answers.md, routing): the categories whose call types are all active,
/// with new matters narrowed to practice areas when the firm splits them by area.
/// None when the firm's choice cannot be said in categories (a category split between
/// active and inactive names, or no active types): the destination keeps the legacy
/// rule until Onboarding reviews it. tools/routing_equivalence.py reports the same split.
let seed (types: ContactType[]) : (string list * string list) option =
    let classified = types |> Array.map (fun t -> Categories.classify t.Name, t.Active)
    let allActive xs = xs |> Array.forall snd
    let unanimous xs = allActive xs || xs |> Array.forall (snd >> not)
    if not (classified |> Array.exists snd) then None
    else
        let byCategory = classified |> Array.groupBy (fun (m, _) -> m.Category)
        if byCategory |> Array.exists (fun (c, xs) -> c <> "new-matter" && not (unanimous xs)) then None
        else
            let whole = byCategory |> Array.filter (snd >> allActive) |> Array.map fst |> List.ofArray
            match byCategory |> Array.tryFind (fun (c, _) -> c = "new-matter") with
            | Some(_, xs) when not (unanimous xs) ->
                let areas = xs |> Array.groupBy (fun (m, _) -> m.PracticeArea)
                if areas |> Array.forall (snd >> unanimous) then
                    Some("new-matter" :: whole, areas |> Array.filter (snd >> allActive) |> Array.map fst |> List.ofArray)
                else None
            | _ -> Some(whole, [])

/// The rule in words, as the firm reads it.
let describe (c: Connection) =
    let rule =
        match ruleOf c with
        | LegacyCallTypes when isCrm c -> "the firm's active call types"
        | LegacyCallTypes -> "every call"
        | ByCategory(categories, []) -> joined categories
        | ByCategory(categories, areas) -> $"{joined categories}; new matters only in {joined areas}"
    if takesChatLeads c then rule else rule + "; no chat leads"

/// The lead with every caller field filled in, to tell a gap in the lead from a gap in the setup.
let private complete (l: Lead) =
    let orElse (v: string) fill = if String.IsNullOrWhiteSpace v then fill else v
    { l with
        FirstName = orElse l.FirstName "Caller"
        LastName = orElse l.LastName "Caller"
        Phone = orElse l.Phone "+15555550100"
        Email = orElse l.Email "caller@example.com"
        Message = orElse l.Message "Message" }

/// Fields this destination requires that this lead cannot fill (a hang-up to a CRM
/// that needs a name): missing from its body, but present once the caller's details
/// are filled in. A field missing either way is the destination's setup (a mapping
/// gap), not the lead's, and is left for the send to report. What each kind requires
/// is its receiver contract (docs/contracts/receivers/), held in FakeCrm.
let leadProblem (c: Store.ConnectionRow) (lead: Lead) : string list =
    let conn = c.Connection
    let missing (l: Lead) =
        match Payloads.build conn.Kind conn.Settings (fun _ -> true) c.Template "" conn.FirmName l with
        | Error _ -> None
        | Ok b -> Some(FakeCrm.checkWith (FakeCrm.requiredByUrl conn.DeliveryUrl) conn.Kind b.ContentType b.Body |> Set.ofList)
    match missing lead, missing (complete lead) with
    | Some actual, Some whenComplete -> Set.difference actual whenComplete |> Set.toList
    | _ -> []

/// The decision for one production lead and one destination. Test leads are not
/// routed: they go only to the destination under test.
let decide (firm: Store.FirmRow) (c: Store.ConnectionRow) (lead: Lead) : RoutingDecision =
    let conn = c.Connection
    let m = Categories.classify lead.CallType
    let decision outcome matchedOn reason =
        { Outcome = outcome
          Rule = describe conn
          Category = m.Category
          PracticeArea = m.PracticeArea
          TaxonomyVersion = Categories.version ()
          MatchedOn = matchedOn
          Reason = reason }
    let routed =
        if lead.Webchat && not (takesChatLeads conn) then
            Error "chat leads are switched off for this destination"
        else
            match ruleOf conn with
            | LegacyCallTypes when isCrm conn ->
                let active = firm.ContactTypes |> Array.filter _.Active |> Array.map (fun t -> normalizeType t.Name)
                if active.Length > 0 && not (Array.contains (normalizeType lead.CallType) active) then
                    Error $"call type \"{lead.CallType}\" is not routed: it matches none of the firm's active contact types"
                else Ok "call type"
            | LegacyCallTypes -> Ok "every call"
            | ByCategory(categories, areas) ->
                if not (List.contains m.Category categories) then
                    Error $"a {m.Category} call; this destination takes {joined categories}"
                elif m.Category = "new-matter" && not areas.IsEmpty && not (List.contains m.PracticeArea areas) then
                    Error $"a new matter in {m.PracticeArea}; this destination takes new matters only in {joined areas}"
                elif m.Category = "new-matter" && not areas.IsEmpty then Ok $"new-matter, {m.PracticeArea}"
                else Ok m.Category
    match routed with
    | Error reason -> decision "not-sent-routing" null reason
    | Ok matchedOn ->
        match leadProblem c lead with
        | [] -> decision "planned" matchedOn null
        | fields ->
            decision "not-sent-lead" null $"{Diagnose.leadProblemPrefix}the lead has no {joined fields}, which this destination requires"
