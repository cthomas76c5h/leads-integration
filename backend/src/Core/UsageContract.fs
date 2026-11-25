/// The call center's usage facts, usage/1 (C6; docs/contracts/usage/README.md): one ended interaction,
/// what it used, never whether it is billable (suite S-3: rating alone decides). Pure: the payload is
/// checked and read here; recording it is Store's. A tolerant reader: only the fields delivery reads
/// are checked, and the rest are kept as sent, because the center changes the contract additively.
module LeadIntegrations.UsageContract

open System
open System.Text.Json
open LeadIntegrations.Domain

let usage1 = "usage/1"

/// How an ended interaction came out. `Open` (not ended yet) is the center's too, but never accepted:
/// usage facts are immutable, and an open interaction's would still change.
let results = set [ "PutThrough"; "Message"; "Handled"; "Abandoned"; "Lost" ]

type Parsed =
    { Interaction: string
      SenderFirmId: string
      Revision: Nullable<int64>
      StartedAt: int64
      EndedAt: int64
      DurationMs: int64
      Result: string
      AnsweredBy: string
      Hash: string }

let private field (p: JsonElement) (name: string) =
    match p.TryGetProperty name with
    | true, v -> Some v
    | _ -> None

let private text (p: JsonElement) (name: string) =
    match field p name with
    | Some v when v.ValueKind = JsonValueKind.String && not (String.IsNullOrWhiteSpace(v.GetString())) -> v.GetString()
    | Some _ -> fail 422 $"{name}: a non-empty string"
    | None -> fail 422 $"{name}: missing"

let private whole (p: JsonElement) (name: string) =
    match field p name with
    | Some v when v.ValueKind = JsonValueKind.Number ->
        match v.TryGetInt64() with
        | true, n -> n
        | _ -> fail 422 $"{name}: a whole number"
    | Some _ -> fail 422 $"{name}: a number"
    | None -> fail 422 $"{name}: missing"

let private wholeOrNull (p: JsonElement) (name: string) =
    match field p name with
    | None -> Nullable()
    | Some v when v.ValueKind = JsonValueKind.Null -> Nullable()
    | Some _ -> Nullable(whole p name)

/// A payload checked and read; any refusal is a 422 saying why.
let parse (raw: string) : Parsed =
    let p =
        try
            JsonDocument.Parse(raw).RootElement.Clone()
        with :? JsonException as e ->
            fail 422 $"invalid JSON: {e.Message}"
    if p.ValueKind <> JsonValueKind.Object then fail 422 "the usage record must be a JSON object"
    match field p "contract" with
    | Some c when c.ValueKind = JsonValueKind.String && c.GetString() = usage1 -> ()
    | Some c -> fail 422 $"this route takes {usage1}; the payload says {c.GetRawText()}"
    | None -> fail 422 "contract: missing"
    let result = text p "result"
    if result = "Open" then fail 422 "result: Open: usage facts are taken once the interaction has ended"
    if not (results.Contains result) then fail 422 $"""result: {result} is not one of {String.Join(", ", results)}"""
    let endedAt =
        match wholeOrNull p "ended_at" with
        | n when n.HasValue -> n.Value
        | _ -> fail 422 "ended_at: missing: usage facts are taken once the interaction has ended"
    let startedAt = whole p "started_at"
    let duration = whole p "duration_ms"
    if duration < 0L then fail 422 "duration_ms: negative"
    if endedAt < startedAt then fail 422 "ended_at: before started_at"
    let answeredBy =
        match field p "answered_by" with
        | Some v when v.ValueKind = JsonValueKind.String -> v.GetString()
        | _ -> null
    { Interaction = text p "interaction"
      SenderFirmId = text p "firm"
      Revision = wholeOrNull p "revision"
      StartedAt = startedAt
      EndedAt = endedAt
      DurationMs = duration
      Result = result
      AnsweredBy = answeredBy
      Hash = LeadContract.hash p }
