/// Realistic synthetic traffic for the lab: leads published through the same
/// path as production leads (Dispatch.publish, routing applies), to firms that
/// have somewhere to deliver. Every person is invented; call types are the
/// firm's own contact-type names; intake wording comes from the reference leads
/// plus the firm's own labels. Nothing reaches a real CRM: imported connections
/// point at the local fake receivers.
module LeadIntegrations.Seed

open System
open LeadIntegrations.Domain

type Options =
    { Count: int
      RandomSeed: int
      /// Share of leads that hang up before giving details.
      HangUpRate: float
      /// Share of leads that arrive by web chat.
      ChatRate: float
      /// Only this firm, when set.
      FirmId: string }

let defaults =
    { Count = 200
      RandomSeed = 42
      HangUpRate = 0.07
      ChatRate = 0.10
      FirmId = null }

/// Call mix by category. An assumption, not measured: the imported data says
/// which kinds of calls firms name, not how often each arrives.
let callMix =
    [ "new-matter", 0.45
      "general", 0.18
      "existing-client", 0.10
      "legal-professional", 0.10
      "insurance-medical", 0.07
      "emergency", 0.03
      "jail-custody", 0.03
      "billing", 0.04 ]

let private firstNames =
    [| "Avery"; "Jordan"; "Morgan"; "Riley"; "Casey"; "Jamie"; "Quinn"; "Rowan"; "Elena"; "Marcus"; "Priya"; "Tomas"
       "Keisha"; "Hannah"; "Diego"; "Mei"; "Samuel"; "Aisha"; "Connor"; "Lucia"; "Nadia"; "Owen"; "Grace"; "Malik"
       "Sofia"; "Ethan"; "Yara"; "Caleb"; "Imani"; "Felix"; "Leah"; "Andre"; "Camila"; "Victor"; "Noor"; "Wesley" |]

let private lastNames =
    [| "Alder"; "Brennan"; "Castillo"; "Dawson"; "Ellery"; "Fairbanks"; "Galloway"; "Hartwell"; "Ibarra"; "Jansen"
       "Kowalski"; "Lindqvist"; "Mbeki"; "Navarro"; "Okonkwo"; "Petrakis"; "Quintero"; "Rasmussen"; "Sorensen"; "Thackeray"
       "Underwood"; "Valdez"; "Whitlock"; "Xiong"; "Yamada"; "Zielinski"; "Abernathy"; "Bellamy"; "Corcoran"; "Delacroix" |]

type Summary =
    { Firms: int
      Leads: int
      Deliveries: int
      Skipped: int
      ByCategory: (string * int) list
      HangUps: int
      Chats: int }

let private pickWeighted (rnd: Random) (items: (string * float) list) =
    let total = items |> List.sumBy snd
    let mutable r = rnd.NextDouble() * total
    items
    |> List.tryFind (fun (_, w) ->
        r <- r - w
        r <= 0.0)
    |> Option.map fst
    |> Option.defaultValue (fst (List.last items))

