/// The delivery timings Answering AI sets (docs/lead-delivery/delivery-timing.json):
/// the window to act per call category, when a hand-off goes to the fallback, and
/// the retry schedule. Two separate clocks: the window says whether a lead reached
/// the firm in time; the fallback delay says how soon a lead stuck behind a failing
/// destination is handed to the firm another way. Embedded; DELIVERY_TIMING_FILE
/// points a deployment at its own copy.
module LeadIntegrations.Timing

open System
open System.IO
open System.Reflection
open System.Text.Json
open System.Text.RegularExpressions

type Settings =
    { WindowToAct: Map<string, int64>
      FallbackAfter: Map<string, int64>
      RetrySchedule: int64[]
      /// How long after acceptance a destination that can be looked up must show the record.
      VerifyWithin: int64 }

let private duration (text: string) : int64 =
    let m = Regex.Match(text.Trim(), @"^(\d+)(s|m|h|d)$")
    if not m.Success then failwith $"delivery timing: cannot read duration \"{text}\""
    let n = int64 m.Groups[1].Value
    match m.Groups[2].Value with
    | "s" -> n * 1000L
    | "m" -> n * 60_000L
    | "h" -> n * 3_600_000L
    | _ -> n * 86_400_000L

let private byCategory (root: JsonElement) (name: string) =
    let o = root.GetProperty name
    let m = [ for p in o.EnumerateObject() -> p.Name, duration (p.Value.GetString()) ] |> Map.ofList
    if not (m.ContainsKey "default") then failwith $"delivery timing: {name} needs a default"
    m

/// Reads the settings; fails loudly on anything malformed, so a bad file never runs.
let parse (json: string) : Settings =
    use doc = JsonDocument.Parse json
    let root = doc.RootElement
    let retries = [| for d in root.GetProperty("retrySchedule").EnumerateArray() -> duration (d.GetString()) |]
    if retries.Length = 0 then failwith "delivery timing: retrySchedule is empty"
    { WindowToAct = byCategory root "windowToAct"
      FallbackAfter = byCategory root "fallbackAfter"
      RetrySchedule = retries
      VerifyWithin =
        match root.TryGetProperty "verifyWithin" with
        | true, v -> duration (v.GetString())
        | _ -> 2L * 3_600_000L }

let private embedded () =
    use s = Assembly.GetExecutingAssembly().GetManifestResourceStream "delivery-timing.json"
    if isNull s then failwith "embedded resource missing: delivery-timing.json"
    use r = new StreamReader(s)
    r.ReadToEnd()

/// The settings in force: DELIVERY_TIMING_FILE when set, else the embedded file.
let current =
    lazy
        (match Environment.GetEnvironmentVariable "DELIVERY_TIMING_FILE" with
         | null
         | "" -> parse (embedded ())
         | path -> parse (File.ReadAllText path))

let private lookup (m: Map<string, int64>) (category: string) =
    match m.TryFind(if isNull category then "" else category) with
    | Some v -> v
    | None -> m["default"]

/// How long after the call a lead of this category still counts as reaching the firm in time.
let windowToActMs (category: string) = lookup current.Value.WindowToAct category

/// How long a lead of this category may wait with nothing accepted before the fallback takes the hand-off.
let fallbackAfterMs (category: string) = lookup current.Value.FallbackAfter category

/// How long an accepted record may stay unfound by a lookup before it is flagged (RFC-008 §3.1).
let verifyWithinMs () = current.Value.VerifyWithin
