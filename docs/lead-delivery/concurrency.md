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
| **Delivery worker** | `Api/Worker.fs` → `Dispatch.work` | One loop per process: keeps up to 20 sends running and claims again as each finishes; every 5 s the sweeps (fallbacks, hand-offs), with the verification lookups as a task of their own | Requests. With several processes, other workers |
| **Sends in flight** | `Dispatch.startAndSend` | Up to 20 requests to receivers at once, each with a 10 s timeout, outside any transaction | Everything, including a pause of their own destination |
| **Test control** | `Api/TestControl.fs` | Scenario runs only: moves a virtual clock and runs the same worker pass, injects faults | Disabled outside scenario runs |
| **Simulator** | `Core/Simulate.fs` | Rewrites a week of history in one write transaction, single-threaded | Run from the importer, not alongside the worker |

## 3. The database

- **SQLite in WAL mode** (`Db.openDatabase`). Readers never block the writer and see the last committed state.
- **One writer at a time.** `Db.write` takes the database's `WriteLock` (a semaphore), opens a transaction, runs the function, commits. Every write in the process goes through it, so two writes never interleave.
- **Other processes** are kept out by SQLite's own file lock; `busy_timeout = 5000` makes a writer wait up to 5 s for it before failing.
- **Every write transaction begins `IMMEDIATE`** (the default of Microsoft.Data.Sqlite's `BeginTransaction`): it takes the file's write lock at its start, so a second writer waits for it rather than reading a snapshot and failing at its first write. `WorkerTests` shows it with six writers, each with its own lock, sending 400 leads through one file: no write failed, every lead was sent once, none was lost.
- **Reads** (`Db.read`) take no lock and no transaction.
- **A failed write commits nothing.** `Db.Unavailable` (or any database error) aborts the whole transaction; intake answers 503 "not recorded" (`Handlers.sendLead`).

Because writes are serialised, a check followed by a write in the same transaction
cannot be overtaken: whatever committed before the transaction began is what it sees.

**On PostgreSQL** (RFC-011; the suite standardizes on it) the same guarantee holds another way.
Writers run concurrently, with no in-process lock:

- **`Db.write` runs `SERIALIZABLE`.** PostgreSQL aborts a transaction that a concurrent one could
  have changed (40001), so the outcome is as if writes had run one at a time. A transaction that
  loses a race (40001, a deadlock 40P01, or a unique index taking the other writer's row 23505) is
  run again from the start, up to ten times with a short random pause: a check-then-insert then
  sees the other writer's row and answers as a repeat. A write's function keeps its effects inside
  the transaction, so running it again is safe (no write here sends, reads files or waits).
- **`Db.writeConditional` runs `READ COMMITTED`,** for claims only: each is one conditional
  `UPDATE … WHERE <still claimable>`, and a second writer waits for the first and re-checks the
  row, so of two claims one wins. Under `SERIALIZABLE`, many workers claiming from one queue
  mostly abort each other.
- **`beginAttempt` locks the destination's row** (`Store.lockConnection`, `SELECT … FOR UPDATE`)
  before checking whether it is paused or switched off, so a pause can't commit between the check
  and the attempt (section 7, limit 2).

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
| Recover | `Dispatch.recover` (only when the claim took over an expired lease) | Yes | The new lease still holds (a late answer may have settled the delivery since the claim); whether the latest attempt may have reached the destination without a definitive answer: still `started`, or ended with no HTTP answer (`Store.unansweredAttempt`) | That attempt marked unknown; the delivery `unknown` if a second copy is not safe | `LeaseRecovered`, `AttemptMade`, `DeliveryUnknown` |
| Begin | `Dispatch.beginAttempt` | Yes | The lease version still holds; the destination still exists, is active, enabled and not waiting for reconnection; the body builds | The attempt as `started`; or the delivery held, cancelled or failed instead | `Held` or `DeliveryCancelled` when it stops |
| Send | `Dispatch.send` | **No** | Address policy (no private addresses unless allowed) | — | — |
| Complete | `Dispatch.complete` | Yes | The lease version still holds | The answer on the attempt; the delivery's new state; the destination's streak or status | `AttemptMade`, then `DeliveryAccepted`, `DeliveryRefused`, `DeliveryGaveUp`, `Held` or `DeliveryUnknown` |

If the lease no longer holds at Complete, the answer is kept as evidence on the
worker's own attempt ("unknown, then a late answer") and the delivery is not
touched, with one exception: an **accepted** answer is definitive (the destination
has the lead), so it settles a delivery that is not yet settled (waiting, being
sent under a newer lease, or unknown) and that no operator has resolved. Settling it
ends the newer lease, so the worker that took over stops at its next step instead of
sending a second copy.

### 4.2 After an unknown outcome

An attempt is **unknown** when it was sent and no answer was recorded: the worker
lost its lease, or the request timed out (`Dispatch.outcomeOf`). What follows
depends on how the destination handles a second copy (`Dispatch.duplicatesOf`: the
destination's `duplicates` setting, else its kind's receiver contract):

| Duplicate handling | After unknown | Why |
|---|---|---|
| `harmless` (email, SMS, Slack) | Send again | A second message does no harm |
| `idempotency-key` | Send again | The receiver ignores a key it has seen |
| `lookup` | Status `unknown`, and looked up by our lead reference (`Dispatch.runVerifications`): found settles it (accepted, verified, resolved as found by the lookup); not found leaves it for an operator | A lookup can confirm a record, never prove one will not appear |
| `external-reference`, `none`, `unknown` | Status `unknown`: an operator checks the destination, then resolves it | A second record in a CRM cannot be told apart from the first |

Each kind's choice is stated in its delivery contract (`afterUnknown` in
`docs/contracts/receivers/{kind}.json`), and `ContractTests` checks it against this code.

A refused connection, DNS or TLS failure sent nothing, so it stays an ordinary
transient retry.

An operator **resolves** an unknown outcome after checking the destination
(`Dispatch.resolve`, or a resend through `Dispatch.replay`): **found** (accepted, on
their word), **closed** (left unsent, with the reason) or **resent** (a new delivery
replaces it). The resolution is recorded on the delivery (`resolution`) and
**stands**. In `Dispatch.complete`, a late accepted answer settles a delivery only
while it is unsettled and unresolved; otherwise it is kept on its attempt as evidence
and flagged with `LateAcceptance`, because the destination may now hold a second copy
([answers.md](answers.md)).

### 4.3 Order of work, and fairness

`Store.claimDue` decides, in one SQL query over everything due:

1. **Nearest hand-off deadline first**: the lead's hand-off due time, fixed at intake, so an emergency waiting behind a backlog of routine records is sent first.
2. **No destination holds more than `Store.inFlightPerDestination` (4) leases at once**, counting leases still running from earlier claims. A destination that answers slowly, or not at all, ties up at most four send slots; everything else keeps moving (destination isolation).
3. **One firm takes at most a quarter of a claim while others have work due**; the slots left over go to whatever is due next, so nothing waits when no one else is busy.

Because it ranks everything due in SQL (window functions per destination and per firm), a firm with a large backlog cannot hide another firm's lead, which the earlier version (the first 80 due, sorted in memory) could.

The worker (`Dispatch.work`) keeps up to `Dispatch.maxInFlight` (20) sends running and claims again as soon as one finishes, so a slow send delays only itself. The verification lookups run as a task of their own, one sweep at a time, never on the send path.

## 5. Every race, and what decides it

| Race | Decided by | Outcome | Proved by |
|---|---|---|---|
| Two workers claim one delivery | The conditional `UPDATE` in `Store.tryClaim` | One wins; the other gets nothing and sends nothing | `double-claim`, `LeaseTests` |
| A worker stalls after claiming; its lease runs out; another claims | `lease_version`: the stalled worker's begin checks a version that no longer holds | The stalled worker sends nothing when it wakes | `double-claim` |
| An operator pauses a destination before the attempt starts | Both are write transactions; the pause committed first, so `beginAttempt` sees it | Held, never sent; sent when resumed (`Store.releaseHeld`) | `pause-during-send` |
| An operator pauses while the request is on the wire | The attempt started before the pause committed | That attempt cannot be recalled; its answer is recorded normally. Later leads are held | `pause-during-send` |
| A worker dies after sending | The lease runs out; `Dispatch.recover` finds the started attempt | Unknown; resent only if harmless | `crash-after-send` |
| An answer arrives after the lease was taken over | `Dispatch.complete` checks the version | Kept as evidence on its own attempt; an accepted one settles a delivery not yet settled and not resolved; otherwise `LateAcceptance` | `LeaseTests`, `pause-during-send`, `stall-after-start` |
| The answer arrives after another worker took over the lease but **before it recovers** | Accepted: the answer settles the delivery and ends the newer lease, and `Dispatch.recover` stops when its lease no longer holds. No answer: `Store.unansweredAttempt` still counts the attempt as possibly sent | No second copy to a CRM. *Found by the interleaving search*: before the fix, the takeover saw no started attempt and sent the lead again | `InterleavingTests` (search and two pinned traces) |
| A worker records its attempt, stalls past its lease, then sends anyway | The recovering worker sees the started attempt; the stalled worker's version no longer holds | A CRM is not sent again by the recovering worker; the stalled send's answer settles the unknown delivery. Email is sent again (harmless), and the stalled answer is flagged as a second copy | `stall-after-start` |
| An operator resolves an unknown outcome, then its late answer arrives | `Dispatch.complete` checks `resolution` | The resolution stands: closed stays closed, a resend is not undone; the answer is evidence, flagged `LateAcceptance` | `unknown-resolved`, `LeaseTests` |
| A request times out | `Dispatch.outcomeOf` | Retried where harmless; otherwise unknown | `crm-timeout`, `relay-timeouts` |
| The process restarts mid-send | Nothing is requeued on start; leases run out | Recovery decides, as for a crash | `crash-after-send` |
| A destination needs reconnection while others wait | `Dispatch.complete` holds that destination's other waiting deliveries in the same transaction (`Store.holdPendingFor`) | Held, not retried, until reconnected | `auth-expired`, `hook-gone` |
| A channel sends the same call twice, even at once | `Dispatch.intake` looks up the conversation and records the lead in one write transaction; a unique index on (firm, conversation) backs it | One lead; the repeat is answered with the lead already recorded | `duplicate-intake`, `IntakeTests` |
| The call center sends the same lead twice, or a changed one, even at once | `Dispatch.ingest` resolves the firm alias, looks up the interaction and the lead id, and writes the lead, its deliveries and the original payload in one write transaction; unique indexes on (firm, interaction) and (firm, lead id) back it | One lead; the same payload is a duplicate, a different one a 409 that changes nothing | `IngestTests` |
| The database stops answering during intake | The write fails before its transaction begins | 503 "not recorded"; the channel sends again later; one lead | `database-down-at-intake` |
| A fallback is due as the usual destination accepts | `Dispatch.runFallbacks` checks for an accepted attempt and an existing fallback delivery in its transaction | The fallback goes at most once; an acceptance after the check does not recall it (the firm gets both) | `both-routes-down`, `HandOffTests` |
| An emergency is acknowledged as its escalation falls due | `Dispatch.runHandOffs` checks `acknowledged_at` and `escalated_at` in its transaction | Escalated at most once; an acknowledgement committed first prevents it | `all-routes-down`, `HandOffTests` |
| A routing change is approved while deliveries wait or send | `Dispatch.approveRouting` decides again only `pending` deliveries, and only when asked | Waiting ones keep their revision unless the firm applies the change; one being sent is never touched | `routing-change-during-outage`, `RoutingTests` |
| A destination answers slowly while other leads fall due | `Dispatch.work` claims again as each send finishes; `Store.claimDue` gives one destination at most four leases at once | Only that destination's deliveries wait | `WorkerTests` (fails under the old batch-at-a-time loop) |
| One firm has a large backlog of urgent leads | `Store.claimDue` ranks everything due in SQL, caps each destination's leases and each firm's share of a claim | Another firm's lead is claimed in the same pass | `WorkerTests` |
| Several processes write to one database file | Each write transaction begins `IMMEDIATE`; a claim is one conditional write | Writers wait for each other; every lead sent once | `WorkerTests` (six writers, 400 leads) |
| Several processes write to one PostgreSQL database | `SERIALIZABLE` writes, run again when they lose a race; claims conditional under `READ COMMITTED` | One lead per event, one firm per canonical id, one resend per request, every delivery claimed once | `ConcurrencyTests` (eight writers at once, on both engines; each guarantee shown to fail without its mechanism) |
| An operator resends while the original still waits | `Dispatch.replay` checks, in its transaction, for a delivery of the lead to that destination still `pending` or `delivering` | Refused (409) until it settles; a bulk resend skips it and says so | `LeaseTests` |
| A lookup finds the record of an unknown outcome as an operator resolves it | Both write conditionally (`… AND status='unknown' AND resolution IS NULL`) | Whichever commits first settles it; the other changes nothing | `emergency-under-pressure` |
| Two sweeps look up the same record | Conditional updates in `Dispatch.runVerifications` (`WHERE verified_at IS NULL`, `… AND verify_overdue_at IS NULL`) | Verified or flagged once; the event is emitted once | — |

## 6. Timings

| Constant | Value | Where |
|---|---|---|
| Lease | 5 min | `Dispatch.leaseMs` |
| Request timeout | 10 s | `DELIVERY_TIMEOUT_MS`, `Config.fs` |
| Sends in flight per worker | 20, refilled as each finishes | `Dispatch.maxInFlight`, `Dispatch.work` |
| Leases at once per destination | 4 | `Store.inFlightPerDestination` |
| One firm's share of a claim | A quarter, while others have work due | `Store.claimDue` |
| Claimed per test-control pass | 50, then waits for all of them (a virtual clock) | `TestControl.fs`, `Dispatch.runDue` |
| Idle wait when nothing was due | 200 ms | `Worker.fs` |
| Sweeps (fallbacks, hand-offs, lookups) | Every 5 s | `Worker.fs` |
| Lookup of an unverified record | First a minute after its last attempt, then at most once a minute | `Dispatch.lookupEveryMs` |
| Lookups per sweep | At most 20, sent at once; the longest unchecked first | `Dispatch.lookupBatch` |
| Waiting for another process's write | Up to 5 s | `busy_timeout`, `Db.fs` |

## 7. Known limits

Fixed since this list was first written: verification lookups bounded and conditional; a resend refused
while the lead is in flight; a slow receiver no longer delays newly due deliveries (`Dispatch.work`); a
destination and a firm each limited to their share of the send slots (`Store.claimDue`); and the claim
that SQLite transactions here are deferred, which was wrong (section 3).

1. **Several processes are shown on one machine, not across machines.** The multi-writer test uses separate connections and locks in one process, which meet at SQLite's file lock as separate processes do; no run used separate processes or a network file system (where SQLite's locking is not reliable). Under heavy contention a write that waits more than 5 s fails: the worker retries its pass, and intake answers 503 so the channel sends again.
2. **PostgreSQL's one predicted change is made.** `beginAttempt` locks the destination's row (`Store.lockConnection`); `ConcurrencyTests` shows a pause waiting for it, and committing between check and attempt without it. More was needed than predicted: `SERIALIZABLE` writes, since `Dispatch.replay` and other checks-then-writes have no unique index behind them (eight concurrent resends made eight deliveries under `READ COMMITTED`).
4. **A destination's send slots are a soft limit on PostgreSQL.** `Store.claimDue` counts the leases a destination already holds, and claims run `READ COMMITTED`, so concurrent workers can briefly lease more than four to one destination. Fairness, not correctness: every delivery is still claimed once.
3. **Fairness is per claim, not over time.** A firm with many destinations gets four leases on each; shares are not weighted by firm size or plan.

