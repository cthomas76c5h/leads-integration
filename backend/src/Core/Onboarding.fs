/// Operator-assisted onboarding:
/// the operator configures, the client connects and approves a specific
/// version, the system tests, the operator activates. Edits after go-live make
/// a draft revision; live leads keep the active revision until activation.
module LeadIntegrations.Onboarding

open System
open LeadIntegrations.Db
open LeadIntegrations.Domain

let recipeRows: Collections.Generic.IDictionary<string, MapRow[]> =
    dict
        [ "create-intake",
          [| { From = "full_name"; To = "Contact name" }
             { From = "phone"; To = "Phone" }
             { From = "email"; To = "Email" }
             { From = "call_type"; To = "Matter type" }
             { From = "full_message"; To = "Notes" }
             { From = "event_id"; To = "External ID" } |]
          "create-contact",
          [| { From = "first_name"; To = "First name" }
             { From = "last_name"; To = "Last name" }
             { From = "phone"; To = "Phone" }
             { From = "email"; To = "Email" }
             { From = "message"; To = "Note" } |]
          "notify-staff",
          [| { From = "full_name"; To = "Caller" }
             { From = "phone"; To = "Phone" }
             { From = "message"; To = "Message" } |] ]

/// Only a plain webhook takes our mapping as its body. Elsewhere the mapping
/// lives in the CRM integration or the Zap: we can show it, not enforce it.
let enforced (s: Setup) = s.Kind = "webhook"

let hasActive (s: Setup) = not (isNull (box s.Active))

let private approvedThis (s: Setup) = s.Approvals |> Array.tryFind (fun a -> a.Version = s.Version && a.Decision = "approved")
let private changesThis (s: Setup) = s.Approvals |> Array.tryFind (fun a -> a.Version = s.Version && a.Decision = "changes")

let stateOf (db: Database) (s: Setup) : string * string * string =
    if hasActive s && s.Active.Version = s.Version then "Live", "Watch the Delivery Center", "ok"
    elif isNull s.ConnectionId then "Connection needed", "Send the secure link", "warn"
    elif (changesThis s).IsSome then "Changes requested", "Read the comment, update, resend", "bad"
    elif (approvedThis s).IsNone then
        if s.SentVersion.HasValue && s.SentVersion.Value = s.Version then "Awaiting client approval", "Follow up", "muted"
        else "Draft", "Send for approval", "muted"
    else
        let test =
            if isNull s.TestDeliveryId then None
            else read db (fun x -> Store.tryDelivery x s.TestDeliveryId) |> Option.map _.Delivery
        match test with
        | None -> "Ready to test", "Send a test lead", "warn"
        | Some t when t.Status = "failed" || t.Status = "cancelled" -> "Test failed", "Inspect the mapping", "bad"
        | Some t when t.Status <> "delivered" -> "Testing", "Wait a moment", "warn"
        | Some _ when not s.ClientConfirmedAt.HasValue -> "Test accepted", "Client confirms the record in the CRM", "warn"
        | Some _ -> "Ready to go live", (if hasActive s then $"Activate version {s.Version}" else "Confirm go-live"), "ok"

type SampleRow = { From: string; To: string; Label: string; Value: string }

type SetupView =
    { Id: string
      FirmId: string
      FirmName: string
      Token: string
      Kind: string
      KindLabel: string
      /// How the client connects: 'url' pastes an address, 'oauth' / 'api_key' authorizes.
      Auth: string
      Recipe: string
      Rows: MapRow[]
      Version: int
      SentVersion: Nullable<int>
      ConnectionId: string
      TestDeliveryId: string
      ClientConfirmedAt: Nullable<int64>
      Active: ActiveRevision
      Approvals: Approval[]
      History: SetupEvent[]
      CreatedAt: int64
      State: string
      Next: string
      Tone: string
      Live: bool
      ActiveVersion: Nullable<int>
      Revising: bool
      Enforced: bool
      Connected: bool
      Sample: SampleRow[] }

let private firmOf (db: Database) (s: Setup) = read db (fun x -> Store.firmOrFail x s.FirmId)

let sampleLead (firm: Store.FirmRow) =
    Synthetic.lead firm.Name "full" 0 null false firm.CustomLabels

