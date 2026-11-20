import { useEffect, useId, useState } from 'react';
import type { LeadOutcome, LeadSummary, Tone } from '../api/types';
import { api } from '../api/client';
import { StatusBadge } from '../components/Deliveries';
import { MappingChips } from '../components/Mapping';
import { Loading } from '../components/Shell';
import { labels, useTaxonomy } from '../lib/categories';
import { callTheFirm, CLASS, confirmationText, handOffText, problem, STAGE } from '../lib/delivery';
import { leadNo, plural, when } from '../lib/format';
import { usePoll } from '../lib/poll';
import { href, replaceQuery } from '../lib/route';

const LIMIT = 50;

const OUTCOMES: [LeadOutcome | '', string][] = [
  ['', 'Any outcome'], ['expired', 'Missed its window (late or not reached)'], ['delivered', 'Delivered'], ['refused', 'Refused'], ['pending', 'In progress'],
  ['held', 'Held (destination paused)'], ['skipped', 'Not sent'], ['none', 'No destination'],
];
const OUTCOME: Record<LeadOutcome, { label: string; tone: Tone }> = {
  expired: { label: 'Expired', tone: 'bad' },
  delivered: { label: 'Delivered', tone: 'ok' },
  refused: { label: 'Refused', tone: 'bad' },
  pending: { label: 'In progress', tone: 'warn' },
  held: { label: 'Held', tone: 'warn' },
  skipped: { label: 'Not sent', tone: 'muted' },
  none: { label: 'No destination', tone: 'muted' },
};
const asOutcome = (s: string | null): LeadOutcome | '' => OUTCOMES.find(([k]) => k === s)?.[0] ?? '';

