module LeadIntegrations.Tests.Support

open System
open System.Collections.Generic
open LeadIntegrations
open LeadIntegrations.Domain
open LeadIntegrations.Import

/// The PostgreSQL server the suite runs against (RFC-011): delivery's own, from backend/compose.yaml
/// (`docker compose -f backend/compose.yaml up -d`), or another named by DELIVERY_TEST_PG. Each test
/// takes a database of its own there. SQLite remains only as the interleaving explorer's model of
/// the store (InterleavingTests), with its own in-memory databases.
let postgres =
    Environment.GetEnvironmentVariable "DELIVERY_TEST_PG"
    |> Option.ofObj
    |> Option.filter (String.IsNullOrWhiteSpace >> not)
    |> Option.defaultValue Db.defaultPath

/// A statement against the server itself (creating or dropping a database).
let admin (sql: string) =
    use c = new Npgsql.NpgsqlConnection(Npgsql.NpgsqlConnectionStringBuilder(postgres, Database = "postgres").ConnectionString)
    c.Open()
    use cmd = c.CreateCommand()
    cmd.CommandText <- sql
    cmd.ExecuteNonQuery() |> ignore

/// The address of a database on the test server.
let addressOf (name: string) = Npgsql.NpgsqlConnectionStringBuilder(postgres, Database = name, Pooling = true).ConnectionString

/// A fresh PostgreSQL database of its own, dropped when disposed.
type TempDb() =
    let name = $"t_{Guid.NewGuid():N}"
    let db =
        admin $"CREATE DATABASE {name}"
        Db.openDatabase (addressOf name)
    member _.Db = db
    interface IDisposable with
        member _.Dispose() =
            Npgsql.NpgsqlConnection.ClearAllPools()
            try admin $"DROP DATABASE IF EXISTS {name} WITH (FORCE)" with _ -> ()

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
