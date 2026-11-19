# Lead delivery: code map

Where each part of the [model](README.md) and each [operator workflow](operations.md)
lives in the code, in the order it runs. Functions are named rather than given
line numbers, so search for the name in the linked file. Paths are from the repo
root. Words in **bold** are in [GLOSSARY.md](../../GLOSSARY.md).

When you change a flow below, update this file in the same change.

## Names the code still uses

The code predates the glossary in a few places:

| Glossary | Code |
|---|---|
| **Destination** | `Connection`, `connections` table, `/api/connections` |
| **Lead** (recorded) | `LeadEvent`, `events` table |
| **Resend** | `replay`, `replayOf`, `/replay` |
| **Not sent** | status `skipped` |
| **Paused**, **Needs reconnection**, **Switched off**, **Off in source** | `Enabled = false` or `Status = "disabled"`, told apart in `Destinations.all` |
| **Refused**, **Gave up** | status `failed` (the reason and `Diagnosis` say which) |

## Layers

| Layer | Files | Role |
|---|---|---|
| HTTP | [Routes.fs](../../backend/src/Api/Routes.fs), [Handlers.fs](../../backend/src/Api/Handlers.fs) | Thin: parse, call Core, answer. Every route is in [api.md](../api.md) |
| Domain types | [Domain.fs](../../backend/src/Core/Domain.fs) | `Lead`, `LeadEvent`, `Delivery`, `Attempt`, `Diagnosis`, `Connection`, `Setup` |
| Rules | [Policy.fs](../../backend/src/Core/Policy.fs), [Dispatch.fs](../../backend/src/Core/Dispatch.fs), [Diagnose.fs](../../backend/src/Core/Diagnose.fs), [Categories.fs](../../backend/src/Core/Categories.fs), [Issues.fs](../../backend/src/Core/Issues.fs) | What happens to a delivery, and what it means |
| Bodies and receivers | [Payloads.fs](../../backend/src/Core/Payloads.fs), [FakeCrm.fs](../../backend/src/Core/FakeCrm.fs), [Receivers.fs](../../backend/src/Api/Receivers.fs) | What we send; what the stand-in CRMs accept |
| Storage and read models | [Db.fs](../../backend/src/Core/Db.fs) (schema), [Store.fs](../../backend/src/Core/Store.fs), [Destinations.fs](../../backend/src/Core/Destinations.fs) | Plain SQL |
| Background | [Worker.fs](../../backend/src/Api/Worker.fs) | Sends whatever is due |
| Setup and change requests | [Onboarding.fs](../../backend/src/Core/Onboarding.fs) | RFC-001 sections 4–6: versions, approvals, test, go-live |
| Lab data | [Import.fs](../../backend/src/Core/Import.fs) and `backend/src/Import/`, [Seed.fs](../../backend/src/Core/Seed.fs), [Simulate.fs](../../backend/src/Core/Simulate.fs) | Firms in, leads in, a simulated week |

F# compiles in file order, so [Core.fsproj](../../backend/src/Core/Core.fsproj)
is also a reading order: each file uses only those above it.

## 1. A lead is recorded and its deliveries planned

`POST /api/firms/{id}/leads` (and the same path for seeded leads):

1. `Handlers.sendLead` → `Dispatch.publish`. Everything below is one transaction (`Db.write`), so a lead is never recorded without its deliveries.
2. `Store.insertEvent` records the lead.
3. `Store.connectionsOf` lists the firm's destinations; only enabled, active ones not in setup are taken (a test lead goes only to the destination under test).
4. `Dispatch.routingExclusion` decides each one: a chat lead to a destination that refuses chats, or a CRM whose firm has active call types none of which match, is **not sent** (`skipped`, with the reason). Otherwise `pending`.
5. `Store.insertDelivery` writes each delivery (built by `Dispatch.newDelivery`).

## 2. A delivery is sent, retried and settles

1. `DeliveryWorker` ([Worker.fs](../../backend/src/Api/Worker.fs)) loops on `Dispatch.runDue`.
2. `Store.claimDue` moves due rows `pending` → `delivering`, so no two workers send one. On start, `Store.requeueInterrupted` puts back any left `delivering`.
3. `Dispatch.attempt`, per delivery:
   - destination not active → `cancelled`;
   - `Dispatch.buildFor` → `Payloads.build` (a webhook with a setup renders its template with `Template.render`); a body that cannot be built → `failed "body: …"` (**our fault**, no attempt);
   - POST to the destination's URL. In the lab that is `Receivers.handle`, which checks the body with `FakeCrm.checkWith` (the kind's required fields plus any `?require=`), then answers as its mode says (ok, reject, flaky, gone, slow, down).
