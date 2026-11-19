/// Call categories: what a contact-type name means (new matter, court call,
/// line handling, ...). The rules live in docs/call-categories/taxonomy.json,
/// embedded at build time; this is the same algorithm as
/// tools/call_categories.py, and the reference-lead test keeps the two agreeing.
module LeadIntegrations.Categories

open System
open System.IO
open System.Reflection
open System.Text.Json
open System.Text.RegularExpressions
open LeadIntegrations.Domain

type CallMapping =
    { Category: string
      PracticeArea: string // null unless new-matter
      Modifiers: string[]
      Crm: string } // send | skip | review | never; null when unmapped

type CallCategory =
    { Id: string
      Label: string
      Description: string
      IsCallCategory: bool
      Crm: string
      Share: Nullable<float>
      Examples: string[] }

type PracticeArea =
    { Id: string
      Label: string
      Occurrences: Nullable<int>
      Examples: string[] }

type Modifier =
    { Id: string
      Label: string
      Occurrences: Nullable<int> }

type Agreement =
    { ActiveTypes: int
      ActiveTypesClassifiedNewMatter: int
      NewMatterTypes: int
      NewMatterTypesActive: int }

type Taxonomy =
    { Categories: CallCategory[]
      PracticeAreas: PracticeArea[]
      Modifiers: Modifier[]
      Agreement: Agreement }

type ReferenceLead =
    { Id: string
      Why: string
      Expect: CallMapping
      Mapped: CallMapping
      Matches: bool
      Lead: Lead }

let private resource (name: string) =
    let asm = Assembly.GetExecutingAssembly()
    use s = asm.GetManifestResourceStream name
    if isNull s then failwith $"embedded resource missing: {name}"
    use r = new StreamReader(s)
    r.ReadToEnd()

let private rx (p: string) = Regex(p, RegexOptions.CultureInvariant)

let private str (e: JsonElement) (name: string) =
    match e.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
    | _ -> null

type private Rule =
    { Id: string
      Pattern: Regex // null for the special rules
      Special: string } // MODIFIERS_ONLY | NAME_ONLY | null

let private taxonomyDoc = lazy (JsonDocument.Parse(resource "taxonomy.json").RootElement.Clone())
let private coverageDoc = lazy (JsonDocument.Parse(resource "coverage.json").RootElement.Clone())

let private modifierRules =
    lazy
        [| for m in taxonomyDoc.Value.GetProperty("modifiers").EnumerateArray() -> str m "id", rx (str m "pattern") |]

let private areaRules =
    lazy
        [| for a in taxonomyDoc.Value.GetProperty("practiceAreas").EnumerateArray() -> str a "id", rx (str a "pattern") |]

let private categoryRules =
    lazy
        [| for c in taxonomyDoc.Value.GetProperty("categories").EnumerateArray() ->
               let p = str c "pattern"
               let special = if p = "MODIFIERS_ONLY" || p = "NAME_ONLY" then p else null
               { Id = str c "id"; Pattern = (if isNull special then rx p else null); Special = special } |]

let private fallbackId =
    lazy
        (taxonomyDoc.Value.GetProperty("categories").EnumerateArray()
         |> Seq.tryFind (fun c ->
             match c.TryGetProperty "practiceAreaFallback" with
             | true, v -> v.ValueKind = JsonValueKind.True
             | _ -> false)
         |> Option.map (fun c -> str c "id"))

let private crmOf =
    lazy (dict [ for c in taxonomyDoc.Value.GetProperty("categories").EnumerateArray() -> str c "id", str c "crm" ])

let private filler = lazy (set ((str taxonomyDoc.Value "filler").Split(' ', StringSplitOptions.RemoveEmptyEntries)))

let private allRules =
    lazy
        [| yield! modifierRules.Value |> Array.map snd
           yield! areaRules.Value |> Array.map snd
           yield! categoryRules.Value |> Array.choose (fun r -> Option.ofObj r.Pattern) |]

let private handling = set [ "after-hours"; "business-hours"; "patch"; "no-patch"; "away" ]
let private nonAlnum = Regex("[^a-z0-9]+", RegexOptions.CultureInvariant)
let private spaces = Regex(@"\s+", RegexOptions.CultureInvariant)
let private letters = Regex("[A-Za-z]+", RegexOptions.CultureInvariant)

