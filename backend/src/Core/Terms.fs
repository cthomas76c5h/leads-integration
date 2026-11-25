/// Terms of service (docs/terms/strategy.md): one global, versioned document; every
/// user accepts at first sign-in; a new version asks for acceptance again only when its
/// publisher says so, and only from the users it concerns (firm admins, or everyone);
/// minor edits never lock anyone out; every acceptance is recorded with who and when.
module LeadIntegrations.Terms

open System
open LeadIntegrations.Db
open LeadIntegrations.Domain

type Version =
    { Version: int
      Title: string
      /// What changed, in plain words: the portal notice shows it.
      Summary: string
      Body: string
      /// editorial | pricing | material
      ChangeKind: string
      /// none | admins | all: who must accept this version again
      Require: string
      PublishedAt: int64
      /// From here a required acceptance blocks the portal; before it, a notice asks.
      EffectiveAt: int64
      Source: string }

type Acceptance = { Version: int; FirmId: string; FirmName: string; UserId: string; Role: string; At: int64 }

/// The publisher's choice, suggested from the kind of change.
let defaultRequire (kind: string) =
    match kind with
    | "material" -> "all"
    | "pricing" -> "admins"
    | _ -> "none"

let private cols = "version, title, summary, body, change_kind, require, published_at, effective_at, source"

let private read (r: System.Data.Common.DbDataReader) : Version =
    { Version = Read.int r 0; Title = Read.str r 1; Summary = Read.str r 2; Body = Read.str r 3; ChangeKind = Read.str r 4
      Require = Read.str r 5; PublishedAt = Read.int64 r 6; EffectiveAt = Read.int64 r 7; Source = Read.str r 8 }

let versions (s: Session) = s.Query $"SELECT {cols} FROM terms_versions ORDER BY version DESC" [] read

let publish (s: Session) (title: string) (summary: string) (body: string) (kind: string) (require: string) (effectiveAt: int64) (source: string) (nowMs: int64) =
    if String.IsNullOrWhiteSpace body then fail 422 "the terms have no text"
    if String.IsNullOrWhiteSpace summary then fail 422 "say what changed: the portal notice shows it"
    if not (List.contains kind [ "editorial"; "pricing"; "material" ]) then fail 422 "change kind must be editorial, pricing or material"
    if not (List.contains require [ "none"; "admins"; "all" ]) then fail 422 "require must be none, admins or all"
    let next = (s.Query "SELECT COALESCE(MAX(version), 0) FROM terms_versions" [] (fun r -> Read.int r 0))[0] + 1
    s.Execute
        $"INSERT INTO terms_versions ({cols}) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9)"
        [ next; title; summary; body; kind; require; nowMs; max nowMs effectiveAt; source ]
    |> ignore
    next

let accept (s: Session) (version: int) (firmId: string) (userId: string) (role: string) (nowMs: int64) =
    s.Execute
        "INSERT INTO terms_acceptances (version, firm_id, user_id, role, at) VALUES ($1,$2,$3,$4,$5) ON CONFLICT DO NOTHING"
        [ version; firmId; userId; role; nowMs ]
    |> ignore

let private accepted (s: Session) (firmId: string) (userId: string) =
    s.Query "SELECT version FROM terms_acceptances WHERE firm_id=$1 AND user_id=$2" [ firmId; userId ] (fun r -> Read.int r 0) |> Set.ofArray

/// What a user sees on signing in.
type Gate =
    { /// block | notice | none
      Mode: string
      Version: Nullable<int>
      Title: string
      Summary: string
      /// Why: first sign-in, or a version that asks this user to accept again.
      Reason: string
      DueAt: Nullable<int64> }

let private concerns (role: string) (require: string) = require = "all" || (require = "admins" && role = "admin")

let gate (s: Session) (firmId: string) (userId: string) (role: string) (nowMs: int64) : Gate =
    let vs = versions s |> Array.filter (fun v -> v.PublishedAt <= nowMs)
    let mine = accepted s firmId userId
    match Array.tryHead vs with
    | None -> { Mode = "none"; Version = Nullable(); Title = null; Summary = null; Reason = null; DueAt = Nullable() }
    | Some latest when mine.IsEmpty ->
        // First sign-in: the current terms, before anything else.
        { Mode = "block"; Version = Nullable latest.Version; Title = latest.Title; Summary = null
          Reason = "Accept the terms of service to start using the portal."; DueAt = Nullable() }
    | Some latest ->
        // The newest version that concerns this user and they have not accepted.
        match vs |> Array.tryFind (fun v -> concerns role v.Require && not (mine.Contains v.Version)) with
        | Some v ->
            let due = v.EffectiveAt > nowMs
            { Mode = (if due then "notice" else "block"); Version = Nullable v.Version; Title = v.Title; Summary = v.Summary
              Reason = (if role = "admin" && v.Require = "admins" then "As your firm's admin, please review and accept the updated terms." else "Please review and accept the updated terms.")
              DueAt = (if due then Nullable v.EffectiveAt else Nullable()) }
        | None when not (mine.Contains latest.Version) ->
            // A version that does not ask for acceptance: a notice of what changed, nothing to click.
            { Mode = "notice"; Version = Nullable latest.Version; Title = latest.Title; Summary = latest.Summary
              Reason = "The terms were updated. No action is needed."; DueAt = Nullable() }
        | None -> { Mode = "none"; Version = Nullable(); Title = null; Summary = null; Reason = null; DueAt = Nullable() }

