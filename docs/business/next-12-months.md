# The next 12 months

What the business should do this year, and what leadership is asked to decide. It
builds on [what firms need](what-firms-need.md) and the
[commercial case](commercial-case.md). The figures are rough: they describe the shape
of the business, not exact measurements.

## Where we are

We do not have a technology gap. We have a product, ownership and commercial gap.

Much of what firms need is already built, but spread across separate projects:

- **The AI voice platform** already applies each firm's rules as structured configuration, escalates to human receptionists, records every call, and delivers leads dependably.
- **The legacy human service** still serves most firms, with instructions held as free text that operators read mid-call.
- **Several other efforts** overlap it: a rebuild of the legacy platform, an integration bus, a chat rewrite, and this lead-delivery lab. No one owns the caller's whole journey across them.

So the question for the year is not "what should we build to make lead delivery
reliable?" It is **"how do we turn what we already have into the most dependable
intake service for the firms we already serve?"**

## The product

> **A dependable receptionist that follows your firm's instructions, knows when to
> involve a person, and makes sure every call reaches its next action.**

Three promises, each one we must be able to show, not just claim:

- **Consistency:** the receptionist followed the firm's instructions.
- **Accuracy:** it captured what the caller said correctly.
- **Continuity:** when a transfer failed or a caller needed more, someone took responsibility.

Lead delivery, routing and the platform underneath are the invisible foundation.

## Measuring every call

Structured rules make consistency possible; they do not guarantee it. A call can go
wrong in three different places, and each needs its own measure:

- **Configuration:** the firm's instructions were set up wrongly.
- **Execution:** the receptionist did not follow them during the call.
- **Completion:** the call went well, but the next step (a transfer, a callback, a booking) never happened.

Every call gets a quality record:

| Check | The question |
|---|---|
| **Configuration** | Were the right, approved rules in force? |
| **Adherence** | Were the required questions asked and the instructions followed? |
| **Accuracy** | Does what we captured match what the caller said? |
| **Outcome** | Did the transfer, message, booking or escalation actually happen? |
| **Continuity** | Does every unfinished action have someone responsible for it? |

The recording, the transcript and the snapshot of the rules each call used make
automatic checking possible. The checker also needs the record of transfers, bookings
and deliveries, and a sample of calls reviewed by people. **We do not advertise
"verified" until the automatic checks agree with people's judgement.** "A material
error" gets a written definition before anyone counts them.

## Priorities

| Priority | Action | Why |
|---|---|---|
| **Now** | Rotate the credentials found in plain text, and check whether they were misused | A security risk today |
| **Now** | Name one product owner for the caller's whole journey: voice, chat, human reception and delivery | Stops conflicting investment |
| **Now** | Freeze new work that overlaps the voice platform (the legacy rebuild, the integration bus); keep what is reusable | Stops five architectures growing apart, without throwing anything away yet |
| **First** | Assess whether the voice platform is ready to be the foundation, technically and operationally | Before committing to it |
| **First** | Build the per-call quality record and checker | Shows how good the service really is |
| **First** | Give every unsuccessful transfer an owner and a follow-up | The weakest point firms experience |
| **First** | Pilot consultation booking with a few firms | Already built, switched off, and aimed at the top complaint |
| **Next** | One lead contract across voice, chat and the human service | Lets the systems converge gradually |
| **Next** | Versioned, firm-approved configuration | Correct rules, and a record of who changed what |
| **Later** | Move legacy workflows across and retire duplicate systems | Only once the voice platform matches them and has proven itself |

## Three pilots

Run alongside the engineering, with real firms:

| Pilot | The promise | Measured by |
|---|---|---|
| **Message accuracy** | Your instructions followed, your messages right | Material errors per thousand calls |
| **Missed transfer recovery** | When nobody answers, the call does not just disappear | Share of failed transfers with a completed, acknowledged next step |
| **Consultation booking** | Qualified callers can book without waiting for your staff | Completed bookings per hundred chances to book |

Each pilot also tracks support load, firm satisfaction, cost to serve, and whether the
firm would pay to keep the capability. Where possible, compare against similar calls
handled by the human service. That means reviewing a sample of human-handled calls by
hand, since the human service keeps no transcripts or rule snapshots to compare
automatically.

## The demonstration

**An attorney is in court and cannot take a new client's call.** The receptionist
answers using the firm's approved instructions, captures what it needs, recognises the
kind of call, and tries the agreed transfer. Nobody is available, so it starts the
firm's agreed follow-up. The right person gets an accurate message with a clear next
step, and the firm can see exactly what happened, including whether its instructions
were followed.

It addresses firms' two biggest complaints, the message and the missed transfer, and
the engineering underneath stays invisible.

## The quarters

- **First quarter:** security fix, owner named, overlapping work frozen, the voice platform assessed, the quality checker started, the three pilots designed and the first firms chosen.
- **Second quarter:** the pilots running; the quality checker compared against human review; ownership after missed transfers live for pilot firms.
- **Third quarter:** pricing tested on what the pilots proved; the lead contract agreed; firm-approved configuration.
- **Fourth quarter:** packaging decided on evidence; the plan to move firms from the human service to AI with human escalation, and to retire duplicate systems.

## What we ask leadership to decide

1. **One product direction:** dependable, managed intake by AI and people together, with the existing voice platform as the leading candidate foundation.
2. **One accountable owner** for the caller's whole journey, not for one codebase.
3. **Evidence-driven investment:** engineering is prioritised by its proven effect on message accuracy, calls handled through to their next step, retention, cost to serve, and what firms will pay.

Merging the systems does not by itself make a product firms want. The year succeeds if
we can say, and show:

> **"We follow your firm's instructions, get your messages right, and make sure
> callers are not abandoned when your staff are busy, and here is the evidence."**
