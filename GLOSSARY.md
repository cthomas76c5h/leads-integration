# Lead delivery

Getting each lead Answering AI takes to the places a firm wants it, and showing
operators what happened when it did not arrive. The model and workflows are in
[docs/lead-delivery/](docs/lead-delivery/README.md).

## Firms and calls

**Time zone**:
The region a firm keeps its office hours in, as an IANA name (America/Los_Angeles), set by Answering AI; America/New_York by default. It sets the firm's after-hours count and the dates on what the firm reads. Delivery never needs it: windows and retries are elapsed time.
_Avoid_: offset, EST/PST (they ignore daylight saving)

**Firm**:
A law firm (or other business) whose calls Answering AI answers.
_Avoid_: client, account, customer

**Brand**:
The name and look under which a firm meets Answering AI. Presentation only: it never decides behaviour, tenancy or billing.
_Avoid_: app variant, tenant

**Conversation**:
One call or chat Answering AI took, whatever its outcome. A lead is recorded from some conversations; billing reads every one.
_Avoid_: session, room, call (when a chat is included)

**Caller**:
The person who called or chatted with Answering AI on a firm's behalf.
_Avoid_: lead (the lead is the record, not the person), contact

**Lead**:
The record of one call that Answering AI hands over for delivery: who called, the call type, the intake answers and the message. It never changes once recorded.
_Avoid_: event, message, contact, CRM lead

**Chat lead**:
A lead that came from a web chat rather than a phone call.
_Avoid_: webchat (as a noun)

**Hang-up**:
A lead whose caller left before giving a name.

**Test lead**:
A lead sent on purpose to check one destination. It is never counted with production leads.
_Avoid_: dummy lead, sample

**Intake answers**:
The firm-specific questions asked during the call and the caller's answers.
_Avoid_: custom fields

## Call types and categories

**Call type**:
The firm's own label for a kind of call ("*New Client Calls", "Opposing Counsel"), chosen during the call.
_Avoid_: contact type (the api-2 name), intake type

**Active call type**:
A call type the firm has marked as one it wants sent to its CRMs.

**Call category**:
One of a small fixed set of kinds of call (new matter, existing client, court or agency, billing, …) that every call type maps to.
_Avoid_: lead type, disposition

**Practice area**:
The area of law a new matter belongs to (personal injury, family, criminal, …).

**Modifier**:
A qualifier on a call type that is not its category: after-hours, no-patch, in-custody, a named person.

## Destinations

**Destination**:
One place a firm's leads go: a CRM, an automation (a Zap or webhook), or a notification (email, SMS, Slack).
_Avoid_: connection (that is the authorization), integration, installation, CRM (only one kind of destination)

**Destination kind**:
The product a destination delivers to, such as Clio Grow, MyCase or email.
_Avoid_: CRM type, handler

**Connection**:
The firm's authorization that lets a destination reach its external system: an API key, an OAuth grant or a URL. Only its presence and fingerprint are ever kept.
_Avoid_: credentials (the secret itself, which is never stored or shown)

**Routing rule**:
Which leads a destination takes: the call categories, the practice areas for new matters, and whether it takes chat leads. Each destination's rule is evaluated on its own, with no order between destinations.
_Avoid_: filter, contact-type match

**Routing decision**:
Why one lead goes, or does not go, to one destination: **planned**, **not sent** (routing) or **not sent** (lead problem), with the rule, the classification and the taxonomy version it used. Made once, when the lead is recorded, and kept with the delivery. *Lab.*
_Avoid_: filter result, match

**Planned**:
A routing decision that a delivery follows. For a paused destination, a planned delivery is held, not missed. *Lab.*

**Mapping**:
How a lead's fields become the fields the destination expects.
_Avoid_: template, field map

**Revision**:
An unchangeable version of a destination's routing rule and mapping. Exactly one revision is live.

