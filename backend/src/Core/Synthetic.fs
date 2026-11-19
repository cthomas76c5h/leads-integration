/// Synthetic leads: real field names and the firm's real intake labels and
/// contact types, fake people. No caller PII enters the MVP.
module LeadIntegrations.Synthetic

open LeadIntegrations.Domain

type private Caller =
    { First: string
      Last: string
      Phone: string
      Matter: string }

let private callers =
    [| { First = "Jane"; Last = "Doe"; Phone = "+15555550142"; Matter = "Personal Injury" }
       { First = "Marcus"; Last = "Reed"; Phone = "+15555550143"; Matter = "Workers Comp" }
       { First = "Ana"; Last = "Silva"; Phone = "+15555550144"; Matter = "Family Law" }
       { First = "Tom"; Last = "Okafor"; Phone = "+15555550145"; Matter = "Criminal Defense" } |]

/// A plausible answer for an intake label, by keyword. Never real data.
let answerFor (label: string) =
    let l = if isNull label then "" else label.ToLowerInvariant()
    if l.Contains "date" || l.Contains "when" then "2026-11-12"
    elif l.Contains "injur" then "Neck and back pain"
    elif l.Contains "county" || l.Contains "where" || l.Contains "location" then "Nassau County"
    elif l.Contains "employer" then "Acme Freight"
    elif l.Contains "insur" then "Yes, State Farm"
    elif l.Contains "child" then "2"
    elif l.Contains "email" then "caller@example.com"
    elif l.Contains "phone" || l.Contains "number" then "+15555550100"
    elif l.Contains "name" then "Sample Name"
    elif l.Contains "?" then "Yes"
    else "Sample answer"

/// `variant`: "full" or "sparse" (a hang-up after the greeting: most fields empty).
let lead (firmName: string) (variant: string) (n: int) (callType: string) (webchat: bool) (customLabels: string[]) : Lead =
    if variant = "sparse" then
        { FirstName = ""
          LastName = ""
          Phone = "+15555550199"
          Email = ""
          CallType = (if isNull callType then "" else callType)
          Message = "Caller hung up before giving details."
          Source = "Answering AI"
          Webchat = webchat
          Custom = [||] }
    else
        let c = callers[abs n % callers.Length]
        let callType = if System.String.IsNullOrWhiteSpace callType then c.Matter else callType
        { FirstName = c.First
          LastName = c.Last
          Phone = c.Phone
          Email = $"{c.First.ToLowerInvariant()}.{c.Last.ToLowerInvariant()}@example.com"
          CallType = callType
          Message = $"{c.First} {c.Last} called {firmName} about a {callType.ToLowerInvariant()} matter and asked for a callback."
          Source = (if webchat then "Answering AI chat" else "Answering AI")
          Webchat = webchat
          Custom = customLabels |> Array.truncate 12 |> Array.map (fun l -> { Label = l; Data = answerFor l }) }
