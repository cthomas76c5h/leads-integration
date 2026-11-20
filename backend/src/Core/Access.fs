/// Firm isolation (RFC-008 §9): a firm's people sign in with a token an operator issues, and every
/// firm-facing route answers only for the token's firm. Another firm's lead is answered as if it did
/// not exist, so a refusal reveals nothing, and every refusal is audited (AccessRefused).
module LeadIntegrations.Access

open System
open LeadIntegrations.Db
open LeadIntegrations.Domain

type FirmUser = { Id: string; FirmId: string; Name: string; Role: string }

type Refusal =
    { At: int64
      UserId: string
      FirmId: string
      Boundary: string
      Target: string
      Outcome: string }

/// An operator issues a firm user's sign-in token. Returned once; only its hash is kept.
let issue (db: Database) (firmId: string) (name: string) (role: string) : FirmUser * string =
    if String.IsNullOrWhiteSpace name then fail 422 "name is required"
    let role = if role = "admin" then "admin" else "member"
    let token = "fut_" + Secrets.newToken () + Secrets.newToken ()
    let user = { Id = Secrets.newId "usr"; FirmId = firmId; Name = name.Trim(); Role = role }
    write db (fun s ->
        Store.firmOrFail s firmId |> ignore
        s.Execute
            "INSERT INTO firm_users (id, firm_id, name, role, token_hash, created_at) VALUES ($1,$2,$3,$4,$5,$6)"
            [ user.Id; firmId; user.Name; role; Secrets.tokenHash token; now () ]
        |> ignore)
    user, token

/// The user a bearer token signs in, or None.
let signedIn (db: Database) (token: string) : FirmUser option =
    if String.IsNullOrWhiteSpace token then None
    else
        read db (fun s ->
            s.TryOne
                "SELECT id, firm_id, name, role FROM firm_users WHERE token_hash=$1"
                [ Secrets.tokenHash token ]
                (fun r -> { Id = Read.str r 0; FirmId = Read.str r 1; Name = Read.str r 2; Role = Read.str r 3 }))

/// Record a refused request and emit AccessRefused (firm: the requester's; lead: what it asked for, if a lead).
let refuse (db: Database) (u: FirmUser) (boundary: string) (target: string) (leadId: string) =
    write db (fun s ->
        s.Execute
            "INSERT INTO access_audit (at, user_id, firm_id, boundary, target, outcome) VALUES ($1,$2,$3,$4,$5,'refused')"
            [ now (); u.Id; u.FirmId; boundary; target ]
        |> ignore
        Store.emit s "AccessRefused" u.FirmId leadId null)

let refusals (db: Database) : Refusal[] =
    read db (fun s ->
        s.Query
            "SELECT at, user_id, firm_id, boundary, target, outcome FROM access_audit ORDER BY seq"
            []
            (fun r -> { At = Read.int64 r 0; UserId = Read.str r 1; FirmId = Read.str r 2; Boundary = Read.str r 3; Target = Read.str r 4; Outcome = Read.str r 5 }))

/// The lead, if it is the user's firm's; otherwise the refusal is audited and the answer is "no such lead".
let leadFor (db: Database) (u: FirmUser) (boundary: string) (leadId: string) : Store.LeadDetail =
    let found =
        read db (fun s ->
            match s.TryOne "SELECT firm_id, test FROM events WHERE id=$1" [ leadId ] (fun r -> Read.str r 0, Read.bool r 1) with
            | Some(firmId, false) when firmId = u.FirmId -> Some(Store.leadOrFail s leadId)
            | _ -> None)
    match found with
    | Some l -> l
    | None ->
        // Another firm's lead, a test lead, or none at all get the same answer, so the answer reveals nothing.
        // Each is audited: probing for ids is itself worth seeing.
        refuse db u boundary leadId leadId
        fail 404 "no such lead"
