/// Milestone 5, delivery's side: the call center's usage facts (usage/1, C6), as it publishes them
/// (its example, docs/contracts/usage/). Facts only: rating reads their measured length, and alone
/// decides what is billable (suite S-3).
module LeadIntegrations.Tests.UsageTests

open System
open System.IO
open System.Text.Json.Nodes
open Expecto
open LeadIntegrations
open LeadIntegrations.Domain
open LeadIntegrations.Tests.Support

let private example () =
    File.ReadAllText(Path.Combine(Db.repoRoot, "docs", "contracts", "usage", "usage-1.example.json"))

/// The example with some fields set (a value of null removes the field).
let private withFields (fields: (string * JsonNode) list) =
    let root = JsonNode.Parse(example ()).AsObject()
    for name, value in fields do
        if isNull value then root.Remove name |> ignore else root[name] <- value
    root.ToJsonString()

let private str (v: string) : JsonNode = JsonValue.Create v
let private num (v: int64) : JsonNode = JsonValue.Create v

let private failsWith (status: int) (f: unit -> 'a) =
    try
        f () |> ignore
        failtest $"expected a {status}"
    with Fail(s, message) ->
        Expect.equal s status message
        message

/// A firm the example's "firm-a" resolves to, with one email destination.
let private firm (t: TempDb) =
    let email = { conn "10" "email" [] with Settings = Collections.Generic.Dictionary(dict [ "recipients", "intake@example.com" ]) }
    Import.apply t.Db origin (batch Source.legacy [ Support.firm "1" "Usage Firm" "k" [ email ] ]) |> ignore
    let firmId = Db.read t.Db (fun s -> (Store.allFirms s |> Array.head).Id)
    Db.write t.Db (fun s -> Store.setFirmAlias s "firm-a" firmId (now ()) |> ignore)
    firmId

let private usageCount (db: Db.Database) =
    Db.read db (fun s -> s.Query "SELECT COUNT(*) FROM usage_facts" [] (fun r -> r.GetInt64 0) |> Array.head)

[<Tests>]
let tests =
    testList "usage" [
        test "the center's usage facts are kept as they came, once per interaction" {
            use t = new TempDb()
            let firmId = firm t
            let r = Billing.ingestUsage t.Db (example ())
            Expect.isFalse r.Duplicate "new"
            Expect.equal (r.Usage.FirmId, r.Usage.Interaction, r.Usage.Result) (firmId, "call-1", "PutThrough") "read"
            Expect.equal (r.Usage.DurationMs, r.Usage.Revision) (60000L, Nullable 3L) "measured, under its revision"
            let stored = Db.read t.Db (fun s -> s.Query "SELECT payload FROM usage_facts" [] (fun r -> r.GetString 0)) |> Array.head
            Expect.equal stored (example ()) "byte for byte"
            // The same record, written differently: a repeat. A different one for that interaction: a conflict.
            let again = Billing.ingestUsage t.Db (JsonNode.Parse(example ()).ToJsonString(Text.Json.JsonSerializerOptions(WriteIndented = true)))
            Expect.isTrue again.Duplicate "a repeat"
            Expect.stringContains (failsWith 409 (fun () -> Billing.ingestUsage t.Db (withFields [ "duration_ms", num 61000L ]))) "never change" "a conflict"
            Expect.equal (usageCount t.Db) 1L "one record"
        }

        test "only an ended interaction is taken, from a known firm, with the fields rating reads" {
            use t = new TempDb()
            firm t |> ignore
            failsWith 422 (fun () -> Billing.ingestUsage t.Db (withFields [ "result", str "Open"; "ended_at", null ])) |> ignore
            failsWith 422 (fun () -> Billing.ingestUsage t.Db (withFields [ "ended_at", null ])) |> ignore
            failsWith 422 (fun () -> Billing.ingestUsage t.Db (withFields [ "contract", str "usage/2" ])) |> ignore
            failsWith 422 (fun () -> Billing.ingestUsage t.Db (withFields [ "duration_ms", str "a minute" ])) |> ignore
            failsWith 422 (fun () -> Billing.ingestUsage t.Db (withFields [ "interaction", null ])) |> ignore
            failsWith 422 (fun () -> Billing.ingestUsage t.Db (withFields [ "result", str "Unheard-of" ])) |> ignore
            Expect.stringContains (failsWith 422 (fun () -> Billing.ingestUsage t.Db (withFields [ "firm", str "nobody" ]))) "not a known firm" "unknown firm"
            Expect.equal (usageCount t.Db) 0L "nothing kept"
        }

        test "fields delivery doesn't read are accepted and kept: the center adds to its contract" {
            use t = new TempDb()
            firm t |> ignore
            let r = Billing.ingestUsage t.Db (withFields [ "handled_in_language", str "es"; "interaction", str "call-2" ])
            Expect.equal (r.Usage.Payload.GetProperty("handled_in_language").GetString()) "es" "kept as sent"
        }

        test "rating takes a lead's length from its interaction's usage facts, not the estimate" {
            use t = new TempDb()
            let firmId = firm t
            // A caller who gave no name: estimated as a hang-up under 20 seconds, so free.
            let nameless = { sampleLead () with FirstName = ""; LastName = "" }
            let measured = Dispatch.intake t.Db firmId "call-measured" nameless
            let estimated = Dispatch.intake t.Db firmId "call-estimated" nameless
            // Another caller's number: the same number within ten minutes would be a free repeat.
            let long = Dispatch.intake t.Db firmId "call-long" { sampleLead () with Phone = "+15555550198" }
            let before = Db.read t.Db (fun s -> Billing.conversationOf s measured.Event)
            Expect.isFalse before.Billable "estimated: a free hang-up"
            Billing.ingestUsage t.Db (withFields [ "interaction", str "call-measured"; "duration_ms", num 45_000L ]) |> ignore
            Billing.ingestUsage t.Db (withFields [ "interaction", str "call-long"; "duration_ms", num (25L * 60_000L) ]) |> ignore
            let after = Db.read t.Db (fun s -> Billing.conversationOf s measured.Event)
            Expect.equal after.Seconds 45 "measured"
            Expect.isTrue after.Billable "45 seconds is a conversation, not a hang-up"
            let still = Db.read t.Db (fun s -> Billing.conversationOf s estimated.Event)
            Expect.isFalse still.Billable "no usage: still estimated"
            let longCall = Db.read t.Db (fun s -> Billing.conversationOf s long.Event)
            Expect.equal (longCall.Seconds, longCall.Units) (1500, 2) "25 minutes counts as two (fair use)"
        }
    ]