let private pick (rnd: Random) (xs: 'T[]) = xs[rnd.Next xs.Length]

/// The firm's own names for a category, without names of people (no staff names in leads).
let private ownNames (firm: Store.FirmRow) (category: string) =
    firm.ContactTypes
    |> Array.choose (fun t ->
        let m = Categories.classify t.Name
        if m.Category = category && not (Array.contains "named-person" m.Modifiers) then Some(t, m) else None)

/// One lead for this firm.
let lead (rnd: Random) (opts: Options) (firm: Store.FirmRow) : Lead * string =
    let refs = Categories.referenceLeads.Value
    let category = pickWeighted rnd callMix
    // Prefer the firm's active names: they are the calls it actually takes.
    let own = ownNames firm category
    let preferred = own |> Array.filter (fun (t, _) -> t.Active)
    let pool = if preferred.Length > 0 && rnd.NextDouble() < 0.9 then preferred else own
    let callType, mapping =
        if pool.Length > 0 then
            let t, m = pick rnd pool
            t.Name, m
        else
            // The firm has no name for this kind of call: use a reference lead's.
            let r = refs |> Array.filter (fun r -> r.Mapped.Category = category && not (Array.contains "named-person" r.Mapped.Modifiers))
            if r.Length = 0 then failwith $"call mix names '{category}' but no reference lead has that category"
            let r = pick rnd r
            r.Lead.CallType, r.Mapped
    // The reference lead closest to it: same category and practice area, else same category.
    let template =
        let same = refs |> Array.filter (fun r -> r.Mapped.Category = mapping.Category && r.Lead.FirstName <> "")
        let exact = same |> Array.filter (fun r -> r.Mapped.PracticeArea = mapping.PracticeArea)
        if exact.Length > 0 then pick rnd exact
        elif same.Length > 0 then pick rnd same
        else refs[0]
    let webchat = rnd.NextDouble() < opts.ChatRate
    let source = if webchat then "Answering AI chat" else "Answering AI"
    if rnd.NextDouble() < opts.HangUpRate then
        { FirstName = ""
          LastName = ""
          Phone = $"+1555555{rnd.Next(100, 200):D4}"
          Email = ""
          CallType = callType
          Message = "Caller hung up before giving details."
          Source = source
          Webchat = webchat
          Custom = [||] },
        mapping.Category
    else
        let first = pick rnd firstNames
        let last = pick rnd lastNames
        let fromTemplate = template.Lead.Custom |> Array.map (fun c -> c.Label) |> Set.ofArray
        // The firm's own intake questions, answered plausibly, after the template's.
        let firmFields =
            firm.CustomLabels
            |> Array.filter (fun l -> not (fromTemplate.Contains l))
            |> Array.truncate 4
            |> Array.map (fun l -> { Label = l; Data = Synthetic.answerFor l })
        { FirstName = first
          LastName = last
          Phone = $"+1555555{rnd.Next(100, 200):D4}"
          Email = (if rnd.NextDouble() < 0.7 then $"{first.ToLowerInvariant()}.{last.ToLowerInvariant()}@example.com" else "")
          CallType = callType
          Message = template.Lead.Message
          Source = source
          Webchat = webchat
          Custom = Array.append template.Lead.Custom firmFields |> Array.truncate 12 },
        mapping.Category

/// Firms a lead can reach: active, with at least one enabled, active connection.
let targets (db: Db.Database) (firmId: string) =
    Db.read db (fun s ->
        Store.allFirms s
        |> Array.filter (fun f ->
            (String.IsNullOrEmpty firmId || f.Id = firmId)
            && f.Active
            && Store.connectionsOf s f.Id |> Array.exists (fun c -> c.Connection.Enabled && c.Connection.Status = "active")))

/// Publish `opts.Count` leads spread across the target firms. The API's worker sends them.
let run (db: Db.Database) (opts: Options) : Summary =
    let firms = targets db opts.FirmId
    if firms.Length = 0 then fail 409 "no firm has an enabled, active connection to deliver to"
    let rnd = Random opts.RandomSeed
    let results =
        [ for _ in 1 .. opts.Count ->
              let firm = pick rnd firms
              let l, category = lead rnd opts firm
              let _, ds = Dispatch.publish db firm.Id l false None None
              l, category, ds ]
    let deliveries = results |> List.collect (fun (_, _, ds) -> List.ofArray ds)
    { Firms = firms.Length
      Leads = results.Length
      Deliveries = deliveries.Length
      Skipped = deliveries |> List.filter (fun d -> d.Status = "skipped") |> List.length
      ByCategory = results |> List.countBy (fun (_, c, _) -> c) |> List.sortByDescending snd
      HangUps = results |> List.filter (fun (l, _, _) -> l.FirstName = "") |> List.length
      Chats = results |> List.filter (fun (l, _, _) -> l.Webchat) |> List.length }
