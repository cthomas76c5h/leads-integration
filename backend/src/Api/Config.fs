/// Settings from the environment, with defaults that run the lab locally.
module LeadIntegrations.Api.Config

open System

type Config =
    { Port: int
      DbPath: string
      /// The address the fake receivers are reached at; imported connections deliver there.
      Origin: string
      TimeScale: float
      DeliveryTimeoutMs: int
      AllowLocalTargets: bool
      WorkerEnabled: bool
      /// frontend/dist, served at / when present (production-like single process).
      FrontendDist: string }

let private env (name: string) (fallback: string) =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> fallback
    | v -> v.Trim()

let private parse (name: string) (fallback: string) (f: string -> bool * 'T) : Result<'T, string> =
    let raw = env name fallback
    match f raw with
    | true, v -> Ok v
    | _ -> Error $"{name}: cannot read \"{raw}\""

let load () : Result<Config, string> =
    let inv = Globalization.CultureInfo.InvariantCulture
    let port = parse "PORT" "5080" Int32.TryParse
    let scale = parse "TIME_SCALE" "600" (fun s -> Double.TryParse(s, Globalization.NumberStyles.Float, inv))
    let timeout = parse "DELIVERY_TIMEOUT_MS" "10000" Int32.TryParse
    let allowLocal = parse "ALLOW_LOCAL_TARGETS" "true" Boolean.TryParse
    let worker = parse "WORKER_ENABLED" "true" Boolean.TryParse
    match port, scale, timeout, allowLocal, worker with
    | Ok port, Ok scale, Ok timeout, Ok allowLocal, Ok worker ->
        if scale <= 0.0 then Error "TIME_SCALE must be positive"
        else
            Ok
                { Port = port
                  DbPath = env "DB_PATH" LeadIntegrations.Db.defaultPath
                  Origin = env "ORIGIN" $"http://localhost:{port}"
                  TimeScale = scale
                  DeliveryTimeoutMs = timeout
                  AllowLocalTargets = allowLocal
                  WorkerEnabled = worker
                  FrontendDist = env "FRONTEND_DIST" (IO.Path.Combine(LeadIntegrations.Db.repoRoot, "frontend", "dist")) }
    | Error e, _, _, _, _
    | _, Error e, _, _, _
    | _, _, Error e, _, _
    | _, _, _, Error e, _
    | _, _, _, _, Error e -> Error e

let addressPolicy (cfg: Config) : LeadIntegrations.Address.Policy = { AllowLocal = cfg.AllowLocalTargets }

let dispatchOptions (cfg: Config) : LeadIntegrations.Dispatch.Options =
    { TimeScale = cfg.TimeScale
      TimeoutMs = cfg.DeliveryTimeoutMs
      Address = addressPolicy cfg
      UserAgent = "AnsweringAI-Webhooks/0.1" }
