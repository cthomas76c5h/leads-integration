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
            let log (message: string) (error: exn option) =
                match error with
                | Some ex -> logger.LogError(ex, message)
                | None -> logger.LogInformation(message)
            do! Dispatch.work db http opts Random.Shared.NextDouble log stop
        }