4. `Policy.classify` turns the answer into `Delivered`, `Retry`, `Failed` or `Disable`.
5. `Dispatch.transition` is the whole state machine, shared by the worker and the simulator:
   - `Retry` → `pending` again, next time from `Policy.nextDelayMs` (`Policy.retryScheduleMs`, never sooner than `Retry-After`);
   - out of attempts, or refused → `failed`; a production failure adds to the **failure streak**, and at `Policy.disableAfterConsecutiveFailures` the destination is **switched off**;
   - `Disable` (410) → the destination stops and `Store.cancelPendingFor` cancels its other pending deliveries;
   - `Delivered` resets the streak.
6. Written by `Store.insertAttempt`, `Store.setDeliveryState`, `Store.updateConnection`.

## 3. How a delivery is read: stage and diagnosis

Every delivery read from the database passes through `Store.withAttempts`, which adds:

- **Stage** (`Store.stageOf`): queued, accepted, refused or not sent: what we can claim.
- **Diagnosis** (`Diagnose.diagnose`): the failure class and the cause in the receiver's words (`Diagnose.receiverWords`). The class is checked in this order: `routing` (not sent), `transient` while retrying, `stopped` (cancelled), `ours` (`body:`), `configuration` (address refused, 3xx), `connection` (401, 403, 410), `lead` (a 4xx about a name or phone the lead does not have), `configuration` (any other 4xx), `transient` (gave up).

The frontend only renders it:

- [lib/delivery.ts](../../frontend/src/lib/delivery.ts): `CLASS` (labels and tones), `problem`, `nextAction`, `needsAttention`, `journey`.
- [components/Deliveries.tsx](../../frontend/src/components/Deliveries.tsx): `StatusBadge`, `Journey`, `ProblemPanel` (the buttons each class gets), `DeliveryList`.

## 4. Operator actions

| Action | UI | Backend |
|---|---|---|
| Resend | `ProblemPanel`; bulk in the Delivery Center | `Handlers.replay` → `Dispatch.replay`: a new delivery for the same lead, `replayOf` set |
| Pause, resume, reactivate | `ProblemPanel`; Destinations `Row` | `Handlers.patchConnection`. A delivery whose destination is paused is held at send time (`Dispatch.attempt`: pending, no next attempt, `Store.heldReason`); resuming or reactivating releases it (`Store.releaseHeld`). Test leads still go |
| Send a test lead | same | `Handlers.testConnection` → `Dispatch.testConnection` (a test lead to this destination only) |
| Send missed leads | Destinations `Row` | `Handlers.sendMissed` → `Dispatch.sendMissed`, over `Store.missedEvents`; routing applies |
| Edit mapping | `MappingFix` in [Fix.tsx](../../frontend/src/components/Fix.tsx), reusing `MappingEditor` from [Onboarding.tsx](../../frontend/src/pages/Onboarding.tsx) | `Handlers.patchSetup` → `Onboarding.update` (a draft version; live unchanged), then `send` → `decide` (the firm, through its secure link) → `test` → `confirm` → `goLive`. State: `Onboarding.stateOf` |
| See what we send | `PreviewFix` in [Fix.tsx](../../frontend/src/components/Fix.tsx) | `Handlers.preview` (the body plus what `FakeCrm.checkWith` would refuse) |

Only a webhook with a setup takes our mapping as its body (`Onboarding.enforced`);
every other kind's body is fixed in `Payloads.build`.

## 4b. Window to act and fallback

