# Lead delivery: operations

What operators do to keep leads reaching firms. The model behind it is in
[README.md](README.md); words in **bold** are defined in
[GLOSSARY.md](../../GLOSSARY.md).

The principle (RFC-001): **the system handles normal deliveries by itself;
operators handle exceptions.** Every workflow follows
**Spot → Diagnose → Fix → Resend → Verify**, and every action is recorded.

Screens named below are the lab's (`http://localhost:5080`). Where production
(api-2) differs, see [production-today.md](production-today.md). Where each
screen and action lives in the code: [code-map.md](code-map.md).

## Who does what

| Role | May | May not |
|---|---|---|
| **Operator** | Read every lead, delivery and attempt; send **test leads**; **resend** a delivery that did not get accepted; pause and resume a destination; reactivate a **switched off** destination after a fix; prepare a revision | Resend an **accepted** delivery; change a **connection**; make a revision live without approval |
| **Senior operator** | Everything an operator may, plus: resend an accepted delivery (risk of a duplicate in the CRM); override approval; reroute a firm's leads | |
| **Firm contact** | Approve revisions; reconnect a destination (sign in again, issue a new key or hook); confirm a lead is in the CRM | |
| **Engineer** | Investigate **our fault** failures and receivers that change behaviour | |

## Screens

