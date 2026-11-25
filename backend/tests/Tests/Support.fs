module LeadIntegrations.Tests.Support

open System
open System.Collections.Generic
open LeadIntegrations
open LeadIntegrations.Domain
open LeadIntegrations.Import

/// The PostgreSQL server the suite runs against, as an Npgsql connection string to a database it may
/// create others from (RFC-011 step 2): `DELIVERY_TEST_PG="Host=127.0.0.1;Port=55433;Username=delivery;
/// Password=delivery;Database=delivery"` (backend/compose.yaml). Unset: every test uses SQLite files.
let postgres = Environment.GetEnvironmentVariable "DELIVERY_TEST_PG" |> Option.ofObj |> Option.filter (String.IsNullOrWhiteSpace >> not)

let onPostgres = postgres.IsSome

let private admin (sql: string) =
    use c = new Npgsql.NpgsqlConnection(postgres.Value)
    c.Open()
    use cmd = c.CreateCommand()
    cmd.CommandText <- sql
    cmd.ExecuteNonQuery() |> ignore

/// A fresh database, removed when disposed: a SQLite file, or with DELIVERY_TEST_PG a database of its
/// own on that server.
type TempDb() =
    let pgName = if onPostgres then Some $"t_{Guid.NewGuid():N}" else None
    let db =
        match pgName with
        | Some name ->
            admin $"CREATE DATABASE {name}"
            let cs = Npgsql.NpgsqlConnectionStringBuilder(postgres.Value, Database = name, Pooling = true)
            Db.openDatabase cs.ConnectionString
        | None -> Db.openTemp ()
    member _.Db = db
    interface IDisposable with
        member _.Dispose() =
            match pgName with
            | Some name ->
                Npgsql.NpgsqlConnection.ClearAllPools()
                try admin $"DROP DATABASE IF EXISTS {name} WITH (FORCE)" with _ -> ()
            | None ->
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
                for suffix in [ ""; "-wal"; "-shm" ] do
                    try
                        IO.File.Delete(db.Path + suffix)
                    with _ -> ()

/// A test that depends on SQLite itself (its files, its backup API): skipped on PostgreSQL.
let sqliteOnly (name: string) (body: unit -> unit) =
    Expecto.Tests.test name {
        if onPostgres then Expecto.Tests.skiptest "depends on SQLite itself" else body ()
    }

let origin = "http://localhost"

let firm (sourceId: string) (name: string) (key: string) (conns: ImportedConnection list) : ImportedFirm =
    { SourceId = sourceId
      Name = name
      Active = true
      KeyHash = Secrets.matchHash key
      KeyFingerprint = Secrets.fingerprint key
      ContactTypes = [||]
      CustomLabels = [| "Date of accident?"; "Were you injured?" |]
      Connections = Array.ofList conns }

let conn (sourceRef: string) (kind: string) (creds: (string * string) list) : ImportedConnection =
    { SourceRef = sourceRef
      Kind = kind
      Name = Catalog.label kind
      Credentials = creds |> List.map (fun (n, v) -> credential kind n v) |> Array.ofList
      Settings = Dictionary()
      Enabled = true }

let batch (system: string) (firms: ImportedFirm list) : Batch =
    { System = system
      Firms = Array.ofList firms
      Orphans = [||]
      Skipped = 0
      Notes = [] }

let sampleLead () =
    Synthetic.lead "Test Firm" "full" 0 null false [| "Date of accident?"; "Were you injured?" |]
