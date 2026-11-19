import { useEffect, useId, useState } from 'react';
import type { DestinationHealth, DestinationRow, DestinationState, Tone } from '../api/types';
import { api } from '../api/client';
import { Loading } from '../components/Shell';
import { useCatalog } from '../lib/catalog';
import { CLASS } from '../lib/delivery';
import { ago, plural } from '../lib/format';
import { usePoll, useTick } from '../lib/poll';
import { href, replaceQuery } from '../lib/route';
import { useToast } from '../lib/toast';

const LIMIT = 50;

export const HEALTH: Record<DestinationHealth, { label: string; tone: Tone }> = {
  stopped: { label: 'Stopped', tone: 'bad' },
  failing: { label: 'Failing', tone: 'bad' },
  late: { label: 'Late', tone: 'warn' },
  retrying: { label: 'Retrying', tone: 'warn' },
  paused: { label: 'Paused', tone: 'muted' },
  'setting-up': { label: 'Setting up', tone: 'muted' },
  healthy: { label: 'Healthy', tone: 'ok' },
  quiet: { label: 'Quiet', tone: 'muted' },
  off: { label: 'Off in source', tone: 'muted' },
};

const STATE: Record<DestinationState, string> = {
  'setting-up': 'Setting up',
  live: 'Live',
  paused: 'Paused',
  'needs-reconnection': 'Needs reconnection',
  'switched-off': 'Switched off',
  off: 'Never used here',
};

type Filter = DestinationHealth | 'missed' | '';
const FILTERS: Filter[] = ['', 'stopped', 'failing', 'late', 'missed', 'retrying', 'paused', 'setting-up', 'healthy', 'quiet', 'off'];
const asFilter = (s: string | null): Filter => FILTERS.find((f) => f === s) ?? '';
const span = (from: number) => ago(from).replace(' ago', '');

/** Every destination, most urgent first: state, health and the next action, with the buttons that do it. */
export function Destinations({ query }: { query: URLSearchParams }) {
  useTick(5000);
  const kinds = useCatalog();
  const id = useId();
  const [text, setText] = useState(query.get('q') ?? '');
  const [q, setQ] = useState(text);
  const [health, setHealth] = useState<Filter>(asFilter(query.get('health')));
  const [kind, setKind] = useState(query.get('kind') ?? '');
  const [all, setAll] = useState(query.get('all') === '1');
  const [offset, setOffset] = useState(Number(query.get('offset') ?? 0) || 0);

  useEffect(() => {
    const timer = window.setTimeout(() => { setQ(text.trim()); }, 250);
    return () => window.clearTimeout(timer);
  }, [text]);
  useEffect(() => {
    replaceQuery('/destinations', { q, health, kind, all: all ? 1 : null, offset: offset || null });
  }, [q, health, kind, all, offset]);

  const key = JSON.stringify({ q, health, kind, all, offset });
  const list = usePoll(
    () => api.destinations({ q, health: health || undefined, kind, firmActive: all ? 'all' : undefined, limit: LIMIT, offset }),
    key,
  );
  const filter = (f: Filter) => { setHealth(health === f ? '' : f); setOffset(0); };
  const data = list.data;

  return (
    <>
      <h1>Destinations</h1>
      <p className="muted">
        Every place firms' leads go, most urgent first. A stopped destination is quiet: its leads stop arriving rather than failing.
      </p>

      <div className="tiles">
        <Tile f="stopped" on={health} n={data?.counts.stopped} icon="⏸" label="Stopped" note="Switched off or needs reconnection" onClick={filter} />
        <Tile f="failing" on={health} n={data?.counts.failing} icon="⚠" label="Failing" note="Its newest delivery was refused" onClick={filter} />
        <Tile f="late" on={health} n={data?.counts.late} icon="⏱" label="Late" note="Retrying for over 15 minutes" onClick={filter} tone="warn" />
        <Tile f="missed" on={health} n={data?.missed} icon="↺" label="With missed leads" note="Leads that arrived while it was off" onClick={filter} tone="warn" />
      </div>

      <section className="card">
        <div className="filters" role="search">
          <label className="field" htmlFor={`${id}-q`}>Search
            <input id={`${id}-q`} type="search" value={text} placeholder="Firm, destination or kind…" onChange={(e) => { setText(e.target.value); setOffset(0); }} />
          </label>
          <label className="field" htmlFor={`${id}-h`}>Health
            <select id={`${id}-h`} value={health} onChange={(e) => { setHealth(asFilter(e.target.value)); setOffset(0); }}>
              <option value="">Any health</option>
              {FILTERS.filter(Boolean).map((f) => (
                <option key={f} value={f}>
                  {f === 'missed' ? 'With missed leads' : HEALTH[f as DestinationHealth].label}
                  {data ? ` (${f === 'missed' ? data.missed : data.counts[f as DestinationHealth]})` : ''}
                </option>
              ))}
            </select>
          </label>
          <label className="field" htmlFor={`${id}-k`}>Kind
            <select id={`${id}-k`} value={kind} onChange={(e) => { setKind(e.target.value); setOffset(0); }}>
              <option value="">Every kind</option>
              {(kinds ?? []).map((k) => <option key={k.kind} value={k.kind}>{k.label}</option>)}
            </select>
          </label>
          <label className="check-inline">
            <input type="checkbox" checked={all} onChange={(e) => { setAll(e.target.checked); setOffset(0); }} /> Include inactive firms
          </label>
        </div>
      </section>

      <section className="card" aria-label="Destinations" aria-busy={!data}>
        {!data ? <Loading error={list.error} /> : (
          <>
            <div className="table-wrap">
              <table className="data destinations">
                <thead>
                  <tr><th>Destination</th><th>Health</th><th>Last accepted</th><th>Last 7 days</th><th className="num">Missed</th><th>Next action</th></tr>
                </thead>
                <tbody>
                  {data.items.map((r) => <Row key={r.connectionId} r={r} onChanged={list.reload} />)}
                  {data.items.length === 0 && (
                    <tr><td colSpan={6} className="muted empty-note">No destination matches.</td></tr>
                  )}
                </tbody>
              </table>
            </div>
            <nav className="pager" aria-label="Pages">
              <span className="small muted">{data.total === 0 ? '0' : `${offset + 1}–${Math.min(offset + LIMIT, data.total)}`} of {data.total}</span>
              <button type="button" className="btn" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - LIMIT))}>← Previous</button>
              <button type="button" className="btn" disabled={offset + LIMIT >= data.total} onClick={() => setOffset(offset + LIMIT)}>Next →</button>
            </nav>
          </>
        )}
      </section>
    </>
  );
}

