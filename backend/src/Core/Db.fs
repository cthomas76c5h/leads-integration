/// The store: plain SQL, positional parameters ($1, $2, …), explicit column readers. Delivery's store
/// is PostgreSQL (RFC-011; the suite standardizes on it, S-8), chosen by a connection string
/// ("Host=…"). The same SQL also runs on SQLite, kept for one use only: the interleaving explorer's
/// model of the store (tests/Tests/InterleavingTests.fs), which clones in-memory SQLite databases at
/// every state, something PostgreSQL can't do cheaply. The API and the importer refuse SQLite.
module LeadIntegrations.Db

open System
open System.Data.Common
open System.Threading
open Microsoft.Data.Sqlite
open Npgsql

type Engine =
    | Sqlite
    | Postgres

/// A PostgreSQL connection string, in Npgsql's keyword form ("Host=…;Database=…").
let isPostgres (address: string) =
    not (isNull address) && address.Contains("Host=", StringComparison.OrdinalIgnoreCase)

type Database(path: string, connectionString: string, engine: Engine) =
    let writeLock = new SemaphoreSlim(1, 1)
    new(path: string) =
        if isPostgres path then Database(path, path, Postgres)
        else
            Database(
                path,
                SqliteConnectionStringBuilder(DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Private, Pooling = true)
                    .ConnectionString,
                Sqlite
            )
    /// A private database in memory (tests): it lives while at least one connection to it stays open.
    static member InMemory(name: string) =
        Database(name, SqliteConnectionStringBuilder(DataSource = name, Mode = SqliteOpenMode.Memory, Cache = SqliteCacheMode.Shared, Pooling = false).ConnectionString, Sqlite)
    /// Where it is: the SQLite file, or the PostgreSQL connection string.
    member _.Path = path
    member _.ConnectionString = connectionString
    member _.Engine = engine
    member _.WriteLock = writeLock
    /// Failure injection (RFC-008 §10, test control only): every write fails as if the database had
    /// stopped answering, before its transaction begins. Reads still work, so a scenario can look.
    member val WritesFail = false with get, set

let private open' (db: Database) : DbConnection =
    match db.Engine with
    | Postgres ->
        let conn = new NpgsqlConnection(db.ConnectionString)
        conn.Open()
        conn
    | Sqlite ->
        let conn = new SqliteConnection(db.ConnectionString)
        conn.Open()
        use pragma = conn.CreateCommand()
        pragma.CommandText <- "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;"
        pragma.ExecuteNonQuery() |> ignore
        conn

let private command (conn: DbConnection) (tx: DbTransaction) (sql: string) (args: obj list) =
    let cmd = conn.CreateCommand()
    cmd.CommandText <- sql
    if not (isNull tx) then cmd.Transaction <- tx
    let positional = conn :? NpgsqlConnection
    args
    |> List.iteri (fun i a ->
        let v: obj =
            match a with
            | null -> box DBNull.Value
            | :? bool as b -> box (if b then 1L else 0L)
            | :? int as n -> box (int64 n)
            | v -> v
        let p = cmd.CreateParameter()
        // Npgsql binds $1, $2, … by position when parameters have no names; SQLite by name.
        if not positional then p.ParameterName <- $"${i + 1}"
        p.Value <- v
        cmd.Parameters.Add p |> ignore)
    cmd