## 8. Where it is tested

- **Scenarios** (`python tools/run_scenarios.py`): `double-claim`, `pause-during-send`, `crash-after-send`, `crm-timeout`, `relay-timeouts`, `duplicate-intake`, `database-down-at-intake`, `routing-change-during-outage`, `all-routes-down`, `unknown-resolved`, `stall-after-start`, `emergency-under-pressure`. Faults are injected through `POST /api/test/faults` (`Dispatch.Faults`, `Db.WritesFail`).
- **F# tests**: `LeaseTests` (exclusive claim, deadline order, the policy after unknown), `IntakeTests`, `HandOffTests`, `RoutingTests`, `ContractTests` (each delivery contract against the code), and `InterleavingTests` (section 9), `WorkerTests` (a slow destination, a busy firm, several writers on one file), `ConcurrencyTests` (eight writers at once on intake, contract ingest, provisioning, resend and claims, and a pause against an attempt: on SQLite and, with `DELIVERY_TEST_PG`, PostgreSQL).

## 9. The model, checked exhaustively

The scenarios test the races someone thought of. `InterleavingTests` tests every
ordering of the actors' steps, up to a bound, against the real `Store` and `Dispatch`
code on a real SQLite database, and checks the invariants after every step.

### 9.1 The model

One lead, planned to one destination, as in the lab after intake. A state is

    S = (deliveries, destination, workerA, workerB, receiver, clock)