function Tile({ f, on, n, icon, label, note, onClick, tone = 'bad' }: {
  f: Filter; on: Filter; n: number | undefined; icon: string; label: string; note: string; onClick: (f: Filter) => void; tone?: 'bad' | 'warn';
}) {
  const hot = (n ?? 0) > 0;
  return (
    <button type="button" className={`tile ${hot ? (tone === 'bad' ? 'alert' : 'warn-tile') : ''} ${on === f ? 'on' : ''}`} aria-pressed={on === f} onClick={() => onClick(f)}>
      <span className={`tile-icon ${tone}`} aria-hidden="true">{icon}</span><span>{label}</span><strong>{n ?? '…'}</strong><small className="muted">{note}</small>
    </button>
  );
}

function Row({ r, onChanged }: { r: DestinationRow; onChanged: () => void }) {
  const { act } = useToast();
  const h = HEALTH[r.health];
  const w = r.week;
  const live = r.state === 'live';
  const problemLink = r.lastProblem && href('/deliveries', { d: r.lastProblem.deliveryId, firm: r.firmId });
  const reactivate = () => void act(() => api.patchConnection(r.connectionId, { reactivate: true }), 'Reactivated').then(onChanged);
  const resume = () => void act(() => api.patchConnection(r.connectionId, { enabled: true }), 'Resumed').then(onChanged);
  const test = () => void act(() => api.testConnection(r.connectionId), 'Test lead sent: see Delivery Center → Tests').then(onChanged);
  const sendMissed = () => {
    if (!window.confirm(`Send ${plural(r.missedLeads, 'missed lead')} to ${r.name} at ${r.firmName}? They are sent as they are, oldest first; routing still applies.`)) return;
    void act(() => api.sendMissed(r.connectionId), (x) => `${plural(x.queued, 'lead')} queued${x.notSent ? `, ${x.notSent} not sent by routing` : ''}`).then(onChanged);
  };

  const [advice, buttons] = ((): [string, { label: string; run?: () => void; href?: string }[]] => {
    const open = problemLink ? [{ label: 'Open the last refusal', href: problemLink }] : [];
    switch (r.health) {
      case 'stopped':
        return r.state === 'needs-reconnection'
          ? ['Only the firm can fix this: they must sign in again or reconnect. Then reactivate.', [...open, { label: 'Reactivate', run: reactivate }]]
          : ['Switched off after repeated failures. Find the cause, fix it, then reactivate.', [...open, { label: 'Reactivate', run: reactivate }]];
      case 'failing':
        return ['Its newest delivery was refused. Open it, fix the cause, then resend.', [...open, { label: 'Send a test lead', run: test }]];
      case 'late':
        return [`Retrying for ${r.retryingSince ? span(r.retryingSince) : 'a while'}. Check it answers.`, [
          { label: 'See its retries', href: href('/deliveries', { firm: r.firmId, status: 'pending' }) }, { label: 'Send a test lead', run: test }]];
      case 'retrying':
        return ['Retrying by itself.', []];
      case 'paused':
        return ['Paused: new leads are held for it and sent when it resumes; their hand-offs go to the fallback.', [{ label: 'Resume', run: resume }]];
      case 'setting-up':
        return ['Test leads only until go-live.', [{ label: 'Onboarding', href: href('/onboarding') }]];
      case 'healthy':
        return ['Nothing to do.', []];
      case 'quiet':
        return ['No leads in the last 7 days.', []];
      case 'off':
        return ['Switched off in its source system. Turn it on only if the firm wants it.', [{ label: 'Turn on', run: resume }]];
    }
  })();
  const missedNote = r.missedLeads > 0 && live
    ? `${plural(r.missedLeads, 'lead')} arrived while it was off and never reached it. Send them if the firm still wants them.`
    : null;
  if (missedNote) buttons.push({ label: `Send ${plural(r.missedLeads, 'missed lead')}`, run: sendMissed });

  return (
    <tr>
      <td>
        <strong>{r.name}</strong>
        <span className="sub">{r.kind}{r.firmActive ? '' : ' · firm inactive'}</span>
        <a className="sub" href={href('/flow', { firm: r.firmId })}>{r.firmName}</a>
      </td>
      <td>
        <span className={`badge ${h.tone}`}>{h.label}</span>
        <span className="sub">{STATE[r.state]}{r.failureStreak > 0 ? ` · ${plural(r.failureStreak, 'failure')} in a row` : ''}</span>
      </td>
      <td>{r.lastAcceptedAt ? ago(r.lastAcceptedAt) : <span className="muted">never</span>}
        {r.lastLeadAt && <span className="sub">last lead {ago(r.lastLeadAt)}</span>}</td>
      <td className="small">
        {w.accepted + w.refused + w.notSent + w.retrying === 0 ? <span className="muted">none</span> : (
          <>
            <span className="ok">{w.accepted} accepted</span>
            {w.refused > 0 && <><br /><span className="bad">{w.refused} refused</span></>}
            {w.retrying > 0 && <><br /><span className="warn-text">{w.retrying} retrying</span></>}
            {w.notSent > 0 && <><br /><span className="muted">{w.notSent} not sent</span></>}
          </>
        )}
      </td>
      <td className="num">{r.missedLeads > 0 ? <strong className="warn-text">{r.missedLeads}</strong> : <span className="muted">0</span>}</td>
      <td className="next">
        {r.lastProblem && r.health !== 'late' && (
          <span className="cause"><span className={`chip ${CLASS[r.lastProblem.diagnosis.class].tone}`}>{CLASS[r.lastProblem.diagnosis.class].label}</span> {r.lastProblem.diagnosis.cause}</span>
        )}
        {r.stoppedReason && !r.lastProblem && <span className="cause">{r.stoppedReason}</span>}
        {!(missedNote && (r.health === 'healthy' || r.health === 'quiet')) && <span className="small">{advice}</span>}
        {missedNote && <span className="small">{missedNote}</span>}
        {r.missedLeads > 0 && !live && <span className="small muted">Its {plural(r.missedLeads, 'missed lead')} can be sent once it is live.</span>}
        {buttons.length > 0 && (
          <span className="row-actions">
            {buttons.map((b) => b.href
              ? <a key={b.label} className="btn small-btn" href={b.href}>{b.label}</a>
              : <button key={b.label} type="button" className="btn small-btn" onClick={b.run}>{b.label}</button>)}
          </span>
        )}
      </td>
    </tr>
  );
}
