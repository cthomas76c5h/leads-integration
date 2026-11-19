import type { Delivery, DeliveryStage, DeliveryStatus, FailureClass, HandOff, Tone } from '../api/types';
import { duration } from './format';

// "Accepted" is the strongest claim we can prove: the receiver took the lead.
export const STATUS: Record<DeliveryStatus, { label: string; tone: Tone }> = {
  delivered: { label: 'Accepted', tone: 'ok' },
  pending: { label: 'Retrying', tone: 'warn' },
  delivering: { label: 'Sending', tone: 'warn' },
  failed: { label: 'Needs attention', tone: 'bad' },
  cancelled: { label: 'Cancelled', tone: 'muted' },
  skipped: { label: 'Not sent', tone: 'muted' },
};

export const STAGE: Record<DeliveryStage, string> = {
  queued: 'Queued',
  accepted: 'Accepted by receiver',
  refused: 'Refused by receiver',
  'not-sent': 'Not sent',
};

export type StepState = 'done' | 'now' | 'bad' | 'todo' | 'unknown';
export interface JourneyStep { label: string; state: StepState; note?: string }

export function journey(d: Delivery): JourneyStep[] {
  const sent = d.attempts.length > 0;
  if (d.status === 'skipped' || (d.stage === 'not-sent' && !sent)) {
    return [
      { label: 'Lead published', state: 'done' },
      { label: 'Not sent', state: 'bad', note: d.reason ?? undefined },
      { label: 'Accepted by receiver', state: 'todo' },
      { label: 'In the CRM', state: 'unknown', note: 'not visible to us' },
    ];
  }
  const stopped = d.status === 'failed' || d.status === 'cancelled' || d.stage === 'refused';
  const accepted = d.stage === 'accepted' || d.status === 'delivered';
  return [
    { label: 'Lead published', state: 'done' },
    { label: d.attempts.length > 1 ? `Sent (${d.attempts.length} tries)` : 'Sent', state: sent ? 'done' : 'now' },
    { label: 'Accepted by receiver', state: accepted ? 'done' : stopped ? 'bad' : sent ? 'now' : 'todo' },
    { label: 'In the CRM', state: 'unknown', note: 'not visible to us' },
  ];
}

/** When the lead reached someone at the firm, against its window to act, in plain words. */
export function handOffText(h: HandOff | null, createdAt: number, counts?: { total: number; skipped: number }, now = Date.now()): { text: string; tone: Tone } | null {
  if (!h) return null;
  const window = duration(h.windowMs);
  if (h.state === 'on-time' && h.at !== null) {
    return { text: `Reached the firm in ${duration(h.at - createdAt)}${h.viaFallback ? `, by its fallback (${h.by})` : ''}`, tone: h.viaFallback ? 'warn' : 'ok' };
  }
  if (h.state === 'expired') {
    return h.at !== null
      ? { text: `Expired: reached the firm ${duration(h.at - createdAt)} after the call, past its ${window} window`, tone: 'bad' }
      : counts && counts.total > 0 && counts.skipped === counts.total
        ? { text: `Expired: none of the firm's destinations takes this kind of call, so it reached no one`, tone: 'bad' }
        : counts && counts.total === 0
          ? { text: 'Expired: the firm had no live destination, so it reached no one', tone: 'bad' }
          : { text: `Expired: nobody reached within its ${window} window`, tone: 'bad' };
  }
  return { text: `${duration(Math.max(0, h.dueAt - now))} left of its ${window} window`, tone: 'warn' };
}

/** What kind of problem it is, in the words operators act on (docs/lead-delivery/operations.md#diagnose-a-delivery). */
export const CLASS: Record<FailureClass, { label: string; tone: Tone }> = {
  transient: { label: 'Temporary', tone: 'warn' },
  lead: { label: 'Lead problem', tone: 'muted' },
  configuration: { label: 'Setup problem', tone: 'bad' },
  connection: { label: 'Connection problem', tone: 'bad' },
  ours: { label: 'Our fault', tone: 'bad' },
  routing: { label: 'Routing', tone: 'muted' },
  stopped: { label: 'Destination stopped', tone: 'muted' },
};

/** The receiver's own words when it gave any (the server works this out), else what we know. */
export function problem(d: Delivery): string {
  if (d.diagnosis) return d.diagnosis.cause;
  const last = d.attempts.at(-1);
  return last?.status ? `HTTP ${last.status}` : (last?.error ?? d.reason ?? '');
}

/** One plain next step, by failure class. Null when there is nothing to do. */
export function nextAction(d: Delivery): string | null {
  const g = d.diagnosis;
  if (!g) return null;
  switch (g.class) {
    case 'transient':
      return g.retrying
        ? 'Nothing to do: it retries by itself.'
        : 'No answer after every retry. Send a test lead; once it is accepted, resend.';
    case 'lead':
      return "This lead can't go to this destination as it is. Don't resend: it will be refused again. The firm still has it from its other destinations.";
    case 'configuration':
      return "The destination refuses it because of its setup. Fix the mapping or the destination's settings, send a test lead, then resend.";
    case 'connection':
      return 'Only the firm can fix this: they must sign in again or reconnect the hook. Then send a test lead and resend.';
    case 'ours':
      return "We couldn't build the request. Escalate to an engineer with a link to this delivery; don't resend yet.";
    case 'routing':
      return "Working as set. If the firm wants these calls here, change the destination's routing.";
    case 'stopped':
      return 'The destination was stopped before this was sent. Fix it and reactivate it, then resend.';
  }
}

/** Waits on an operator. A lead problem does not: nothing at the destination can fix it. */
export const needsAttention = (d: Delivery): boolean => d.status === 'failed' && d.diagnosis?.class !== 'lead';

export const canReplay = (d: Delivery): boolean => d.status !== 'pending' && d.status !== 'delivering';

/** A failure later fixed by a replay that was accepted is resolved, not "needs attention". */
export function resolvedSet(rows: Delivery[]): Set<string> {
  const accepted = new Set(rows.filter((d) => d.status === 'delivered').map((d) => `${d.eventId}|${d.connectionId}`));
  return new Set(rows.filter((d) => d.status === 'failed' && accepted.has(`${d.eventId}|${d.connectionId}`)).map((d) => d.id));
}
