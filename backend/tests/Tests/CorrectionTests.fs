module LeadIntegrations.Tests.CorrectionTests

open Expecto
open LeadIntegrations

let private firmWithEmail () =
    let t = new Support.TempDb()
    Import.apply t.Db Support.origin (Support.batch Domain.Source.legacy [ Support.firm "1" "Correction Firm" "k" [ Support.conn "10" "email" [] ] ]) |> ignore
    t, Db.read t.Db (fun s -> (Store.allFirms s |> Array.head).Id)

let private lead callType = { Support.sampleLead () with CallType = callType }

[<Tests>]
let tests =
    testList "corrections" [
        test "while the cycle is open, a correction re-rates the conversation and keeps both records" {
            let t, firmId = firmWithEmail ()
            use _ = t
            let r = Dispatch.intake t.Db firmId null (lead "*Vendor/Solicitation Calls")
            Expect.equal (Corrections.billing t.Db r.Event.Id).Is "free" "a vendor call is free"
            let v = Corrections.correct t.Db r.Event.Id "new-matter" null "reviewer" "Sam"
            Expect.equal v.Category "new-matter" "corrected"
            Expect.equal (v.Records |> Array.map _.Source) [| "intake"; "reviewer" |] "both records kept"
            Expect.equal (Corrections.billing t.Db r.Event.Id).Is "billable" "re-rated while the cycle is open"
            let summary = Db.read t.Db (fun s -> Store.leadOrFail s r.Event.Id)
            Expect.equal summary.Mapping.Category "new-matter" "reads show the current classification"
            Expect.equal summary.HandOff.WindowMs (Urgency.windowMs "vendor") "the window stands as judged at intake"
        }

        test "after the cycle closes, a lower charge is credited next cycle and a higher one is never charged" {
            let t, firmId = firmWithEmail ()
            use _ = t
            let billed = Dispatch.intake t.Db firmId null (lead "*New Personal Injury Case")
            let free = Dispatch.intake t.Db firmId null { lead "*Vendor/Solicitation Calls" with Phone = "+15555550188" }
            let rated = Db.write t.Db (fun s -> Billing.closeCycle s firmId (Domain.now () + 1L))
            Expect.equal rated 2 "both rated at the close"
            Corrections.correct t.Db billed.Event.Id "vendor" null "firm-contact" "Pat" |> ignore
            Corrections.correct t.Db free.Event.Id "new-matter" null "firm-contact" "Pat" |> ignore
            Expect.equal (Corrections.billing t.Db billed.Event.Id).Is "credited-next-cycle" "a credit"
            Expect.equal (Corrections.billing t.Db free.Event.Id).Is "free" "never a new charge"
            let names = Db.read t.Db Store.domainEvents |> Array.map (fun e -> e.Name, e.EventId)
            Expect.contains names ("UsageCredited", billed.Event.Id) "the credit is an event"
            Expect.isFalse (names |> Array.contains ("UsageCredited", free.Event.Id)) "no credit for the one left free"
            Expect.equal (Db.write t.Db (fun s -> Billing.closeCycle s firmId (Domain.now () + 2L))) 0 "a closed conversation is never rated again"
        }

        test "a correction names a real category, and changes something" {
            let t, firmId = firmWithEmail ()
            use _ = t
            let r = Dispatch.intake t.Db firmId null (lead "*General Calls")
            Expect.throws (fun () -> Corrections.correct t.Db r.Event.Id "new-matters" null "reviewer" "Sam" |> ignore) "a typo is refused"
            Expect.throws (fun () -> Corrections.correct t.Db r.Event.Id "general" null "reviewer" "Sam" |> ignore) "already general"
        }
    ]
