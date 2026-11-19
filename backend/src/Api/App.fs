/// Builds the web application. Tests build it with a temp database and the
/// worker off, and drive it through TestServer.
module LeadIntegrations.Api.App

open System
open System.IO
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.FileProviders
open Microsoft.Extensions.Logging
open Oxpecker
open LeadIntegrations

/// An unexpected exception becomes a logged 500 { error }.
let private serverErrors (ctx: HttpContext) (next: RequestDelegate) : Threading.Tasks.Task =
    task {
        try
            do! next.Invoke ctx
        with ex ->
            ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("LeadIntegrations.Api")
                .LogError(ex, "server error {Method} {Path}", ctx.Request.Method, ctx.Request.Path.Value)
            if not ctx.Response.HasStarted then
                ctx.Response.Clear()
                do! Http.error 500 "internal error" ctx
    }

let build (builder: WebApplicationBuilder) (cfg: Config.Config) (db: Db.Database) : WebApplication =
    builder.Services.AddOxpecker() |> ignore
    builder.Services.AddSingleton<Db.Database>(db) |> ignore
    builder.Services.AddSingleton<Dispatch.Options>(Config.dispatchOptions cfg) |> ignore
    if cfg.WorkerEnabled then builder.Services.AddHostedService<Worker.DeliveryWorker>() |> ignore
    let app = builder.Build()
    app.Use(serverErrors) |> ignore
    // The built frontend, when present: one process serves both.
    let dist = Path.GetFullPath cfg.FrontendDist
    let serveFrontend = Directory.Exists dist
    if serveFrontend then
        let files = new PhysicalFileProvider(dist)
        app.UseDefaultFiles(DefaultFilesOptions(FileProvider = files)).UseStaticFiles(StaticFileOptions(FileProvider = files)) |> ignore
    app.UseRouting() |> ignore
    app.UseEndpoints(fun e ->
        e.MapOxpeckerEndpoints(Routes.endpoints { Db = db; Cfg = cfg })
        if serveFrontend then e.MapFallbackToFile("index.html", StaticFileOptions(FileProvider = new PhysicalFileProvider(dist))) |> ignore)
    |> ignore
    app