where `deliveries` are the rows (status, lease version and expiry, resolution,
attempts), `destination` is enabled or paused, each worker holds what it would hold
in memory between its steps, and `receiver` is every request the destination got and
whether it kept the lead. The actions, each one real transaction or one event outside:

| Action | Actor | Runs |
|---|---|---|
| `Claim(w)` | worker | `Store.claimDue` |
| `Recover(w)` | worker, after claiming an expired lease | `Dispatch.recover` |
| `Begin(w)` | worker | `Dispatch.beginAttempt` |
| `Send(w, kept)` | network | the request arrives; the receiver keeps the lead or loses it |
| `Answer(w, accepted \| timeout \| 503)` | network, then worker | `Dispatch.complete` (201 only if kept, 503 only if not) |
| `Crash(w)`, `CrashAfterSend(w)` | worker | forgets what it held |
| `TimePasses` | clock | 6 minutes: leases run out, retries fall due |
| `Pause`, `Resume` | operator | `Dispatch.setPaused`, `Store.releaseHeld` |
| `Resend`, `Close` | operator | `Dispatch.replay`, `Dispatch.resolve` |

Concurrency is every interleaving of these. Time passes at most twice and the operator
acts at most twice per run, so the search ends.

### 9.2 The invariants (safety: something bad never happens)

