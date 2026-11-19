# RFC-001: Every Lead to the Firm, in Time

**Answering AI — Product and Technical Proposal**

**Date:** November 19, 2026
**Status:** Proposed — Awaiting stakeholder approval
**Decision owner:** Product / Engineering Leadership
**Reviewers:** Operations, Engineering, Customer Success, Management
**Detail:** [domain model](../lead-delivery/README.md) · [operations](../lead-delivery/operations.md) · [production today](../lead-delivery/production-today.md) · [answers](../lead-delivery/answers.md)

---

## 1. Executive Summary

Law firms pay Answering AI for one thing from lead delivery: **every new client
who calls reaches a person at the firm, in time.** Today we cannot show that we
do it, and we usually learn of a lost lead from the firm.

We built a delivery model in a lab (RFC-006) and ran it over a simulated week of
every failure path. Reliability alone (retries, switching off broken connections,
resending) is not enough, for two reasons:

1. **Late is a kind of lost.** A caller who is not called back within the hour
   often calls the next firm. Retrying for a day, or holding leads until a
   connection returns, keeps the record but loses the client.
2. **The worst failures are silent.** A stopped connection does not error; its
   leads simply stop arriving. Production has one global alert for every firm.

**We propose to measure every lead's journey, deliver it within a window to act
set by the kind of call, use a backup route the firm agreed in advance when the
usual route cannot make it in time, and name one owner judged by one number.**

### Recommended decision

Approve:

