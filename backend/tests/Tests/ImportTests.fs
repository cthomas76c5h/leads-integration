module LeadIntegrations.Tests.ImportTests

open Expecto
open LeadIntegrations
open LeadIntegrations.Tests.Support

[<Tests>]
let tests =
    testList
        "import"
        [ test "one firm across systems is matched by its account key, and re-import is idempotent" {
              use t = new TempDb()
              let legacyBatch =
                  batch Domain.Source.legacy [ firm "17" "Rose & Associates" "al_key_rose" [ conn "501" "mycase" [ "token", "tok"; "refresh_token", "ref" ] ] ]
              let chatbot =
                  batch Domain.Source.chatbot [ firm "4" "Rose and Associates PLLC" "al_key_rose" [ conn "tenant-4-web-lead" "answering-legal" [ ("api_key", "al_key_rose") ] ] ]
              Import.apply t.Db origin legacyBatch |> ignore
              Import.apply t.Db origin chatbot |> ignore
              Import.apply t.Db origin legacyBatch |> ignore // again: no duplicates
              let total, firms =
                  Db.read t.Db (fun s ->
                      Store.listFirms s { Q = null; Kind = null; Source = null; IssuesOnly = false; Limit = 50; Offset = 0 })
              Expect.equal total 1 "one firm"
              Expect.equal (Array.sort firms[0].Sources) [| "chatbot"; "legacy" |] "known to both"
              Expect.equal firms[0].Name "Rose & Associates" "first name kept"
              let detail = Db.read t.Db (fun s -> Store.firmDetail s firms[0].Id [||])
              Expect.equal detail.Connections.Length 2 "two connections, not three"
              Expect.equal (Db.read t.Db Store.overview).FirmsInSeveralSources 1 "overview counts the match"
          }

          test "no credential value is stored anywhere in the database" {
              use t = new TempDb()
              let secret = "SECRET-VALUE-0123456789"
              Import.apply
                  t.Db
                  origin
                  (batch Domain.Source.legacy [ firm "1" "A" secret [ conn "9" "clio-grow" [ "api_key", secret ] ] ])
              |> ignore
              // Every row of every table, as text.
              let text =
                  Db.read t.Db (fun s ->
                      let tables = s.Query "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'" [] (fun r -> r.GetString 0)
                      tables
                      |> Array.map (fun table -> s.Query $"SELECT COALESCE(string_agg(x::text, '|'), '') FROM {table} x" [] (fun r -> r.GetString 0) |> Array.head)
                      |> String.concat "|")
              Expect.isFalse (text.Contains secret) "value not in the database"
              let c = (Db.read t.Db (fun s -> Store.allConnections s)).[0].Connection
              Expect.isTrue c.Credentials[0].Present "present"
              Expect.equal c.DeliveryUrl "http://localhost/receiver/crm/clio-grow" "points at the fake receiver"
          }

          test "a key shared by several firms matches none of them, and is flagged" {
              use t = new TempDb()
              Import.apply t.Db origin (batch Domain.Source.legacy [ firm "1" "Real Firm" "shared" [] ]) |> ignore
              let web id = conn $"tenant-{id}-web-lead" "answering-legal" [ ("api_key", "shared") ]
              let run = Import.apply t.Db origin (batch Domain.Source.chatbot [ firm "4" "Tenant A" "shared" [ web 4 ]; firm "5" "Tenant B" "shared" [ web 5 ] ])
              let total, _ = Db.read t.Db (fun s -> Store.listFirms s { Q = null; Kind = null; Source = null; IssuesOnly = false; Limit = 50; Offset = 0 })
              Expect.equal total 3 "three separate firms, nothing merged"
              Expect.exists run.Notes (fun n -> n.Contains "share") "noted"
              let cs = Db.read t.Db Store.allConnections |> Array.map _.Connection
              Expect.isTrue (cs |> Array.forall (fun c -> c.Issues |> Array.exists (fun i -> i.Code = "shared-key"))) "flagged"
          }

          test "orphaned CRM rows are kept and flagged" {
              use t = new TempDb()
              Import.apply t.Db origin { batch Domain.Source.legacy [] with Orphans = [| conn "77" "clio-grow" [ ("api_key", "k") ] |] } |> ignore
              let c = (Db.read t.Db Store.allConnections).[0].Connection
              Expect.exists c.Issues (fun i -> i.Code = "orphaned") "flagged"
          }

          test "OAuth row without refresh token is flagged; duplicates are flagged" {
              use t = new TempDb()
              Import.apply
                  t.Db
                  origin
                  (batch
                      Domain.Source.legacy
                      [ firm "1" "A" "k1" [ conn "1" "mycase" [ "token", "t" ]; conn "2" "mycase" [ "token", "t"; "refresh_token", "r" ] ] ])
              |> ignore
              let cs = Db.read t.Db Store.allConnections |> Array.map _.Connection
              Expect.exists cs[0].Issues (fun i -> i.Code = "oauth-no-refresh") "no refresh token"
              Expect.isTrue (cs |> Array.forall (fun c -> c.Issues |> Array.exists (fun i -> i.Code = "duplicate"))) "both flagged duplicate"
          } ]
