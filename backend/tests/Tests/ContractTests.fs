/// Each destination kind's delivery contract (docs/contracts/receivers/{kind}.json) against the code
/// that carries it out: a contract that says one thing while the code does another fails here.
module LeadIntegrations.Tests.ContractTests

open System.IO
open System.Text.Json
open Expecto
open LeadIntegrations

type private Contract =
    { Kind: string
      Duplicates: string
      AfterUnknown: string
      Strongest: string
      HandOff: string
      RemoteId: string
      Answers: string }

let private contracts =
    lazy
        (Directory.GetFiles(Path.Combine(Db.repoRoot, "docs", "contracts", "receivers"), "*.json")
         |> Array.filter (fun f -> Path.GetFileName f <> "modes.json")
         |> Array.map (fun f ->
             use doc = JsonDocument.Parse(File.ReadAllText f)
             let r = doc.RootElement
             let str (name: string) = r.GetProperty(name).GetString()
             { Kind = str "kind"
               Duplicates = str "duplicates"
               AfterUnknown = str "afterUnknown"
               Strongest = r.GetProperty("evidence").GetProperty("strongest").GetString()
               HandOff = str "handOff"
               RemoteId = str "remoteId"
               Answers = str "answers" }))

/// What the code does after an unknown outcome, for a destination with this duplicate handling.
let private afterUnknownInCode (duplicates: string) =
    if Dispatch.retryAfterUnknown duplicates then "resend"
    elif duplicates = "lookup" then "lookup" // Dispatch.runVerifications looks it up and settles it
    else "operator"

[<Tests>]
let tests =
    testList "delivery contracts" [
        test "every catalog kind has a delivery contract, and every contract a catalog kind" {
            let inContracts = contracts.Value |> Array.map _.Kind |> set
            let inCatalog = Catalog.kinds |> Array.map _.Kind |> set
            Expect.equal inContracts inCatalog "the same kinds"
        }

        test "what follows an unknown outcome is what the code does" {
            for c in contracts.Value do
                Expect.equal c.AfterUnknown (afterUnknownInCode c.Duplicates) $"{c.Kind}: afterUnknown for duplicates {c.Duplicates}"
        }

        test "the strongest evidence is what the code can record" {
            for c in contracts.Value do
                let expected =
                    if Payloads.acknowledgeable.Contains c.Kind then "acknowledged" // the confirm link in the message
                    elif c.Duplicates = "lookup" then "verified" // a lookup by our lead reference
                    else "accepted"
                Expect.equal c.Strongest expected $"{c.Kind}: evidence.strongest"
        }

        test "the kinds that reach a person are the ones the hand-off prefers" {
            for c in contracts.Value do
                Expect.equal (c.HandOff = "person") (Catalog.reachesPerson.Contains c.Kind) $"{c.Kind}: handOff"
        }

        test "every kind's answers are read by the one shared table, until one needs its own" {
            for c in contracts.Value do
                Expect.equal c.Answers "standard" $"{c.Kind}: answers (Policy.classify has no per-kind reading)"
        }

        test "a record id is claimed only where something keeps it" {
            // Nothing in the code reads a destination's record id yet, so no contract may claim one is returned.
            for c in contracts.Value do
                Expect.notEqual c.RemoteId "returned" $"{c.Kind}: remoteId returned, but no code keeps it"
        }
    ]
