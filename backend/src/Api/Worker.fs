/// The delivery worker: leases due deliveries and sends them (RFC-008 §5.3). Several may run:
/// a lease is one conditional write, so only one worker wins each delivery.
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
            // Nothing is requeued on a restart: a lease left by the old process runs out, and recovery
            // decides by what had started (RFC-008 §5.3), so a lead in flight is never resent blindly.
            let mutable lastFallbackCheck = 0L
            while not stop.IsCancellationRequested do
                try
                    // Hand-offs running late go to the firm's fallback; checked every few seconds.
                    let t = Domain.now ()
                    if t - lastFallbackCheck > 5_000L then
                        lastFallbackCheck <- t
                        let n = Dispatch.runFallbacks db t
                        if n > 0 then logger.LogInformation("sent {Count} leads to their firm's fallback", n)
                        let h = Dispatch.runHandOffs db t
                        if h > 0 then logger.LogInformation("{Count} hand-offs escalated or expired", h)
                        let! v = Dispatch.runVerifications db http opts t
                        if v > 0 then logger.LogInformation("{Count} deliveries verified or flagged", v)
                    let! sent = Dispatch.runDue db http opts Random.Shared.NextDouble 20
                    if sent = 0 then do! Task.Delay(200, stop)
                with
                | :? OperationCanceledException -> ()
                | ex ->
                    logger.LogError(ex, "delivery worker pass failed")
                    do! Task.Delay(1000, stop)
        }
