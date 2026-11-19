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
| `given` | `clock` (time zero, with its time zone); `firms` (fictional names, always); `destinations` with kind, role (`usual` or `fallback`), state and routing |
| `when` | Steps, each at an offset (`+30s`, `+5m`, `+2h`, `+1d`) doing one thing: a **lead** is recorded; a destination's **receiver** starts answering a certain way (a mode, an exact answer, or no answer); someone takes an **action** |
| `then` | Expectations, each at an offset, stating one thing that holds then: a **destination**'s state; the newest **delivery** of a lead to a destination; a lead's **hand-off**; a lead's **outcome** |

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
| `requires`, `optional` | Each field (a dotted JSON path, `name[key]` for an array of name and value, or a form field) and which part of the standard lead fills it |
| `accepts` | The status of an accepted lead |
| `refusesMissing` | The answer when a required field is missing or blank |
| `toConfirm` | What the sandbox must still settle |

Modes (`ok`, `reject`, `flaky`, `gone`, `down`, `slow`, `expired-auth`) are the
same for every kind and live once, in [modes.json](receivers/modes.json).

## What is here so far

| File | Status |
|---|---|
| [scenarios/auth-expired.json](scenarios/auth-expired.json) | `proposed`: needs reconnection at once, held deliveries, fallback hand-off. The lab fails it today; its `today` field says how |
| [receivers/clio-grow.json](receivers/clio-grow.json) | `lab`: what `FakeCrm.fs` checks for Clio Grow today, with what the sandbox must confirm |

Next, in phase 6: every scenario in `Simulate.fs` and every kind in `Catalog.fs`
as a file here, and the lab reading them.
