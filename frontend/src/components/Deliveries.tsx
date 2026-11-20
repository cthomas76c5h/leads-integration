import { useState } from 'react';
import type { Delivery, FailureClass, ReceivedRequest, Setup } from '../api/types';
import { MappingFix, PreviewFix } from './Fix';
import { api } from '../api/client';
import { canReplay, CLASS, journey, nextAction, problem, STAGE, STATUS } from '../lib/delivery';
import { href } from '../lib/route';
import { ago, leadNo, plural, pretty } from '../lib/format';
import { useToast } from '../lib/toast';

export function Journey({ d }: { d: Delivery }) {
  return (
    <ol className="journey" aria-label="Delivery journey">
      {journey(d).map((s) => (
        <li key={s.label} className={s.state}>
          <span>{s.label}</span>
          {s.note && <small>{s.note}</small>}
        </li>
      ))}
    </ol>
  );
}

export function StatusBadge({ d, resolved = false }: { d: Delivery; resolved?: boolean }) {
  if (resolved) return <span className="badge muted">Resolved by resend</span>;
  // Not sent, or refused, for something the lead lacks: nothing at the destination can fix it.
  if ((d.status === 'failed' || d.status === 'skipped') && d.diagnosis?.class === 'lead') return <span className="badge muted">Lead problem</span>;
  // Waiting for its destination to resume or be reconnected: not retrying.
  // Accepted, yet a lookup has not found it in time: the record may be lost at the destination.
  if (d.status === 'delivered' && d.verifyOverdue) return <span className="badge bad" title="Accepted, but a lookup has not found the record">Not found in CRM</span>;
  if (d.status === 'delivered' && d.verifiedAt !== null) return <span className="badge ok" title="Found by our lead reference">Verified</span>;
  if (d.stage === 'held') return <span className="badge warn" title={d.reason?.includes('reconnection') ? 'Held until the firm reconnects' : 'Held until the destination resumes'}>Held</span>;
  const s = STATUS[d.status];
  return <span className={`badge ${s.tone}`}>{s.label}</span>;
}

export function Attempts({ d }: { d: Delivery }) {
  if (d.attempts.length === 0) {
    const rule = d.decision ? ` Rule: ${d.decision.rule}; this call: ${d.decision.category}${d.decision.practiceArea ? `, ${d.decision.practiceArea}` : ''}.` : '';
    return <p className="muted small">{d.status === 'skipped' ? 'Not sent' : 'No attempt yet'}{d.reason ? `: ${d.reason}` : ''}.{rule}</p>;
  }
  return (
    <>
      {d.attempts.map((a) => (
        <div key={a.n} className={`attempt ${a.outcome === 'delivered' ? 'ok' : 'bad'}`}>
          <div>
            <strong>#{a.n}</strong> {a.status ? `HTTP ${a.status}` : (a.error ?? 'no answer')} · {a.durationMs} ms · {a.outcome}
            {a.nextAttemptInMs !== null && <span className="muted"> · next try in {Math.ceil(a.nextAttemptInMs / 1000)}s</span>}
          </div>
          <div className="url muted">POST {a.url}</div>
          <details className="raw">
            <summary>Request body</summary>
            <pre className="out">{pretty(a.requestBody)}</pre>
          </details>
          {a.responseSnippet && (
            <details className="raw" open={a.outcome !== 'delivered'}>
              <summary>Response</summary>
              <pre className="out">{pretty(a.responseSnippet)}</pre>
            </details>
          )}
        </div>
      ))}
    </>
  );
}

interface Action { label: string; run?: () => void; href?: string; disabled?: boolean }

/**
 * What is wrong, in the receiver's words, what to do, and the buttons that do it:
 * all chosen by the failure class (docs/lead-delivery/operations.md#diagnose-a-delivery).
 */
