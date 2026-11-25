/// A lead taken in a versioned contract: intake-lead/draft-2, the lead the call center publishes
/// (docs/contracts/intake-lead/README.md). Pure: the payload is checked against the contract's own
/// schema (an exact copy of intake's, embedded), reduced to a canonical form for the idempotency
/// check, and mapped to the lead delivery works with. Nothing here reads or writes the database.
module LeadIntegrations.LeadContract

open System
open System.IO
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Text.Json
open LeadIntegrations.Domain

let draft2 = "intake-lead/draft-2"

/// Draft 3: the lead the call center publishes, with when it was ready and which builder made it.
/// Its window to act starts at the interaction's completion (suite S-5).
let draft3 = "intake-lead/draft-3"

/// The contracts taken here, each an exact copy of intake's schema.
let contracts = [ draft2; draft3 ]

let private schemas =
    lazy
        (contracts
         |> List.map (fun c ->
             let name = c.Replace("/", ".") + ".schema.json"
             let asm = Assembly.GetExecutingAssembly()
             use s = asm.GetManifestResourceStream name
             if isNull s then failwith $"embedded resource missing: {name}"
             use r = new StreamReader(s)
             c, JsonDocument.Parse(r.ReadToEnd()).RootElement.Clone())
         |> dict)

// ----- The schema, as JSON Schema: the subset the contract uses -----

let private same (a: JsonElement) (b: JsonElement) =
    a.ValueKind = b.ValueKind
    && (match a.ValueKind with
        | JsonValueKind.String -> a.GetString() = b.GetString()
        | JsonValueKind.Number -> a.GetDecimal() = b.GetDecimal()
        | JsonValueKind.True
        | JsonValueKind.False
        | JsonValueKind.Null -> true
        | _ -> a.GetRawText() = b.GetRawText())

let private isType (t: string) (v: JsonElement) =
    match t, v.ValueKind with
    | "object", JsonValueKind.Object
    | "array", JsonValueKind.Array
    | "string", JsonValueKind.String
    | "number", JsonValueKind.Number
    | "null", JsonValueKind.Null -> true
    | "boolean", (JsonValueKind.True | JsonValueKind.False) -> true
    | "integer", JsonValueKind.Number -> v.GetDecimal() = Math.Truncate(v.GetDecimal())
    | _ -> false

let private shown (v: JsonElement) =
    let raw = v.GetRawText()
    if raw.Length > 40 then raw.Substring(0, 40) + "…" else raw

/// Every way the instance breaks the schema, as "path: problem". The keywords the contract uses:
/// $ref (to #/$defs), const, enum, type, required, properties, additionalProperties: false, items.
/// A keyword outside that set fails loudly, so a new draft can't be half-checked.
let rec private check (root: JsonElement) (s: JsonElement) (path: string) (v: JsonElement) (errors: ResizeArray<string>) =
    for p in s.EnumerateObject() do
        match p.Name with
        | "$ref" ->
            let name = p.Value.GetString().Replace("#/$defs/", "")
            check root (root.GetProperty("$defs").GetProperty name) path v errors
        | "const" ->
            if not (same p.Value v) then errors.Add $"{path}: must be {p.Value.GetRawText()}"
        | "enum" ->
            if not (p.Value.EnumerateArray() |> Seq.exists (fun e -> same e v)) then
                errors.Add $"{path}: {shown v} is not one of {p.Value.GetRawText()}"
        | "type" ->
            let types =
                if p.Value.ValueKind = JsonValueKind.Array then [ for t in p.Value.EnumerateArray() -> t.GetString() ]
                else [ p.Value.GetString() ]
            if not (types |> List.exists (fun t -> isType t v)) then
                errors.Add $"""{path}: expected {String.Join(" or ", types)}, got {v.ValueKind.ToString().ToLowerInvariant()}"""
        | "required" when v.ValueKind = JsonValueKind.Object ->
            for r in p.Value.EnumerateArray() do
                match v.TryGetProperty(r.GetString()) with
                | true, _ -> ()
                | _ -> errors.Add $"{path}: missing {r.GetString()}"
        | "properties" when v.ValueKind = JsonValueKind.Object ->
            for prop in p.Value.EnumerateObject() do
                match v.TryGetProperty prop.Name with
                | true, child -> check root prop.Value $"{path}.{prop.Name}" child errors
                | _ -> ()
        | "additionalProperties" when v.ValueKind = JsonValueKind.Object ->
            if p.Value.ValueKind = JsonValueKind.False then
                let known =
                    match s.TryGetProperty "properties" with
                    | true, ps -> set [ for x in ps.EnumerateObject() -> x.Name ]
                    | _ -> Set.empty
                for x in v.EnumerateObject() do
                    if not (known.Contains x.Name) then errors.Add $"{path}: unexpected property {x.Name}"
        | "items" when v.ValueKind = JsonValueKind.Array ->
            v.EnumerateArray() |> Seq.iteri (fun i item -> check root p.Value $"{path}[{i}]" item errors)
        | "required"
        | "properties"
        | "additionalProperties"
        | "items"
        | "$schema"
        | "$id"
        | "$defs"
        | "title"
        | "description" -> ()
        | other -> failwith $"intake-lead schema keyword not supported by the checker: {other}"

