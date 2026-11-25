# RFC-011: Delivery on PostgreSQL

**Answering AI — Technical Proposal**

**Date:** November 25, 2026
**Status:** Done (2026-11-25): delivery's store is PostgreSQL; the decision to move is taken (suite S-8), this is how
**Decision owner:** Engineering
**Related:** suite S-8 and milestone M4b (`../call-center-lab/docs/suite/`) · [concurrency.md](../lead-delivery/concurrency.md) · RFC-008 (the guarantees the store must keep)

---

## 1. Why, and what must not change

The suite standardizes on PostgreSQL (S-8), and delivery must move before usage integration
(M5). Each service keeps its own data: delivery gets its own database, not the center's.

What must not change is everything RFC-008 and [concurrency.md](../lead-delivery/concurrency.md)
promise: a lead recorded with its deliveries in one transaction; one claim per delivery; a
lease that a stale worker can't act on; nothing sent twice or lost when writers race. Today
some of those rest on SQLite's single writer. On PostgreSQL they must rest on constraints and
transaction isolation instead.

## 2. What depends on SQLite today (verified, 2026-11-25)

| What | Where | On PostgreSQL |
|---|---|---|
| **Summing conditions:** `SUM(d.status = 'delivered')`, 13 places | `Store.fs` 595–596, 712–724; `Destinations.fs` 75–76 | Invalid: a condition is not a number. `SUM(CASE WHEN … THEN 1 ELSE 0 END)` works on both |
| **A condition read as a number:** `c.settings LIKE '%…%'` selected as a column | `Store.fs` 434, 718 | Returns a boolean the reader takes as an integer: `CASE WHEN … THEN 1 ELSE 0 END` |
| **`SUM` over a 64-bit column returns a decimal** | the same 13 | `CAST(SUM(…) AS BIGINT)`, on both |
| `INTEGER` columns (74), holding UTC milliseconds | `Db.fs` schema | 32-bit in PostgreSQL: every one becomes `BIGINT` (SQLite treats `BIGINT` as `INTEGER`) |
| Two-argument `MIN`/`MAX` | `Store.fs` 274–276 | `LEAST`/`GREATEST`, which SQLite lacks: a `CASE` works on both |
| `INSERT OR IGNORE` | `Terms.fs` 57 | `INSERT … ON CONFLICT DO NOTHING`, on both |
| `COLLATE NOCASE` | `Db.fs` 121; `Store.fs` 1214 | `lower(name)`, on both |
| Ties broken by `rowid` | `Store.fs` 1101, 1111 | No `rowid`: an explicit sequence column |
| Append-only triggers with `RAISE(ABORT, …)` | `Db.fs`, `approvals_append_only_u/_d` | A trigger function in PL/pgSQL |
| `PRAGMA foreign_keys`, `busy_timeout`, `journal_mode = WAL`; columns added by probing `pragma_table_info` | `Db.fs` 34, 431, 469 | Not needed, or `ALTER TABLE … ADD COLUMN IF NOT EXISTS` |
| **One writer at a time:** an in-process `SemaphoreSlim`, and every write transaction `IMMEDIATE` | `Db.fs` 13, 87–97; concurrency.md §1 | Writers run concurrently. Check-then-insert (intake, ingest, provisioning) must rely on the unique indexes and retry, or take a row or advisory lock |
| Tests: a temporary database file per test; `WorkerTests` "writers with separate locks share one file"; `InterleavingTests` | `Support.fs`, 27 uses of Sqlite types in 8 files | A database per test from a template; "separate processes share one database" |
| Reading a legacy SQLite file | `src/Import/` | Unchanged: it is a source to read, not delivery's store |

`LIKE` is case-insensitive in SQLite and case-sensitive in PostgreSQL. Every `LIKE` in the
code matches text delivery wrote itself, in a fixed case, so none should change behaviour;
step 2 checks it by running the whole suite on both.

## 3. How

Each step is usable on its own, and passes every test and scenario before the next starts.

| Step | Work | Done when |
|---|---|---|
| 1 | **SQL that runs on both.** The rows of §2 that have a form valid in both, written that way, still on SQLite | Every test and scenario passes on SQLite, unchanged |
| 2 | **The store chooses its database by its connection string.** `Db` gains a PostgreSQL session (Npgsql, already a dependency of the importer); the schema in `BIGINT`, the triggers per database. Tests run against either: `DELIVERY_DB=postgres` makes each test take a database from a template on the server in `backend/compose.yaml` | The whole suite passes on both |
| 3 | **Concurrency on PostgreSQL's terms.** Remove the in-process write lock for PostgreSQL. Check-then-insert paths retry on a unique violation and answer as a repeat; claims and leases use conditional updates that touch one row. `WorkerTests` and `InterleavingTests` run with separate connections as separate processes | Both pass on PostgreSQL, with concurrency.md updated |
| 4 | **PostgreSQL by default.** The API, importer, scenarios and acceptance tool use it; the lab's data is re-imported (it is all re-importable: the legacy importer, seed and simulate) | Scenarios and `tools/acceptance_m3.py` pass on PostgreSQL |
| 5 | **SQLite removed** from delivery's store | No SQLite store code left; the legacy importer still reads its SQLite source |