export function ProblemPanel({ d, destinationStopped, setup, onResend, onChanged }: {
  d: Delivery; destinationStopped: boolean; setup?: Setup | null; onResend: () => void; onChanged: () => void;
}) {
  const { act } = useToast();
  const [fixing, setFixing] = useState<'mapping' | 'preview' | null>(null);
  const g = d.diagnosis;
  if (!g) return null;
  const c = CLASS[g.class];
  const next = nextAction(d);
  const resend: Action = { label: '▷ Resend', run: onResend, disabled: !canReplay(d) };
  const test: Action = { label: 'Send a test lead', run: () => void act(() => api.testConnection(d.connectionId), 'Test lead sent: see the Tests tab').then(onChanged) };
  const reactivate: Action = { label: 'Reactivate the destination', run: () => void act(() => api.patchConnection(d.connectionId, { reactivate: true }), 'Destination reactivated').then(onChanged) };
  // A paused destination holds new leads for the record; their hand-offs go to the firm's fallback at once.
  const pause: Action = { label: '❚❚ Pause the destination', run: () => void act(() => api.patchConnection(d.connectionId, { enabled: false }), 'Paused: new leads are held until it is resumed').then(onChanged) };
  const copy: Action = { label: 'Copy a link for an engineer', run: () => void act(() => navigator.clipboard.writeText(location.href), 'Link copied') };
  const openLead: Action = { label: 'Open the lead', href: href('/leads', { lead: d.eventId }) };
  // A stopped destination takes no test and no resend: the one step is to bring it back, once that is safe.
  const whenStopped: Partial<Record<FailureClass, string>> = {
    connection: 'Reactivate once the firm has reconnected',
    configuration: 'Reactivate once the setup is fixed',
    transient: 'Reactivate once it answers again',
    stopped: 'Reactivate once it is fixed',
  };
  // A setup problem is fixed where the body is made: our mapping (a webhook with a setup), or the kind's own body.
  const fix: Action = setup?.enforced
    ? { label: '✎ Edit mapping', run: () => setFixing('mapping') }
    : { label: 'See what we send', run: () => setFixing('preview') };
  const actions: Action[] = (() => {
    const back = whenStopped[g.class];
    if (destinationStopped && back) return g.class === 'configuration' ? [fix, { ...reactivate, label: back }] : [{ ...reactivate, label: back }];
    switch (g.class) {
      case 'transient': return g.retrying ? [] : [test, resend];
      case 'lead': return [openLead, { ...resend, label: 'Resend anyway' }];
      case 'configuration': return [fix, resend, test, pause];
      case 'connection': return [test, resend];
      case 'ours': return [copy];
      case 'routing': return [{ label: "See the firm's call types", href: `#/firms/${encodeURIComponent(d.firmId)}` }];
      case 'stopped': return [resend];
      case 'unknown': return [openLead, { ...resend, label: 'Resend: it is missing' }];
    }
  })();
  return (
    <>
      <div className={`problem ${c.tone}`} role="status">
        <span className={`badge ${c.tone}`}>{c.label}</span>
        <strong>{g.cause}</strong>
        {next && <span>{next}</span>}
        {destinationStopped && whenStopped[g.class] && <span className="small muted">The destination is stopped: nothing is sent to it until it is reactivated.</span>}
      </div>
      {actions.length > 0 && (
        <div className="problem-actions">
          {actions.map((a, i) => a.href
            ? <a key={a.label} className={`btn wide ${i === 0 ? 'primary' : ''}`} href={a.href}>{a.label}</a>
            : <button key={a.label} type="button" className={`btn wide ${i === 0 ? 'primary' : ''}`} disabled={a.disabled} onClick={a.run}>{a.label}</button>)}
        </div>
      )}
      {fixing === 'mapping' && setup && <MappingFix d={d} setup={setup} onClose={() => setFixing(null)} onChanged={onChanged} />}
      {fixing === 'preview' && <PreviewFix d={d} onClose={() => setFixing(null)} />}
    </>
  );
}

/**
 * Resending an accepted lead can put a duplicate in the firm's CRM: only a senior operator may, with the
 * firm's reason (docs/lead-delivery/answers.md). The lab has no sign-in, so the role is declared here.
 */