/// The payload's schema errors under a contract; empty when it is a valid lead in it.
let validateAs (contract: string) (payload: JsonElement) : string list =
    let schema = schemas.Value[contract]
    let errors = ResizeArray()
    check schema schema "$" payload errors
    List.ofSeq errors

/// The payload's schema errors under draft 2.
let validate (payload: JsonElement) : string list = validateAs draft2 payload

// ----- Canonical form: the idempotency check compares meaning, not whitespace or key order -----

let rec private writeCanonical (w: Utf8JsonWriter) (v: JsonElement) =
    match v.ValueKind with
    | JsonValueKind.Object ->
        w.WriteStartObject()
        for p in v.EnumerateObject() |> Seq.toArray |> Array.sortWith (fun a b -> String.CompareOrdinal(a.Name, b.Name)) do
            w.WritePropertyName p.Name
            writeCanonical w p.Value
        w.WriteEndObject()
    | JsonValueKind.Array ->
        w.WriteStartArray()
        for x in v.EnumerateArray() do
            writeCanonical w x
        w.WriteEndArray()
    | _ -> v.WriteTo w

/// sha256 (hex) of the payload with sorted keys and no whitespace.
let hash (payload: JsonElement) : string =
    use ms = new MemoryStream()
    (use w = new Utf8JsonWriter(ms)
     writeCanonical w payload)
    Convert.ToHexStringLower(SHA256.HashData(ms.ToArray()))

// ----- To delivery's lead -----

/// A payload that passed the schema, with what delivery keeps beside it.
type Parsed =
    { LeadId: string
      InteractionId: string
      SenderFirmId: string
      PolicyRevision: int
      PolicyApproved: bool
      /// The lead's content only: publication metadata never reaches it.
      Hash: string
      /// The contract it came in.
      Contract: string
      /// When the interaction completed (conversation.endedAt), in UTC milliseconds.
      CompletedAt: int64
      Lead: Lead }

let private strOrNull (e: JsonElement) (name: string) =
    match e.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
    | _ -> null

let private orEmpty (s: string) = if isNull s then "" else s

/// Contact fields are already in `contact`, and the message is the lead's message: not repeated as answers.
let private carriedElsewhere =
    set [ "first_name"; "last_name"; "caller_name"; "full_name"; "phone"; "phone_number"; "email"; "message" ]

let private label (field: string) =
    let words = field.Replace('_', ' ').Trim()
    if words.Length = 0 then field else string (Char.ToUpperInvariant words[0]) + words.Substring 1

/// What happened, in words, for a lead whose call left no message of its own.
let private summary (callType: string) (outcome: JsonElement) =
    let target = strOrNull outcome "target"
    let what =
        match strOrNull outcome "happened" with
        | "put-through" -> if isNull target then "Put through to the firm." else $"Put through to {target}."
        | "booked" -> "Consultation booked."
        | "message" -> "Message taken."
        | "declined" -> "Declined: the firm does not take this matter."
        | "escalated" -> "Handed to a person."
        | _ -> "No outcome reached."
    if String.IsNullOrWhiteSpace callType then what else $"{callType}. {what}"

