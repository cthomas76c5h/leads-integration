/// Standard Webhooks signatures (standardwebhooks.com), ported from webhook-lab:
///   webhook-id:        the event id (same on every retry, so receivers can dedupe)
///   webhook-timestamp: unix seconds of this attempt
///   webhook-signature: v1,<base64 HMAC-SHA256 of "{id}.{timestamp}.{body}">
module LeadIntegrations.Signing

open System
open System.Security.Cryptography
open System.Text

let toleranceSeconds = 300L

let private keyOf (secret: string) =
    Convert.FromBase64String(if secret.StartsWith "whsec_" then secret.Substring 6 else secret)

let sign (secret: string) (id: string) (timestamp: int64) (body: string) =
    use mac = new HMACSHA256(keyOf secret)
    "v1," + Convert.ToBase64String(mac.ComputeHash(Encoding.UTF8.GetBytes $"{id}.{timestamp}.{body}"))

let headers (secret: string) (id: string) (body: string) (nowMs: int64) : (string * string) list =
    let ts = nowMs / 1000L
    [ "webhook-id", id; "webhook-timestamp", string ts; "webhook-signature", sign secret id ts body ]

let verify (secret: string) (id: string) (timestamp: string) (signatures: string) (body: string) (nowMs: int64) : Result<unit, string> =
    match Int64.TryParse timestamp with
    | _ when String.IsNullOrEmpty id || String.IsNullOrEmpty signatures -> Error "missing headers"
    | false, _ -> Error "missing headers"
    | true, ts when abs (nowMs / 1000L - ts) > toleranceSeconds -> Error "timestamp too old or in the future"
    | true, ts ->
        let expected = Encoding.UTF8.GetBytes(sign secret id ts body)
        let matches (candidate: string) =
            let got = Encoding.UTF8.GetBytes candidate
            got.Length = expected.Length && CryptographicOperations.FixedTimeEquals(ReadOnlySpan got, ReadOnlySpan expected)
        if signatures.Split ' ' |> Array.exists matches then Ok() else Error "no signature matches"
