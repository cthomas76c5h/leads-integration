# Contracts: scenarios and receivers as data

The checks an implementation of lead delivery must pass, written as data so any
implementation, in any language, can run them ([RFC-007 §6.3](../rfc/007_domain_model_and_build_order.md#63-proving-it-scenarios-and-receiver-contracts-as-data)).
Words are the [glossary](../../GLOSSARY.md)'s. **This is a first draft of the
format**; it is fixed in lab refactor phase 6, when the lab's simulator and fake
receivers are rewritten to read these files.

| File | Holds |
|---|---|
| [scenario.schema.json](scenario.schema.json) | The format of a scenario |
| [scenarios/](scenarios/) | One file per scenario |
| [receiver.schema.json](receiver.schema.json) | The format of a receiver contract |
| [receivers/modes.json](receivers/modes.json) | How a fake receiver behaves in each mode, the same for every kind |
| [receivers/](receivers/) | One delivery contract per destination kind: what it requires, how it accepts and refuses, how it handles duplicates and settles an unknown outcome, the evidence it can give, whether it reaches a person |
| [events.json](events.json) | The domain events a scenario may assert, each with its source |

Check every file:

```sh
python tools/check_contracts.py
```

It validates each file against its schema, then checks what a schema cannot:
every firm, destination and lead a scenario names exists (in steps, faults,
access requests and expectations), every mode is in `modes.json`, every event
is in `events.json`, and every category and practice area, including corrected
categories and new routing rules, is in
[taxonomy.json](../call-categories/taxonomy.json).

**Checked is not run.** The checker proves the files are well formed and
consistent. The [scenario runner](#running-the-scenarios) proves an
implementation behaves as they say.

## Running the scenarios

```sh
python tools/run_scenarios.py                   # every scenario against the lab
python tools/run_scenarios.py auth-expired      # by name
python tools/run_scenarios.py --model lab       # the ones the lab must pass
python tools/run_scenarios.py --report out.json # results as JSON too
```

Each scenario gets a fresh API process with its own temporary database and
[test control](../api.md#test-control-test_controltrue-only) on: the clock is
set to the scenario's start, firms and destinations are created through the
public routes, the `when` steps are played in time order (the clock advanced
between them, with the worker run at every due retry and at least once a
minute), and each `then` expectation is checked from what the API reports.

| Result | Means |
|---|---|
| `PASS` | Every expectation held |
| `FAIL` | At least one expectation did not hold; each is listed |
| `UNSUPPORTED` | The scenario needs a step or expectation the lab cannot do or report (a fault, an access check, an event, evidence beyond accepted); listed, never skipped |
| `ERROR` | The runner or the API failed; not a verdict on the scenario |

A scenario passes only when **every** assertion is supported and holds: an
assertion the implementation cannot report counts against the scenario, never out
of the total. Each scenario's line shows its assertions passed/failed/unsupported
(unsupported includes those never reached because an earlier step was
unsupported), and the run ends with the totals for scenarios and for assertions.

A `lab` scenario must pass: the run exits 1 if one does not. A `proposed`
scenario is expected to fail or be unsupported until its rule is built; one that
passes is reported for promotion, after checking that it asserts what makes it
proposed (two did not, and were tightened).

The scenarios are neutral; `LabAdapter` in the runner is the one place
lab-specific translation lives: a category becomes a reference lead's call type,
category routing becomes the firm's active call types, lab statuses become the
model's states, and email and SMS destinations get fictional recipients.
Another implementation would get its own adapter.

## A scenario

A story stated as data. **Given** a starting state, **when** things happen on a
timeline, **then** these states hold.

| Part | Holds |
|---|---|
| `name`, `story` | The scenario's name (also its file name) and one paragraph in the glossary's words |
| `slice` | Which slice of the [build order](../rfc/007_domain_model_and_build_order.md#8-build-order-thirteen-vertical-slices) it proves |
| `model` | Whose rules the expectations state: `production`, `lab`, or `proposed` (agreed, not built) |
| `proves` | Links to the rules it proves, in the model docs |
| `today` | Where the lab or production differs from the expectations, if it does |
| `given` | `clock` (time zero, with its time zone); `firms` (fictional names, always); `destinations` with kind, role (`usual` or `fallback`), state, routing (categories, chat leads) for a webhook, the body fields its live revision sends (`mapping`), and, where a scenario relies on duplicate handling the kind's contract has not confirmed, the handling it **assumes** (`duplicates`) |
| `when` | Steps, each at an offset from the clock, largest unit first (`+30s`, `+5m`, `+2h30m`, `+25h10m`, `+1d`), doing one thing: a **lead** is recorded; a destination's **receiver** starts answering a certain way (a mode, optionally with fields it requires on top of its kind's; an exact answer; or no answer); someone takes an **action** (resend, pause, resume, reactivate, reconnect, send missed leads, change a mapping, change routing, approve (optionally applying the new revision to waiting deliveries), acknowledge a lead, correct a classification); a **fault** happens inside our system (a worker crashes after sending, a second worker claims, a worker stalls before sending and later continues, a request is sent and its answer held then released (on the wire), a lease expires, the database stops or starts answering, the channel repeats a completion event); a firm's user makes an **access** request (read, list, export or resend a lead); a firm's billing **cycle closes**. A lead may carry the channel's **conversation** id |
| `then` | Expectations, each at an offset, stating one thing that holds then: a **destination**'s state and failure streak; the newest **delivery** of a lead to a destination (its state, failure class, attempts, how many deliveries the pair has with resends, whether it was late); the revision it uses, its evidence level, whether verification is overdue, each attempt's outcome, words its reason contains); that a lead has **no delivery** to a destination at all; a lead's **hand-off** (with its evidence level and whether it escalated); a lead's **outcome**; what intake **captured** (recorded or not, how many leads for its conversation, what the channel was told); the answer to an **access** request (allowed or refused, audited); a lead's current **classification** (records kept, destinations offered); how its conversation is **billed**. And two kinds that are not states: an **event** emitted (exactly how many, about which lead, destination or firm), and a **prohibited effect**: no event of a kind (`noEvent`), or no attempt to a destination (`noAttempt`), within a stated interval |

Expectation times allow for the retry schedule's ±20% jitter, so a scenario
holds whatever the random draw. `approve` is the firm contact approving a draft
revision, after which it is live: setup's test and confirm steps are not modelled.
Revisions are numbered per destination: the live one in `given` is 1, and each
approval makes the next one live.

The fault, access, acknowledgement, correction, capture, evidence, billing,
event and prohibited-effect parts, and the delivery state `unknown`, come from
[RFC-008](../rfc/008_delivery_guarantees_and_evidence.md); its ten
failure-injection scenarios use them, and each asserts the durable state it
leaves, the events it emits and the side effects it must not have.

States use the model's names ([domain model §4](../lead-delivery/README.md#4-lifecycles)),
not any implementation's: `needs-reconnection`, not `disabled`; `held`, not
`pending`. Callers are invented, and a scenario states only what the rules read
(category, practice area, channel, hang-up, test).

A scenario with `model: proposed` is expected to fail on the lab until the rule
is built. That is its purpose: it is the acceptance test for the slice.

## A delivery contract (receiver contract)

What one destination kind requires, how it answers, and what we can and cannot know about it.
`ContractTests.fs` checks every field below that the code acts on against the code, both ways:
a contract that says one thing while the code does another fails the tests. The fake receivers are
generated from these, and the same files drive the contract tests against each
CRM's sandbox ([RFC-006](../rfc/006_integration_lab.md)).

| Part | Holds |
|---|---|
| `kind`, `label` | The destination kind (also the file name), as in the catalog |
| `model` | `lab` (what the fake receiver checks today) or `confirmed` (checked against the CRM's sandbox) |
| `request` | Method and content type |
| `requires`, `optional` | Each field (a dotted JSON path, `name[key]` for an array of name and value, or a form field) and which part of the standard lead fills it. `requires` may be empty: a plain webhook requires nothing by itself, and a scenario adds what a particular receiver requires |
| `accepts` | The status of an accepted lead |
| `refusesMissing` | The answer when a required field is missing or blank |
| `duplicates` | How the kind handles a lead it may have seen, which decides what follows an unknown outcome ([RFC-008 §4.3](../rfc/008_delivery_guarantees_and_evidence.md#43-duplicate-handling-per-destination-kind)): `idempotency-key`, `lookup`, `external-reference`, `none` (confirmed to have no way to tell), `harmless`, or **`unknown`**: not yet verified against the destination's sandbox, and treated at runtime like `external-reference` or `none`. Every kind is `unknown` until there is evidence; only email, SMS and Slack, where a second copy is harmless by nature, are not |
| `afterUnknown` | What follows an unknown outcome: `resend` (a second copy is safe), `lookup` (looked up by our lead reference; found settles it, otherwise an operator checks) or `operator` (held for an operator to resolve: found, closed or resent). Follows from `duplicates` |
| `evidence` | The strongest evidence it can give that the lead reached the firm (`accepted`, `verified`, `acknowledged`; [RFC-008 §3.1](../rfc/008_delivery_guarantees_and_evidence.md#31-five-levels)), and how |
| `handOff` | `person` (someone at the firm reads it: it can carry the hand-off and the fallback) or `record` (it stores the lead in a system) |
| `remoteId` | Whether its answer returns its own id for the record it made: `returned`, `none` or `unknown` until its sandbox shows it. Nothing keeps one yet, so no contract may say `returned` |
| `answers` | How its answers are read: `standard` (the one shared table, `Policy.classify`) until a kind needs its own |
| `toConfirm` | What the sandbox must still settle |

Modes (`ok`, `reject`, `flaky`, `gone`, `down`, `slow`, `expired-auth`,
`lost-record`, `accept-then-timeout`) are the same for every kind and live once, in
[modes.json](receivers/modes.json). `expired-auth` is proposed: the lab's receivers
do not have it yet. `lost-record` accepts, but a lookup never finds the record;
`accept-then-timeout` makes the record and never answers in time, so the outcome is
unknown while the record exists. In `down` mode, lookups answer 503 too.

## Events

[events.json](events.json) lists every event a scenario may assert, with its
source: the [domain model](../lead-delivery/README.md#7-domain-events),
[RFC-007 §5](../rfc/007_domain_model_and_build_order.md#5-aggregates-and-invariants),
[RFC-008](../rfc/008_delivery_guarantees_and_evidence.md), or `contracts` for a
name these scenarios introduce that RFC-008 should adopt (`DeliveryCancelled`,
`DeliveryUnknown`, `DeliveryVerified`, `VerificationOverdue`, `LeaseRecovered`,
`HandOffEscalated`, `HandOffAcknowledged`, `UsageRated`, `UsageCredited`,
`AccessRefused`).

## The intake lead contract

Delivery takes the call center's leads in intake's contract, `intake-lead/draft-2`: an exact
copy of its schema, its examples, the mapping, and what is temporary are in
[intake-lead/](intake-lead/README.md). A scenario's lead step says `contract` to arrive this way: built from
one of the contract's examples, with the scenario's firm `alias`, category and conversation
written in, then its `changes`; what the channel is told then includes `conflict` and `refused`.

## What is here

Every scenario the lab's simulator runs, a scenario for each delivery rule that
had none, RFC-008's ten failure-injection scenarios, and a receiver contract for
every kind in the catalog, and three for the intake lead contract (`contract-lead-*`): 34 scenarios (33 `lab`, 1 `proposed`) and 16
receivers. A `proposed` scenario fails on the lab today; its `today` field says
how the lab differs. All of them run (see above): every `lab` scenario passes,
and every `proposed` one fails or is unsupported, for the reason its `today`
field gives.

### Scenarios

| Scenario | Slice | Model | Proves |
|---|---|---|---|
| [test-lead-isolation](scenarios/test-lead-isolation.json) | 0 | `lab` | A test lead goes only to the destination under test and never counts toward its streak |
| [chat-leads-refused](scenarios/chat-leads-refused.json) | 2 | `lab` | A destination that refuses chat leads gets not sent, with a reason; email still has it |
| [platform-outage](scenarios/platform-outage.json) | 3 | `lab` | 429 then 503 for 2½ hours: retried, never sooner than Retry-After; accepted after; streak stays zero |
| [relay-timeouts](scenarios/relay-timeouts.json) | 3 | `lab` | No answer from email is retried (a second copy is harmless) and accepted once the relay answers |
| [emergency-under-pressure](scenarios/emergency-under-pressure.json) | 6 | `lab` | A CRM makes the record and times out (unknown); paused; the fallback takes the emergency; escalation and the firm's confirmation at once; a lookup settles the CRM once it recovers; an operator's resend of it is refused |
| [stall-after-start](scenarios/stall-after-start.json) | 3 | `lab` | A worker that recorded its attempt and stalled past its lease still sends: the CRM gets one copy, settled by its late answer; email is resent, harmlessly |
| [unknown-resolved](scenarios/unknown-resolved.json) | 3 | `lab` | An operator closes, resends or finds an unknown delivery; the late answers that follow are evidence only, never undoing the resolution |
| [crm-timeout](scenarios/crm-timeout.json) | 3 | `lab` | A timeout is unknown: email is retried, the CRM waits for an operator and is never sent again |
| [dead-endpoint](scenarios/dead-endpoint.json) | 3 | `lab` | About 69 hours of retries, then gave up; 5 in a row switch the destination off |
| [redirect](scenarios/redirect.json) | 3 | `lab` | 301 is refused, never followed; 5 in a row switch off; reactivate and resend under the same identity |
| [switch-off](scenarios/switch-off.json) | 3 | `lab` | An accepted delivery resets the streak; 5 refused in a row switch off |
| [lead-problem](scenarios/lead-problem.json) | 3 | `lab` | A hang-up to a CRM that needs a name is not sent, as a lead problem, and never counts toward the streak |
| [resend-accepted](scenarios/resend-accepted.json) | 3 | `lab` | An operator's resend of an accepted lead is refused; a senior operator's, with the reason, is a new delivery, same identity, the newest counts |
| [routing-by-category](scenarios/routing-by-category.json) | 4 | `lab` | A CRM that takes new matters only: a billing question is not sent there, and reaches the firm by email |
| [mapping-gap](scenarios/mapping-gap.json) | 5 | `lab` | A live mapping missing a required field is refused as configuration; revised, approved, resent, accepted |
| [both-routes-down](scenarios/both-routes-down.json) | 6 | `lab` | Every route down: the firm with a fallback is reached a third into the window; the firm without expires |
| [incident-now](scenarios/incident-now.json) | 6 | `lab` | Retries keep the record and lose the client: accepted after recovery, hand-off already expired |
| [auth-expired](scenarios/auth-expired.json) | 6 | `lab` | 401: needs reconnection at once, held, fallback at once; reconnected, sent late, never switched off |
| [hook-gone](scenarios/hook-gone.json) | 6 | `lab` | 410: needs reconnection, held; the firm reconnects and held leads are accepted |
| [paused-destination](scenarios/paused-destination.json) | 6 | `lab` | Paused: held, hand-off to the fallback at once; on resume, sent with the original time, marked late |
| [follow-up-without-delivery](scenarios/follow-up-without-delivery.json) | 6 | `proposed` | RFC-010: every route down, yet the callback is owed at once; nobody accepts it in 15 minutes, so it escalates as a follow-up, not a delivery, escalation |

#### RFC-008 failure injection

Each scenario of [RFC-008 §10](../rfc/008_delivery_guarantees_and_evidence.md#10-failure-injection-scenarios),
the production gates, and the file that states it. Each asserts durable state,
emitted events and prohibited effects; its file's `model` says whether the lab passes it.

| RFC-008 §10 scenario | File | Slice | Events asserted | Prohibited effects |
|---|---|---|---|---|
| crash-after-send | [crash-after-send.json](scenarios/crash-after-send.json) | 3 | `DeliveryUnknown`, `DeliveryAccepted` | No attempt to the CRM after the crash; the CRM never accepted twice |
| crm-timeout | [crm-timeout.json](scenarios/crm-timeout.json) | 3 | `DeliveryUnknown` | No second attempt to the CRM after its timeout |
| double-claim | [double-claim.json](scenarios/double-claim.json) | 3 | `LeaseRecovered`, exactly one `AttemptMade`, `DeliveryAccepted` | No attempt after the stalled worker wakes |
| pause-during-send | [pause-during-send.json](scenarios/pause-during-send.json) | 3 | `DesiredStateChanged` (three), `Held`, `FallbackUsed` | Race before send: no attempt while paused. On the wire: the attempt finishes accepted, is not held, and no further attempt follows |
| routing-change-during-outage | [routing-change-during-outage.json](scenarios/routing-change-during-outage.json) | 5 | `RevisionWentLive`, `DeliveryCancelled`, `NotSent` | No attempt for a cancelled or not-sent delivery |
| accepted-never-verified | [accepted-never-verified.json](scenarios/accepted-never-verified.json) | 3 | `DeliveryVerified`, `VerificationOverdue` | Never verified without a found record |
| duplicate-intake | [duplicate-intake.json](scenarios/duplicate-intake.json) | 1 | Exactly one `ConversationHandled`, `LeadRecorded`, `AttemptMade` | No second conversation, lead or delivery (exact counts) |
| classification-corrected | [classification-corrected.json](scenarios/classification-corrected.json) | 9 | `ClassificationCorrected`, `UsageCredited` | No new charge after the cycle closes; no automatic send |
| cross-firm-read | [cross-firm-read.json](scenarios/cross-firm-read.json) | 0 | Four `AccessRefused` | No resend, no attempt from the refused request |
| database-down-at-intake | [database-down-at-intake.json](scenarios/database-down-at-intake.json) | 1 | `ConversationHandled`, `LeadRecorded`, `DeliveryAccepted`, once each | No capture, conversation or attempt while the database is down |
| all-routes-down | [all-routes-down.json](scenarios/all-routes-down.json) | 6 | `HandOffEscalated`, `HandOffExpired`, `HandOffAcknowledged` | The expired lead never reached; the acknowledged one never escalated or expired |

Billing (slice 10) and terms (slice 11) have no scenarios of their own yet. The
format now states a cycle closing and a conversation's billing effect (for
corrections), but full billing scenarios would need conversations with
durations, prices and statements, and terms scenarios would need terms versions,
users and acceptances, in `given`, `when` and `then`.

### Receivers

All are `lab`: what the fake receivers check today
([FakeCrm.fs](../../backend/src/Core/FakeCrm.fs)), with each one's `requires`
equal to the kind's fields in [Catalog.fs](../../backend/src/Core/Catalog.fs).
None is `confirmed` against a sandbox yet; each external product's `toConfirm`
lists what its sandbox must settle, including its duplicate handling.

| Kind | Content | Requires | Duplicates |
|---|---|---|---|
| [answering-legal](receivers/answering-legal.json) | JSON | first_name, last_name, phone, message | `unknown`: the legacy system answers 208 for an `answer_id` it has, but this kind sends `message_id` |
| [clio-grow](receivers/clio-grow.json) | JSON | inbox_lead.from_first, from_last, from_phone, from_message, from_source | `unknown` until its sandbox confirms |
| [mycase](receivers/mycase.json) | JSON | first_name, last_name, cell_phone_number, lead_details | `unknown` until its sandbox confirms |
| [lawmatics-form](receivers/lawmatics-form.json) | JSON | first_name, last_name, phone, message | `unknown` until its sandbox confirms |
| [lawmatics-form-v2](receivers/lawmatics-form-v2.json) | JSON | first_name, last_name, phone, full_message | `unknown` until its sandbox confirms |
| [lawmatics-api](receivers/lawmatics-api.json) | JSON | first_name, last_name, phone, match_contact_by, notes | `unknown` until its sandbox confirms |
| [lead-docket](receivers/lead-docket.json) | Form | First, Last, Phone, Summary | `unknown` until its sandbox confirms |
| [smokeball](receivers/smokeball.json) | JSON | contact.person.firstName, lastName, phone; matter.isLead; task.staffId | `unknown` until its sandbox confirms |
| [practicepanther](receivers/practicepanther.json) | JSON | account.primary_contact.first_name, phone_mobile; call_log.call_direction | `unknown` until its sandbox confirms |
| [lawruler](receivers/lawruler.json) | JSON | first_name, last_name, phone (retired) | `unknown` until its sandbox confirms |
| [hubspot](receivers/hubspot.json) | JSON | fields[firstname], fields[lastname], fields[phone] | `unknown` until its sandbox confirms |
| [zapier-webhook](receivers/zapier-webhook.json) | JSON | event_id, first_name, phone | `unknown`: the lead id is sent as `event_id`; filtering on it is each Zap's choice |
| [webhook](receivers/webhook.json) | JSON | Nothing by itself: the body is the firm's approved mapping | `unknown`: the lead id is sent as the signed `webhook-id` header; deduplicating on it is each receiver's choice |
| [email](receivers/email.json) | JSON | to, subject, text | `harmless` |
| [sms](receivers/sms.json) | JSON | to, text | `harmless` |
| [slack](receivers/slack.json) | JSON | text | `harmless` |

Next, in lab refactor phase 6: the lab's simulator and fake receivers read these
files instead of holding their own copies.