let view (db: Database) (s: Setup) : SetupView =
    let firm = firmOf db s
    let state, next, tone = stateOf db s
    let vars = Payloads.vars "evt_sample" firm.Name (sampleLead firm)
    let labels = Payloads.sourceFields firm.CustomLabels |> dict
    let k = Catalog.tryFind s.Kind
    { Id = s.Id
      FirmId = s.FirmId
      FirmName = firm.Name
      Token = s.Token
      Kind = s.Kind
      KindLabel = Catalog.label s.Kind
      Auth = (match k with Some k -> k.Auth | None -> "none")
      Recipe = s.Recipe
      Rows = s.Rows
      Version = s.Version
      SentVersion = s.SentVersion
      ConnectionId = s.ConnectionId
      TestDeliveryId = s.TestDeliveryId
      ClientConfirmedAt = s.ClientConfirmedAt
      Active = s.Active
      Approvals = s.Approvals
      History = s.History
      CreatedAt = s.CreatedAt
      State = state
      Next = next
      Tone = tone
      Live = hasActive s
      ActiveVersion = (if hasActive s then Nullable s.Active.Version else Nullable())
      Revising = hasActive s && s.Active.Version <> s.Version
      Enforced = enforced s
      Connected = not (isNull s.ConnectionId)
      Sample =
        s.Rows
        |> Array.map (fun r ->
            { From = r.From
              To = r.To
              Label = (match labels.TryGetValue r.From with | true, l -> l | _ -> r.From)
              Value = (match vars.TryGetValue r.From with | true, v -> v | _ -> "") }) }

/// The client's view: no token, no connection id.
let clientView db s =
    { view db s with
        Token = null
        ConnectionId = null }

let private load (db: Database) id =
    read db (fun x -> Store.trySetup x id) |> Option.defaultWith (fun () -> fail 404 "no such setup")

let loadByToken (db: Database) token =
    read db (fun x -> Store.trySetupByToken x token) |> Option.defaultWith (fun () -> fail 404 "this link is not valid")

let private save (db: Database) (s: Setup) (who: string) (what: string) =
    write db (fun x ->
        Store.saveSetup x s
        if not (isNull what) then Store.appendEvent x s.Id who what)
    load db s.Id

let list (db: Database) = read db Store.listSetups |> Array.map (view db)

let get db id = load db id |> view db

let create (db: Database) (firmId: string) (kind: string) (recipe: string) (cloneFrom: string) =
    let k = Catalog.find kind
    let firm = read db (fun x -> Store.firmOrFail x firmId)
    let from = if String.IsNullOrEmpty cloneFrom then None else read db (fun x -> Store.trySetup x cloneFrom)
    let rows =
        match from with
        // A live setup's proven mapping, not its draft.
        | Some f -> if hasActive f then Array.copy f.Active.Rows else Array.copy f.Rows
        | None ->
            match recipeRows.TryGetValue recipe with
            | true, r -> Array.copy r
            | _ -> [||]
    let s =
        { Id = Secrets.newId "su"
          FirmId = firm.Id
          Token = Secrets.newToken ()
          Kind = k.Kind
          Recipe = (if String.IsNullOrEmpty recipe then "create-intake" else recipe)
          Rows = rows
          Version = 1
          LastVersion = 1
          SentVersion = Nullable()
          ConnectionId = null
          TestDeliveryId = null
          ClientConfirmedAt = Nullable()
          Active = Unchecked.defaultof<_>
          Approvals = [||]
          History = [||]
          CreatedAt = now () }
    let note =
        match from with
        | Some f -> $"Draft created by cloning the mapping of {(read db (fun x -> Store.firmOrFail x f.FirmId)).Name}"
        | None -> $"Draft created: {k.Label}, recipe {s.Recipe}"
    save db s "operator" note |> view db

/// A new configuration version: earlier approvals no longer cover it. The
/// connection is not touched; real leads keep the active revision.
let private bump (s: Setup) =
    { s with
        LastVersion = s.LastVersion + 1
        Version = s.LastVersion + 1
        TestDeliveryId = null
        ClientConfirmedAt = Nullable() }

let update (db: Database) (id: string) (recipe: string) (rows: MapRow[]) =
    let s = load db id
    let changed = ResizeArray<string>()
    let mutable next = s
    if not (String.IsNullOrEmpty recipe) && recipe <> s.Recipe then
        next <- { next with Recipe = recipe }
        changed.Add $"recipe → {recipe}"
    if not (isNull rows) then
        let rows = rows |> Array.filter (fun r -> not (isNull r.From) && not (isNull r.To))
        if Json.serialize rows <> Json.serialize s.Rows then
            next <- { next with Rows = rows }
            changed.Add "field mapping updated"
    if changed.Count = 0 then view db s
    else
        let bumped = bump next
        let live =
            if hasActive s then $"; live stays on version {s.Active.Version} until this one is approved, tested and activated" else ""
        save db bumped "operator" $"""{String.Join("; ", changed)} (now version {bumped.Version}{live})""" |> view db

let send (db: Database) id =
    let s = load db id
    if isNull s.ConnectionId then fail 409 "the client has not connected yet"
    save db { s with SentVersion = Nullable s.Version } "operator" $"Version {s.Version} sent to the client for approval" |> view db

let private template (s: Setup) = if enforced s then Some(Payloads.rowsToTemplate s.Rows) else None

