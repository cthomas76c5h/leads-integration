/// api-2 (Django, Postgres `leads`): firms are leads_client, CRM configs are
/// leads_crm, routing is leads_contact. Read-only: the session is opened with
/// default_transaction_read_only, so no statement here can write.
///
/// Secrets: CRM api_key / token / refresh_token and the client api_key are read
/// one row at a time and immediately reduced to present + fingerprint (+ the
/// match hash for the client key). Lead rows are read only for intake *labels*.
module LeadIntegrations.Importers.Api2

open System
open System.Collections.Generic
open Npgsql
open LeadIntegrations
open LeadIntegrations.Domain
open LeadIntegrations.Import

let private readOnly (cs: string) =
    let b = NpgsqlConnectionStringBuilder(cs)
    b.Options <- ((if isNull b.Options then "" else b.Options + " ") + "-c default_transaction_read_only=on").Trim()
    b.ApplicationName <- "lead-integrations-import"
    b.ConnectionString

let private hasColumn (conn: NpgsqlConnection) (table: string) (col: string) =
    use cmd = new NpgsqlCommand("SELECT 1 FROM information_schema.columns WHERE table_name=@t AND column_name=@c", conn)
    cmd.Parameters.AddWithValue("t", table) |> ignore
    cmd.Parameters.AddWithValue("c", col) |> ignore
    not (isNull (cmd.ExecuteScalar()))

let private str (r: Data.Common.DbDataReader) (i: int) = if r.IsDBNull i then null else r.GetValue(i).ToString()

let private flag (r: Data.Common.DbDataReader) (i: int) =
    if r.IsDBNull i then false
    else
        match r.GetValue i with
        | :? bool as b -> b
        | v -> v.ToString() = "1" || v.ToString().Equals("true", StringComparison.OrdinalIgnoreCase)

let read (connectionString: string) (maxLabelsPerFirm: int) : Batch =
    use conn = new NpgsqlConnection(readOnly connectionString)
    conn.Open()
    let notes = ResizeArray<string>()

    // Firms
    let activeCol = if hasColumn conn "leads_client" "active" then "active" else "TRUE"
    if activeCol = "TRUE" then notes.Add "leads_client has no `active` column (older schema): every firm imported as active"
    let clients = Dictionary<string, string * bool * string * string>()
    (
        use cmd = new NpgsqlCommand($"SELECT id, name, api_key, {activeCol} FROM leads_client", conn)
        use r = cmd.ExecuteReader()
        while r.Read() do
            let key = str r 2 // secret: reduced at once
            clients[str r 0] <- (str r 1, flag r 3, Secrets.matchHash key, Secrets.fingerprint key)
    )

    // Routing: contact types per firm
    let contacts = Dictionary<string, ResizeArray<ContactType>>()
    (
        use cmd = new NpgsqlCommand("SELECT client_id, name, active FROM leads_contact WHERE client_id IS NOT NULL", conn)
        use r = cmd.ExecuteReader()
        while r.Read() do
            let id = str r 0
            if not (contacts.ContainsKey id) then contacts[id] <- ResizeArray()
            contacts[id].Add { Name = str r 1; Active = flag r 2 }
    )

    // CRM rows
    let crmByClient = Dictionary<string, ResizeArray<ImportedConnection>>()
    let orphans = ResizeArray<ImportedConnection>()
    let mutable skipped = 0
    (
        use cmd =
            new NpgsqlCommand("SELECT id, client_id, name, api_key, token, refresh_token, url, enabled, send_webchats FROM leads_crm ORDER BY id", conn)
        use r = cmd.ExecuteReader()
        while r.Read() do
            let code = str r 2
            match Catalog.ofApi2Code code with
            | None ->
                skipped <- skipped + 1
                notes.Add $"leads_crm #{str r 0}: unknown CRM code \"{code}\", skipped"
            | Some kind ->
                let creds =
                    [| credential kind "api_key" (str r 3)
                       credential kind "token" (str r 4)
                       credential kind "refresh_token" (str r 5)
                       credential kind "url" (str r 6) |]
                    // Only the fields this kind uses, plus any unexpected one that is set.
                    |> Array.filter (fun c -> c.Present || (Catalog.find kind).CredentialFields |> Array.contains c.Name)
                let ic =
                    { SourceRef = str r 0
                      Kind = kind
                      Name = Catalog.label kind
                      Credentials = creds
                      Settings = settings [ "api2Code", code; "sendWebchats", (if flag r 8 then "true" else "false") ]
                      Enabled = flag r 7 }
                let clientId = str r 1
                if isNull clientId || not (clients.ContainsKey clientId) then orphans.Add ic
                else
                    if not (crmByClient.ContainsKey clientId) then crmByClient[clientId] <- ResizeArray()
                    crmByClient[clientId].Add ic
    )

    // Intake labels, for firms with a CRM only: the label text of recent leads'
    // custom answers. Answers themselves are not selected.
    let labels = Dictionary<string, string[]>()
    try
        use cmd =
            new NpgsqlCommand(
                """SELECT client_id, label FROM (
                     SELECT l.client_id, e->>'label' AS label, COUNT(*) AS n,
                            ROW_NUMBER() OVER (PARTITION BY l.client_id ORDER BY COUNT(*) DESC) AS rk
                     FROM (SELECT client_id, custom2 FROM leads_lead
                           WHERE client_id = ANY(@ids) AND custom2 LIKE '[%' ORDER BY id DESC LIMIT 50000) l,
                          LATERAL json_array_elements(l.custom2::json) e
                     WHERE e->>'label' IS NOT NULL AND e->>'label' <> ''
                     GROUP BY l.client_id, e->>'label') x
                   WHERE rk <= @max""",
                conn
            )
        cmd.Parameters.AddWithValue("ids", crmByClient.Keys |> Seq.map int64 |> Array.ofSeq) |> ignore
        cmd.Parameters.AddWithValue("max", int64 maxLabelsPerFirm) |> ignore
        cmd.CommandTimeout <- 120
        use r = cmd.ExecuteReader()
        let acc = Dictionary<string, ResizeArray<string>>()
        while r.Read() do
            let id = str r 0
            if not (acc.ContainsKey id) then acc[id] <- ResizeArray()
            acc[id].Add(str r 1)
        for kv in acc do
            labels[kv.Key] <- kv.Value.ToArray()
    with ex ->
        notes.Add $"intake labels not read ({ex.GetType().Name}: {ex.Message.Split('\n')[0]}); synthetic leads use none"

    let firms =
        clients
        |> Seq.map (fun kv ->
            let name, active, keyHash, keyFp = kv.Value
            { SourceId = kv.Key
              Name = name
              Active = active
              KeyHash = keyHash
              KeyFingerprint = keyFp
              ContactTypes = (match contacts.TryGetValue kv.Key with | true, v -> v.ToArray() | _ -> [||])
              CustomLabels = (match labels.TryGetValue kv.Key with | true, v -> v | _ -> [||])
              Connections = (match crmByClient.TryGetValue kv.Key with | true, v -> v.ToArray() | _ -> [||]) })
        |> Array.ofSeq

    notes.Insert(0, $"{clients.Count} clients, {crmByClient.Values |> Seq.sumBy _.Count} CRM rows with a firm, {contacts.Values |> Seq.sumBy _.Count} contact types, {labels.Count} firms with intake labels")
    { System = Source.api2
      Firms = firms
      Orphans = orphans.ToArray()
      Skipped = skipped
      Notes = List.ofSeq notes }
