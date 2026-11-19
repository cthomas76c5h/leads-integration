/// How long a lead stays in time (its window to act), by call category, and when
/// its hand-off goes to the firm's fallback: two separate clocks, both set by
/// Answering AI in docs/lead-delivery/delivery-timing.json (Timing.fs).
module LeadIntegrations.Urgency

/// The window to act for a call category: 72 hours by default, 24 for emergencies and custody.
let windowMs (category: string) : int64 = Timing.windowToActMs category

/// How long a lead of this category may wait with nothing accepted before the
/// fallback takes the hand-off: 1 hour by default, 15 minutes for emergencies and
/// custody. Its own clock, independent of the window.
let fallbackAfterMs (category: string) : int64 = Timing.fallbackAfterMs category

/// on-time | expired (the window passed before any hand-off; it may have reached the firm later) | waiting
let handOffState (createdAt: int64) (window: int64) (handedAt: int64 option) (nowMs: int64) =
    match handedAt with
    | Some at when at <= createdAt + window -> "on-time"
    | Some _ -> "expired"
    | None when nowMs > createdAt + window -> "expired"
    | None -> "waiting"
