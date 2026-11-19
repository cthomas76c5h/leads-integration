/// The delivery worker: claims due outbox rows and sends them. One per
/// process; SQLite's single writer keeps two from claiming the same row.
module LeadIntegrations.Api.Worker

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open LeadIntegrations

type DeliveryWorker(db: Db.Database, opts: Dispatch.Options, logger: ILogger<DeliveryWorker>) =
    inherit BackgroundService()

    override _.ExecuteAsync(stop: CancellationToken) : Task =
        task {
            use http = Dispatch.newHttpClient ()
            let requeued = Db.write db (fun s -> Store.requeueInterrupted s (Domain.now ()))
            if requeued > 0 then logger.LogInformation("requeued {Count} deliveries interrupted by a restart", requeued)
            let mutable lastFallbackCheck = 0L
            while not stop.IsCancellationRequested do
                try
                    // Hand-offs running late go to the firm's fallback; checked every few seconds.
                    let t = Domain.now ()
                    if t - lastFallbackCheck > 5_000L then
                        lastFallbackCheck <- t
                        let n = Dispatch.runFallbacks db t
                        if n > 0 then logger.LogInformation("sent {Count} leads to their firm's fallback", n)
                    let! sent = Dispatch.runDue db http opts Random.Shared.NextDouble 20
                    if sent = 0 then do! Task.Delay(200, stop)
                with
                | :? OperationCanceledException -> ()
                | ex ->
                    logger.LogError(ex, "delivery worker pass failed")
                    do! Task.Delay(1000, stop)
        }
