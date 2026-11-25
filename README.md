# lead-integrations

An MVP for lead delivery

> **Status: the direction has moved on.** What firms told us changed the plan: lead
> delivery is the invisible foundation of a dependable intake service, not the
> product. The service is built on the call center as its source of truth
> ([where this sits](#where-this-sits)), and delivery integrates with it. The current
> direction is in [docs/business/](docs/business/), starting with
> [what-firms-need.md](docs/business/what-firms-need.md). This lab stays as an
> experimental tool and a record of its method. The vision summary and RFC-001 to
> RFC-010 are the earlier proposal: read them as evidence, not as the plan. The short
> version for leadership is [leadership-brief.md](docs/business/leadership-brief.md).

## Where this sits

**The call center ([`../call-center-lab`](../call-center-lab/README.md)) is the source of
truth** (*Proposed*: the integration is not built). It owns every interaction, call or
chat, answered by the AI or by a person, from arrival to end; the people who answer;
each firm's configuration as applied; and what each interaction produced. It publishes
those through its own versioned contracts
([its outputs](../call-center-lab/docs/black-box.md#outputs)).

Everything else is built on top of it and integrates through those contracts. None of
it keeps its own copy of what happened on a call.

| Built on the center | Takes from it | Gives back |
|---|---|---|
| **Delivery** (this repo) | The lead, once, with its interaction's ID | Delivery outcomes and proof of hand-off, for the firm and for operations |
| **Follow-up** (with intake) | Follow-up progress: accepted, called back, resolved (O4) | — |
| **Call categories** (this repo) | Each interaction's intake labels | The category, on the lead and in reports |
| **Billing** (this repo) | Usage evidence per interaction (O7) | Statements; nothing the center acts on |
| **Terms and the firm portal** (this repo) | The firm, as the center knows it | Acceptance records |
| **Intake policy** (`../intake-policy-lab`) | — | Each firm's approved rules, which the center applies |

Contracts change additively while in use, and each side names its own terms behind an
adapter, so a change inside the center never reaches a destination. The lab's leads today
are imported from a read-only copy of the legacy system's data and synthetic traffic; they
are evidence of what firms need, not a source the product keeps.

## Start here

New to this work? Read these in order. The first three are the current direction;
the rest describe this lab, for when you need them.

**What is agreed and what is draft.** Not everything here has the same standing:

| Agreed: build on it | Draft: expect it to change |
|---|---|
| The glossary, and the decisions in [answers.md](docs/lead-delivery/answers.md) (each marked *Decided*) | [RFC-001 to RFC-010](docs/rfc/README.md): proposed, awaiting approval. RFC-007 (the model and build order) and RFC-008 (the guarantees and evidence, which revises RFC-007) set the build; RFC-009 (routing by category) and RFC-010 (one lead, two lifecycles) are the newest |
| The direction in [docs/business/](docs/business/): a dependable intake service built on the call center as the source of truth ([where this sits](#where-this-sits)). (The earlier decision to build the product new, in RFC-007's slices, is superseded; see [next-12-months.md](docs/business/next-12-months.md#where-we-are)) | The scenario and receiver formats in [contracts/](docs/contracts/README.md): a draft, with 34 scenarios (including RFC-008's ten failure-injection scenarios) and a receiver contract for every catalog kind; the runner plays them against the lab (`python tools/run_scenarios.py`); no CRM contract confirmed against its sandbox |
| Every rule in the [domain model](docs/lead-delivery/README.md) marked *Lab* or *Production* | Every rule marked *Proposed*: agreed direction, not built |

A scenario marked `"model": "proposed"` is expected to fail against the lab
until its rule is built. One does today on purpose: `follow-up-without-delivery`,
RFC-010's follow-up lifecycle, which the lab does not have yet. Its `today` field
says how the lab differs. It is not a bug.

| # | Read | To learn | Time |
|---|---|---|---|
| 1 | [business/what-firms-need.md](docs/business/what-firms-need.md) | What firms use us for and complain about. Everything else follows from it | 5 min |
| 2 | [business/commercial-case.md](docs/business/commercial-case.md) | Who buys, why, and what we changed our minds about | 15 min |
| 3 | [business/next-12-months.md](docs/business/next-12-months.md), then [what-the-lab-taught-us.md](docs/business/what-the-lab-taught-us.md) | The plan for the year, in order, and what of this lab carries over | 20 min |
| 4 | [GLOSSARY.md](GLOSSARY.md) | The words. Code, screens and docs use them as written | 15 min |
| 5 | [lead-delivery/README.md](docs/lead-delivery/README.md) | The delivery model: a lead's journey, lifecycles, policies, invariants, events | 30 min |
| 6 | [RFC-007](docs/rfc/007_domain_model_and_build_order.md) | The whole product's contexts, what any implementation must guarantee, the order to build features in, and how each is built (its build order is superseded) | 30 min |
| 7 | [RFC-008](docs/rfc/008_delivery_guarantees_and_evidence.md) | What each guarantee means at each boundary, evidence for a hand-off, who owns a destination's state, and the failure scenarios that gate production | 25 min |
| 8 | [contracts/](docs/contracts/README.md) | The checks a feature must pass, as scenario and receiver files | 10 min |
| 9 | [lead-delivery/answers.md](docs/lead-delivery/answers.md) | Every decision, why, and what would change it | 15 min |
| 10 | [lead-delivery/operations.md](docs/lead-delivery/operations.md) | What operators do, screen by screen | 15 min |
| 11 | [lead-delivery/code-explorer.html](docs/lead-delivery/code-explorer.html) (open in a browser), then [code-map.md](docs/lead-delivery/code-map.md) | The lab's code functionally: the data model, every flow from input to rows written, an example lead followed step by step; then where each flow lives (only if you work on the lab) | 25 min |

Later, by need: the earlier proposal to leadership ([vision-meeting/summary.md](docs/vision-meeting/summary.md), superseded by docs/business/), the lab's direction and roadmap ([strategy.md](docs/lead-delivery/strategy.md)),
what building the lab taught us ([lessons.md](docs/lead-delivery/lessons.md)), what
the legacy system in production does, as evidence ([production-today.md](docs/lead-delivery/production-today.md)),
the proposal for each area ([rfc/](docs/rfc/README.md)), and the real situations to walk through with operations and firms to test what the decisions assume ([discovery scenarios](docs/discovery/scenarios.md)).

## Run

```sh
cd backend
dotnet run --project src/Import -- all
dotnet run --project src/Api                # http://localhost:5080
dotnet run --project tests/Tests --artifacts-path ../.testbuild   # tests; safe while the API runs
python ../tools/run_scenarios.py            # every contract scenario against a fresh lab
python ../tools/publish_leads.py --alias general-firm=firm_… ../../call-center-lab/out/leads   # the call center's leads (docs/contracts/intake-lead/)
dotnet run --project src/Import -- seed --leads 200   # synthetic traffic; the API's worker delivers it
cd ../frontend && npm install && npm run dev # http://localhost:5173, proxies to 5080
```

The tests build into `.testbuild/` because a running API locks its own `bin/`
(MSB3026 "file is locked by LeadIntegrations.Api"); with the API stopped,
`dotnet run --project tests/Tests` works too.

`npm run build` in `frontend/` lets the backend serve the UI itself at :5080.
Importer options: `--pg`, `--dump`, `--sqlite`, `--db` (see `src/Import/Program.fs`).
`seed` options: `--leads`, `--seed` (same seed, same leads), `--firm`. Seeded leads
use invented people (555-01xx, example.com), the firm's own contact-type names,
and reference-lead intake wording (`docs/call-categories/`). They go only to
firms with an enabled connection, which delivers to the local fake receivers.

`simulate` (`--days 7`, `--seed 7`, `--crm-leads 40`) replaces the delivery
history with a simulated week over the existing leads, so every state the
Delivery Center, the delivery graph and Leads can show has rows: an Answering
Legal outage recovered by retries, email-relay timeouts, a dead endpoint
switched off after 5 failures, a 410 Gone hook, expired Clio auth, a MyCase
redirect fixed, reactivated and replayed, a paused destination, routing skips,
tests, and PracticePanther still retrying now (the worker finishes those). It
uses the real routing, payload, receiver-check and retry rules at real time,
sends nothing, and is repeatable: a rerun removes what the last one added.
Firms with an enabled CRM are topped up to `--crm-leads` synthetic leads.
Stop the API while it runs. The week also has two firms that lose every route
during the outage: one with an agreed SMS fallback (its new matters reach it in
time), one without (they expire).

The proposal to leadership is in `docs/vision-meeting/` (a one-page summary, the
script with core and backup screens, our answers). Before capturing screenshots,
`dotnet run --project src/Import -- demo-names` gives every firm a fictional name
(re-importing restores the real ones), so no real firm appears in a deck. Regenerate its screenshots with the API running:
`python tools/capture_vision_deck.py` (Python Playwright with Chromium).

## What is where

| | |
|---|---|
| `docs/rfc/` | The proposals for approval, RFC-001 to RFC-010: delivery in time, call categories, billing, terms, the standard lead and integrations, the lab, the domain model and build order, the guarantees and evidence, routing by category, one lead and two lifecycles |
| `docs/contracts/` | Scenarios and receiver contracts as language-neutral data; `python tools/check_contracts.py` checks them |
| `docs/vision-meeting/` | The proposal: screens in story order, what we will deliver, questions and answers |
| `docs/billing/strategy.md` | The billing model and how to move off the legacy one |
| `docs/terms/strategy.md` | Terms of service: acceptance, versions and the record |
| `GLOSSARY.md` | The domain's words: firm, lead, destination, delivery, held, resend… |
| `docs/lead-delivery/` | Start with `strategy.md` (direction and roadmap); `answers.md` (every decision); `lessons.md` (what building it taught us); the domain model, operator workflows and screens, how production compares, and `code-map.md`: where each flow lives in the code |
| `docs/api.md` | The contract between backend and frontend |
| `backend/src/Core/Catalog.fs` | Every integration kind, its auth, and what each legacy source calls it: the table to agree on |
| `backend/src/Core/Payloads.fs` | The body each kind receives |
| `backend/src/Core/FakeCrm.fs` | What each stand-in receiver requires |
| `backend/src/Core/Dispatch.fs` | Routing, outbox, retries, disable on 410 |
| `backend/src/Core/Onboarding.fs` | Versions, approvals, draft revisions, discard |
| `backend/src/Import/` | The legacy importer: one read-only reader per legacy source, for evidence until the call center's lead contract replaces it |
| `frontend/src/pages/` | Integrations overview, firms, delivery graph, Delivery Center, onboarding; `review.tsx` is the client page |

## Rules this MVP keeps

- **Shape-only credentials.** A credential is stored as present/absent plus a
  masked fingerprint (`••••a1b2`, from a salted hash). Firms are matched across
  legacy sources by a salted hash of their account key, and only when exactly
  one firm holds that key.
- **Nothing reaches a real CRM.** Imported connections point at the local receivers.
- **No caller PII.** Leads are synthetic, built from each firm's real intake
  labels and contact types.
- **Honest delivery stages.** A 2xx is "accepted by receiver", never "in the CRM".
