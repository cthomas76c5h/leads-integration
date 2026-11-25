# The call center's usage facts: how billing takes them

**Status: Lab (milestone 5, delivery's side), 2026-11-25.** The center publishes, for each ended
interaction, what it used: `usage/1`, its contract C6 (`../call-center-lab/docs/suite/README.md`).
Delivery takes those facts, keeps them as they came, and rates a lead from what was measured instead
of an estimate. They are facts only: **rating alone decides what is billable** (suite S-3).

| File | What it is |
|---|---|
| [usage-1.example.json](usage-1.example.json) | An exact copy of the center's published example (`../call-center-lab/docs/contracts/usage/`). Never edited here: `python tools/check_contracts.py` fails if it differs. The center checks every field in it is still published (`crates/center-sim/tests/contracts.rs`) |
| `POST /api/ingest/usage/1` | The route ([api.md](../../api.md)); `GET /api/firms/{id}/usage` lists a firm's |

## What happens to a record

1. **Checked, tolerantly** ([UsageContract.fs](../../../backend/src/Core/UsageContract.fs)): the
   contract is `usage/1`, and the fields rating reads are there with the right type: `interaction`,
   `firm`, `started_at`, `ended_at`, `duration_ms`, `result`, and `revision` and `answered_by` when
   given. Every other field is accepted and kept as sent: the center changes its contract additively.
2. **Only an ended interaction.** `ended_at` must be set and `result` must not be `Open`: usage facts
   are immutable, and an open interaction's would still change.
3. **One per firm and interaction** ([Billing.ingestUsage](../../../backend/src/Core/Billing.fs)), in
   one transaction: the firm resolved as for leads (canonical id, else alias); the same record again
   is a repeat (200), a different one a conflict (409, nothing changed); otherwise kept byte for byte.
4. **Rating reads it.** A lead joins its usage through its conversation id, which is the center's
   interaction id (`Store.measuredSeconds`). That lead's length is the measured `duration_ms`
   (rounded up to a second), not the lab's estimate: a nameless caller who talked 45 seconds is not a
   free hang-up, and a 25-minute call counts as two under fair use.

## Gaps this slice leaves

| Gap | Effect today |
|---|---|
| An interaction with usage but no lead (abandoned, lost, a call that left no lead) | Kept, not rated: conversations are still one per lead. The statement says how many, by how they ended (check `usage-without-lead`, a note that never flags the account). How to rate them is a business rule still to decide: abandoned and lost calls are presumably free, but a handled call with no lead may only mean its lead hasn't arrived |
| Usage that arrives after its cycle closed | A closed cycle stays as it was locked; the usage is kept, not re-rated |
| The category is not in `usage/1` | It comes from the lead, as before; a call without a lead has none |
| One billing account per firm | Accounts holding several firms wait for M6 (suite X-5) |
| The center does not publish yet | Its example is the only record so far; the center's outbox is M3's |