1. `Urgency.windowMs` gives each call category its window to act (72 hours; 24 for emergencies and custody) and `Urgency.fallbackAfterMs` its fallback delay (1 hour; 15 minutes): two separate clocks, read by `Timing` from [delivery-timing.json](delivery-timing.json), which also holds the retry schedule `Policy.retryScheduleMs` reads. Set by Answering AI; `DELIVERY_TIMING_FILE` points a deployment at its own copy. `Urgency.handOffState` says on time, expired or waiting.
1a. A lead every destination excluded by routing gets no `HandOff` (`Store.readLead`), so it is never expired and not in the in-time numbers; `Dispatch.runFallbacks` and the simulator's `HandOffCheck` never send it to the fallback.
1b. The screens never say "Expired" alone: an expired lead that reached the firm is *Reached late* (amber), one that did not is *Not reached in time* (red) (`handOffText` in `frontend/src/lib/delivery.ts`, the Leads badge).
2. Every lead read (`Store.listLeads`, `Store.leadOrFail`) carries a `HandOff`: its first accepted delivery anywhere, by which destination, and whether that was the fallback. An expired hand-off makes the lead's outcome `expired` (`Store.outcomeOf`).
3. A destination with the setting `role: fallback` is skipped by `Dispatch.publish`. `Dispatch.runFallbacks`, called every few seconds by `DeliveryWorker`, gives it any lead with no accepted delivery once `Urgency.fallbackAfterMs` has passed, once per lead.
4. The simulator does the same with its `HandOffCheck` action.

## 4c. Billing and terms of service

1. **Price book and accounts:** `Billing.seedPriceBook` and `Billing.seedAccounts` (called by the simulator) fill `price_book`, `billing_accounts` and `subscriptions` ([Db.fs](../../backend/src/Core/Db.fs) schema).
2. **Conversations:** `Billing.conversations` turns a cycle's production leads into conversations, each billable or free with the rule (`Billing.rules`) that made it free; talk time is synthetic (`Billing.seconds`).
3. **Rating:** `Billing.rate` is a pure function of the conversations, the effective-dated subscriptions (prorated segments) and the price book; it adds the value (new matters, hand-off in time), the legacy shadow (`Billing.legacy`) and the controls (`checks`).
4. **Locking:** `Billing.statement` rates the current cycle live and stores a closed cycle once in `invoices`, returning it unchanged ever after.
5. **Plan changes:** `Billing.changePlan`: an upgrade ends the current subscription now, a downgrade at the next cycle.
6. **Terms:** [Terms.fs](../../backend/src/Core/Terms.fs): `publish`, `accept`, `gate` (block, notice or nothing for one user), `coverage`, `acceptances`, and `seed` (the version history).

## 5. Screens and the queries behind them

| Screen | Frontend | Backend |
|---|---|---|
| Delivery Center tiles | `DeliveryCenter` ([DeliveryCenter.tsx](../../frontend/src/pages/DeliveryCenter.tsx)), `api.deliverySummary` | `Handlers.deliverySummary` → `Store.deliverySummary` |
| Delivery log, incl. `status=attention` and `late` | same, `api.deliveries` | `Handlers.listDeliveries` → `Store.listDeliveries`, with `Store.newestPerPair` and `Store.lateAfterMs` |
| Destinations | [Destinations.tsx](../../frontend/src/pages/Destinations.tsx): `Destinations`, `Row` | `Handlers.listDestinations` → `Destinations.list` (filter, rank) over `Destinations.all` (state, health, missed leads, last problem) |
| Leads | [Leads.tsx](../../frontend/src/pages/Leads.tsx): `Leads`, `LeadDrawer` | `Store.listLeads`, `Store.leadOrFail`; outcome from `Store.outcomeOf` over the newest delivery per destination |
| Delivery graph | [FlowPage.tsx](../../frontend/src/pages/FlowPage.tsx): `buildGraph`, `edgeLook` | `Store.firmDetail` plus `Store.listDeliveries` for the firm |
| Firm report ("Your week in calls") | [Report.tsx](../../frontend/src/pages/Report.tsx), with [Bars.tsx](../../frontend/src/components/Bars.tsx) | `Handlers.firmReport` → `Report.build` (leads by category and practice area, hand-off summary, `Destinations.all` for the firm) |
| Firm portal | [Portal.tsx](../../frontend/src/pages/Portal.tsx) | `Handlers.termsGate` → `Terms.gate`; `Handlers.statement` → `Billing.statement`; `Handlers.changePlan` → `Billing.changePlan` |
| Billing operations | [BillingOps.tsx](../../frontend/src/pages/BillingOps.tsx) | `Handlers.billingAccounts` → `Billing.accountRows`; `Billing.priceBook` |
| Terms admin | [Terms.tsx](../../frontend/src/pages/Terms.tsx) | `Terms.versions`, `Terms.coverage`, `Terms.publish`, `Terms.acceptances` |
| Vision | [Vision.tsx](../../frontend/src/pages/Vision.tsx) | `Store.deliverySummary` (with `expiredLeads`) and `Destinations.list` for the live tiles |
| Firm picker | [FirmPicker.tsx](../../frontend/src/components/FirmPicker.tsx) | `Store.listFirms` (ranked: live destinations first) and `Destinations.list` (suggestions) |
| Call categories | [Categories.tsx](../../frontend/src/pages/Categories.tsx) | `Categories.taxonomy`, `Categories.classify`, `Categories.referenceLeads` |
| Onboarding | [Onboarding.tsx](../../frontend/src/pages/Onboarding.tsx) | [Onboarding.fs](../../backend/src/Core/Onboarding.fs) |
| Page routing | [App.tsx](../../frontend/src/App.tsx), [lib/route.ts](../../frontend/src/lib/route.ts) (`key` changes on navigation only, so pages reload their filters from the URL) | |

