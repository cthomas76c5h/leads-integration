// Types from docs/api.md. Change the contract first, then this file.

export type SourceSystem = 'api2' | 'chatbot' | 'airi' | 'manual';
export const SOURCE_SYSTEMS: SourceSystem[] = ['api2', 'chatbot', 'airi', 'manual'];

export type Category = 'platform' | 'crm' | 'automation' | 'notify';
export type AuthScheme = 'api_key' | 'oauth' | 'url' | 'none';

/** One supported destination type. Static, from the backend's catalog. */
export interface Kind {
  kind: string;
  label: string;
  category: Category;
  auth: AuthScheme;
  credentialFields: string[];
  sourceCodes: { system: SourceSystem; code: string }[];
  mappingEnforced: boolean;
  payloadFields: string[];
  notes: string;
}

export interface CredentialField {
  name: string;
  present: boolean;
  fingerprint: string | null;
  meaning: string | null;
}

export type Severity = 'info' | 'warn' | 'bad';
export interface Issue { code: string; severity: Severity; message: string }

export interface Connection {
  id: string;
  firmId: string;
  firmName: string;
  kind: string;
  name: string;
  source: SourceSystem;
  sourceRef: string | null;
  credentials: CredentialField[];
  settings: Record<string, string>;
  enabled: boolean;
  status: 'active' | 'needs-reconnection' | 'disabled';
  disabledReason: string | null;
  consecutiveFailures: number;
  deliveryUrl: string;
  issues: Issue[];
  createdAt: number;
}

export interface FirmSource {
  system: SourceSystem;
  sourceId: string;
  name: string;
  active: boolean;
}

export interface FirmSummary {
  id: string;
  name: string;
  sources: SourceSystem[];
  keyFingerprint: string | null;
  connectionKinds: string[];
  issueCount: number;
  /** Enabled, active destinations: the ones that receive leads. */
  liveDestinations: number;
  active: boolean;
}

export interface FirmDetail extends FirmSummary {
  timeZone: string;
  sourcesDetail: FirmSource[];
  contactTypes: { name: string; active: boolean; mapping: CallMapping }[];
  customLabels: string[];
  connections: Connection[];
  setups: Setup[];
}

export interface OverviewKind {
  kind: string;
  label: string;
  category: Category;
  total: number;
  enabled: number;
  withIssues: number;
  bySource: Partial<Record<SourceSystem, number>>;
}

export interface Overview {
  firms: number;
  firmsBySource: Record<SourceSystem, number>;
  firmsInSeveralSources: number;
  kinds: OverviewKind[];
  issues: { code: string; severity: Severity; message: string; count: number }[];
  imports: ImportRun[];
}

export interface ImportRun {
  id: string;
  system: SourceSystem;
  at: number;
  firms: number;
  connections: number;
  skipped: number;
  notes: string[];
}

export interface Lead {
  firstName: string;
  lastName: string;
  phone: string;
  email: string;
  callType: string;
  message: string;
  source: string;
  webchat: boolean;
  custom: { label: string; data: string }[];
}

export interface LeadEvent { id: string; firmId: string; test: boolean; createdAt: number; lead: Lead }

// ----- Call categories (docs/call-categories) -----

export type CrmDefault = 'send' | 'skip' | 'review' | 'never';

export interface CallMapping {
  category: string;
  practiceArea: string | null;
  modifiers: string[];
  crm: CrmDefault | null;
}

export interface CallCategory {
  id: string;
  label: string;
  description: string;
  isCallCategory: boolean;
  crm: CrmDefault;
  share: number | null;
  examples: string[];
}

export interface Taxonomy {
  categories: CallCategory[];
  practiceAreas: { id: string; label: string; occurrences: number | null; examples: string[] }[];
  modifiers: { id: string; label: string; occurrences: number | null }[];
  agreement: { activeTypes: number; activeTypesClassifiedNewMatter: number; newMatterTypes: number; newMatterTypesActive: number };
}

export interface ReferenceLead {
  id: string;
  why: string;
  expect: CallMapping;
  mapped: CallMapping;
  matches: boolean;
  lead: Lead;
}

export type DeliveryStatus = 'pending' | 'delivering' | 'delivered' | 'failed' | 'cancelled' | 'skipped';
export type DeliveryStage = 'queued' | 'held' | 'accepted' | 'refused' | 'not-sent';

export interface Attempt {
  n: number;
  at: number;
  durationMs: number;
  url: string;
  status: number | null;
  error: string | null;
  outcome: string;
  requestBody: string;
  responseSnippet: string;
  nextAttemptInMs: number | null;
}

export interface Delivery {
  id: string;
  eventId: string;
  connectionId: string;
  connectionName: string;
  kind: string;
  firmId: string;
  firmName: string;
  test: boolean;
  status: DeliveryStatus;
  /** 'accepted' = receiver answered 2xx. Never "in CRM" from HTTP alone. */
  stage: DeliveryStage;
  reason: string | null;
  /** Why this lead goes, or not, to this destination (RFC-009). Null for tests, fallback hand-offs, older deliveries. */
  decision: RoutingDecision | null;
  nextAttemptAt: number | null;
  replayOf: string | null;
  createdAt: number;
  attempts: Attempt[];
  /** What is wrong and why, worked out by the server. Null when nothing is. */
  diagnosis: Diagnosis | null;
}

