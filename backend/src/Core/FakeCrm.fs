/// The local stand-ins for each CRM. Imported connections never reach a real
/// CRM (shape-only import); they post here, and this checks the body the way
/// the CRM would, so a wrong field name or a missing value is a real refusal.
module LeadIntegrations.FakeCrm

open System
open System.Text.Json
open System.Web

let private lookup (root: JsonElement) (path: string) : string option =
    // "fields[firstname]": a HubSpot-style array of { name, value }.
    let m = Text.RegularExpressions.Regex.Match(path, @"^(\w+)\[(\w+)\]$")
    if m.Success then
        match root.TryGetProperty m.Groups[1].Value with
        | true, arr when arr.ValueKind = JsonValueKind.Array ->
            arr.EnumerateArray()
            |> Seq.tryFind (fun f ->
                match f.TryGetProperty "name" with
                | true, n -> n.GetString() = m.Groups[2].Value
                | _ -> false)
            |> Option.bind (fun f ->
                match f.TryGetProperty "value" with
                | true, v -> Some(v.ToString())
                | _ -> None)
        | _ -> None
    else
        let rec walk (el: JsonElement) (parts: string list) =
            match parts with
            | [] ->
                match el.ValueKind with
                | JsonValueKind.Null
                | JsonValueKind.Undefined -> None
                | JsonValueKind.String -> Some(el.GetString())
                | _ -> Some(el.ToString())
            | p :: rest ->
                if el.ValueKind <> JsonValueKind.Object then None
                else
                    match el.TryGetProperty p with
                    | true, child -> walk child rest
                    | _ -> None
        walk root (path.Split '.' |> List.ofArray)

/// Fields a lab receiver URL asks for on top of the kind's own: `?require=Phone,Email`.
/// A plain webhook requires nothing by itself; this is how the lab plays a receiver that does.
let requiredByQuery (query: string) : string list =
    let v = HttpUtility.ParseQueryString(if isNull query then "" else query)["require"]
    if String.IsNullOrWhiteSpace v then [] else v.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries) |> List.ofArray

let requiredByUrl (url: string) : string list =
    match Uri.TryCreate((if isNull url then "" else url), UriKind.Absolute) with
    | true, u -> requiredByQuery u.Query
    | _ -> []

/// Problems with the body, empty when the receiver would accept it. `extra`: fields
/// required on top of the kind's own (see `requiredByUrl`).
let checkWith (extra: string list) (kind: string) (contentType: string) (body: string) : string list =
    match Catalog.tryFind kind with
    | None -> [ $"no such CRM: {kind}" ]
    | Some k ->
        let required = (k.PayloadFields |> List.ofArray) @ extra |> List.distinct
        let isForm = not (isNull contentType) && contentType.StartsWith "application/x-www-form-urlencoded"
        if isForm then
            let form = HttpUtility.ParseQueryString(if isNull body then "" else body)
            required
            |> List.choose (fun f -> if String.IsNullOrWhiteSpace form[f] then Some $"missing {f}" else None)
        else
            try
                use doc = JsonDocument.Parse(if String.IsNullOrEmpty body then "" else body)
                required
                |> List.choose (fun f ->
                    match lookup doc.RootElement f with
                    | Some v when not (String.IsNullOrWhiteSpace v) -> None
                    | _ -> Some $"missing {f}")
            with :? JsonException as e ->
                [ $"not JSON: {e.Message}" ]

/// Problems with the body, empty when the CRM would accept it.
let check (kind: string) (contentType: string) (body: string) : string list = checkWith [] kind contentType body