1. **Measurement first:** a read-only feed of production numbers (loss, lateness, CRM usage), then a delivery service that records every decision with a reason, real errors instead of "500", and an alert per destination, built in [RFC-007](007_domain_model_and_build_order.md)'s slices.
2. **A window to act per kind of call**, and a **lead outcome of Expired** when it passes with nobody at the firm having the lead.
3. **A fallback** (usually a text to the firm's intake team) agreed at setup, used at once when the usual destination is paused or needs reconnection, or once a third of the window has passed with nothing accepted.
4. **Held deliveries** for the record while a destination is stopped, sent on when it returns, marked late.
5. **Failure classes and destination health**, so operators fix the cause rather than chase individual leads.
6. **A lead delivery owner** in Operations, judged by the share of leads handed off in time.

---

## 2. Problem Statement

### What production does today

From the api-2 source and a read-only development copy of its data:

| Behaviour | Consequence |
|---|---|
| One attempt per CRM, posted inside the intake request; no retry | A CRM that is briefly down loses the lead's CRM record |
| A lead that routing excludes leaves no record | Nobody can say what a firm did not receive, or why |
| Every exception is stored as "500" | Our faults, the CRM's faults and the firm's setup look the same |
| A disabled CRM gets nothing, and nothing is recorded | Leads during an outage are invisible |
| One alert: 5 failures among the last 10 CRM deliveries across **all firms**, weekdays 8–4 | A single firm's stopped connection is never noticed |
| Every lead treated the same, whatever its urgency | An emergency and a vendor call wait in the same queue |

In the development copy: 476 CRM deliveries, 130 stored as "500" and 73 as
401; Clio accepted 0 of 71 (expired sign-ins). The copy is mostly test traffic,
so these are signals, not rates. **Nobody can say how many leads were lost or
late last week.** See [production-today.md](../lead-delivery/production-today.md).

### What we learned in the lab

The lab ran a standard delivery model (record first, retries, switch-off, resend)
over a simulated week of every failure path:

| Finding | Why it matters |
|---|---|
| The lab first retried for ~21 hours and sent missed leads days later, looking new | Reliability alone protects the record, not the client |
| A stopped destination was the biggest problem, and the quietest | It needs its own view and alert, ahead of individual failures |
| Hang-ups refused by a CRM filled "needs attention" and counted towards switching the CRM off | One lead's missing name must not stop a firm's integration |
| Forty deliveries retrying to one CRM read as forty problems | Outages affect a destination kind, not a firm |
| "Retrying" for 3 minutes and for 3 hours looked the same | Age against urgency must be visible |

### Business impact

A lost or late new client costs the firm a matter, and costs us credibility and
often the customer. Because loss is silent, there is no way to prioritise, and
firefighting is the only mode available.

---

## 3. Product Vision

**Every lead has two jobs with different clocks, and we keep both.**

- **The hand-off:** a person at the firm who can act has the lead, within its **window to act**.
- **The record:** the firm's system of record has the lead, complete, possibly late.

A destination may do one or both. **The hand-off never waits for the record:** no
retry schedule, hold or reconnection delays getting a lead to someone who can act.

### Operators

- Start every day on one screen: expired leads first, then stopped destinations, then what needs a person, then what is retrying by itself.
- See each problem by its cause, in the receiver's words, with the action that fixes it beside it.
- Never chase noise: lead problems (a hang-up a CRM cannot take) are counted apart.

### Firms

- Leads arrive in time, by the usual route or by the agreed fallback, and the firm always sees which.
- A weekly report of their calls, with proof of what reached them and how fast.

---

## 4. Proposed Model

### 4.1 Window to act

| Kind of call | Window to act |
|---|---|
| Emergency, jail or custody | 15 minutes |
| New matter | 1 hour |
| Existing client, court or agency, insurance or medical | Same business day |
| Billing, vendor, general | Next business day |

Starting values, to be confirmed with firms' own time-to-contact data. A firm may
shorten a window, not lengthen it. The kind of call comes from call categories
(RFC-002).

### 4.2 Fallback

- Each firm agrees a fallback destination in its approved setup (RFC-005): usually SMS to its intake team, or email.
- **Every firm whose only destination is a CRM must have one;** email is the default for the rest.
- If the usual destination is paused or needs reconnection, the lead goes to the fallback **at once**: that destination cannot deliver, and its record is held (4.3).
- Otherwise, if no destination has accepted the lead when **a third of its window** has passed (still retrying, or refused), the lead goes to the fallback.
- Either way, once per lead, marked "sent by fallback: {destination} unavailable".
- A fallback is always pre-agreed and always shown: client information never goes anywhere the firm did not approve.

### 4.3 Held deliveries

- While a destination is paused or needs reconnection, its deliveries are **held** (not lost, not attempted).
- When it is live again, held deliveries are sent oldest first, with the lead's original time, **marked late**, never as a fresh lead (in a CRM, never a new "call back now" task).
- After 3 days held, the operator agrees with the firm what to do.

### 4.4 Lead outcome

Judged on the newest delivery to each destination, most serious first:
**Expired** (window passed with no accepted hand-off) → In progress → Held →
Needs attention → Not sent → Accepted. An expired lead goes to the top of the
Delivery Center and alerts an operator, who tells the firm.

### 4.5 Failure classes

| Class | Example | What happens |
|---|---|---|
| **Transient** | No answer, 408, 425, 429, 5xx | Retry: 5 s, 1 min, 5 min, 30 min, 2 h, 6 h, 12 h (±20%), honouring `Retry-After` |
| **Lead problem** | A hang-up with no name, to a CRM that requires one | **Not sent**, recorded; the firm still has it by email and the portal; never counts against the destination |
| **Configuration** | The receiver refuses a field we send, or every lead; 3xx | Refused; operator fixes the mapping through a draft revision the firm approves (RFC-005) |
| **Connection** | 401, 403, 410 | Destination moves to **needs reconnection** at once, deliveries held, firm contact sent a reconnect link |
| **Ours** | We could not build the request | Escalate to engineering; no resend until fixed |
| **Routing** | The firm's rule excludes this kind of call | Not sent, with the reason; working as configured |

### 4.6 Destination health

Each destination is **Setting up → Live**, and from Live to **Paused**,
**Needs reconnection** or **Switched off**. Health, most urgent first: stopped,
failing, late, retrying, paused, setting up, healthy, quiet, off.

Switch-off after 5 production deliveries in a row end refused or gave up,
**counting only configuration problems and gave-up**: lead problems never count,
and connection problems move to needs reconnection immediately instead of after
5 lost leads.

---

## 5. Operator Workflow

The system handles normal deliveries by itself; operators handle exceptions.
Every workflow follows **Spot → Diagnose → Fix → Resend → Verify**, and every
action is recorded. Three rules shape the console:

1. **The daily check is ordered by urgency:** leads near or past their window; stopped destinations; deliveries needing attention; the retry queue (late ones flagged); leads with no destination.
2. **The problem drawer is driven by the failure class:** the receiver's own words as the headline, one next action, and the buttons for that class (Edit mapping, Resend, Send a test lead, Pause, Reactivate, Send reconnect link).
3. **Destinations has its own view,** most urgent first, with missed or held leads and the action to send them.

Full workflows, including outages and "the firm says a lead is not in their CRM":
[operations.md](../lead-delivery/operations.md).

### Alerts

One alert per destination per cause, never one per lead. Each names the firm, the
destination and the next action.

| Alert | When | To |
|---|---|---|
| Destination needs reconnection | First 401/403/410 | Operator; firm contact gets the reconnect link |
| Destination switched off | Failure streak reaches 5 | Operator |
| Lead near its window | A third of the window passed without a hand-off (fallback fires) | Operator |
| **Expired lead** | Window passed without a hand-off | Operator, at once |
| Outage | Retries to one destination kind across several firms within 10 minutes | Operator, engineer |
| Quiet firm | A live firm with no accepted delivery in business hours for longer than usual | Operator |

### Ownership

| Responsibility | Owner |
|---|---|
| Every lead from call to hand-off to proof; daily delivery health | **Lead delivery owner** (Operations) |
| Destination setup and change requests | Onboarding (RFC-005) |
| Our-fault failures; receivers that change behaviour | Engineering, on call |
| Reconnecting a destination | Firm contact, from a reconnect link |

---

## 6. Technical Approach

**Build the delivery model from the domain, in the slices of
[RFC-007](007_domain_model_and_build_order.md#8-build-order-thirteen-vertical-slices);
api-2 is not extended** ([answers](../lead-delivery/answers.md#routing-and-integrations)).
This RFC's parts land in these slices:

| Part | RFC-007 slice | What firms and operators get |
|---|---|---|
| **Every decision recorded** with a reason, including not sent; required fields checked before sending | 2 | No lead silently skipped by a destination |
| **Worker and retries:** recorded before sent, retries on the schedule, failure classes | 1, 3 | Intake never waits on a CRM; transient failures recover by themselves |
| **Destination state:** live, paused, needs reconnection, switched off; an alert per destination | 3 | Connection problems stop at once; we find stopped destinations before firms do |
| **Hand-off:** the window per lead from its category; the fallback | 6 | Every new client reaches a person in time |
| **Held deliveries** for stopped destinations; sent on return, marked late | 6 | A complete record, never presented as new |
| **Audit:** who, when and why for resend, pause, reactivate, override | 0, 3 | Repeat resends allowed, and accountable |

The lab already implements most of these and the views (`Dispatch.transition`,
`Dispatch.runFallbacks`, `Urgency`, `Diagnose`, `Destinations`); see the
[code map](../lead-delivery/code-map.md). Held deliveries are proposed: the lab's
stand-in is **missed leads**, counted and sent by hand.

Requirements throughout are the guarantees in
[RFC-007 §6](007_domain_model_and_build_order.md#6-what-any-implementation-must-guarantee):
recorded before sent, a stable lead identity safe to receive twice, destinations
independent, exactly one sender, tests separate from production. Credentials are
never stored in plain form, and nothing promises exactly-once creation in a CRM.

---

## 7. Scope and Rollout

Rollout follows the [roadmap](../lead-delivery/strategy.md#5-roadmap):

| Phase | Milestone | Deliver |
|---|---|---|
| **1. Measure** | Measure | The read-only production feed (RFC-006): loss, CRM usage, call mix; the baseline for every measure below |
| **2. Reliability** | 1 (slices 0–3) | Recorded before sent; worker and retries; destination state; failure classes; the Delivery Center and Destinations views |
| **3. In time** | 2 (slices 4–6) | Windows to act and routing from call categories; fallback; held deliveries |
| **4. Proof** | 3 (slice 8) | The weekly firm report and proof of delivery in the firm portal |

### Out of scope

- Extending api-2.
- Firm-set windows to act in the first release (fixed defaults first).
- Confirming records inside every CRM (where an API allows it, RFC-005).
- Calling firms automatically.

---

## 8. Success Criteria

| Metric | Proposed target |
|---|---|
| Share of leads handed off within their window (new matters and emergencies) | Baseline in phase 1; target set from it, then rising quarter on quarter |
| Expired leads per week | Counted from phase 1; falling after phase 2 |
| Missed leads per week (no record) | Zero after held deliveries |
| Time to detect a stopped destination | Under 15 minutes in business hours |
| Stopped connections reported by firms before we knew | Zero |
| Production regressions | No unexplained loss of existing deliveries |

---

## 9. Key Risks and Mitigations

**Fallback seen as rerouting client data.** *Mitigation:* only to a destination
the firm agreed in its approved setup; every use shown to the firm.

**Wrong windows.** *Mitigation:* starting values, confirmed with firms; firms may shorten them; measured from phase 1.

**Alert fatigue.** *Mitigation:* one alert per destination per cause; lead problems never alert.

**Duplicate CRM records on resend.** *Mitigation:* the same lead identity; resending an accepted delivery needs a senior operator and the firm's confirmation the record is missing.

**Moving firms onto the new build.** *Mitigation:* firms move one at a time, behind a flag per firm, with exactly one system sending each firm's leads; each slice proven against its scenarios first (RFC-006, RFC-007).

---

## 10. Decisions and Approval Request

| # | Decision | Proposed resolution |
|---|---|---|
| D1 | Owner | A lead delivery owner in Operations; engineering on call for our faults |
| D2 | The one number | Share of leads handed off within their window to act, per firm, plus expired leads |
| D3 | Windows to act | 15 min / 1 h / same business day / next business day by kind of call; firms may shorten |
| D4 | Fallback | Agreed at setup; required where the only destination is a CRM; fires at once when the usual destination is paused or needs reconnection, otherwise at a third of the window |
| D5 | Stopped destinations | Deliveries held, sent on return marked late; operator and firm agree after 3 days |
| D6 | Hang-ups to CRMs | Not sent; recorded as a lead problem; the firm has them by email and the portal |
| D7 | Switch-off | Counts configuration problems and gave-up only; connection problems stop at once |
| D8 | Resend of an accepted lead | Senior operator, after the firm confirms the record is missing |
| D9 | Architecture | Built from the domain model in RFC-007's slices; api-2 not extended |

The reasoning, and what would change each, is in [answers.md](../lead-delivery/answers.md).

### Approval requested

1. The window to act, fallback and held-delivery model.
2. The lead delivery owner and the one number.
3. Phase 1 (the production feed) to start now, alongside slice 0; later phases sized from its numbers.

### Specialist sign-off before production

- **Operations:** owner, alert routing, after-hours handling.
- **Engineering:** worker design, moving firms one at a time, failure thresholds.
- **Customer Success:** fallback agreement in setup; how firms are told.
- **Privacy:** fallback destinations and SMS content (minimum caller detail).

---

## 11. Next Steps

1. Name the lead delivery owner.
2. Start the production feed (RFC-006) and slice 0 (RFC-007).
3. Publish the first production numbers and revise any answer they overturn.
4. Agree fallbacks with firms whose only destination is a CRM.
5. Size slices 3 and 6 from the measured loss and lateness.

---

## Final Recommendation

**Measure first, then make the hand-off independent of the record.**

**Guiding principle: a lead is only delivered when a person who can act has it, in time, and we can prove it.**
