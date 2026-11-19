/// How long a lead stays worth acting on (its window to act), by call category,
/// and when its hand-off goes to the firm's fallback. Starting values to test
/// with firms, not measured (docs/lead-delivery/README.md, "Window to act and fallback").
module LeadIntegrations.Urgency

let private minute = 60_000L

/// The window to act for a call category.
let windowMs (category: string) : int64 =
    match category with
    | "emergency"
    | "jail-custody" -> 15L * minute
    | "new-matter" -> 60L * minute
    // "Same business day", simplified to 8 hours.
    | "existing-client"
    | "legal-professional"
    | "insurance-medical" -> 8L * 60L * minute
    // "Next business day", simplified to 24 hours: billing, vendor, general and the rest.
    | _ -> 24L * 60L * minute

/// The hand-off goes to the fallback when this much of the window has passed without one.
let fallbackAfterMs (window: int64) = window / 3L

/// on-time | expired (the window passed before any hand-off; it may have reached the firm later) | waiting
let handOffState (createdAt: int64) (window: int64) (handedAt: int64 option) (nowMs: int64) =
    match handedAt with
    | Some at when at <= createdAt + window -> "on-time"
    | Some _ -> "expired"
    | None when nowMs > createdAt + window -> "expired"
    | None -> "waiting"