**Setup**:
Bringing a destination from first contact to live: connect, approve, test, go live.
_Avoid_: onboarding ticket, install

**Change request**:
A request to change what a live destination receives. It becomes a draft revision that the firm contact approves before it goes live.
_Avoid_: ticket, support request

**Live**:
The state of a destination that receives production leads.

**Desired state**:
What the firm and operators want a destination to be: setting up, live, paused or removed. Owned by firm configuration.
_Avoid_: status (too broad)

**Observed health**:
What delivery has seen of a destination: healthy, failing, authorization refused, or switched off. Owned by delivery; combined with the desired state to decide whether a delivery may be sent.
_Avoid_: status (too broad)

**Paused**:
A destination an operator has stopped on purpose. Its leads are held for the record, and handed off through its fallback.
_Avoid_: disabled

**Needs reconnection**:
A destination whose connection stopped working (expired sign-in, revoked key, removed hook): the first 401, 403 or 410. Only the firm can fix it. Its leads are held, and their hand-offs go to the fallback at once; it is not counted as the destination failing. *Lab.*
_Avoid_: disabled, broken

**Switched off**:
A destination stopped automatically after too many failures in a row.
_Avoid_: disabled, auto-paused

**Off in source**:
A destination imported switched off from its source system and never used here. Nobody paused it.
_Avoid_: paused, disabled

**Missed lead**:
A lead recorded while a destination was stopped (switched off), which therefore has no delivery to it. Sending it later is an operator's decision. A paused destination misses nothing: its leads are held.
_Avoid_: dropped lead, lost lead

**Failure streak**:
The number of production deliveries to one destination that failed in a row, for reasons that are the destination's.

## Delivery

**Delivery**:
One lead going to one destination, from planning to its final outcome.
_Avoid_: post, send, CRM lead

**Attempt**:
One request to the destination within a delivery, with what it answered. It **starts** when its record is committed, after eligibility and the lease are checked; the request is sent only then. *Lab.*
_Avoid_: try (in writing), post

**Lease**:
A worker's time-limited claim on one delivery, with a version that every claim increases. Only the holder of the current version may start or finish an attempt. An expired lease is recovered by another worker; it is never by itself permission to resend. *Lab.*
_Avoid_: lock, flag

**Unknown outcome**:
An attempt that was sent but never answered: a timeout while waiting, a reset connection, a worker lost mid-send. The destination may or may not have the lead; what follows depends on how that destination kind handles duplicates: sent again where a second copy is harmless, otherwise held for an operator to check. *Lab for a worker that lost its lease; a timeout is still retried as transient.*
_Avoid_: timeout (that is one cause), transient failure (that is for attempts that cannot have arrived)

**Accepted**:
The destination answered that it took the lead. The strongest claim a receiver's answer alone supports; it is not proof that a person has the lead.
_Avoid_: delivered to CRM, success

**Verified in CRM**:
Someone (the firm, or an API that allows it) confirmed the record exists in the CRM.

**Acknowledged**:
A person at the firm, or a workflow the firm authorised, confirmed receipt of a lead: a reply to the text, a click on the signed link, the lead opened in the firm portal.
_Avoid_: read (opening an email is not counted), seen

**Evidence level**:
The strongest proof a delivery or hand-off has: submitted, accepted, verified or acknowledged. The one number is reported at two of them, accepted and confirmed.
_Avoid_: confidence (that belongs to classification)

**Refused**:
The destination answered and rejected the lead; sending the same thing again will not help.
_Avoid_: failed (too broad), error

**Retrying**:
A delivery waiting for its next attempt after an answer that may succeed later.

**Gave up**:
A delivery that ran out of attempts without being accepted.
_Avoid_: dead-lettered, expired

**Window to act**:
How long after the call we have to get the lead to someone at the firm: 72 hours, or 24 for an emergency or someone in custody. Set by Answering AI. A lead that misses it either *reached the firm late* or was *not reached in time*.
_Avoid_: TTL, expiry date, shelf life