export interface RoutingDecision {
  outcome: 'planned' | 'not-sent-routing' | 'not-sent-lead';
  rule: string;
  category: string;
  practiceArea: string | null;
  taxonomyVersion: number;
  matchedOn: string | null;
  reason: string | null;
}

/** GLOSSARY.md failure classes, plus 'routing' (not sent on purpose) and 'stopped' (destination stopped first). */
export type FailureClass = 'transient' | 'lead' | 'configuration' | 'connection' | 'ours' | 'routing' | 'stopped';

export interface Diagnosis {
  class: FailureClass;
  /** The receiver's own words when it gave any, else what we know. */
  cause: string;
  /** True while it is still retrying by itself. */
  retrying: boolean;
}

// ----- Destinations -----

export type DestinationState = 'setting-up' | 'live' | 'paused' | 'needs-reconnection' | 'switched-off' | 'off';
/** Most urgent first. See docs/api.md for each one's rule. */
export type DestinationHealth = 'stopped' | 'failing' | 'late' | 'retrying' | 'paused' | 'setting-up' | 'healthy' | 'quiet' | 'off';

export interface DestinationRow {
  connectionId: string;
  name: string;
  kind: string;
  firmId: string;
  firmName: string;
  firmActive: boolean;
  state: DestinationState;
  health: DestinationHealth;
  stoppedReason: string | null;
  failureStreak: number;
  lastAcceptedAt: number | null;
  lastLeadAt: number | null;
  week: { accepted: number; refused: number; notSent: number; retrying: number };
  retryingSince: number | null;
  /** Leads recorded while it was paused or stopped, with no delivery to it. */
  missedLeads: number;
  lastProblem: { deliveryId: string; at: number; diagnosis: Diagnosis } | null;
}

export interface DestinationList {
  total: number;
  counts: Record<DestinationHealth, number>;
  /** Destinations with missed leads, in the same filter. */
  missed: number;
  items: DestinationRow[];
}

export interface DestinationQuery {
  q?: string;
  health?: DestinationHealth | 'missed';
  kind?: string;
  firmActive?: 'all';
  limit?: number;
  offset?: number;
}

export interface DeliveryDetail extends Delivery { event: LeadEvent }

export interface ReceivedRequest {
  at: number;
  path: string;
  kind: string | null;
  mode: string;
  answered: number;
  verdict: string;
  body: string;
}

// ----- Onboarding -----

export interface MapRow { from: string; to: string }
/** A mapping source for one firm, from GET /api/setups/{id}/fields. Custom answers are `custom.<slug>`. */
export interface SourceField { key: string; label: string }
export interface Approval { version: number; decision: 'approved' | 'changes'; by: string; comment: string; at: number }
export interface SetupEvent { at: number; who: 'operator' | 'client' | 'system'; what: string }
export interface ActiveRevision { version: number; recipe: string; kind: string; rows: MapRow[]; at: number }

export type SetupState =
  | 'Connection needed' | 'Draft' | 'Awaiting client approval' | 'Changes requested' | 'Ready to test'
  | 'Testing' | 'Test failed' | 'Test accepted' | 'Ready to go live' | 'Live';

export type Tone = 'ok' | 'warn' | 'bad' | 'muted';

export interface Setup {
  id: string;
  firmId: string;
  firmName: string;
  token: string;
  kind: string;
  kindLabel: string;         // catalog label, also on the client route
  auth: AuthScheme;          // 'url': the client pastes an address; otherwise authorize
  recipe: string;
  rows: MapRow[];
  version: number;
  sentVersion: number | null;
  connectionId: string | null;
  testDeliveryId: string | null;
  clientConfirmedAt: number | null;
  active: ActiveRevision | null;
  approvals: Approval[];
  history: SetupEvent[];
  createdAt: number;
  // computed
  state: SetupState;
  next: string;
  tone: Tone;
  live: boolean;
  activeVersion: number | null;
  revising: boolean;
  enforced: boolean;
  sample: { from: string; to: string; label: string; value: string }[]; // label: source field display name
}

/** What the client's secure link sees. */
export type ClientSetup = Omit<Setup, 'token' | 'connectionId'> & { connected: boolean };

// ----- Request / response shapes -----

export interface FirmQuery {
  q?: string;
  kind?: string;
  source?: SourceSystem | '';
  issues?: boolean;
  limit?: number;
  offset?: number;
}
export interface Page<T> { total: number; items: T[] }

export interface SendLeadBody { variant?: 'full' | 'sparse'; callType?: string; webchat?: boolean; referenceLead?: string }
export interface SendLeadResult { event: LeadEvent; deliveries: Delivery[]; mapping: CallMapping }

export interface Preview { url: string; headers: Record<string, string>; body: string; errors: string[] }