The console's design came from the stakeholder walkthrough
(`../airi/webhook-lab/docs/meeting-screenshots/`) and what a simulated week
showed ([lessons §4](lessons.md#4-problems-only-show-up-across-many-leads)).

### Principles

1. **Operate by exception.** The first screen counts what needs a person and hides what does not. Every count is a door: clicking "Needs attention 2" shows those two.
2. **Say what happened in the receiver's words, then what to do.** A problem is headed by its cause ("Phone is required"), not a code, followed by one plain next action.
3. **Fix where you found it.** The actions that fix a problem sit beside it: Resend, Edit mapping, Pause. No engineer, no other screen.
4. **Close the loop visibly.** After a resend the new delivery shows Accepted, the old one "Resolved by resend", the counts drop, and the message says the lead identity was kept.
5. **Show the journey, and be honest about its end.** Lead published → Sent → **Accepted** → In the CRM: *not visible to us*. Never claim more than the receiver acknowledged.
6. **Every row has a state and a next action.** Queues read as to-do lists; "waiting on you" counts only what the operator must do.
7. **One lead, many destinations, drawn.** For one firm, the graph shows the lead fanning out, each line coloured by its outcome with the answer on it.
8. **Tests look different.** A test lead carries a chip everywhere and never counts in the totals.
9. **Lateness first.** Expired leads come before everything else, because a lead's value expires.

### Where to look

| Question | Screen | Shows | Actions | Lab |
|---|---|---|---|---|
| Is anything broken right now? | **Delivery Center** | Tiles (expired leads, needs attention, retry queue with late ones, destinations stopped, accepted today), each a door to its rows; the delivery log; the problem drawer | Resend (bulk too), Edit mapping, Pause, Reactivate | Built; server totals (`/api/deliveries/summary`) |
| What did we send and what did they answer? | **Problem drawer** (a delivery in the Delivery Center) | Lead facts, journey, the receiver's own words, failure class, next action, each attempt's request and answer | The one or two actions for its class ([below](#diagnose-a-delivery)) | Built, except Send reconnect link |
| Which destinations are stopped, failing, late or have missed leads? | **Destinations** | Every destination, most urgent first: state, health, last accepted, this week, failure streak, missed leads, next action | Reactivate, Resume, Send a test lead, Send missed leads | Built; missed leads stand in for held deliveries, and their age against the window to act is not shown |
| What happened to this caller's lead? | **Leads** | One row per lead with its **lead outcome** and hand-off ("reached the firm in 20 min, by its fallback"); open it to see every destination | Open a delivery | Built |
| How is this firm set up? | **Delivery graph** (the firm picker ranks firms with live destinations first) or the **Firm** page | The fan-out for that firm, live, with each answer | Send a test lead, open a destination | Built |
| What did the firm's week look like? | **Firm report** | Calls by category and practice area, hand-off in time, proof of delivery per destination | | Built in the lab; not yet in the firm portal |
| What do the firm's call types mean? | **Call categories** | The taxonomy, reference leads, the firm's call types by category | Send a reference lead | Built |
| What is being set up? | **Onboarding** | Setups by state with their next action | Per RFC-001 | Built |
| What needs me now? | **Alerts** | One alert per destination per cause, with its next action ([below](#alerts)) | Open, acknowledge | Not built: needs held deliveries and per-destination state |

## Daily check

At the start of each shift, and whenever an alert arrives, in this order. Speed
matters: a lead's value expires within its **window to act** (minutes for an
emergency or someone in custody, about an hour for a new matter), so the first
question is always "did someone at the firm get it in time?".

0. **Leads near or past their window to act with no hand-off** *(Proposed)*.
   Make sure the firm has them now, by its **fallback** or by phone, and say
   how late they are. Everything else can wait for this.
1. **Destinations that stopped.** Switched off, needs reconnection, paused for
   longer than planned. Each one means a firm is getting nothing at that place.
   Go to [Destination stopped](#destination-stopped).
2. **Deliveries that need attention**, newest first. Refused and gave up.
   Go to [Diagnose a delivery](#diagnose-a-delivery).
3. **The retry queue.** Retrying is normal; a delivery retrying for more than
   15 minutes is late (the lab's threshold for every lead; *Proposed:* judged
   against each lead's window to act). Many retries to one destination kind at once is an
   [outage](#outage-at-a-destination-kind). A retrying new matter or emergency
   does not wait for its retries: its hand-off goes to the fallback.
4. **Leads with no destination** (Leads → outcome "No destination"). A live
   firm with no live destination is probably a setup gap, not a delivery problem.

Done when every item above is resolved, waiting on someone named, or knowingly
left (with a note).

## Diagnose a delivery

Open the delivery. Read the journey (published → sent → accepted), the last
attempt's status and the receiver's own words. Classify it:

The drawer does the classifying: its headline, advice and buttons follow the
**failure class** (`Diagnose.fs`). When the destination is stopped, the drawer
leads with Reactivate instead ("Reactivate once the firm has reconnected").

| You see | Class | Drawer headline (example) | Next action | Drawer's buttons |
|---|---|---|---|---|
| Retrying: no answer, 429, 5xx | **Transient failure** | "No answer since 2:10 pm (4 tries)" | Nothing. It retries for about 21 hours. Act only if it is late or many deliveries are affected. | none while it retries |
| Gave up after 8 attempts | Transient that did not pass | "No answer for 21 hours" | Check the destination is up (test lead). Then resend. | Send a test lead, Resend |
| Refused, receiver names a missing field the lead does not have ("missing first_name" on a hang-up) | **Lead problem** | "No caller name: Clio Grow requires one" | Nothing to fix at the destination. Do not resend; it will be refused again. Confirm the firm got the lead by its notification. | Open the lead, Resend anyway |
| Refused, receiver names a field the lead *has*, or refuses every lead | **Configuration problem** | The receiver's words ("Phone is required") | **Edit mapping** when the mapping is ours: it makes a draft revision; send it for the firm's approval, test, activate (RFC-001 sections 5–6). Otherwise **See what we send** shows the body and what its receiver would refuse; that is set in the destination's source system. Then resend the refused leads. | Edit mapping or See what we send, Resend, Send a test lead, Pause |
| Refused with 3xx (redirect) | Configuration problem | The receiver's words | The address moved. Confirm the new address with the firm contact; update; test; resend. | As above |
| 401 or 403 | **Connection problem** | "Sign-in expired" | The firm must sign in again or issue a new key. See [Destination stopped](#destination-stopped). | Send a test lead, Resend (Send reconnect link *not built*) |
| 410 Gone | Connection problem | "Hook removed" | The hook was removed (a deleted Zap, a revoked URL). The destination stopped itself. Ask the firm contact whether to reconnect or retire it. | Send a test lead, Resend |
| "body: …" with no attempt, or a failure before sending | **Our fault** | "We couldn't build the request" | Escalate to an engineer with the delivery link. Do not resend until fixed. | Copy a link for an engineer |
| Not sent: routing | Working as configured | "Court calls don't go to this CRM" | Nothing, unless the firm says it wants this kind of call. Then it is a [routing change](#change-what-a-destination-receives). | See the firm's call types |

## Resend

Resend sends the lead again as a new delivery under the **same lead identity**,
so a destination that dedupes recognises it. (The code still calls it
`replay`; the screens say Resend.)

- **Resend only after the cause is fixed.** A resend into the same problem adds
  another refusal and, for production deliveries, another step toward
  switching the destination off.
- **Never resend while it is still retrying.** The schedule is already doing it.
- **Resending an accepted delivery** can create a duplicate record in the CRM.
  Senior operator only, and only when the firm contact confirms the record is
  missing.
- **After an outage or reconnection,** resend in bulk: select the refused and
  gave-up deliveries for that destination in the Delivery Center.
- A successful resend clears the original from "needs attention": the newest
  delivery for that lead and destination is the one that counts.

Verify: the new delivery is accepted. For an important lead, ask the firm
contact to confirm it is in the CRM (**verified in CRM**).

## Destination stopped

| State | Why | Who acts | Then |
|---|---|---|---|
| **Needs reconnection** | 401/403 (sign-in expired, key revoked) or 410 (hook removed) | Firm contact reconnects; operator sends them the secure link | Test lead, then the destination is live again |
| **Switched off** | 5 production deliveries in a row refused or gave up | Operator finds the cause (usually a configuration problem) and fixes it | Reactivate, test, resend what failed |
| **Paused** | An operator paused it (firm request, migration, investigation) | The operator who paused it | Resume |

Steps:

1. Open the destination (Delivery Center → switched-off list, or the Delivery
   graph). Read the reason and its last few deliveries.
2. Fix the cause, or ask the firm contact to reconnect.
3. **Send a test lead.** Do not reactivate on hope.
4. Reactivate or resume.
5. **First, the hand-off.** As soon as you find a stopped destination, check the
   leads that arrived while it was stopped reached someone at the firm in time:
   - *Proposed:* each one's hand-off went to the firm's **fallback** when the destination stopped. Confirm it was accepted there.
   - *Lab and production today:* there is no fallback. For any lead still inside its window to act (new matters, emergencies), tell the firm now by another route. Fixing the destination comes after.
6. **Then, the record.** Deal with the leads that arrived while it was stopped:
   - *Proposed:* they are **held** and sent automatically, oldest first, when it is live again, with their original time and marked late. Check the held count reaches zero.
   - *Lab:* they are **missed leads**: Destinations shows how many, and **Send missed leads** sends them, oldest first, with routing applied. Leads past their window arrive in the CRM looking new: agree with the firm contact before sending old ones, so nobody calls a week-old lead as if it just came in.
   - *Production today:* they have no record at all. Find them by firm and period and resend by hand. This is the gap held leads close.
7. Verify the next production lead is accepted.

## Outage at a destination kind

Many firms' deliveries to one kind (say every Clio Grow destination) start
retrying at once. That is the destination's outage, not each firm's problem.

1. Confirm: retries share a kind and started together; test leads to two firms fail the same way.
2. **Do not resend.** Retries are already scheduled and honour `Retry-After`.
3. Watch the retry queue drain once the destination recovers.
4. After recovery, filter that kind for **gave up** (outages longer than about 21 hours) and resend them in bulk.
5. If the outage is long, tell affected firms that leads are safe and will arrive late, and that their notifications still have them.

Note: an outage does not count toward switching a destination off until a delivery gives up.

## Firm says a lead is not in their CRM

1. Find the lead: Leads → search the caller's name or phone, filter by firm.
2. Read each destination's delivery:
   - **Accepted:** the CRM's receiver took it. The cause is on their side: a
     Zap step, a CRM duplicate rule, the wrong pipeline or inbox. Show the firm
     contact the request we sent and the answer we got. Resend only as a senior
     operator if they confirm it is missing.
   - **Not sent: routing:** the firm's routing rule excludes that kind of call.
     Agree with them whether to change it.
   - **Refused, gave up, retrying:** diagnose as above.
   - **No delivery to that destination:** it was paused, stopped or not live
     when the lead arrived. Send it by hand once it is live.
3. Reply with what happened and what changed.

## Change what a destination receives

A firm asks for a different set of calls ("stop sending court calls to Clio",
"send chat leads too").

1. Find the **call categories** involved (Call categories page; the Firm page
   groups the firm's call types by category).
2. Prepare a revision of the destination's **routing rule** (RFC-001 section 5:
   the firm contact approves the change summary).
3. Test with a **reference lead** of the right category (Call categories → reference leads → send to the firm).
4. Make the revision live. Changes apply to leads recorded after that moment, never to leads already decided.

## Set up a new destination

Specified in RFC-001 section 4. From delivery's point of view:

- Until it is **live** it receives test leads only, and no production totals
  include them.
- Before go-live, every issue the import or setup found must be cleared:
  missing key or token, missing staff ID, a key shared with another firm, a
  missing email recipient. These refuse every lead once live.
- Go-live needs an approved revision, an accepted test lead, and the firm
  contact's confirmation that the test record is in the CRM.

## Alerts

*Proposed.* An alert names the firm, the destination and the next action. One
alert per destination per cause, not per lead.

| Alert | When | To |
|---|---|---|
| Destination needs reconnection | First 401/403/410 | Operator; firm contact gets the reconnect link |
| Destination switched off | Failure streak reaches 5 | Operator |
| Lead near its window to act | A lead with no accepted hand-off when a third of its window has passed: its hand-off goes to the fallback | Operator (the firm sees the fallback delivery) |
| **Expired lead** | A lead's window to act passed with no accepted hand-off | Operator, at once; the operator tells the firm |
| Outage | Retries to one destination kind across several firms within 10 minutes | Operator, engineer |
| Held past its window | A held delivery whose lead's window has passed: only the record is left to deliver | Operator, to agree with the firm how to mark it |
| Quiet firm | A live firm with no accepted delivery in business hours for longer than usual | Operator |

Production today has one alert instead: 5 failures in a row among today's last
10 CRM deliveries across all firms, or no CRM delivery at all today, checked
every 10 minutes on weekdays 8 am–4 pm Eastern.

## Practising in the lab

`dotnet run --project src/Import -- simulate` (in `backend/`) fills the lab with
a week where each workflow above has a real case: an outage that recovers, a
dead endpoint switched off, a 410, an expired sign-in, a redirect fixed and
resent, a paused destination, routing decisions, tests, and deliveries still
retrying, and two outages that test the window to act: one at a firm with a
fallback, one at a firm without. See the [README](../../README.md#run).
