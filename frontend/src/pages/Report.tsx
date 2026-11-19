import type { FirmReport } from '../api/types';
import { api } from '../api/client';
import { Bars } from '../components/Bars';
import { FirmPicker } from '../components/FirmTools';
import { Loading } from '../components/Shell';
import { labels, useTaxonomy } from '../lib/categories';
import { duration, plural } from '../lib/format';
import { usePoll } from '../lib/poll';
import { go, href } from '../lib/route';
import { HEALTH } from './Destinations';

const pctOf = (n: number, of: number) => (of ? `${Math.round((n / of) * 100)}%` : '—');

/**
 * "Your week in calls": what a firm would see. Its calls told as matters (call
 * categories and practice areas), how fast each reached someone, and proof of
 * delivery per destination. Built from data already held (docs/call-categories/why-it-matters.md).
 */
export function Report({ query }: { query: URLSearchParams }) {
  const firmId = query.get('firm');
  const report = usePoll(() => api.firmReport(firmId ?? ''), firmId ? `report:${firmId}` : null, 10_000);
  const t = useTaxonomy();
  const l = labels(t);
  const r = report.data && report.data.firmId === firmId ? report.data : null;

  return (
    <>
      <div className="row between">
        <div>
          <h1>Your week in calls</h1>
          <p className="muted">What a firm would see: its calls as matters, how fast each reached someone, and proof of delivery.</p>
        </div>
        <FirmPicker value={firmId} onChange={(id) => go('/report', { firm: id })} />
      </div>
      {!firmId ? <p className="muted">Pick a firm to see its week.</p> : !r ? <Loading error={report.error} /> : <Body r={r} l={l} />}
    </>
  );
}

function Body({ r, l }: { r: FirmReport; l: ReturnType<typeof labels> }) {
  const h = r.handOff;
  const handed = h.onTime + h.expired;
  // The firm's own dates: its report reads in its time zone, wherever the viewer is.
  const from = new Date(r.from).toLocaleDateString([], { month: 'short', day: 'numeric', timeZone: r.timeZone });
  const to = new Date(r.to).toLocaleDateString([], { month: 'short', day: 'numeric', timeZone: r.timeZone });
  const areas = r.byPracticeArea.map((a) => ({ id: a.id, label: l.area(a.id) ?? a.id, value: a.count, strong: a.id !== 'unspecified' }));
  const categories = r.byCategory.map((c) => ({ id: c.id, label: l.category(c.id), value: c.count, strong: c.id === 'new-matter' }));

  return (
    <div className="report">
      <section className="card report-hero" aria-labelledby="rep-h">
        <p className="small muted">{from} – {to}</p>
        <h2 id="rep-h" className="report-firm">{r.firmName}</h2>
        <p className="report-lede">
          Answering AI took <strong>{plural(r.calls, 'call')}</strong> for you this week, including{' '}
          <strong>{plural(r.newMatters, 'new matter')}</strong>
          {r.urgent > 0 && <> and <strong>{plural(r.urgent, 'urgent call')}</strong> (emergencies and custody)</>}.{' '}
          {handed > 0 && <><strong>{pctOf(h.onTime, h.onTime + h.expired + h.waiting)}</strong> reached your team within their window to act{h.medianMs !== null && <>, typically in <strong>{duration(h.medianMs)}</strong></>}.</>}
        </p>
      </section>

      <div className="tiles">
        <div className="tile"><span>New matters</span><strong>{r.newMatters}</strong><small className="muted">of {plural(r.calls, 'call')}</small></div>
        <div className="tile"><span>After hours</span><strong>{r.afterHours}</strong><small className="muted">calls outside 8 am – 6 pm or at weekends</small></div>
        <div className={`tile ${h.expired ? 'alert' : ''}`}>
          <span>Reached you in time</span><strong>{pctOf(h.onTime, h.onTime + h.expired + h.waiting)}</strong>
          <small className={h.expired ? 'bad' : 'muted'}>{h.expired ? `${plural(h.expired, 'lead')} past its window` : 'every lead within its window'}{h.viaFallback ? ` · ${h.viaFallback} by your fallback` : ''}</small>
        </div>
        <div className="tile">
          <span>Urgent calls in time</span><strong>{h.urgentTotal ? `${h.urgentOnTime} of ${h.urgentTotal}` : '—'}</strong>
          <small className="muted">emergencies and custody: 24-hour window</small>
        </div>
      </div>

      <div className="report-grid">
        <section className="card" aria-labelledby="pa-h">
          <h2 id="pa-h">New matters by practice area</h2>
          <p className="small muted">From the call type each caller was given. "No practice area named" is where the call type does not say; classifying at intake would fill it.</p>
          <Bars rows={areas} label="New matters by practice area" />
        </section>
        <section className="card" aria-labelledby="cc-h">
          <h2 id="cc-h">Every call, by kind</h2>
          <p className="small muted">The same call categories for every firm, so this week compares with any other.</p>
          <Bars rows={categories} label="Calls by call category" />
        </section>
      </div>

      <section className="card" aria-labelledby="pd-h">
        <h2 id="pd-h">Proof of delivery</h2>
        <p className="small muted">Every destination, and what it did with this week's leads. "Accepted" means the destination took the lead.</p>
        <div className="table-wrap">
          <table className="data">
            <thead><tr><th>Destination</th><th>Now</th><th className="num">Accepted</th><th className="num">Refused</th><th className="num">Not sent by routing</th><th className="num">Missed while off</th></tr></thead>
            <tbody>
              {r.destinations.map((d) => (
                <tr key={d.connectionId}>
                  <td><strong>{d.name}</strong>{d.fallback && <> <span className="chip">fallback</span></>}<span className="sub">{d.kind}</span></td>
                  <td><span className={`badge ${HEALTH[d.health].tone}`}>{HEALTH[d.health].label}</span></td>
                  <td className="num">{d.accepted}</td>
                  <td className="num">{d.refused ? <span className="bad">{d.refused}</span> : 0}</td>
                  <td className="num">{d.notSent}</td>
                  <td className="num">{d.missedLeads ? <strong className="warn-text">{d.missedLeads}</strong> : 0}</td>
                </tr>
              ))}
              {r.destinations.length === 0 && <tr><td colSpan={6} className="muted empty-note">No destinations.</td></tr>}
            </tbody>
          </table>
        </div>
      </section>

      <section className="card" aria-labelledby="lb-h">
        <h2 id="lb-h">Your words, our categories</h2>
        <p className="small muted">Your own names for kinds of calls stay yours; each one means the same thing to every report and routing rule.</p>
        <div className="table-wrap">
          <table className="data narrow">
            <thead><tr><th>Your call type</th><th>Means</th><th className="num">Calls</th></tr></thead>
            <tbody>
              {r.labels.map((x) => (
                <tr key={x.label}>
                  <td><code>{x.label}</code></td>
                  <td>{l.category(x.category)}{x.practiceArea && <span className="muted"> · {l.area(x.practiceArea)}</span>}</td>
                  <td className="num">{x.count}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>

      <p className="small muted">
        Staff view: <a href={href('/leads', { firm: r.firmId })}>this firm's leads</a> · <a href={href('/flow', { firm: r.firmId })}>its delivery graph</a> · <a href={href('/destinations', { q: r.firmName })}>its destinations</a>
      </p>
    </div>
  );
}
