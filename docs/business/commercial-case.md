# The commercial case for lead delivery

Who buys, what problem they pay to solve, and why they would choose us, answered the
way a sales or product engineer would answer them: from what our systems show, before
the conversations that confirm it.

**How to read this.** Each answer is our best answer from what we hold: the code of
today's systems, a development copy of their data (mostly test traffic), our billing
model and a simulated week. None of it comes from talking to firms. So each answer
gives its evidence, a confidence, and how to confirm it. Where we hold no evidence we
say so. Decisions stay in [answers.md](../lead-delivery/answers.md); the product and
engineering direction stays in [strategy.md](../lead-delivery/strategy.md). This page
asks whether that direction is worth paying for.

## The hypothesis (version 1)

**Dependable, managed intake for small and growing law firms.**

| | |
|---|---|
| **Customer** | Small and growing law firms that lack the staff, time or tools to handle every inquiry consistently |
| **Problem** | Answering, qualifying, passing on and following up inquiries takes staff time, and every step is a chance to miss one |
| **Solution** | A managed AI-and-human intake service that handles the routine, works with the firm's existing systems, escalates the exceptions, and makes every outcome visible |
| **Economic value** | Fewer interruptions, less administrative work, dependable handling of every inquiry, and possibly more retained clients |
| **Differentiator to prove** | More dependable intake at a lower total cost, not cheaper AI conversations |

**Preventing lost new clients is our leading value proposition, not yet a known reason
to buy.** The economics point to it, but firms may buy for other reasons: freedom from
interruptions, predictable staffing costs, bilingual coverage, or a more professional
first impression. Recovering lost clients may be the benefit underneath without being
what makes a firm sign. Our reliability work serves all of these; which one firms pay
for decides how we sell it.

Two things we know for certain: the firms already pay us to answer their calls, and
today we cannot show them what happened to each inquiry. Closing that gap comes first.

## The questions

### Who is the buyer?

| Segment | Who signs | Who feels the pain | What they ask us | Confidence |
|---|---|---|---|---|
| **Small firm, 1–5 attorneys** (most of our base) | The owner attorney | The same person, between court and clients | "Did anyone call? Was it a real case?" | Medium |
| **Growing firm, 5–30 attorneys** | Managing partner, on the office or intake manager's word | The intake or office manager, who is blamed when a lead goes missing | "Why didn't this reach Clio? Who has it?" | Medium |
| **Larger or multi-office firm** | Operations or intake director | Intake staff and the director's reports | "Show me response times by office and practice area" | Low: few in our data |
| **Trades businesses** (already customers) | The owner-operator | The owner | The same questions, without practice areas | Low: size unknown |

