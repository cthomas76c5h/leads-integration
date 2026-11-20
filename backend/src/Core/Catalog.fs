/// Every destination type we know of, and what each source system calls it.
/// This is the table to agree on: an integration is "supported" when it is a
/// row here with a payload builder (Payloads.fs) and a receiver check (FakeCrm.fs).
module LeadIntegrations.Catalog

type SourceCode = { System: string; Code: string }

type Kind =
    { Kind: string
      Label: string
      Category: string // platform | crm | automation | notify
      Auth: string // api_key | oauth | url | none
      CredentialFields: string[]
      SourceCodes: SourceCode[]
      MappingEnforced: bool
      PayloadFields: string[]
      Notes: string }

let private code system c = { System = system; Code = c }

/// Kinds whose receiver contract declares `"duplicates": "harmless"` (docs/contracts/receivers/):
/// a second copy costs nothing, so an unknown outcome is retried (RFC-008 §4.3). Every other
/// kind is "unknown" until its sandbox shows otherwise. A test keeps this and the contracts agreeing.
let harmlessDuplicates = set [ "email"; "sms"; "slack" ]

let kinds: Kind[] =
    [| { Kind = "answering-legal"
         Label = "Answering Legal web leads"
         Category = "platform"
         Auth = "api_key"
         CredentialFields = [| "api_key" |]
         SourceCodes = [| code "chatbot" "cb_Tenants.apiKey"; code "airi" "answering_legal"; code "api2" "Client.api_key" |]
         MappingEnforced = false
         PayloadFields = [| "first_name"; "last_name"; "phone"; "message" |]
         Notes = "The web-leads API that api-2 serves; chatbot and airi post into it, and api-2 fans out to the firm's CRMs. The firm's key is the cross-system match key." }
       { Kind = "clio-grow"
         Label = "Clio Grow (inbox lead)"
         Category = "crm"
         Auth = "api_key"
         CredentialFields = [| "api_key" |]
         SourceCodes = [| code "api2" "Clio" |]
         MappingEnforced = false
         PayloadFields = [| "inbox_lead.from_first"; "inbox_lead.from_last"; "inbox_lead.from_phone"; "inbox_lead.from_message"; "inbox_lead.from_source" |]
         Notes = "api_key is the firm's inbox_lead_token." }
       { Kind = "mycase"
         Label = "MyCase"
         Category = "crm"
         Auth = "oauth"
         CredentialFields = [| "token"; "refresh_token"; "api_key"; "url" |]
         SourceCodes = [| code "api2" "Case" |]
         MappingEnforced = false
         PayloadFields = [| "first_name"; "last_name"; "cell_phone_number"; "lead_details" |]
         Notes = "OAuth. api_key holds the referral-source id; url holds the authorization link." }
       { Kind = "lawmatics-form"
         Label = "Lawmatics (legacy form)"
         Category = "crm"
         Auth = "api_key"
         CredentialFields = [| "api_key" |]
         SourceCodes = [| code "api2" "Law" |]
         MappingEnforced = false
         PayloadFields = [| "first_name"; "last_name"; "phone"; "message" |]
         Notes = "api_key is a form id, not a secret key. Sends custom answers by form field name." }
       { Kind = "lawmatics-form-v2"
         Label = "Lawmatics (form)"
         Category = "crm"
         Auth = "api_key"
         CredentialFields = [| "api_key" |]
         SourceCodes = [| code "api2" "Law2" |]
         MappingEnforced = false
         PayloadFields = [| "first_name"; "last_name"; "phone"; "full_message" |]
         Notes = "api_key is a form id. Same product as Law and Law3, different integration." }
       { Kind = "lawmatics-api"
         Label = "Lawmatics (API, OAuth)"
         Category = "crm"
         Auth = "oauth"
         CredentialFields = [| "token"; "refresh_token" |]
         SourceCodes = [| code "api2" "Law3" |]
         MappingEnforced = false
         PayloadFields = [| "first_name"; "last_name"; "phone"; "match_contact_by"; "notes" |]
         Notes = "Custom answers are matched to Lawmatics custom fields by label at send time." }
       { Kind = "lead-docket"
         Label = "Lead Docket"
         Category = "crm"
         Auth = "url"
         CredentialFields = [| "api_key" |]
         SourceCodes = [| code "api2" "Docket" |]
         MappingEnforced = false
         PayloadFields = [| "First"; "Last"; "Phone"; "Summary" |]
         Notes = "api_key holds the firm's full POST URL, form-encoded body." }
       { Kind = "smokeball"
         Label = "Smokeball"
         Category = "crm"
         Auth = "oauth"
         CredentialFields = [| "token"; "refresh_token"; "api_key" |]
         SourceCodes = [| code "api2" "Smoke" |]
         MappingEnforced = false
         PayloadFields = [| "contact.person.firstName"; "contact.person.lastName"; "contact.person.phone"; "matter.isLead"; "task.staffId" |]
         Notes = "OAuth. api_key holds the staff id tasks are assigned to. Creates contact, lead matter, task and memo." }
       { Kind = "practicepanther"
         Label = "PracticePanther"
         Category = "crm"
         Auth = "oauth"
         CredentialFields = [| "token"; "refresh_token" |]
         SourceCodes = [| code "api2" "Panther" |]
         MappingEnforced = false
         PayloadFields = [| "account.primary_contact.first_name"; "account.primary_contact.phone_mobile"; "call_log.call_direction" |]
         Notes = "OAuth. Finds or creates the account by phone, then a matter and a call log." }
       { Kind = "lawruler"
         Label = "LawRuler"
         Category = "crm"
         Auth = "url"
         CredentialFields = [| "url" |]
         SourceCodes = [| code "api2" "Ruler" |]
         MappingEnforced = false
         PayloadFields = [| "first_name"; "last_name"; "phone" |]
         Notes = "The api-2 handler is switched off; listed so imported rows are not lost." }
       { Kind = "hubspot"
         Label = "HubSpot (form)"
         Category = "crm"
         Auth = "oauth"
         CredentialFields = [| "access_token"; "portal_id"; "form_id" |]
         SourceCodes = [| code "chatbot" "cb_HubSpotIntegrations" |]
         MappingEnforced = false
         PayloadFields = [| "fields[firstname]"; "fields[lastname]"; "fields[phone]" |]
         Notes = "Chatbot only. Configs live in the production database, not in the backup." }
       { Kind = "zapier-webhook"
         Label = "Zapier (Catch Hook)"
         Category = "automation"
         Auth = "url"
         CredentialFields = [| "url" |]
         SourceCodes = [| code "airi" "webhook:zapier" |]
         MappingEnforced = false
         PayloadFields = [| "event_id"; "first_name"; "phone" |]
         Notes = "Standard body; the field mapping lives in the firm's Zap, so we can show it but not enforce it. The URL is a credential." }
       { Kind = "webhook"
         Label = "Webhook (firm's own system)"
         Category = "automation"
         Auth = "url"
         CredentialFields = [| "url" |]
         SourceCodes = [||]
         MappingEnforced = true
         PayloadFields = [||]
         Notes = "Our field mapping is the body, so it is enforced and testable. Signed (Standard Webhooks)." }
       { Kind = "email"
         Label = "Email"
         Category = "notify"
         Auth = "none"
         CredentialFields = [||]
         SourceCodes = [| code "chatbot" "cb_Tenants.recipientsEmail"; code "airi" "email" |]
         MappingEnforced = false
         PayloadFields = [| "to"; "subject"; "text" |]
         Notes = "Recipients are settings, not credentials." }
       { Kind = "sms"
         Label = "SMS"
         Category = "notify"
         Auth = "none"
         CredentialFields = [||]
         SourceCodes = [| code "airi" "sms" |]
         MappingEnforced = false
         PayloadFields = [| "to"; "text" |]
         Notes = "E.164 numbers." }
       { Kind = "slack"
         Label = "Slack"
         Category = "notify"
         Auth = "url"
         CredentialFields = [| "url" |]
         SourceCodes = [| code "airi" "slack" |]
         MappingEnforced = false
         PayloadFields = [| "text" |]
         Notes = "Incoming-webhook URL, a credential." } |]