## Progress

| Step | State |
|---|---|
| 1 | **Done.** 13 sums over conditions, 2 conditions read as numbers, two-argument `MIN`/`MAX`, `INSERT OR IGNORE` and name order rewritten in forms both accept |
| 2 | **Done.** `Db` takes a file path (SQLite) or an Npgsql connection string (`Host=…`); the PostgreSQL schema is derived from the one schema (`BIGINT`, identities, `rowid` on the two tables that order by it, `lower(name)`, the append-only trigger as a function); columns added with `ADD COLUMN IF NOT EXISTS`. `DELIVERY_TEST_PG` runs the suite with a database per test. On PostgreSQL it found and fixed: a `GROUP BY` that selected an ungrouped joined column (SQLite allows it), and parameters tested only for null (PostgreSQL can't infer their type: cast). All 207 tests pass on both; the scenarios pass on SQLite |
| 3 | **Done.** No in-process lock on PostgreSQL. `Db.write` runs `SERIALIZABLE` and runs a transaction again when it loses a race (unique violation, serialization failure, deadlock), with a short random pause; claims run `READ COMMITTED` through `Db.writeConditional`, since a conditional single-row update needs no more; `beginAttempt` locks the destination's row. `ConcurrencyTests` races eight writers on intake, contract ingest, provisioning, resend and claims, on both engines, and each guarantee was shown to fail with its mechanism removed: without the retry, intake, ingest and provisioning hit unique violations; without `SERIALIZABLE`, eight concurrent resends made eight deliveries; without the row lock, a pause committed mid-attempt. Ten repeated runs on PostgreSQL clean. Found: a destination's send slots are a soft limit under concurrent claims (fairness, not correctness; concurrency.md §7) |
| 4 | **Done.** The API and importer default to delivery's PostgreSQL (`Db.defaultPath`); `DB_PATH` still takes a SQLite file. A PostgreSQL database that doesn't exist is created on open. The scenario runner gives each scenario its own database and drops it after (`LeadIntegrations.Api --drop-database`, which refuses the lab's own); `--sqlite` keeps files. Test control refuses either lab database. All 36 lab scenarios pass on PostgreSQL (and on SQLite); `tools/acceptance_m3.py` passes against an API on PostgreSQL (65 leads, each delivered once). Found: the API's startup line printed the connection string, password included; it now prints `postgres user@host:port/database`. The lab's existing SQLite data is not copied: re-import it |
| 5 | **Done**, with option 1 below. The API and the importer refuse SQLite; the test suite runs on PostgreSQL, a database per test; the tests that read SQLite files now read PostgreSQL (credentials checked in every row; the atomicity trigger a PL/pgSQL function; an older database upgraded on PostgreSQL). SQLite stays only in `Db` as the interleaving explorer's model. The importer still reads a legacy SQLite file as a source |

**Settled 2026-11-25: option 1.** The explorer keeps SQLite as its model of the store's logic; its
SQL is the same on both engines, and PostgreSQL's real concurrency is covered by the race tests
(`ConcurrencyTests`).

**Was open: the interleaving explorer.** `InterleavingTests` explores every interleaving of two
workers, an operator and a receiver by cloning an in-memory SQLite database at each of over a
thousand states (SQLite's backup API). PostgreSQL has nothing as cheap. Step 5 needs a choice:
keep SQLite in the tests as the explorer's model of the store's logic (its SQL is now the same on
both), or explore fewer states on PostgreSQL with databases cloned from templates.

## 4. Risks

| Risk | Answer |
|---|---|
| A race that SQLite's single writer hid | Step 3 runs the existing race tests against PostgreSQL before it becomes the default |
| Tests much slower with a database per test | A template database is a file copy on the server; measure in step 2 and share one database per test module if it isn't fast enough |
| Developers without Docker | `backend/compose.yaml` is one command; any PostgreSQL 16 works through the connection string |

## 5. To approve

1. The five steps, in order, each passing everything before the next.
2. Delivery's own database on its own server in development (`backend/compose.yaml`, local port 55433), separate from the center's.
3. SQLite removed from delivery's store at the end (step 5), not kept as a second backend.
