# Concurrency in the lab

How the lab keeps a lead from being lost, sent twice by mistake, or sent to a
destination an operator has just stopped, while many things happen at once. This
describes the code in `backend/src` as of November 2026. The guarantees it
implements are specified in [RFC-008 §4–5](../rfc/008_delivery_guarantees_and_evidence.md);
the flows around it are in [code-map.md](code-map.md). For a guided, step-by-step
view, open [concurrency-explorer.html](concurrency-explorer.html) in a browser.

## 1. The model in one paragraph

Every change of state is **one short write transaction**, and writes are taken one
at a time. **No network call ever happens inside a transaction.** A worker that
wants to send a delivery first takes a **lease** on it (an owner, an expiry and a
version number), then, in a second transaction, checks the lease and the
destination and records the attempt as **started**, and only then sends the
request. The answer is recorded in a third transaction, and only if the worker
still holds the same lease version. A worker that lost its lease can change
nothing. A lease that runs out is recovered by another worker, which decides from
what the record shows: nothing started (send it), or started with no answer
(**unknown**: send again only where a second copy is harmless). Domain events are
written in the same transaction as the change they describe.

## 2. What runs at the same time

| Actor | Where | Does | Concurrent with |
|---|---|---|---|
| **HTTP requests** | `Api/Handlers.fs`, `Api/Portal.fs`, `Api/Receivers.fs` | Intake, operator actions (pause, resend, routing approval, corrections), firm portal reads, the fake receivers | Each other and the worker. ASP.NET serves requests in parallel |
| **Delivery worker** | `Api/Worker.fs` → `Dispatch.runDue` | One loop per process: every 5 s the sweeps (fallbacks, hand-offs, verification lookups), then claims up to 20 due deliveries and sends them in parallel | Requests. With several processes, other workers |
| **Sends in flight** | `Dispatch.startAndSend` | Up to 20 requests to receivers at once, each with a 10 s timeout, outside any transaction | Everything, including a pause of their own destination |
| **Test control** | `Api/TestControl.fs` | Scenario runs only: moves a virtual clock and runs the same worker pass, injects faults | Disabled outside scenario runs |
| **Simulator** | `Core/Simulate.fs` | Rewrites a week of history in one write transaction, single-threaded | Run from the importer, not alongside the worker |

## 3. The database

- **SQLite in WAL mode** (`Db.openDatabase`). Readers never block the writer and see the last committed state.
- **One writer at a time.** `Db.write` takes the database's `WriteLock` (a semaphore), opens a transaction, runs the function, commits. Every write in the process goes through it, so two writes never interleave.
- **Other processes** are kept out by SQLite's own file lock; `busy_timeout = 5000` makes a writer wait up to 5 s for it before failing.
- **Reads** (`Db.read`) take no lock and no transaction.
- **A failed write commits nothing.** `Db.Unavailable` (or any database error) aborts the whole transaction; intake answers 503 "not recorded" (`Handlers.sendLead`).

Because writes are serialised, a check followed by a write in the same transaction
cannot be overtaken: whatever committed before the transaction began is what it sees.

## 4. A delivery's lease

```
pending (due) ──claim──▶ delivering ──beginAttempt──▶ attempt 'started' ──send──▶ answer ──complete──▶ delivered | pending (retry) | failed | unknown
     ▲                   owner, until,      (checks lease + destination)     (no transaction)      (checks lease version)
     │                   version+1
     └── lease runs out ──▶ claimed again (Recovered) ──recover──▶ nothing started: carry on
                                                                     started, no answer: unknown → resend only if harmless
```

| Field | Set by | Meaning |
|---|---|---|
| `status = 'delivering'` | `Store.tryClaim` | Leased by a worker |
| `lease_owner` | `Store.tryClaim` | The worker (`Dispatch.workerId`, one per process) |
| `lease_until` | `Store.tryClaim` | Claim time + `Dispatch.leaseMs` (5 min): far longer than one attempt (10 s), so a live worker never loses it mid-send |
| `lease_version` | `Store.tryClaim` | +1 on every claim. Every later write by that worker checks it (`Store.holdsLease`) |
| `attempts.outcome = 'started'` | `Store.startAttempt` | Written before the request is sent: the proof that something may have reached the destination |

`Store.setDeliveryState` clears the owner and expiry whenever a delivery settles.

### 4.1 The three transactions of one attempt