## 6. Where the lab's data comes from

- **Firms and destinations:** the importers (`backend/src/Import/`: `Api2.fs`, `Chatbot.fs`, `Airi.fs`) → `Import.apply`. Secrets are reduced to presence and a fingerprint in `Import.credential`; import findings come from `Issues.compute`.
- **Call types → call categories:** `Categories.classify`, from `docs/call-categories/taxonomy.json` (see [call-categories](../call-categories/README.md)).
- **Synthetic leads:** `Seed.lead` and `Seed.run`, which go through `Dispatch.publish` like real leads.
- **A simulated week:** `Simulate.run`. It replaces delivery history without sending anything: its own `Publish` and `send` steps, but the same `routingExclusion`, `buildFor`, `FakeCrm.checkWith`, `Policy.classify` and `Dispatch.transition` as the worker. Its `scenarios` list is a catalogue of operator situations, each with a `Story`.

## Tests: the rules, executable

| File | Holds |
|---|---|
| [CoreTests.fs](../../backend/tests/Tests/CoreTests.fs) | Response classification, retry and `Retry-After`, signing, templates, address rules, fingerprints |
| [PayloadTests.fs](../../backend/tests/Tests/PayloadTests.fs) | Every catalog kind builds a body its receiver accepts |
| [CategoryTests.fs](../../backend/tests/Tests/CategoryTests.fs) | Reference leads map as expected; seeded leads are synthetic and routed |
| [DiagnoseTests.fs](../../backend/tests/Tests/DiagnoseTests.fs) | Which failure class each answer gets; a simulated week has every class |
| [DestinationsTests.fs](../../backend/tests/Tests/DestinationsTests.fs) | State, health, missed leads, Delivery Center totals |
| [SimulateTests.fs](../../backend/tests/Tests/SimulateTests.fs) | The week reaches every state and is repeatable |
| [BillingTests.fs](../../backend/tests/Tests/BillingTests.fs) | Cycles, free-by-rule, proration, locked closed cycles, legacy data problems, and every terms gate case |
| [HandOffTests.fs](../../backend/tests/Tests/HandOffTests.fs) | Windows to act, fallback once and only when late, expired leads, the firm report, the outage scenario with and without a fallback |
| [OnboardingTests.fs](../../backend/tests/Tests/OnboardingTests.fs) | Versions, approvals, draft revisions |
| [ApiTests.fs](../../backend/tests/Tests/ApiTests.fs) | Routes end to end against a running app; test control exists only when switched on, and sets one destination's receiver |

**The contract scenarios** ([docs/contracts/](../contracts/README.md)) run against
the lab with [tools/run_scenarios.py](../../tools/run_scenarios.py): a fresh API
per scenario, driven through the public routes and
[TestControl.fs](../../backend/src/Api/TestControl.fs) (the clock in
`Domain.now`, a worker pass on demand, and per-destination receiver answers in
`Receivers.overrides`). Test control is mounted only with `TEST_CONTROL=true`,
which `Config.load` refuses against the lab's own database.

## Reading order

1. [GLOSSARY.md](../../GLOSSARY.md) and the model in [README.md](README.md).
2. Flows 1 and 2 above; read `Dispatch.transition` closely.
3. Flow 3, then open one Delivery Center drawer of each class with a simulated week
   (`dotnet run --project src/Import -- simulate` in `backend/`).
4. Flow 5: Destinations, then the Delivery Center.
5. Flow 4, which builds on the rest.

With each step, read its test file from the table above.