/// Lowercase, punctuation to spaces, padded so `\b` patterns see whole words.
let private norm (s: string) =
    " " + spaces.Replace(nonAlnum.Replace((if isNull s then "" else s).ToLowerInvariant(), " "), " ").Trim() + " "

/// Words left once every rule's matches and the filler words are removed,
/// written like a name (Title case, 3+ letters): "General Calls Andrea".
let private nameTokens (raw: string) =
    let raw = if isNull raw then "" else raw
    let rest = allRules.Value |> Array.fold (fun (t: string) r -> r.Replace(t, " ")) (norm raw)
    let left =
        rest.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        |> Array.filter (fun w -> not (filler.Value.Contains w) && not (w |> Seq.forall Char.IsDigit))
        |> Collections.Generic.HashSet
    let words = [| for m in letters.Matches raw -> m.Value |]
    // The source system cuts names at 25 characters: the last word is often a stub ("Accide").
    if raw.Trim().Length >= 24 && words.Length > 0 then left.Remove(words[words.Length - 1].ToLowerInvariant()) |> ignore
    words
    |> Array.filter (fun w ->
        left.Contains(w.ToLowerInvariant()) && w.Length >= 3 && Char.IsUpper w[0] && not (w |> Seq.forall Char.IsUpper))

let private result (category: string) (area: string) (mods: string list) (names: string[]) =
    let mods = if names.Length > 0 && category <> "staff-routing" then mods @ [ "named-person" ] else mods
    { Category = category
      PracticeArea = area
      Modifiers = Array.ofList mods
      Crm = (match crmOf.Value.TryGetValue category with | true, v -> v | _ -> null) }

let private classifyRules (raw: string) =
    let text = norm raw
    let mods =
        modifierRules.Value |> Array.filter (fun (_, r) -> r.IsMatch text) |> Array.map fst |> List.ofArray
    let mods = if List.contains "no-patch" mods then List.filter ((<>) "patch") mods else mods
    let names = nameTokens raw
    // Handling words ("AH", "patch", "New Years") say how, not what: drop them before
    // matching categories. Language, channel and custody words stay: they carry meaning.
    let stripped =
        modifierRules.Value
        |> Array.filter (fun (id, _) -> handling.Contains id)
        |> Array.fold (fun (t: string) (_, r) -> r.Replace(t, " ")) text
    let area = areaRules.Value |> Array.tryFind (fun (_, r) -> r.IsMatch stripped) |> Option.map fst
    let modSet = set mods
    let rec go (rules: Rule list) =
        match rules with
        | [] -> result "unmapped" null mods names
        | r :: rest ->
            if r.Id = "general" && r.Pattern.IsMatch stripped then result r.Id null mods names
            elif r.Id = "general" && fallbackId.Value.IsSome && area.IsSome && area.Value <> "other" then
                // A name that only names a practice area ("Personal Injury") is a new matter.
                result fallbackId.Value.Value area.Value mods names
            elif r.Special = "MODIFIERS_ONLY" then
                if not (Set.intersect modSet handling).IsEmpty && (modSet - handling - set [ "in-custody" ]).IsEmpty && names.Length = 0 then
                    result r.Id null mods names
                else go rest
            elif r.Special = "NAME_ONLY" then
                if names.Length > 0 then result r.Id null mods names else go rest
            elif r.Pattern.IsMatch(if r.Id = "system" then text else stripped) then
                let pa = if Some r.Id = fallbackId.Value then defaultArg area "unspecified" else null
                result r.Id pa mods names
            elif r.Id = "general" && not (Set.intersect modSet (set [ "spanish"; "other-language"; "webchat" ])).IsEmpty then
                // "Spanish Calls", "WebChat": a kind of caller or channel, nothing narrower.
                result r.Id null mods names
            else go rest
    go (List.ofArray categoryRules.Value)

