module LeadIntegrations.Tests.CoreTests

open System.Collections.Generic
open Expecto
open LeadIntegrations
open LeadIntegrations.Domain

[<Tests>]
let tests =
    testList
        "core"
        [ test "a signed body verifies; a changed body does not" {
              let secret = Secrets.newSigningSecret ()
              let now = Domain.now ()
              let h = Signing.headers secret "evt_1" "{\"a\":1}" now |> dict
              Expect.equal (Signing.verify secret h["webhook-id"] h["webhook-timestamp"] h["webhook-signature"] "{\"a\":1}" now) (Ok()) "verifies"
              Expect.isError (Signing.verify secret h["webhook-id"] h["webhook-timestamp"] h["webhook-signature"] "{\"a\":2}" now) "tampered"
              Expect.isError (Signing.verify secret h["webhook-id"] h["webhook-timestamp"] h["webhook-signature"] "{\"a\":1}" (now + 600_000L)) "replayed"
          }

          test "response classification" {
              let c s = Policy.classify s null null 0L
              Expect.equal (c (Some 201)) Policy.Delivered "2xx"
              for code in [ 401; 403; 410 ] do
                  Expect.isTrue (match c (Some code) with Policy.NeedsReconnection _ -> true | _ -> false) $"{code}: needs reconnection"
              Expect.isTrue (match c (Some 503) with Policy.Retry _ -> true | _ -> false) "5xx retries"
              Expect.isTrue (match c (Some 429) with Policy.Retry _ -> true | _ -> false) "429 retries"
              Expect.isTrue (match c (Some 302) with Policy.Failed _ -> true | _ -> false) "redirects are not followed"
              Expect.isTrue (match c (Some 422) with Policy.Failed _ -> true | _ -> false) "4xx fails"
              Expect.isTrue (match c None with Policy.Retry _ -> true | _ -> false) "no answer retries"
          }

          test "only a senior operator resends an accepted lead, and says why" {
              let as' role why : Resend = { By = "Sam"; Role = role; Why = why }
              Expect.isOk (Dispatch.resendAllowed false false (as' "operator" null)) "any operator resends one that was not accepted"
              Expect.isOk (Dispatch.resendAllowed false false Unchecked.defaultof<Resend>) "without saying who (the bulk resend)"
              Expect.isError (Dispatch.resendAllowed true false (as' "operator" "missing")) "an operator may not resend an accepted lead"
              Expect.isError (Dispatch.resendAllowed true false Unchecked.defaultof<Resend>) "nor anyone unnamed"
              Expect.isError (Dispatch.resendAllowed true false (as' "senior-operator" " ")) "a senior operator gives the reason"
              Expect.isOk (Dispatch.resendAllowed true false (as' "senior-operator" "firm confirmed the record is missing")) "and then may"
              Expect.isOk (Dispatch.resendAllowed true true (as' "operator" null)) "a test lead is the operator's own check"
          }

          test "a timeout is retried only where a second copy is harmless; elsewhere it is unknown" {
              let timedOut = Policy.classify None "no answer in 10000 ms" null 0L
              use t = new Support.TempDb()
              Import.apply t.Db Support.origin (Support.batch Source.legacy [ Support.firm "1" "Timeout Firm" "k" [ Support.conn "10" "email" []; Support.conn "11" "mycase" [] ] ]) |> ignore
              let rows = Db.read t.Db (fun s -> Store.allConnections s)
              let conn kind (settings: Map<string, string>) =
                  let c = (rows |> Array.find (fun (r: Store.ConnectionRow) -> r.Connection.Kind = kind)).Connection
                  let d = System.Collections.Generic.Dictionary<string, string>(c.Settings)
                  for KeyValue(k, v) in settings do d[k] <- v
                  { c with Settings = d }
              let isUnknown o = match o with Policy.Unknown _ -> true | _ -> false
              Expect.isFalse (isUnknown (Dispatch.outcomeOf (conn "email" Map.empty) true timedOut)) "email retries"
              Expect.isTrue (isUnknown (Dispatch.outcomeOf (conn "mycase" Map.empty) true timedOut)) "a CRM waits for an operator"
              Expect.isFalse (isUnknown (Dispatch.outcomeOf (conn "mycase" (Map.ofList [ "duplicates", "idempotency-key" ])) true timedOut)) "unless it deduplicates"
              Expect.isFalse (isUnknown (Dispatch.outcomeOf (conn "mycase" Map.empty) false timedOut)) "a refused connection sent nothing: transient"
              let t = Dispatch.transition (conn "mycase" Map.empty) false 1 (Dispatch.outcomeOf (conn "mycase" Map.empty) true timedOut) (fun _ -> Some 1000L)
              Expect.equal t.Status "unknown" "not retried"
              Expect.isNone t.Connection "the failure streak is untouched"
          }

          test "Retry-After in seconds and as a date" {
              Expect.equal (Policy.parseRetryAfter "120" 0L) (Some 120_000L) "seconds"
              let now = System.DateTimeOffset(2026, 11, 18, 12, 0, 0, System.TimeSpan.Zero)
              let later = now.AddSeconds(30.0).ToString "R"
              Expect.equal (Policy.parseRetryAfter later (now.ToUnixTimeMilliseconds())) (Some 30_000L) "date"
              Expect.equal (Policy.parseRetryAfter "soon" 0L) None "garbage"
          }

          test "backoff respects Retry-After and runs out" {
              let mid () = 0.5 // no jitter
              Expect.equal (Policy.nextDelayMs 1 1.0 None mid) (Some 5_000L) "first retry"
              Expect.equal (Policy.nextDelayMs 1 1.0 (Some 60_000L) mid) (Some 60_000L) "Retry-After wins"
              let last = Policy.retryScheduleMs.Length
              Expect.equal (Policy.nextDelayMs last 1.0 None mid) (Some(24L * 3_600_000L)) "the last wait: a day"
              Expect.equal (Policy.nextDelayMs (last + 1) 1.0 None mid) None "out of attempts"
              Expect.isGreaterThan (Array.sum Policy.retryScheduleMs) (60L * 3_600_000L) "retries cover most of the 72-hour window"
          }

          test "billing cycles start at midnight New York time, in summer and in winter" {
              let utc (y, m, d, h, mi) = System.DateTimeOffset(y, m, d, h, mi, 0, System.TimeSpan.Zero).ToUnixTimeMilliseconds()
              // 00:30 on July 1 in New York (EDT, UTC-4) is in July's cycle, which began at 04:00 UTC.
              let summerFrom, _ = Billing.cycleOf 1 (utc (2026, 7, 1, 4, 30))
              Expect.equal summerFrom (utc (2026, 7, 1, 4, 0)) "summer: midnight is 04:00 UTC"
              // 23:30 on June 30 in New York is still June's.
              let juneFrom, juneTo = Billing.cycleOf 1 (utc (2026, 7, 1, 3, 30))
              Expect.equal juneFrom (utc (2026, 6, 1, 4, 0)) "the previous cycle"
              Expect.equal juneTo summerFrom "ends where July's starts"
              // In winter (EST, UTC-5) midnight is 05:00 UTC.
              let winterFrom, _ = Billing.cycleOf 1 (utc (2026, 12, 1, 5, 30))
              Expect.equal winterFrom (utc (2026, 12, 1, 5, 0)) "winter: midnight is 05:00 UTC"
          }

          test "office hours are the firm's own: 6:30 pm in New York is 3:30 pm in Los Angeles" {
              let t = System.DateTimeOffset(2026, 11, 18, 23, 30, 0, System.TimeSpan.Zero).ToUnixTimeMilliseconds() // a Wednesday
              Expect.isTrue (Report.afterHours (Zones.find "America/New_York") t) "after hours in New York"
              Expect.isFalse (Report.afterHours (Zones.find "America/Los_Angeles") t) "office hours in Los Angeles"
              Expect.isNone (Zones.tryFind "Eastern-ish") "not an IANA name"
              Expect.equal (Zones.find "Eastern-ish").Id (Zones.find Zones.company).Id "an unknown zone falls back to the company's"
          }

          test "an older database gains the time zone column when opened, with the default" {
              let path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"li-old-{System.Guid.NewGuid():N}.db")
              try
                  do
                      use c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False")
                      c.Open()
                      use cmd = c.CreateCommand()
                      cmd.CommandText <- "CREATE TABLE firms (id TEXT PRIMARY KEY, name TEXT NOT NULL, key_hash TEXT NULL, key_fingerprint TEXT NULL, active INTEGER NOT NULL DEFAULT 1, contact_types TEXT NOT NULL DEFAULT '[]', custom_labels TEXT NOT NULL DEFAULT '[]', created_at INTEGER NOT NULL); INSERT INTO firms (id, name, created_at) VALUES ('firm_old', 'Old Firm', 1);"
                      cmd.ExecuteNonQuery() |> ignore
                  let db = Db.openDatabase path
                  let f = Db.read db (fun s -> Store.firmOrFail s "firm_old")
                  Expect.equal f.TimeZone "America/New_York" "existing firms get the company's zone"
              finally
                  Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
                  for suffix in [ ""; "-wal"; "-shm" ] do
                      try System.IO.File.Delete(path + suffix) with _ -> ()
          }

          test "delivery timings: read from the settings file; a malformed one never runs" {
              let s = Timing.parse """{"windowToAct":{"default":"72h","emergency":"24h"},"fallbackAfter":{"default":"1h"},"retrySchedule":["5s","2d"]}"""
              Expect.equal s.WindowToAct["default"] (72L * 3_600_000L) "hours"
              Expect.equal s.RetrySchedule [| 5_000L; 2L * 86_400_000L |] "seconds and days"
              Expect.throws (fun () -> Timing.parse """{"windowToAct":{"emergency":"24h"},"fallbackAfter":{"default":"1h"},"retrySchedule":["5s"]}""" |> ignore) "a default is required"
              Expect.throws (fun () -> Timing.parse """{"windowToAct":{"default":"soon"},"fallbackAfter":{"default":"1h"},"retrySchedule":["5s"]}""" |> ignore) "durations must read"
              Expect.equal (Timing.windowToActMs "some-new-category") (72L * 3_600_000L) "an unknown category gets the default"
          }

          test "template values are JSON-escaped; unknown custom field is empty; unknown field is an error" {
              let vars = dict [ ("full_name", "Jane \"JD\" Doe") ] :> IDictionary<_, _>
              let r = Template.render """{"n":"{{full_name}}","c":"{{custom.nope|default:'x'}}"}""" vars
              Expect.isTrue r.Ok "renders"
              Expect.equal r.Body """{"n":"Jane \"JD\" Doe","c":"x"}""" "escaped"
              Expect.isFalse (Template.render """{"n":"{{fulname}}"}""" vars).Ok "typo is an error"
          }

          test "address rules" {
              let strict: Address.Policy = { AllowLocal = false }
              let lab: Address.Policy = { AllowLocal = true }
              Expect.isOk (Address.checkShape strict "https://hooks.zapier.com/hooks/catch/1/x/") "https"
              Expect.isError (Address.checkShape strict "http://example.com/") "http refused"
              Expect.isError (Address.checkShape strict "https://user:pw@example.com/") "credentials in URL refused"
              Expect.isOk (Address.checkShape lab "http://localhost:5080/receiver/ok") "local allowed in the lab"
              Expect.isTrue (Address.isPrivate (System.Net.IPAddress.Parse "169.254.169.254")) "metadata address is private"
          }

          test "fingerprints never contain the value and match for equal values" {
              let fp = Secrets.fingerprint "sk_live_abcdef123456"
              Expect.stringStarts fp "••••" "masked"
              Expect.isFalse (fp.Contains "3456") "not the tail of the value"
              Expect.equal (Secrets.fingerprint " sk_live_abcdef123456 ") fp "trimmed"
              Expect.isNull (Secrets.fingerprint "") "absent"
          }

          test "mysqldump values parse with escapes and NULL" {
              let rows = Importers.MySqlDump.parseValues """(1,'O\'Brien & Co',NULL,'a\\b'),(2,'two''s',3.5,'x')"""
              Expect.equal rows [ [ "1"; "O'Brien & Co"; null; "a\\b" ]; [ "2"; "two's"; "3.5"; "x" ] ] "parsed"
          } ]
