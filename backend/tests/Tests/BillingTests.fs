module LeadIntegrations.Tests.BillingTests

open System
open Expecto
open LeadIntegrations
open LeadIntegrations.Domain

let private minute = 60_000L
let private day = 86_400_000L
/// Thursday 19 Nov 2026, noon Eastern.
let private now = DateTimeOffset(2026, 11, 19, 17, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()

let private lead first callType phone = { Support.sampleLead () with FirstName = first; LastName = (if first = "" then "" else "Example"); CallType = callType; Phone = phone }

/// One firm with a week of conversations and a billing account on the 15th (seeded like the lab).
let private firmWithAccount () =
    let t = new Support.TempDb()
    let f = { Support.firm "1" "Billing Firm" "k" [ Support.conn "10" "email" [] ] with ContactTypes = [| { Name = "*New Client Calls"; Active = true } |] }
    Import.apply t.Db Support.origin (Support.batch Domain.Source.legacy [ f ]) |> ignore
    let firmId = Db.read t.Db (fun s -> (Store.allFirms s |> Array.head).Id)
    let record at (l: Lead) =
        Db.write t.Db (fun s -> Store.insertEvent s { Id = Secrets.newId "evt"; FirmId = firmId; Test = false; CreatedAt = at; Lead = l })
    record (now - 3L * day) (lead "Avery" "*New Client Calls" "+15555550101")
    record (now - 2L * day) (lead "Jordan" "*Vendor Calls" "+15555550102")
    record (now - 2L * day + 2L * minute) (lead "" "*New Client Calls" "+15555550103")
    record (now - day) (lead "Riley" "*New Client Calls" "+15555550104")
    record (now - day + 5L * minute) (lead "Riley" "*New Client Calls" "+15555550104")
    Db.write t.Db (fun s ->
        Billing.seedPriceBook s now
        Billing.seedAccounts s now (fun _ -> "answer") None None
        s.Execute "UPDATE billing_accounts SET cycle_day=15" [] |> ignore)
    t, firmId

[<Tests>]
let tests =
    testList
        "billing and terms"
        [ test "a cycle runs from its anchor day to the same day next month, on the company's calendar" {
              let from, until = Billing.cycleOf 15 now
              // Dates on the company's calendar, daylight saving included (November 1, 2026 is still EDT at midnight).
              let et (ms: int64) = Zones.toLocal (Zones.find Zones.company) ms
              Expect.equal ((et from).Month, (et from).Day) (11, 15) "from the 15th"
              Expect.equal ((et until).Month, (et until).Day) (12, 15) "to the 15th of next month"
              let from1, _ = Billing.cycleOf 1 now
              Expect.equal (et from1).Day 1 "or the 1st"
          }

          test "free by rule, each conversation recording the rule" {
              let t, firmId = firmWithAccount ()
              use _ = t
              let st = Db.write t.Db (fun s -> Billing.statement s firmId "current" now)
              Expect.equal st.Conversations 5 "every conversation"
              let rule r = st.FreeByRule |> Array.tryFind (fun x -> x.Rule = r) |> Option.map _.Count |> Option.defaultValue 0
              Expect.equal (rule "solicitation@1") 1 "a vendor call is free"
              Expect.equal (rule "hang-up-20s@1") 1 "an instant hang-up is free"
              Expect.equal (rule "repeat-10m@1") 1 "a call back within 10 minutes is free"
              Expect.equal st.Billable 2 "the two real conversations are billed"
              Expect.equal (st.Lines |> Array.sumBy _.AmountCents) st.TotalCents "the lines add up"
          }

          test "an upgrade mid-cycle splits the cycle, prorated by day; a downgrade waits for the next cycle" {
              let t, firmId = firmWithAccount ()
              use _ = t
              let before = Db.write t.Db (fun s -> Billing.statement s firmId "current" now)
              let plan = before.Segments[0].PlanName
              let starts = Db.write t.Db (fun s -> Billing.changePlan s firmId "deliver-50@1" now)
              Expect.equal starts now "an upgrade applies at once"
              let after = Db.write t.Db (fun s -> Billing.statement s firmId "current" now)
              Expect.equal after.Segments.Length 2 "two segments"
              Expect.stringContains after.Segments[0].Note "prorated" "the first is prorated"
              Expect.isGreaterThan after.TotalCents before.TotalCents "and the bill rises"
              let downTo = Db.write t.Db (fun s -> Billing.changePlan s firmId "answer-50@1" now)
              Expect.equal downTo (snd (Billing.cycleOf 15 now)) $"a downgrade from {plan} starts with the next cycle"
          }

          test "a closed cycle is locked: later plan changes do not rewrite it" {
              let t, firmId = firmWithAccount ()
              use _ = t
              let first = Db.write t.Db (fun s -> Billing.statement s firmId "previous" now)
              Expect.isTrue first.LockedAt.HasValue "locked on first read"
              Db.write t.Db (fun s -> Billing.changePlan s firmId "insight-500@1" now) |> ignore
              let again = Db.write t.Db (fun s -> Billing.statement s firmId "previous" now)
              Expect.equal again.TotalCents first.TotalCents "the past cycle keeps its total"
              Expect.equal again.Segments[0].PlanName first.Segments[0].PlanName "and its plan"
          }

          test "the legacy shadow flags the data the old formula depends on" {
              let t, firmId = firmWithAccount ()
              use _ = t
              Db.write t.Db (fun s -> s.Execute "UPDATE billing_accounts SET legacy_overage='see contract'" [] |> ignore)
              let st = Db.write t.Db (fun s -> Billing.statement s firmId "current" now)
              Expect.isFalse st.Legacy.TotalCents.HasValue "no legacy total without a numeric rate"
              Expect.exists st.Legacy.Problems (fun p -> p.Contains "not a number") "and it says why"
              let row = Db.write t.Db (fun s -> Billing.accountRows s now) |> Array.head
              Expect.isFalse row.Ready "not ready for the new billing until fixed"
          }

          test "demo names: every firm fictional, stable, unique, and a trades business stays a trades business" {
              use t = new Support.TempDb()
              let firms =
                  [ Support.firm "1" "Real Law Office of Someone" "k1" []
                    Support.firm "2" "Another Real Firm LLP" "k2" []
                    Support.firm "3" "Valley Heating & Air - Test" "k3" [] ]
              Import.apply t.Db Support.origin (Support.batch Domain.Source.legacy firms) |> ignore
              Db.write t.Db DemoNames.apply |> ignore
              let names = Db.read t.Db (fun s -> Store.allFirms s |> Array.map (fun f -> f.Id, f.Name))
              Expect.isFalse (names |> Array.exists (fun (_, n) -> n.Contains "Real" || n.Contains "Valley")) "no real name left"
              Expect.equal (names |> Array.map snd |> Array.distinct).Length names.Length "unique"
              Expect.isTrue (names |> Array.exists (fun (_, n) -> n.Contains "Heating" || n.Contains "Plumbing" || n.Contains "Home Services")) "the trades firm keeps a trades name"
              Db.write t.Db DemoNames.apply |> ignore
              Expect.equal (Db.read t.Db (fun s -> Store.allFirms s |> Array.map (fun f -> f.Id, f.Name))) names "the same names every run"
              let sources = Db.read t.Db (fun s -> s.Query "SELECT name FROM firm_sources" [] (fun r -> r.GetString 0))
              Expect.isFalse (sources |> Array.exists (fun n -> n.Contains "Real")) "source records renamed too"
          }

          test "terms: first sign-in blocks; editorial changes never lock anyone out; scoped versions ask only whom they concern" {
              let t, firmId = firmWithAccount ()
              use _ = t
              let adminId, memberId = $"{firmId}:admin", $"{firmId}:member"
              let gate user role = Db.read t.Db (fun s -> Terms.gate s firmId user role now)
              let publish kind require effective =
                  Db.write t.Db (fun s -> Terms.publish s "Terms" "What changed" "Text" kind require effective "text" (now - day))
              let v1 = publish "material" "all" 0L
              Expect.equal (gate memberId "member").Mode "block" "first sign-in: accept before anything else"
              Db.write t.Db (fun s -> Terms.accept s v1 firmId memberId "member" now; Terms.accept s v1 firmId adminId "admin" now)
              let v2 = publish "editorial" "none" 0L
              Expect.equal (gate memberId "member").Mode "notice" "an editorial change is a notice, not a lock"
              Expect.equal (gate memberId "member").Version (Nullable v2) "about that version"
              let v3 = publish "pricing" "admins" 0L
              Expect.equal (gate adminId "admin").Mode "block" "a pricing change asks admins"
              Expect.notEqual (gate memberId "member").Mode "block" "and never blocks members"
              Db.write t.Db (fun s -> Terms.accept s v3 firmId adminId "admin" now)
              Expect.notEqual (gate adminId "admin").Mode "block" "accepted: the portal opens"
              publish "material" "all" (now + 5L * day) |> ignore
              let due = gate memberId "member"
              Expect.equal (due.Mode, due.DueAt.HasValue) ("notice", true) "before its effective date: a notice with the date"
              Expect.throws (fun () -> Db.write t.Db (fun s -> Terms.publish s "T" "" "x" "editorial" "none" 0L "text" now) |> ignore) "a version must say what changed"
              Expect.equal (Terms.defaultRequire "pricing") "admins" "pricing defaults to admins"
          } ]
