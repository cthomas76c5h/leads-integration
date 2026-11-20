module LeadIntegrations.Tests.IntakeTests

open Expecto
open LeadIntegrations

/// A firm with one email destination (imported), and its id.
let private firmWithEmail () =
    let t = new Support.TempDb()
    Import.apply t.Db Support.origin (Support.batch Domain.Source.api2 [ Support.firm "1" "Intake Firm" "k" [ Support.conn "10" "email" [] ] ]) |> ignore
    t, Db.read t.Db (fun s -> (Store.allFirms s |> Array.head).Id)

[<Tests>]
let tests =
    testList "intake" [
        test "a channel repeating a conversation's event gets the lead already recorded, and nothing is planned again" {
            let t, firmId = firmWithEmail ()
            use _ = t
            let first = Dispatch.intake t.Db firmId "call-7001" (Support.sampleLead ())
            let again = Dispatch.intake t.Db firmId "call-7001" (Support.sampleLead ())
            Expect.isFalse first.Duplicate "recorded"
            Expect.isTrue again.Duplicate "already recorded"
            Expect.equal again.Event.Id first.Event.Id "the same lead"
            let lead = Db.read t.Db (fun s -> Store.leadOrFail s first.Event.Id)
            Expect.equal lead.Deliveries.Length 1 "one delivery"
            Expect.equal lead.ConversationId "call-7001" "keyed on the conversation"
            let names = Db.read t.Db Store.domainEvents |> Array.map _.Name
            Expect.equal (names |> Array.filter ((=) "LeadRecorded") |> Array.length) 1 "recorded once"
        }

        test "another conversation, or none, is its own lead" {
            let t, firmId = firmWithEmail ()
            use _ = t
            let a = Dispatch.intake t.Db firmId "call-7001" (Support.sampleLead ())
            let b = Dispatch.intake t.Db firmId "call-7002" (Support.sampleLead ())
            let c = Dispatch.intake t.Db firmId null (Support.sampleLead ())
            let d = Dispatch.intake t.Db firmId null (Support.sampleLead ())
            Expect.equal (set [ a.Event.Id; b.Event.Id; c.Event.Id; d.Event.Id ]).Count 4 "four leads"
            Expect.isFalse (b.Duplicate || c.Duplicate || d.Duplicate) "none a repeat"
        }

        test "a lead the database could not take is not recorded at all, and is recorded once when sent again" {
            let t, firmId = firmWithEmail ()
            use _ = t
            t.Db.WritesFail <- true
            Expect.throws (fun () -> Dispatch.intake t.Db firmId "call-9001" (Support.sampleLead ()) |> ignore) "the write fails"
            t.Db.WritesFail <- false
            let count () = Db.read t.Db (fun s -> fst (Store.listLeads s { Q = null; FirmId = firmId; Category = null; Outcome = null; Test = System.Nullable(); Limit = 50; Offset = 0 }))
            Expect.equal (count ()) 0 "nothing recorded"
            Expect.isEmpty (Db.read t.Db Store.domainEvents) "nothing emitted"
            let r = Dispatch.intake t.Db firmId "call-9001" (Support.sampleLead ())
            Expect.isFalse r.Duplicate "recorded now, as new"
            Expect.equal (count ()) 1 "once"
        }
    ]