let private byKind = kinds |> Array.map (fun k -> k.Kind, k) |> dict

let tryFind (kind: string) =
    match byKind.TryGetValue kind with
    | true, k -> Some k
    | _ -> None

let find kind =
    tryFind kind |> Option.defaultWith (fun () -> Domain.fail 422 $"unknown kind: {kind}")

let label kind =
    tryFind kind |> Option.map _.Label |> Option.defaultValue kind

/// api-2 CRM.name codes → kind.
let ofApi2Code (c: string) =
    match (if isNull c then "" else c.Trim()) with
    | "Clio" -> Some "clio-grow"
    | "Case" -> Some "mycase"
    | "Law" -> Some "lawmatics-form"
    | "Law2" -> Some "lawmatics-form-v2"
    | "Law3" -> Some "lawmatics-api"
    | "Docket" -> Some "lead-docket"
    | "Smoke" -> Some "smokeball"
    | "Panther" -> Some "practicepanther"
    | "Ruler" -> Some "lawruler"
    | _ -> None

/// What a credential field holds for a kind, when it is not what the name says.
let meaning (kind: string) (field: string) : string =
    match kind, field with
    | "clio-grow", "api_key" -> "inbox lead token"
    | "mycase", "api_key" -> "referral source id"
    | "mycase", "url" -> "authorization link"
    | ("lawmatics-form" | "lawmatics-form-v2"), "api_key" -> "form id"
    | "lead-docket", "api_key" -> "full POST URL"
    | "smokeball", "api_key" -> "staff id"
    | _, "refresh_token" -> "OAuth refresh token"
    | _, ("token" | "access_token") -> "OAuth access token"
    | _ -> null
