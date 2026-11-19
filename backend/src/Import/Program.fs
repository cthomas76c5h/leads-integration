/// dotnet run --project src/Import -- <command> [options]
///
///   api2     --pg "Host=localhost;Port=5432;Database=leads;Username=postgres"
///            (password from pgpass.conf or PGPASSWORD; the session is read-only)
///   chatbot  --dump ../../chatbot/backup.sql
///   airi     --sqlite ../../airi/client-portal/devdata.sqlite
///   all      runs the three with their defaults
///   seed     --leads 200 --seed 42 [--firm firm_…]
///            publishes synthetic leads (invented people, the firms' own call
///            types) to firms with an enabled connection; the API's worker
///            delivers them to the local fake receivers
///   demo-names
///            replaces every firm's name with a stable fictional one, for demos
///            and screenshots (re-importing restores the real names)
///   simulate --days 7 --seed 7 --crm-leads 40
///            replaces the delivery history with a simulated week over the
///            existing leads (outage, timeouts, dead endpoint, 410, expired
///            auth, redirect + replay, pause, tests, a few still retrying);
///            stop the API first or it may send while this rewrites
///
/// Common: --db data/lead-integrations.db  --origin http://localhost:5080
module LeadIntegrations.Importers.Program

open System
open LeadIntegrations

let private arg (args: string[]) (name: string) (fallback: string) =
    match args |> Array.tryFindIndex ((=) name) with
    | Some i when i + 1 < args.Length -> args[i + 1]
    | _ -> fallback

let private github = IO.Path.GetFullPath(IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..", ".."))

let private report (run: Domain.ImportRun) =
    printfn "%s: %d firms, %d connections, %d skipped" run.System run.Firms run.Connections run.Skipped
    for n in run.Notes do
        printfn "  - %s" n

[<EntryPoint>]
let main args =
    let command = if args.Length > 0 then args[0] else "help"
    let db () = Db.openDatabase (arg args "--db" (Environment.GetEnvironmentVariable "DB_PATH" |> Option.ofObj |> Option.defaultValue Db.defaultPath))
    let origin = arg args "--origin" (Environment.GetEnvironmentVariable "ORIGIN" |> Option.ofObj |> Option.defaultValue "http://localhost:5080")
    let pg = arg args "--pg" (Environment.GetEnvironmentVariable "API2_PG" |> Option.ofObj |> Option.defaultValue "Host=localhost;Port=5432;Database=leads;Username=postgres")
    let dump = arg args "--dump" (IO.Path.Combine(github, "chatbot", "backup.sql"))
    let sqlite = arg args "--sqlite" (IO.Path.Combine(github, "airi", "client-portal", "devdata.sqlite"))
    let run name (read: unit -> Import.Batch) =
        try
            let batch = read ()
            report (Import.apply (db ()) origin batch)
            0
        with ex ->
            eprintfn "%s import failed: %s" name ex.Message
            1
    match command with
    | "api2" -> run "api2" (fun () -> Api2.read pg 12)
    | "chatbot" -> run "chatbot" (fun () -> Chatbot.read dump)
    | "airi" -> run "airi" (fun () -> Airi.read sqlite)
    | "all" ->
        // api-2 first: it has the most firms, so others merge into its names.
        [ run "api2" (fun () -> Api2.read pg 12)
          run "chatbot" (fun () -> Chatbot.read dump)
          run "airi" (fun () -> Airi.read sqlite) ]
        |> List.max
    | "seed" ->
        try
            let opts =
                { Seed.defaults with
                    Count = int (arg args "--leads" (string Seed.defaults.Count))
                    RandomSeed = int (arg args "--seed" (string Seed.defaults.RandomSeed))
                    FirmId = arg args "--firm" null }
            let s = Seed.run (db ()) opts
            printfn "seed: %d leads to %d firms -> %d deliveries queued, %d skipped by routing" s.Leads s.Firms (s.Deliveries - s.Skipped) s.Skipped
            printfn "  %d hang-ups, %d web chats" s.HangUps s.Chats
            for c, n in s.ByCategory do
                printfn "  %-20s %d" c n
            printfn "Deliveries are sent by the API's worker (dotnet run --project src/Api)."
            0
        with
        | Domain.Fail(_, message) ->
            eprintfn "seed failed: %s" message
            1
        | :? FormatException ->
            eprintfn "seed: --leads and --seed take numbers"
            2
    | "demo-names" ->
        let n = Db.write (db ()) DemoNames.apply
        printfn "demo-names: %d firms renamed to fictional names; run `simulate` next so delivery bodies carry them" n
        0
    | "simulate" ->
        try
            let opts =
                { Simulate.defaults with
                    Days = int (arg args "--days" (string Simulate.defaults.Days))
                    RandomSeed = int (arg args "--seed" (string Simulate.defaults.RandomSeed))
                    CrmLeads = int (arg args "--crm-leads" (string Simulate.defaults.CrmLeads))
                    Origin = origin }
            let r = Simulate.run (db ()) opts
            let day (ms: int64) = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString "ddd d MMM HH:mm"
            printfn "simulate: %s to %s" (day r.From) (day r.To)
            printfn "  %d leads (%d added for firms with a CRM), %d tests" r.Leads r.AddedLeads r.Tests
            printfn "  %d deliveries, %d attempts, %d replays" r.Deliveries r.Attempts r.Replays
            for status, n in r.ByStatus do
                printfn "    %-10s %d" status n
            printfn "  scenarios:"
            for sc in r.Scenarios do
                printfn "    %-16s %s" sc.Name sc.Where
            printfn "  stopped (switched off, or needs reconnection after a 401, 403 or 410):"
            for name, reason in r.Disabled do
                printfn "    %s: %s" name reason
            printfn "Deliveries still retrying are sent by the API's worker (dotnet run --project src/Api)."
            0
        with
        | Domain.Fail(_, message) ->
            eprintfn "simulate failed: %s" message
            1
        | :? FormatException ->
            eprintfn "simulate: --days, --seed and --crm-leads take numbers"
            2
    | _ ->
        printfn "usage: import (api2 | chatbot | airi | all | seed | demo-names | simulate) [--db path] [--origin url] [--pg cs] [--dump path] [--sqlite path] [--leads n] [--seed n] [--firm id] [--days n] [--crm-leads n]"
        if command = "help" then 0 else 2
