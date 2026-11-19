import { useEffect, useId, useState } from 'react';
import type { PortalRole, Statement, TermsGate } from '../api/types';
import { api } from '../api/client';
import { Bars } from '../components/Bars';
import { FirmPicker } from '../components/FirmTools';
import { Loading } from '../components/Shell';
import { labels, useTaxonomy } from '../lib/categories';
import { day, money, plural } from '../lib/format';
import { usePoll } from '../lib/poll';
import { go, href } from '../lib/route';
import { useToast } from '../lib/toast';

const ROLES: [PortalRole, string][] = [['admin', 'Firm admin'], ['member', 'Team member'], ['new', 'New team member (first sign-in)']];
const asRole = (s: string | null): PortalRole => (s === 'member' || s === 'new' ? s : 'admin');
const DAY = 86_400_000;

/**
 * The firm's portal, as one of its users sees it: the terms of service first when
 * they ask for acceptance, then billing: what the cycle costs, what was free and why,
 * and what the firm got. Lab data, illustrative prices.
 */
export function Portal({ query }: { query: URLSearchParams }) {
  const firmId = query.get('firm');
  const role = asRole(query.get('role'));
  const [cycle, setCycle] = useState<'current' | 'previous'>(query.get('cycle') === 'previous' ? 'previous' : 'current');
  const id = useId();
  const gate = usePoll(() => api.termsGate(firmId ?? '', role), firmId ? `gate:${firmId}:${role}` : null, 10_000);
  const st = usePoll(() => api.statement(firmId ?? '', cycle), firmId ? `statement:${firmId}:${cycle}` : null, 10_000);
  const reload = () => { gate.reload(); st.reload(); };
  const set = (q: Record<string, string | null>) => go('/portal', { firm: firmId, role, cycle: cycle === 'previous' ? 'previous' : null, ...q });

  return (
    <>
      <div className="row between">
        <div>
          <h1>Firm portal</h1>
          <p className="muted">A preview of what a firm's users see: the terms of service when they ask for acceptance, then billing.</p>
        </div>
        <div className="row portal-who">
          <FirmPicker value={firmId} onChange={(f) => go('/portal', { firm: f, role })} />
          <label className="field" htmlFor={`${id}-r`}>Signed in as
            <select id={`${id}-r`} value={role} onChange={(e) => set({ role: e.target.value })}>
              {ROLES.map(([k, v]) => <option key={k} value={k}>{v}</option>)}
            </select>
          </label>
        </div>
      </div>
      {!firmId ? <p className="muted">Pick a firm to preview its portal.</p>
        : !gate.data || !st.data ? <Loading error={gate.error ?? st.error} />
        : (
          <div className="portal">
            {gate.data.mode === 'notice' && <TermsNotice g={gate.data} firmId={firmId} role={role} onDone={reload} />}
            <div className={gate.data.mode === 'block' ? 'portal-behind' : ''} aria-hidden={gate.data.mode === 'block'} inert={gate.data.mode === 'block'}>
              <Billing s={st.data} cycle={cycle} setCycle={(c) => { setCycle(c); set({ cycle: c === 'previous' ? 'previous' : null }); }} isAdmin={role === 'admin'} onChanged={reload} />
            </div>
            {gate.data.mode === 'block' && <TermsBlock g={gate.data} firmId={firmId} role={role} onDone={reload} />}
          </div>
        )}
    </>
  );
}

function TermsBlock({ g, firmId, role, onDone }: { g: TermsGate; firmId: string; role: PortalRole; onDone: () => void }) {
  const { act } = useToast();
  const terms = usePoll(() => api.terms(), 'terms', 30_000);
  const [agree, setAgree] = useState(false);
  const v = terms.data?.versions.find((x) => x.version === g.version);
  return (
    <section className="card terms-block" role="dialog" aria-modal="true" aria-labelledby="tb-h">
      <p className="eyebrow">Version {g.version}</p>
      <h2 id="tb-h">{g.title}</h2>
      <p>{g.reason}</p>
      {g.summary && <div className="problem warn"><strong>What changed</strong><span>{g.summary}</span></div>}
      <div className="terms-text" tabIndex={0}>{v ? v.body.split('\n\n').map((p) => <p key={p.slice(0, 40)}>{p.split('\n').map((line, i) => i === 0 ? <strong key={i}>{line}<br /></strong> : line)}</p>) : 'Loading the terms…'}</div>
      <label className="check-inline"><input type="checkbox" checked={agree} onChange={(e) => setAgree(e.target.checked)} /> I have read and accept these terms on behalf of {role === 'admin' ? 'my firm' : 'myself'}.</label>
      <button type="button" className="btn primary" disabled={!agree || g.version === null}
        onClick={() => void act(() => api.acceptTerms(firmId, role, g.version ?? 0), 'Accepted: recorded with your name and the time').then(onDone)}>Accept and continue</button>
    </section>
  );
}

