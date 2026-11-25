/// One canonical lead → the body each destination receives. The CRM shapes
/// follow what the legacy system sends each CRM today; the standard body is
/// the lead contract in docs/api.md.
module LeadIntegrations.Payloads

open System
open System.Collections.Generic
open System.Text.Json.Nodes
open LeadIntegrations.Domain

type Built = { ContentType: string; Body: string }

/// A custom label as a template/mapping key: "Date of accident?" → "date_of_accident".
let slug (label: string) =
    let chars =
        (if isNull label then "" else label.Trim().ToLowerInvariant())
        |> Seq.map (fun c -> if Char.IsLetterOrDigit c then c else '_')
        |> Seq.toArray
    String(chars).Trim('_') |> fun s -> Text.RegularExpressions.Regex.Replace(s, "_+", "_")

let private orNa (s: string) = if String.IsNullOrWhiteSpace s then "N/A" else s

let fullName (l: Lead) = $"{l.FirstName} {l.LastName}".Trim()

/// The message plus every intake answer, as CRMs without custom fields get it.
let fullMessage (l: Lead) =
    let answers =
        l.Custom
        |> Array.filter (fun c -> not (String.IsNullOrWhiteSpace c.Data))
        |> Array.map (fun c -> $"{c.Label}: {c.Data}")
    String.Join("\n", Array.append [| l.Message |] answers).Trim()

/// Everything a template or mapping row may use.
let vars (eventId: string) (firmName: string) (l: Lead) : IDictionary<string, string> =
    let d = Dictionary<string, string>()
    d["first_name"] <- l.FirstName
    d["last_name"] <- l.LastName
    d["full_name"] <- fullName l
    d["phone"] <- l.Phone
    d["email"] <- l.Email
    d["call_type"] <- l.CallType
    d["message"] <- l.Message
    d["full_message"] <- fullMessage l
    d["source"] <- l.Source
    d["webchat"] <- (if l.Webchat then "true" else "false")
    d["event_id"] <- eventId
    d["firm_name"] <- firmName
    for c in l.Custom do
        d[$"custom.{slug c.Label}"] <- c.Data
    d

/// The fields a mapping row can map from, with labels, for a given set of custom labels.
let sourceFields (customLabels: string[]) =
    Array.append
        [| "first_name", "First name"
           "last_name", "Last name"
           "full_name", "Full name"
           "phone", "Phone"
           "email", "Email"
           "call_type", "Call type"
           "message", "Message"
           "full_message", "Message with intake answers"
           "source", "Source"
           "event_id", "Lead id" |]
        (customLabels |> Array.map (fun l -> $"custom.{slug l}", l))

let rowsToTemplate (rows: MapRow[]) =
    let o = JsonObject()
    for r in rows do
        if not (String.IsNullOrWhiteSpace r.From) && not (String.IsNullOrWhiteSpace r.To) then
            o[r.To.Trim()] <- JsonValue.Create("{{" + r.From + "}}")
    o.ToJsonString(Json.pretty)

let private str (s: string) : JsonNode = JsonValue.Create(if isNull s then "" else s)

let private obj (pairs: (string * JsonNode) list) =
    let o = JsonObject()
    for k, v in pairs do
        o[k] <- v
    o :> JsonNode

let private arr (items: JsonNode seq) = JsonArray(Seq.toArray items) :> JsonNode

let private json (node: JsonNode) =
    Ok { ContentType = "application/json"; Body = node.ToJsonString(Json.options) }

let standardBody (eventId: string) (l: Lead) : JsonNode =
    obj
        [ "schema_version", JsonValue.Create 1
          "event", str "lead.created"
          "event_id", str eventId
          "lead_id", str eventId
          "first_name", str l.FirstName
          "last_name", str l.LastName
          "full_name", str (fullName l)
          "phone", str l.Phone
          "email", str l.Email
          "call_type", str l.CallType
          "message", str l.Message
          "source", str l.Source
          "webchat", JsonValue.Create l.Webchat
          "custom", arr (l.Custom |> Seq.map (fun c -> obj [ "label", str c.Label; "data", str c.Data ])) ]

let private setting (settings: IDictionary<string, string>) key =
    match settings.TryGetValue key with
    | true, v -> v
    | _ -> ""

/// The kinds a person reads, so they can carry the lead's acknowledgement link (RFC-008 §3.1).
let acknowledgeable = set [ "email"; "sms" ]

/// Add the acknowledgement link to an email or SMS body: as `ackUrl`, and at the end of its text.
/// Opening the message proves nothing; following the link and confirming does.
let withAcknowledgement (kind: string) (url: string) (built: Built) : Built =
    if not (acknowledgeable.Contains kind) || String.IsNullOrEmpty url then built
    else
        match System.Text.Json.Nodes.JsonNode.Parse built.Body with
        | :? System.Text.Json.Nodes.JsonObject as o ->
            let text = match o["text"] with null -> "" | t -> t.GetValue<string>()
            let line = if kind = "sms" then $" Confirm you have it: {url}" else $"\n\nConfirm you have this lead: {url}"
            o["text"] <- System.Text.Json.Nodes.JsonValue.Create(text + line)
            o["ackUrl"] <- System.Text.Json.Nodes.JsonValue.Create url
            { built with Body = o.ToJsonString() }
        | _ -> built