1. **One valid lease.** No two workers hold the current lease version of one delivery.
2. **Stale workers write nothing.** A worker whose lease version no longer holds changes no delivery when it begins or completes, except that an accepted answer settles a delivery not yet settled and not resolved (§4.1).
3. **A pause stops new attempts.** Once a pause commits, no attempt starts on that destination until it resumes.
4. **No second copy where a second copy is not safe.** For a destination whose contract says `afterUnknown` is not `resend`, our automatic sends never leave two copies at the receiver. An operator's resend is a deliberate decision and is not counted.
5. **Accepted means the destination has it.** A delivery is `delivered` only if the receiver kept the lead, or an operator found it there.

Liveness (a healthy waiting delivery is eventually sent) needs fairness assumptions
and is not checked; the scenarios cover it for the cases that matter.

### 9.3 How it is checked

A depth-first search over the actions. Each state is a private in-memory SQLite
database, cloned from its parent with SQLite's backup API, so every branch starts
from exactly the state it names. States that are the same (same rows by position,
same worker stages, same receiver record, same budget left) are explored once, unless
reached again with more steps left. The run also records which races it reached, and
fails if it never reached one it must (a stale begin, a stale answer, a begin on a
paused destination, a late answer settling a delivery, recovery leaving an unknown
outcome, an operator closing one): a check that never meets a race proves nothing.

