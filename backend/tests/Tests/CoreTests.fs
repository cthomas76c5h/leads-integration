module LeadIntegrations.Tests.CoreTests

open System.Collections.Generic
open Expecto
open LeadIntegrations

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
              Expect.isTrue (match c (Some 410) with Policy.Disable _ -> true | _ -> false) "410 disables"
              Expect.isTrue (match c (Some 503) with Policy.Retry _ -> true | _ -> false) "5xx retries"
              Expect.isTrue (match c (Some 429) with Policy.Retry _ -> true | _ -> false) "429 retries"
              Expect.isTrue (match c (Some 302) with Policy.Failed _ -> true | _ -> false) "redirects are not followed"
              Expect.isTrue (match c (Some 422) with Policy.Failed _ -> true | _ -> false) "4xx fails"
              Expect.isTrue (match c None with Policy.Retry _ -> true | _ -> false) "no answer retries"
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
              Expect.equal (Policy.nextDelayMs 8 1.0 None mid) None "out of attempts"
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
