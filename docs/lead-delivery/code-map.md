# Lead delivery: code map

Where each part of the [model](README.md) and each [operator workflow](operations.md)
lives in the code, in the order it runs. Functions are named rather than given
line numbers, so search for the name in the linked file. Paths are from the repo
root. Words in **bold** are in [GLOSSARY.md](../../GLOSSARY.md).

For a first look, open [code-explorer.html](code-explorer.html) in a browser: the data model,
each flow from input to the rows it writes, and one example lead followed step by step through
four stories. It is a snapshot; this file is the reference. How the worker, intake and operators
stay safe when they act at once (leases, transaction boundaries, every race) is in
[concurrency.md](concurrency.md), with a step-by-step [concurrency-explorer.html](concurrency-explorer.html).

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

1. `Handlers.sendLead` → `Dispatch.intake` → `Dispatch.publish`'s recording. Everything below is one transaction (`Db.write`), so a lead is never recorded without its deliveries. With a `conversationId`, `Dispatch.intake` first looks for the lead already recorded for it (`Store.eventForConversation`; `events_conversation` is unique per firm): a repeat gets that lead back, `duplicate: true`, with nothing planned again. A write that fails (`Db.Unavailable`, or any database error) is answered 503 "not recorded" by `Handlers.sendLead`: the transaction never committed, so the channel's copy is the only one, and it sends it again.
2. `Store.insertEvent` records the lead.
3. `Store.connectionsOf` lists the firm's destinations; live ones (enabled, active, not in setup) paused ones (`Dispatch.isPaused`: `enabled=false` with the `pausedAt` setting) and ones that need reconnection (`status=needs-reconnection`) are taken (a test lead goes only to the destination under test).
4. `Routing.decide` ([Routing.fs](../../backend/src/Core/Routing.fs)) decides each production lead, one destination at a time: a chat lead to a destination that refuses chats, or a call outside the destination's rule (`routeCategories`, `routePracticeAreas` settings; the legacy active-call-type rule for a CRM without one), is **not sent** for routing. A lead whose body would lack a field the kind requires, but only because the lead is missing it (`Routing.leadProblem`), is **not sent** as a lead problem (reason `lead problem: …`). Otherwise `pending`; for a paused destination or one that needs reconnection, held (`pending` with no next attempt; `Dispatch.holdReason`). Test leads are not routed and never held.
5. `Store.insertDelivery` writes each delivery (built by `Dispatch.newDelivery`) with its `RoutingDecision` (`decision` column). Only a routing exclusion counts as "the firm's routing sends it nowhere" (`Store.readLead`, `Dispatch.runFallbacks`); a lead problem is still owed a hand-off.
6. On import, `Import.apply` seeds a CRM's rule with `Routing.seed` from the firm's active call types, unless the destination already has one.

