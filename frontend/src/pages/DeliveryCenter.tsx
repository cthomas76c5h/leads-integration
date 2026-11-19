import { useMemo, useState } from 'react';
import { api } from '../api/client';
import type { Delivery, DeliveryStatus } from '../api/types';
import { connectionTone } from '../components/Connection';
import { Attempts, Journey, ProblemPanel, ReceivedList, StatusBadge } from '../components/Deliveries';
import { Loading } from '../components/Shell';
import { canReplay, needsAttention, resolvedSet, STAGE } from '../lib/delivery';
import { clock, leadNo, plural } from '../lib/format';
import { usePoll, useTick } from '../lib/poll';
import { href, replaceQuery } from '../lib/route';
import { useToast } from '../lib/toast';

type Tab = 'production' | 'tests' | 'received';
type StatusFilter = 'all' | 'attention' | 'late' | DeliveryStatus;
const STATUS_FILTERS: [StatusFilter, string][] = [
  ['all', 'All statuses'], ['attention', 'Needs attention'], ['delivered', 'Accepted'], ['pending', 'Retrying'], ['late', 'Late (over 15 min)'],
  ['delivering', 'Sending'], ['failed', 'Failed'], ['cancelled', 'Cancelled'], ['skipped', 'Not sent'],
];
const PAGE = 100;
const asTab = (s: string | null): Tab => (s === 'tests' || s === 'received' ? s : 'production');
const asStatus = (s: string | null): StatusFilter => STATUS_FILTERS.find(([k]) => k === s)?.[0] ?? 'all';

