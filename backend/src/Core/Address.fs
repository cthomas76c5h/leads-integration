/// Where we may send a lead. A firm pastes this address, so it is untrusted:
/// without a check a hook could point into our own network (SSRF). Checked when
/// saved and again when sent, because DNS can change.
module LeadIntegrations.Address

open System
open System.Net
open System.Net.Sockets

type Policy = { AllowLocal: bool }

let private isLocalHost (host: string) = host = "localhost" || host = "127.0.0.1"

let isPrivate (ip: IPAddress) =
    let ip = if ip.IsIPv4MappedToIPv6 then ip.MapToIPv4() else ip
    if IPAddress.IsLoopback ip then true
    elif ip.AddressFamily = AddressFamily.InterNetworkV6 then
        ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal
    else
        match ip.GetAddressBytes() with
        | [| a; b; _; _ |] ->
            a = 10uy || a = 127uy || a = 0uy
            || (a = 169uy && b = 254uy) // link-local, includes cloud metadata 169.254.169.254
            || (a = 172uy && b >= 16uy && b <= 31uy)
            || (a = 192uy && b = 168uy)
            || (a = 100uy && b >= 64uy && b <= 127uy) // carrier-grade NAT
        | _ -> false

let checkShape (policy: Policy) (raw: string) : Result<Uri, string> =
    match Uri.TryCreate((if isNull raw then "" else raw.Trim()), UriKind.Absolute) with
    | false, _ -> Error "not a web address"
    | true, uri ->
        let local = policy.AllowLocal && isLocalHost uri.Host
        if uri.Scheme <> "https" && not (local && uri.Scheme = "http") then Error "must use https"
        elif uri.UserInfo <> "" then Error "must not contain a user name or password"
        else Ok uri

let checkResolves (policy: Policy) (uri: Uri) : Threading.Tasks.Task<Result<Uri, string>> =
    task {
        if policy.AllowLocal && isLocalHost uri.Host then return Ok uri
        else
            let! ips =
                task {
                    match IPAddress.TryParse uri.Host with
                    | true, ip -> return [| ip |]
                    | _ ->
                        try
                            return! Dns.GetHostAddressesAsync uri.Host
                        with _ ->
                            return [||]
                }
            if ips.Length = 0 then return Error $"cannot resolve {uri.Host}"
            elif ips |> Array.exists isPrivate then return Error $"{uri.Host} resolves to a private address"
            else return Ok uri
    }
