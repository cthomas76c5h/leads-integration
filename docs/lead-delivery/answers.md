# Our answers

The questions this work raised, answered: what we will do, why, and what would
make us change it. Leadership asked for answers, not options; these are ours,
and we own them. The one input we do not have yet is production data: the
read-only production feed ([RFC-006](../rfc/006_integration_lab.md)) produces
it, and any answer it overturns we revise here and say so.

Status of each answer: **Decided** (we proceed), or **Decided, to confirm with data**
(we proceed and check it against the production feed).

The [RFCs](../rfc/README.md) ask stakeholders to approve these decisions. This is the only place they are stated with their reasons. Other documents give the reasoning
in depth ([strategy](strategy.md), [why categories matter](../call-categories/why-it-matters.md),
[billing](../billing/strategy.md), [terms](../terms/strategy.md)) and link here
for the answer.

## Ownership and measures

| Question | Our answer | Why | What would change it |
|---|---|---|---|
| Who owns the lead end to end? | **A lead delivery owner in Operations**, accountable for every lead from call to hand-off to proof. Engineering is on call for our-fault failures. Onboarding owns setup and changes. *Decided.* | Today nobody owns the whole life of a lead, so fires are fixed where they surface ([strategy 2.7](strategy.md#27-nobody-owns-the-lead-end-to-end)) | Nothing; the person may change, the role should not |
| What single number judges delivery? | **Share of leads handed off within their window to act, per firm, plus the count of expired leads**, new matters and emergencies first, counting only leads the firm wants delivered somewhere (a call its routing excludes everywhere owes no hand-off, and never goes to the fallback), **reported at two evidence levels side by side, never merged: accepted within the window** (measurable for every lead) **and confirmed within the window** (acknowledged by someone at the firm, where the destination can confirm; a CRM verification counts only where the firm has declared, in its approved setup, that its intake team works from that CRM). *Decided.* | It measures what firms buy: a person who can act, in time. A destination accepting a lead is not a person having it, so the floor and the confirmed number are both shown ([RFC-008 §3](../rfc/008_delivery_guarantees_and_evidence.md#3-evidence-levels-and-the-hand-off-promise)) | Firms not using acknowledgement, leaving the confirmed number too thin to report |
| What else do we report weekly? | Missed leads, expired leads, time to detect a stopped destination, time from signup to first delivered lead, support tickets per 100 firms, share of setups done self-serve ([strategy §7](strategy.md#7-measures)). *Decided.* | Each shows one way the operation scales or breaks | — |

## Speed: window to act, fallback, holding

| Question | Our answer | Why | What would change it |
|---|---|---|---|
| How fast must each kind of call reach the firm? | **72 hours for every kind of call; 24 hours for emergencies and custody.** Set by Answering AI, not by firms, in one versioned settings file ([delivery-timing.json](delivery-timing.json)). *Decided.* | Getting the lead to the firm within 72 hours is our job; how fast the firm then acts is theirs. Reliability comes first: a lead that never arrives is the loss we cause, so retries and the fallback matter more than a tight deadline | Firms' own time-to-contact data showing a kind of call needs a shorter window |
| What happens when the usual route cannot deliver? | **The firm's agreed fallback takes the hand-off on its own clock, separate from the window: at once if the usual destination is paused or needs reconnection (its record is held), and otherwise once a lead has waited 1 hour with nothing accepted (15 minutes for emergencies and custody)**, still retrying or refused. SMS to its intake line, or email. Every firm whose only destination is a CRM must have one; email is the default for the rest. Every fallback use is shown to the firm. The delays are in [delivery-timing.json](delivery-timing.json). *Decided.* | A lead stuck behind a failing destination should reach a person within the hour, however long its window; a stopped destination cannot deliver, so waiting only delays it. RFC-001 forbids silent rerouting, so the fallback is agreed in setup | Fallback use so frequent that firms find it noisy |
| How long do deliveries keep retrying? | **About 69 hours: 5 s, 1 min, 5 min, 30 min, 2 h, 6 h, 12 h, 24 h, 24 h** after each failure, ±20% jitter, never sooner than the destination's Retry-After. Set in [delivery-timing.json](delivery-timing.json). *Decided.* | The record keeps trying for most of the 72-hour window; most outages end long before | Production failure data showing outages that outlast it |
| What happens to the record while a destination is stopped? | **Held**, then sent when it returns, with the original time, marked late, never as a fresh lead. After 3 days held, the operator agrees with the firm what to do. *Decided.* | Firms need a complete record; a week-old lead must not look new | — |
| Hang-ups to CRMs? | **Not sent to CRMs.** Recorded as a lead problem; the firm still has them by email and in the portal. Not billable under 20 seconds. *Decided.* | A CRM refuses them anyway; today they fill "needs attention" and count towards switching destinations off | — |

## Routing and integrations

| Question | Our answer | Why | What would change it |
|---|---|---|---|
| How are leads routed? | **By call category, per destination.** Default: CRMs take new matters; email and the portal take everything; emergencies and custody also go by SMS. Changes go through the RFC-001 approval workflow. *Decided.* | 99.8% of today's active call types already mean "new matter"; the default reproduces today's behaviour explicitly | — |
| Which integrations do we keep? | **Native, certified one at a time: first one representative CRM end to end, then others from Clio Grow, Lawmatics (API), MyCase, Lead Docket, PracticePanther and Smokeball as demand and sandbox evidence justify them.** None is a commitment until it passes its sandbox tests, including its duplicate handling. Retire Lawruler (handler already off) and the legacy Lawmatics forms (none enabled). Everything else through Zapier or webhooks with the public contract. *Decided, to confirm with data.* | These are the kinds with working builders and real use in the data, but production evidence of use is thin; a few excellent integrations beat six that work only on the happy path ([RFC-008 §11](../rfc/008_delivery_guarantees_and_evidence.md#11-changes-to-rfc-007s-build-order)) | The production feed showing a retired kind in real use, or demand for a kind not yet certified |
| Who may resend a lead that was already accepted? | **A senior operator, after the firm confirms the record is missing.** Any operator may resend one that was not accepted. *Decided.* | Resending an accepted lead can create a duplicate in the CRM | — |
| Do we build a new delivery system? | **Yes: built from the domain model, slice by slice, in the order of [RFC-007](../rfc/007_domain_model_and_build_order.md#8-build-order-thirteen-vertical-slices).** api-2 is not extended; it remains a source the lab reads (read-only) and the record of today's gaps ([production-today](production-today.md)). *Decided.* | api-2's delivery has no tests, no record of what it skips and one attempt per lead; the model, decisions and scenarios already exist, so building to them is cheaper than retrofitting them. Each slice is usable on its own and proven by its scenarios, which keeps a new build from stalling | A slice running far over its estimate, or the production feed showing a need the model does not cover |

## Call categories

| Question | Our answer | Why | What would change it |
|---|---|---|---|
| Do categories become the standard language of a call? | **Yes.** Category, practice area and modifiers on every call, with each firm's own label kept. *Decided.* | One field does four jobs today; the most important fact about a call is the least structured ([why-it-matters.md](../call-categories/why-it-matters.md)) | — |
| Where do flags, staff names and phone handling go? | **Out of the call-type field:** billing flags to account settings, staff names to a staff directory with routing rules to a person, phone handling to line settings. *Decided.* | Each job belongs in its own place | — |
| How many call categories are there? | **Nine kinds of call.** The taxonomy's `system`, `line-handling` and `staff-routing` entries are not kinds of call: they leave the categories in the taxonomy's next version (slice 4) and become markers that send a call type, during mapping, to account settings, line settings or the staff directory. *Decided.* | They are the call-type field's other jobs, not things a caller wants ([RFC-008 §6.3](../rfc/008_delivery_guarantees_and_evidence.md#63-taxonomy-scope)) | — |
| Who sets the category, the firm's label or Answering AI? | **Answering AI, at intake, from the conversation**, with a confidence; low-confidence calls are reviewed; firms can correct. *Decided.* | Fills the 2,279 new matters with no practice area; labels alone cannot | — |
| Do we sell benchmarks? | **Yes, in the Insight tier, aggregate only, never caller details, with an opt-out per firm.** *Decided.* | Proof of value at renewal; data nobody uses today | Privacy or contractual limits found in review |
| Beyond law firms? | **Yes, as a goal:** a trades pack (service lines instead of practice areas) once the legal core is stable. *Decided.* | The core is already vertical-neutral and the customer base already includes trades | — |

## Billing

| Question | Our answer | Why | What would change it |
|---|---|---|---|
| What do firms pay for? | **Answer, Deliver and Insight tiers, each a subscription with included handled conversations; one overage rate per plan version.** Prices set from cost per conversation and what accounts pay today. *Decided.* | Packages the suite; firms understand conversations, not multiplied minutes ([billing strategy](../billing/strategy.md)) | — |
| What is billable? | **A handled conversation.** Free by rule: solicitations, wrong numbers, hang-ups under 20 seconds with no details, repeat calls within 10 minutes, tests. Calls over 20 minutes count as two. *Decided, to confirm with data.* | Billing noise breeds disputes; rules recorded per call make every invoice explainable | Call-length data showing other thresholds fit better |
| The legacy × 1.33 and the carrier fee? | **Folded into the new rates; no separate lines.** *Decided.* | Unexplained multipliers and surcharges invite disputes | — |
| Accounts on unusual terms? | **Audited in the first two weeks; each gets an owner and moves to the price book within two billing cycles**, unless a contract says otherwise. *Decided.* | Per-account formulas are invisible revenue risk | — |
| Build or buy billing? | **Buy** invoicing, payments, tax and collections; **build** the usage ledger and the no-charge rules with the lead platform. *Decided.* | The commodity parts are audited and solved; the rules are ours | — |
| How do we move off the legacy model? | **Shadow-bill two cycles**, explain every difference per account, then switch by cohort (new accounts first). *Decided.* | No firm is surprised; every change is explained before it is charged | — |
| A plan change mid-cycle? | **An upgrade applies at once, prorated by day; a downgrade from the next cycle.** *Decided; built in the lab.* | Firms get more at once when they buy more; nobody loses what they paid for | — |
| Can past cycles change? | **No. A closed cycle's statement is locked as rated.** *Decided; built in the lab.* | A plan or price change must never rewrite history a firm has already seen | — |
| Where does the cycle day live? | **On the billing account, one source**, read by every system; a change applies from the next cycle. Cycles run midnight to midnight on **the company's calendar** (America/New_York, daylight saving included), whatever the firm's own zone. *Decided.* | Two systems disagreeing about when a cycle starts produces wrong bills; one calendar keeps every invoice comparable | Firms outside the Eastern zone asking for cycles on their own dates |
| Whose time zone? | **Delivery needs none:** timestamps are UTC and every window, fallback delay and retry is an elapsed duration. **Calendars use IANA zones, never fixed offsets:** billing on the company's (America/New_York); a firm's office hours and the dates on its report and portal in **the firm's own zone**, set by Answering AI (default America/New_York); operator screens in the viewer's. *Decided.* | Fixed offsets ignore daylight saving: a cycle boundary was an hour off from March to November | — |
| Who owns each billing field? | **One named system of record per field** (plan, price, cycle, category), written down; imports seed it once, edits happen only there. *Decided.* | A field nobody owns ends up blank or contradictory | — |
| Which accounts move first? | **Those whose data passes the readiness checks**, by cycle-day cohort; the rest are a work queue. *Decided; built in the lab.* | Start small with clean data | — |
| Outcome pricing (per new matter delivered in time)? | **Not now;** later, if at all, as an add-on. Revisit after two quarters of hand-off data and a legal review of lead-payment rules. *Decided.* | Needs reliable data first, and state rules vary | — |

## Terms of service

| Question | Our answer | Why | What would change it |
|---|---|---|---|
| Ask users to accept again on every change? | **No: only versions marked to ask, only the users they concern, off by default.** *Decided; built in the lab.* | Every-change re-acceptance locked firms out over grammar fixes | — |
| Who must accept what? | **Editorial: nobody (a notice). Pricing: firm admins. Material: every user.** Overridable per version, and recorded. *Decided; built in the lab.* | Ask the people a change concerns | Counsel's advice on a specific change |
| When does a required acceptance block the portal? | **From the version's effective date**; before it, a notice with the date. *Decided; built in the lab.* | Firms get time to review with their own counsel | — |
| Promotions? | **Their own terms, accepted when taken up, with an end date**; the main terms keep one general clause. *Decided.* | Each offer should not mean a new version for every firm | — |
| One version or several? | **One global version**, in the portal; the app and website display it. *Decided.* | Three systems with their own logic behaved differently | — |
| How are users told? | **In the portal, at sign-in**, for every version, with a plain summary of what changed. Email may follow for pricing changes as a courtesy, never as the record. *Decided.* | Users learned of changes by being blocked | — |
| What is the record? | **Every version in full and every acceptance (version, firm, user, role, time), append-only.** *Decided; built in the lab.* | It answers "who agreed to what, and when" | — |

Detail: [terms strategy](../terms/strategy.md).
