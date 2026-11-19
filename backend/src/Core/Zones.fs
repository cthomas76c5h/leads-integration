/// Time zones. Every timestamp is UTC milliseconds and every delivery rule is an
/// elapsed duration, so delivery needs no time zone. Calendars do: a firm's office
/// hours and the dates on what it reads are in the firm's own zone; billing cycles
/// are on the company's zone, daylight saving included, so every invoice shares
/// one calendar. IANA names (America/New_York), never fixed offsets.
module LeadIntegrations.Zones

open System

/// The company's zone: billing cycles and price-book dates, and every firm's default.
let company = "America/New_York"

/// The zone for an IANA region name (Area/Location, like America/Los_Angeles); None for
/// anything else, including abbreviations (PST) and offsets (-05:00), which the system
/// would otherwise accept as aliases but which do not follow daylight saving or a region.
let tryFind (id: string) : TimeZoneInfo option =
    if String.IsNullOrWhiteSpace id || not (Text.RegularExpressions.Regex.IsMatch(id, @"^[A-Za-z]+(/[A-Za-z0-9_+\-]+)+$")) then None
    else
        try
            Some(TimeZoneInfo.FindSystemTimeZoneById id)
        with
        | :? TimeZoneNotFoundException
        | :? InvalidTimeZoneException -> None

/// The zone for an IANA name, or the company's when it is missing or unknown.
let find (id: string) : TimeZoneInfo =
    match tryFind id with
    | Some z -> z
    | None -> TimeZoneInfo.FindSystemTimeZoneById company

/// The wall-clock time in a zone for a UTC instant.
let toLocal (zone: TimeZoneInfo) (t: int64) : DateTime =
    TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds t, zone).DateTime

/// The UTC instant of a wall-clock time in a zone. Midnight is never skipped or
/// repeated by daylight saving in the zones we serve (changes happen at 2 am).
let fromLocal (zone: TimeZoneInfo) (local: DateTime) : int64 =
    let wall = DateTime.SpecifyKind(local, DateTimeKind.Unspecified)
    DateTimeOffset(wall, zone.GetUtcOffset wall).ToUnixTimeMilliseconds()
