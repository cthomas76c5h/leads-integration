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

## The hypothesis (version 2)

**Dependable intake for law firms that cannot take every call while they work.**

| | |
|---|---|
| **Customer** | Small and growing law firms: a median of 44 calls a month. The 570 firms taking 100 or more a month (23%) carry two-thirds of the volume |
| **Problem** | Calls arrive during the working day (83% on weekdays, 8 am–6 pm) while attorneys are in court or with clients. When we try to put a caller through, two in three attempts find nobody. Firms' main complaint is the quality of the message we took |
| **Solution** | A managed intake service: it answers, captures the call accurately, reaches whoever can act (live, or by the email or text they already read), follows up until someone does, and shows what happened there |
| **Economic value** | Fewer interruptions, every caller handled well, and fewer callers lost when nobody at the firm could pick up |
| **Differentiator to prove** | Message quality, and a hand-off that still works when the firm cannot answer, not cheaper AI calls |

**What production changed from version 1** ([what firms need](what-firms-need.md)):

- **Daytime overflow, not after-hours rescue.** Firms use us mostly as a receptionist for calls they cannot take while working.
- **The hand-off fails at the firm.** Two in three live transfers find nobody; that, more than our delivery code, is where callers can be lost.
- **Firms complain about what we captured, not whether it arrived.** 60% of tickets are "Message concern"; 3 of 480 mention not receiving leads.
- **Firms read their leads by email or text, not in the portal.** 8.6% of portal leads are ever marked read. Proof and confirmation must go where firms already look.
- **Only one in five calls is labelled a new client,** and two in five are marked free. The case built on "every call is a new client" overstated it.

Preventing lost new clients stays a value proposition, but not the lead one: the
evidence points to **dependable handling of every call while the firm is busy**, with
message quality as the felt pain.

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

**What production points to: calls the firm cannot take while it works, handled well
enough that nothing is lost and nothing has to be redone.** 83% of billable calls
arrive in the working day; two in three live transfers find nobody at the firm; and
firms' main complaint is the quality of the message we took
([what firms need](what-firms-need.md)). A new client never reached in
time is the costliest case of this, not the most frequent: one call in five is
labelled a new matter. In law, a caller who is not
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