/** Totals for the Delivery Center tiles: every matching delivery, not a page. */
export interface DeliverySummary {
  since: number;
  accepted: number;
  refused: number;
  notSent: number;
  needsAttention: number;
  leadProblems: number;
  retrying: number;
  late: number;
  stoppedDestinations: number;
  /** Production leads in the last 7 days whose window to act passed before any hand-off. */
  expiredLeads: number;
}

export interface DeliveryQuery {
  /** Also 'attention' (newest per lead and destination refused, not a lead problem) and 'late'. */
  status?: DeliveryStatus | 'attention' | 'late' | '';
  firmId?: string;
  connectionId?: string;
  test?: boolean;
  limit?: number;
  offset?: number;
}

export type LeadOutcome = 'expired' | 'none' | 'pending' | 'held' | 'refused' | 'skipped' | 'delivered';

/** When a lead first reached the firm, against its window to act. */
export interface HandOff {
  windowMs: number;
  dueAt: number;
  at: number | null;
  by: string | null;
  viaFallback: boolean;
  state: 'on-time' | 'expired' | 'waiting';
}

export interface LeadSummary {
  id: string;
  firmId: string;
  firmName: string;
  test: boolean;
  createdAt: number;
  caller: string;
  phone: string;
  callType: string;
  webchat: boolean;
  mapping: CallMapping;
  counts: { total: number; delivered: number; refused: number; skipped: number; pending: number; held: number };
  outcome: LeadOutcome;
  /** Null for test leads. */
  handOff: HandOff | null;
}

/** "Your week in calls": one firm's last 7 days in call categories, with proof of delivery. */
export interface FirmReport {
  timeZone: string;
  firmId: string; firmName: string; from: number; to: number;
  calls: number; newMatters: number; urgent: number; afterHours: number; chats: number; hangUps: number;
  byCategory: { id: string; count: number }[];
  byPracticeArea: { id: string; count: number }[];
  handOff: { onTime: number; expired: number; waiting: number; viaFallback: number; medianMs: number | null; urgentOnTime: number; urgentTotal: number };
  destinations: {
    connectionId: string; name: string; kind: string; fallback: boolean; health: DestinationHealth; state: DestinationState;
    accepted: number; refused: number; notSent: number; missedLeads: number;
  }[];
  labels: { label: string; category: string; practiceArea: string | null; count: number }[];
}

export interface LeadDetail extends LeadSummary { lead: Lead; deliveries: Delivery[] }

export interface LeadQuery {
  q?: string;
  firmId?: string;
  category?: string;
  outcome?: LeadOutcome | '';
  test?: boolean;
  limit?: number;
  offset?: number;
}

export type SetupAction = 'send' | 'test' | 'confirm' | 'go-live' | 'discard';

// ----- Billing (docs/billing/strategy.md). Lab data, illustrative prices. -----

export type Tier = 'answer' | 'deliver' | 'insight';
export interface Price { id: string; plan: string; version: number; tier: Tier; name: string; monthlyCents: number; included: number; overageCents: number; effectiveFrom: number }
export interface StatementCheck { code: string; severity: 'bad' | 'warn'; message: string }
export interface Statement {
  timeZone: string;
  firmId: string; firmName: string; cycleFrom: number; cycleTo: number; inProgress: boolean; lockedAt: number | null;
  segments: { planName: string; tier: Tier; from: number; to: number; feeCents: number; included: number; note: string }[];
  conversations: number; billable: number; free: number; units: number; included: number; overage: number;
  byCategory: { category: string; billable: number; free: number }[];
  freeByRule: { rule: string; label: string; count: number }[];
  lines: { label: string; quantity: number; unitCents: number; amountCents: number }[];
  totalCents: number;
  value: { newMatters: number; onTime: number; expired: number; handOffs: number };
  legacy: { minutes: number; noChargeMinutes: number; noChargeAllowance: number; wrapup: number; billedMinutes: number;
            planMinutes: number | null; overageMinutes: number; overageRateCents: number | null; totalCents: number | null; problems: string[] };
  checks: StatementCheck[];
}
export interface BillingAccountRow {
  firmId: string; firmName: string; cycleDay: 1 | 15; plan: string | null; tier: Tier | null; ready: boolean; problems: string[];
  conversations: number; billable: number; totalCents: number; legacyCents: number | null; checks: StatementCheck[];
}

// ----- Terms of service (docs/terms/strategy.md) -----

export type ChangeKind = 'editorial' | 'pricing' | 'material';
export type Require = 'none' | 'admins' | 'all';
export interface TermsVersion { version: number; title: string; summary: string; body: string; changeKind: ChangeKind; require: Require; publishedAt: number; effectiveAt: number; source: 'docx' | 'text' }
export interface TermsCoverage { version: number; asked: number; accepted: number }
export interface TermsGate { mode: 'block' | 'notice' | 'none'; version: number | null; title: string | null; summary: string | null; reason: string | null; dueAt: number | null }
export interface TermsAcceptance { version: number; firmId: string; firmName: string; userId: string; role: 'admin' | 'member'; at: number }
export type PortalRole = 'admin' | 'member' | 'new';