export function DeliveryCenter({ query }: { query: URLSearchParams }) {
  useTick();
  const { act } = useToast();
  const [tab, setTabState] = useState<Tab>(asTab(query.get('tab')));
  const [firm, setFirm] = useState(query.get('firm') ?? 'all');
  const [status, setStatus] = useState<StatusFilter>(asStatus(query.get('status')));
  const [selected, setSelected] = useState<string | null>(query.get('d'));
  const [checked, setChecked] = useState<Set<string>>(new Set());
  const [offset, setOffsetState] = useState(Number(query.get('offset') ?? 0) || 0);

  const sync = (next: Partial<{ tab: Tab; firm: string; status: StatusFilter; d: string | null; offset: number }>) => {
    const v = { tab, firm, status, d: selected, offset, ...next };
    replaceQuery('/deliveries', {
      tab: v.tab === 'production' ? null : v.tab, firm: v.firm === 'all' ? null : v.firm, status: v.status === 'all' ? null : v.status, d: v.d,
      offset: v.offset || null,
    });
  };
  const setOffset = (o: number) => { setOffsetState(o); setChecked(new Set()); sync({ offset: o }); };
  const setTab = (t: Tab) => { setTabState(t); setSelected(null); setChecked(new Set()); setOffsetState(0); sync({ tab: t, d: null, offset: 0 }); };
  const select = (id: string | null) => { setSelected(id); sync({ d: id }); };

  const test = tab === 'tests';
  // Every status filters on the server (attention and late too), so paging walks every match.
  const serverStatus = status === 'all' ? undefined : status;
  // "Today" is the operator's own day.
  const since = useMemo(() => new Date().setHours(0, 0, 0, 0), []);
  const summary = usePoll(
    () => api.deliverySummary({ test, firmId: firm === 'all' ? undefined : firm, since }),
    tab === 'received' ? null : `summary:${test}:${firm}`,
  );
  const list = usePoll(
    () => api.deliveries({ test, firmId: firm === 'all' ? undefined : firm, status: serverStatus, limit: PAGE, offset }),
    tab === 'received' ? null : `deliveries:${test}:${firm}:${serverStatus ?? ''}:${offset}`,
  );
  const disabled = usePoll(() => api.connections({ status: 'disabled' }), 'connections:disabled');
  const received = usePoll(() => api.received(50), tab === 'received' ? 'received' : null);
  const detail = usePoll(() => api.delivery(selected ?? ''), selected ? `delivery:${selected}` : null);
  // The setup behind a destination, when it has one (its mapping is then ours to edit).
  const setups = usePoll(() => api.setups(), selected ? 'setups' : null, 5000);

  // Tests are shown separately from production, even if the server ignores `test`.
  const rows = useMemo(() => (list.data ?? []).filter((d) => d.test === test), [list.data, test]);
  const resolved = useMemo(() => resolvedSet(rows), [rows]);
  const attention = (d: Delivery) => needsAttention(d) && !resolved.has(d.id);
  const reload = () => { list.reload(); disabled.reload(); detail.reload(); summary.reload(); setups.reload(); };

  // Needs attention first, then newest first (server order).
  const shown = rows
    .filter((d) => status !== 'attention' || attention(d))
    .map((d, i) => ({ d, i }))
    .sort((a, b) => Number(attention(b.d)) - Number(attention(a.d)) || a.i - b.i)
    .map(({ d }) => d);

  const firms = useMemo(() => {
    const m = new Map<string, string>();
    for (const d of rows) m.set(d.firmId, d.firmName);
    if (firm !== 'all' && !m.has(firm)) m.set(firm, '(selected firm)');
    return [...m.entries()].sort((a, b) => a[1].localeCompare(b[1]));
  }, [rows, firm]);

  // Server totals over every matching delivery, not this page.
  const t = summary.data;
  const finished = t ? t.accepted + t.refused : 0;
  const rate = finished ? Math.round((t!.accepted / finished) * 1000) / 10 : null;
  const show = (s: StatusFilter) => { if (tab === 'received') setTab('production'); setStatus(s); setOffsetState(0); sync({ status: s, offset: 0 }); };

  const replay = async (ids: string[]) => {
    await act(async () => { for (const id of ids) await api.replay(id); }, ids.length > 1 ? `Resent ${ids.length} deliveries` : 'Resent with the same lead identity');
    reload();
  };

  const sel = selected ? (detail.data?.id === selected ? detail.data : rows.find((d) => d.id === selected) ?? null) : null;
  const lead = detail.data?.id === selected ? detail.data.event.lead : null;

  return (
    <>
      <h1>Delivery Center</h1>
      <p className="muted">Every lead, every connection, every firm. Most deliveries need nothing; handle the exceptions here.</p>

      <div className="tiles">
        <a className={`tile ${t?.expiredLeads ? 'alert' : ''}`} href={href('/leads', { outcome: 'expired', tests: test ? 1 : null, firm: firm === 'all' ? null : firm })}>
          <span className="tile-icon bad" aria-hidden="true">⏱</span><span>Expired leads</span><strong>{t?.expiredLeads ?? '…'}</strong>
          <small className="muted">Window to act passed before anyone at the firm got them · last 7 days</small>
        </a>
        <button type="button" className="tile" onClick={() => show('delivered')}>
          <span className="tile-icon ok" aria-hidden="true">✓</span><span>Accepted today</span><strong>{t?.accepted ?? '…'}</strong>
          <small className="ok">{rate === null ? 'Nothing finished yet today' : `${rate}% of finished today`}{t?.notSent ? ` · ${t.notSent} not sent` : ''}</small>
        </button>
        <button type="button" className={`tile ${t?.needsAttention ? 'alert' : ''}`} onClick={() => show('attention')}>
          <span className="tile-icon bad" aria-hidden="true">⚠</span><span>Needs attention</span><strong>{t?.needsAttention ?? '…'}</strong>
          <small>Show them{t?.leadProblems ? ` · ${plural(t.leadProblems, 'lead problem')} not counted` : ''}</small>
        </button>
        <button type="button" className={`tile ${t?.late ? 'warn-tile' : ''}`} onClick={() => show(t?.late ? 'late' : 'pending')}>
          <span className="tile-icon" aria-hidden="true">⟳</span><span>Retry queue</span><strong>{t?.retrying ?? '…'}</strong>
          <small className={t?.late ? 'warn-text' : 'muted'}>{t?.late ? `${t.late} late (over 15 min): show them` : 'Retrying by itself'}</small>
        </button>
        <a className={`tile ${t?.stoppedDestinations ? 'alert' : ''}`} href={href('/destinations', { health: 'stopped' })}>
          <span className="tile-icon bad" aria-hidden="true">⏸</span><span>Stopped destinations</span><strong>{t?.stoppedDestinations ?? '…'}</strong><small className="muted">See them in Destinations</small>
        </a>
      </div>

      {disabled.data && disabled.data.length > 0 && (
        <section className="card" aria-labelledby="dis-h">
          <div className="card-head"><h2 id="dis-h" className="grow">Needs attention: stopped destinations</h2><a className="small" href={href('/destinations', { health: 'stopped' })}>All destinations →</a></div>
          <div className="attention-list">
            {disabled.data.map((c) => (
              <div key={c.id} className="attention-item">
                <span className={`badge ${connectionTone(c).tone}`}>{connectionTone(c).label}</span>
                <span className="grow"><a href={`#/firms/${encodeURIComponent(c.firmId)}`}><strong>{c.firmName}</strong></a> · {c.name}
                  {c.disabledReason && <span className="small muted"> · {c.disabledReason}</span>}</span>
                <button type="button" className="btn" onClick={() => void act(() => api.patchConnection(c.id, { reactivate: true }), 'Connection reactivated').then(reload)}>Reactivate</button>
              </div>
            ))}
          </div>
        </section>
      )}

      <div className={`ops-grid ${sel && tab !== 'received' ? 'with-drawer' : ''}`}>
        <section className="card" aria-label="Deliveries">
          <div className="tabs" role="tablist" aria-label="Delivery views">
            {(['production', 'tests', 'received'] as const).map((t) => (
              <button key={t} type="button" role="tab" aria-selected={tab === t} className={`tab ${tab === t ? 'on' : ''}`} onClick={() => setTab(t)}>
                {{ production: 'Production', tests: 'Tests', received: 'Received' }[t]}
              </button>
            ))}
          </div>

          {tab === 'received' ? (
            <>
              <p className="small muted">What the local fake receivers saw, and their verdict. Newest first.</p>
              {received.data ? <ReceivedList rows={received.data} /> : <Loading error={received.error} />}
            </>
          ) : (
            <>
              <div className="card-head">
                <h2 className="grow">{test ? 'Test deliveries' : 'Delivery log'}</h2>
                {checked.size > 0 && <button type="button" className="btn" onClick={() => { void replay([...checked]); setChecked(new Set()); }}>Resend {checked.size}</button>}
                <select value={firm} aria-label="Firm" onChange={(e) => { setFirm(e.target.value); setChecked(new Set()); setOffsetState(0); sync({ firm: e.target.value, offset: 0 }); }}>
                  <option value="all">All firms</option>
                  {firms.map(([id, name]) => <option key={id} value={id}>{name}</option>)}
                </select>
                <select value={status} aria-label="Status" onChange={(e) => { const s = asStatus(e.target.value); setStatus(s); setOffsetState(0); sync({ status: s, offset: 0 }); }}>
                  {STATUS_FILTERS.map(([k, l]) => <option key={k} value={k}>{l}</option>)}
                </select>
              </div>
              {!list.data ? <Loading error={list.error} /> : (
                <div className="table-wrap">
                  <table className="log">
                    <thead>
                      <tr><th><span className="visually-hidden">Select</span></th><th>Lead</th><th>Firm</th><th>Connection</th><th>Status</th><th>Stage</th><th>Last try</th></tr>
                    </thead>
                    <tbody>
                      {shown.map((d) => (
                        <tr key={d.id} className={d.id === selected ? 'on' : ''} onClick={() => select(d.id)}>
                          <td onClick={(e) => e.stopPropagation()}>
                            <input type="checkbox" aria-label={`Select ${leadNo(d.eventId)}`} checked={checked.has(d.id)} disabled={!canReplay(d)}
                              onChange={(e) => { const n = new Set(checked); if (e.target.checked) n.add(d.id); else n.delete(d.id); setChecked(n); }} />
                          </td>
                          <td>
                            {/* The row is clickable; this button makes it keyboard reachable. */}
                            <button type="button" className="link-btn mono" aria-pressed={d.id === selected} onClick={(e) => { e.stopPropagation(); select(d.id); }}>{leadNo(d.eventId)}</button>
                            {d.replayOf && <> <span className="chip">resend</span></>}
                          </td>
                          <td className="ellipsis">{d.firmName}</td>
                          <td className="ellipsis">{d.connectionName}</td>
                          <td><StatusBadge d={d} resolved={resolved.has(d.id)} /></td>
                          <td className="ellipsis small">{STAGE[d.stage]}</td>
                          <td className="muted">{d.attempts.length ? clock(d.attempts.at(-1)?.at ?? d.createdAt) : '—'}</td>
                        </tr>
                      ))}
                      {shown.length === 0 && (
                        <tr><td colSpan={7} className="muted empty">Nothing here. Send a synthetic lead from a firm page.</td></tr>
                      )}
                    </tbody>
                  </table>
                </div>
              )}
              {list.data && (offset > 0 || list.data.length === PAGE) && (
                <nav className="pager" aria-label="Pages">
                  <span className="small muted">Deliveries {list.data.length ? `${offset + 1}–${offset + list.data.length}` : 'none'}</span>
                  <button type="button" className="btn" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - PAGE))}>← Newer</button>
                  <button type="button" className="btn" disabled={list.data.length < PAGE} onClick={() => setOffset(offset + PAGE)}>Older →</button>
                </nav>
              )}
              <p className="small"><a href={href('/leads', { firm: firm === 'all' ? null : firm, tests: test ? 1 : null })}>One row per lead: Leads →</a></p>
            </>
          )}
        </section>

        {sel && tab !== 'received' && (
          <aside className="card drawer-card" aria-label={`Lead ${leadNo(sel.eventId)}`}>
            <div className="card-head">
              <h2 className="grow">Lead {leadNo(sel.eventId)}{sel.test && <> <span className="chip">test</span></>}</h2>
              <button type="button" className="btn icon" aria-label="Close" onClick={() => select(null)}>✕</button>
            </div>
            <dl className="facts">
              <dt>Caller</dt><dd>{lead ? `${lead.firstName} ${lead.lastName}`.trim() || '(no name)' : '…'}{lead?.callType ? ` · ${lead.callType}` : ''}{lead?.webchat ? ' · webchat' : ''}</dd>
              <dt>Firm</dt><dd><a href={`#/firms/${encodeURIComponent(sel.firmId)}`}>{sel.firmName}</a></dd>
              <dt>Connection</dt><dd>{sel.connectionName} <span className="muted">({sel.kind})</span></dd>
              <dt>Status</dt><dd><StatusBadge d={sel} resolved={resolved.has(sel.id)} /></dd>
              <dt>Stage</dt><dd>{STAGE[sel.stage]}</dd>
              {sel.reason && <><dt>Reason</dt><dd>{sel.reason}</dd></>}
              {sel.nextAttemptAt && <><dt>Next try</dt><dd>in {Math.max(0, Math.ceil((sel.nextAttemptAt - Date.now()) / 1000))}s</dd></>}
              {sel.replayOf && <><dt>Resend of</dt><dd><button type="button" className="link-btn mono" onClick={() => select(sel.replayOf)}>{sel.replayOf.slice(-8)}</button></dd></>}
            </dl>
            <Journey d={sel} />
            {resolved.has(sel.id)
              ? <p className="small muted">A later resend to this destination was accepted.</p>
              : <ProblemPanel d={sel} destinationStopped={disabled.data?.some((c) => c.id === sel.connectionId) ?? false}
                  setup={setups.data?.find((x) => x.connectionId === sel.connectionId) ?? null}
                  onResend={() => void replay([sel.id])} onChanged={reload} />}
            <a className="btn wide" href={href('/flow', { firm: sel.firmId })}>Open the delivery graph</a>
            <details className="raw" open>
              <summary>What was sent ({plural(sel.attempts.length, 'try', 'tries')})</summary>
              <Attempts d={sel} />
            </details>
          </aside>
        )}
      </div>

      <section className="card how">
        <div className="card-head"><h2 className="grow">How it works</h2><span className="ok small">✓ “Accepted” means the receiver took the lead. Whether it is in the CRM is not visible to us.</span></div>
        <ol className="how-strip">
          <li><span aria-hidden="true">☎</span><strong>Answering AI</strong><small>Answers and takes the intake</small></li>
          <li className="hub"><span aria-hidden="true">▣</span><strong>Standard lead event</strong><small>One format for every firm</small></li>
          <li><span aria-hidden="true">⚙</span><strong>Delivery engine</strong><small>Routing, payloads, retries, log</small></li>
          <li><span aria-hidden="true">⇢</span><strong>Firm systems</strong><small>CRMs, Zapier, webhooks, notifications</small></li>
        </ol>
      </section>
    </>
  );
}