function TermsNotice({ g, firmId, role, onDone }: { g: TermsGate; firmId: string; role: PortalRole; onDone: () => void }) {
  const { act } = useToast();
  const [hidden, setHidden] = useState(false);
  useEffect(() => setHidden(false), [g.version]);
  if (hidden) return null;
  const asks = g.dueAt !== null;
  return (
    <div className={`terms-notice ${asks ? 'asks' : ''}`} role="status">
      <div className="grow">
        <strong>Terms of service updated (version {g.version})</strong>
        <span>{g.summary}</span>
        <small className="muted">{asks ? `${g.reason} Please accept by ${day(g.dueAt ?? 0)}, when it takes effect.` : g.reason}</small>
      </div>
      {asks
        ? <button type="button" className="btn primary" onClick={() => void act(() => api.acceptTerms(firmId, role, g.version ?? 0), 'Accepted').then(onDone)}>Review and accept</button>
        : <button type="button" className="btn" onClick={() => setHidden(true)}>Got it</button>}
    </div>
  );
}

function Billing({ s, cycle, setCycle, isAdmin, onChanged }: {
  s: Statement; cycle: 'current' | 'previous'; setCycle: (c: 'current' | 'previous') => void; isAdmin: boolean; onChanged: () => void;
}) {
  const l = labels(useTaxonomy());
  const last = s.segments.at(-1);
  const used = s.units;
  const share = s.included ? Math.min(1, used / s.included) : 0;
  const daysLeft = Math.max(0, Math.ceil((s.cycleTo - Date.now()) / DAY));
  const onTime = s.value.handOffs ? Math.round((s.value.onTime / s.value.handOffs) * 100) : null;

  return (
    <div className="portal-billing">
      <section className="card portal-head" aria-labelledby="bill-h">
        <div className="grow">
          <p className="eyebrow">Billing · {s.firmName}</p>
          <h2 id="bill-h">{day(s.cycleFrom, s.timeZone)} – {day(s.cycleTo - DAY, s.timeZone)}</h2>
          <p className="muted small">
            {s.inProgress ? `Current cycle · ${plural(daysLeft, 'day')} left` : s.lockedAt ? `Closed · this statement was locked on ${day(s.lockedAt, s.timeZone)} and will not change` : 'Closed'}
          </p>
        </div>
        <div className="tabs" role="tablist" aria-label="Billing cycle">
          <button type="button" role="tab" aria-selected={cycle === 'current'} className={`tab ${cycle === 'current' ? 'on' : ''}`} onClick={() => setCycle('current')}>Current cycle</button>
          <button type="button" role="tab" aria-selected={cycle === 'previous'} className={`tab ${cycle === 'previous' ? 'on' : ''}`} onClick={() => setCycle('previous')}>Previous cycle</button>
        </div>
      </section>

      <div className="tiles">
        <div className="tile"><span>{s.inProgress ? 'Estimated so far' : 'Total for the cycle'}</span><strong>{money(s.totalCents)}</strong><small className="muted">{last ? `${last.planName} · ${money(last.feeCents)} a cycle` : 'No plan'}</small></div>
        <div className="tile"><span>Conversations</span><strong>{s.billable}</strong><small className="muted">billed · {s.free} free by rule · {s.conversations} in all</small></div>
        <div className="tile"><span>New matters</span><strong>{s.value.newMatters}</strong><small className="muted">in this cycle</small></div>
        <div className="tile"><span>Reached you in time</span><strong>{onTime === null ? '—' : `${onTime}%`}</strong><small className={s.value.expired ? 'bad' : 'muted'}>{s.value.expired ? `${plural(s.value.expired, 'lead')} past its window` : 'every lead within its window'}</small></div>
      </div>

      <section className="card" aria-labelledby="use-h">
        <div className="card-head"><h2 id="use-h" className="grow">Included conversations</h2><span className="small muted">{used} of {s.included} used{s.overage ? ` · ${s.overage} beyond` : ''}</span></div>
        <div className="meter" role="meter" aria-valuemin={0} aria-valuemax={s.included} aria-valuenow={used} aria-label="Included conversations used">
          <span style={{ inlineSize: `${Math.max(used ? 1.5 : 0, share * 100)}%` }} className={used > s.included ? 'over' : ''} />
        </div>
        {s.segments.length > 1 && (
          <p className="small">Your plan changed on {day(s.segments[1]?.from ?? s.cycleFrom, s.timeZone)}: {s.segments.map((x) => `${x.planName} (${day(x.from, s.timeZone)} – ${day(x.to - 1)}, ${x.included} included)`).join(', then ')}. Each part is charged for its share of the cycle.</p>
        )}
      </section>

      <div className="report-grid">
        <section className="card" aria-labelledby="st-h">
          <h2 id="st-h">Statement</h2>
          <table className="data narrow">
            <thead><tr><th>Item</th><th className="num">Qty</th><th className="num">Price</th><th className="num">Amount</th></tr></thead>
            <tbody>
              {s.lines.map((x) => <tr key={x.label}><td>{x.label}</td><td className="num">{x.quantity}</td><td className="num">{money(x.unitCents)}</td><td className="num">{money(x.amountCents)}</td></tr>)}
              <tr className="total"><td colSpan={3}><strong>{s.inProgress ? 'Estimated total so far' : 'Total'}</strong></td><td className="num"><strong>{money(s.totalCents)}</strong></td></tr>
            </tbody>
          </table>
          <p className="small muted">No minute multipliers and no separate carrier fee: the price covers handling time and phone costs.</p>
        </section>
        <section className="card" aria-labelledby="free-h">
          <h2 id="free-h">What was free, and why</h2>
          <p className="small muted">These conversations are never billed. Each one records the rule that made it free.</p>
          <Bars rows={s.freeByRule.map((r) => ({ id: r.rule, label: r.label, value: r.count }))} label="Free conversations by rule" empty="None in this cycle." />
          <h3 className="section-title">Every conversation, by kind</h3>
          <table className="data narrow">
            <thead><tr><th>Kind of call</th><th className="num">Billed</th><th className="num">Free</th></tr></thead>
            <tbody>{s.byCategory.map((c) => <tr key={c.category}><td>{l.category(c.category)}</td><td className="num">{c.billable}</td><td className="num">{c.free}</td></tr>)}</tbody>
          </table>
        </section>
      </div>

      {isAdmin && s.inProgress && <ChangePlan s={s} onChanged={onChanged} />}
      <p className="small muted">See your calls as matters: <a href={href('/report', { firm: s.firmId })}>your week in calls</a>.</p>
    </div>
  );
}