/** Every lead (event), newest first, with what happened to its deliveries. */
export function Leads({ query }: { query: URLSearchParams }) {
  const t = useTaxonomy();
  const l = labels(t);
  const [text, setText] = useState(query.get('q') ?? '');
  const [q, setQ] = useState(text);
  const [category, setCategory] = useState(query.get('category') ?? '');
  const [outcome, setOutcome] = useState<LeadOutcome | ''>(asOutcome(query.get('outcome')));
  const [tests, setTests] = useState(query.get('tests') === '1');
  const [firmId, setFirmId] = useState(query.get('firm') ?? '');
  const [offset, setOffset] = useState(Number(query.get('offset') ?? 0) || 0);
  const [selected, setSelected] = useState<string | null>(query.get('lead'));
  const id = useId();

  useEffect(() => {
    const timer = window.setTimeout(() => { setQ(text.trim()); }, 250);
    return () => window.clearTimeout(timer);
  }, [text]);
  useEffect(() => {
    replaceQuery('/leads', { q, category, outcome, tests: tests ? 1 : null, firm: firmId, offset: offset || null, lead: selected });
  }, [q, category, outcome, tests, firmId, offset, selected]);

  const key = JSON.stringify({ q, category, outcome, tests, firmId, offset });
  const { data, error } = usePoll(
    () => api.leads({ q, category, outcome, test: tests, firmId: firmId || undefined, limit: LIMIT, offset }),
    key,
  );
  const reset = <T,>(set: (v: T) => void) => (v: T) => { set(v); setOffset(0); };
  const firmName = data?.items.find((x) => x.firmId === firmId)?.firmName;

  return (
    <>
      <h1>Leads</h1>
      <p className="muted">Every lead Answering AI took, newest first, and what happened to it at each destination.</p>

      <section className="card">
        <div className="filters" role="search">
          <label className="field" htmlFor={`${id}-q`}>Search
            <input id={`${id}-q`} type="search" value={text} placeholder="Caller, phone, call type or firm…" onChange={(e) => { setText(e.target.value); setOffset(0); }} />
          </label>
          <label className="field" htmlFor={`${id}-c`}>Category
            <select id={`${id}-c`} value={category} onChange={(e) => reset(setCategory)(e.target.value)}>
              <option value="">All categories</option>
              {(t?.categories ?? []).map((c) => <option key={c.id} value={c.id}>{c.label}</option>)}
              <option value="unmapped">Unmapped</option>
            </select>
          </label>
          <label className="field" htmlFor={`${id}-o`}>Outcome
            <select id={`${id}-o`} value={outcome} onChange={(e) => reset(setOutcome)(asOutcome(e.target.value))}>
              {OUTCOMES.map(([k, v]) => <option key={k} value={k}>{v}</option>)}
            </select>
          </label>
          <label className="field" htmlFor={`${id}-t`}>Kind
            <select id={`${id}-t`} value={tests ? 'tests' : 'production'} onChange={(e) => reset(setTests)(e.target.value === 'tests')}>
              <option value="production">Production leads</option>
              <option value="tests">Tests</option>
            </select>
          </label>
          {firmId && (
            <span className="chip">
              Firm: {firmName ?? 'selected'}{' '}
              <button type="button" className="link-btn" aria-label="Show every firm" onClick={() => reset(setFirmId)('')}>✕</button>
            </span>
          )}
        </div>
      </section>

      <div className={`ops-grid ${selected ? 'with-drawer leads-grid' : ''}`}>
        <section className="card" aria-label="Leads" aria-busy={!data}>
          {!data ? <Loading error={error} /> : (
            <>
              <p className="small muted" aria-live="polite">{plural(data.total, 'lead')}{q || category || outcome || firmId ? ' match' : ''}</p>
              <div className="table-wrap">
                <table className="data">
                  <thead>
                    <tr><th>Lead</th><th>Caller</th><th>Firm</th><th>Call type</th><th>Delivery</th></tr>
                  </thead>
                  <tbody>
                    {data.items.map((x) => <Row key={x.id} x={x} on={x.id === selected} onOpen={() => setSelected(x.id === selected ? null : x.id)} onFirm={() => reset(setFirmId)(x.firmId)} l={l} />)}
                    {data.items.length === 0 && (
                      <tr><td colSpan={5} className="muted empty-note">
                        No lead matches. Send one from a firm page or the Call categories page, or run <code>dotnet run --project src/Import -- seed</code>.
                      </td></tr>
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
        {selected && <LeadDrawer id={selected} onClose={() => setSelected(null)} l={l} />}
      </div>
    </>
  );
}

function Row({ x, on, onOpen, onFirm, l }: { x: LeadSummary; on: boolean; onOpen: () => void; onFirm: () => void; l: ReturnType<typeof labels> }) {
  // A missed window says which kind of miss: reached late, or not reached at all.
  const o = x.outcome === 'expired' && x.handOff?.at != null ? { label: 'Reached late', tone: 'warn' as Tone }
    : x.outcome === 'expired' ? { label: 'Not reached in time', tone: 'bad' as Tone }
    : OUTCOME[x.outcome];
  const c = x.counts;
  const parts = [
    c.delivered && `${c.delivered} of ${c.total} destinations accepted`, c.refused && `${c.refused} refused`,
    c.pending && `${c.pending} in progress`, c.skipped && `${c.skipped} not sent by routing`,
  ].filter(Boolean).join(' · ');
  return (
    <tr className={on ? 'on' : ''}>
      <td>
        <button type="button" className="link-btn mono" aria-pressed={on} onClick={onOpen}>{leadNo(x.id)}</button>
        <span className="sub">{when(x.createdAt)}</span>
      </td>
      <td>
        {x.caller || <span className="muted">Hang-up, no name</span>}
        <span className="sub mono">{x.phone}</span>
        {x.webchat && <span className="chip">chat</span>}
      </td>
      <td><button type="button" className="link-btn" title="Only this firm" onClick={onFirm}>{x.firmName}</button></td>
      <td><code>{x.callType || '—'}</code><span className="sub"><MappingChips m={x.mapping} l={l} crm={false} /></span></td>
      <td>
        <span className={`badge ${o.tone}`}>{o.label}</span><span className="sub">{parts || 'no destination'}</span>
        {(() => { const h = handOffText(x.handOff, x.createdAt, x.counts); return h && <span className={`sub ${h.tone === 'ok' ? '' : h.tone === 'bad' ? 'bad' : 'warn-text'}`}>{h.text}</span>; })()}
        {callTheFirm(x.handOff) && <span className="badge bad" title="Not confirmed within its fallback delay">Call the firm</span>}
      </td>
    </tr>
  );
}

function LeadDrawer({ id, onClose, l }: { id: string; onClose: () => void; l: ReturnType<typeof labels> }) {
  const { data, error } = usePoll(() => api.lead(id), `lead:${id}`);
  if (!data) return <aside className="card drawer-card" aria-label="Lead"><Loading error={error} /></aside>;
  const x = data.lead;
  return (
    <aside className="card drawer-card" aria-label={`Lead ${leadNo(data.id)}`}>
      <div className="card-head">
        <h2 className="grow">Lead {leadNo(data.id)}{data.test && <> <span className="chip">test</span></>}</h2>
        <button type="button" className="btn icon" aria-label="Close" onClick={onClose}>✕</button>
      </div>
      <dl className="facts">
        <dt>When</dt><dd>{when(data.createdAt)}</dd>
        <dt>Firm</dt><dd><a href={`#/firms/${encodeURIComponent(data.firmId)}`}>{data.firmName}</a></dd>
        <dt>Caller</dt><dd>{data.caller || <span className="muted">no name given</span>}</dd>
        <dt>Phone</dt><dd className="mono">{x.phone || '—'}</dd>
        <dt>Email</dt><dd className="mono">{x.email || '—'}</dd>
        <dt>Source</dt><dd>{x.source}{x.webchat ? ' · chat' : ''}</dd>
        <dt>Call type</dt><dd><code>{x.callType || '—'}</code><br /><MappingChips m={data.mapping} l={l} /></dd>
        {(() => { const h = handOffText(data.handOff, data.createdAt, data.counts); return h && <><dt>Hand-off</dt><dd className={h.tone === 'bad' ? 'bad' : h.tone === 'warn' ? 'warn-text' : ''}>{h.text}</dd></>; })()}
        {(() => { const h = confirmationText(data.handOff, data.createdAt); return h && <><dt>Confirmed</dt><dd className={h.tone === 'bad' ? 'bad' : h.tone === 'warn' ? 'warn-text' : ''}>{h.text}</dd></>; })()}
        <dt>Message</dt><dd>{x.message || <span className="muted">—</span>}</dd>
      </dl>
      {x.custom.length > 0 && (
        <details className="raw" open>
          <summary>Intake ({x.custom.length})</summary>
          <dl className="facts">{x.custom.map((c) => <div key={c.label} style={{ display: 'contents' }}><dt>{c.label}</dt><dd>{c.data}</dd></div>)}</dl>
        </details>
      )}
      <h3 className="section-title">Deliveries ({data.deliveries.length})</h3>
      {data.deliveries.length === 0 ? <p className="muted small">No destination: the firm had no enabled, active connection.</p> : (
        <ul className="issues">
          {data.deliveries.map((d) => (
            <li key={d.id} style={{ display: 'grid', gap: '0.15rem' }}>
              <span className="row">
                <StatusBadge d={d} />
                <strong>{d.connectionName}</strong>
                {d.replayOf && <span className="chip">resend</span>}
              </span>
              {d.diagnosis
                ? <span className="small"><span className={`chip ${CLASS[d.diagnosis.class].tone}`}>{CLASS[d.diagnosis.class].label}</span> {problem(d)}</span>
                : <span className="small muted">{STAGE[d.stage]}</span>}
              <a className="small" href={href('/deliveries', { d: d.id, tab: d.test ? 'tests' : null, firm: d.firmId })}>Open in Delivery Center →</a>
            </li>
          ))}
        </ul>
      )}
    </aside>
  );
}