function ResendAccepted({ d, onDone, onCancel }: { d: Delivery; onDone: () => void; onCancel: () => void }) {
  const { act } = useToast();
  const [by, setBy] = useState('');
  const [why, setWhy] = useState('');
  const [senior, setSenior] = useState(false);
  const submit = (e: React.FormEvent) => {
    e.preventDefault();
    void act(() => api.replay(d.id, { by: by.trim() || null, role: senior ? 'senior-operator' : 'operator', why: why.trim() }), 'Resent with the same lead identity')
      .then(onDone);
  };
  return (
    <form className="problem warn resend-accepted" onSubmit={submit}>
      <strong>{d.connectionName} already accepted this lead.</strong>
      <span>Resending can create a duplicate. Only a senior operator may, once the firm confirms the record is missing.</span>
      <label className="field">Your name<input value={by} onChange={(e) => setBy(e.target.value)} autoComplete="name" /></label>
      <label className="field">Why (the firm's words)<input required value={why} onChange={(e) => setWhy(e.target.value)} placeholder="Firm confirmed the record is missing" /></label>
      <label><input type="checkbox" checked={senior} onChange={(e) => setSenior(e.target.checked)} /> I am a senior operator</label>
      <div className="problem-actions">
        <button type="submit" className="btn primary" disabled={!senior || !why.trim()}>Resend</button>
        <button type="button" className="btn" onClick={onCancel}>Cancel</button>
      </div>
    </form>
  );
}

const roleLabel = (role: string) => (role === 'senior-operator' ? 'senior operator' : 'operator');

/** Expandable list of deliveries with the journey, the problem, attempts and Resend. */
export function DeliveryList({ rows, showConnection = false, showFirm = false, empty = 'Nothing sent yet.', onChanged }: {
  rows: Delivery[]; showConnection?: boolean; showFirm?: boolean; empty?: string; onChanged?: () => void;
}) {
  const { act } = useToast();
  const [asking, setAsking] = useState<string | null>(null);
  if (!rows.length) return <p className="muted">{empty}</p>;
  return (
    <div className="deliveries">
      {rows.map((d) => {
        const next = nextAction(d);
        return (
          <div key={d.id}>
          <details className={`delivery ${d.status}`}>
            <summary>
              <span className="dot" aria-hidden="true" />
              <span className="grow ellipsis">
                <span className="mono">{leadNo(d.eventId)}</span>
                {d.test && <> <span className="chip">test</span></>}
                {d.replayOf && <> <span className="chip" title={d.resend ? `By ${d.resend.by ?? 'an operator'} (${roleLabel(d.resend.role)})${d.resend.why ? `: ${d.resend.why}` : ''}` : undefined}>resend</span></>}
                {showFirm && <> · {d.firmName}</>}
                {showConnection && <> · {d.connectionName}</>}
              </span>
              <span className="muted">{STAGE[d.stage]} · {plural(d.attempts.length, 'try', 'tries')} · {ago(d.createdAt)}</span>
              {canReplay(d) && (
                <button type="button" className="btn small-btn"
                  onClick={(e) => {
                    e.preventDefault();
                    if (d.status === 'delivered') setAsking(d.id);
                    else void act(() => api.replay(d.id), 'Resent with the same lead identity').then(() => onChanged?.());
                  }}>
                  Resend
                </button>
              )}
            </summary>
            <Journey d={d} />
            {d.resend && (
              <p className="small muted">Resent by {d.resend.by ?? 'an operator'} ({roleLabel(d.resend.role)}){d.resend.why ? `: ${d.resend.why}` : ''}</p>
            )}
            {d.diagnosis && !d.diagnosis.retrying && (
              <div className={`problem ${CLASS[d.diagnosis.class].tone}`}>
                <span className={`badge ${CLASS[d.diagnosis.class].tone}`}>{CLASS[d.diagnosis.class].label}</span>
                <strong>{problem(d)}</strong>
                {next && <span>{next}</span>}
              </div>
            )}
            <Attempts d={d} />
          </details>
          {asking === d.id && <ResendAccepted d={d} onCancel={() => setAsking(null)} onDone={() => { setAsking(null); onChanged?.(); }} />}
          </div>
        );
      })}
    </div>
  );
}

/** What the fake receivers saw, and their verdict. */
export function ReceivedList({ rows }: { rows: ReceivedRequest[] }) {
  if (!rows.length) return <p className="muted">Nothing received yet.</p>;
  return (
    <div className="deliveries">
      {rows.map((r, i) => {
        const ok = r.answered >= 200 && r.answered < 300;
        return (
          <details key={`${r.at}-${i}`} className={`delivery ${ok ? 'delivered' : 'failed'}`}>
            <summary>
              <span className="dot" aria-hidden="true" />
              <span className="grow mono ellipsis">{r.path}</span>
              {r.kind && <span className="chip">{r.kind}</span>}
              <span className={ok ? 'verdict-ok' : 'verdict-bad'}>{r.verdict}</span>
              <span className="muted">{r.mode} · HTTP {r.answered} · {ago(r.at)}</span>
            </summary>
            <pre className="out">{pretty(r.body)}</pre>
          </details>
        );
      })}
    </div>
  );
}
