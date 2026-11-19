/// JSON settings shared by the API, the database columns that hold JSON, and
/// the importers. camelCase out, case-insensitive in.
module LeadIntegrations.Json

open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Serialization

let options =
    let o =
        JsonSerializerOptions(
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        )
    o

let pretty =
    let o = JsonSerializerOptions(options)
    o.WriteIndented <- true
    o

let serialize (value: 'T) = JsonSerializer.Serialize(value, options)

let deserialize<'T> (text: string) : 'T = JsonSerializer.Deserialize<'T>(text, options)

/// A JSON column that may be NULL or empty: the fallback instead of a parse error.
let deserializeOr<'T> (fallback: 'T) (text: string) : 'T =
    if System.String.IsNullOrWhiteSpace text then fallback
    else
        try
            let v = JsonSerializer.Deserialize<'T>(text, options)
            if isNull (box v) then fallback else v
        with :? JsonException -> fallback

/// Compact, valid JSON from any JSON text (re-serialised), or the parse error.
let normalize (text: string) : Result<string, string> =
    try
        use doc = JsonDocument.Parse text
        Ok(JsonSerializer.Serialize(doc.RootElement, options))
    with :? JsonException as e ->
        Error e.Message

let indent (text: string) =
    try
        use doc = JsonDocument.Parse text
        JsonSerializer.Serialize(doc.RootElement, pretty)
    with :? JsonException ->
        text
