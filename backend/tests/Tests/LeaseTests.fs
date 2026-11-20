module LeadIntegrations.Tests.LeaseTests

open System
open System.IO
open System.Text.Json
open Expecto
open LeadIntegrations
open LeadIntegrations.Domain

/// A firm with one email destination (imported), and its id.
let private firmWithEmail () =
    let t = new Support.TempDb()
    Import.apply t.Db Support.origin (Support.batch Source.api2 [ Support.firm "1" "Lease Firm" "k" [ Support.conn "10" "email" [] ] ]) |> ignore
    t, Db.read t.Db (fun s -> (Store.allFirms s |> Array.head).Id)

let private lead callType = { Support.sampleLead () with CallType = callType }
let private later () = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1_000L

[<Tests>]
let tests =
    testList "lease" [
        test "one worker wins a claim; an expired lease is recovered, and the old version can start nothing" {
            let t, firmId = firmWithEmail ()
            use _ = t
            let _, ds = Dispatch.publish t.Db firmId (lead "*New Client Calls") false None None
            let id = ds[0].Id
            let first = Db.write t.Db (fun s -> Store.tryClaim s "wkr_a" (later ()) Dispatch.leaseMs id)
            Expect.isSome first "the first worker claims it"
            Expect.isNone (Db.write t.Db (fun s -> Store.tryClaim s "wkr_b" (later ()) Dispatch.leaseMs id)) "a second worker gets nothing"
            Expect.isTrue (Db.read t.Db (fun s -> Store.holdsLease s id first.Value.Version)) "the holder's version holds it"
            Db.write t.Db (fun s -> Store.expireLease s id (later ()))
            let second = Db.write t.Db (fun s -> Store.tryClaim s "wkr_b" (later ()) Dispatch.leaseMs id)
            Expect.isSome second "an expired lease is claimed again"
            Expect.isTrue second.Value.Recovered "as a recovery"
            Expect.isGreaterThan second.Value.Version first.Value.Version "with a higher version"
            Expect.isFalse (Db.read t.Db (fun s -> Store.holdsLease s id first.Value.Version)) "the old version no longer holds it"
        }

        test "the nearest hand-off deadline is claimed first" {
            let t, firmId = firmWithEmail ()
            use _ = t
            // A new matter (72-hour window), then an emergency (24 hours): the emergency goes first.
            let _, routine = Dispatch.publish t.Db firmId (lead "*New Client Calls") false None None
            let _, urgent = Dispatch.publish t.Db firmId (lead "*Emergency Calls") false None None
            let claims = Db.write t.Db (fun s -> Store.claimDue s "wkr_a" (later ()) Dispatch.leaseMs 1)
            Expect.equal (claims |> Array.map _.DeliveryId) [| urgent[0].Id |] "the emergency, though it came second"
            Expect.notEqual urgent[0].Id routine[0].Id "two deliveries"
        }

        test "an unknown outcome is sent again only where a second copy is harmless" {
            Expect.isTrue (Dispatch.retryAfterUnknown "harmless") "email, SMS, Slack"
            Expect.isTrue (Dispatch.retryAfterUnknown "idempotency-key") "the destination ignores a key it has seen"
            for d in [ "lookup"; "external-reference"; "none"; "unknown" ] do
                Expect.isFalse (Dispatch.retryAfterUnknown d) $"{d}: an operator checks first"
        }

        test "the kinds treated as harmless duplicates are the ones their receiver contracts declare" {
            let declared =
                Directory.GetFiles(Path.Combine(Db.repoRoot, "docs", "contracts", "receivers"), "*.json")
                |> Array.choose (fun f ->
                    use doc = JsonDocument.Parse(File.ReadAllText f)
                    let root = doc.RootElement
                    match root.TryGetProperty "kind", root.TryGetProperty "duplicates" with
                    | (true, k), (true, d) when d.GetString() = "harmless" -> Some(k.GetString())
                    | _ -> None)
                |> Set.ofArray
            Expect.equal Catalog.harmlessDuplicates declared "Catalog.harmlessDuplicates and the contracts agree"
        }

        test "a CRM that can be looked up is asked by our lead reference, at its own lookup" {
            Expect.equal
                (Dispatch.lookupUrl "http://localhost:5080/receiver/crm/clio-grow?dest=crm" "evt_1")
                "http://localhost:5080/receiver/crm/clio-grow/lookup?dest=crm&ref=evt_1"
                "beside the delivery address, keeping its query"
            Expect.equal (Dispatch.lookupUrl "http://localhost:5080/receiver/crm/mycase" "evt 2") "http://localhost:5080/receiver/crm/mycase/lookup?ref=evt%202" "escaped"
            let t, _ = firmWithEmail ()
            use _ = t
            Db.write t.Db (fun s -> Store.keepRecord s "clio-grow" "crm" "evt_1" 0L)
            Expect.isTrue (Db.read t.Db (fun s -> Store.hasRecord s "clio-grow" "crm" "evt_1")) "the fake CRM finds what it kept"
            Expect.isFalse (Db.read t.Db (fun s -> Store.hasRecord s "clio-grow" "crm" "evt_2")) "and nothing else"
            Expect.isFalse (Db.read t.Db (fun s -> Store.hasRecord s "clio-grow" "other" "evt_1")) "per destination"
        }

        test "the time to verify is set with the other delivery timings" {
            Expect.equal (Timing.verifyWithinMs ()) (2L * 3_600_000L) "2 hours, to confirm with data"
        }

        test "a resend is refused while the lead is still waiting or being sent to that destination" {
            let t, firmId = firmWithEmail ()
            use _ = t
            let _, ds = Dispatch.publish t.Db firmId (lead "*New Client Calls") false None None
            let id = ds[0].Id
            let refused () =
                try
                    Dispatch.replay t.Db id Unchecked.defaultof<Resend> |> ignore
                    None
                with Fail(status, message) -> Some(status, message)
            Expect.equal (refused () |> Option.map fst) (Some 409) "queued: refused"
            Db.write t.Db (fun s -> Store.tryClaim s "wkr_a" (later ()) Dispatch.leaseMs id) |> ignore
            Expect.stringContains (refused () |> Option.map snd |> Option.defaultValue "") "right now" "being sent: refused"
            Db.write t.Db (fun s -> Store.setDeliveryState s id "failed" "HTTP 503" (System.Nullable()))
            Expect.isNone (refused ()) "settled: a resend is allowed"
            Expect.equal (refused () |> Option.map fst) (Some 409) "and the resend itself now waits, so a second is refused"
        }
    ]
