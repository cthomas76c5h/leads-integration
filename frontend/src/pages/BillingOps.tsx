import { useId, useState } from 'react';
import type { BillingAccountRow } from '../api/types';
import { api } from '../api/client';
import { Loading } from '../components/Shell';
import { day, money } from '../lib/format';
import { usePoll } from '../lib/poll';
import { href, replaceQuery } from '../lib/route';

type View = 'all' | 'ready' | 'problems' | 'flagged';
const asView = (s: string | null): View => (s === 'ready' || s === 'problems' || s === 'flagged' ? s : 'all');

/**
 * Billing operations: every account's current cycle under the new model beside the
 * legacy formula (the shadow comparison), readiness for the switch, and the controls
 * that hold an invoice for review. Lab data, illustrative prices.
 */
export function BillingOps({ query }: { query: URLSearchParams }) {
  const id = useId();
  const [view, setView] = useState<View>(asView(query.get('view')));
  const [cycleDay, setCycleDay] = useState(query.get('day') ?? '');
  const [q, setQ] = useState(query.get('q') ?? '');
  const rows = usePoll(() => api.billingAccounts(), 'billing-accounts', 15_000);
  const book = usePoll(() => api.priceBook(), 'price-book', 60_000);
  const sync = (v: Partial<{ view: View; day: string; q: string }>) => {
    const n = { view, day: cycleDay, q, ...v };
    replaceQuery('/billing', { view: n.view === 'all' ? null : n.view, day: n.day || null, q: n.q || null });
  };

  if (!rows.data) return <><h1>Billing operations</h1><Loading error={rows.error} /></>;
  const all = rows.data.filter((r) => (!cycleDay || String(r.cycleDay) === cycleDay) && (!q || r.firmName.toLowerCase().includes(q.toLowerCase())));
  const ready = all.filter((r) => r.ready);
  const problems = all.filter((r) => !r.ready);
  // A note ('info') is shown, but only a control that needs a person flags the account.
  const needsLook = (r: BillingAccountRow) => r.checks.some((c) => c.severity !== 'info');
  const flagged = all.filter(needsLook);
  // Data problems and controls first, then the biggest differences: what needs explaining before the switch.
  const weight = (r: BillingAccountRow) => (r.ready ? 0 : 2e9) + (needsLook(r) ? 1e9 : 0) + Math.abs(r.legacyCents === null ? 0 : r.totalCents - r.legacyCents);
  const shown = (view === 'ready' ? ready : view === 'problems' ? problems : view === 'flagged' ? flagged : all).slice().sort((a, b) => weight(b) - weight(a));
  const compared = ready.filter((r) => r.legacyCents !== null);
  const newTotal = compared.reduce((n, r) => n + r.totalCents, 0);
  const oldTotal = compared.reduce((n, r) => n + (r.legacyCents ?? 0), 0);
  const pick = (v: View) => { setView(v); sync({ view: v }); };

  return (
    <>
      <h1>Billing operations</h1>
      <p className="muted">Every account's current cycle, rated under the new model beside the legacy formula. Nobody is charged differently until the shadow comparison is explained, account by account.</p>

      <div className="tiles">
        <button type="button" className={`tile ${view === 'ready' ? 'on' : ''}`} onClick={() => pick('ready')}><span>Ready for the new billing</span><strong>{ready.length}</strong><small className="muted">of {all.length} accounts</small></button>
        <button type="button" className={`tile ${problems.length ? 'warn-tile' : ''} ${view === 'problems' ? 'on' : ''}`} onClick={() => pick('problems')}><span>Data problems</span><strong>{problems.length}</strong><small className="muted">legacy terms the old formula cannot read</small></button>
        <button type="button" className={`tile ${flagged.length ? 'warn-tile' : ''} ${view === 'flagged' ? 'on' : ''}`} onClick={() => pick('flagged')}><span>Held for review</span><strong>{flagged.length}</strong><small className="muted">a control flagged this cycle</small></button>
        <div className="tile"><span>Shadow comparison</span><strong>{money(newTotal - oldTotal)}</strong><small className="muted">new {money(newTotal)} against legacy {money(oldTotal)}, ready accounts so far this cycle</small></div>
      </div>

      <section className="card">
        <div className="filters" role="search">
          <label className="field" htmlFor={`${id}-q`}>Search
            <input id={`${id}-q`} type="search" value={q} placeholder="Firm…" onChange={(e) => { setQ(e.target.value); sync({ q: e.target.value }); }} />
          </label>
          <label className="field" htmlFor={`${id}-d`}>Billing cycle starts
            <select id={`${id}-d`} value={cycleDay} onChange={(e) => { setCycleDay(e.target.value); sync({ day: e.target.value }); }}>
              <option value="">Any day</option><option value="1">On the 1st</option><option value="15">On the 15th</option>
            </select>
          </label>
          <label className="field" htmlFor={`${id}-v`}>Show
            <select id={`${id}-v`} value={view} onChange={(e) => pick(asView(e.target.value))}>
              <option value="all">Every account</option><option value="ready">Ready</option><option value="problems">Data problems</option><option value="flagged">Held for review</option>
            </select>
          </label>
        </div>
        <div className="table-wrap">
          <table className="data">
            <thead><tr><th>Firm</th><th>Cycle</th><th>Plan</th><th className="num">Conversations</th><th className="num">New model</th><th className="num">Legacy formula</th><th className="num">Difference</th><th>Status</th></tr></thead>
            <tbody>
              {shown.slice(0, 100).map((r) => <Row key={r.firmId} r={r} />)}
              {shown.length === 0 && <tr><td colSpan={8} className="muted empty-note">No account matches.</td></tr>}
            </tbody>
          </table>
        </div>
        {shown.length > 100 && <p className="small muted">First 100 of {shown.length}.</p>}
      </section>

      <section className="card" aria-labelledby="pb-h">
        <h2 id="pb-h">Price book</h2>
        <p className="small muted">Every plan is a published, versioned entry. A change makes a new version from a date; it never edits an old one. Illustrative prices.</p>
        <div className="table-wrap">
          <table className="data narrow">
            <thead><tr><th>Plan</th><th>Version</th><th>From</th><th className="num">Monthly</th><th className="num">Included conversations</th><th className="num">Each beyond</th></tr></thead>
            <tbody>
              {(book.data ?? []).filter((p) => p.included === 100).map((p) => (
                <tr key={p.id}><td>{p.name}</td><td>{p.version}</td><td>{p.effectiveFrom ? day(p.effectiveFrom) : 'since launch'}</td><td className="num">{money(p.monthlyCents)}</td><td className="num">{p.included}</td><td className="num">{money(p.overageCents)}</td></tr>
              ))}
            </tbody>
          </table>
        </div>
        <p className="small muted">Showing the 100-conversation plans; each tier also comes in 50, 250 and 500.</p>
      </section>
    </>
  );
}