/// Build the request body. `credentialPresent name` says whether the connection
/// has that credential on file (the value itself is never held: shape-only import).
let build
    (kind: string)
    (settings: IDictionary<string, string>)
    (credentialPresent: string -> bool)
    (template: string)
    (eventId: string)
    (firmName: string)
    (l: Lead)
    : Result<Built, string[]> =
    match kind with
    | "answering-legal" ->
        obj
            [ "first_name", str l.FirstName
              "last_name", str l.LastName
              "email", str l.Email
              "phone", str l.Phone
              "message_id", str eventId
              "message", str l.Message
              "custom",
              arr (
                  l.Custom
                  |> Seq.map (fun c -> obj [ "lookup_key", str (slug c.Label); "label", str c.Label; "data", str c.Data ])
              ) ]
        |> json
    | "clio-grow" ->
        obj
            [ ("inbox_lead",
               obj
                  [ "from_first", str (orNa l.FirstName)
                    "from_last", str (orNa l.LastName)
                    "from_message", str (fullMessage l)
                    "from_email", str (orNa l.Email)
                    "from_phone", str (orNa l.Phone)
                    "from_source", str l.Source ]) ]
        |> json
    | "mycase" ->
        obj
            [ "first_name", str l.FirstName
              "last_name", str l.LastName
              "email", str l.Email
              "cell_phone_number", str l.Phone
              "lead_details", str (fullMessage l) ]
        |> json
    | "lawmatics-form" ->
        obj
            [ "first_name", str l.FirstName
              "last_name", str l.LastName
              "email", str l.Email
              "phone", str l.Phone
              "message", str l.Message
              "custom", obj [ for c in l.Custom -> c.Label, str c.Data ]
              "answer_id", str eventId ]
        |> json
    | "lawmatics-form-v2" ->
        obj
            [ "first_name", str l.FirstName
              "last_name", str l.LastName
              "email", str l.Email
              "phone", str l.Phone
              "message", str l.Message
              "full_message", str (fullMessage l) ]
        |> json
    | "lawmatics-api" ->
        obj
            [ "case_title", str ""
              "first_name", str l.FirstName
              "last_name", str l.LastName
              "match_contact_by", str "phone"
              "email", str l.Email
              "phone", str l.Phone
              "notes", arr [ obj [ "name", str "Message Details"; "body", str (fullMessage l) ] ]
              // The legacy system resolves each label to a Lawmatics custom-field id at send time.
              "custom_fields", arr (l.Custom |> Seq.map (fun c -> obj [ "label", str c.Label; "value", str c.Data ])) ]
        |> json
    | "lead-docket" ->
        let form =
            [ "First", l.FirstName; "Last", l.LastName; "Email", l.Email; "Phone", l.Phone; "Summary", fullMessage l ]
            |> List.map (fun (k, v) -> k + "=" + Uri.EscapeDataString(if isNull v then "" else v))
            |> String.concat "&"
        Ok { ContentType = "application/x-www-form-urlencoded"; Body = form }
    | "smokeball" ->
        obj
            [ "contact", obj [ ("person", obj [ "firstName", str l.FirstName; "lastName", str l.LastName; "email", str l.Email; "phone", str l.Phone ]) ]
              "matter", obj [ "status", str "Open"; "isLead", JsonValue.Create true; "description", str (fullMessage l) ]
              "task",
              obj
                  [ "subject", str $"Call back {fullName l}"
                    "categories", arr [ str "Phone Message" ]
                    // The staff id is on file in the source but not imported; say whether it exists.
                    if credentialPresent "api_key" then "staffId", str "(staff id on file)" ]
              "memo", obj [ "title", str (fullName l); "text", str (fullMessage l) ] ]
        |> json
    | "practicepanther" ->
        obj
            [ "account",
              obj [ ("primary_contact", obj [ "first_name", str l.FirstName; "last_name", str l.LastName; "email", str l.Email; "phone_mobile", str l.Phone ]) ]
              "matter", obj [ "name", str $"{fullName l}: {l.CallType}"; "status", str "Open" ]
              "call_log", obj [ "subject", str $"Call from {fullName l}"; "call_direction", str "Inbound"; "notes", str (fullMessage l) ] ]
        |> json
    | "lawruler" ->
        obj [ "first_name", str l.FirstName; "last_name", str l.LastName; "phone", str l.Phone; "email", str l.Email; "notes", str (fullMessage l) ]
        |> json
    | "hubspot" ->
        let field n (v: string) = obj [ "name", str n; "value", str v ]
        obj
            [ "fields", arr [ field "firstname" l.FirstName; field "lastname" l.LastName; field "email" l.Email; field "phone" l.Phone; field "company" firmName ]
              "context", obj [ ("pageName", str "Answering AI intake") ] ]
        |> json
    | "zapier-webhook" -> json (standardBody eventId l)
    | "webhook" ->
        if String.IsNullOrWhiteSpace template then json (standardBody eventId l)
        else
            let r = Template.render template (vars eventId firmName l)
            if r.Ok then Ok { ContentType = "application/json"; Body = r.Body } else Error r.Errors
    | "email" ->
        obj
            [ "to", str (setting settings "recipients")
              "subject", str $"New lead: {fullName l} ({l.CallType})"
              "text", str (fullMessage l) ]
        |> json
    | "sms" -> obj [ "to", str (setting settings "to"); "text", str $"New lead: {fullName l} {l.Phone}. {l.Message}" ] |> json
    | "slack" -> obj [ ("text", str $"*New lead* {fullName l} ({l.Phone})\n{fullMessage l}") ] |> json
    | other -> Error [| $"no payload builder for \"{other}\"" |]
