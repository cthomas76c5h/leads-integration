/// The shapes in docs/api.md. Records serialise to exactly the JSON the
/// frontend expects (camelCase); optional values are null / Nullable rather
/// than F# options, so System.Text.Json reads and writes them plainly.
module LeadIntegrations.Domain

open System

/// The lab's one clock. A scenario run sets it (test control, docs/api.md);
/// otherwise it is the system clock.
let mutable private fixedNow: int64 voption = ValueNone

/// Fix the clock at `t` (Unix ms). Only test control calls this.
let setClock (t: int64) = fixedNow <- ValueSome t

/// Back to the real clock (tests that set it).
let resetClock () = fixedNow <- ValueNone

let now () =
    match fixedNow with
    | ValueSome t -> t
    | ValueNone -> DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

module Source =
    let legacy = "legacy"
    let chatbot = "chatbot"
    let airi = "airi"
    let manual = "manual"
    let all = [| legacy; chatbot; airi; manual |]

type CredentialField =
    { Name: string
      Present: bool
      Fingerprint: string
      Meaning: string }

type Issue =
    { Code: string
      Severity: string // info | warn | bad
      Message: string }

type FirmSource =
    { System: string
      SourceId: string
      Name: string
      Active: bool }

type ContactType = { Name: string; Active: bool }

type Connection =
    { Id: string
      FirmId: string
      FirmName: string
      Kind: string
      Name: string
      Source: string
      SourceRef: string
      Credentials: CredentialField[]
      Settings: Collections.Generic.Dictionary<string, string>
      Enabled: bool
      Status: string // active | disabled
      DisabledReason: string
      ConsecutiveFailures: int
      DeliveryUrl: string
      Issues: Issue[]
      CreatedAt: int64 }

type CustomField = { Label: string; Data: string }

type Lead =
    { FirstName: string
      LastName: string
      Phone: string
      Email: string
      CallType: string
      Message: string
      Source: string
      Webchat: bool
      Custom: CustomField[]
      /// The category a sender set on the lead (a contract lead: intake-lead/draft-2), used as given.
      /// Null: classified from CallType (Categories.ofLead).
      Category: string
      PracticeArea: string }

type LeadEvent =
    { Id: string
      FirmId: string
      Test: bool
      CreatedAt: int64
      Lead: Lead }

type Attempt =
    { N: int
      At: int64
      DurationMs: int64
      Url: string
      Status: Nullable<int>
      Error: string
      Outcome: string
      RequestBody: string
      ResponseSnippet: string
      NextAttemptInMs: Nullable<int64> }

/// What is wrong with a delivery and why (docs/lead-delivery). A class, so
/// "nothing is wrong" is null in JSON, like the other optional fields.
[<AllowNullLiteral>]
type Diagnosis(cls: string, cause: string, retrying: bool) =
    /// transient | lead | configuration | connection | ours | routing | stopped | unknown
    member _.Class = cls
    /// The receiver's own words when it gave any, else what we know.
    member _.Cause = cause
    member _.Retrying = retrying

/// Why a lead goes, or does not go, to one destination (RFC-009 §3.2). Made once,
/// when the lead is recorded, and kept with the delivery, so it can be explained
/// and reproduced later from the lead, the rule and the taxonomy version.
type RoutingDecision =
    { /// planned | not-sent-routing | not-sent-lead
      Outcome: string
      /// The destination's rule in words, as it was when the decision was made.
      Rule: string
      Category: string
      PracticeArea: string
      TaxonomyVersion: int
      /// What the rule matched: a category, "call type" (the legacy rule) or "every call". Null when not planned.
      MatchedOn: string
      /// Why not sent, in the firm's words. Null when planned.
      Reason: string
      /// The destination's approved revision whose rule decided it (RFC-008 §5.2): 1 until a change is approved.
      Revision: int }

/// An operator's resend (docs/lead-delivery/operations.md#resend). Role: operator | senior-operator.
/// The lab has no sign-in, so the role is declared with the request; with sign-in it comes from the session.
type Resend = { By: string; Role: string; Why: string }

/// How an operator settled a delivery whose outcome was unknown (RFC-008 §4.2). Kind: found (it is at
/// the destination) | closed (not sent again) | resent (a new delivery replaces it). It stands: an
/// answer that arrives after it is kept as evidence, never allowed to change it.
type Resolution = { Kind: string; By: string; Why: string; At: int64 }

type Delivery =
    { Id: string
      EventId: string
      ConnectionId: string
      ConnectionName: string
      Kind: string
      FirmId: string
      FirmName: string
      Test: bool
      Status: string // pending | delivering (leased) | delivered | failed | cancelled | skipped | unknown
      Stage: string // queued | held | accepted | refused | not-sent | unknown
      Reason: string
      /// Why this lead goes, or not, to this destination (Routing.decide). Null for test
      /// leads, fallback hand-offs and deliveries recorded before decisions were.
      Decision: RoutingDecision
      NextAttemptAt: Nullable<int64>
      ReplayOf: string
      /// Who resent it, as what, and why (invariant 10: every operator action recorded). Null unless a resend.
      Resend: Resend
      /// Set when an operator settled an unknown outcome; null otherwise.
      Resolution: Resolution
      CreatedAt: int64
      Attempts: Attempt[]
      Diagnosis: Diagnosis
      /// The strongest evidence (RFC-008 §3.1): verified | accepted | submitted | none.
      Evidence: string
      /// Found at the destination by a lookup by our lead reference.
      VerifiedAt: Nullable<int64>
      /// Accepted, but a lookup has not found the record within the time to verify.
      VerifyOverdue: bool
      /// The destination can be looked up (its duplicate handling is `lookup`).
      Verifiable: bool }

type ReceivedRequest =
    { At: int64
      Path: string
      Kind: string
      Mode: string
      Answered: int
      Verdict: string
      Body: string }

type ImportRun =
    { Id: string
      System: string
      At: int64
      Firms: int
      Connections: int
      Skipped: int
      Notes: string[] }

// ----- Onboarding -----

type MapRow = { From: string; To: string }

type Approval =
    { Version: int
      Decision: string // approved | changes
      By: string
      Comment: string
      At: int64 }

type SetupEvent = { At: int64; Who: string; What: string }

type ActiveRevision =
    { Version: int
      Recipe: string
      Kind: string
      Rows: MapRow[]
      At: int64 }

/// The stored setup. Computed fields (state, next, ...) are added by Onboarding.view.
type Setup =
    { Id: string
      FirmId: string
      Token: string
      Kind: string
      Recipe: string
      Rows: MapRow[]
      Version: int
      /// Highest version ever issued: a discarded draft's number is never reused.
      LastVersion: int
      SentVersion: Nullable<int>
      ConnectionId: string
      TestDeliveryId: string
      ClientConfirmedAt: Nullable<int64>
      Active: ActiveRevision
      Approvals: Approval[]
      History: SetupEvent[]
      CreatedAt: int64 }

/// A domain failure the API turns into an HTTP status.
exception Fail of status: int * message: string

let fail status message = raise (Fail(status, message))