/// Per version: of the users it asked, how many accepted.
type Coverage = { Version: int; Asked: int; Accepted: int }

let users (s: Session) =
    // The lab's synthetic portal users: one admin and one member per billing account.
    s.Query "SELECT firm_id FROM billing_accounts" [] (fun r -> Read.str r 0)
    |> Array.collect (fun f -> [| f, $"{f}:admin", "admin"; f, $"{f}:member", "member" |])

let coverage (s: Session) : Coverage[] =
    let us = users s
    versions s
    |> Array.map (fun v ->
        let asked = us |> Array.filter (fun (_, _, role) -> v.Require <> "none" && concerns role v.Require)
        let got =
            s.Query "SELECT firm_id, user_id FROM terms_acceptances WHERE version=$1" [ v.Version ] (fun r -> Read.str r 0, Read.str r 1) |> Set.ofArray
        { Version = v.Version
          Asked = asked.Length
          Accepted = asked |> Array.filter (fun (f, u, _) -> got.Contains(f, u)) |> Array.length })

let acceptances (s: Session) (version: int) (limit: int) =
    s.Query
        "SELECT a.version, a.firm_id, f.name, a.user_id, a.role, a.at FROM terms_acceptances a JOIN firms f ON f.id = a.firm_id WHERE a.version=$1 ORDER BY a.at DESC LIMIT $2"
        [ version; limit ]
        (fun r -> { Version = Read.int r 0; FirmId = Read.str r 1; FirmName = Read.str r 2; UserId = Read.str r 3; Role = Read.str r 4; At = Read.int64 r 5 })

// ----- Lab seed: the history the business actually went through, as versions -----

let private clauses =
    [ "1. The service", "Answering AI answers your calls and chats, takes intake in your call categories, and delivers each lead to the destinations you choose."
      "2. Your account", "Your firm's admins manage users, destinations and billing. Each user is responsible for keeping their sign-in private."
      "3. Fees and billing", "You pay the subscription for your plan each billing cycle, plus any conversations beyond those included, at the rates in the price book in force. Calls that are free by rule are listed on your statement."
      "4. Your data", "Leads belong to your firm. We keep only what intake collected and what delivery needs, and we never sell caller information."
      "5. Changes to these terms", "We tell you in the portal what changed. If a change needs your acceptance, we ask the people it concerns before it takes effect." ]

let private bodyOf (extra: (string * string) list) =
    clauses @ extra |> List.map (fun (h, t) -> $"{h}\n{t}") |> String.concat "\n\n"

let seed (s: Session) (nowMs: int64) =
    for sql in [ "DELETE FROM terms_acceptances"; "DELETE FROM terms_versions" ] do
        s.Execute sql [] |> ignore
    let day = 86_400_000L
    let promotions = [ ("6. Promotions", "Promotional offers have their own terms, shown when you take them up; they end on the date stated.") ]
    let local = DateTimeOffset.FromUnixTimeMilliseconds(nowMs - 5L * 3_600_000L)
    let nextMonth = DateTimeOffset(local.Year, local.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1).ToUnixTimeMilliseconds() + 5L * 3_600_000L
    let history =
        [ "Terms of Service", "First version of the terms.", bodyOf [], "material", "all", nowMs - 120L * day, nowMs - 120L * day
          "Terms of Service", "Grammar and formatting only; nothing changes for you.", bodyOf [], "editorial", "none", nowMs - 60L * day, nowMs - 60L * day
          "Terms of Service", "A new section 6 on promotions: offers have their own terms and end dates.", bodyOf promotions, "material", "admins", nowMs - 20L * day, nowMs - 20L * day
          "Terms of Service",
          "Prices change from the first of next month (price book version 2, about 5%). Your plan and included conversations stay the same.",
          bodyOf promotions, "pricing", "admins", nowMs - 2L * day, nextMonth ]
    for title, summary, body, kind, require, published, effective in history do
        s.Execute
            $"INSERT INTO terms_versions ({cols}) VALUES ((SELECT COALESCE(MAX(version), 0) + 1 FROM terms_versions),$1,$2,$3,$4,$5,$6,$7,'docx')"
            [ title; summary; body; kind; require; published; effective ]
        |> ignore
    // Everyone accepted version 1 at first sign-in; most admins accepted the promotions
    // version; a few have already accepted the pricing notice.
    for i, (firmId, userId, role) in Array.indexed (users s) do
        accept s 1 firmId userId role (nowMs - (110L - int64 (i % 90)) * day)
        if role = "admin" && i % 10 < 7 then accept s 3 firmId userId role (nowMs - (18L - int64 (i % 15)) * day - int64 (i % 41) * 1_380_000L)
        if role = "admin" && i % 10 < 3 then accept s 4 firmId userId role (nowMs - day - int64 (i % 37) * 1_380_000L)
