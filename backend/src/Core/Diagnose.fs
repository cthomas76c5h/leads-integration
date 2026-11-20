/// What is wrong with a delivery, in the terms operators act on (the failure
/// classes in GLOSSARY.md and docs/lead-delivery/operations.md). The
/// drawer's headline, advice and buttons follow the class.
module LeadIntegrations.Diagnose

open System
open System.Text.Json
open LeadIntegrations.Domain

let private blank (s: string) = String.IsNullOrWhiteSpace s

/// How a not-sent delivery's reason marks a lead problem (Routing.decide), as opposed
/// to a routing exclusion. Only an exclusion means no hand-off is owed.
let leadProblemPrefix = "lead problem: "

let isLeadProblem (reason: string) = not (isNull reason) && reason.StartsWith leadProblemPrefix

/// How a held delivery's reason starts (Store.heldReason, Store.reconnectReason).
let heldPrefix = "held:"

/// A delivery held because its destination needs reconnection (401, 403, 410).
let reconnectPrefix = "held: needs reconnection"

/// The receiver's own words, when its answer was JSON that said something.
let receiverWords (snippet: string) : string option =
    if blank snippet then None
    else
        try
            use doc = JsonDocument.Parse snippet
            let root = doc.RootElement
            if root.ValueKind <> JsonValueKind.Object then None
            else
                [ "error"; "message"; "detail"; "title"; "result" ]
                |> List.tryPick (fun k ->
                    match root.TryGetProperty k with
                    | true, v when v.ValueKind = JsonValueKind.String && not (blank (v.GetString())) -> Some(v.GetString())
                    | _ -> None)
        with :? JsonException ->
            None

let private mentions (words: string list) (text: string) =
    let t = (if isNull text then "" else text).ToLowerInvariant()
    words |> List.exists t.Contains

let private stripPrefix (prefix: string) (s: string) =
    if not (isNull s) && s.StartsWith prefix then s.Substring(prefix.Length).Trim() else s

/// `lead` is read only when it decides the class (a refusal that may be the lead's fault).
let diagnose (kind: string) (status: string) (reason: string) (attempts: Attempt[]) (lead: Lazy<Lead option>) : Diagnosis =
    let last = Array.tryLast attempts
    let code = last |> Option.bind (fun a -> if a.Status.HasValue then Some a.Status.Value else None)
    let said =
        last
        |> Option.bind (fun a -> receiverWords a.ResponseSnippet)
        |> Option.orElse (last |> Option.bind (fun a -> if blank a.Error then None else Some a.Error))
        |> Option.defaultValue (if isNull reason then "" else reason)
    let make cls cause retrying = Diagnosis(cls, cause, retrying)
    match status with
    | "delivered" -> null
    | "skipped" when isLeadProblem reason -> make "lead" (stripPrefix leadProblemPrefix reason) false
    | "skipped" -> make "routing" reason false
    // Held because the connection stopped working: only the firm can fix it; the record waits.
    | "pending" when not (isNull reason) && reason.StartsWith reconnectPrefix ->
        make "connection" (if attempts.Length = 0 then "Held until the firm reconnects this destination" else said) false
    // Held by a pause: nothing is wrong; the operator resumes it.
    | "pending" when not (isNull reason) && reason.StartsWith heldPrefix -> null
    | "pending"
    | "delivering" -> if attempts.Length = 0 then null else make "transient" said true
    | "cancelled" -> make "stopped" (stripPrefix "connection disabled:" reason) false
    // Sent, never answered, and a second copy could create a duplicate: an operator checks the destination.
    | "unknown" -> make "unknown" (if isNull reason then "The outcome of the last attempt is unknown" else reason) false
    | _ ->
        if not (isNull reason) && reason.StartsWith "body:" then make "ours" (stripPrefix "body:" reason) false
        elif not (isNull reason) && reason.StartsWith "address refused" then make "configuration" reason false
        else
            match code with
            | Some(401 | 403 | 410) -> make "connection" said false
            | Some c when c >= 300 && c < 400 -> make "configuration" $"Redirected (HTTP {c}); redirects are not followed" false
            | Some c when c >= 400 && c < 500 && c <> 408 && c <> 425 && c <> 429 ->
                match lead.Value with
                | Some l when blank l.FirstName && blank l.LastName && mentions [ "name"; "first"; "last" ] said ->
                    make "lead" "No caller name, which this destination requires" false
                | Some l when blank l.Phone && mentions [ "phone" ] said ->
                    make "lead" "No phone number, which this destination requires" false
                | _ -> make "configuration" said false
            // No answer, or 408/425/429/5xx until the schedule ran out.
            | _ -> make "transient" said false
