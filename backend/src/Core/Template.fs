/// JSON body templates: {{field}} and {{field|default:'text'}} (webhook-lab
/// template.ts). Values are JSON-escaped, so a placeholder sits inside a JSON
/// string. An unknown `custom.*` field renders empty (answers differ per call
/// type); any other unknown field is a typo and an error.
module LeadIntegrations.Template

open System.Collections.Generic
open System.Text.Json
open System.Text.RegularExpressions

type Rendered =
    { Ok: bool
      Body: string
      Errors: string[]
      Used: string[] }

let private placeholder =
    Regex(@"\{\{\s*([a-zA-Z0-9_.\- ]+?)\s*(?:\|\s*default:'([^']*)')?\s*\}\}", RegexOptions.Compiled)

let private escape (s: string) =
    let q = JsonSerializer.Serialize(s, Json.options)
    q.Substring(1, q.Length - 2)

let render (template: string) (vars: IDictionary<string, string>) : Rendered =
    let errors = ResizeArray<string>()
    let used = HashSet<string>()
    let text =
        placeholder.Replace(
            template,
            MatchEvaluator(fun m ->
                let name = m.Groups[1].Value
                let fallback = if m.Groups[2].Success then Some m.Groups[2].Value else None
                match vars.TryGetValue name with
                | true, raw ->
                    used.Add name |> ignore
                    let raw = if isNull raw then "" else raw
                    escape (if raw = "" then defaultArg fallback "" else raw)
                | _ when name.StartsWith "custom." -> escape (defaultArg fallback "")
                | _ ->
                    errors.Add $"unknown field {{{{{name}}}}}"
                    "")
        )
    let body =
        match Json.normalize text with
        | Ok compact -> compact
        | Error e ->
            errors.Add $"not valid JSON: {e}"
            text
    { Ok = errors.Count = 0
      Body = body
      Errors = errors.ToArray()
      Used = Seq.toArray used }
