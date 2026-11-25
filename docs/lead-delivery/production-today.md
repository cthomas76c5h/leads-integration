# Lead delivery: production today

How the legacy system delivers leads to CRMs now, measured against the model in
[README.md](README.md). Read from the legacy delivery code and a local, read-only
copy of its database.

**This is evidence, not a plan.** The legacy system is not being extended: the
service is built on the call center as its source of truth, and delivery integrates
with it ([where this sits](../../README.md#where-this-sits); decisions in
[answers](answers.md#routing-and-integrations)). This page records what today's
system does, so the gaps the new build closes are written down and the production
feed can check them.

## How a lead flows today

1. Intake posts the lead to the legacy system. A lead whose intake answer ID already
   exists is answered `208 Already Reported` and not processed again.
2. The lead is saved, and CRM posting runs **inside the same request**. A hook on
   saving a new lead also starts CRM posting. Read together, the code posts twice;
   the local data shows no systematic duplicates (all 7 duplicate pairs are manual
   resends), so intake probably takes another path. **Confirm which path production
   intake uses.**
3. A contact-type check: if the firm has no active contact types, or the lead's type
   is not one of them after trimming `" ,.;:-*"` from the ends and lowercasing, posting
   stops. **No CRM receives the lead and nothing is recorded.**
4. For each enabled CRM of the firm, in turn:
   - A chat lead to a CRM that does not take chats is skipped with a log line only.
   - A delivery row is created, the CRM's handler posts once, and the HTTP status is
     stored. 200, 201 and 202 count as success.
   - A handler that raises, or returns nothing, is stored as **500**.
   - One CRM kind (Lawruler) has no handler; its rows only log "no crm found".
5. There is no automatic retry.

Recovery:

- **Resend:** support staff, once per delivery row, only if the CRM is enabled.
  Resending a success needs a super-administrator. It posts to that CRM again and
  creates a new row.
- An administrative command posts given leads to all their CRMs again, with no
  once-only check.
- Token refresh runs as separate scheduled commands.

Alerting (a separate scheduled job): every 10 minutes, weekdays 8 am–4 pm Eastern,
it reads today's 10 newest CRM deliveries across **all firms** and alerts on 5
failures in a row, or when there are none today.

## What the local copy shows

476 CRM delivery rows, Sep 2024 to Jun 2025. Most of this copy is test traffic
(7 of the 8 enabled CRMs are on an internal test firm), so treat the rates as
"failures can go unnoticed", not as production numbers:

| Status | Rows |
|---|---|
| 500 (any exception, or no status) | 130 |
| 401 | 73 |
| 200 / 201 / 202 | 273 |

By kind: Clio 0 of 71 accepted, MyCase 2 of 67, Smokeball 79 of 144; Lawmatics,
Lead Docket and PracticePanther nearly all accepted. Measure production before
deciding priorities.

## Gap to the model

| Model rule ([README.md](README.md)) | Production today | What closing it in the legacy system would have taken (not planned) |
|---|---|---|
| Every destination gets a decision | Routing and chat decisions leave no record | Write a delivery row with a reason for every decision, including not sent |
| Failures say what happened | Exceptions and missing answers are all "500" | Add `error` and `response_snippet`; leave the status empty when nothing answered |
| Lead problems are not sent, not refused | A hang-up goes to the CRM and is refused | Check each kind's required fields before posting; record "not sent: lead problem" |
| A lead is recorded before it is sent; destinations are independent | Posted inline, one CRM after another, inside the intake request | Post from a worker: a next-attempt time on each delivery row, run by the existing scheduler |
| Transient failures retry | One attempt | The same worker retries no answer, 429 and 5xx on the schedule; never 4xx |
| Destination health is per destination | One global alert over today's last 10 | A state per CRM (live, paused, needs reconnection, switched off) and an alert per CRM |
| Connection problems stop at once | Each lead after an expired sign-in fails with 401 | First 401/403/410 → needs reconnection, alert, hold |
| Leads are held, not lost; the hand-off never waits | Disabled CRMs get nothing; no record; no fallback | A fallback (the firm's agreed email or SMS) for the hand-off at once, and held deliveries for the record, sent when the CRM is live again |
| Resend is safe and audited | Once per row; success resend by SuperAdmin | Allow repeat resends, record who and why, keep the lead identity |
| Routing is explicit | Active contact-type text match for all CRMs together | Store the call category on each contact type; a routing rule per CRM |
| Setup problems block go-live | A CRM can be enabled with missing keys or IDs | Run the lab's setup checks when a CRM is saved |
