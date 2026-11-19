/// airi (the voice receptionist): accounts and lead_destinations from a SQLite
/// database with the client-portal schema (devdata.sqlite locally). A
/// destination's config JSON holds its secrets (api_key, url): reduced at once.
module LeadIntegrations.Importers.Airi

open System
open System.Collections.Generic
open System.Text.Json
open Microsoft.Data.Sqlite
open LeadIntegrations
open LeadIntegrations.Domain
open LeadIntegrations.Import

let private prop (el: JsonElement) (name: string) =
    if el.ValueKind = JsonValueKind.Object then
        match el.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
        | _ -> null
    else null

/// airi destination kind (+ config) → our kind.
let private kindOf (kind: string) (config: JsonElement) =
    match kind with
    | "answering_legal" -> Some "answering-legal"
    | "email" -> Some "email"
    | "sms" -> Some "sms"
    | "slack" -> Some "slack"
    | "webhook" when prop config "service" = "zapier" -> Some "zapier-webhook"
    | "webhook" -> Some "webhook"
    | _ -> None

let read (path: string) : Batch =
    let cs = SqliteConnectionStringBuilder(DataSource = path, Mode = SqliteOpenMode.ReadOnly).ConnectionString
    use conn = new SqliteConnection(cs)
    conn.Open()
    let notes = ResizeArray<string>()
    let accounts = ResizeArray<string * string * bool>()
    (
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT id, name, disabled FROM accounts"
        use r = cmd.ExecuteReader()
        while r.Read() do
            accounts.Add(r.GetValue(0).ToString(), (if r.IsDBNull 1 then null else r.GetString 1), (not (r.IsDBNull 2) && r.GetValue(2).ToString() <> "0"))
    )
    let byAccount = Dictionary<string, ResizeArray<ImportedConnection>>()
    let keyOf = Dictionary<string, string * string>()
    let mutable skipped = 0
    (
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT id, account_id, kind, enabled, config FROM lead_destinations"
        use r = cmd.ExecuteReader()
        while r.Read() do
            let id = r.GetValue(0).ToString()
            let account = r.GetValue(1).ToString()
            let akind = r.GetString 2
            use doc = JsonDocument.Parse(if r.IsDBNull 4 || String.IsNullOrWhiteSpace(r.GetString 4) then "{}" else r.GetString 4)
            let config = doc.RootElement
            match kindOf akind config with
            | None ->
                skipped <- skipped + 1
                notes.Add $"lead_destinations #{id}: kind \"{akind}\" has no catalog entry, skipped"
            | Some kind ->
                let apiKey = prop config "api_key"
                let url = prop config "url" |> Option.ofObj |> Option.orElse (Option.ofObj (prop config "webhook_url")) |> Option.toObj
                if kind = "answering-legal" && Secrets.isPresent apiKey then
                    keyOf[account] <- (Secrets.matchHash apiKey, Secrets.fingerprint apiKey)
                let creds =
                    (Catalog.find kind).CredentialFields
                    |> Array.map (fun f -> credential kind f (if f = "api_key" then apiKey elif f = "url" then url else null))
                let to' = prop config "to"
                if not (byAccount.ContainsKey account) then byAccount[account] <- ResizeArray()
                byAccount[account].Add
                    { SourceRef = $"destination-{id}"
                      Kind = kind
                      Name = Catalog.label kind
                      Credentials = creds
                      Settings =
                        settings
                            [ "airiKind", akind
                              if Secrets.isPresent to' then (if kind = "email" then "recipients" else "to"), "(on file)" ]
                      Enabled = (not (r.IsDBNull 3) && r.GetValue(3).ToString() <> "0") }
    )
    let firms =
        accounts
        |> Seq.map (fun (id, name, disabled) ->
            let keyHash, keyFp = match keyOf.TryGetValue id with | true, v -> v | _ -> null, null
            { SourceId = id
              Name = name
              Active = not disabled
              KeyHash = keyHash
              KeyFingerprint = keyFp
              ContactTypes = [||]
              CustomLabels = [||]
              Connections = (match byAccount.TryGetValue id with | true, v -> v.ToArray() | _ -> [||]) })
        |> Array.ofSeq
    notes.Insert(0, $"{accounts.Count} accounts, {byAccount.Values |> Seq.sumBy _.Count} destinations from {IO.Path.GetFileName path}")
    notes.Add "Production airi configs (account_configs.config destinations) are not on disk; import a pg_dump restore when available"
    { System = Source.airi
      Firms = firms
      Orphans = [||]
      Skipped = skipped
      Notes = List.ofSeq notes }
