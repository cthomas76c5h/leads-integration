# RFC-008: Delivery Guarantees, Concurrency and End-to-End Evidence

**Answering AI — Product and Technical Proposal**

**Date:** November 19, 2026
**Status:** Proposed — Awaiting stakeholder approval
**Decision owner:** Engineering / Product Leadership
**Reviewers:** Engineering, Operations, Product, Finance, Security, Customer Success
**Related:** RFC-001 (the hand-off promise) · RFC-002 (classification) · RFC-003 (usage and credits) · RFC-005 (integrations, approval) · RFC-007 (the model and build order, revised by this RFC)
**Detail:** [domain model](../lead-delivery/README.md) · [contracts](../contracts/README.md) · [answers](../lead-delivery/answers.md)

---

## 1. Executive Summary

An architecture review of RFC-001 to RFC-007 approved the direction and found
that several business promises are stronger than the mechanisms behind them can
prove. Six issues, three of them critical:

| Priority | Issue | Risk |
|---|---|---|
| P0 | A destination **accepting** a lead is treated as a person at the firm **having** it | The one number measures transport success, not the promise firms buy |
| P0 | "Safe to receive twice" is promised on the destination's side, which we do not control | A retry after a lost answer can create a second record in a CRM |
| P0 | Destination state is changed by two contexts | Races: a paused destination still sent to; stale routing or credentials used |
| P1 | Classification drives routing, urgency, billing and reports with no rule for uncertainty or correction | One wrong label can misroute an emergency and mis-bill a call |
| P1 | Security and production readiness come at the end of the build | Isolation and safety cannot be added after the product is built around their absence |
| P1 | Real phone and chat intake is proven late | The platform's main producer of leads is untested until slice 9 |

**We propose explicit evidence levels for every delivery and hand-off, honest
guarantees at each boundary with a rule for answers that never arrive, one owner
for each part of a destination's state, a decision policy for uncertain and
corrected classifications, a correction rule for append-only records, security
gates from the first slice, and ten failure-injection scenarios that must pass
before production.** RFC-007's build order changes accordingly (section 11).

### Recommended decision

Approve:

1. **Evidence levels** (section 3), and the one number reported at two of them: accepted within the window, and confirmed within the window.
2. **The guarantee at each boundary** (section 4), including the rule for an **unknown outcome** and each destination kind's duplicate handling.
3. **Desired state and observed health** as separate owners, combined by one eligibility rule; leases for claims; revalidation before sending (section 5).
4. **The classification decision policy** and the effects of a correction (section 6).
5. **Supersession** as the correction rule for append-only records (section 7).
6. **Which repairs need fresh approval** (section 8).
7. **Security gates in slice 0 and in every slice's acceptance** (section 9).
8. **The ten failure-injection scenarios** as production gates (section 10), and the delivery scorecard (section 12).
9. **The revised build order** in RFC-007 (section 11).

---

## 2. Problem Statement

