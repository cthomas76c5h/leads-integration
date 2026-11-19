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
| [receivers/](receivers/) | One file per destination kind: what it requires, how it accepts and refuses |

Check every file:

```sh
python tools/check_contracts.py
```

It validates each file against its schema, then checks what a schema cannot:
every firm, destination and lead a scenario names exists, every mode is in
`modes.json`, and every category and practice area is in
[taxonomy.json](../call-categories/taxonomy.json).

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
| `given` | `clock` (time zero, with its time zone); `firms` (fictional names, always); `destinations` with kind, role (`usual` or `fallback`), state, routing (categories, chat leads) and, for a webhook, the body fields its live revision sends (`mapping`) |
| `when` | Steps, each at an offset from the clock, largest unit first (`+30s`, `+5m`, `+2h30m`, `+25h10m`, `+1d`), doing one thing: a **lead** is recorded; a destination's **receiver** starts answering a certain way (a mode, optionally with fields it requires on top of its kind's; an exact answer; or no answer); someone takes an **action** (resend, pause, resume, reactivate, reconnect, send missed leads, change a mapping, approve) |
| `then` | Expectations, each at an offset, stating one thing that holds then: a **destination**'s state and failure streak; the newest **delivery** of a lead to a destination (its state, failure class, attempts, how many deliveries the pair has with resends, whether it was late); that a lead has **no delivery** to a destination at all; a lead's **hand-off**; a lead's **outcome** |

Expectation times allow for the retry schedule's ±20% jitter, so a scenario
holds whatever the random draw. `approve` is the firm contact approving a draft
revision, after which it is live: setup's test and confirm steps are not modelled.

States use the model's names ([domain model §4](../lead-delivery/README.md#4-lifecycles)),
not any implementation's: `needs-reconnection`, not `disabled`; `held`, not
`pending`. Callers are invented, and a scenario states only what the rules read
(category, practice area, channel, hang-up, test).

A scenario with `model: proposed` is expected to fail on the lab until the rule
is built. That is its purpose: it is the acceptance test for the slice.

## A receiver contract

What one destination kind requires, and how it answers. The fake receivers are
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
| `toConfirm` | What the sandbox must still settle |

Modes (`ok`, `reject`, `flaky`, `gone`, `down`, `slow`, `expired-auth`) are the
same for every kind and live once, in [modes.json](receivers/modes.json).

## What is here

Every scenario the lab's simulator runs, a scenario for each delivery rule that
had none, and a receiver contract for every kind in the catalog. A `proposed`
scenario fails on the lab today; its `today` field says how the lab differs.

### Scenarios

| Scenario | Slice | Model | Proves |
|---|---|---|---|
| [test-lead-isolation](scenarios/test-lead-isolation.json) | 0 | `lab` | A test lead goes only to the destination under test and never counts toward its streak |
| [chat-leads-refused](scenarios/chat-leads-refused.json) | 2 | `lab` | A destination that refuses chat leads gets not sent, with a reason; email still has it |
| [platform-outage](scenarios/platform-outage.json) | 3 | `lab` | 429 then 503 for 2½ hours: retried, never sooner than Retry-After; accepted after; streak stays zero |
| [relay-timeouts](scenarios/relay-timeouts.json) | 3 | `lab` | No answer is transient: retried and accepted once the relay answers |
| [dead-endpoint](scenarios/dead-endpoint.json) | 3 | `lab` | About 21 hours of retries, then gave up; 5 in a row switch the destination off |
| [redirect](scenarios/redirect.json) | 3 | `lab` | 301 is refused, never followed; 5 in a row switch off; reactivate and resend under the same identity |
| [switch-off](scenarios/switch-off.json) | 3 | `lab` | An accepted delivery resets the streak; 5 refused in a row switch off |
| [lead-problem](scenarios/lead-problem.json) | 3 | `proposed` | A hang-up to a CRM that needs a name is not sent, as a lead problem, and never counts toward the streak |
| [resend-accepted](scenarios/resend-accepted.json) | 3 | `proposed` | Only a senior operator resends an accepted lead; a new delivery, same identity, the newest counts |
| [routing-by-category](scenarios/routing-by-category.json) | 4 | `proposed` | A CRM that takes new matters only: a billing question is not sent there, and reaches the firm by email |
| [mapping-gap](scenarios/mapping-gap.json) | 5 | `lab` | A live mapping missing a required field is refused as configuration; revised, approved, resent, accepted |
| [both-routes-down](scenarios/both-routes-down.json) | 6 | `lab` | Every route down: the firm with a fallback is reached a third into the window; the firm without expires |
| [incident-now](scenarios/incident-now.json) | 6 | `lab` | Retries keep the record and lose the client: accepted after recovery, hand-off already expired |
| [auth-expired](scenarios/auth-expired.json) | 6 | `proposed` | 401: needs reconnection at once, held, fallback at once; reconnected, sent late, never switched off |
| [hook-gone](scenarios/hook-gone.json) | 6 | `proposed` | 410: needs reconnection, held; the firm reconnects and held leads are accepted |
| [paused-destination](scenarios/paused-destination.json) | 6 | `proposed` | Paused: held, hand-off to the fallback at once; on resume, sent with the original time, marked late |

Billing (slice 10) and terms (slice 11) have no scenarios yet: the format covers
delivery only, and would need conversations, statements and terms versions added
to `given`, `when` and `then`.

### Receivers

All are `lab`: what the fake receivers check today
([FakeCrm.fs](../../backend/src/Core/FakeCrm.fs)), with each one's `requires`
equal to the kind's fields in [Catalog.fs](../../backend/src/Core/Catalog.fs).
None is `confirmed` against a sandbox yet; each external product's `toConfirm`
lists what its sandbox must settle.

| Kind | Content | Requires |
|---|---|---|
| [answering-legal](receivers/answering-legal.json) | JSON | first_name, last_name, phone, message |
| [clio-grow](receivers/clio-grow.json) | JSON | inbox_lead.from_first, from_last, from_phone, from_message, from_source |
| [mycase](receivers/mycase.json) | JSON | first_name, last_name, cell_phone_number, lead_details |
| [lawmatics-form](receivers/lawmatics-form.json) | JSON | first_name, last_name, phone, message |
| [lawmatics-form-v2](receivers/lawmatics-form-v2.json) | JSON | first_name, last_name, phone, full_message |
| [lawmatics-api](receivers/lawmatics-api.json) | JSON | first_name, last_name, phone, match_contact_by, notes |
| [lead-docket](receivers/lead-docket.json) | Form | First, Last, Phone, Summary |
| [smokeball](receivers/smokeball.json) | JSON | contact.person.firstName, lastName, phone; matter.isLead; task.staffId |
| [practicepanther](receivers/practicepanther.json) | JSON | account.primary_contact.first_name, phone_mobile; call_log.call_direction |
| [lawruler](receivers/lawruler.json) | JSON | first_name, last_name, phone (retired) |
| [hubspot](receivers/hubspot.json) | JSON | fields[firstname], fields[lastname], fields[phone] |
| [zapier-webhook](receivers/zapier-webhook.json) | JSON | event_id, first_name, phone |
| [webhook](receivers/webhook.json) | JSON | Nothing by itself: the body is the firm's approved mapping |
| [email](receivers/email.json) | JSON | to, subject, text |
| [sms](receivers/sms.json) | JSON | to, text |
| [slack](receivers/slack.json) | JSON | text |

Next, in lab refactor phase 6: the lab's simulator and fake receivers read these
files instead of holding their own copies.
