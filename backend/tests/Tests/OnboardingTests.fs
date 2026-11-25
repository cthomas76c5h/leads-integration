module LeadIntegrations.Tests.OnboardingTests

open Expecto
open LeadIntegrations
open LeadIntegrations.Tests.Support

let private lab: Address.Policy = { AllowLocal = true }

let private firmId (t: TempDb) =
    Import.apply t.Db origin (batch Domain.Source.legacy [ firm "1" "Revision & Co" "k" [] ]) |> ignore
    (Db.read t.Db (fun s -> Store.listFirms s { Q = null; Kind = null; Source = null; IssuesOnly = false; Limit = 5; Offset = 0 }) |> snd).[0].Id

/// Mark a setup's test delivery as accepted (no worker runs in these tests).
let private accept (t: TempDb) (v: Onboarding.SetupView) =
    Db.write t.Db (fun s -> Store.setDeliveryState s v.TestDeliveryId "delivered" null (System.Nullable()))

let private approveTestConfirm (t: TempDb) (id: string) =
    let v = Onboarding.send t.Db id
    let s = Onboarding.loadByToken t.Db v.Token
    Onboarding.decide t.Db s.Token "approved" "Office manager" "" s.Version
    let v = Onboarding.test t.Db id
    accept t v
    Onboarding.confirm t.Db id

[<Tests>]
let tests =
    testList
        "onboarding"
        [ test "editing a live webhook setup makes a draft; live keeps the active template until activation" {
              use t = new TempDb()
              let v = Onboarding.create t.Db (firmId t) "webhook" "notify-staff" null
              Expect.equal v.State "Connection needed" "starts unconnected"
              Onboarding.connect t.Db lab origin v.Token "http://localhost/receiver/ok"
              approveTestConfirm t v.Id |> ignore
              let live = Onboarding.goLive t.Db v.Id
              Expect.equal live.State "Live" "live"
              let template () = (Db.read t.Db (fun s -> Store.connectionOrFail s live.ConnectionId)).Template
              let v1 = template ()
              let rows = Array.append live.Rows [| { From = "email"; To = "Email" } |]
              let draft = Onboarding.update t.Db v.Id null rows
              Expect.equal draft.Version 2 "new version"
              Expect.isTrue draft.Revising "revising"
              Expect.equal draft.State "Draft" "draft state"
              Expect.equal (template ()) v1 "live template unchanged"
              Expect.throws (fun () -> Onboarding.goLive t.Db v.Id |> ignore) "cannot activate an unapproved draft"
              // The draft's test carries the draft body.
              let tested = Onboarding.send t.Db v.Id
              Onboarding.decide t.Db tested.Token "approved" "Partner" "" 2
              let tested = Onboarding.test t.Db v.Id
              let d = Db.read t.Db (fun s -> Store.deliveryOrFail s tested.TestDeliveryId)
              Expect.stringContains (Option.get d.Template) "\"Email\"" "test uses the draft mapping"
              Expect.equal (template ()) v1 "still unchanged"
              accept t tested
              Onboarding.confirm t.Db v.Id |> ignore
              let active = Onboarding.goLive t.Db v.Id
              Expect.equal active.ActiveVersion (System.Nullable 2) "activated"
              Expect.stringContains (template ()) "\"Email\"" "live template switched"
          }

          test "discarding a draft returns to live and never reuses its version number" {
              use t = new TempDb()
              let v = Onboarding.create t.Db (firmId t) "zapier-webhook" "create-intake" null
              Onboarding.connect t.Db lab origin v.Token "http://localhost/receiver/ok"
              approveTestConfirm t v.Id |> ignore
              let live = Onboarding.goLive t.Db v.Id
              Expect.throws (fun () -> Onboarding.discard t.Db v.Id |> ignore) "nothing to discard"
              Onboarding.update t.Db v.Id null (Array.append live.Rows [| { From = "email"; To = "Email" } |]) |> ignore
              Onboarding.send t.Db v.Id |> ignore
              Onboarding.decide t.Db v.Token "approved" "Partner" "" 2
              let back = Onboarding.discard t.Db v.Id
              Expect.equal back.Version 1 "back on live version"
              Expect.equal back.State "Live" "live"
              Expect.throws (fun () -> Onboarding.decide t.Db v.Token "approved" "Partner" "" 1) "a stale page cannot approve live again"
              let next = Onboarding.update t.Db v.Id null (Array.append live.Rows [| { From = "call_type"; To = "Matter" } |])
              Expect.equal next.Version 3 "v2 never reused"
              Expect.equal next.State "Draft" "v2's approval does not cover v3"
          }

          test "a change request needs a comment" {
              use t = new TempDb()
              let v = Onboarding.create t.Db (firmId t) "clio-grow" "create-intake" null
              Onboarding.connect t.Db lab origin v.Token null
              Expect.throws (fun () -> Onboarding.decide t.Db v.Token "changes" "A" "  " 1) "comment required"
              Onboarding.decide t.Db v.Token "changes" "A" "Use the family-law inbox" 1
              Expect.equal (Onboarding.get t.Db v.Id).State "Changes requested" "state"
          } ]
