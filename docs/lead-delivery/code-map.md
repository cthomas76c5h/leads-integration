# Lead delivery: code map

Where each part of the [model](README.md) and each [operator workflow](operations.md)
lives in the code, in the order it runs. Functions are named rather than given
line numbers, so search for the name in the linked file. Paths are from the repo
root. Words in **bold** are in [GLOSSARY.md](../../GLOSSARY.md).

For a first look, open [code-explorer.html](code-explorer.html) in a browser: the data model,
each flow from input to the rows it writes, and one example lead followed step by step through
four stories. It is a snapshot; this file is the reference.

When you change a flow below, update this file in the same change, and the explorer if the
flow's steps changed.

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
3. `Store.connectionsOf` lists the firm's destinations; live ones (enabled, active, not in setup) paused ones (`Dispatch.isPaused`: `enabled=false` with the `pausedAt` setting) and ones that need reconnection (`status=needs-reconnection`) are taken (a test lead goes only to the destination under test).
4. `Routing.decide` ([Routing.fs](../../backend/src/Core/Routing.fs)) decides each production lead, one destination at a time: a chat lead to a destination that refuses chats, or a call outside the destination's rule (`routeCategories`, `routePracticeAreas` settings; the legacy active-call-type rule for a CRM without one), is **not sent** for routing. A lead whose body would lack a field the kind requires, but only because the lead is missing it (`Routing.leadProblem`), is **not sent** as a lead problem (reason `lead problem: …`). Otherwise `pending`; for a paused destination or one that needs reconnection, held (`pending` with no next attempt; `Dispatch.holdReason`). Test leads are not routed and never held.
5. `Store.insertDelivery` writes each delivery (built by `Dispatch.newDelivery`) with its `RoutingDecision` (`decision` column). Only a routing exclusion counts as "the firm's routing sends it nowhere" (`Store.readLead`, `Dispatch.runFallbacks`); a lead problem is still owed a hand-off.
6. On import, `Import.apply` seeds a CRM's rule with `Routing.seed` from the firm's active call types, unless the destination already has one.

## 2. A delivery is sent, retried and settles

1. `DeliveryWorker` ([Worker.fs](../../backend/src/Api/Worker.fs)) loops on `Dispatch.runDue`.
2. `Store.claimDue` leases what is due, nearest hand-off deadline first: `delivering` with `lease_owner`, `lease_until` (`Dispatch.leaseMs`) and a `lease_version` every claim increases (`Store.tryClaim`, one conditional write). A lease that ran out is claimed again; nothing is requeued on a restart.
3. A recovered lease (`Dispatch.recover`): no attempt started → carry on; one started with no answer → that attempt is **unknown** (`Store.markAttemptUnknown`), then sent again only if the destination takes a second copy harmlessly (`Dispatch.duplicatesOf`, `retryAfterUnknown`: the `duplicates` setting, else `Catalog.harmlessDuplicates`), otherwise `unknown`, waiting for an operator.
4. `Dispatch.beginAttempt`, in one transaction: the lease version still holds, and the destination is still eligible, then the attempt is recorded as `started` (`Store.startAttempt`) before the request is sent. A pause committed before this holds the delivery; one committed after cannot recall it:
   - destination not active → `cancelled`;
   - `Dispatch.buildFor` → `Payloads.build` (a webhook with a setup renders its template with `Template.render`); a body that cannot be built → `failed "body: …"` (**our fault**, no attempt);
   - POST to the destination's URL. In the lab that is `Receivers.handle`, which checks the body with `FakeCrm.checkWith` (the kind's required fields plus any `?require=`), then answers as its mode says (ok, reject, flaky, gone, slow, down).