**Evidence.** About 5,500 firms in our call data, about 2,800 of them law firms with
call types. 99.8% of active call types mean "new matter": firms use us for new client
calls. CRM delivery is rare: 8 of 444 CRM rows enabled in the copy we hold, and 122
firms with an active CRM in routing. So most firms take their leads by email, which
suggests small, low-tooling firms ([strategy §2.4](../lead-delivery/strategy.md#24-the-integration-strategy-rests-on-an-untested-assumption)).

**Confirm:** firm size and plan from billing records; who signed each contract.

### What is their expensive problem?

**Our leading candidate: a new client who called and was never reached in time.**
The alternatives a firm may feel more directly are interruptions, staff time spent on
intake, and coverage out of hours or in another language. Interviews and cancellation
reasons decide which leads. In law, a caller who is not
called back within minutes or hours calls the next firm. For a personal-injury or
criminal-defence practice, one lost retained client can cost more than a year of our
service.

What makes it expensive is that it is **invisible**:

- **Today a lead can vanish with no record.** Production gets one attempt, inside the intake request. A lead its call type does not route goes nowhere, silently. A stopped CRM connection does not error; leads just stop ([production-today.md](../lead-delivery/production-today.md)).
- **Firms find out before we do,** usually from a caller who says "I called you last week".
- **Nobody can say how many leads were lost last week** ([strategy §2.3](../lead-delivery/strategy.md#23-we-are-flying-blind-and-busy-feels-like-control)).

Second-order problems, smaller but felt every month:

- **A bill they cannot check:** minutes × 1.33, a carrier fee, rates that differ account by account, and paying for hang-ups and sales calls ([strategy §2.10](../lead-delivery/strategy.md#210-we-bill-for-minutes-firms-buy-clients)).
- **Sorting calls by hand:** messages arrive as free text, and the firm decides which are real matters.

**Confidence:** high that the loss can happen (it is in the code); **unknown how often
it does**, and whether firms feel it as their top problem or never notice it. That is
the most important thing to measure.

**Confirm:** the production feed (lost and late leads per firm); "we never got that
lead" tickets in support.

### What do they buy today?

| From whom | What | What it tells us |
|---|---|---|
| **Us** | Answering, priced per minute with a 1.33 multiplier, a carrier fee and account-specific rates; integrations offered alongside | They already pay for the channel we would add value to. Upgrades, not new logos, are the first market |
| **A CRM vendor** (Clio, MyCase, Lawmatics, Smokeball, PracticePanther, Lead Docket, HubSpot and others in our catalog) | Practice management, intake forms, increasingly their own intake automation | Where their leads should end up; also where the competition for "intake" comes from |
| **Intake staff or the attorney's own time** | Calling leads back, sorting messages | The cost we can reduce, or the person we make effective |
| **Marketing** (pay-per-click, directories, referral services) | The inquiries themselves | The firm already puts a price on a lead. That price is our value anchor |

**Confidence:** high for what they buy from us; medium for the rest (the catalog shows
which CRMs they connect, not what else they pay for).

### What triggers a purchase or an upgrade?

| Trigger | What they buy then | Confidence |
|---|---|---|
| **A lead they discover was lost** ("we never got that call") | Proof and a fallback: the Deliver tier | Medium: the scenario our discovery list puts first |
| **Adopting or switching a CRM** | The integration, set up so it keeps working | Medium: CRM connections exist but many have broken (Clio: 0 of 71 accepted in our copy, expired sign-ins) |
| **An intake hire, or one leaving** | Coverage and a dependable hand-off, so a new person is not a new risk | Low |
| **More marketing spend, more calls** | Capacity, after-hours coverage, then insight on what the spend brought | Low |
| **A bill dispute** | A simpler, explainable price; or a cancellation | Medium: the legacy bill's design invites disputes |

**Confirm:** for each recent upgrade and plan change, what happened in the month
before; the same for the last 20 cancellations.

### Why would they choose us?

| Against | Our answer | Is it true today? |
|---|---|---|
| **Their current receptionist or answering service** | We answer every call, and show you every new client reached you, in time. A broken connection does not quietly lose you a client | **Not yet.** The proof and the fallback are built in the lab, not in production |
| **Their CRM's own intake automation** | We work with whatever CRM you use, or none; we cover the phone 24/7; one record of every inquiry across your systems | Partly. We answer calls today; the cross-system record is the lab's design |
| **Another AI answering provider** | Legal-specific call categories across thousands of firms; proof of delivery; a bill that shows what you got | The categories are real (built from our data); the rest is to build |

The honest position: **our differentiation is designed and proven in the lab, not yet
shipped.** Until it is in production, we compete as an answering service on service
and price.

### What makes them stay?

- **Seeing the value each week:** "your week in calls", with new matters by practice area and proof each one arrived. That's the Insight tier's report, which the lab already produces.
- **Trust that nothing goes missing.** A single discovered lost lead can undo years of good service. Retention depends more on never losing one than on any feature.
- **A bill they understand.** Per handled conversation, with sales calls, wrong numbers and instant hang-ups free, and each line traceable.
- **Switching costs we create honestly:** their call categories and routing, their connected destinations, their phone numbers, and their history of calls as matters.

**Confidence:** medium. We do not have churn reasons, and they are the most useful
missing data.

**Confirm:** churn notes and cancellation reasons for the last year; renewal rates by
plan.

### How much will they pay?

**Best answer:** about what they pay today for answering, plus an upgrade for delivery
assurance that's priced against the value of the leads it protects, not the cost to
serve.

- **The anchor is their current bill.** We have it per firm; the two months of side-by-side bills in the [billing plan](../billing/strategy.md) show each firm its old and new price before anything changes.
- **The structure is decided:** a subscription per tier with included handled conversations, then overage per conversation ([billing §3](../billing/strategy.md#3-pricing)).
- **The ceiling for the Deliver tier is the value at risk:**

  ```
  value at risk per month = new-matter inquiries × share lost or late × share that would have retained × value of a retained client
  ```

  For example, 60 new-matter inquiries a month × 3% lost or late × 25% retained × $3,000 comes to about $1,350 a month at risk. **Every one of those numbers is an assumption.** The share lost is exactly what the production feed measures; the others come from the firm. This is a discovery tool, never a claim.
- **Outcome pricing is later and optional.** Per new matter delivered in time aligns best with value, but rules on paying for leads and sharing fees vary by state and need a legal review first.

**Confidence:** low on price points (they are illustrative); medium on structure.

**The open question is whether dependable delivery is a tier or a baseline.** If most
firms take leads by email and are content, a separately priced reliability tier may
not resonate. It may sell better as a guarantee built into answering, with premiums for
what firms see directly: qualification, scheduling, urgent escalation, other languages
and reporting. Test both, at real prices, with similar firms:

| Offer | Positioning | What it tells us |
|---|---|---|
| **A** | Premium answering with a guaranteed hand-off and proof | Whether reliability strengthens the product firms already buy |
| **B** | Advanced intake coordination: CRM, escalation, ownership of each lead | Whether firms pay separately for workflow |

Whether firms accept the terms tells us far more than whether they like the concept.
The [billing strategy](../billing/strategy.md) has decided on a separate Deliver tier;
if offer A wins, that decision is revised in
[answers.md](../lead-delivery/answers.md#billing).

### Why do customers cancel, complain or upgrade?

We hold no support tickets or cancellation notes, so these are predictions from the
system's design, ranked by how strongly the evidence points to them:

1. **Complain: "we never got that lead."** Silent loss is possible by design today, and firms notice before we do.
2. **Complain: "our CRM stopped receiving leads."** Expired sign-ins are the most common failure in the copy we hold.
3. **Complain: "why am I paying for this call?"** Hang-ups, sales calls and the 1.33 multiplier.
4. **Complain: "I'm locked out."** Every edit to the terms of service made everyone accept again before signing in ([terms strategy](../terms/strategy.md)).
5. **Cancel:** after a discovered lost lead, after a bill dispute, or when the CRM offers intake of its own.
6. **Upgrade:** more numbers or languages, live transfer to the firm, a CRM connection.

**This is the first thing to replace with facts.** One afternoon with the support
queue and the cancellation list will confirm or overturn most of this page. Each
reason for leaving points to a different investment:

| If firms leave because of | Then invest in |
|---|---|
| Price | Service economics and a simpler price |
| Bad call experiences | Voice quality, qualification and escalation |
| Missing leads | Delivery reliability and proof (the lab's work) |
| Staff still doing too much | Intake workflow automation |
| Their CRM vendor's own intake | Hybrid service, and coordination across systems |
| Poor lead quality | Qualification and intake policy |

The technology that took the most engineering is not necessarily the one firms value
most.

## How we would sell it

### Discovery: the five questions for a firm

Ask "walk me through what happens after a potential client calls", then:

| Ask about | What we already know about our firms | What only the firm can tell us |
|---|---|---|
| **Capture** | We answer every call; calls are classified into a dozen categories | How many calls go to voicemail when we are not on the line |
| **Qualification** | Intake questions are set by call type; 99.8% of active types are new matters | Which matters they actually want, by practice area and case size |
| **Hand-off** | Mostly by email; some by CRM, often broken | Who reads it, how fast, and what happens on weekends |
| **Follow-up** | Not ours: the firm calls the client back | How often a callback slips, and who notices |
| **Measurement** | We can count new matters per firm per week | How many became consultations and clients |

### The demonstration

Run it on a business failure, not a dashboard. The lab already runs this story
end to end as a scenario (`emergency-under-pressure`), with fictional firms:

1. An after-hours emergency call arrives; the firm's CRM makes the record but never answers in time.
2. Instead of a silent loss, the lead is marked unknown, and the CRM is paused.
3. 15 minutes later the agreed text message reaches the intake person, who confirms with one tap.
4. When the CRM recovers, the system finds the record it made: one record, no duplicate.
5. The firm sees every step, with times.

**The message:** *"You should not find out your CRM connection broke because a new
client never called back."*

### Positioning by segment

| Segment | Proposition | Price model | Demo |
|---|---|---|---|
| Small firm | Dependable answering, every new client reaches you, a bill you can read | Answer tier subscription | After-hours call reaches the owner's phone |
| Growing firm | Intake that keeps working: proof, a fallback, your CRM kept in sync | Deliver tier | The broken-CRM story above |
| Larger firm | Governance: response times, routing by practice area, every change approved | Deliver + Insight, annual | Routing a change through approval, then the week's report |

## What we would deliberately not do

- **Compete as a CRM.** We deliver to theirs.
- **Compete on the cheapest call.** Voice AI is becoming a commodity; proof and dependability are what's left to sell.
- **Build many native CRM integrations ahead of demand.** Certify the few firms use; Zapier and email for the rest.
- **Sell to other providers (infrastructure)** until someone outside asks for it.
- **Market beyond legal yet.** Keep serving the trades customers we have, and decide on a trades package from their numbers.

## What would change these answers

| If we find | Then |
|---|---|
| Few leads are actually lost or late | Delivery assurance is a feature, not a product; sell answering and insight |
| Firms do not notice or care about losses | Lead with the bill and the weekly report, not with proof |
| Cancellations cluster around CRM-bundled intake | Lean into cross-system coordination and CRM neutrality, sooner |
| Firms with CRMs value a working integration above all | Certify the top CRMs first; that is the Deliver tier's core |
| Trades customers grow faster than law firms | Bring the vertical-neutral core forward |

## The next 30 days: three investigations

The first tells us what customers **have done**; the second, **why**; the third,
what they **will pay for**.

1. **Commercial reality: what they have done.** The last year of cancellations, upgrades, support issues and recurring revenue, per firm and plan. Find the most common reasons firms buy, leave and expand. Alongside it, the production feed: how many leads are lost or late, and how many firms use a CRM.
2. **Customer workflows: why.** Ten firm conversations across the small and growing segments: where staff time goes, how a prospective client is handled, and what they would call an unacceptable failure. Use the [discovery scenarios](../discovery/scenarios.md) and the five questions above.
3. **Pricing behaviour: what they will pay for.** Offers A and B, at real prices, to matched sets of existing customers.

## Commercial evidence ledger

Every claim on this page, and how sure we are of it. **Confirmed** means shown by our
own code or records (noting that the data copy we hold is mostly test traffic).
**Inferred** means reasoned from evidence but not observed in production.
**Untested** means a belief. A claim moves up only with the observation in the last
column.

| Claim | Status | Basis | What would change it |
|---|---|---|---|
| Today's production can lose a lead with no record | Confirmed | The api-2 code: one attempt, no record of skipped leads ([production-today.md](../lead-delivery/production-today.md)) | The feed showing production intake takes another, safer path |
| Firms already pay us to answer their calls | Confirmed | Our billing | — |
| Firms use us mainly for new-client calls | Confirmed (configuration) | 99.8% of active call types mean "new matter" | The production call mix (configuration is not traffic) |
| Some customers are trades businesses, not law firms | Confirmed | Our call data | — |
| Leads are lost or late often enough to matter | Untested | — | The production feed's lost and late leads per firm |
| Most firms take leads by email, not a CRM | Inferred | 8 of 444 CRM rows enabled, in a mostly-test copy | The feed's CRM usage per firm |
| CRM connections break silently, mostly on expired sign-ins | Inferred | Clio: 0 of 71 accepted in the copy | The feed's failures by cause |
| Firms learn of a lost lead before we do | Inferred | Alerting covers all firms with one check, weekdays 8–4 | "We never got that lead" tickets, and who raised them |
| Most customers are small firms | Inferred | Few CRMs, email delivery | Firm size in billing records |
| Firms buy to win clients they would lose (not to save time or interruptions) | Untested | — | Interviews; cancellation and purchase reasons |
| Bills cause disputes | Inferred | The legacy formula (×1.33, charged hang-ups) | Dispute tickets and credits issued |
| Terms re-acceptance locked users out | Confirmed | [Terms strategy §1](../terms/strategy.md) | — |
| Firms will pay more for verified delivery on its own | Untested | — | Offer A against offer B, at real prices |
| Today's bill is the right price anchor | Inferred | Billing plan's side-by-side bills | The side-by-side months; offer acceptance |
| CRM vendors' own intake is taking customers | Untested | Competitor announcements, not verified | Cancellation reasons naming a CRM |
| Proof, fallback and escalation work | Confirmed in the lab only | 30 scenarios and the interleaving check | A production pilot |

## The question this page must answer

> **Which segment will pay materially more for something only our technology enables,
> and keep paying once the novelty wears off?**

Until there is an evidence-backed answer, this is a hypothesis document, not the
business strategy. Positioning, packaging, the sales demonstration, pricing and the
engineering roadmap all follow from that answer.
