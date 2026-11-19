/// "Your week in calls": one firm's last 7 days told in call categories, with
/// proof of delivery (docs/call-categories/why-it-matters.md, step 1). Built only
/// from data already held; production leads only.
module LeadIntegrations.Report

open System
open LeadIntegrations.Db

type Count = { Id: string; Count: int }

/// The firm's own wording for a kind of call, and what it means.
type Label =
    { Label: string
      Category: string
      PracticeArea: string
      Count: int }

type HandOffSummary =
    { /// Leads that reached someone at the firm within their window to act.
      OnTime: int
      Expired: int
      Waiting: int
      /// Of the on-time ones, taken by the firm's fallback.
      ViaFallback: int
      /// Median time from the call to the first accepted delivery.
      MedianMs: Nullable<int64>
      /// Emergencies and custody calls: on time, of all.
      UrgentOnTime: int
      UrgentTotal: int }

type DestinationLine =
    { ConnectionId: string
      Name: string
      Kind: string
      Fallback: bool
      Health: string
      State: string
      Accepted: int
      Refused: int
      NotSent: int
      MissedLeads: int }

type FirmReport =
    { FirmId: string
      FirmName: string
      From: int64
      To: int64
      Calls: int
      NewMatters: int
      Urgent: int
      AfterHours: int
      Chats: int
      HangUps: int
      ByCategory: Count[]
      ByPracticeArea: Count[]
      HandOff: HandOffSummary
      Destinations: DestinationLine[]
      Labels: Label[] }

let private hour = 3_600_000L
/// Firms' hours are US Eastern (standard time); office hours 8 am to 6 pm on weekdays.
let private eastern = -5L * hour

let afterHours (t: int64) =
    let local = DateTimeOffset.FromUnixTimeMilliseconds(t + eastern)
    let weekend = local.DayOfWeek = DayOfWeek.Saturday || local.DayOfWeek = DayOfWeek.Sunday
    weekend || local.Hour < 8 || local.Hour >= 18

let private counts (keys: string seq) =
    keys |> Seq.countBy id |> Seq.sortByDescending snd |> Seq.map (fun (k, n) -> { Id = k; Count = n }) |> Array.ofSeq

let build (s: Session) (firmId: string) (nowMs: int64) : FirmReport =
    let firm = Store.firmOrFail s firmId
    let from = nowMs - 7L * 24L * hour
    let _, all =
        Store.listLeads s { Q = null; FirmId = firmId; Category = null; Outcome = null; Test = Nullable false; Limit = 500; Offset = 0 }
    let leads = all |> Array.filter (fun l -> l.CreatedAt >= from)
    let isUrgent (l: Store.LeadSummary) = l.Mapping.Category = "emergency" || l.Mapping.Category = "jail-custody"
    let handed = leads |> Array.filter (fun l -> not (isNull (box l.HandOff)) && l.HandOff.At.HasValue)
    let median =
        let ms = handed |> Array.map (fun l -> l.HandOff.At.Value - l.CreatedAt) |> Array.sort
        if ms.Length = 0 then Nullable() else Nullable ms[ms.Length / 2]
    let state (l: Store.LeadSummary) = if isNull (box l.HandOff) then "" else l.HandOff.State
    let urgent = leads |> Array.filter isUrgent
    { FirmId = firm.Id
      FirmName = firm.Name
      From = from
      To = nowMs
      Calls = leads.Length
      NewMatters = leads |> Array.filter (fun l -> l.Mapping.Category = "new-matter") |> Array.length
      Urgent = urgent.Length
      AfterHours = leads |> Array.filter (fun l -> afterHours l.CreatedAt) |> Array.length
      Chats = leads |> Array.filter _.Webchat |> Array.length
      HangUps = leads |> Array.filter (fun l -> l.Caller = "") |> Array.length
      ByCategory = counts (leads |> Seq.map (fun l -> l.Mapping.Category))
      ByPracticeArea =
        counts (leads |> Seq.filter (fun l -> l.Mapping.Category = "new-matter") |> Seq.map (fun l -> if isNull l.Mapping.PracticeArea then "unspecified" else l.Mapping.PracticeArea))
      HandOff =
        { OnTime = leads |> Array.filter (fun l -> state l = "on-time") |> Array.length
          Expired = leads |> Array.filter (fun l -> state l = "expired") |> Array.length
          Waiting = leads |> Array.filter (fun l -> state l = "waiting") |> Array.length
          ViaFallback = leads |> Array.filter (fun l -> state l = "on-time" && l.HandOff.ViaFallback) |> Array.length
          MedianMs = median
          UrgentOnTime = urgent |> Array.filter (fun l -> state l = "on-time") |> Array.length
          UrgentTotal = urgent.Length }
      Destinations =
        Destinations.all s nowMs
        |> Array.filter (fun r -> r.FirmId = firmId && r.State <> "off")
        |> Array.map (fun r ->
            let c = (Store.connectionOrFail s r.ConnectionId).Connection
            { ConnectionId = r.ConnectionId
              Name = r.Name
              Kind = r.Kind
              Fallback = Dispatch.isFallback c
              Health = r.Health
              State = r.State
              Accepted = r.Week.Accepted
              Refused = r.Week.Refused
              NotSent = r.Week.NotSent
              MissedLeads = r.MissedLeads })
      Labels =
        leads
        |> Array.groupBy _.CallType
        |> Array.map (fun (label, ls) ->
            let m = ls[0].Mapping
            { Label = label; Category = m.Category; PracticeArea = m.PracticeArea; Count = ls.Length })
        |> Array.sortByDescending _.Count
        |> Array.truncate 10 }