5. `Dispatch.complete` records the answer only if the lease version still holds; a worker that lost its lease keeps the answer as evidence on its own attempt (and an accepted late answer settles an `unknown` delivery). Every step emits its domain event (`Store.emit`: `AttemptMade`, `DeliveryAccepted`, `Held`, `LeaseRecovered`, …).
6. `Policy.classify` turns the answer into `Delivered`, `Retry`, `Failed` or `NeedsReconnection`; `Dispatch.outcomeOf` makes a timeout `Unknown` where a second copy is not safe (the same `retryAfterUnknown` as a recovered lease).
7. `Dispatch.transition` is the whole state machine, shared by the worker and the simulator:
   - `Retry` → `pending` again, next time from `Policy.nextDelayMs` (`Policy.retryScheduleMs`, never sooner than `Retry-After`);
   - out of attempts, or refused → `failed`; a production failure adds to the **failure streak**, and at `Policy.disableAfterConsecutiveFailures` the destination is **switched off**;
   - `Disable` (410) → the destination stops and `Store.cancelPendingFor` cancels its other pending deliveries;
   - `Delivered` resets the streak.
6. Written by `Store.insertAttempt`, `Store.setDeliveryState`, `Store.updateConnection`.

## 3. How a delivery is read: stage and diagnosis

Every delivery read from the database passes through `Store.withAttempts`, which adds:

- **Stage** (`Store.stageOf`): queued, held, accepted, refused, not sent or unknown: what we can claim.
- **Diagnosis** (`Diagnose.diagnose`): the failure class and the cause in the receiver's words (`Diagnose.receiverWords`). The class is checked in this order: `lead` (not sent, `lead problem:`), `routing` (not sent), `connection` (held: needs reconnection), none (held: paused), `transient` while retrying, `stopped` (cancelled), `ours` (`body:`), `configuration` (address refused, 3xx), `connection` (401, 403, 410), `lead` (a 4xx about a name or phone the lead does not have), `configuration` (any other 4xx), `transient` (gave up).

The frontend only renders it:

- [lib/delivery.ts](../../frontend/src/lib/delivery.ts): `CLASS` (labels and tones), `problem`, `nextAction`, `needsAttention`, `journey`.
- [components/Deliveries.tsx](../../frontend/src/components/Deliveries.tsx): `StatusBadge`, `Journey`, `ProblemPanel` (the buttons each class gets), `DeliveryList`.

## 4. Operator actions

| Action | UI | Backend |
|---|---|---|
| Resend | `ProblemPanel`; bulk in the Delivery Center | `Handlers.replay` → `Dispatch.replay`: a new delivery for the same lead, `replayOf` set |
| Pause, resume, reactivate | `ProblemPanel`; Destinations `Row` | `Handlers.patchConnection` records or clears `pausedAt`. Leads recorded while paused are held when planned (`Dispatch.publish`); a delivery already waiting is held at send time (`Dispatch.attempt`). Resuming or reactivating releases both (`Store.releaseHeld`). Held deliveries are `stage: held`, not retrying or late (`Store.sendingOrRetrying`). Test leads still go |
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
3. A destination with the setting `role: fallback` is skipped by `Dispatch.publish`. `Dispatch.runFallbacks`, called every few seconds by `DeliveryWorker`, gives it any lead with no accepted delivery once `Urgency.fallbackAfterMs` has passed, or at once when every delivery that could reach the firm is held, once per lead. The simulator's `Publish` queues its `HandOffCheck` at once in the same case.
4. The simulator does the same with its `HandOffCheck` action.

## 4b2. Time zones

Every timestamp is UTC milliseconds; delivery rules are elapsed durations and need no zone. [Zones.fs](../../backend/src/Core/Zones.fs) converts for calendars, by IANA name: `Billing.cycleOf` on the company's zone (`Zones.company`, daylight saving included), `Report.afterHours` on the firm's (`FirmRow.TimeZone`, set with `PATCH /api/firms/{id}`). The frontend's `format.ts` helpers take an optional zone: the firm report and portal pass the firm's or the statement's. The simulator keeps fixed Eastern standard time: it is demo data.

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
- **A simulated week:** `Simulate.run`. It replaces delivery history without sending anything: its own `Publish` and `send` steps, but the same `Routing.decide`, `buildFor`, `FakeCrm.checkWith`, `Policy.classify` and `Dispatch.transition` as the worker. Its `scenarios` list is a catalogue of operator situations, each with a `Story`.

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
