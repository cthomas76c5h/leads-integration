# Lead Delivery: What We Know and What We Need

As of 20 November 2026.

## The problem

Every lead Answering AI takes must reach someone at the firm who can act on it, within the window its call category allows, and the record must show that it did. A lead that arrives too late, or never, is a client the firm loses and a failure we cause.

That breaks down into six obligations:

- **Reach a person in time.** 24 hours for emergencies and custody, 72 hours for everything else. If the usual route fails, the firm's agreed fallback takes over after 15 minutes (urgent) or 1 hour.
- **Get the record into the firm's system**, usually its CRM, even if that is late.
- **Stay correct when the outside world is not.** CRMs are slow, down or ambiguous; sign-ins expire; a channel sends the same call twice; workers die mid-send; operators act at the same moment as the system.
- **Never overstate what happened.** Accepted is not verified, and verified is not acknowledged by a person. When we cannot know, we say the outcome is unknown.
- **Leave evidence a dispute can be settled with:** who got the lead, when, by which route, and who decided what.
- **Run the rest of the business on the same records:** routing by call category, billing on handled conversations, terms acceptance, and setting up each integration with the firm's approval.

## What the lab proves

Every behaviour we have agreed is an executable check, and every check passes. The lab runs against fake receivers that play each CRM, so it proves our side of each boundary, not how the real CRMs behave.

| Check | What it covers | Result |
| --- | --- | --- |
| 30 scenarios (240 assertions) | Each delivery rule and failure, written as language-neutral data: outages, timeouts, pauses, crashes, duplicate calls, escalation, corrections, firm isolation | All pass |
| 179 F# tests | The rules one at a time, the API end to end, each CRM's delivery contract against the code | All pass |
| Exhaustive interleaving check | Every ordering of two workers, an operator and an unreliable receiver acting on one lead, with five safety rules checked after every step | No rule broken: about 1,400 states per destination on every run, about 4,900 on a deep run |
| Multi-writer test | Six writers sharing one database file, 400 leads | Every lead sent once, none lost, no write failed |

The interleaving check found a real bug that no scenario had: in a narrow window during a worker takeover, a CRM could be sent the same lead twice. It is fixed, and the exact sequence is kept as a test. Three bugs planted on purpose were each caught.

## Where our understanding is solid

The delivery mechanics are well understood, built and proven, along with the reason behind each rule. Each decision, with what would change it, is recorded in [answers.md](answers.md).

- **Intake:** one lead per call, however often the channel repeats it; a lead the database could not take is refused, never half-recorded.
- **Routing:** each destination takes the call categories its approved rule names, and every decision records the rule version that made it.
- **Sending:** worker leases, retries, and unknown outcomes settled by an operator or a CRM lookup, never by a blind resend.
- **Destinations:** pause and resume, reconnection, and a per-destination limit so one slow CRM cannot hold up the others.
- **The hand-off:** the fallback, escalation for unconfirmed emergencies, and the firm's confirmation link.
- **Evidence:** accepted, verified and acknowledged kept apart, plus a domain event for every change.
- **Concurrency:** documented in [concurrency.md](concurrency.md), with a step-by-step [explorer](concurrency-explorer.html).

## Where it is thinner

Three areas are built to their written specification, but the choices behind them belong to others or depend on systems the lab cannot run.

- **Billing and terms ([RFC-003](../rfc/003_billing_on_value.md), [RFC-004](../rfc/004_terms_of_service.md)).** The usage ledger, credits after a cycle closes, and terms acceptance work. The packages and prices are Product and Finance decisions, known only as written.
- **Onboarding and self-serve ([RFC-005](../rfc/005_standard_lead_and_integrations.md)).** The setup and firm-approval flow exists. How firms actually experience it is not known.
- **Production today (api-2).** Known from the read-only importer and [production-today.md](production-today.md), not from running it. Real volumes, failure rates and current delivery times are not in the lab.

## Assumptions to confirm with data

These values are reasoned best guesses, chosen so the work would not stop waiting for data. Each can be changed in one place once real data arrives.

| Assumption | Value today | Set in | What would confirm or change it |
| --- | --- | --- | --- |
| How each CRM handles a repeat, whether it can be looked up, whether it returns its own record id | Unknown for every CRM, so treated cautiously | Each CRM's delivery contract (`toConfirm` in `docs/contracts/receivers/`) | That CRM's sandbox, one at a time |
| Window to act | 24 h urgent, 72 h otherwise | [delivery-timing.json](delivery-timing.json) | Firms' time-to-contact data |
| Fallback delay | 15 min urgent, 1 h otherwise | [delivery-timing.json](delivery-timing.json) | How often the fallback is used, and whether firms find it noisy |
| Retry schedule | About 69 hours | [delivery-timing.json](delivery-timing.json) | How long real outages last |
| Time for a CRM record to appear | 2 hours, first lookup after 1 minute | [delivery-timing.json](delivery-timing.json), `Dispatch.fs` | The first CRM sandbox with a lookup |
| Firms confirming urgent leads | 85% within 10 minutes (simulation only) | `Simulate.fs` | A pilot of the confirmation link |
| Sends in flight per destination | 4 | `Store.fs` | Production volume and latency per CRM |
| One firm's share of the send slots | A quarter, while others have work | `Store.fs` | Production volume by firm |

## Open questions only the business or production can answer

The code and documents cannot settle these. The owners are suggestions.

| Question | Why it matters | Suggested owner |
| --- | --- | --- |
| Who owns lead delivery day to day? | RFC-001 asks for a named owner; unknown outcomes and escalations need someone to act on them | Leadership |
| Which CRMs matter most commercially? | Each CRM is certified against its sandbox one at a time; the order decides where real evidence arrives first | Product |
| Who calls the firm when an emergency escalates, and is that staffed out of hours? | Escalation is built, but the phone call it triggers is a person's job | Operations |
| What are production volumes, peaks and failure rates per CRM? | They set the timeouts, retry schedule and fairness limits above | Engineering (from api-2 data) |
| Are the billing packages and prices approved? | RFC-003 is proposed; the lab's billing follows it as written | Product and Finance |
| Who signs in as an operator or senior operator, and what security sign-off is needed? | The lab has firm sign-in but no operator sign-in | Engineering and Security |
| One server or several, and SQLite or PostgreSQL in production? | Several servers sharing one file are shown on one machine only; PostgreSQL needs one known change | Engineering |
| How do firms experience setup and approval today? | Self-serve onboarding (RFC-005) is designed without that input | Customer success |

## Asks for the team

The next real progress depends on these, more than on more lab work.

- [ ] Name an owner for lead delivery.
- [ ] Rank the CRMs to certify first, and arrange sandbox access for the top one.
- [ ] Share production figures from api-2: leads per day, peak hour, failures and delivery times per CRM.
- [ ] Pilot the confirmation link for emergency leads with a few firms, and staff the escalation call.
- [ ] Review the billing packages in RFC-003.
- [ ] Decide what operator sign-in and security review production needs.

Where to read more: [the RFC index](../rfc/README.md) (the proposals and their status), [answers.md](answers.md) (every decision and what would change it), and [concurrency.md](concurrency.md) with its [explorer](concurrency-explorer.html) (how delivery stays correct under load).
