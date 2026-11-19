/// What is wrong or worth knowing about one connection, computed from its
/// shape. These are the import findings an operator can act on.
module LeadIntegrations.Issues

open LeadIntegrations.Domain

let private issue code severity message =
    { Code = code
      Severity = severity
      Message = message }

let private has (creds: CredentialField[]) name =
    creds |> Array.exists (fun c -> c.Name = name && c.Present)

let compute (kind: string) (creds: CredentialField[]) (settings: System.Collections.Generic.IDictionary<string, string>) (enabled: bool) : Issue[] =
    let k = Catalog.tryFind kind
    [| match k with
       | None -> issue "unknown-kind" "bad" $"Unknown integration type \"{kind}\": no payload builder"
       | Some k ->
           if k.Auth = "oauth" then
               if not (has creds "token" || has creds "access_token") then
                   issue "oauth-no-token" "bad" "OAuth integration with no access token: the firm must authorize again"
               elif not (has creds "refresh_token") && kind <> "hubspot" then
                   issue "oauth-no-refresh" "warn" "No refresh token: delivery stops when the access token expires"
           if k.Auth = "api_key" && not (has creds "api_key") then
               issue "missing-key" "bad" "No API key: nothing can be delivered"
           if k.Auth = "url" && kind <> "zapier-webhook" && kind <> "webhook" && kind <> "slack"
              && not (has creds "api_key" || has creds "url") then
               issue "missing-url" "bad" "No destination URL"
           if kind = "lead-docket" then
               issue "key-is-url" "info" "api_key holds the full POST URL: treat it as a credential"
           if kind = "lawruler" then
               issue "handler-off" "warn" "The api-2 handler for this CRM is switched off: no lead has been delivered"
           if kind = "mycase" && not (has creds "api_key") then
               issue "mycase-no-referral" "info" "No referral source id: MyCase leads arrive without a source"
           if kind = "smokeball" && not (has creds "api_key") then
               issue "smokeball-no-staff" "warn" "No staff id: Smokeball tasks have no assignee"
           match settings.TryGetValue "sharedKey" with
           | true, "true" ->
               issue "shared-key" "bad" "Other firms use the same Answering Legal key: their leads land in one account"
           | _ -> ()
           match settings.TryGetValue "orphaned" with
           | true, "true" -> issue "orphaned" "bad" "This CRM row belongs to no firm in the source"
           | _ -> ()
           match settings.TryGetValue "duplicateOf" with
           | true, other when not (System.String.IsNullOrEmpty other) ->
               issue "duplicate" "warn" $"The same firm has another {k.Label} row ({other})"
           | _ -> ()
       if not enabled then
           issue "disabled-in-source" "info" "Disabled in the source system" |]
