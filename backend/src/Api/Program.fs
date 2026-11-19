module LeadIntegrations.Api.Program

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open LeadIntegrations

[<EntryPoint>]
let main args =
    match Config.load () with
    | Error message ->
        eprintfn "config: %s" message
        1
    | Ok cfg ->
        let db = Db.openDatabase cfg.DbPath
        let builder = WebApplication.CreateBuilder args
        builder.WebHost.UseUrls($"http://localhost:{cfg.Port}") |> ignore
        let app = App.build builder cfg db
        printfn "lead-integrations on %s  (db %s, TIME_SCALE=%g)" cfg.Origin db.Path cfg.TimeScale
        app.Run()
        0