let private readAll (cmd: DbCommand) (map: DbDataReader -> 'T) =
    use r = cmd.ExecuteReader()
    let rows = ResizeArray<'T>()
    while r.Read() do
        rows.Add(map r)
    rows.ToArray()

/// Statements against one connection, optionally inside a transaction.
type Session(conn: DbConnection, tx: DbTransaction) =
    /// On PostgreSQL, where writers run concurrently and a row lock is sometimes needed.
    member _.IsPostgres = conn :? NpgsqlConnection

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

/// A read. Readers never block writers (WAL on SQLite; MVCC on PostgreSQL).
let read (db: Database) (f: Session -> 'T) : 'T =
    use conn = open' db
    f (Session(conn, null))

/// The database could not take a write: nothing in that transaction was committed.
exception Unavailable of string

/// How many times a PostgreSQL write is tried when it loses a race (RFC-011 step 3).
let private attempts = 10

/// A race lost to another writer, which running the transaction again settles: a unique index
/// took the other writer's row (23505), or the database chose this transaction to give way
/// (serialization failure 40001, deadlock 40P01).
let private lostRace (e: exn) =
    match e with
    | :? PostgresException as p -> p.SqlState = "23505" || p.SqlState = "40001" || p.SqlState = "40P01"
    | _ -> false

let private backoff = new ThreadLocal<Random>(fun () -> Random())

let private run (db: Database) (isolation: Data.IsolationLevel) (f: Session -> 'T) : 'T =
    if db.WritesFail then raise (Unavailable "the database is not answering")
    let once () =
        use conn = open' db
        use tx = if db.Engine = Postgres then conn.BeginTransaction isolation else conn.BeginTransaction()
        let result = f (Session(conn, tx))
        tx.Commit()
        result
    match db.Engine with
    | Sqlite ->
        db.WriteLock.Wait()
        try once () finally db.WriteLock.Release() |> ignore
    | Postgres ->
        let rec go n =
            try once ()
            with e when n < attempts && lostRace e ->
                // A short random pause, growing with each try, so racers don't collide again in step.
                Thread.Sleep(backoff.Value.Next(1, 5 * n + 1))
                go (n + 1)
        go 1

/// A write in one transaction.
///
/// SQLite: serialised with every other write in this process (one writer at a time).
/// PostgreSQL: writers run concurrently (RFC-011 step 3), each transaction SERIALIZABLE, so the
/// result is as if they had run one at a time, as on SQLite: a check followed by a write can't be
/// overtaken. Conditional updates guard claims and leases, and unique indexes anything recorded
/// once. A write that loses a race is run again from the start, in a new transaction, and sees what
/// the other writer committed: a check-then-insert then answers as a repeat. The function must
/// therefore keep its effects inside the transaction, as every write here does.
let write (db: Database) (f: Session -> 'T) : 'T = run db Data.IsolationLevel.Serializable f

/// A write whose correctness rests only on conditional single-row updates (a claim: `UPDATE …
/// WHERE <still claimable>`), so it needs no serializability. On PostgreSQL it runs READ COMMITTED,
/// where a second writer waits for the first and then re-checks the row: of two claims, one wins.
/// Under SERIALIZABLE, many workers claiming from one queue would mostly abort each other.
let writeConditional (db: Database) (f: Session -> 'T) : 'T = run db Data.IsolationLevel.ReadCommitted f

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

-- A lead's classification records (RFC-008 §6.2): the one made at intake, then each correction,
-- which supersedes the one before it. None is ever changed or deleted.
CREATE TABLE IF NOT EXISTS classifications (
  event_id TEXT NOT NULL REFERENCES events(id),
  seq INTEGER NOT NULL,
  category TEXT NOT NULL,
  practice_area TEXT NULL,
  source TEXT NOT NULL,        -- intake | reviewer | firm-contact
  by TEXT NULL,
  at INTEGER NOT NULL,
  taxonomy_version INTEGER NOT NULL,
  PRIMARY KEY (event_id, seq)
);

-- Each conversation as a closed cycle rated it (RFC-003): never changed after.
CREATE TABLE IF NOT EXISTS usage (
  event_id TEXT PRIMARY KEY REFERENCES events(id),
  cycle_until INTEGER NOT NULL,
  billable INTEGER NOT NULL,
  rule TEXT NULL,
  rated_at INTEGER NOT NULL
);

-- A correction after the cycle closed that lowers its charge: credited in the next cycle.
CREATE TABLE IF NOT EXISTS credits (
  event_id TEXT PRIMARY KEY REFERENCES events(id),
  cycle_from INTEGER NOT NULL,
  reason TEXT NOT NULL,
  at INTEGER NOT NULL
);

-- People at a firm who sign in to the firm portal (RFC-008 §9). The token is shown once, kept as a hash.
CREATE TABLE IF NOT EXISTS firm_users (
  id TEXT PRIMARY KEY,
  firm_id TEXT NOT NULL REFERENCES firms(id),
  name TEXT NOT NULL,
  role TEXT NOT NULL,          -- admin | member
  token_hash TEXT NOT NULL UNIQUE,
  created_at INTEGER NOT NULL
);

-- Every refused request at a firm boundary (RFC-008 §9): who asked, as which firm, for what.
CREATE TABLE IF NOT EXISTS access_audit (
  seq INTEGER PRIMARY KEY AUTOINCREMENT,
  at INTEGER NOT NULL,
  user_id TEXT NOT NULL,
  firm_id TEXT NOT NULL,       -- the requester's firm
  boundary TEXT NOT NULL,      -- read | list | export | resend
  target TEXT NULL,            -- the lead or firm asked for
  outcome TEXT NOT NULL        -- refused
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

-- A lead taken in a versioned contract (intake-lead/draft-2), kept exactly as it came, written in the
-- same transaction as its event and deliveries. One per firm and interaction; one per firm and lead id.
CREATE TABLE IF NOT EXISTS lead_contracts (
  event_id TEXT PRIMARY KEY REFERENCES events(id),
  firm_id TEXT NOT NULL REFERENCES firms(id),
  contract TEXT NOT NULL,
  lead_id TEXT NOT NULL,
  interaction_id TEXT NOT NULL,
  sender_firm_id TEXT NOT NULL,
  policy_revision INTEGER NOT NULL,
  policy_approved INTEGER NOT NULL,
  payload TEXT NOT NULL,
  payload_hash TEXT NOT NULL,
  received_at INTEGER NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS lead_contracts_interaction ON lead_contracts(firm_id, interaction_id);
CREATE UNIQUE INDEX IF NOT EXISTS lead_contracts_lead ON lead_contracts(firm_id, lead_id);

-- One interaction's usage facts as the call center published them (usage/1, C6): immutable, one per
-- firm and interaction. Rating reads its measured duration; it never says what is billable (S-3).
CREATE TABLE IF NOT EXISTS usage_facts (
  firm_id TEXT NOT NULL REFERENCES firms(id),
  interaction_id TEXT NOT NULL,
  contract TEXT NOT NULL,
  revision INTEGER NULL,
  started_at INTEGER NOT NULL,
  ended_at INTEGER NOT NULL,
  duration_ms INTEGER NOT NULL,
  result TEXT NOT NULL,
  answered_by TEXT NULL,
  payload TEXT NOT NULL,
  payload_hash TEXT NOT NULL,
  received_at INTEGER NOT NULL,
  PRIMARY KEY (firm_id, interaction_id)
);

-- Until the suite shares firm ids: a sender's firm id, mapped by hand to a firm here.
CREATE TABLE IF NOT EXISTS firm_aliases (
  alias TEXT PRIMARY KEY,
  firm_id TEXT NOT NULL REFERENCES firms(id),
  created_at INTEGER NOT NULL
);
"""

/// The repository root (…/lead-integrations), from this file's location, so the API and the
/// importer agree on default paths whatever their working directory. DB_PATH overrides.
let repoRoot = IO.Path.GetFullPath(IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))

/// The lab's database: delivery's own PostgreSQL (backend/compose.yaml; RFC-011). DB_PATH overrides
/// it with another connection string.
let defaultPath = "Host=127.0.0.1;Port=55433;Username=delivery;Password=delivery;Database=delivery"

/// Where a database is, for a log line: never its password (credentials are never shown).
let describe (address: string) =
    if isPostgres address then
        let b = NpgsqlConnectionStringBuilder address
        $"postgres {b.Username}@{b.Host}:{b.Port}/{b.Database}"
    else address

/// The same server and database, however the connection string is written.
let sameDatabase (a: string) (b: string) =
    if isPostgres a && isPostgres b then
        let x = NpgsqlConnectionStringBuilder a
        let y = NpgsqlConnectionStringBuilder b
        String.Equals(x.Host, y.Host, StringComparison.OrdinalIgnoreCase) && x.Port = y.Port && x.Database = y.Database
    elif not (isPostgres a) && not (isPostgres b) then
        IO.Path.GetFullPath a = IO.Path.GetFullPath b
    else false

/// Runs one statement against the server's maintenance database (creating or dropping a database
/// can't be done from inside it).
let private onServer (address: string) (sql: string) (args: obj list) =
    let admin = NpgsqlConnectionStringBuilder(address, Database = "postgres", Pooling = false)
    use conn = new NpgsqlConnection(admin.ConnectionString)
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- sql
    for a in args do
        cmd.Parameters.Add(NpgsqlParameter(Value = a)) |> ignore
    cmd.ExecuteScalar()

let private databaseName (address: string) =
    let name = NpgsqlConnectionStringBuilder(address).Database
    if isNull name || not (Text.RegularExpressions.Regex.IsMatch(name, "^[a-z_][a-z0-9_]{0,62}$")) then
        failwith $"database name {name}: lowercase letters, digits and _ only"
    name

/// Creates the PostgreSQL database the address names if it doesn't exist yet.
let ensureDatabase (address: string) =
    let name = databaseName address
    if isNull (onServer address "SELECT 1 FROM pg_database WHERE datname = $1" [ name ]) then
        try onServer address $"CREATE DATABASE {name}" [] |> ignore
        with :? PostgresException as e when e.SqlState = "42P04" -> () // created meanwhile by another process

/// Drops the PostgreSQL database the address names, closing its connections. Never the lab's own.
let dropDatabase (address: string) =
    if sameDatabase address defaultPath then failwith "refusing to drop the lab's database"
    let name = databaseName address
    NpgsqlConnection.ClearAllPools()
    onServer address $"DROP DATABASE IF EXISTS {name} WITH (FORCE)" [] |> ignore

/// The schema in PostgreSQL's terms, from the one schema above (RFC-011 step 2): UTC milliseconds and
/// counts in 64 bits, identities for SQLite's auto-numbered keys, a `rowid` where queries break
/// ties by it, case-insensitive name order by lower(name), and the append-only rule as a trigger
/// function.
let private postgresSchema =
    lazy
        (let lines = schema.Split('\n') |> Array.filter (fun l -> not (l.TrimStart().StartsWith "CREATE TRIGGER"))
         let text = String.Join("\n", lines)
         let text = text.Replace("INTEGER PRIMARY KEY AUTOINCREMENT", "BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY")
         let text = Text.RegularExpressions.Regex.Replace(text, @"\bINTEGER\b", "BIGINT")
         let text = Text.RegularExpressions.Regex.Replace(text, @"\bREAL\b", "DOUBLE PRECISION")
         let text = text.Replace("firms(name COLLATE NOCASE)", "firms(lower(name))")
         let text =
             [ "approvals"; "setup_events" ]
             |> List.fold
                 (fun (t: string) table ->
                     t.Replace($"CREATE TABLE IF NOT EXISTS {table} (", $"CREATE TABLE IF NOT EXISTS {table} (\n  rowid BIGINT GENERATED ALWAYS AS IDENTITY,"))
                 text
         text
         + """
CREATE OR REPLACE FUNCTION append_only() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN RAISE EXCEPTION '% are append-only', TG_TABLE_NAME; END $$;
DROP TRIGGER IF EXISTS approvals_append_only ON approvals;
CREATE TRIGGER approvals_append_only BEFORE UPDATE OR DELETE ON approvals FOR EACH ROW EXECUTE FUNCTION append_only();
""")

let openDatabase (path: string) : Database =
    let db =
        if isPostgres path then
            ensureDatabase path
            Database(path)
        else
            let full = IO.Path.GetFullPath path
            IO.Directory.CreateDirectory(IO.Path.GetDirectoryName full) |> ignore
            Database(full)
    use conn = open' db
    if db.Engine = Sqlite then
        use wal = conn.CreateCommand()
        wal.CommandText <- "PRAGMA journal_mode = WAL;"
        wal.ExecuteNonQuery() |> ignore
    use cmd = conn.CreateCommand()
    cmd.CommandText <- (if db.Engine = Postgres then postgresSchema.Value else schema)
    cmd.ExecuteNonQuery() |> ignore
    // Columns added after a database was first created.
    for table, column, definition in
        [ "firms", "time_zone", "TEXT NOT NULL DEFAULT 'America/New_York'"
          "deliveries", "decision", "TEXT NULL"
          "deliveries", "lease_owner", "TEXT NULL"
          "deliveries", "lease_until", "INTEGER NULL"
          "deliveries", "lease_version", "INTEGER NOT NULL DEFAULT 0"
          "deliveries", "resend", "TEXT NULL" // who resent it, as what, and why (JSON)
          "deliveries", "resolution", "TEXT NULL" // how an operator settled an unknown outcome (JSON)
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
          "events", "conversation_id", "TEXT NULL"
          // The firm's canonical id and billing account, as the provisioning boundary gave them (suite S-2).
          "firms", "canonical_id", "TEXT NULL"
          "firms", "account_id", "TEXT NULL"
          "firms", "provisioned_at", "INTEGER NULL"
          "firms", "provisioning_updated_at", "INTEGER NULL"
          // Draft-3 publication metadata (request headers): never part of the payload or its hash.
          "lead_contracts", "first_published_at", "INTEGER NULL"
          "lead_contracts", "last_published_at", "INTEGER NULL"
          "lead_contracts", "publication_attempts", "INTEGER NULL" ] do
        match db.Engine with
        | Postgres ->
            use alter = conn.CreateCommand()
            let typed = definition.Replace("INTEGER", "BIGINT")
            alter.CommandText <- $"ALTER TABLE {table} ADD COLUMN IF NOT EXISTS {column} {typed}"
            alter.ExecuteNonQuery() |> ignore
        | Sqlite ->
            // SQLite has no ADD COLUMN IF NOT EXISTS.
            use probe = conn.CreateCommand()
            probe.CommandText <- $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'"
            if Convert.ToInt64(probe.ExecuteScalar()) = 0L then
                use alter = conn.CreateCommand()
                alter.CommandText <- $"ALTER TABLE {table} ADD COLUMN {column} {definition}"
                alter.ExecuteNonQuery() |> ignore
    // The legacy source's code was renamed; rows imported before keep working.
    use renamed = conn.CreateCommand()
    renamed.CommandText <- "UPDATE firm_sources SET system = 'legacy' WHERE system = 'api2'; UPDATE connections SET source = 'legacy' WHERE source = 'api2'"
    renamed.ExecuteNonQuery() |> ignore
    use canonical = conn.CreateCommand()
    canonical.CommandText <- "CREATE UNIQUE INDEX IF NOT EXISTS firms_canonical ON firms(canonical_id) WHERE canonical_id IS NOT NULL"
    canonical.ExecuteNonQuery() |> ignore
    use unique = conn.CreateCommand()
    unique.CommandText <- "CREATE UNIQUE INDEX IF NOT EXISTS events_conversation ON events(firm_id, conversation_id) WHERE conversation_id IS NOT NULL"
    unique.ExecuteNonQuery() |> ignore
    db