| Depth | States (CRM, email) | Time (both) | When |
|---|---|---|---|
| 8 | 1,357, 1,407 | 16 s | Every test run (default) |
| 10 | 3,086, 3,453 | 44 s | `INTERLEAVING_DEPTH=10` |
| 11 | 4,645, 4,942 | 68 s | `INTERLEAVING_DEPTH=11` |

Run it alone: `dotnet run --project tests/Tests --artifacts-path ../.testbuild -- --filter-test-list interleavings`.

### 9.4 What it showed

- **Three planted bugs were each caught at depth 8**, with the shortest trace that shows them: `complete` ignoring the lease version (invariant 2, 6 steps), `beginAttempt` ignoring a pause (invariant 3), and recovery resending to a CRM after an unknown outcome (invariant 4, 8 steps). Email passes the last one, as its contract allows a second copy.
- **One real bug, at depth 9 and 10**, missed by every scenario: a worker's answer that arrived after another worker took over the lease, but before it recovered, closed the attempt, so the recovery saw no unanswered attempt and sent the CRM the lead again. Fixed in `Dispatch.complete` (an accepted answer settles the delivery and ends the newer lease), `Dispatch.recover` (stops if its lease no longer holds) and `Store.unansweredAttempt` (an attempt with no HTTP answer counts as possibly sent). Both traces are kept as plain tests.
