/// Thin SQLite helpers, in the style of chatbot/backend-fsharp Db.fs: plain
/// SQL, positional parameters, explicit column readers. One process owns the
/// file; writes go through one lock so the delivery worker and the API never
/// meet SQLITE_BUSY.
module LeadIntegrations.Db

open System
open System.Data.Common
open System.Threading
open Microsoft.Data.Sqlite

type Database(path: string) =
    let cs =
        SqliteConnectionStringBuilder(DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Private, Pooling = true)
            .ConnectionString
    let writeLock = new SemaphoreSlim(1, 1)
    member _.Path = path
    member _.ConnectionString = cs
    member _.WriteLock = writeLock
    /// Failure injection (RFC-008 §10, test control only): every write fails as if the database had
    /// stopped answering, before its transaction begins. Reads still work, so a scenario can look.
    member val WritesFail = false with get, set

let private open' (db: Database) =
    let conn = new SqliteConnection(db.ConnectionString)
    conn.Open()
    use pragma = conn.CreateCommand()
    pragma.CommandText <- "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;"
    pragma.ExecuteNonQuery() |> ignore
    conn

let private command (conn: SqliteConnection) (tx: SqliteTransaction) (sql: string) (args: obj list) =
    let cmd = conn.CreateCommand()
    cmd.CommandText <- sql
    if not (isNull tx) then cmd.Transaction <- tx
    args
    |> List.iteri (fun i a ->
        let v: obj =
            match a with
            | null -> box DBNull.Value
            | :? bool as b -> box (if b then 1L else 0L)
            | v -> v
        cmd.Parameters.AddWithValue($"${i + 1}", v) |> ignore)
    cmd