RFC-007 states guarantees ([§6.1](007_domain_model_and_build_order.md#61-the-guarantees))
and aggregates ([§5](007_domain_model_and_build_order.md#5-aggregates-and-invariants)),
and the lab exercises most of them in a simulated week. Read closely against the
lab's code and each other, four gaps remain:

| Gap | Where | Today in the lab |
|---|---|---|
| **Hand-off proven by acceptance.** A hand-off is "reached" when a delivery is accepted ([README §5](../lead-delivery/README.md#window-to-act-and-fallback)) | RFC-001, RFC-007 §5.1 | An SMS gateway's 2xx counts as the firm having the lead |
| **External duplicates promised away.** "Receiving the same lead twice has no further effect, on our side or the destination's" | RFC-007 §6.1 | The lead id is sent as a header; whether a CRM uses it is unknown. RFC-001 is the honest one: "no promise of exactly-once CRM creation" |
| **Two owners of destination state.** Firm configuration owns the destination "including state and failure streak"; Delivery owns "destination health" and sends `DestinationStateChanged` back | RFC-007 §4, §5.2, §5.3 | One `connections` row holds both; the worker reads its state, sends, then writes the outcome against whatever the row says by then |
| **Claims without leases.** A claim flips `pending` to `delivering`; on restart, every `delivering` row goes back to `pending` and is sent again | `Store.claimDue`, `Store.requeueInterrupted` | A crash after the CRM created a record, before we recorded it, sends the lead again |
| **A pause does not stop queued deliveries.** Planning skips a paused destination, but the worker checks only whether it is switched off before sending | `Dispatch.attempt` | A delivery queued before an operator pauses is still sent after the pause (found while writing the [pause-during-send](../contracts/scenarios/pause-during-send.json) scenario) |

And three cross-RFC questions had no answer: what a corrected category does to a
bill already rated; how repeat calls are detected without keeping caller
numbers in billing; and whether reconnecting credentials, rolling back or
resuming needs the firm's approval again.

---

## 3. Evidence Levels and the Hand-off Promise

### 3.1 Five levels

Every delivery and every hand-off records the **strongest evidence** it has:

| Level | Meaning | Typical source |
|---|---|---|
| **Submitted** | We sent the request; no answer yet, or an unknown one | Our attempt record |
| **Accepted** | The destination acknowledged the request | A 2xx from the receiver |
| **Verified** | We confirmed the record exists at the destination | A lookup through the destination's API; the firm confirming |
| **Acknowledged** | A person at the firm, or a workflow the firm authorised, confirmed receipt | A reply to the SMS, a click on the signed link in the SMS or email, the lead opened in the firm portal by a firm user |
| **Acted on** | A measurable response followed | A status change in the CRM, a call back logged |

**Acted on** is recorded where available, for Insight; it is never part of the
delivery promise. Not every destination can reach every level:

| Destination | Strongest level it can give | How |
|---|---|---|
| SMS | Acknowledged | Reply, or a signed acknowledgement link in the text |
| Email | Acknowledged | A signed acknowledgement link; opening the email does not count (unreliable) |
| Firm portal | Acknowledged | A firm user opens the lead |
| CRM | Verified, where its API allows a lookup; otherwise Accepted | Lookup by our lead reference ([§4.3](#43-duplicate-handling-per-destination-kind)) |
| Webhook, Zapier | Accepted | The receiver's 2xx |
| Slack | Accepted | The message posted |

### 3.2 The hand-off, revised

The **hand-off** aggregate (RFC-007 §5.1) keeps its states (Pending, Reached,
Reached by fallback, Expired) and adds the **evidence level** at which it was
reached, and when each level was first reached.

- **Expired** keeps its meaning: no hand-off **accepted** within the window. Acceptance stays the floor for every destination.
- **The one number is reported at two levels**, side by side and never merged:
  - **Accepted within the window:** measurable for every lead.
  - **Confirmed within the window:** acknowledged within the window, for leads whose hand-off destination can confirm. A CRM **verification** counts as confirmation only where the firm has declared, in its approved setup, that its intake team works from that CRM as its queue (**CRM is our intake queue**). That declaration is a firm-level policy, recorded with the revision that carries it and shown in the report; without it, a verified record is reported as verified, never as confirmed, because a record existing does not mean anyone saw it.
- **Emergencies and custody** need acknowledgement, not only acceptance. If no acknowledgement arrives within half the window, the hand-off escalates: the fallback, then an operator who calls the firm. Every step is shown to the firm. *Proposed.*
- A firm may choose, in its approved setup, to require acknowledgement for new matters too.
- Both firm-level choices (acknowledgement for new matters; CRM as intake queue) are part of the approved revision, so changing either goes through the firm's approval and is in the audit trail.

### 3.3 What changes for firms

The weekly report shows both numbers, and the gap between them is a conversation
worth having: "your leads reached your intake line in time; 30% were confirmed
within the hour". Firms that want stronger proof turn on acknowledgement links.

---

## 4. Guarantees at Each Boundary

### 4.1 The boundaries

| Boundary | Guarantee | Mechanism |
|---|---|---|
| Channel → conversation | **Idempotent:** a repeated completion event never creates a second conversation | The channel's own conversation id as a unique key |
| Conversation → lead | **Idempotent:** one lead per conversation that produces one | Lead id derived from the conversation id |
| Lead → planned deliveries | **Atomic:** the lead and one planned delivery per destination, in one transaction ([RFC-007 §6.1](007_domain_model_and_build_order.md#61-the-guarantees)) | One database transaction |
| Planned delivery → worker | **At least once**, with **at most one active owner** at a time | A lease (section 5.3) |
| Worker → destination | **At least once per delivery**, subject to the retry policy | Attempts recorded before and after each send |
| Record at the destination | **Best effort against duplicates**, as strong as the destination allows | Section 4.3 |

**Exactly once is never promised** beyond our own database. The scorecard counts
duplicate incidents instead (section 12).

If the database is unavailable when intake completes, the channel is told the
lead was **not** recorded, and keeps its own copy to resend. Intake never
reports a capture it did not commit. *Proposed; to be agreed with each channel.*

### 4.2 An unknown outcome

An attempt ends in one of four ways:

| Outcome | When | What follows |
|---|---|---|
| **Answered** | The destination answered | Classified as today: accepted, transient, refused, connection (RFC-007, README §5) |
| **Not sent** | Failed before any byte reached the destination: DNS, refused connection, address check | Transient: retry. Nothing can have been created |
| **Unknown** | The request was sent, but no answer came: timeout while waiting, connection reset, worker lost its lease mid-send | Section 4.3 decides: retry, look up, or ask an operator |
| **Our fault** | The body could not be built | Refused, without an attempt |

Today the lab treats every "no answer" as transient and retries, and a restart
resends whatever was in flight. Both become **unknown** under this rule.

### 4.3 Duplicate handling per destination kind

Each destination kind declares how it handles a lead it may have seen, in its
[receiver contract](../contracts/README.md) (a new `duplicates` field):

| Capability | Meaning | After an unknown outcome |
|---|---|---|
| **Idempotency key** | The destination ignores a request with a key it has seen | Retry |
| **Lookup** | We can ask whether a record with our lead reference exists | Look up; send only if absent; the lookup also gives **Verified** |
| **External reference** | The destination stores our lead reference but does not deduplicate | Retry for a hand-off destination; for a record-only destination, hold for an operator, who checks and resends or closes |
| **None** | No way to tell | As external reference |
| **Duplicates harmless** | A second copy costs nothing (SMS, email, Slack, the portal) | Retry: a duplicate notice is better than a missed one |
| **Unknown** | Not yet verified in the destination's sandbox | As external reference, until verified |

Every destination kind is **unknown** until its sandbox shows otherwise; a
contract never claims a capability without evidence. Today only SMS, email and
Slack are declared (harmless); every CRM, the answering-legal web-leads API,
webhooks and Zapier are unknown, with the question listed in their receiver
contract. Webhooks and Zapier receive the lead id in a signed header; whether a
receiver honours it is that receiver's choice, so it is not counted on.

---

## 5. Destination State: Ownership and Concurrency

### 5.1 Two owners, one rule

| Part | Owner | Values | Changed by |
|---|---|---|---|
| **Desired state** | Firm configuration | Setting up, live, paused, removed | Setup, go-live, an operator's pause or resume, removal |
| **Approved definition** | Firm configuration | The live revision (routing rule, mapping, role) | Approval and activation (RFC-001 workflow) |
| **Authorization** | Firm configuration | A connection with an **authorization version** (presence and fingerprint only) | The firm connecting or reconnecting |
| **Observed health** | Delivery | Healthy, failing (with its streak), refused authorization (for an authorization version), switched off | Attempt outcomes; an operator's reactivation resets it |

**Eligibility to send** is one rule, evaluated by Delivery:

> desired state is live, **and** the revision the delivery uses is still active or explicitly carried forward, **and** observed health is not switched off, **and** authorization was not refused **for the current authorization version**.

**Needs reconnection** is then a derived state shown to people, not stored
twice: authorization refused for the current version. When the firm reconnects,
the version changes and the refusal no longer applies; nobody has to clear it.

Each context writes only its own part. The `DestinationStateChanged` event of
RFC-007 is replaced by `DesiredStateChanged` (Firm configuration) and
`DestinationHealthChanged` (Delivery), each with a sequence number per
destination; consumers ignore anything older than what they hold.

### 5.2 Which revision a delivery uses

- **Routing is decided once,** when the lead is recorded, against the revision live then, and recorded on the delivery.
- **The body is built from that same revision** when sent.
- When a new revision goes live, deliveries still waiting (queued, retrying, held) **keep their revision**, unless the person activating it chooses **"apply to waiting deliveries"**. That choice is part of the approval record. Deliveries whose routing no longer matches under the new revision are then cancelled with a reason; newly matching leads are not re-planned automatically (section 6.3).
- A revision withdrawn because it is broken (for example a mapping the receiver refuses) is never used again; its waiting deliveries move to the replacement revision once the firm approves it.

### 5.3 Claims and revalidation

- **A lease, not a flag.** A worker claims a delivery with its identity and an expiry (`claimed_by`, `claimed_until`). Only the holder may record its attempt. An expired lease is recovered by any worker, and the attempt it covered counts as **unknown** (section 4.2).
- **Revalidate before sending.** In the transaction that claims the delivery, and again immediately before the request is sent, the worker checks eligibility (section 5.1) and stamps the attempt with the **revision** and **authorization version** it uses. If either check fails, nothing is sent.
- **A pause takes effect for attempts not yet started.** An attempt already on the wire when an operator pauses is recorded as it ends. The audit shows both, in order.
- **Recording is truthful.** An attempt's outcome is recorded against the revision and authorization version it used, never the ones current when the answer arrives.

### 5.4 Events these rules add

In addition to those of [RFC-007 §5.3](007_domain_model_and_build_order.md#53-events-between-contexts)
and the [domain model](../lead-delivery/README.md#7-domain-events). The full list a
scenario may assert, with where each comes from, is
[events.json](../contracts/events.json).

| Event | Raised by | When |
|---|---|---|
| `DeliveryUnknown` | Delivery | An attempt was sent and never answered (section 4.2) |
| `DeliveryVerified` | Delivery | A lookup found the record at the destination |
| `VerificationOverdue` | Delivery | An accepted delivery was not verified within the set time |
| `DeliveryCancelled` | Delivery | A waiting delivery stopped: destination removed, or routing no longer matches under a carried-forward revision |
| `LeaseRecovered` | Delivery | A worker took over an expired lease; the covered attempt became unknown |
| `HandOffAcknowledged` | Delivery | Someone at the firm confirmed receipt |
| `HandOffEscalated` | Delivery | No acknowledgement in half the window: the fallback, then an operator |
| `UsageRated` | Usage | A conversation was rated for its cycle |
| `UsageCredited` | Usage | A correction lowered a closed cycle's charge; the credit lands in a later cycle |
| `AccessRefused` | Accounts | A request crossed firms, or lacked the role, and was refused |

---

## 6. Classification: a Policy for Uncertainty and Correction

### 6.1 Each action decides on its own terms

Each classification carries a confidence (RFC-002). Each action that reads it has
its own threshold and its own safe direction when below it:

| Action | When confident | When uncertain | Why that direction |
|---|---|---|---|
| **Routing** | The destination's rule | Send to every destination whose rule matches **any** plausible category | A missed new client costs more than an extra record |
| **Window to act** | The category's window | The **shortest** window among the plausible categories; a possible emergency or custody call is treated as one | Lateness is the loss we cannot undo |
| **Free-call rules** | Apply the rule | **Hold the conversation for review** before the cycle closes; unresolved at close, decided **in the firm's favour** | Uncertainty is our cost, not the firm's |
| **Reports** | The category | Counted in an **uncertain** column, never folded into a category | Reports must not look more certain than the data |

Thresholds are versioned with the policy and set from shadow data (slice 9);
none is a constant in code.

### 6.2 Corrections

A correction, by a reviewer or by the firm, is a new classification record that
**supersedes** the previous one (section 7). Its effects:

| What it touches | Effect |
|---|---|
| **Deliveries already made** | Never resent or recalled automatically |
| **Destinations the lead now matches** | Offered to an operator: "send to the destinations it now matches", shown to the firm when used |
| **Window and hand-off** | The original window stands for the hand-off already judged; the corrected category is recorded for reports |
| **Billing, cycle open** | Re-rated from the corrected classification |
| **Billing, cycle closed** | A credit in the next cycle if the correction lowers the charge; **never a new charge** after a cycle closes |
| **Reports** | Rebuilt from the current classification; the correction rate is itself reported |
| **The firm's mapping** | Updated for future leads, if the correction is about a call type |

### 6.3 Taxonomy scope

The taxonomy's twelve entries include three that are not kinds of call:
`system`, `line-handling` and `staff-routing`. They describe what else the
call-type field was used for. They leave the list of **call categories** and
become markers that route a call type, during mapping, to its proper home:
account settings, line settings and the staff directory
([answers](../lead-delivery/answers.md#call-categories)). The **nine** kinds of
call remain. The change lands with the taxonomy's next version in slice 4; the
lab's rules keep the current version until then.

---

## 7. Corrections to Append-only Records

Records are never edited. A change is a new record:

- **Supersession.** A correcting record names the record it supersedes. The current view is the newest record in each chain; every earlier record stays readable.
- **Compensation** for anything already relied on: a closed billing cycle, an invoice sent. A compensating entry (a credit, an adjustment) is added to a later cycle; the closed statement never changes ([answers](../lead-delivery/answers.md#billing)).
- **It applies to** classifications, usage entries, deliveries (a resend is a new delivery pointing at the one it resends), revisions and terms versions.

### 7.1 Conversations close before outcomes are known

A conversation can close while its deliveries are still retrying or held.

- **Usage is rated from the conversation's own facts:** duration, channel, classification, caller. Billing never waits for, or depends on, a delivery outcome.
- **Reports and Insight join delivery and hand-off events as they arrive;** a week's report is final only once every hand-off in it is reached or expired.

### 7.2 Repeat calls without caller numbers in billing

The repeat-call rule (same caller within 10 minutes) compares a **keyed hash of
the caller's number, with a key per firm**. Equal numbers give equal hashes
within one firm; nothing can be compared across firms, and the number itself
never enters billing.

---

## 8. Repairs, Rollbacks and Resumes: What Needs Approval

From the original integration workflow ([RFC-001, webhook lab §6](../../../airi/webhook-lab/docs/rfc/001_lead_delivery_and_client_integration.md)),
restated for this model:

| Change | Firm's approval | Recorded |
|---|---|---|
| A change to what the firm receives: destination, fields, routing, record behaviour | **Required** | The new revision and its approval |
| Reconnecting credentials, same behaviour | Not again; the firm authorises the connection | New authorization version |
| Repairing formatting without changing approved meaning | Not again | Before and after, who, why |
| Pausing a failing or compromised destination | Not needed; done at once; never rerouted silently | Who, why; the firm told |
| Resuming a paused destination, same revision | Not again | Who, why; held deliveries sent |
| Reactivating a switched-off destination after a fix | Not again, unless the fix changed behaviour | Who, why, the fix |
| Rolling back to an earlier revision | Only if that revision is still approved **and** its authorization still valid | Who, why, the revision |
| Retry timing | Not needed | Policy version |

---

## 9. Security Gates from the First Slice

Security is an acceptance criterion of every slice, not a phase at the end.

| Gate | Enforced by | Proved by | From |
|---|---|---|---|
| **Firm isolation** | The database (row-level security or a firm-scoped role per request), not query convention | A cross-firm scenario at every public boundary: firm B reading, listing, exporting or resending firm A's lead is refused | Slice 0 |
| **Destination address safety** | Address checks before sending (the lab's `Address` module), and the connection made to the address that was checked, so a name cannot resolve differently between check and send | Scenarios with internal, loopback and rebinding addresses | Slice 2 |
| **Revoked access stops sending** | The authorization version checked before every send (section 5.3) | A scenario that revokes during an outage and resumes | Slice 3 |
| **Credentials** | Encrypted at rest; never logged, shown or exported; presence and fingerprint only | Log and export scans in CI | Slice 0 |
| **Signed delivery** | Every webhook and Zapier request signed, with the lead id | Contract tests | Slice 2 |
| **Least caller data** | Each destination gets only the fields its approved mapping names | Contract tests per kind | Slice 5 |
| **Audit** | Append-only, every operator and firm action | Scenarios assert the audit entries | Slice 0 |

---

## 10. Failure-Injection Scenarios

Ten scenarios become production gates. They are written as
[contract files](../contracts/README.md), with a new `fault` step that injects
what the review describes:

| Scenario | Required result |
|---|---|
| **crash-after-send:** the worker crashes after the CRM created the record, before recording it | The attempt is **unknown**; what follows depends on the kind's duplicate handling (§4.3); never a silent second send to a kind without it |
| **double-claim:** two workers claim the same delivery | One lease holder; the other sends nothing; an expired lease is recovered safely |
| **pause-during-send:** an operator pauses as a worker is about to send | Nothing sent after the pause; an attempt already on the wire is recorded as it ends |
| **routing-change-during-outage:** the firm activates a new revision while deliveries wait | Waiting deliveries keep their revision unless "apply to waiting deliveries" was chosen; new leads use the new one |
| **accepted-never-verified:** the CRM accepts but the lookup never finds the record | The delivery stays **accepted**, never **verified**; it is flagged after a set time |
| **duplicate-intake:** the voice channel sends the completion event twice | One conversation, one lead |
| **classification-corrected:** a lead classified as general is corrected to a new matter | Both records kept; billing re-rated or credited per §6.2; the new destination offered, not sent |
| **cross-firm-read:** firm B asks for firm A's lead at every public boundary | Refused every time; the refusal audited |
| **database-down-at-intake:** the database is unavailable when intake completes | Intake is told the lead was not recorded; no capture is claimed |
| **all-routes-down:** both the usual destination and the fallback fail | The hand-off expires, escalates to an operator, and is first in every view |

---

## 11. Changes to RFC-007's Build Order

[RFC-007 §8](007_domain_model_and_build_order.md#8-build-order-thirteen-vertical-slices)
is revised in place. In short:

| Change | Why |
|---|---|
| **The conversation record and real-channel ingestion move into slice 1:** conversations from today's chat system (then voice), translated at the boundary into the standard conversation and lead; usage collected in shadow from then on | Proves the real producer of leads early; gives billing real history long before slice 10 |
| **One CRM certified end to end in slice 3:** authorization, duplicate handling, delivery, verification, against its sandbox | The adapter interface is refined on evidence before five more are built |
| **The other CRMs follow demand and evidence** in slice 7, not as a fixed six | A narrow set of excellent integrations beats six that work only on the happy path |
| **Security gates in slice 0 and every slice's acceptance** (section 9); the final readiness item shrinks to operational drills | Isolation and safety cannot be added afterwards |
| **Evidence levels, acknowledgement and escalation in slice 6** | The hand-off promise is measured as firms experience it |
| **Leases, unknown outcomes and revalidation in slice 3** | The concurrency rules belong with retries |
| **The classification policy and corrections in slice 9** | They govern every action that reads a category |

---

## 12. The Delivery Scorecard

Distinct measures, never collapsed into one success rate:

| Measure | Shows |
|---|---|
| Deliveries attempted, by kind | Volume |
| Accepted | Transport success |
| Verified | Records confirmed at the destination |
| Hand-offs accepted within the window | The one number, at the floor |
| Hand-offs confirmed within the window | The one number, as firms experience it |
| Expired hand-offs | Loss through lateness |
| Unknown outcomes, and how each was resolved | Exposure to duplicates and gaps |
| Duplicate incidents | Records created twice, found by verification or reported |
| Time to detect a stopped destination | Whether we find problems before firms do |
| Classification corrections, and their billing effect | How much uncertainty costs, and whom |

---

## 13. Key Risks and Mitigations

**Acknowledgement adds friction for firms.** *Mitigation:* a link or one-word reply; required only for emergencies by default; firms see what it buys them in the report.

**Holding record-only deliveries after an unknown outcome delays the record.** *Mitigation:* only for kinds with no duplicate handling; the hand-off is unaffected; operators see these first.

**Two numbers confuse.** *Mitigation:* always shown side by side, with the gap explained; the accepted number stays the floor.

**Conservative classification over-delivers.** *Mitigation:* only below the threshold; measured by the correction rate; thresholds tuned from shadow data.

---

## 14. Decisions and Approval Request

| # | Decision | Proposed resolution |
|---|---|---|
| D1 | Evidence | Five levels; every delivery and hand-off records its strongest |
| D2 | The one number | Reported at two levels: accepted within the window, confirmed within the window |
| D3 | Emergencies | Need acknowledgement; escalate after half the window |
| D4 | Boundaries | Idempotent intake, atomic planning, at-least-once sending, best-effort external deduplication; exactly-once never promised |
| D5 | Unknown outcomes | A fourth attempt outcome, resolved by the kind's duplicate handling |
| D6 | Destination state | Desired state and authorization in Firm configuration, observed health in Delivery, one eligibility rule |
| D7 | Concurrency | Leases; revalidation before sending; attempts stamped with revision and authorization version |
| D8 | Classification | Per-action thresholds, each failing safe in its own direction; corrections supersede; no new charge after a cycle closes |
| D9 | Taxonomy | Nine kinds of call; three markers leave the categories in the next taxonomy version |
| D10 | Corrections | Supersession, and compensation for anything already relied on |
| D11 | Repairs | The approval table of section 8 |
| D12 | Security | The gates of section 9, from slice 0 |
| D13 | Production gates | The ten scenarios of section 10 pass before production |

D2, D9 and the conditional CRM list are recorded in
[answers.md](../lead-delivery/answers.md) now; the rest move there once approved.

### Approval requested

1. The evidence levels and the two-level number (D1 to D3).
2. The guarantees, concurrency and state ownership (D4 to D7).
3. The classification and correction policy (D8 to D10), with Finance on the billing effects.
4. The repair policy, security gates and production gates (D11 to D13).

### Specialist sign-off

- **Finance:** corrections never adding charges after close; uncertainty decided in the firm's favour.
- **Security:** the gates of section 9; the keyed hash of caller numbers.
- **Customer Success:** acknowledgement links and the escalation call.
- **Engineering:** leases, unknown outcomes, the eligibility rule.

---

## 15. Next Steps

1. Build the scenario runner and test control interface (RFC-007 slice 0), and run all [contract scenarios](../contracts/README.md) against the lab: the `lab` ones must pass, the `proposed` ones fail for the reasons their `today` field gives. The ten failure-injection scenarios and every receiver's `duplicates` field are written.
2. Choose the first CRM to certify end to end, from the production feed and sandbox access.
3. Fold sections 3, 5 and 7 into the [domain model](../lead-delivery/README.md) once approved.

---

## Final Recommendation

**Promise only what we can prove, and prove it at every boundary.**

**Guiding principle: acceptance is where our evidence starts, not where the
firm's promise ends.**
