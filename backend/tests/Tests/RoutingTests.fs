module LeadIntegrations.Tests.RoutingTests

open System.Collections.Generic
open Expecto
open LeadIntegrations
open LeadIntegrations.Domain

let private firm (activeTypes: string list) : Store.FirmRow =
    { Id = "firm_1"; Name = "Example Firm A"; KeyHash = null; KeyFingerprint = null; Active = true
      ContactTypes = activeTypes |> List.map (fun n -> { Name = n; Active = true }) |> Array.ofList
      CustomLabels = [||]; CreatedAt = 0L; TimeZone = "America/New_York" }

let private dest (kind: string) (settings: (string * string) list) : Store.ConnectionRow =
    { Connection =
        { Id = "con_" + kind; FirmId = "firm_1"; FirmName = "Example Firm A"; Kind = kind; Name = kind
          Source = Source.manual; SourceRef = null; Credentials = [||]
          Settings = Dictionary(dict (("to", "+15555550199") :: ("recipients", "intake@example.com") :: settings))
          Enabled = true; Status = "active"; DisabledReason = null; ConsecutiveFailures = 0
          DeliveryUrl = $"http://localhost/receiver/crm/{kind}"; Issues = [||]; CreatedAt = 0L }
      SigningSecret = "secret"
      Template = null }

let private lead callType = { Support.sampleLead () with CallType = callType }
let private hangUp callType = { lead callType with FirstName = ""; LastName = ""; Email = "" }
let private newMatters = [ Routing.categoriesKey, "new-matter" ]

