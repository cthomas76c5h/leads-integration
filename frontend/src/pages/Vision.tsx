import type { ReactNode } from 'react';
import { api } from '../api/client';
import { usePoll } from '../lib/poll';
import { href } from '../lib/route';

/**
 * The view from outside (docs/lead-delivery/strategy.md),
 * with live numbers from this lab next to each claim. Opens the meeting.
 */
export function Vision() {
  const summary = usePoll(() => api.deliverySummary({ test: false }), 'vision:summary', 10_000);
  const missed = usePoll(() => api.destinations({ health: 'missed', limit: 200 }), 'vision:missed', 10_000);
  const s = summary.data;
  const missedLeads = missed.data?.items.reduce((n, r) => n + r.missedLeads, 0);

  return (
    <div className="vision">
      <section className="vision-hero">
        <p className="eyebrow">Lead delivery · the view from outside</p>
        <h1>Every lead, to someone who can act, in time, and provable.</h1>
        <p className="vision-thesis">
          We are not short of effort; we are short of a standard: <strong>one way to describe a lead</strong>, <strong>one set of call categories</strong>,
          {' '}<strong>one way of delivering</strong>, <strong>one owner</strong>, and <strong>one number</strong> that says whether it worked, in time.
        </p>
      </section>

      <section aria-labelledby="live-h">
        <h2 id="live-h" className="section-title">Right now, in this lab's simulated week</h2>
        <div className="tiles">
          <Live n={s?.expiredLeads} label="Leads that arrived too late" note="nobody at the firm had them before the caller was likely to move on" to={href('/leads', { outcome: 'expired' })} bad />
          <Live n={missedLeads} label="Leads that never arrived" note="came in while a firm's CRM or inbox connection was switched off" to={href('/destinations', { health: 'missed' })} bad />
          <Live n={s?.stoppedDestinations} label="Connections that stopped" note="nothing errors: the leads simply stop arriving, and nobody is told" to={href('/destinations', { health: 'stopped' })} bad />
          <Live n={s?.leadProblems} label="Hang-ups a CRM refused" note="noise today: counted separately, not as problems to chase" to={href('/deliveries', { status: 'failed' })} />
        </div>
      </section>

      <section aria-labelledby="hides-h">
        <h2 id="hides-h" className="section-title">What day-to-day operations hides</h2>
        <div className="vision-grid">
          <Point n="1" title="Delivery is the product, but it is built like plumbing" to={href('/deliveries')}>
            Firms pay for new clients reaching them. Today each lead gets one try at a firm's CRM, and nothing records the ones that were skipped.
          </Point>
          <Point n="2" title="Most firefighting is variety we created" to={href('/categories')}>
            Firms use 4,390 different names for kinds of call; they boil down to about a dozen. Almost every firm wants the same thing: new clients to its CRM.
          </Point>
          <Point n="3" title="Flying blind; busy feels like control" to={href('/destinations', { health: 'stopped' })}>
            Failures are silent. We hear from firms, late, and nobody can say how many leads were lost last week.
          </Point>
          <Point n="4" title="The integration bet is untested" to={href('/destinations', { kind: 'clio-grow', all: 1 })}>
            Only 8 of 444 CRM connections are switched on, and one CRM accepted none of its last 71 leads. Make a few work perfectly; cover the rest with email and Zapier.
          </Point>
          <Point n="5" title="Already bigger than law firms">
            Heating, plumbing and investigation businesses are customers too. Almost everything we build works for them; only the areas of law are legal-specific.
          </Point>
          <Point n="6" title="Data nobody uses" to={href('/report')}>
            We know what every call was about, across about 5,500 firms: proof of our value to each firm, and comparisons with similar firms.
          </Point>
          <Point n="7" title="Nobody owns the lead from start to finish">
            Five systems grew one at a time. Each problem is fixed where it shows up, so the same problems come back somewhere else.
          </Point>
          <Point n="8" title="A lead's value expires" to={href('/leads', { outcome: 'expired' })}>
            A caller who is not called back quickly calls the next firm. Delivering late keeps the record but loses the client: what matters is reaching a person in time.
          </Point>
          <Point n="9" title="Call categories are the missing language" to={href('/categories')}>
            One field does four jobs today. Used as a standard language, it decides where each call goes, how urgent it is, and becomes the report firms see.
          </Point>
          <Point n="10" title="We bill for minutes; firms buy clients">
            The old bill is minutes with a hidden multiplier and rates that differ account by account, and "no charge" is a label someone types. Nothing on the invoice shows the value delivered.
          </Point>
        </div>
      </section>

      <section aria-labelledby="bill-h">
        <h2 id="bill-h" className="section-title">Billing: priced on value, easy to explain, always checkable</h2>
        <div className="vision-moves">
          <div className="card"><strong>Three packages</strong>
            <span><b>Answer</b>: every call and chat, sorted by kind. <b>Deliver</b>: every lead to the firm's CRM or inbox, a backup route, and proof it arrived. <b>Insight</b>: the firm's week in calls, trends and comparisons. A monthly price with calls included, and one standard rate beyond.</span></div>
          <div className="card"><strong>Charge per real conversation</strong>
            <span>Not minutes. Sales calls, wrong numbers, instant hang-ups, repeat calls and tests are free, and the statement says why each one was free. Phone costs and after-call work are in the price, not added on top.</span></div>
          <div className="card"><strong>One record behind every invoice</strong>
            <span>The same call records feed delivery, the firm's report and billing, so they always agree. One published price list, checks before any invoice goes out, and a statement that shows what the firm got next to what it pays.</span></div>
        </div>
        <p className="small muted">
          Replaces minutes × 1.33 (a hidden charge for after-call work), a separate carrier fee and account-by-account rates. The new bill runs beside the old one for two months before anyone is charged differently.
          See it working: <a href={href('/portal')}>a firm's statement</a> · <a href={href('/billing')}>billing operations</a> · <a href={href('/terms')}>terms of service</a>.
        </p>
      </section>

      <section aria-labelledby="moves-h">
        <h2 id="moves-h" className="section-title">Three moves</h2>
        <div className="vision-moves">
          <div className="card"><strong>1. Name an owner for the lead, start to finish</strong>
            <span>Judged by one number: the share of leads that reached a person at the firm in time. A decision for leadership; no software needed.</span></div>
          <div className="card"><strong>2. Measure before building</strong>
            <span>Real numbers from today's systems: what is lost, what is late, which CRMs are used, the mix of calls. Nothing in production changes; it replaces guesses with facts before we build.</span></div>
          <div className="card"><strong>3. Build to one standard</strong>
            <span>A new lead platform built to one model, in small steps a firm can use: standard call categories instead of free text, one way to describe a lead from phone and chat, and a short list of supported CRMs. Each step removes a whole class of problems.</span></div>
        </div>
      </section>

      <section aria-labelledby="dir-h">
        <h2 id="dir-h" className="section-title">The direction: the Stripe of lead delivery, then reach firms through the tools they already use</h2>
        <ol className="vision-steps">
          <li><strong>Trust</strong><span>proof every lead arrived, and in time</span></li>
          <li><strong>Self-serve</strong><span>firms set up and change their own connections, with approval</span></li>
          <li><strong>Platform</strong><span>one standard lead, and a published way for other software to receive it</span></li>
          <li><strong>Distribution</strong><span>offered inside the CRMs firms already use</span></li>
        </ol>
        <h3 className="section-title">Roadmap</h3>
        <ol className="vision-steps roadmap">
          <li><strong>1. Reliable delivery</strong><span>about two months · every lead recorded, retried and accounted for; real numbers from today's systems alongside</span></li>
          <li><strong>2. In time, with approval</strong><span>about a month more · sorted by kind of call, a backup route, nothing changes without the firm's approval</span></li>
          <li><strong>3. Proof and CRMs</strong><span>firms see their week; six CRMs; calls sorted as they come in</span></li>
          <li><strong>4. Billing and terms</strong><span>priced per real conversation; terms without lock-outs</span></li>
          <li><strong>5. Self-serve</strong><span>firms connect and approve themselves; other software receives our leads</span></li>
        </ol>
      </section>

      <section aria-labelledby="dec-h">
        <h2 id="dec-h" className="section-title">Our answers</h2>
        <div className="table-wrap">
          <table className="data narrow">
            <thead><tr><th>Question</th><th>Our answer</th></tr></thead>
            <tbody>
              <tr><td>Who owns the lead from start to finish?</td><td>One owner in Operations, judged by the share of leads that reached a person in time; engineering on call for faults on our side</td></tr>
              <tr><td>How fast must a lead reach the firm?</td><td>Emergencies and people in custody: 15 minutes. New clients: 1 hour. Existing clients, courts and insurers: the same business day. Everything else: the next business day</td></tr>
              <tr><td>What if the usual route fails?</td><td>A backup the firm agreed (a text to its intake team, or email) takes over at once if the usual route is stopped, otherwise once a third of that time has passed. The firm always sees it; the CRM copy follows later, marked late</td></tr>
              <tr><td>Hang-ups?</td><td>Not sent to CRMs, which refuse them; the firm still sees them by email and in its portal; free if under 20 seconds</td></tr>
              <tr><td>Which CRMs do we support directly?</td><td>Six: Clio Grow, Lawmatics, MyCase, Lead Docket, PracticePanther and Smokeball. Two little-used ones retire. Everything else connects through Zapier</td></tr>
              <tr><td>Where does each call go?</td><td>By kind of call: new clients to the CRM, everything to email and the portal, emergencies also by text message</td></tr>
              <tr><td>Billing?</td><td>Three packages, priced per real conversation, from one record of every call. Buy the invoicing software; build the call record and the free-call rules. Run the new bill beside the old for two months, then switch in groups</td></tr>
              <tr><td>Changing plan mid-month? Past months?</td><td>An upgrade starts at once, charged by the day; a downgrade starts next month. A closed month is never changed</td></tr>
              <tr><td>Terms of service?</td><td>One version for everyone. Ask people to accept again only when a change concerns them (price changes: firm admins; changes to what users may do: everyone), with notice first. Every acceptance is recorded</td></tr>
              <tr><td>Charging per new client delivered?</td><td>Not now: revisit after six months of data and a legal review</td></tr>
              <tr><td>Beyond law firms?</td><td>Yes: almost everything already works for other businesses; a package for trades once the legal core is stable</td></tr>
              <tr><td>A new system?</td><td>Yes: built to one model, in small steps a firm can use, each proven in this lab first. The current lead system is not extended; firms move across one at a time</td></tr>
            </tbody>
          </table>
        </div>
        <p className="small muted">Figures come from a development copy of our systems, mostly test traffic, and firm names are fictional. Real numbers from today's systems replace the figures before we build; any answer they overturn, we revise and say so.</p>
      </section>
    </div>
  );
}


function Live({ n, label, note, to, bad = false }: { n: number | undefined; label: string; note: string; to: string; bad?: boolean }) {
  return (
    <a className={`tile ${bad && n ? 'alert' : ''}`} href={to}>
      <span>{label}</span><strong>{n ?? '…'}</strong><small className="muted">{note}</small>
    </a>
  );
}

function Point({ n, title, to, children }: { n: string; title: string; to?: string; children: ReactNode }) {
  return (
    <article className="card vision-point">
      <span className="vision-n" aria-hidden="true">{n}</span>
      <h3>{title}</h3>
      <p className="small">{children}</p>
      {to && <a className="small" href={to}>See it in the lab →</a>}
    </article>
  );
}

