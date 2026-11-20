/// Credentials are described, never stored (RFC-001 §8; the MVP imports
/// "shape only"). A fingerprint lets two people confirm they are looking at the
/// same key without either of them seeing it; the match hash lets the importer
/// recognise one firm across systems by its Answering Legal key.
module LeadIntegrations.Secrets

open System
open System.Security.Cryptography
open System.Text

// A fixed, app-specific prefix: the hashes are useless outside this system.
let private salt = "lead-integrations:v1:"

let private sha256Hex (text: string) =
    SHA256.HashData(Encoding.UTF8.GetBytes(salt + text)) |> Convert.ToHexStringLower

let isPresent (value: string) = not (String.IsNullOrWhiteSpace value)

/// '••••a1b2': the last four hex digits of a salted hash, never of the value.
let fingerprint (value: string) : string =
    if isPresent value then "••••" + (sha256Hex (value.Trim())).Substring(60) else null

/// The cross-system match key for an Answering Legal API key. Null when absent.
let matchHash (value: string) : string =
    if isPresent value then sha256Hex ("match:" + value.Trim()) else null

/// A random secret we generate ourselves (webhook signing), never imported.
let newSigningSecret () =
    "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes 24)

/// A firm user's sign-in token as it is stored: a salted hash, never the token.
let tokenHash (token: string) : string =
    if isPresent token then sha256Hex ("token:" + token.Trim()) else null

let newToken () =
    Convert.ToBase64String(RandomNumberGenerator.GetBytes 16).Replace('+', '-').Replace('/', '_').TrimEnd('=')

let newId (prefix: string) =
    prefix + "_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes 8)
