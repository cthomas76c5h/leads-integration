/// The provisioning boundary (suite S-2): the only way a firm here gets its canonical id. A firm is
/// an operational tenant, apart from the commercial billing account that holds it; both ids are
/// issued outside delivery, opaque, and never parsed. Provisioning is the source of truth for the
/// firm's name, time zone and account: provisioning the same id again sets them to what it says.
module LeadIntegrations.Provisioning

open System
open System.Text.RegularExpressions
open LeadIntegrations.Db
open LeadIntegrations.Domain

type ProvisionedFirm =
    { CanonicalId: string
      FirmId: string
      Name: string
      TimeZone: string
      AccountId: string
      ProvisionedAt: int64
      UpdatedAt: int64 }

/// What provisioning asks for. `Adopt`: an existing firm here to link instead of creating one.
type Request =
    { Name: string
      TimeZone: string
      AccountId: string
      Adopt: string }

/// Opaque, but bounded: something safe in a path, a log line and a database key.
let private idShape = Regex(@"^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}$")

let private checkId (what: string) (id: string) =
    if isNull id || not (idShape.IsMatch id) then
        fail 422 $"{what}: 1 to 64 letters, digits and . _ : - (an opaque id, never meaningful)"

let tryGet (s: Session) (canonicalId: string) : ProvisionedFirm option =
    s.TryOne
        """SELECT canonical_id, id, name, time_zone, account_id, provisioned_at, provisioning_updated_at
           FROM firms WHERE canonical_id=$1"""
        [ canonicalId ]
        (fun r ->
            { CanonicalId = Read.str r 0
              FirmId = Read.str r 1
              Name = Read.str r 2
              TimeZone = Read.str r 3
              AccountId = Read.str r 4
              ProvisionedAt = Read.int64 r 5
              UpdatedAt = Read.int64 r 6 })

/// Creates, links or updates the firm for a canonical id. Returns it, and whether it is new here
/// (created or linked now). Idempotent: the same request again changes nothing.
let provision (s: Session) (canonicalId: string) (req: Request) (nowMs: int64) : ProvisionedFirm * bool =
    checkId "canonical id" canonicalId
    if String.IsNullOrWhiteSpace req.Name then fail 422 "name is required"
    if not (String.IsNullOrWhiteSpace req.AccountId) then checkId "accountId" req.AccountId
    let zone =
        if String.IsNullOrWhiteSpace req.TimeZone then None
        else
            match Zones.tryFind req.TimeZone with
            | Some _ -> Some req.TimeZone
            | None -> fail 422 $"timeZone: \"{req.TimeZone}\" is not an IANA time zone name, like America/New_York"
    let name = req.Name.Trim()
    let account = if String.IsNullOrWhiteSpace req.AccountId then null else req.AccountId
    if (Store.tryFirmAlias s canonicalId).IsSome then
        fail 422 $"{canonicalId} is already an alias here; a canonical id must not be one"
    let setFields (firmId: string) (keepZone: string) =
        s.Execute
            """UPDATE firms SET name=$2, time_zone=$3, account_id=$4, provisioning_updated_at=$5 WHERE id=$1"""
            [ firmId; name; Option.defaultValue keepZone zone; account; nowMs ]
        |> ignore
    match tryGet s canonicalId with
    | Some existing ->
        if not (String.IsNullOrWhiteSpace req.Adopt) && req.Adopt <> existing.FirmId then
            fail 409 $"{canonicalId} is already firm {existing.FirmId}; it cannot adopt {req.Adopt}"
        let changed =
            existing.Name <> name
            || (zone.IsSome && zone.Value <> existing.TimeZone)
            || existing.AccountId <> account
        if changed then setFields existing.FirmId existing.TimeZone
        (tryGet s canonicalId).Value, false
    | None ->
        let firmId =
            if String.IsNullOrWhiteSpace req.Adopt then
                let id = Secrets.newId "firm"
                Store.insertFirm
                    s
                    { Id = id
                      Name = name
                      KeyHash = null
                      KeyFingerprint = null
                      Active = true
                      ContactTypes = [||]
                      CustomLabels = [||]
                      CreatedAt = nowMs
                      TimeZone = Option.defaultValue Zones.company zone }
                Store.upsertFirmSource s id { System = Source.manual; SourceId = id; Name = name; Active = true }
                id
            else
                let f = Store.firmOrFail s req.Adopt
                match s.TryOne "SELECT canonical_id FROM firms WHERE id=$1" [ f.Id ] (fun r -> Read.str r 0) with
                | Some(other: string) when not (isNull other) ->
                    fail 409 $"firm {f.Id} is already provisioned as {other}"
                | _ -> f.Id
        let zoneNow = Store.firmOrFail s firmId |> _.TimeZone
        s.Execute "UPDATE firms SET canonical_id=$2, provisioned_at=$3 WHERE id=$1" [ firmId; canonicalId; nowMs ] |> ignore
        setFields firmId zoneNow
        Store.emit s "FirmProvisioned" firmId null null
        (tryGet s canonicalId).Value, true