function Row({ r }: { r: BillingAccountRow }) {
  const diff = r.legacyCents === null ? null : r.totalCents - r.legacyCents;
  return (
    <tr>
      <td><a href={href('/portal', { firm: r.firmId, role: 'admin' })}>{r.firmName}</a></td>
      <td>{r.cycleDay === 1 ? '1st' : '15th'}</td>
      <td>{r.plan ?? <span className="bad">none</span>}</td>
      <td className="num">{r.billable}<span className="sub">of {r.conversations}</span></td>
      <td className="num">{money(r.totalCents)}</td>
      <td className="num">{r.legacyCents === null ? <span className="muted">cannot compute</span> : money(r.legacyCents)}</td>
      <td className="num">{diff === null ? '—' : <span className={diff > 0 ? 'warn-text' : ''}>{diff > 0 ? '+' : ''}{money(diff)}</span>}</td>
      <td>
        {r.ready ? <span className="badge ok">Ready</span> : <span className="badge warn">Data problem</span>}
        {r.problems.map((p) => <span key={p} className="sub">{p}</span>)}
        {r.checks.map((c) => <span key={c.code} className={`sub ${c.severity === 'bad' ? 'bad' : c.severity === 'warn' ? 'warn-text' : 'muted'}`}>{c.message}</span>)}
      </td>
    </tr>
  );
}