**A lead in a contract** (`POST /api/ingest/intake-lead/draft-2`, the call center's lead; [intake-lead/](../contracts/intake-lead/README.md)): `Handlers.ingestDraft2` → `Dispatch.ingest`. `LeadContract.parse` checks the payload against the embedded copy of intake's schema, hashes its canonical form and maps it (`LeadContract.toLead`); then one `Db.write` resolves `firm.id` through `Store.tryFirmAlias`, finds a repeat by firm and interaction (`Store.contractByInteraction`: same hash, a duplicate; another hash, 409) or by lead id (`Store.contractByLeadId`), and runs the same recording as step 1 to 5 plus `Store.insertLeadContract`, so the lead, its deliveries and the original payload commit together. The category the sender set is used as given everywhere a lead is classified (`Categories.ofLead`, never the call type's name for such a lead).

## 2. A delivery is sent, retried and settles

The concurrency model behind these steps (one writer, no network call inside a transaction, a versioned lease) is in [concurrency.md](concurrency.md).

1. `DeliveryWorker` ([Worker.fs](../../backend/src/Api/Worker.fs)) runs `Dispatch.work`: up to `Dispatch.maxInFlight` sends at once, a slot claimed again as each finishes (`Dispatch.startDue`); the sweeps every 5 s, the lookups as a task of their own. Test control uses `Dispatch.runDue` (claim, then wait for all).
2. `Store.claimDue` leases what is due, nearest hand-off deadline first, at most `Store.inFlightPerDestination` leases per destination and a quarter of the claim per firm while others are due (one SQL query over everything due): `delivering` with `lease_owner`, `lease_until` (`Dispatch.leaseMs`) and a `lease_version` every claim increases (`Store.tryClaim`, one conditional write). A lease that ran out is claimed again; nothing is requeued on a restart.
3. A recovered lease (`Dispatch.recover`): no attempt started → carry on; one started with no answer → that attempt is **unknown** (`Store.markAttemptUnknown`), then sent again only if the destination takes a second copy harmlessly (`Dispatch.duplicatesOf`, `retryAfterUnknown`: the `duplicates` setting, else `Catalog.harmlessDuplicates`), otherwise `unknown`, waiting for an operator, who resolves it (`Dispatch.resolve`: found or closed; or a resend, which `Dispatch.replay` records as `resent`). A resolution stands: `Dispatch.complete` settles a delivery with a late accepted answer only while it is unsettled (waiting, sending or unknown) and unresolved, else emits `LateAcceptance`. Every ordering of these steps is checked by `InterleavingTests` ([concurrency.md §9](concurrency.md#9-the-model-checked-exhaustively)).
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
| Resend | `ProblemPanel`; bulk in the Delivery Center; `ResendAccepted` for an accepted one | `Handlers.replay` → `Dispatch.replay`: a new delivery for the same lead, `replayOf` and `resend` (who, role, why) set; `Dispatch.resendAllowed` refuses an accepted lead (403) unless a senior operator gives the reason |
| Pause, resume, reactivate | `ProblemPanel`; Destinations `Row` | `Handlers.patchConnection` records or clears `pausedAt`. Leads recorded while paused are held when planned (`Dispatch.publish`); a delivery already waiting is held at send time (`Dispatch.attempt`). Resuming or reactivating releases both (`Store.releaseHeld`). Held deliveries are `stage: held`, not retrying or late (`Store.sendingOrRetrying`). Test leads still go |
| Send a test lead | same | `Handlers.testConnection` → `Dispatch.testConnection` (a test lead to this destination only) |
| Send missed leads | Destinations `Row` | `Handlers.sendMissed` → `Dispatch.sendMissed`, over `Store.missedEvents`; routing applies |
| Edit mapping | `MappingFix` in [Fix.tsx](../../frontend/src/components/Fix.tsx), reusing `MappingEditor` from [Onboarding.tsx](../../frontend/src/pages/Onboarding.tsx) | `Handlers.patchSetup` → `Onboarding.update` (a draft version; live unchanged), then `send` → `decide` (the firm, through its secure link) → `test` → `confirm` → `goLive`. State: `Onboarding.stateOf` |
| See what we send | `PreviewFix` in [Fix.tsx](../../frontend/src/components/Fix.tsx) | `Handlers.preview` (the body plus what `FakeCrm.checkWith` would refuse) |

Only a webhook with a setup takes our mapping as its body (`Onboarding.enforced`);
every other kind's body is fixed in `Payloads.build`.

### Firm access

The firm portal's API ([Portal.fs](../../backend/src/Api/Portal.fs)) is separate from the operator console. `Access.issue` gives a firm user a token, kept as `Secrets.tokenHash`; `Access.signedIn` reads the bearer token; `Access.leadFor` answers only for the token's firm, and otherwise records the refusal (`access_audit`, `AccessRefused`) and answers 404, the same as for an unknown lead. A list naming another firm is refused 403. The operator console's routes are unchanged: the lab has no operator sign-in.

## 4b. Window to act and fallback

1. `Urgency.windowMs` gives each call category its window to act (72 hours; 24 for emergencies and custody) and `Urgency.fallbackAfterMs` its fallback delay (1 hour; 15 minutes): two separate clocks, read by `Timing` from [delivery-timing.json](delivery-timing.json), which also holds the retry schedule `Policy.retryScheduleMs` reads. Set by Answering AI; `DELIVERY_TIMING_FILE` points a deployment at its own copy. `Urgency.handOffState` says on time, expired or waiting.
1a. A lead every destination excluded by routing gets no `HandOff` (`Store.readLead`), so it is never expired and not in the in-time numbers; `Dispatch.runFallbacks` and the simulator's `HandOffCheck` never send it to the fallback.
1b. The screens never say "Expired" alone: an expired lead that reached the firm is *Reached late* (amber), one that did not is *Not reached in time* (red) (`handOffText` in `frontend/src/lib/delivery.ts`, the Leads badge).
2. Every lead read (`Store.listLeads`, `Store.leadOrFail`) carries a `HandOff`: its first accepted delivery anywhere, by which destination, and whether that was the fallback. An expired hand-off makes the lead's outcome `expired` (`Store.outcomeOf`).
3. A destination with the setting `role: fallback` is skipped by `Dispatch.publish`. `Dispatch.runFallbacks`, called every few seconds by `DeliveryWorker`, gives it any lead with no accepted delivery once `Urgency.fallbackAfterMs` has passed, or at once when every delivery that could reach the firm is held, once per lead. The simulator's `Publish` queues its `HandOffCheck` at once in the same case.
4. The simulator does the same with its `HandOffCheck` action.
5. **Acknowledgement and escalation** (RFC-008 §3.2). `Store.insertEvent` fixes at intake whether the hand-off needs acknowledgement (`Urgency.needsAcknowledgement`: emergencies and custody), when its window closes (`handoff_due_at`), and the lead's link token (`ack_token`, never in the operator API). `Dispatch.beginAttempt` adds the link to an email or SMS (`Payloads.withAcknowledgement`, `Dispatch.ackUrl`). `GET /ack/{lead}` shows a button and records nothing; its `POST` calls `Store.acknowledge` (`HandOffAcknowledged`). `Dispatch.runHandOffs`, run with the fallbacks, escalates a hand-off with no acknowledgement after its fallback delay (`HandOffEscalated`) and marks one with nothing accepted in its window expired (`HandOffExpired`), each once. `HandOff.Evidence` is `Urgency.evidence`. The simulator writes the same facts with `Store.setHandOff` (a best guess: 85% of urgent leads that reached an email or SMS are confirmed within 10 minutes).
6. **Verification by lookup** (RFC-008 §3.1). It also settles an unknown outcome at such a destination: found → `delivered`, verified, resolution `found` by the lookup (`afterUnknown: lookup` in the kind's delivery contract, checked by `ContractTests`). A destination whose settings say `duplicates: lookup` can be looked up by our lead reference. `Dispatch.runVerifications`, run with the fallbacks, asks each accepted delivery's lookup (`Dispatch.lookupUrl`; the fake CRMs answer at `/receiver/crm/{kind}/lookup`, from `receiver_records`, written by `Receivers.handle` with `Store.keepRecord`) every `lookupEveryMs`: found → `verified_at`, `DeliveryVerified`; not found after `Timing.verifyWithinMs` → `verify_overdue_at`, `VerificationOverdue`, once. `Store.withAttempts` sets `Delivery.Evidence`. A real CRM's lookup is per kind, once its sandbox confirms one.
7. **Routing changes** (RFC-008 §5.2, RFC-009). `Dispatch.proposeRouting` keeps a draft rule on the destination (`routingDraft`, `RevisionDrafted`); `Dispatch.approveRouting` makes it live as the next `revision` (`Routing.revisionOf`, `RevisionWentLive`), and with `applyToWaiting` decides waiting deliveries again (`Store.setDecision`), cancelling those the new rule excludes. Every `RoutingDecision` records its `Revision`.

## 4b2. Time zones

Every timestamp is UTC milliseconds; delivery rules are elapsed durations and need no zone. [Zones.fs](../../backend/src/Core/Zones.fs) converts for calendars, by IANA name: `Billing.cycleOf` on the company's zone (`Zones.company`, daylight saving included), `Report.afterHours` on the firm's (`FirmRow.TimeZone`, set with `PATCH /api/firms/{id}`). The frontend's `format.ts` helpers take an optional zone: the firm report and portal pass the firm's or the statement's. The simulator keeps fixed Eastern standard time: it is demo data.

## 4c. Billing and terms of service

1. **Price book and accounts:** `Billing.seedPriceBook` and `Billing.seedAccounts` (called by the simulator) fill `price_book`, `billing_accounts` and `subscriptions` ([Db.fs](../../backend/src/Core/Db.fs) schema).
2. **Conversations:** `Billing.conversationsWith` (with `Store.corrected`: a lead's current category where it was corrected) turns a cycle's production leads into conversations, each billable or free with the rule (`Billing.rules`) that made it free; talk time is synthetic (`Billing.seconds`).
2a. **Corrections and the usage ledger** (RFC-008 §6.2): `Store.insertEvent` writes the intake classification record (`classifications`, seq 1). [Corrections.fs](../../backend/src/Core/Corrections.fs): `Corrections.correct` adds a record that supersedes it (`ClassificationCorrected`); `Corrections.view` lists them and the destinations now matched (`Routing.decideAs` under the corrected mapping), offered, never sent. `Billing.closeCycle`, run when `Billing.statement` locks a cycle, keeps each conversation as rated (`usage`, `UsageRated`); a correction after that which lowers the charge writes a `credits` row (`UsageCredited`). `Corrections.billing` says billable, free or credited.
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

- **Firms and destinations:** the legacy importers ([backend/src/Import/](../../backend/src/Import/)) read the legacy systems' data → `Import.apply`. Today the lab imports legacy data; the lead will come from the call center ([Proposed](../../README.md#where-this-sits)). Secrets are reduced to presence and a fingerprint in `Import.credential`; import findings come from `Issues.compute`.
- **Draft 3** (`POST /api/ingest/intake-lead/draft-3`): `Handlers.ingestContract` reads the publication headers, then `Dispatch.ingestAs` with the draft-3 schema. A repeat with the same content calls `Store.notePublication` (metadata only); a new lead also gets `Store.setWindowFrom` (the window from `conversation.endedAt`). `Store.clockStart` reads back where a lead's clock started, for `runFallbacks` and `runHandOffs`.
- **Firm identity:** `PUT /api/provisioning/firms/{canonicalId}` → `Handlers.provisionFirm` → `Provisioning.provision`, one write: creates the firm or adopts an existing one, sets its canonical id (unique, `firms_canonical`), name, time zone and billing account, and emits `FirmProvisioned`. `Store.resolveSenderFirm` resolves a contract lead's `firm.id`: canonical id first, then `firm_aliases`.
- **Leads from the call center:** `tools/publish_leads.py` posts the leads its simulator wrote (`out/leads/`) to the contract route above; until firm ids are shared, a sender's firm id maps to a firm here through `firm_aliases`.
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
| [RoutingTests.fs](../../backend/tests/Tests/RoutingTests.fs) | Each destination's rule decides alone; routing changes go live only when approved, and waiting deliveries keep their revision |
| [LeaseTests.fs](../../backend/tests/Tests/LeaseTests.fs) | One worker wins a claim; deadline order; what follows an unknown outcome; resend refused while in flight; resolving an unknown outcome |
| [IntakeTests.fs](../../backend/tests/Tests/IntakeTests.fs) | One lead per conversation; a lead the database could not take is not recorded at all |
| [ConcurrencyTests.fs](../../backend/tests/Tests/ConcurrencyTests.fs) | Eight writers at once, each with its own handle: intake, contract ingest, provisioning, resend and claims each happen once; a pause waits for an attempt's check. On SQLite, and on PostgreSQL with `DELIVERY_TEST_PG` (RFC-011) |
| [Draft3Tests.fs](../../backend/tests/Tests/Draft3Tests.fs) | Milestone 3, delivery's side: a draft-3 lead recorded with its publication; a retry with new metadata is a duplicate, never a conflict; the window and fallback clock from completion; a late lead expires; refusals; a receiver that is down |
| [ProvisioningTests.fs](../../backend/tests/Tests/ProvisioningTests.fs) | Milestone 2: a canonical id creates or adopts a firm once; provisioning owns the name, zone and account; one account holds several firms; refusals; a lead names its firm by canonical id, and an alias never shadows one |
| [IngestTests.fs](../../backend/tests/Tests/IngestTests.fs) | Milestone 1: a contract lead recorded with its payload; repeats, conflicts and refusals; lead, deliveries and payload commit together; the sender's category routes; retried while a receiver is down; delivered after a restart; end to end to the fake receiver |
| [CorrectionTests.fs](../../backend/tests/Tests/CorrectionTests.fs) | A correction supersedes; billing re-rated while open, credited after close, never charged again |
| [ContractTests.fs](../../backend/tests/Tests/ContractTests.fs) | Each kind's delivery contract against the code, both ways |
| [WorkerTests.fs](../../backend/tests/Tests/WorkerTests.fs) | A slow destination delays only its own deliveries; a destination and a firm get only their share; writers with separate locks share one file without a lost or doubled send |
| [InterleavingTests.fs](../../backend/tests/Tests/InterleavingTests.fs) | Every interleaving of two workers, an operator and a receiver, to depth 8, against the real code, with the invariants checked after each step ([concurrency.md §9](concurrency.md#9-the-model-checked-exhaustively)) |
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