| Step | Function | In a transaction? | Checks | Writes | Events |
|---|---|---|---|---|---|
| Claim | `Store.claimDue` → `Store.tryClaim` | Yes | The delivery is still claimable: due and pending, or delivering with its lease run out. One conditional `UPDATE … WHERE claimable`; it changes one row or none | Lease fields | — |
| Recover | `Dispatch.recover` (only when the claim took over an expired lease) | Yes | Whether an attempt was started and never answered (`Store.openAttempt`) | That attempt marked unknown; the delivery `unknown` if a second copy is not safe | `LeaseRecovered`, `AttemptMade`, `DeliveryUnknown` |
| Begin | `Dispatch.beginAttempt` | Yes | The lease version still holds; the destination still exists, is active, enabled and not waiting for reconnection; the body builds | The attempt as `started`; or the delivery held, cancelled or failed instead | `Held` or `DeliveryCancelled` when it stops |
| Send | `Dispatch.send` | **No** | Address policy (no private addresses unless allowed) | — | — |
| Complete | `Dispatch.complete` | Yes | The lease version still holds | The answer on the attempt; the delivery's new state; the destination's streak or status | `AttemptMade`, then `DeliveryAccepted`, `DeliveryRefused`, `DeliveryGaveUp`, `Held` or `DeliveryUnknown` |

If the lease no longer holds at Complete, the answer is kept as evidence on the
worker's own attempt ("unknown, then a late answer") and the delivery is not
touched, except that an accepted late answer settles a delivery left `unknown`.

### 4.2 After an unknown outcome

An attempt is **unknown** when it was sent and no answer was recorded: the worker
lost its lease, or the request timed out (`Dispatch.outcomeOf`). What follows
depends on how the destination handles a second copy (`Dispatch.duplicatesOf`: the
destination's `duplicates` setting, else its kind's receiver contract):

| Duplicate handling | After unknown | Why |
|---|---|---|
| `harmless` (email, SMS, Slack) | Send again | A second message does no harm |
| `idempotency-key` | Send again | The receiver ignores a key it has seen |
| `lookup`, `external-reference`, `none`, `unknown` | Status `unknown`: an operator checks the destination, then resolves it | A second record in a CRM cannot be told apart from the first |

A refused connection, DNS or TLS failure sent nothing, so it stays an ordinary
transient retry.

An operator **resolves** an unknown outcome after checking the destination
(`Dispatch.resolve`, or a resend through `Dispatch.replay`): **found** (accepted, on
their word), **closed** (left unsent, with the reason) or **resent** (a new delivery
replaces it). The resolution is recorded on the delivery (`resolution`) and
**stands**. In `Dispatch.complete`, a late accepted answer settles a delivery only
while it is `unknown` and unresolved; otherwise it is kept on its attempt as evidence
and flagged with `LateAcceptance`, because the destination may now hold a second copy
([answers.md](answers.md)).

### 4.3 Order of work

`Store.claimDue` takes what is due nearest **hand-off deadline** first (the lead's
creation time plus its window to act), so an emergency waiting behind a backlog of
routine records is sent first. It reads four times the batch size and sorts it.

## 5. Every race, and what decides it