**Complaints, from the 480 in-app tickets** ([what firms need](what-firms-need.md#a-message-they-can-act-on)),
against what we predicted from the system's design:

| Predicted | What the tickets show |
|---|---|
| "We never got that lead" would lead | **3 of 480.** Not a felt complaint, or invisible to firms |
| "Our CRM stopped receiving leads" | 7 CRM integration *requests*; no breakage pattern |
| "Why am I paying for this call?" | 16 billing tickets |
| — (not predicted) | **287 "Message concern" (60%):** the quality of what we captured |
| — (not predicted) | 27 call forwarding, 9 translation, 7 ring time or hold |

Our design-led prediction was wrong about what firms feel: it pointed at delivery,
and they point at the message.

**Cancellations and upgrades are still unknown.** This copy cannot show churn reasons
(firms stopping is an upper bound of about 3.7% a month), so the cancellation list
remains **the first thing to replace with facts.** Each reason for leaving points to
a different investment:

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
| **Found (Feb–May 2024):** firms complain about message quality, not delivery, and two in three live transfers find nobody | Invest first in message quality (accuracy, review, the intake questions) and in reaching someone when the firm cannot answer; sell dependable delivery as part of that, not as its own tier |
| Few leads are actually lost or late | Delivery assurance is a feature, not a product; sell answering and insight |
| Firms do not notice or care about losses | Lead with the bill and the weekly report, not with proof |
| Cancellations cluster around CRM-bundled intake | Lean into cross-system coordination and CRM neutrality, sooner |
| Firms with CRMs value a working integration above all | Certify the top CRMs first; that is the Deliver tier's core |
| Trades customers grow faster than law firms | Bring the vertical-neutral core forward |

## The next 30 days: three investigations

The first tells us what customers **have done**; the second, **why**; the third,
what they **will pay for**.

1. **Commercial reality: what they have done.** Partly done from the api-2 copy: volume, call mix, timing, live transfers, portal use and support tickets ([what firms need](what-firms-need.md)). Still needed: cancellations and their reasons, upgrades, and recurring revenue per firm and plan (billing records); and the production feed for lost and late leads and CRM usage.
2. **Customer workflows: why.** Ten firm conversations across the small and growing segments: where staff time goes, how a prospective client is handled, and what they would call an unacceptable failure. Use the [discovery scenarios](../discovery/scenarios.md) and the five questions above.
3. **Pricing behaviour: what they will pay for.** Offers A and B, at real prices, to matched sets of existing customers.

## Commercial evidence ledger

Every claim on this page, and how sure we are of it. **Confirmed**: shown by our own
code or production records. **Contradicted**: production records say otherwise.
**Inferred**: reasoned from evidence, not observed directly. **Untested**: a belief. A
claim moves only with the observation in the last column. Production figures are
February–May 2024 ([what firms need](what-firms-need.md)).

| Claim | Status | Basis | What would change it |
|---|---|---|---|
| Today's production can lose a lead with no record | Confirmed | The api-2 code: one attempt, no record of skipped leads ([production-today.md](../lead-delivery/production-today.md)) | The feed showing production intake takes another, safer path |
| Firms already pay us to answer their calls | Confirmed | Billing; 2,498 firms with leads in four months | — |
| Firms use us mainly for new-client calls | **Contradicted** | 21% of answered calls are labelled new matters; 39% no charge; 29% general messages | — |
| Firms use us mainly during the working day | Confirmed | 83% of billable calls on weekdays, 8 am–6 pm Eastern | Per-firm time zones, if they shift it |
| The hand-off fails mostly at the firm | Confirmed | Two in three live transfers find nobody (128,000 attempts) | — |
| Firms' main complaint is message quality | Confirmed | 287 of 480 tickets are "Message concern" | Cancellation reasons, which tickets do not show |
| Firms complain about not receiving leads | **Contradicted** | 3 of 480 tickets | Loss being invisible to firms (the production feed would show it) |
| Leads are lost or late often enough to matter | Untested | Possible in the code; not visible in tickets | The production feed's lost and late leads per firm |
| Firms read their leads in the portal | **Contradicted** | 8.6% of portal leads ever marked read; 30% of users ever activated | — |
| Most firms take leads by email, not a CRM | Inferred | 8 of 444 CRM rows enabled; 7 tickets asking for an integration | The feed's CRM usage per firm |
| CRM connections break silently, mostly on expired sign-ins | Inferred | Clio: 0 of 71 accepted (CRM rows are mostly test traffic) | The feed's failures by cause |
| Most customers are small firms | Confirmed (by volume) | Median 44 calls a month; 76% of firms under 100 | Firm size in billing records |
| Volume is concentrated in a fifth of firms | Confirmed | 23% of firms (100+ a month) make two-thirds of calls | — |
| Some customers are not law firms | Confirmed | 8% of active firms on Ring Savvy | — |
| Firms buy to win clients they would lose | Untested, and weaker | The usage pattern fits "a receptionist while we work" better | Interviews; purchase and cancellation reasons |
| Bills cause disputes | Inferred, weak | 16 of 480 tickets are billing | Credits issued; cancellation reasons |
| Two in five calls earn nothing | Confirmed | 39% of answered calls marked "No charge" by the operator | — |
| Terms re-acceptance locked users out | Confirmed | [Terms strategy §1](../terms/strategy.md): firms pushed back | — |
| Firms stop at about 3.7% a month or less | Inferred | 11% of February's firms had no leads in May (some quiet, not gone) | Cancellations in billing records |
| Firms will pay more for verified delivery on its own | Untested, and weaker | Delivery is not what firms complain about | Offer A against offer B, at real prices |
| Today's bill is the right price anchor | Inferred | Billing plan's side-by-side bills | The side-by-side months; offer acceptance |
| CRM vendors' own intake is taking customers | Untested | Competitor announcements, not verified | Cancellation reasons naming a CRM |
| Proof, fallback and escalation work | Confirmed in the lab only | 30 scenarios and the interleaving check | A production pilot |

## The question this page must answer

> **Which segment will pay materially more for something only our technology enables,
> and keep paying once the novelty wears off?**

Until there is an evidence-backed answer, this is a hypothesis document, not the
business strategy. Positioning, packaging, the sales demonstration, pricing and the
engineering roadmap all follow from that answer.