[<Tests>]
let tests =
    testList "routing" [
        test "a destination takes the categories its rule names, and records why" {
            let d = Routing.decide (firm []) (dest "mycase" newMatters) (lead "*New Personal Injury Case")
            Expect.equal d.Outcome "planned" "a new matter"
            Expect.equal d.MatchedOn "new-matter" "matched on its category"
            Expect.equal d.Category "new-matter" "classification recorded"
            Expect.equal d.PracticeArea "personal-injury" "practice area recorded"
            Expect.equal d.TaxonomyVersion (Categories.version ()) "taxonomy version recorded"
            let billing = Routing.decide (firm []) (dest "mycase" newMatters) (lead "*Billing/Payment Calls")
            Expect.equal billing.Outcome "not-sent-routing" "a billing call"
            Expect.stringContains billing.Reason "billing" "the reason names the call"
        }
        test "each destination decides on its own: one lead, different rules, different decisions" {
            let f = firm []
            let l = lead "*Existing Client Calls"
            let crm = Routing.decide f (dest "mycase" newMatters) l
            let crm2 = Routing.decide f (dest "lead-docket" [ Routing.categoriesKey, "existing-client" ]) l
            let email = Routing.decide f (dest "email" []) l
            Expect.equal [ crm.Outcome; crm2.Outcome; email.Outcome ] [ "not-sent-routing"; "planned"; "planned" ] "independent rules"
            Expect.equal email.MatchedOn "every call" "email with no rule takes everything"
        }
        test "practice areas narrow new matters only" {
            let pi = dest "mycase" [ Routing.categoriesKey, "new-matter,jail-custody"; Routing.practiceAreasKey, "personal-injury" ]
            Expect.equal (Routing.decide (firm []) pi (lead "*New Personal Injury Case")).Outcome "planned" "personal injury"
            Expect.equal (Routing.decide (firm []) pi (lead "*New Divorce/Custody Call")).Outcome "not-sent-routing" "family law"
            Expect.equal (Routing.decide (firm []) pi (lead "*Jail Calls")).Outcome "planned" "practice areas do not narrow other categories"
        }
        test "chat leads off excludes chats, whatever the category" {
            let d = Routing.decide (firm []) (dest "mycase" (("sendWebchats", "false") :: newMatters)) { lead "*New Personal Injury Case" with Webchat = true }
            Expect.equal d.Outcome "not-sent-routing" "a chat"
            Expect.stringContains d.Rule "no chat leads" "the rule says so"
        }
        test "with no rule yet, a CRM keeps the legacy rule: the firm's active call types" {
            let f = firm [ "*New Client Calls" ]
            Expect.equal (Routing.decide f (dest "mycase" []) (lead "*New Client Calls")).Outcome "planned" "an active type"
            Expect.equal (Routing.decide f (dest "mycase" []) (lead "*New Personal Injury Case")).Outcome "not-sent-routing" "another new-matter name"
            Expect.equal (Routing.decide f (dest "mycase" newMatters) (lead "*New Personal Injury Case")).Outcome "planned" "a category rule ignores the label"
        }
        test "a hang-up to a CRM that needs a name is a lead problem; email still takes it" {
            let f = firm []
            let crm = Routing.decide f (dest "mycase" newMatters) (hangUp "*New Client Calls")
            Expect.equal crm.Outcome "not-sent-lead" "mycase requires a name"
            Expect.isTrue (Diagnose.isLeadProblem crm.Reason) "marked as a lead problem"
            Expect.stringContains crm.Reason "first_name" "names the missing field"
            Expect.equal (Routing.decide f (dest "clio-grow" newMatters) (hangUp "*New Client Calls")).Outcome "planned" "Clio Grow fills a blank name"
            Expect.equal (Routing.decide f (dest "email" []) (hangUp "*New Client Calls")).Outcome "planned" "email takes it"
        }
        test "routing comes before a lead problem: an excluded hang-up is not sent for routing" {
            let d = Routing.decide (firm []) (dest "mycase" newMatters) (hangUp "*Billing/Payment Calls")
            Expect.equal d.Outcome "not-sent-routing" "routing decides first"
        }
        test "a CRM's rule is seeded from the firm's active call types, or not at all" {
            let types (xs: (string * bool) list) = xs |> List.map (fun (n, a) -> { Name = n; Active = a }) |> Array.ofList
            let today = types [ "*New Client Calls", true; "*New Personal Injury Case", true; "*Court Calls", false; "* No Charge", false ]
            Expect.equal (Routing.seed today) (Some([ "new-matter" ], [])) "the default: new matters"
            let withGeneral = types [ "*New Client Calls", true; "*General Calls", true; "*Billing/Payment Calls", false ]
            Expect.equal (Routing.seed withGeneral |> Option.map (fst >> List.sort)) (Some [ "general"; "new-matter" ]) "a firm's choice of general"
            let byArea = types [ "*New Personal Injury Case", true; "*New Divorce/Custody Call", false ]
            Expect.equal (Routing.seed byArea) (Some([ "new-matter" ], [ "personal-injury" ])) "new matters narrowed to an area"
            let split = types [ "*New Client Calls", true; "*General Calls", true; "*Other Calls", false ]
            Expect.isNone (Routing.seed split) "general split between active and inactive: left for review"
            Expect.isNone (Routing.seed (types [ "*Court Calls", false ])) "no active types"
        }
        test "a field the setup never fills is not the lead's problem" {
            // A webhook whose receiver requires a field no lead fills: missing even for a complete lead, so the send reports it.
            let hook = { dest "webhook" [] with Connection = { (dest "webhook" []).Connection with DeliveryUrl = "http://localhost/receiver/crm/webhook?require=Nope" } }
            Expect.equal (Routing.decide (firm []) hook (hangUp "*New Client Calls")).Outcome "planned" "left for the send"
        }

        test "a routing change goes live only when approved; waiting deliveries keep their revision unless the firm applies it to them" {
            let setup applyToWaiting =
                let t = new Support.TempDb()
                Import.apply t.Db Support.origin (Support.batch Source.legacy [ Support.firm "1" "Revision Firm" "k" [ Support.conn "10" "mycase" [] ] ]) |> ignore
                let firmId, c = Db.read t.Db (fun s -> let f = Store.allFirms s |> Array.head in f.Id, (Store.connectionsOf s f.Id |> Array.head).Connection)
                let settings = Dictionary<string, string>(c.Settings)
                settings[Routing.categoriesKey] <- "new-matter,existing-client"
                Db.write t.Db (fun s -> Store.updateConnection s { c with Settings = settings })
                let existing = lead "*Existing Client Calls"
                Expect.equal (Categories.classify existing.CallType).Category "existing-client" "an existing-client call"
                let _, waiting = Dispatch.publish t.Db firmId existing false None None
                Expect.equal (Dispatch.proposeRouting t.Db c.Id [| "new-matter" |] [||] "operator") 2 "the draft would be revision 2"
                let _, beforeApproval = Dispatch.publish t.Db firmId existing false None None
                Expect.equal beforeApproval[0].Decision.Revision 1 "a draft changes nothing"
                Expect.equal beforeApproval[0].Status "pending" "still taken by the live rule"
                let r = Dispatch.approveRouting t.Db c.Id "firm contact" applyToWaiting
                Expect.equal r.revision 2 "revision 2 is live"
                let after id = (Db.read t.Db (fun s -> Store.deliveryOrFail s id)).Delivery
                let _, next = Dispatch.publish t.Db firmId existing false None None
                Expect.equal next[0].Status "skipped" "the next lead uses the new rule"
                Expect.equal next[0].Decision.Revision 2 "and says which revision"
                Expect.throws (fun () -> Dispatch.approveRouting t.Db c.Id "firm contact" false |> ignore) "nothing left to approve"
                t, after waiting[0].Id, r.cancelled
            let t, kept, cancelled = setup false
            use _ = t
            Expect.equal kept.Status "pending" "kept on its revision"
            Expect.equal kept.Decision.Revision 1 "revision 1"
            Expect.equal cancelled 0 "nothing cancelled"
            let t2, applied, cancelled2 = setup true
            use _ = t2
            Expect.equal applied.Status "cancelled" "the new rule excludes it"
            Expect.stringContains applied.Reason "revision 2" "and says why"
            Expect.equal applied.Decision.Revision 2 "decided again under revision 2"
            Expect.equal cancelled2 2 "both waiting deliveries"
        }

        test "a routing change names real categories" {
            use t = new Support.TempDb()
            Expect.throws (fun () -> Dispatch.proposeRouting t.Db "con_x" [| "new-matters" |] [||] "operator" |> ignore) "a typo is refused"
            Expect.throws (fun () -> Dispatch.proposeRouting t.Db "con_x" [||] [||] "operator" |> ignore) "and an empty rule"
        }
    ]