| Race | Decided by | Outcome | Proved by |
|---|---|---|---|
| Two workers claim one delivery | The conditional `UPDATE` in `Store.tryClaim` | One wins; the other gets nothing and sends nothing | `double-claim`, `LeaseTests` |
| A worker stalls after claiming; its lease runs out; another claims | `lease_version`: the stalled worker's begin checks a version that no longer holds | The stalled worker sends nothing when it wakes | `double-claim` |
| An operator pauses a destination before the attempt starts | Both are write transactions; the pause committed first, so `beginAttempt` sees it | Held, never sent; sent when resumed (`Store.releaseHeld`) | `pause-during-send` |
| An operator pauses while the request is on the wire | The attempt started before the pause committed | That attempt cannot be recalled; its answer is recorded normally. Later leads are held | `pause-during-send` |
| A worker dies after sending | The lease runs out; `Dispatch.recover` finds the started attempt | Unknown; resent only if harmless | `crash-after-send` |
| An answer arrives after the lease was taken over | `Dispatch.complete` checks the version | Kept as evidence on its own attempt; settles an `unknown` delivery nobody resolved; otherwise `LateAcceptance` | `LeaseTests`, `pause-during-send`, `stall-after-start` |
| A worker records its attempt, stalls past its lease, then sends anyway | The recovering worker sees the started attempt; the stalled worker's version no longer holds | A CRM is not sent again by the recovering worker; the stalled send's answer settles the unknown delivery. Email is sent again (harmless), and the stalled answer is flagged as a second copy | `stall-after-start` |
| An operator resolves an unknown outcome, then its late answer arrives | `Dispatch.complete` checks `resolution` | The resolution stands: closed stays closed, a resend is not undone; the answer is evidence, flagged `LateAcceptance` | `unknown-resolved`, `LeaseTests` |
| A request times out | `Dispatch.outcomeOf` | Retried where harmless; otherwise unknown | `crm-timeout`, `relay-timeouts` |
| The process restarts mid-send | Nothing is requeued on start; leases run out | Recovery decides, as for a crash | `crash-after-send` |
| A destination needs reconnection while others wait | `Dispatch.complete` holds that destination's other waiting deliveries in the same transaction (`Store.holdPendingFor`) | Held, not retried, until reconnected | `auth-expired`, `hook-gone` |
| A channel sends the same call twice, even at once | `Dispatch.intake` looks up the conversation and records the lead in one write transaction; a unique index on (firm, conversation) backs it | One lead; the repeat is answered with the lead already recorded | `duplicate-intake`, `IntakeTests` |
| The database stops answering during intake | The write fails before its transaction begins | 503 "not recorded"; the channel sends again later; one lead | `database-down-at-intake` |
| A fallback is due as the usual destination accepts | `Dispatch.runFallbacks` checks for an accepted attempt and an existing fallback delivery in its transaction | The fallback goes at most once; an acceptance after the check does not recall it (the firm gets both) | `both-routes-down`, `HandOffTests` |
| An emergency is acknowledged as its escalation falls due | `Dispatch.runHandOffs` checks `acknowledged_at` and `escalated_at` in its transaction | Escalated at most once; an acknowledgement committed first prevents it | `all-routes-down`, `HandOffTests` |
| A routing change is approved while deliveries wait or send | `Dispatch.approveRouting` decides again only `pending` deliveries, and only when asked | Waiting ones keep their revision unless the firm applies the change; one being sent is never touched | `routing-change-during-outage`, `RoutingTests` |
| An operator resends while the original still waits | `Dispatch.replay` checks, in its transaction, for a delivery of the lead to that destination still `pending` or `delivering` | Refused (409) until it settles; a bulk resend skips it and says so | `LeaseTests` |
| Two sweeps look up the same record | Conditional updates in `Dispatch.runVerifications` (`WHERE verified_at IS NULL`, `… AND verify_overdue_at IS NULL`) | Verified or flagged once; the event is emitted once | — |

## 6. Timings

| Constant | Value | Where |
|---|---|---|
| Lease | 5 min | `Dispatch.leaseMs` |
| Request timeout | 10 s | `DELIVERY_TIMEOUT_MS`, `Config.fs` |
| Batch claimed per pass | 20 (worker), 50 (test control) | `Worker.fs`, `TestControl.fs` |
| Idle wait when nothing was due | 200 ms | `Worker.fs` |
| Sweeps (fallbacks, hand-offs, lookups) | Every 5 s | `Worker.fs` |
| Lookup of an unverified record | At most once a minute | `Dispatch.lookupEveryMs` |
| Lookups per sweep | At most 20, sent at once; the longest unchecked first | `Dispatch.lookupBatch` |
| Waiting for another process's write | Up to 5 s | `busy_timeout`, `Db.fs` |

## 7. Known limits

Found while writing this down; none is covered by a scenario yet. Two more found then are fixed: verification
lookups are bounded (20 per sweep, sent at once, so a sweep takes at most one timeout) and written
conditionally; and a resend is refused while the lead still waits or is being sent to that destination.

1. **A slow receiver delays the next batch.** `Dispatch.runDue` waits for every send in its batch before claiming more, so one receiver that does not answer holds up newly due deliveries by up to the 10 s timeout. Other sends in the same batch are not delayed. Fix: claim again as each send finishes, or run destinations on separate tasks.
2. **One process is the supported deployment.** Leases stay correct across processes (the claim is one conditional write), but SQLite transactions are deferred: a transaction that reads and then writes can fail if another process wrote in between, and the worker pass is retried. A database with concurrent writers (PostgreSQL) would need `beginAttempt` to lock the destination's row (`SELECT … FOR UPDATE`) so a pause cannot commit between its check and its write.

## 8. Where it is tested

- **Scenarios** (`python tools/run_scenarios.py`): `double-claim`, `pause-during-send`, `crash-after-send`, `crm-timeout`, `relay-timeouts`, `duplicate-intake`, `database-down-at-intake`, `routing-change-during-outage`, `all-routes-down`, `unknown-resolved`, `stall-after-start`. Faults are injected through `POST /api/test/faults` (`Dispatch.Faults`, `Db.WritesFail`).
- **F# tests**: `LeaseTests` (exclusive claim, deadline order, the policy after unknown), `IntakeTests`, `HandOffTests`, `RoutingTests`.
