# API contract

The backend (F#, `backend/`) and the frontend (Vite + TypeScript, `frontend/`)
both follow this file. Change it first, then both sides.

- Base: the backend listens on `http://localhost:5080`. The Vite dev server
  proxies `/api` and `/receiver` there.
- JSON, camelCase keys. Times are **unix milliseconds** (numbers).
- Errors: non-2xx with `{ "error": "message" }`.
- Secrets never leave the backend, and for imported data they never entered it:
  a credential is described by `CredentialField` (present + masked fingerprint).

## Types (TypeScript notation)

```ts
type SourceSystem = 'api2' | 'chatbot' | 'airi' | 'manual';

type Category = 'platform' | 'crm' | 'automation' | 'notify';
type AuthScheme = 'api_key' | 'oauth' | 'url' | 'none';

/** One supported destination type. Static, from the backend's catalog. */
interface Kind {
  kind: string;            // 'clio-grow', 'mycase', 'zapier-webhook', ...
  label: string;           // 'Clio Grow (inbox lead)'
  category: Category;
  auth: AuthScheme;
  credentialFields: string[];   // ['token', 'refresh_token']
  sourceCodes: { system: SourceSystem; code: string }[]; // where it comes from, e.g. { system: 'api2', code: 'Case' }
  mappingEnforced: boolean;     // true only for 'webhook': our field mapping is the body
  payloadFields: string[];      // top-level fields the receiver requires, e.g. ['inbox_lead.from_first', ...]
  notes: string;                // one line: quirks worth knowing (e.g. "api_key holds the full POST URL")
}

interface CredentialField {
  name: string;            // 'api_key', 'token', 'refresh_token', 'url'
  present: boolean;
  fingerprint: string | null; // '••••a1b2' (last 4 of a SHA-256 hash, never of the value)
  meaning: string | null;  // what this field actually holds for this kind, e.g. 'form id', 'staff id'
}

interface Issue { code: string; severity: 'info' | 'warn' | 'bad'; message: string }

interface Connection {
  id: string;
  firmId: string;
  firmName: string;
  kind: string;
  name: string;            // display name, e.g. 'MyCase'
  source: SourceSystem;
  sourceRef: string | null;  // id in the source system, e.g. api-2 CRM row id
  credentials: CredentialField[];
  settings: Record<string, string>; // non-secret settings, e.g. { sendWebchats: 'false' }; duplicates: how this destination's
                                     // receiver handles a second copy, where confirmed (RFC-008 §4.3; else its kind's contract); role: 'fallback' marks the firm's
                                     // agreed fallback: it gets no leads directly, only hand-offs whose fallback delay passed (1 h; 15 min for emergencies and custody).
                                     // The routing rule (RFC-009): routeCategories: 'new-matter,existing-client' (taxonomy ids), and
                                     // routePracticeAreas: 'personal-injury,…' (narrows new matters; absent = all). No routeCategories:
                                     // a CRM takes the firm's active call types (legacy), anything else every lead. Imports seed CRMs'.
  enabled: boolean;        // as configured (import: the source's enabled flag)
  status: 'active' | 'needs-reconnection' | 'disabled';
                           // delivery health. needs-reconnection: a 401, 403 or 410 (only the firm can fix it); its
                           // production leads are held, not refused, and the failure streak is untouched.
                           // disabled: switched off after 5 failures in a row; leads meanwhile are missed
  disabledReason: string | null;
  consecutiveFailures: number;
  deliveryUrl: string;     // imported CRMs point at the local fake receiver /receiver/crm/{kind}
  issues: Issue[];
  createdAt: number;
}

interface FirmSource {
  system: SourceSystem;
  sourceId: string;
  name: string;
  active: boolean;
}

interface FirmSummary {
  id: string;
  name: string;
  sources: SourceSystem[];          // which systems know this firm
  keyFingerprint: string | null;    // Answering Legal key fingerprint: the cross-system match key
  connectionKinds: string[];
  issueCount: number;
  /** Enabled, active destinations: the ones that receive leads. */
  liveDestinations: number;
  active: boolean;
}

interface FirmDetail extends FirmSummary {
  timeZone: string;                 // IANA name ('America/New_York' by default): the firm's office hours and the dates it reads
  sourcesDetail: FirmSource[];
  contactTypes: { name: string; active: boolean; mapping: CallMapping }[]; // api-2 routing: a lead goes out only if its callType matches an active one
  customLabels: string[];           // intake question labels seen in the sources (for synthetic leads)
  connections: Connection[];
  setups: Setup[];
}

interface Overview {
  firms: number;
  firmsBySource: Record<SourceSystem, number>;
  firmsInSeveralSources: number;    // matched by key fingerprint
  kinds: {
    kind: string; label: string; category: Category;
    total: number; enabled: number; withIssues: number;
    bySource: Partial<Record<SourceSystem, number>>;
  }[];
  issues: { code: string; severity: Issue['severity']; message: string; count: number }[];
  imports: ImportRun[];
}

interface ImportRun {
  id: string; system: SourceSystem; at: number;
  firms: number; connections: number; skipped: number; notes: string[];
}

/** The lead every payload is built from. Synthetic in this MVP. */
interface Lead {
  firstName: string; lastName: string; phone: string; email: string;
  callType: string; message: string; source: string; webchat: boolean;
  custom: { label: string; data: string }[];
}

interface LeadEvent { id: string; firmId: string; test: boolean; createdAt: number; lead: Lead }

/** One lead (event) in the Leads list. */
interface LeadSummary {
  id: string;                    // the event id
  firmId: string; firmName: string;
  test: boolean; createdAt: number;
  caller: string;                // 'First Last'; '' when the caller gave no name (hang-up)
  phone: string;
  callType: string;
  webchat: boolean;
  mapping: CallMapping;          // what the call type means
  /** Latest delivery per connection: a replay replaces what it replays. */
  counts: { total: number; delivered: number; refused: number; skipped: number; pending: number; held: number };
  /** expired: its window to act passed before any hand-off (comes first) · none: no destination
   *  · pending: any still queued or retrying · held: any waiting for a paused destination
   *  · refused: any failed or cancelled · skipped: not sent anywhere · delivered: the rest (at least one accepted). */
  outcome: 'expired' | 'none' | 'pending' | 'held' | 'refused' | 'skipped' | 'delivered';
  /** Null for test leads. */
  handOff: HandOff | null;
  conversationId: string | null;   // the channel's conversation; null when it gave none (an operator's send)
}

interface LeadDetail extends LeadSummary {
  lead: Lead;
  deliveries: Delivery[];        // every delivery of this lead, replays included, newest first
}

type DeliveryStatus = 'pending' | 'delivering' | 'delivered' | 'failed' | 'cancelled' | 'skipped' | 'unknown';
// delivering: leased by a worker (RFC-008 §5.3). unknown: sent, never answered, and the destination cannot be
// trusted to ignore a second copy, so it waits for an operator to check (needs attention).

interface Attempt {
  n: number; at: number; durationMs: number; url: string;
  status: number | null;   // HTTP status; null when there was no answer
  error: string | null;
  outcome: string;         // 'delivered' | 'retry: HTTP 503' | 'failed: ...' | 'connection disabled'
  requestBody: string;
  responseSnippet: string;
  nextAttemptInMs: number | null;
}

interface Delivery {
  id: string;
  eventId: string;
  connectionId: string;
  connectionName: string;
  kind: string;
  firmId: string;
  firmName: string;
  test: boolean;
  status: DeliveryStatus;
  /** What the operator can claim. 'accepted' = receiver answered 2xx. Never 'in CRM' from HTTP alone. */
  stage: 'queued' | 'held' | 'accepted' | 'refused' | 'not-sent' | 'unknown';   // held: waiting for its destination (pending, no nextAttemptAt)
  reason: string | null;
  /** Why this lead goes, or not, to this destination. Null for test leads, fallback hand-offs and older deliveries. */
  decision: RoutingDecision | null;
  nextAttemptAt: number | null;
  replayOf: string | null;
  resend: { by: string | null; role: 'operator' | 'senior-operator'; why: string | null } | null;  // who resent it, as what, and why
  evidence: 'verified' | 'accepted' | 'submitted' | 'none';   // the strongest so far (RFC-008 §3.1)
  verifiedAt: number | null;   // found at the destination by a lookup by our lead reference
  verifyOverdue: boolean;      // accepted, but not found within the time to verify (delivery-timing.json verifyWithin)
  verifiable: boolean;         // the destination can be looked up: its settings say duplicates: 'lookup'
  createdAt: number;
  attempts: Attempt[];
  /** What is wrong and why, worked out by the server (docs/lead-delivery). Null when nothing is. */
  diagnosis: Diagnosis | null;
}

/**
 * Made once, when the lead is recorded (RFC-009 §3.2), and kept with the delivery.
 * 'not-sent-routing' and 'not-sent-lead' deliveries have status 'skipped'; the reason of a
 * lead problem starts "lead problem: ", and its diagnosis class is 'lead'.
 */
interface RoutingDecision {
  outcome: 'planned' | 'not-sent-routing' | 'not-sent-lead';
  rule: string;              // the destination's rule in words when decided, e.g. 'new-matter; no chat leads'
  category: string;          // the lead's classification it used
  practiceArea: string | null;
  taxonomyVersion: number;
  matchedOn: string | null;  // 'new-matter', 'call type' (legacy rule), 'every call'; null unless planned
  reason: string | null;     // why not sent; null when planned
  revision: number;          // the destination's approved revision whose rule decided it (0: recorded before revisions)
}

/**
 * The failure classes of GLOSSARY.md, plus the two "not failures" an operator still
 * has to read: 'routing' (not sent on purpose) and 'stopped' (cancelled because the
 * destination was switched off or removed).
 *   transient      no answer, 408/425/429/5xx; retrying, or gave up when retrying=false
 *   lead           this lead lacks what the destination requires (a hang-up to a CRM): not sent, or refused
 *   configuration  refused for a reason in the destination's setup (mapping, missing ID, redirect)
 *   connection     401/403/410: only the firm can reconnect
 *   ours           we could not build the request
 *   unknown        sent, never answered; the destination may have it (RFC-008 §4.2): check before resending
 */
type FailureClass = 'transient' | 'lead' | 'configuration' | 'connection' | 'ours' | 'routing' | 'stopped' | 'unknown';

interface Diagnosis {
  class: FailureClass;
  /** The receiver's own words when it gave any ("Phone is required"), else what we know. */
  cause: string;
  /** True while it is still retrying by itself. */
  retrying: boolean;
}

/** Totals for the Delivery Center tiles: every matching delivery, not a page. */
interface DeliverySummary {
  /** Start of "today", as the client sent it (its local midnight). */
  since: number;
  /** Deliveries created since `since`, newest per lead and destination. */
  accepted: number;
  refused: number;   // refused, gave up or cancelled
  notSent: number;
  /** Newest per lead and destination refused or gave up, not a lead problem, any age. */
  needsAttention: number;
  /** Refused for what the lead lacks; listed but not counted as needing attention. */
  leadProblems: number;
  retrying: number;
  /** Retrying more than 15 minutes after it was created. */
  late: number;
  /** Destinations switched off or needing reconnection (`firmId` narrows it). */
  stoppedDestinations: number;
  /** Production leads in the last 7 days whose window to act passed before any hand-off. */
  expiredLeads: number;
}

/**
 * When a lead first reached the firm (its first accepted delivery, any destination),
 * against its window to act (docs/lead-delivery/README.md, "Window to act and fallback").
 * Windows (docs/lead-delivery/delivery-timing.json): emergency and custody 24 h; every other kind 72 h.
 */
interface HandOff {
  windowMs: number;
  dueAt: number;
  at: number | null;          // first accepted delivery; null while none
  by: string | null;          // the destination that took it first
  viaFallback: boolean;       // taken by the firm's fallback
  state: 'on-time' | 'expired' | 'waiting';
  evidence: 'acknowledged' | 'accepted' | 'submitted' | 'none';   // the strongest so far (RFC-008 §3.1)
  needsAcknowledgement: boolean;   // emergencies and custody: a person at the firm must confirm (RFC-008 §3.2)
  acknowledgedAt: number | null;   // confirmed through the link in the email or SMS
  escalatedAt: number | null;      // not confirmed within the fallback delay: an operator calls the firm
  escalated: boolean;
}

/** "Your week in calls": one firm's last 7 days in call categories, with proof of delivery. */
interface FirmReport {
  firmId: string; firmName: string; from: number; to: number;
  timeZone: string;               // the firm's IANA zone: show this report's dates in it; afterHours is 8 am–6 pm weekdays there
  calls: number; newMatters: number; urgent: number; afterHours: number; chats: number; hangUps: number;
  byCategory: { id: string; count: number }[];        // call-category ids
  byPracticeArea: { id: string; count: number }[];    // new matters; 'unspecified' when the call type names none
  handOff: { onTime: number; expired: number; waiting: number; viaFallback: number; medianMs: number | null; urgentOnTime: number; urgentTotal: number };
  destinations: { connectionId: string; name: string; kind: string; fallback: boolean; health: DestinationHealth; state: DestinationState;
                  accepted: number; refused: number; notSent: number; missedLeads: number }[];
  labels: { label: string; category: string; practiceArea: string | null; count: number }[];  // the firm's own words, top 10
}

interface ReceivedRequest {
  at: number; path: string; kind: string | null; mode: string;
  answered: number; verdict: string;   // 'accepted' | 'missing inbox_lead.from_phone' | ...
  body: string;
}

// ----- Destinations (docs/lead-delivery/operations.md) -----

/** Where the destination is in its life (GLOSSARY.md). */
type DestinationState = 'setting-up' | 'live' | 'paused' | 'needs-reconnection' | 'switched-off' | 'off';

/**
 * How it is doing, most urgent first; one per destination.
 *   stopped   needs-reconnection or switched-off
 *   failing   its newest finished production delivery was refused or gave up (not a lead problem)
 *   late      a production delivery has been retrying for more than 15 minutes
 *   retrying  retrying, not late yet
 *   paused    paused: enabled = false after it was used here
 *   off       off in its source system and never used here (sorted last)
 *   setting-up  test leads only
 *   healthy   its newest finished production delivery was accepted
 *   quiet     live, no production delivery in the last 7 days
 */
type DestinationHealth = 'stopped' | 'failing' | 'late' | 'retrying' | 'paused' | 'setting-up' | 'healthy' | 'quiet' | 'off';

interface DestinationRow {
  connectionId: string;
  name: string;
  kind: string;
  firmId: string;
  firmName: string;
  firmActive: boolean;
  state: DestinationState;
  health: DestinationHealth;
  /** Why it was switched off or needs reconnection. */
  stoppedReason: string | null;
  failureStreak: number;
  lastAcceptedAt: number | null;
  /** The newest production delivery to it, whatever happened. */
  lastLeadAt: number | null;
  /** Production deliveries in the last 7 days, newest per lead. */
  week: { accepted: number; refused: number; notSent: number; retrying: number };
  /** The oldest production delivery still retrying. */
  retryingSince: number | null;
  /**
   * Missed leads: the firm's production leads since this destination's first delivery
   * that have no delivery to it (recorded while it was paused or stopped).
   */
  missedLeads: number;
  /** Its newest refused or gave-up production delivery, when the health is about it. */
  lastProblem: { deliveryId: string; at: number; diagnosis: Diagnosis } | null;
}

// ----- Billing (docs/billing/strategy.md). Lab data, illustrative prices. -----

interface Price { id: string /* plan@version */; plan: string; version: number; tier: 'answer' | 'deliver' | 'insight'; name: string;
  monthlyCents: number; included: number /* handled conversations a cycle */; overageCents: number; effectiveFrom: number }

/** One billing cycle for one firm. Closed cycles are rated once and locked (lockedAt). */
interface Statement {
  firmId: string; firmName: string;
  cycleFrom: number; cycleTo: number;            // [from, to): cycleTo is the next cycle's first day
  timeZone: string;                              // the calendar cycles are on: the company's ('America/New_York'), daylight saving included
  inProgress: boolean; lockedAt: number | null;
  segments: { planName: string; tier: string; from: number; to: number; feeCents: number; included: number; note: string }[];  // a plan change splits the cycle
  conversations: number; billable: number; free: number;
  units: number;                                  // billable conversations, very long ones counting twice
  included: number; overage: number;
  byCategory: { category: string; billable: number; free: number }[];
  freeByRule: { rule: string /* id@version */; label: string; count: number }[];
  lines: { label: string; quantity: number; unitCents: number; amountCents: number }[];
  totalCents: number;
  value: { newMatters: number; onTime: number; expired: number; handOffs: number };
  /** The same conversations under the legacy minutes formula: the shadow comparison. */
  legacy: { minutes: number; noChargeMinutes: number; noChargeAllowance: number; wrapup: number; billedMinutes: number;
            planMinutes: number | null; overageMinutes: number; overageRateCents: number | null; totalCents: number | null; problems: string[] };
  checks: { code: string; severity: 'bad' | 'warn'; message: string }[];   // controls before an invoice goes out
}

interface BillingAccountRow { firmId: string; firmName: string; cycleDay: 1 | 15; plan: string | null; tier: string | null;
  ready: boolean; problems: string[]; conversations: number; billable: number; totalCents: number; legacyCents: number | null;
  checks: Statement['checks'] }

// ----- Terms of service (docs/terms/strategy.md) -----

interface TermsVersion { version: number; title: string; summary: string; body: string;
  changeKind: 'editorial' | 'pricing' | 'material'; require: 'none' | 'admins' | 'all';
  publishedAt: number; effectiveAt: number; source: 'docx' | 'text' }

/** What a portal user sees on signing in. block: the portal waits for acceptance; notice: a banner. */
interface TermsGate { mode: 'block' | 'notice' | 'none'; version: number | null; title: string | null; summary: string | null;
  reason: string | null; dueAt: number | null }

// ----- Call categories (docs/call-categories) -----

/** What a call-type name means. Rules and ids: docs/call-categories/taxonomy.json. */
interface CallMapping {
  category: string;              // 'new-matter' | 'general' | 'legal-professional' | ... | 'unmapped'
  practiceArea: string | null;   // new matters only: 'personal-injury', 'criminal', ..., 'unspecified'
  modifiers: string[];           // 'spanish', 'after-hours', 'named-person', ...
  crm: 'send' | 'skip' | 'review' | 'never' | null; // the category's default for CRM delivery; null when unmapped
}

interface CallCategory {
  id: string; label: string; description: string;
  isCallCategory: boolean;       // false: system entry, line handling, a person's name
  crm: 'send' | 'skip' | 'review' | 'never';
  share: number | null;          // of firm × name occurrences in the imported data (coverage.json)
  examples: string[];            // names used by 5+ firms, no person's names
}

interface Taxonomy {
  categories: CallCategory[];    // in rule order
  practiceAreas: { id: string; label: string; occurrences: number | null; examples: string[] }[];
  modifiers: { id: string; label: string; occurrences: number | null }[];
  agreement: { activeTypes: number; activeTypesClassifiedNewMatter: number; newMatterTypes: number; newMatterTypesActive: number };
}

/** A synthetic lead with the mapping it must get. Invented people, real intake wording. */
interface ReferenceLead {
  id: string;
  why: string;                   // what this case tests
  expect: CallMapping;
  mapped: CallMapping;           // what the backend's rules return now
  matches: boolean;
  lead: Lead;
}

// ----- Onboarding (same model as webhook-lab, RFC-001 §4–6) -----

interface MapRow { from: string; to: string }   // from: a Lead field key, see SOURCE_FIELDS below
interface Approval { version: number; decision: 'approved' | 'changes'; by: string; comment: string; at: number }
interface SetupEvent { at: number; who: 'operator' | 'client' | 'system'; what: string }
interface ActiveRevision { version: number; recipe: string; kind: string; rows: MapRow[]; at: number }

interface Setup {
  id: string;
  firmId: string;
  firmName: string;
  token: string;            // the client's secure link; omitted on client routes
  kind: string;             // destination kind from the catalog
  kindLabel: string;        // its catalog label, for the client page (which cannot call /api/catalog)
  auth: AuthScheme;         // how the client connects: 'url' pastes an address; otherwise authorize
  recipe: string;           // 'create-intake' | 'create-contact' | 'notify-staff'
  rows: MapRow[];
  version: number;
  sentVersion: number | null;
  connectionId: string | null;
  testDeliveryId: string | null;
  clientConfirmedAt: number | null;
  active: ActiveRevision | null;
  approvals: Approval[];     // newest first
  history: SetupEvent[];     // newest first
  createdAt: number;
  // computed
  state: 'Connection needed' | 'Draft' | 'Awaiting client approval' | 'Changes requested' | 'Ready to test'
       | 'Testing' | 'Test failed' | 'Test accepted' | 'Ready to go live' | 'Live';
  next: string;
  tone: 'ok' | 'warn' | 'bad' | 'muted';
  live: boolean;
  activeVersion: number | null;
  revising: boolean;
  enforced: boolean;
  sample: { from: string; to: string; label: string; value: string }[]; // label: the source field's display name
}
```

Lead field keys for mapping rows (`SOURCE_FIELDS`): `first_name`, `last_name`,
`full_name`, `phone`, `email`, `call_type`, `message`, `full_message` (message
plus every intake answer), `source`, `event_id`, plus `custom.<key>` for each
custom label, where key is the label lowercased with every run of
non-alphanumerics turned into `_` and trimmed ("Date of accident?" →
`custom.date_of_accident`). `GET /api/setups/{id}/fields` lists them with labels:
`{ key: string, label: string }[]`.

## Routes

| Method | Path | Body | Answer |
|---|---|---|---|
| GET | `/api/health` | | `{ ok: true }` |
| GET | `/api/catalog` | | `Kind[]` |
| GET | `/api/billing/price-book` | | `Price[]` every version |
| GET | `/api/billing/accounts` | | `BillingAccountRow[]`: the current cycle, readiness and the shadow comparison |
| GET | `/api/firms/{id}/statement?cycle=current\|previous` | | `Statement`; a closed cycle is locked on first read |
| POST | `/api/firms/{id}/plan` | `{ priceId }` | `{ startsAt, statement }`: an upgrade applies at once (prorated by day), a downgrade from the next cycle |
| GET | `/api/terms` | | `{ versions: TermsVersion[], coverage: { version, asked, accepted }[] }` newest first |
| POST | `/api/terms` | `{ title?, summary, body, changeKind, require?, effectiveAt?, source? }` | `{ version }`; `require` defaults from the change kind (material: all, pricing: admins, editorial: none) |
| GET | `/api/terms/{version}/acceptances?limit=50` | | `{ version, firmId, firmName, userId, role, at }[]` |
| GET | `/api/portal/terms?firm=&role=admin\|member\|new` | | `TermsGate` for the lab's synthetic user (`new`: never signed in) |
| POST | `/api/portal/terms/accept` | `{ firmId, role, version }` | `TermsGate` after recording the acceptance |
| POST | `/api/firms/{id}/users` | `{ name, role?: 'admin' \| 'member' }` | 201 `{ user: { id, firmId, name, role }, token }`. An operator issues a firm user's sign-in token. It is shown this once; only a salted hash is kept |
| GET | `/api/portal/leads?q=&category=&outcome=&limit=&offset=` | | `{ total, items: LeadSummary[] }`, the signed-in firm's production leads only. `firmId` naming another firm: **403**, audited |
| GET | `/api/portal/leads/{id}` | | `LeadDetail` of the signed-in firm's lead. Another firm's lead, a test lead or an unknown id: the same **404**, audited (RFC-008 §9) |
| GET | `/api/portal/leads/{id}/export` | | The lead as a file (`Content-Disposition: attachment`); scoped as above |
| POST | `/api/portal/leads/{id}/resend` | | 202 `{ resent: string[] }`: the firm asks for its lead again where it was refused, cancelled or unknown; never an accepted delivery. Scoped as above |
| GET | `/api/access/refusals` | | `{ at, userId, firmId, boundary, target, outcome }[]`: every refused firm request, oldest first (operator) |

Every `/api/portal/leads` route needs `Authorization: Bearer {token}` (401 without a valid one), and answers for the token's firm only (Access.fs). A refusal emits `AccessRefused`. The terms routes above still take the firm from the request: they predate sign-in.
| GET | `/api/overview` | | `Overview` |
| GET | `/api/firms?q=&kind=&source=&issues=1&limit=50&offset=0` | | `{ total: number, items: FirmSummary[] }`; by name, or with `q` best matches first (active firms with live destinations, then names starting with `q`) |
| GET | `/api/firms/{id}` | | `FirmDetail` |
| GET | `/api/firms/{id}/report` | | `FirmReport`: the last 7 days, production leads |
| POST | `/api/firms` | `{ name, timeZone? }` | `FirmDetail` (source `manual`; `timeZone` defaults to `America/New_York`) |
| PATCH | `/api/firms/{id}` | `{ timeZone }` | `FirmDetail`. Set by Answering AI; 422 unless an IANA name (`America/Los_Angeles`) |
| POST | `/api/firms/{id}/leads` | `{ variant?: 'full' \| 'sparse', callType?: string, webchat?: boolean, referenceLead?: string, conversationId?: string }` | 201 `{ event: LeadEvent, deliveries: Delivery[], mapping: CallMapping, duplicate: false }`. `conversationId` is the channel's id for the call or chat: intake records one lead per firm and conversation, so a repeat is answered **200** with the lead already recorded, its deliveries and `duplicate: true`, and nothing is planned or sent again (RFC-008 §4.1; api-2 answers 208). Without one, every request is a new lead. **503** `{ error, recorded: false }` with `Retry-After` when the lead could not be committed (the database did not take the write): nothing was recorded, planned or sent, and the channel keeps its copy and sends it again. One delivery per enabled, active connection, each with its routing `decision`; `skipped` with a reason when routing excludes it or the lead lacks what the destination requires. `referenceLead` sends that reference lead as-is; `callType` then replaces its call type (e.g. the firm's own name for the same category) |
| GET | `/api/call-categories` | | `Taxonomy` |
| GET | `/api/call-categories/classify?name=` | | `CallMapping` |
| GET | `/api/reference-leads` | | `ReferenceLead[]` |
| GET | `/api/connections?kind=&status=` | | `Connection[]`. `status` is a `Connection.status`, or `stopped` for any that is not active |
| POST | `/api/firms/{id}/connections` | `{ kind, name?, deliveryUrl? }` | `Connection` (source `manual`; URL checked: https, or localhost in dev) |
| GET | `/api/destinations?q=&health=&kind=&firmActive=all&limit=50&offset=0` | | `{ total: number, counts: Record<DestinationHealth, number>, missed: number, items: DestinationRow[] }` most urgent first. `counts` and `missed` (destinations with missed leads) cover the filter without `health`; `health=missed` lists those with missed leads. Only active firms unless `firmActive=all` |
| POST | `/api/connections/{id}/send-missed` | | `{ queued: number, notSent: number }`: a delivery for each missed lead, routing applied. 409 unless the destination is live |
| POST | `/api/connections/{id}/routing` | `{ categories: string[], practiceAreas?: string[], by }` | `{ draftRevision }`. An operator prepares a new routing rule (call-category and practice-area ids; 422 for an unknown one). Kept as the `routingDraft` setting; nothing changes until approved. Emits `RevisionDrafted` |
| POST | `/api/connections/{id}/routing/approve` | `{ by, applyToWaiting?: boolean }` | `{ revision, cancelled, approvedBy }`. The firm contact approves: the rule goes live as the next `revision` (`RevisionWentLive`). Deliveries still waiting keep the revision they were planned with, unless `applyToWaiting`: then each is decided again, and one the new rule excludes is `cancelled` with the reason (`DeliveryCancelled`). 409 when nothing waits for approval. The lab records the approval through this route; the firm's own approval link is not built |
| PATCH | `/api/connections/{id}` | `{ enabled?, deliveryUrl?, reactivate?: true }` | `Connection`. `reactivate` sets `status: 'active'` after a switch-off or a reconnection (the lab's stand-in for the firm reconnecting) and makes every held delivery due at once. `enabled: false` pauses it and records `settings.pausedAt`: new leads still get a routing decision, and a planned delivery is held (`status: 'pending'`, `stage: 'held'`, no `nextAttemptAt`, `reason: 'held: destination paused'`), as are deliveries already waiting. A lead whose every delivery that could reach the firm is held goes to the firm's fallback at once. `enabled: true` resumes it, clears `pausedAt` and makes every held delivery due at once. A destination imported off in its source system (`enabled: false`, no `pausedAt`) holds nothing and gets no deliveries |
| GET | `/api/connections/{id}/preview?variant=full` | | `{ url, headers: Record<string,string>, body: string, errors: string[] }` |
| POST | `/api/connections/{id}/test` | | `Delivery` (test lead to this connection only) |
| GET | `/api/leads?q=&firmId=&category=&outcome=&test=&limit=50&offset=0` | | `{ total: number, items: LeadSummary[] }` newest first. `q` matches caller, phone, call type or firm name; `category` is a call-category id |
| GET | `/api/leads/{id}` | | `LeadDetail`. Its `mapping` is the lead's current classification (corrected, if it was); its hand-off window stands as judged at intake |
| GET | `/api/leads/{id}/classification` | | `{ category, practiceArea, records: { seq, category, practiceArea, source: 'intake' \| 'reviewer' \| 'firm-contact', by, at, taxonomyVersion }[], offered: string[] }`. Every classification record, oldest first; the last is current. `offered`: connection ids of the firm's usual destinations the corrected lead now matches that never had it, offered to an operator, never sent automatically (RFC-008 §6.2) |
| POST | `/api/leads/{id}/classification` | `{ category, practiceArea?, source: 'reviewer' \| 'firm-contact', by }` | The classification view above. A correction: a new record that supersedes the last (`ClassificationCorrected`); nothing is resent or recalled. If the lead's cycle has closed and the correction lowers its charge, a credit in the next cycle (`UsageCredited`); a higher charge is never added. 422 for an unknown category or practice area; 409 if nothing changes; 422 for a test lead |
| GET | `/api/leads/{id}/billing` | | `{ is: 'billable' \| 'free' \| 'credited-next-cycle', rule, ratedAt }`. Rated from the current classification while its cycle is open; as kept once the cycle closed (`ratedAt`) |
| GET | `/api/deliveries?status=&firmId=&connectionId=&test=&limit=100&offset=0` | | `Delivery[]` newest first. `status` also takes `attention` (see `DeliverySummary.needsAttention`) and `late` |
| GET | `/api/deliveries/summary?test=false&firmId=&since=` | | `DeliverySummary`; `since` defaults to 24 hours ago |
| GET | `/api/deliveries/{id}` | | `Delivery` + `{ event: LeadEvent }` |
| POST | `/api/deliveries/{id}/replay` | `{ by?, role?: 'operator' \| 'senior-operator', why? }` (empty: an operator) | `Delivery` (new delivery, same event id, `replayOf` and `resend` set). 403 when the lead was already accepted at that destination, unless `role` is `senior-operator` and `why` is given. The lab has no sign-in, so the role is declared here; with sign-in it comes from the session |
| GET | `/api/received?limit=50` | | `ReceivedRequest[]` newest first |
| GET | `/api/setups` | | `Setup[]` |
| GET | `/api/setups/{id}/fields` | | `{ key, label }[]` mapping sources for this firm |
| POST | `/api/setups` | `{ firmId, kind, recipe, cloneFrom? }` | `Setup` |
| PATCH | `/api/setups/{id}` | `{ recipe?, rows? }` | `Setup` (a change makes a new version) |
| POST | `/api/setups/{id}/send` \| `/test` \| `/confirm` \| `/go-live` \| `/discard` | | `Setup` |
| GET | `/api/client/setups/{token}` | | `Setup` without `token`/`connectionId`, plus `connected: boolean` |
| POST | `/api/client/setups/{token}/connect` | `{ url? }` | `{ ok: true }` (webhook / zapier: paste URL; CRM kinds: simulated authorization) |
| POST | `/api/client/setups/{token}/decision` | `{ decision: 'approved' \| 'changes', by, comment, version }` | `{ ok: true }` |
| GET | `/ack/{leadId}?t={token}` | | HTML: a page with one button, "I have this lead". Records nothing (mail scanners open links). 404 page for a link we did not send. The link is in every production email and SMS (`ackUrl`, and at the end of `text`) |
| POST | `/ack/{leadId}?t={token}` | | HTML. Records the acknowledgement (the first one counts) and emits `HandOffAcknowledged` |
| POST | `/receiver/{ok\|reject\|flaky\|gone\|slow\|down}` | anything | lab receivers (`down`: always 503) |
| POST | `/receiver/crm/{kind}?mode=ok\|reject\|flaky\|gone\|slow\|down\|lost-record&require=Phone,Email` | the kind's payload | validates the payload like the real CRM would, plus any `require`d fields (how the lab plays a webhook that needs a field); 201 or 422. An accepted request is kept under our lead reference (its `webhook-id` header), except in `lost-record` mode |
| GET | `/receiver/crm/{kind}/lookup?ref={leadId}&dest=` | | The fake CRM's lookup by our lead reference: 200 when it kept that record, 404 when not |

Errors the UI should show as-is: 404 `no such …`, 409 state conflicts
(`not ready: Draft`), 422 validation.

## Test control (`TEST_CONTROL=true` only)

What the [scenario runner](contracts/README.md#running-the-scenarios) needs beyond the
routes above: the clock, the worker, and how a fake receiver answers. These
routes exist only when the API starts with `TEST_CONTROL=true`, and the API
refuses to start that way against the lab's own database (`DB_PATH` must point
elsewhere). Without it they answer 404. Never enabled outside a test run.

| Method | Path | Body | Returns |
|---|---|---|---|
| POST | `/api/test/clock` | `{ at: number }` | `{ now }`. Sets the one clock every rule reads (`Domain.now`); from then on it moves only when set or advanced |
| POST | `/api/test/advance` | `{ to: number }` | `{ now, passes }`. Moves the clock to `to`, stopping at every due retry and at least once a minute to run the worker: fallbacks, then every due delivery. Retry jitter is zero, so runs repeat exactly |
| PUT | `/api/test/receivers/{key}` | `{ mode? } \| { status, body?, retryAfter? } \| { noAnswer: true }`, plus `requires?: string[]` | `{ ok: true }`. How the fake receiver answers requests whose URL carries `?dest={key}`, until set again. `mode` is one of the [receiver modes](contracts/receivers/modes.json) the lab implements; `noAnswer` waits past the delivery timeout |
| PUT | `/api/test/connections/{id}` | `{ settings?: Record<string, string>, mapping?: string[] }` | `Connection`. Settings the public routes do not expose (`role: 'fallback'`, `sendWebchats: 'false'`, `routeCategories`, `routePracticeAreas`); `mapping` sets a webhook's live mapping from field labels (First name, Last name, Email, Phone, Note) |
| PUT | `/api/test/firms/{id}/contact-types` | `{ name: string, active: boolean }[]` | `FirmDetail`. The firm's call types, which a CRM with no routing rule is routed by |
| POST | `/api/test/faults` | `{ kind, eventId?, connectionId? }` | `{ ok: true }`. A fault inside our system (RFC-008 §10) for a lead's delivery to a destination: `worker-stalls-before-send` (the next claim holds its lease and starts nothing), `worker-continues` (that worker tries to start with the lease version it holds), `second-worker-claims`, `lease-expires`, `worker-crash-after-send` (sent; the answer is never recorded), `answer-held` (the destination's next answer is held: the attempt is on the wire), `answer-released`, `database-unavailable` and `database-available` (every write fails, as a stopped database; reads still work), and `intake-event-repeated` (the channel sends that lead's conversation event again; answers `{ ok, told: 'recorded', duplicate, eventId }`). Any other kind: 422 |
| POST | `/api/test/firms/{id}/close-cycle` | | `{ rated }`. The firm's billing cycle closes now: its conversations so far are rated and kept (`UsageRated`), as when a statement is locked at a cycle's end |
| GET | `/api/test/events` | | `{ seq, at, name, firmId, eventId, connectionId }[]`: the domain events the lab has recorded, oldest first ([events.json](contracts/events.json)) |
