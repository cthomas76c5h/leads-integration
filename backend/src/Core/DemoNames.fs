/// Fictional firm names for demos and screenshots: the lab's imported firms carry real
/// names, which must not travel in a deck. Every firm gets a stable invented name
/// (the same firm, the same name, every run); a trades business keeps a trades name,
/// so the data still shows the business is broader than law. Re-importing restores
/// the real names.
module LeadIntegrations.DemoNames

open System
open LeadIntegrations.Db

let private surnames =
    [| "Alder"; "Bellamy"; "Brennan"; "Castillo"; "Corcoran"; "Dawson"; "Delacroix"; "Ellery"; "Fairbanks"; "Galloway"
       "Hartwell"; "Ibarra"; "Jansen"; "Kowalski"; "Lindqvist"; "Marlowe"; "Navarro"; "Okafor"; "Petrakis"; "Quintero"
       "Rasmussen"; "Sorensen"; "Thackeray"; "Underwood"; "Valdez"; "Whitlock"; "Xiong"; "Yamada"; "Zielinski"; "Abernathy"
       "Calloway"; "Dunmore"; "Everhart"; "Finch"; "Granger"; "Holloway"; "Iverson"; "Kingsley"; "Lockhart"; "Merriweather"
       "Northcott"; "Oakes"; "Pemberton"; "Redfield"; "Stanhope"; "Thornbury"; "Vance"; "Wexford"; "Ashby"; "Blackwood"
       "Ainsley"; "Barrow"; "Carver"; "Danforth"; "Easton"; "Fenwick"; "Garrison"; "Halloran"; "Ingram"; "Jarvis"
       "Kendall"; "Langley"; "Mercer"; "Nolan"; "Osborne"; "Prescott"; "Radcliffe"; "Sutton"; "Talbot"; "Upton"
       "Vaughn"; "Warrick"; "Yardley"; "Ashford"; "Beckett"; "Crane"; "Drummond"; "Ellison"; "Forsyth"; "Gresham"
       "Hale"; "Irwin"; "Kerr"; "Lowell"; "Mayfield"; "Norwood"; "Orton"; "Paxton"; "Rowe"; "Sinclair"
       "Tilden"; "Wilder"; "Ackerman"; "Brandt"; "Collier"; "Devereux"; "Emery"; "Foley"; "Grady"; "Hensley" |]

let private firstNames =
    [| "Avery"; "Jordan"; "Morgan"; "Riley"; "Casey"; "Quinn"; "Rowan"; "Elena"; "Marcus"; "Priya"; "Tomas"; "Hannah"
       "Diego"; "Samuel"; "Aisha"; "Lucia"; "Nadia"; "Owen"; "Grace"; "Felix"; "Leah"; "Andre"; "Camila"; "Victor" |]

let private lawForms =
    [| fun (a: string) (b: string) (_: string) -> $"{a} & {b} LLP"
       fun a _ _ -> $"{a} Law Group"
       fun a _ f -> $"Law Office of {f} {a}"
       fun a _ _ -> $"{a} Legal"
       fun a _ _ -> $"{a} Injury Lawyers"
       fun a b _ -> $"{a}, {b} & Partners"
       fun a _ _ -> $"{a} Family Law"
       fun a _ f -> $"{f} {a}, Attorney at Law"
       fun a _ _ -> $"{a} Defense Firm"
       fun a b _ -> $"{a} {b} PLLC" |]

let private tradeForms =
    [| fun (a: string) -> $"{a} Heating & Air"
       fun a -> $"{a} Plumbing"
       fun a -> $"{a} Home Services" |]

let private isTrade (name: string) =
    let n = (if isNull name then "" else name).ToUpperInvariant()
    [ "HEATING"; "PLUMB"; "HVAC"; " AIR"; "COOLING"; "ELECTRIC"; "ROOF"; "CONTRACT"; "INVESTIGAT"; "HOME SERVICES" ] |> List.exists n.Contains

/// A stable fictional name for one firm; `attempt` picks the next variation when one is taken.
let nameFor (firmId: string) (realName: string) (attempt: int) =
    let h = ((firmId |> Seq.fold (fun acc c -> (acc * 31 + int c) &&& 0x7fffffff) 17) + attempt * 7919) &&& 0x7fffffff
    let a = surnames[h % surnames.Length]
    let b = surnames[(h / 7) % surnames.Length]
    let f = firstNames[(h / 13) % firstNames.Length]
    if isTrade realName then tradeForms[(h / 17) % tradeForms.Length] a
    else lawForms[(h / 17) % lawForms.Length] a (if b = a then "Marsh" else b) f

/// Renames every firm (and its source records) to its fictional name. Returns how many.
let apply (s: Session) : int =
    let firms = s.Query "SELECT id, name FROM firms ORDER BY id" [] (fun r -> Read.str r 0, Read.str r 1)
    let used = Collections.Generic.HashSet<string>()
    for id, real in firms do
        // The next variation until one is free: no "(2)" suffixes.
        let mutable attempt = 0
        let mutable name = nameFor id real attempt
        while not (used.Add name) do
            attempt <- attempt + 1
            name <- nameFor id real attempt
        s.Execute "UPDATE firms SET name=$2 WHERE id=$1" [ id; name ] |> ignore
        s.Execute "UPDATE firm_sources SET name=$2 WHERE firm_id=$1" [ id; name ] |> ignore
    firms.Length
