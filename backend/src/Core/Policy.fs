/// What to do with each receiver response (ported from webhook-lab policy.ts).
/// These rules decide what an operator sees, so they are explicit and tested.
module LeadIntegrations.Policy

open System
open System.Globalization

type Outcome =
    | Delivered
    | Retry of reason: string * retryAfterMs: int64 option
    | Failed of reason: string // this delivery stops; the connection stays on
    | NeedsReconnection of reason: string // the connection stopped working (401, 403, 410): only the firm can fix it
    /// Sent, never answered, and the destination cannot be trusted to ignore a second copy (RFC-008 §4.2, §4.3):
    /// an operator checks before it is sent again. Never from `classify`: `Dispatch.outcomeOf` decides it.
    | Unknown of reason: string

let parseRetryAfter (value: string) (nowMs: int64) : int64 option =
    if String.IsNullOrWhiteSpace value then None
    else
        match Double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, seconds -> Some(max 0L (int64 (seconds * 1000.0)))
        | _ ->
            match DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
            | true, at -> Some(max 0L (at.ToUnixTimeMilliseconds() - nowMs))
            | _ -> None

/// status None: no HTTP answer (timeout, DNS, refused, TLS). A timeout may still be unknown: `Dispatch.outcomeOf`.
let classify (status: int option) (error: string) (retryAfter: string) (nowMs: int64) : Outcome =
    match status with
    | None -> Retry((if String.IsNullOrEmpty error then "no answer" else error), None)
    | Some s when s >= 200 && s < 300 -> Delivered
    // The connection itself stopped working: an expired or revoked sign-in (401, 403), or a
    // removed hook (410 Gone, the REST-hook convention Zapier sends when a Zap is deleted).
    // Retrying cannot help and it is not the destination failing: only the firm can reconnect.
    | Some 410 -> NeedsReconnection "HTTP 410 Gone: the receiver removed this hook"
    | Some(401 | 403 as s) -> NeedsReconnection $"HTTP {s}: the receiver refused our authorization"
    | Some s when s = 408 || s = 425 || s = 429 || s >= 500 -> Retry($"HTTP {s}", parseRetryAfter retryAfter nowMs)
    // Redirects are not followed: one could point the lead somewhere never vetted.
    | Some s when s >= 300 && s < 400 -> Failed $"HTTP {s}: redirects are not followed"
    | Some s -> Failed $"HTTP {s}: the receiver refused the body"

/// The waits between attempts, from the delivery timings Answering AI sets
/// (Timing.fs): 5s, 1m, 5m, 30m, 2h, 6h, 12h, 24h, 24h by default, about 69 hours,
/// so the record keeps trying for as long as a lead is still in time.
/// TIME_SCALE divides it so the lab runs it in seconds.
let retryScheduleMs = Timing.current.Value.RetrySchedule

/// Delay before the next attempt, or None when out of attempts. ±20% jitter so
/// retries from one outage do not arrive together; never sooner than Retry-After.
let nextDelayMs (attemptsSoFar: int) (timeScale: float) (retryAfterMs: int64 option) (random: unit -> float) : int64 option =
    if attemptsSoFar < 1 || attemptsSoFar > retryScheduleMs.Length then None
    else
        let jittered = float retryScheduleMs[attemptsSoFar - 1] * (0.8 + random () * 0.4)
        let wait = max jittered (float (defaultArg retryAfterMs 0L))
        Some(int64 (Math.Round(wait / timeScale)))

/// Consecutive dead production deliveries before a connection is switched off.
let disableAfterConsecutiveFailures = 5
