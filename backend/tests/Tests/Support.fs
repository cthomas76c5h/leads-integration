module LeadIntegrations.Tests.Support

open System
open System.Collections.Generic
open LeadIntegrations
open LeadIntegrations.Domain
open LeadIntegrations.Import

/// A fresh database file, deleted when disposed.
type TempDb() =
    let db = Db.openTemp ()
    member _.Db = db
    interface IDisposable with
        member _.Dispose() =
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
            for suffix in [ ""; "-wal"; "-shm" ] do
                try
                    IO.File.Delete(db.Path + suffix)
                with _ -> ()

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
