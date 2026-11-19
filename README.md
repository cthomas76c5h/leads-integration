# lead-integrations

An MVP for one question: **which lead-delivery integrations does Answering AI
support, and does each one work?** It imports every firm and delivery
configuration from the three existing systems, shows them side by side, and
sends synthetic leads through each integration to local stand-ins that check
the payload the way the real CRM would.

```
api-2 (Postgres) ─┐                         ┌─► /receiver/crm/clio-grow   checks inbox_lead.*
chatbot (dump)  ──┼─► import ─► SQLite ─► outbox ─► worker ─┼─► /receiver/crm/mycase      checks first_name, …
airi (SQLite)   ──┘   (shape only)                          └─► … one per catalog kind
```

## Start here

New to this work? Read these in order. The first six are enough to build a
slice; the rest are for when you need them.

**What is agreed and what is draft.** Not everything here has the same standing:

| Agreed: build on it | Draft: expect it to change |
|---|---|
| The glossary, and the decisions in [answers.md](docs/lead-delivery/answers.md) (each marked *Decided*) | [RFC-001 to RFC-008](docs/rfc/README.md): proposed, awaiting approval. RFC-007 (the model and build order) and RFC-008 (the guarantees and evidence, which revises RFC-007) are the newest |
| The product is **built new, to the domain model**, in RFC-007's slices; api-2 is not extended ([answers](docs/lead-delivery/answers.md#routing-and-integrations)); the milestones are the [roadmap](docs/lead-delivery/strategy.md#5-roadmap) | The scenario and receiver formats in [contracts/](docs/contracts/README.md): a draft, with 26 scenarios (including RFC-008's ten failure-injection scenarios) and a receiver contract for every catalog kind; no runner yet, and no CRM contract confirmed against its sandbox |
| Every rule in the [domain model](docs/lead-delivery/README.md) marked *Lab* or *Production* | Every rule marked *Proposed*: agreed direction, not built |

A scenario marked `"model": "proposed"` is expected to fail against the lab
until its rule is built. Sixteen do today on purpose (for example `auth-expired`,
the acceptance test for slice 6: held deliveries, needs reconnection, fallback
at once); each says how the lab differs in its `today` field. They are not bugs.

| # | Read | To learn | Time |
|---|---|---|---|
| 1 | [vision-meeting/summary.md](docs/vision-meeting/summary.md) | What firms buy from us, what goes wrong today, and what we will do | 5 min |
| 2 | [GLOSSARY.md](GLOSSARY.md) | The words. Code, screens and docs use them as written | 15 min |
| 3 | [lead-delivery/README.md](docs/lead-delivery/README.md) | The delivery model: a lead's journey, lifecycles, policies, invariants, events | 30 min |
| 4 | [RFC-007](docs/rfc/007_domain_model_and_build_order.md) | The whole product's contexts, what any implementation must guarantee, the order to build features in, and how each is built | 30 min |
| 5 | [RFC-008](docs/rfc/008_delivery_guarantees_and_evidence.md) | What each guarantee means at each boundary, evidence for a hand-off, who owns a destination's state, and the failure scenarios that gate production | 25 min |
| 6 | [contracts/](docs/contracts/README.md) | The checks a feature must pass, as scenario and receiver files | 10 min |
| 7 | [lead-delivery/answers.md](docs/lead-delivery/answers.md) | Every decision, why, and what would change it | 15 min |
| 8 | [lead-delivery/operations.md](docs/lead-delivery/operations.md) | What operators do, screen by screen | 15 min |
| 9 | [lead-delivery/code-map.md](docs/lead-delivery/code-map.md) | Where each flow lives in this lab's code (only if you work on the lab) | 10 min |

Later, by need: the direction and roadmap ([strategy.md](docs/lead-delivery/strategy.md)),
what building the lab taught us ([lessons.md](docs/lead-delivery/lessons.md)), what
today's production system does, as evidence ([production-today.md](docs/lead-delivery/production-today.md)),
and the proposal for each area ([rfc/](docs/rfc/README.md)).

## Run

```sh
cd backend
dotnet run --project src/Import -- all      # api-2 (read-only), chatbot dump, airi dev db
dotnet run --project src/Api                 # http://localhost:5080
dotnet run --project tests/Tests --artifacts-path ../.testbuild   # tests; safe while the API runs
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
The api-2 password comes from `pgpass.conf` or `PGPASSWORD`.

## What is where

| | |
|---|---|
| `docs/rfc/` | The proposals for approval, RFC-001 to RFC-007: delivery in time, call categories, billing, terms, the standard lead and integrations, the lab, the domain model and build order |
| `docs/contracts/` | Scenarios and receiver contracts as language-neutral data; `python tools/check_contracts.py` checks them |
| `docs/vision-meeting/` | The proposal: screens in story order, what we will deliver, questions and answers |
| `docs/billing/strategy.md` | The billing model and how to move off the legacy one |
| `docs/terms/strategy.md` | Terms of service: acceptance, versions and the record |
| `GLOSSARY.md` | The domain's words: firm, lead, destination, delivery, held, resend… |
| `docs/lead-delivery/` | Start with `strategy.md` (direction and roadmap); `answers.md` (every decision); `lessons.md` (what building it taught us); the domain model, operator workflows and screens, how production compares, and `code-map.md`: where each flow lives in the code |
| `docs/api.md` | The contract between backend and frontend |
| `backend/src/Core/Catalog.fs` | Every integration kind, its auth, and what each source system calls it: the table to agree on |
| `backend/src/Core/Payloads.fs` | The body each kind receives (from api-2's serializers) |
| `backend/src/Core/FakeCrm.fs` | What each stand-in receiver requires |
| `backend/src/Core/Dispatch.fs` | Routing (api-2 contact types, webchat switch), outbox, retries, disable on 410 |
| `backend/src/Core/Onboarding.fs` | RFC-001 §4–6: versions, approvals, draft revisions, discard |
| `backend/src/Import/` | One reader per source system |
| `frontend/src/pages/` | Integrations overview, firms, delivery graph, Delivery Center, onboarding; `review.tsx` is the client page |

## Rules this MVP keeps

- **Shape-only credentials.** A credential is stored as present/absent plus a
  masked fingerprint (`••••a1b2`, from a salted hash). Firms are matched across
  systems by a salted hash of their Answering Legal key, and only when exactly
  one firm holds that key.
- **Nothing reaches a real CRM.** Imported connections point at the local receivers.
- **No caller PII.** Leads are synthetic, built from each firm's real intake
  labels and contact types.
- **Honest delivery stages.** A 2xx is "accepted by receiver", never "in the CRM".

## First import (2026-11-18)

| | |
|---|---|
| Firms | 5,541. api-2 5,505, chatbot 208, airi 2 |
| Matched across systems | 174 chatbot firms are the same firm as an api-2 client |
| CRM rows (api-2) | 444. MyCase 219, Clio Grow 177, Lead Docket 16, Lawmatics 26 (three integrations), Smokeball 4, PracticePanther 2 |
| Enabled in api-2 | 8 of 444. api-2 sends only to `enabled` rows, so this local copy delivers to 8 CRMs. Check against production |
| Shared keys | 18 chatbot firms share 4 Answering Legal keys: their leads land in one account |
| OAuth without token | 18 rows: the firm must authorize again |
| Not on disk | chatbot HubSpot configs; airi production destinations |
