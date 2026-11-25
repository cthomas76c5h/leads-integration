// Small typed client for docs/api.md. Only the routes listed there.

import type {
  Resend,
  BillingAccountRow, ChangeKind, PortalRole, Price, Require, Statement, TermsAcceptance, TermsCoverage, TermsGate, TermsVersion,
  CallMapping, ClientSetup, Connection, Delivery, DeliveryDetail, DeliveryQuery, DeliverySummary, DestinationList, FirmReport, DestinationQuery, FirmDetail, FirmQuery, FirmSummary, Kind, MapRow,
  LeadDetail, LeadQuery, LeadSummary, Overview, Page, Preview, ReceivedRequest, ReferenceLead, SendLeadBody, SendLeadResult, Setup,
  SetupAction, SourceField, Taxonomy,
} from './types';

type Method = 'GET' | 'POST' | 'PATCH';

export async function request<T>(method: Method, path: string, body?: unknown): Promise<T> {
  const res = await fetch(path, {
    method,
    headers: body === undefined ? { accept: 'application/json' } : { 'content-type': 'application/json', accept: 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await res.text();
  let data: unknown = null;
  if (text) {
    try { data = JSON.parse(text); } catch { data = null; }
  }
  if (!res.ok) {
    const err = data && typeof data === 'object' && 'error' in data && typeof data.error === 'string' ? data.error : null;
    throw new Error(err ?? `HTTP ${res.status}`);
  }
  return data as T;
}

type QueryValue = string | number | boolean | undefined | null;
function qs(params: Record<string, QueryValue>): string {
  const sp = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) {
    if (v === undefined || v === null || v === '') continue;
    if (k === 'issues') { if (v === true) sp.set(k, '1'); continue; } // issues=1 or absent
    sp.set(k, String(v));
  }
  const s = sp.toString();
  return s ? `?${s}` : '';
}

const enc = encodeURIComponent;

export const api = {
  health: () => request<{ ok: true }>('GET', '/api/health'),
  catalog: () => request<Kind[]>('GET', '/api/catalog'),
  overview: () => request<Overview>('GET', '/api/overview'),

  firms: (q: FirmQuery) => request<Page<FirmSummary>>('GET', `/api/firms${qs({ ...q })}`),
  firm: (id: string) => request<FirmDetail>('GET', `/api/firms/${enc(id)}`),
  statement: (firmId: string, cycle: 'current' | 'previous' = 'current') => request<Statement>('GET', `/api/firms/${enc(firmId)}/statement${qs({ cycle })}`),
  changePlan: (firmId: string, priceId: string) => request<{ startsAt: number; statement: Statement }>('POST', `/api/firms/${enc(firmId)}/plan`, { priceId }),
  priceBook: () => request<Price[]>('GET', '/api/billing/price-book'),
  billingAccounts: () => request<BillingAccountRow[]>('GET', '/api/billing/accounts'),
  terms: () => request<{ versions: TermsVersion[]; coverage: TermsCoverage[] }>('GET', '/api/terms'),
  publishTerms: (body: { title?: string; summary: string; body: string; changeKind: ChangeKind; require?: Require; effectiveAt?: number; source?: 'docx' | 'text' }) =>
    request<{ version: number }>('POST', '/api/terms', body),
  termsAcceptances: (version: number, limit = 50) => request<TermsAcceptance[]>('GET', `/api/terms/${version}/acceptances${qs({ limit })}`),
  termsGate: (firmId: string, role: PortalRole) => request<TermsGate>('GET', `/api/portal/terms${qs({ firm: firmId, role })}`),
  acceptTerms: (firmId: string, role: PortalRole, version: number) => request<TermsGate>('POST', '/api/portal/terms/accept', { firmId, role, version }),
  firmReport: (id: string) => request<FirmReport>('GET', `/api/firms/${enc(id)}/report`),
  createFirm: (name: string) => request<FirmDetail>('POST', '/api/firms', { name }),
  sendLead: (firmId: string, body: SendLeadBody) => request<SendLeadResult>('POST', `/api/firms/${enc(firmId)}/leads`, body),

  callCategories: () => request<Taxonomy>('GET', '/api/call-categories'),
  classify: (name: string) => request<CallMapping>('GET', `/api/call-categories/classify${qs({ name })}`),
  referenceLeads: () => request<ReferenceLead[]>('GET', '/api/reference-leads'),

  connections: (q: { kind?: string; status?: 'active' | 'needs-reconnection' | 'disabled' | 'stopped' } = {}) => request<Connection[]>('GET', `/api/connections${qs(q)}`),
  createConnection: (firmId: string, body: { kind: string; name?: string; deliveryUrl?: string; settings?: Record<string, string> }) =>
    request<Connection>('POST', `/api/firms/${enc(firmId)}/connections`, body),
  patchConnection: (id: string, body: { enabled?: boolean; deliveryUrl?: string; reactivate?: true; settings?: Record<string, string> }) =>
    request<Connection>('PATCH', `/api/connections/${enc(id)}`, body),
  preview: (id: string, variant: 'full' | 'sparse' = 'full') => request<Preview>('GET', `/api/connections/${enc(id)}/preview${qs({ variant })}`),
  testConnection: (id: string) => request<Delivery>('POST', `/api/connections/${enc(id)}/test`),
  sendMissed: (id: string) => request<{ queued: number; notSent: number }>('POST', `/api/connections/${enc(id)}/send-missed`),
  destinations: (q: DestinationQuery = {}) => request<DestinationList>('GET', `/api/destinations${qs({ ...q })}`),

  leads: (q: LeadQuery = {}) => request<Page<LeadSummary>>('GET', `/api/leads${qs({ ...q })}`),
  lead: (id: string) => request<LeadDetail>('GET', `/api/leads/${enc(id)}`),

  deliveries: (q: DeliveryQuery = {}) => request<Delivery[]>('GET', `/api/deliveries${qs({ ...q })}`),
  delivery: (id: string) => request<DeliveryDetail>('GET', `/api/deliveries/${enc(id)}`),
  deliverySummary: (q: { test?: boolean; firmId?: string; since?: number } = {}) =>
    request<DeliverySummary>('GET', `/api/deliveries/summary${qs({ ...q })}`),
  replay: (id: string, who?: Partial<Resend>) => request<Delivery>('POST', `/api/deliveries/${enc(id)}/replay`, who),
  resolve: (id: string, body: { resolution: 'found' | 'closed'; by: string; why: string }) =>
    request<Delivery>('POST', `/api/deliveries/${enc(id)}/resolve`, body),
  received: (limit = 50) => request<ReceivedRequest[]>('GET', `/api/received${qs({ limit })}`),

  setups: () => request<Setup[]>('GET', '/api/setups'),
  setupFields: (id: string) => request<SourceField[]>('GET', `/api/setups/${enc(id)}/fields`),
  createSetup: (body: { firmId: string; kind: string; recipe: string; cloneFrom?: string }) => request<Setup>('POST', '/api/setups', body),
  patchSetup: (id: string, body: { recipe?: string; rows?: MapRow[] }) => request<Setup>('PATCH', `/api/setups/${enc(id)}`, body),
  setupAction: (id: string, action: SetupAction) => request<Setup>('POST', `/api/setups/${enc(id)}/${action}`),

  clientSetup: (token: string) => request<ClientSetup>('GET', `/api/client/setups/${enc(token)}`),
  clientConnect: (token: string, url?: string) => request<{ ok: true }>('POST', `/api/client/setups/${enc(token)}/connect`, url ? { url } : {}),
  clientDecision: (token: string, body: { decision: 'approved' | 'changes'; by: string; comment: string; version: number }) =>
    request<{ ok: true }>('POST', `/api/client/setups/${enc(token)}/decision`, body),
};