**Expired lead**:
A lead whose window to act passed before any hand-off reached the firm. It is still delivered for the record, marked late, and never presented as new.
_Avoid_: dead lead, stale lead

**Hand-off**:
Getting a lead in front of someone at the firm who can act on it, within its window to act. The job that cannot wait.
_Avoid_: notification (a destination kind), alert (how operators are told)

**Record**:
Getting a lead into the firm's system of record (usually its CRM), complete and in order. The job that can be late.
_Avoid_: sync, backfill

**Fallback**:
A destination the firm agreed, in its approved setup, to receive the hand-off when the usual one cannot: at once if the usual one is stopped, else after 1 hour with nothing accepted (15 minutes for emergencies and custody). Its own clock, separate from the window to act. Every use is shown to the firm; nothing is rerouted silently.
_Avoid_: failover, reroute

**Held**:
A delivery waiting, not sending, because its destination is paused or needs reconnection. Holding keeps the record; it never stands in for the hand-off, which goes to the fallback.
_Avoid_: queued, parked

**Not sent**:
A delivery that was decided against on purpose, with a reason: the routing rule excludes it, or the lead lacks what the destination requires.
_Avoid_: skipped, filtered, dropped

**Cancelled**:
A delivery stopped because its destination was removed or replaced before it was accepted.

**Needs attention**:
A delivery or destination that will not resolve by itself and is waiting for an operator.
_Avoid_: error, alert (an alert is how someone is told)

**Resend**:
Sending a lead to a destination again as a new delivery, under the same lead identity so the destination can recognise a duplicate.
_Avoid_: replay, repost, retry (retrying is automatic)

**Lead outcome**:
One summary of where a lead got to across all its destinations, judged on the newest delivery to each.

## Failure classes

**Transient failure**:
No answer, rate limited or the destination's server failed; worth trying again later.

**Lead problem**:
This lead lacks something the destination requires, such as a caller name.

**Configuration problem**:
The destination refuses every lead because its setup is wrong: a bad mapping, a missing staff ID, a moved address.

**Connection problem**:
The destination no longer accepts the firm's authorization, or the hook was removed.

**Our fault**:
The lead could not be turned into a request at all, or the system failed before sending.
_Avoid_: 500 (a status code, not a cause)

## Billing

**Handled conversation**:
A call or chat that Answering AI took and that reached a person or produced a lead. The unit firms pay for.
_Avoid_: minute, billed minute

**Non-billable rule**:
A named, versioned rule that makes a conversation free (a solicitation, a wrong number, an instant hang-up, a repeat call, a test). Every free conversation records the rule that made it so.
_Avoid_: no-charge (as a call type)

**Price book**:
The published, versioned list of plans, included volumes and rates. Every account is on a version of it; exceptions are explicit discounts.
_Avoid_: rate card per account

**Usage ledger**:
The append-only record of every conversation, from which every invoice can be recomputed.
_Avoid_: billing export

## Terms of service

**Terms version**:
One published text of the terms, with what changed, its kind of change, who must accept it, and when it takes effect.
_Avoid_: TOS update, revision (a revision is a destination's configuration)

**Change kind**:
Editorial (wording only), pricing (what firms pay) or material (what firms, users or we may do). It suggests who must accept.

**Acceptance**:
The record that one user accepted one terms version, for their firm, as admin or member, at a time. Never edited.
_Avoid_: agreement, consent flag

## People

**Operator**:
An Answering AI staff member who watches delivery and acts on what needs attention.
_Avoid_: agent (that word means the AI on the call), admin, support

**Senior operator**:
An operator allowed to take the riskier actions: resending an accepted lead, changing a connection, overriding approval.
_Avoid_: super admin

**Firm contact**:
The person at the firm who approves changes and reconnects destinations.
_Avoid_: client, customer, user
