/// The importers (src/Import) read each source system and hand over a Batch.
/// Credential *values* never reach a Batch: an importer turns each one into a
/// CredentialField (present + fingerprint) the moment it reads it.
///
/// Applying a batch is idempotent: firms are matched by (system, source id),
/// then across systems by the Answering Legal key hash; connections by
/// (source, source ref). Re-running an import updates instead of duplicating.
module LeadIntegrations.Import

open System
open System.Collections.Generic
open LeadIntegrations.Db
open LeadIntegrations.Domain

type ImportedConnection =
    { SourceRef: string
      Kind: string
      Name: string
      Credentials: CredentialField[]
      Settings: Dictionary<string, string>
      Enabled: bool }

type ImportedFirm =
    { SourceId: string
      Name: string
      Active: bool
      /// Secrets.matchHash of the firm's Answering Legal key, or null.
      KeyHash: string
      KeyFingerprint: string
      ContactTypes: ContactType[]
      CustomLabels: string[]
      Connections: ImportedConnection[] }

type Batch =
    { System: string
      Firms: ImportedFirm[]
      /// Connections whose firm is missing in the source (e.g. api-2 CRM rows with no client).
      Orphans: ImportedConnection[]
      Skipped: int
      Notes: string list }

/// Describe one credential without keeping it.
let credential (kind: string) (name: string) (value: string) : CredentialField =
    { Name = name
      Present = Secrets.isPresent value
      Fingerprint = Secrets.fingerprint value
      Meaning = Catalog.meaning kind name }

let settings (pairs: (string * string) list) =
    let d = Dictionary<string, string>()
    for k, v in pairs do
        if not (isNull v) then d[k] <- v
    d

let private union (a: 'T[]) (b: 'T[]) = Array.append a b |> Array.distinct

let private mergeContactTypes (a: ContactType[]) (b: ContactType[]) =
    Array.append a b
    |> Array.groupBy (fun t -> Dispatch.normalizeType t.Name)
    |> Array.map (fun (_, ts) -> { ts[0] with Active = ts |> Array.exists _.Active })

let private placeholderFirmId (system: string) = $"firm_orphans_{system}"

let apply (db: Database) (origin: string) (batch: Batch) : ImportRun =
    let deliveryUrl kind = $"{origin}/receiver/crm/{kind}"
    let mutable firmsTouched = 0
    let mutable connsTouched = 0
    let notes = ResizeArray<string>(batch.Notes)
    // A key several firms in this batch share cannot tell them apart: match those by source id only.
    let sharedKeys =
        batch.Firms
        |> Array.filter (fun f -> not (isNull f.KeyHash))
        |> Array.countBy _.KeyHash
        |> Array.filter (fun (_, n) -> n > 1)
        |> Array.map fst
        |> set
    if not sharedKeys.IsEmpty then
        let firms = batch.Firms |> Array.filter (fun f -> sharedKeys.Contains f.KeyHash) |> Array.length
        notes.Add $"{firms} firms share {sharedKeys.Count} Answering Legal key(s) with another firm in {batch.System}: not matched across systems by key"
    write db (fun s ->
        let upsertConnection (firmId: string) (ic: ImportedConnection) (extra: (string * string) list) =
            let set = Dictionary<string, string>(ic.Settings)
            for k, v in extra do
                set[k] <- v
            match Store.tryConnectionBySource s batch.System ic.SourceRef with
            | Some existing ->
                Store.updateConnection
                    s
                    { existing.Connection with
                        FirmId = firmId
                        Kind = ic.Kind
                        Name = ic.Name
                        Credentials = ic.Credentials
                        Settings = set
                        Enabled = ic.Enabled
                        DeliveryUrl = deliveryUrl ic.Kind }
            | None ->
                Store.insertConnection
                    s
                    { Id = Secrets.newId "con"
                      FirmId = firmId
                      FirmName = null
                      Kind = ic.Kind
                      Name = ic.Name
                      Source = batch.System
                      SourceRef = ic.SourceRef
                      Credentials = ic.Credentials
                      Settings = set
                      Enabled = ic.Enabled
                      Status = "active"
                      DisabledReason = null
                      ConsecutiveFailures = 0
                      DeliveryUrl = deliveryUrl ic.Kind
                      Issues = [||]
                      CreatedAt = now () }
                    (Secrets.newSigningSecret ())
                    null
            connsTouched <- connsTouched + 1

        for f in batch.Firms do
            let shared = not (isNull f.KeyHash) && sharedKeys.Contains f.KeyHash
            let existing =
                Store.tryFirmBySource s batch.System f.SourceId
                |> Option.orElse (if shared then None else Store.tryFirmByKeyHash s f.KeyHash)
            let firmId =
                match existing with
                | Some e ->
                    Store.updateFirm
                        s
                        { e with
                            KeyHash = (if isNull e.KeyHash then f.KeyHash else e.KeyHash)
                            KeyFingerprint = (if isNull e.KeyFingerprint then f.KeyFingerprint else e.KeyFingerprint)
                            Active = e.Active || f.Active
                            ContactTypes = mergeContactTypes e.ContactTypes f.ContactTypes
                            CustomLabels = union e.CustomLabels f.CustomLabels }
                    e.Id
                | None ->
                    let id = Secrets.newId "firm"
                    Store.insertFirm
                        s
                        { Id = id
                          Name = (if String.IsNullOrWhiteSpace f.Name then $"({batch.System} {f.SourceId})" else f.Name.Trim())
                          KeyHash = f.KeyHash
                          KeyFingerprint = f.KeyFingerprint
                          Active = f.Active
                          ContactTypes = f.ContactTypes
                          CustomLabels = f.CustomLabels
                          CreatedAt = now () }
                    id
            Store.upsertFirmSource
                s
                firmId
                { System = batch.System
                  SourceId = f.SourceId
                  Name = f.Name
                  Active = f.Active }
            firmsTouched <- firmsTouched + 1
            // The same kind twice for one firm: keep both, flag both.
            let dupes =
                f.Connections
                |> Array.groupBy _.Kind
                |> Array.filter (fun (_, cs) -> cs.Length > 1)
                |> Array.collect (fun (_, cs) -> cs |> Array.map (fun c -> c.SourceRef, (cs |> Array.find (fun o -> o.SourceRef <> c.SourceRef)).SourceRef))
                |> dict
            for c in f.Connections do
                let extra =
                    [ match dupes.TryGetValue c.SourceRef with
                      | true, other -> "duplicateOf", $"{batch.System} #{other}"
                      | _ -> ()
                      if shared && c.Kind = "answering-legal" then "sharedKey", "true" ]
                upsertConnection firmId c extra

        if batch.Orphans.Length > 0 then
            let pid = placeholderFirmId batch.System
            if (Store.tryFirm s pid).IsNone then
                Store.insertFirm
                    s
                    { Id = pid
                      Name = $"Unassigned {batch.System} CRM rows"
                      KeyHash = null
                      KeyFingerprint = null
                      Active = false
                      ContactTypes = [||]
                      CustomLabels = [||]
                      CreatedAt = now () }
            for c in batch.Orphans do
                upsertConnection pid c [ ("orphaned", "true") ]
            notes.Add $"{batch.Orphans.Length} connection(s) had no firm in {batch.System}; kept under \"Unassigned {batch.System} CRM rows\""

        let run =
            { Id = Secrets.newId "imp"
              System = batch.System
              At = now ()
              Firms = firmsTouched
              Connections = connsTouched
              Skipped = batch.Skipped
              Notes = notes.ToArray() }
        Store.insertImportRun s run
        run)