function ChangePlan({ s, onChanged }: { s: Statement; onChanged: () => void }) {
  const { act } = useToast();
  const id = useId();
  const book = usePoll(() => api.priceBook(), 'price-book', 60_000);
  const current = s.segments.at(-1);
  const plans = (book.data ?? []).filter((p) => p.version === 1);
  const [pick, setPick] = useState('');
  const chosen = plans.find((p) => p.id === pick);
  const now = plans.find((p) => p.name === current?.planName);
  const upgrade = chosen && now ? chosen.monthlyCents > now.monthlyCents : true;
  return (
    <section className="card" aria-labelledby="cp-h">
      <h2 id="cp-h">Change plan</h2>
      <div className="row">
        <label className="field" htmlFor={`${id}-p`}>Plan
          <select id={`${id}-p`} value={pick} onChange={(e) => setPick(e.target.value)}>
            <option value="">Choose a plan…</option>
            {plans.map((p) => <option key={p.id} value={p.id} disabled={p.name === current?.planName}>{p.name} · {p.included} conversations · {money(p.monthlyCents)} · then {money(p.overageCents)} each</option>)}
          </select>
        </label>
        <button type="button" className="btn primary" disabled={!chosen}
          onClick={() => void act(() => api.changePlan(s.firmId, pick), (r) => (r.startsAt <= Date.now() + 1000 ? 'Changed today: this cycle is prorated' : `Changes on ${day(r.startsAt)}`)).then(onChanged)}>
          Change plan
        </button>
      </div>
      {chosen && <p className="small">{upgrade ? 'An upgrade takes effect today; this cycle is charged by day for each plan.' : `A downgrade takes effect on ${day(s.cycleTo)}, at the start of your next cycle.`}</p>}
    </section>
  );
}