/// The category, practice area and modifiers of a contact-type / call-type name.
let classify (raw: string) : CallMapping =
    let r = classifyRules raw
    let prefix = str taxonomyDoc.Value "systemPrefix"
    if not (String.IsNullOrEmpty prefix) && (if isNull raw then "" else raw).Trim().StartsWith prefix
       && (r.Category = "staff-routing" || r.Category = "unmapped") then
        { r with Category = "system"; PracticeArea = null; Modifiers = r.Modifiers |> Array.filter ((<>) "named-person"); Crm = crmOf.Value["system"] }
    else r

// ----- The taxonomy as the API shows it -----

let private examplesFor (bucket: string) =
    match coverageDoc.Value.GetProperty("examples").TryGetProperty bucket with
    | true, v -> [| for e in v.EnumerateArray() -> str e "name" |]
    | _ -> [||]

let private intAt (path: string list) =
    let rec walk (e: JsonElement) path =
        match path with
        | [] -> if e.ValueKind = JsonValueKind.Number then Nullable(e.GetInt32()) else Nullable()
        | p :: rest ->
            match e.TryGetProperty(p: string) with
            | true, v -> walk v rest
            | _ -> Nullable()
    walk coverageDoc.Value path

let private shareOf (id: string) =
    match coverageDoc.Value.GetProperty("byCategory").TryGetProperty id with
    | true, v -> Nullable(v.GetProperty("share").GetDouble())
    | _ -> Nullable()

let taxonomy =
    lazy
        (let t = taxonomyDoc.Value
         let agree k = (intAt [ "agreementWithLeadFlag"; k ]).GetValueOrDefault()
         { Categories =
             [| for c in t.GetProperty("categories").EnumerateArray() ->
                    let id = str c "id"
                    { Id = id
                      Label = str c "label"
                      Description = str c "description"
                      IsCallCategory = c.GetProperty("isCallCategory").GetBoolean()
                      Crm = str c "crm"
                      Share = shareOf id
                      Examples = (if id = "new-matter" then examplesFor "unspecified" else examplesFor id) } |]
           PracticeAreas =
             [| for a in t.GetProperty("practiceAreas").EnumerateArray() ->
                    let id = str a "id"
                    { Id = id
                      Label = str a "label"
                      Occurrences = intAt [ "newMatterByPracticeArea"; id ]
                      Examples = examplesFor id } |]
           Modifiers =
             [| for m in t.GetProperty("modifiers").EnumerateArray() do
                    let id = str m "id"
                    yield ({ Id = id; Label = str m "label"; Occurrences = intAt [ "byModifier"; id ] }: Modifier)
                yield { Id = "named-person"; Label = "Names a person"; Occurrences = intAt [ "byModifier"; "named-person" ] } |]
           Agreement =
             { ActiveTypes = agree "activeTypes"
               ActiveTypesClassifiedNewMatter = agree "activeTypesClassifiedNewMatter"
               NewMatterTypes = agree "newMatterTypes"
               NewMatterTypesActive = agree "newMatterTypesActive" } })

// ----- Reference leads -----

let private sameMapping (a: CallMapping) (b: CallMapping) =
    a.Category = b.Category && a.PracticeArea = b.PracticeArea && set a.Modifiers = set b.Modifiers && a.Crm = b.Crm

let referenceLeads =
    lazy
        (let doc = JsonDocument.Parse(resource "reference-leads.json").RootElement
         [| for r in doc.GetProperty("leads").EnumerateArray() ->
                let lead = Json.deserialize<Lead> (r.GetProperty("lead").GetRawText())
                let e = r.GetProperty "expect"
                let expect =
                    { Category = str e "category"
                      PracticeArea = str e "practiceArea"
                      Modifiers = [| for m in e.GetProperty("modifiers").EnumerateArray() -> m.GetString() |]
                      Crm = str e "crm" }
                let mapped = classify lead.CallType
                { Id = str r "id"
                  Why = str r "why"
                  Expect = expect
                  Mapped = mapped
                  Matches = sameMapping expect mapped
                  Lead = lead } |])

let referenceLead (id: string) =
    referenceLeads.Value
    |> Array.tryFind (fun r -> r.Id = id)
    |> Option.defaultWith (fun () -> fail 404 $"no such reference lead: {id}")