/// The payload as delivery's lead. Only a payload `validate` accepted.
let toLead (p: JsonElement) : Lead =
    let contact = p.GetProperty "contact"
    let classification = p.GetProperty "classification"
    let callType =
        match classification.GetProperty("callType").ValueKind with
        | JsonValueKind.Object -> strOrNull (classification.GetProperty "callType") "name"
        | _ -> null
    let first, last =
        match strOrNull contact "firstName", strOrNull contact "lastName", strOrNull contact "fullName" with
        | null, null, full when not (String.IsNullOrWhiteSpace full) ->
            let parts = full.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)
            parts[0], (if parts.Length > 1 then parts[1] else "")
        | f, l, _ -> orEmpty f, orEmpty l
    let facts = [ for f in p.GetProperty("facts").EnumerateArray() -> f ]
    let answered (f: JsonElement) = strOrNull f "status" = "answered" && not (isNull (strOrNull f "value"))
    let message =
        facts
        |> List.tryFind (fun f -> strOrNull f "field" = "message" && answered f)
        |> Option.map (fun f -> strOrNull f "value")
        |> Option.defaultWith (fun () -> summary callType (p.GetProperty "outcome"))
    let channel = strOrNull (p.GetProperty "conversation") "channel"
    // Null category: the sender did not classify it. It stays unmapped; delivery never reads the name.
    let category = strOrNull classification "category"
    { FirstName = first
      LastName = last
      Phone = orEmpty (strOrNull contact "phone")
      Email = orEmpty (strOrNull contact "email")
      CallType = orEmpty callType
      Message = message
      Source = (if channel = "chat" then "Answering AI chat" else "Answering AI")
      Webchat = (channel = "chat")
      Custom =
        [| for f in facts do
               let field = strOrNull f "field"
               if answered f && not (carriedElsewhere.Contains field) then
                   { Label = label field; Data = strOrNull f "value" } |]
      Category = (if isNull category then "unmapped" else category)
      PracticeArea = strOrNull classification "practiceArea" }

/// A payload checked against the route's contract, hashed and mapped; any refusal is a 422 with the reasons.
let parseAs (contract: string) (raw: string) : Parsed * JsonElement =
    let payload =
        try
            JsonDocument.Parse(raw).RootElement.Clone()
        with :? JsonException as e ->
            fail 422 $"invalid JSON: {e.Message}"
    if payload.ValueKind <> JsonValueKind.Object then fail 422 "the lead must be a JSON object"
    match strOrNull payload "contract" with
    | c when c <> contract ->
        let says = if isNull c then "nothing" else c
        fail 422 $"this route takes {contract}; the payload says {says}"
    | _ -> ()
    match validateAs contract payload with
    | [] -> ()
    | errors ->
        let shownErrors = errors |> List.truncate 20
        let more = if errors.Length > 20 then $" (and {errors.Length - 20} more)" else ""
        fail 422 $"""not a valid {contract} lead: {String.Join("; ", shownErrors)}{more}"""
    let classification = payload.GetProperty "classification"
    let category = strOrNull classification "category"
    let area = strOrNull classification "practiceArea"
    if not (isNull category) && not (Categories.isCategory category) then
        fail 422 $"classification.category: {category} is not in the call-category taxonomy (version {Categories.version ()})"
    if not (isNull area) && not (Categories.isPracticeArea area) then
        fail 422 $"classification.practiceArea: {area} is not in the call-category taxonomy (version {Categories.version ()})"
    let policy = payload.GetProperty "policy"
    { LeadId = strOrNull payload "leadId"
      InteractionId = strOrNull (payload.GetProperty "conversation") "id"
      SenderFirmId = strOrNull (payload.GetProperty "firm") "id"
      PolicyRevision = policy.GetProperty("revision").GetInt32()
      PolicyApproved = policy.GetProperty("approved").GetBoolean()
      Hash = hash payload
      Contract = contract
      CompletedAt = DateTimeOffset.Parse(strOrNull (payload.GetProperty "conversation") "endedAt").ToUnixTimeMilliseconds()
      Lead = toLead payload },
    payload

/// A draft-2 payload (milestone 1's route).
let parse (raw: string) : Parsed * JsonElement = parseAs draft2 raw
