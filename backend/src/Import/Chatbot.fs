/// chatbot (MySQL `answering_legal`), from the mysqldump backup: firms are
/// cb_Tenants, delivery is the Answering Legal web lead (cb_Tenants.apiKey)
/// plus email to recipientsEmail, and the intake labels are cb_CustomParameters
/// via cb_Templates.tenantId. HubSpot configs are not in the backup.
module LeadIntegrations.Importers.Chatbot

open System
open System.Text.Json
open LeadIntegrations
open LeadIntegrations.Domain
open LeadIntegrations.Import
open LeadIntegrations.Importers.MySqlDump

let private recipientCount (json: string) =
    try
        use doc = JsonDocument.Parse(if String.IsNullOrWhiteSpace json then "[]" else json)
        if doc.RootElement.ValueKind = JsonValueKind.Array then
            doc.RootElement.EnumerateArray() |> Seq.filter (fun e -> e.ValueKind = JsonValueKind.String && e.GetString().Contains '@') |> Seq.length
        else 0
    with :? JsonException -> 0

let read (dumpPath: string) : Batch =
    let tenants = rows dumpPath "cb_Tenants"
    let templates = rows dumpPath "cb_Templates"
    let parameters = rows dumpPath "cb_CustomParameters"
    let calendly = rows dumpPath "cb_CalendlyIntegrations"
    let tenantOfTemplate = templates |> List.map (fun t -> get t "id", get t "tenantId") |> dict
    let labelsByTenant =
        parameters
        |> List.choose (fun p ->
            match tenantOfTemplate.TryGetValue(get p "templateId") with
            | true, tenant when not (isNull tenant) && not (String.IsNullOrWhiteSpace(get p "label")) -> Some(tenant, (get p "label").Trim())
            | _ -> None)
        |> List.groupBy fst
        |> List.map (fun (t, ls) -> t, ls |> List.map snd |> List.countBy id |> List.sortByDescending snd |> List.map fst |> List.truncate 12 |> Array.ofList)
        |> dict
    let mutable skipped = 0
    let firms =
        tenants
        |> List.choose (fun t ->
            let id = get t "id"
            if not (isNull (get t "deleteAt")) then
                skipped <- skipped + 1
                None
            else
                let key = get t "apiKey" // secret: reduced at once
                let recipients = recipientCount (get t "recipientsEmail")
                let name =
                    match get t "companyName" with
                    | null
                    | "" -> get t "name"
                    | c -> c
                let connections =
                    [| if Secrets.isPresent key then
                           { SourceRef = $"tenant-{id}-web-lead"
                             Kind = "answering-legal"
                             Name = Catalog.label "answering-legal"
                             Credentials = [| credential "answering-legal" "api_key" key |]
                             Settings = settings []
                             Enabled = true }
                       if recipients > 0 then
                           { SourceRef = $"tenant-{id}-email"
                             Kind = "email"
                             Name = "Email"
                             Credentials = [||]
                             Settings = settings [ "recipients", $"({recipients} recipient(s) on file)" ]
                             Enabled = true } |]
                Some
                    { SourceId = id
                      Name = name
                      Active = (get t "status" |> fun s -> isNull s || s = "1" || s.Equals("active", StringComparison.OrdinalIgnoreCase))
                      KeyHash = Secrets.matchHash key
                      KeyFingerprint = Secrets.fingerprint key
                      ContactTypes = [||]
                      CustomLabels = (match labelsByTenant.TryGetValue id with | true, v -> v | _ -> [||])
                      Connections = connections })
        |> Array.ofList
    { System = Source.chatbot
      Firms = firms
      Orphans = [||]
      Skipped = skipped
      Notes =
        [ $"{tenants.Length} tenants in the dump, {skipped} soft-deleted and skipped"
          $"{parameters.Length} custom parameters → intake labels for {labelsByTenant.Count} firms"
          $"{calendly.Length} Calendly integrations not imported: scheduling, not lead delivery"
          "HubSpot configs are not in this backup (cb_HubSpotIntegrations lives only in production)" ] }