let test (db: Database) id =
    let s = load db id
    if isNull s.ConnectionId then fail 409 "not connected"
    if (approvedThis s).IsNone then fail 409 "the client has not approved this version"
    let firm = firmOf db s
    // The test carries this revision's body, so a draft is tested without touching live.
    let d = Dispatch.testConnection db s.ConnectionId (sampleLead firm) (template s)
    save db { s with TestDeliveryId = d.Id; ClientConfirmedAt = Nullable() } "operator" "Test lead sent" |> view db

let confirm (db: Database) id =
    let s = load db id
    save db { s with ClientConfirmedAt = Nullable(now ()) } "operator" "Client confirmed on the call: the test record is in their CRM" |> view db

let goLive (db: Database) id =
    let s = load db id
    let state, _, _ = stateOf db s
    if state <> "Ready to go live" then fail 409 $"not ready: {state}"
    let was = if hasActive s then Some s.Active.Version else None
    write db (fun x ->
        let c = (Store.connectionOrFail x s.ConnectionId).Connection
        let settings = Collections.Generic.Dictionary<string, string>(c.Settings)
        settings.Remove "stage" |> ignore
        Store.updateConnection x { c with Settings = settings; Enabled = true }
        if enforced s then Store.setConnectionTemplate x c.Id (Payloads.rowsToTemplate s.Rows))
    let active =
        { Version = s.Version
          Recipe = s.Recipe
          Kind = s.Kind
          Rows = Array.copy s.Rows
          At = now () }
    let what =
        match was with
        | None -> $"Went live on approved version {s.Version}"
        | Some w -> $"Activated approved version {s.Version} (replaces version {w})"
    save db { s with Active = active } "operator" what |> view db

let discard (db: Database) id =
    let s = load db id
    if not (hasActive s) || s.Active.Version = s.Version then fail 409 "there is no draft revision to discard"
    let draft = s.Version
    let a = s.Active
    save
        db
        { s with
            Recipe = a.Recipe
            Rows = Array.copy a.Rows
            Version = a.Version
            TestDeliveryId = null
            ClientConfirmedAt = Nullable() }
        "operator"
        $"Discarded draft version {draft}; back to live version {a.Version}"
    |> view db

/// The client connects through the secure link: pastes a hook URL (webhook,
/// Zapier) or authorizes a CRM (simulated: the local fake receiver).
let connect (db: Database) (policy: Address.Policy) (origin: string) token (url: string) =
    let s = loadByToken db token
    if not (isNull s.ConnectionId) then fail 409 "already connected"
    let k = Catalog.find s.Kind
    let deliveryUrl, how =
        match s.Kind with
        | "webhook"
        | "zapier-webhook"
        | "slack" ->
            match Address.checkShape policy url with
            | Error reason -> fail 422 $"address: {reason}"
            | Ok uri -> uri.ToString(), "Pasted the hook address through the secure link"
        | kind -> $"{origin}/receiver/crm/{kind}", $"Authorized {k.Label} (simulated: the local receiver stands in)"
    let firm = firmOf db s
    let creds =
        k.CredentialFields
        |> Array.map (fun f ->
            { Name = f
              Present = true
              Fingerprint = Secrets.fingerprint (Secrets.newToken ())
              Meaning = Catalog.meaning s.Kind f })
    let c =
        { Id = Secrets.newId "con"
          FirmId = firm.Id
          FirmName = firm.Name
          Kind = s.Kind
          Name = k.Label
          Source = Source.manual
          SourceRef = null
          Credentials = creds
          Settings = Collections.Generic.Dictionary(dict [ ("stage", "setup") ]) // only test leads until go-live
          Enabled = true
          Status = "active"
          DisabledReason = null
          ConsecutiveFailures = 0
          DeliveryUrl = deliveryUrl
          Issues = [||]
          CreatedAt = now () }
    write db (fun x -> Store.insertConnection x c (Secrets.newSigningSecret ()) (Option.toObj (template s)))
    save db { s with ConnectionId = c.Id } "client" how |> ignore

let decide (db: Database) token (decision: string) (by: string) (comment: string) (version: int) =
    let s = loadByToken db token
    let decision = if decision = "approved" then "approved" else "changes"
    let by = if String.IsNullOrWhiteSpace by then "Firm administrator" else by.Trim()
    let comment = if isNull comment then "" else comment.Trim()
    if decision = "changes" && comment = "" then fail 422 "say what should change"
    if version <> s.Version then fail 409 "the setup changed while you were reading; reload to see the new version"
    if hasActive s && s.Active.Version = s.Version then fail 409 "nothing is awaiting your approval; this version is already live"
    write db (fun x ->
        Store.appendApproval
            x
            s.Id
            { Version = s.Version
              Decision = decision
              By = by
              Comment = comment
              At = now () }
        Store.appendEvent
            x
            s.Id
            "client"
            (if decision = "approved" then $"{by} approved version {s.Version}" else $"{by} asked for changes: {comment}"))

let fields (db: Database) id =
    let s = load db id
    let firm = firmOf db s
    Payloads.sourceFields firm.CustomLabels |> Array.map (fun (k, l) -> {| key = k; label = l |})