let private readAll (cmd: SqliteCommand) (map: DbDataReader -> 'T) =
    use r = cmd.ExecuteReader()
    let rows = ResizeArray<'T>()
    while r.Read() do
        rows.Add(map r)
    rows.ToArray()

/// Statements against one connection, optionally inside a transaction.
type Session(conn: SqliteConnection, tx: SqliteTransaction) =
    member _.Query (sql: string) (args: obj list) (map: DbDataReader -> 'T) : 'T[] =
        use cmd = command conn tx sql args
        readAll cmd map

    member this.TryOne sql args map = this.Query sql args map |> Array.tryHead

    member _.Execute (sql: string) (args: obj list) : int =
        use cmd = command conn tx sql args
        cmd.ExecuteNonQuery()

    member _.Scalar (sql: string) (args: obj list) : int64 =
        use cmd = command conn tx sql args
        match cmd.ExecuteScalar() with
        | null -> 0L
        | :? DBNull -> 0L
        | v -> Convert.ToInt64 v

/// A read. Readers never block writers (WAL).
let read (db: Database) (f: Session -> 'T) : 'T =
    use conn = open' db
    f (Session(conn, null))

/// The database could not take a write: nothing in that transaction was committed.
exception Unavailable of string

/// A write in one transaction, serialised with every other write.
let write (db: Database) (f: Session -> 'T) : 'T =
    if db.WritesFail then raise (Unavailable "the database is not answering")
    db.WriteLock.Wait()
    try
        use conn = open' db
        use tx = conn.BeginTransaction()
        let result = f (Session(conn, tx))
        tx.Commit()
        result
    finally
        db.WriteLock.Release() |> ignore

module Read =
    let str (r: DbDataReader) (i: int) : string = if r.IsDBNull i then null else r.GetString i
    let int64 (r: DbDataReader) (i: int) = r.GetInt64 i
    let int (r: DbDataReader) (i: int) = Convert.ToInt32(r.GetInt64 i)
    let bool (r: DbDataReader) (i: int) = r.GetInt64 i <> 0L
    let int64N (r: DbDataReader) (i: int) : Nullable<int64> = if r.IsDBNull i then Nullable() else Nullable(r.GetInt64 i)
    let intN (r: DbDataReader) (i: int) : Nullable<int> = if r.IsDBNull i then Nullable() else Nullable(Convert.ToInt32(r.GetInt64 i))

let private schema =
    """
CREATE TABLE IF NOT EXISTS firms (
  id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  key_hash TEXT NULL,            -- salted hash of the Answering Legal key: the cross-system match
  key_fingerprint TEXT NULL,
  active INTEGER NOT NULL DEFAULT 1,
  contact_types TEXT NOT NULL DEFAULT '[]',
  custom_labels TEXT NOT NULL DEFAULT '[]',
  time_zone TEXT NOT NULL DEFAULT 'America/New_York', -- IANA name: office hours and the dates the firm reads
  created_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS firms_key ON firms(key_hash);
CREATE INDEX IF NOT EXISTS firms_name ON firms(name COLLATE NOCASE);

CREATE TABLE IF NOT EXISTS firm_sources (
  system TEXT NOT NULL,
  source_id TEXT NOT NULL,
  firm_id TEXT NOT NULL REFERENCES firms(id),
  name TEXT NOT NULL,
  active INTEGER NOT NULL,
  PRIMARY KEY (system, source_id)
);
CREATE INDEX IF NOT EXISTS firm_sources_firm ON firm_sources(firm_id);

CREATE TABLE IF NOT EXISTS connections (
  id TEXT PRIMARY KEY,
  firm_id TEXT NOT NULL REFERENCES firms(id),
  kind TEXT NOT NULL,
  name TEXT NOT NULL,
  source TEXT NOT NULL,
  source_ref TEXT NULL,
  credentials TEXT NOT NULL DEFAULT '[]',   -- CredentialField[]: present + fingerprint, never values
  settings TEXT NOT NULL DEFAULT '{}',
  enabled INTEGER NOT NULL DEFAULT 1,
  status TEXT NOT NULL DEFAULT 'active',
  disabled_reason TEXT NULL,
  consecutive_failures INTEGER NOT NULL DEFAULT 0,
  delivery_url TEXT NOT NULL,
  signing_secret TEXT NOT NULL,             -- ours, generated; signs webhook bodies
  template TEXT NULL,                       -- the active mapping body ('webhook' only)
  created_at INTEGER NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS connections_source ON connections(source, source_ref) WHERE source_ref IS NOT NULL;
CREATE INDEX IF NOT EXISTS connections_firm ON connections(firm_id);

CREATE TABLE IF NOT EXISTS events (
  id TEXT PRIMARY KEY,
  firm_id TEXT NOT NULL REFERENCES firms(id),
  test INTEGER NOT NULL,
  lead TEXT NOT NULL,
  created_at INTEGER NOT NULL
);

-- The outbox: a delivery row is written with its event, before any sending.
CREATE TABLE IF NOT EXISTS deliveries (
  id TEXT PRIMARY KEY,
  event_id TEXT NOT NULL REFERENCES events(id),
  connection_id TEXT NOT NULL REFERENCES connections(id),
  test INTEGER NOT NULL,
  status TEXT NOT NULL,
  reason TEXT NULL,
  template TEXT NULL,          -- set on a test of a draft revision: the body under test
  has_template INTEGER NOT NULL DEFAULT 0,
  next_attempt_at INTEGER NULL,
  replay_of TEXT NULL,
  created_at INTEGER NOT NULL,
  decision TEXT NULL,          -- RoutingDecision JSON: why this lead goes, or not, to this destination
  lease_owner TEXT NULL,       -- the worker holding the claim while status = 'delivering' (RFC-008 §5.3)
  lease_until INTEGER NULL,    -- when that claim runs out
  lease_version INTEGER NOT NULL DEFAULT 0  -- increased by every claim: only its holder may start or finish an attempt
);

-- Append-only: what happened, in order (docs/contracts/events.json). Read by scenarios and audits.
CREATE TABLE IF NOT EXISTS domain_events (
  seq INTEGER PRIMARY KEY AUTOINCREMENT,
  at INTEGER NOT NULL,
  name TEXT NOT NULL,
  firm_id TEXT NULL,
  event_id TEXT NULL,          -- the lead
  connection_id TEXT NULL      -- the destination
);
CREATE INDEX IF NOT EXISTS deliveries_due ON deliveries(status, next_attempt_at);
CREATE INDEX IF NOT EXISTS deliveries_created ON deliveries(created_at);
CREATE INDEX IF NOT EXISTS deliveries_event ON deliveries(event_id, connection_id);
CREATE INDEX IF NOT EXISTS events_created ON events(created_at);

CREATE TABLE IF NOT EXISTS attempts (
  delivery_id TEXT NOT NULL REFERENCES deliveries(id),
  n INTEGER NOT NULL,
  at INTEGER NOT NULL,
  duration_ms INTEGER NOT NULL,
  url TEXT NOT NULL,
  status INTEGER NULL,
  error TEXT NULL,
  outcome TEXT NOT NULL,
  request_body TEXT NOT NULL,
  response_snippet TEXT NOT NULL,
  next_attempt_in_ms INTEGER NULL,
  PRIMARY KEY (delivery_id, n)
);

-- The fake CRMs' own records (Receivers.fs): what a lookup by our lead reference finds.
CREATE TABLE IF NOT EXISTS receiver_records (
  kind TEXT NOT NULL,
  dest TEXT NOT NULL,          -- the scenario's destination key; '' outside scenarios
  ref TEXT NOT NULL,           -- our lead reference (the webhook-id header)
  at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS received (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  at INTEGER NOT NULL,
  path TEXT NOT NULL,
  kind TEXT NULL,
  mode TEXT NOT NULL,
  answered INTEGER NOT NULL,
  verdict TEXT NOT NULL,
  body TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS setups (
  id TEXT PRIMARY KEY,
  firm_id TEXT NOT NULL REFERENCES firms(id),
  token TEXT NOT NULL UNIQUE,
  kind TEXT NOT NULL,
  recipe TEXT NOT NULL,
  rows TEXT NOT NULL,
  version INTEGER NOT NULL,
  last_version INTEGER NOT NULL,
  sent_version INTEGER NULL,
  connection_id TEXT NULL,
  test_delivery_id TEXT NULL,
  client_confirmed_at INTEGER NULL,
  active TEXT NULL,               -- ActiveRevision JSON: what real leads use
  created_at INTEGER NOT NULL
);

-- Append-only: approvals and history are never updated or deleted.
CREATE TABLE IF NOT EXISTS approvals (
  setup_id TEXT NOT NULL REFERENCES setups(id),
  version INTEGER NOT NULL,
  decision TEXT NOT NULL,
  by_name TEXT NOT NULL,
  comment TEXT NOT NULL,
  at INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS setup_events (
  setup_id TEXT NOT NULL REFERENCES setups(id),
  at INTEGER NOT NULL,
  who TEXT NOT NULL,
  what TEXT NOT NULL
);
CREATE TRIGGER IF NOT EXISTS approvals_append_only_u BEFORE UPDATE ON approvals BEGIN SELECT RAISE(ABORT, 'approvals are append-only'); END;
CREATE TRIGGER IF NOT EXISTS approvals_append_only_d BEFORE DELETE ON approvals BEGIN SELECT RAISE(ABORT, 'approvals are append-only'); END;

-- ----- Billing (docs/billing/strategy.md): lab data, synthetic -----
-- The price book: one row per plan version; a change makes a new version, never an edit.
CREATE TABLE IF NOT EXISTS price_book (
  id TEXT PRIMARY KEY,            -- plan@version, e.g. deliver-100@1
  plan TEXT NOT NULL,
  version INTEGER NOT NULL,
  tier TEXT NOT NULL,             -- answer | deliver | insight
  name TEXT NOT NULL,
  monthly_cents INTEGER NOT NULL,
  included INTEGER NOT NULL,      -- handled conversations a cycle
  overage_cents INTEGER NOT NULL, -- per conversation beyond the included ones
  effective_from INTEGER NOT NULL
);
-- One billing account per firm: its cycle anchor and, for the shadow comparison, the legacy terms.
CREATE TABLE IF NOT EXISTS billing_accounts (
  firm_id TEXT PRIMARY KEY REFERENCES firms(id),
  cycle_day INTEGER NOT NULL,     -- 1 or 15
  legacy_plan_minutes INTEGER NULL,
  legacy_plan_cents INTEGER NULL,
  legacy_overage TEXT NULL,       -- as the legacy system holds it: free text
  legacy_wrapup REAL NOT NULL,
  created_at INTEGER NOT NULL
);
-- Effective-dated: a plan change ends one row and starts another.
CREATE TABLE IF NOT EXISTS subscriptions (
  id TEXT PRIMARY KEY,
  firm_id TEXT NOT NULL REFERENCES firms(id),
  price_id TEXT NOT NULL REFERENCES price_book(id),
  starts_at INTEGER NOT NULL,
  ends_at INTEGER NULL,
  discount_pct INTEGER NOT NULL DEFAULT 0,
  discount_reason TEXT NULL,
  discount_until INTEGER NULL
);
CREATE INDEX IF NOT EXISTS subscriptions_firm ON subscriptions(firm_id, starts_at);
-- A closed cycle's statement, kept as it was: past cycles never change with live settings.
CREATE TABLE IF NOT EXISTS invoices (
  firm_id TEXT NOT NULL REFERENCES firms(id),
  cycle_from INTEGER NOT NULL,
  cycle_to INTEGER NOT NULL,
  statement TEXT NOT NULL,
  locked_at INTEGER NOT NULL,
  PRIMARY KEY (firm_id, cycle_from)
);

-- ----- Terms of service (docs/terms/strategy.md) -----
CREATE TABLE IF NOT EXISTS terms_versions (
  version INTEGER PRIMARY KEY,
  title TEXT NOT NULL,
  summary TEXT NOT NULL,          -- what changed, in plain words: shown in the portal notice
  body TEXT NOT NULL,
  change_kind TEXT NOT NULL,      -- editorial | pricing | material
  require TEXT NOT NULL,          -- none | admins | all: who must accept this version again
  published_at INTEGER NOT NULL,
  effective_at INTEGER NOT NULL,  -- acceptance blocks the portal only from here
  source TEXT NOT NULL            -- docx | text
);
-- Append-only: who accepted which version, when.
CREATE TABLE IF NOT EXISTS terms_acceptances (
  version INTEGER NOT NULL REFERENCES terms_versions(version),
  firm_id TEXT NOT NULL REFERENCES firms(id),
  user_id TEXT NOT NULL,
  role TEXT NOT NULL,             -- admin | member
  at INTEGER NOT NULL,
  PRIMARY KEY (version, firm_id, user_id)
);

CREATE TABLE IF NOT EXISTS import_runs (
  id TEXT PRIMARY KEY,
  system TEXT NOT NULL,
  at INTEGER NOT NULL,
  firms INTEGER NOT NULL,
  connections INTEGER NOT NULL,
  skipped INTEGER NOT NULL,
  notes TEXT NOT NULL
);
"""

/// The repository root (…/lead-integrations), from this file's location, so the API and the
/// importer agree on default paths whatever their working directory. DB_PATH overrides.
let repoRoot = IO.Path.GetFullPath(IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))

let defaultPath = IO.Path.Combine(repoRoot, "backend", "data", "lead-integrations.db")

let openDatabase (path: string) : Database =
    let full = IO.Path.GetFullPath path
    IO.Directory.CreateDirectory(IO.Path.GetDirectoryName full) |> ignore
    let db = Database(full)
    use conn = open' db
    use wal = conn.CreateCommand()
    wal.CommandText <- "PRAGMA journal_mode = WAL;"
    wal.ExecuteNonQuery() |> ignore
    use cmd = conn.CreateCommand()
    cmd.CommandText <- schema
    cmd.ExecuteNonQuery() |> ignore
    // Columns added after a database was first created (SQLite has no ADD COLUMN IF NOT EXISTS).
    for table, column, definition in
        [ "firms", "time_zone", "TEXT NOT NULL DEFAULT 'America/New_York'"
          "deliveries", "decision", "TEXT NULL"
          "deliveries", "lease_owner", "TEXT NULL"
          "deliveries", "lease_until", "INTEGER NULL"
          "deliveries", "lease_version", "INTEGER NOT NULL DEFAULT 0"
          "deliveries", "resend", "TEXT NULL" // who resent it, as what, and why (JSON)
          // Verification by lookup (RFC-008 §3.1): found, flagged as not found in time, last looked.
          "deliveries", "verified_at", "INTEGER NULL"
          "deliveries", "verify_overdue_at", "INTEGER NULL"
          "deliveries", "verify_checked_at", "INTEGER NULL"
          // The hand-off (RFC-008 §3.2): fixed at intake from the category, then what happened to it.
          "events", "ack_token", "TEXT NULL" // the lead's acknowledgement link; never in the operator API
          "events", "ack_required", "INTEGER NOT NULL DEFAULT 0"
          "events", "handoff_due_at", "INTEGER NULL"
          "events", "acknowledged_at", "INTEGER NULL"
          "events", "acknowledged_via", "TEXT NULL"
          "events", "escalated_at", "INTEGER NULL"
          "events", "expired_at", "INTEGER NULL"
          // The channel's conversation id (RFC-008 §4.1): one lead per conversation, whatever the channel repeats.
          "events", "conversation_id", "TEXT NULL" ] do
        use probe = conn.CreateCommand()
        probe.CommandText <- $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'"
        if Convert.ToInt64(probe.ExecuteScalar()) = 0L then
            use alter = conn.CreateCommand()
            alter.CommandText <- $"ALTER TABLE {table} ADD COLUMN {column} {definition}"
            alter.ExecuteNonQuery() |> ignore
    use unique = conn.CreateCommand()
    unique.CommandText <- "CREATE UNIQUE INDEX IF NOT EXISTS events_conversation ON events(firm_id, conversation_id) WHERE conversation_id IS NOT NULL"
    unique.ExecuteNonQuery() |> ignore
    db

/// A private in-memory-like database for tests: a temp file, deleted by the caller.
let openTemp () =
    openDatabase (IO.Path.Combine(IO.Path.GetTempPath(), $"lead-integrations-test-{Guid.NewGuid():N}.db"))
