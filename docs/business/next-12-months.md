# The next 12 months

What the business should do this year, and what we ask leadership to decide. It builds
on [what firms need](what-firms-need.md) and the [commercial case](commercial-case.md);
how the lab's way of working serves it is in [from technical correctness to commercial value](what-the-lab-taught-us.md).
The short version for leadership is [the leadership brief](leadership-brief.md).

## In three sentences

> We already have much of the technology needed for a substantially better answering
> service, but it is spread across separate projects and its performance is not
> consistently measured. Over the next 12 months we will come together around one
> product direction, prove that AI and people together improve accuracy and
> follow-through, and find out which customers will pay for that. We will scale and move
> customers across only where customer outcomes, reliability and economics justify it.

## Where we are

We do not have a technology gap. We have a product, ownership and commercial gap.

Most of what firms need already exists somewhere in the company. The existing AI voice
system follows each firm's rules, involves a person when needed, records every call and
delivers leads with retries and a reconciliation sweep, though whether production runs
the sweep is not yet verified (see "Straight away"). The human service still serves
most firms. Several other
projects overlap them, and nobody owns the caller's whole journey across all of it.

The plan builds on the call center as the source of truth: it owns every interaction,
AI or person, and what each produced, and delivery, billing, call categories and
reports integrate with it through its contracts (Proposed; see
[where this sits](../../README.md#where-this-sits)).

So the question for the year is not what to build next. It is **how to turn what we
already have into the most dependable intake service for the firms we already serve.**

## The product

> **A dependable receptionist that follows your firm's instructions, knows when to
> involve a person, and makes sure every call reaches its next action.**

We are building a dependable intake service, not just an AI receptionist or a delivery
system. Its defining strength is **following each firm's instructions faithfully**:
most complaints about messages are about a call handled differently from how the firm
asked, not about mishearing the caller. That shapes nearly every product decision.

It makes three promises we must be able to show, not just claim:

- **Consistency:** the firm's instructions were followed.
- **Accuracy:** the caller's details were captured correctly.
- **Continuity:** when a call could not be finished, someone took responsibility for it.

And a fourth that leadership judges the year by:

- **Commercial performance:** the better service keeps customers, earns upgrades, and costs less per call handled well.

A better receptionist that loses customers to cheaper ones is not a success, so the
commercial measure sits beside the other three, not below them.

Everything underneath, from lead delivery to the platforms themselves, stays
invisible.

## Three ways to serve a firm

Replacing people with AI is not the goal. A better service at good economics is. The
service should work in three modes, chosen by each firm's needs, the kind of call, the
quality evidence and the cost:

- **AI first:** routine calls, messages, simple qualification and booking.
- **AI-assisted people:** the AI gathers the details, and a person handles what is complex, sensitive or unusual.
- **People first:** the firms or calls where a person clearly does better.

The platform supports all three, and the evidence decides which firms and calls go
where.

## Knowing how good we are

Following rules by design is not the same as following them on every call. A call can
go wrong because the firm's instructions were set up wrongly, because they were not
followed in the conversation, or because the next step never happened afterwards.

So every call should be checked: the right rules in force, the instructions followed,
the details right, the next step done, and someone responsible for anything left open.
Much of this can be checked automatically from the recording and the rules each call
used, but no single check is the truth. An AI reviewer can misread a call, misunderstand
a rule with conditions, or miss that a step never happened. So we combine simple
rule-based checks, an AI reviewer, the record of what each call actually did, and
people reviewing a sample without knowing the AI's verdict. We track how often the
checks wrongly pass a bad call, especially a serious one: a checker that reports all is
well while missing real failures is worse than none. We do not claim the results until
the checks agree with people.

So checking starts small: validate it against independently reviewed real conversations
first (the chat pilot), and expand coverage only once it reliably catches the failures
that affect customers.

## The economics we still need

The strategy is clear on the problem, the product and the method. It still lacks the
numbers that say where the money is: which firms are most profitable, which are worth
upgrading, why firms leave, how much better and cheaper the AI is, and what moving a
firm across is actually worth.

Keep three kinds of value apart, because each needs different proof:

- **Lower cost:** the same calls handled with fewer human minutes, fewer corrections and less support. We can measure this ourselves.
- **Better retention:** firms stay because their instructions are followed and they need to check on us less. This needs to follow groups of firms over time.
- **More revenue:** firms pay for booking, more complex handling, languages or reporting. Only a priced offer they accept proves this.

Never add them into one return-on-investment figure.

**The first-quarter deliverable is a customer-segment contribution-margin analysis.**
For each firm: what it pays, its calls and how many are billable, human handling time,
AI cost, escalations, support tickets, credits, and whether it stayed. Then group the
firms into segments (solo attorneys, growing practices with intake staff, higher-volume
firms), and by practice area and how they are served. Rank segments by contribution
margin, never by revenue or call volume alone. Start with the busiest firms, alongside
a fair sample of smaller ones: the most valuable firms may be the ones combining
volume, high expectations and good margins, not simply the busiest. The data lives in
the billing system and the legacy systems that answer calls today, so it needs their
owners.

The question it answers: **which segment gives us the best chance to improve quality and
economics at the same time, serving firms better with AI and people together, without
losing more of them?** The answer could change the roadmap, because segments may need
different mixes of AI and people.

## What to do, in order

**Straight away**

- Fix the security issues the review turned up.
- Name one accountable owner for the caller's whole journey: voice, chat, human reception and delivery.
- **Verify production delivery.** The existing AI voice system has retries, a reconciliation sweep and a staff retry, but the sweep and the staff retry are off by default, and nothing records whether production has them on. Confirm the deployed settings, how failures become visible, and who acts on them; do the same for the older and human-service delivery paths. The absence of complaints does not prove delivery works.
- Pause new work that duplicates what the call center owns, without throwing anything away yet.

**First**

- Build the customer-segment contribution-margin analysis.
- Talk to customers: current firms, recent cancellations, and firms that turned down the AI service. Why they bought, what they value, why they leave, and which calls they trust to AI. What we know about firms is not recent, so this comes first.
- Assess the call center's readiness against explicit operational, quality and economic acceptance criteria (see the first quarter below), so it is not accepted just because it is the plan.
- Validate quality checking against independently reviewed real conversations, then expand coverage.
- Agree callback ownership and escalation rules with firms, then implement and measure them. The firm controls whether its people act, so the rule must be the firm's.
- Try consultation booking with a few firms: it is already built and aimed at a top complaint.

**Next**

- One shared description of a lead, published by the call center, across voice, chat and the human service.
- Firm rules with a history of changes, approved by the firm.

**Later**

- Move firms across and retire duplicate systems, once the new path has proven itself.

## Three pilots

Run them with real firms, alongside the engineering:

- **Accurate messages:** your instructions followed and your messages right. Do errors fall?
- **Missed transfers recovered:** when nobody answers, the call does not just disappear. Does every failed transfer get a next step?
- **Consultations booked:** qualified callers book without waiting for your staff. How many bookings complete?

Every pilot, and the economic analysis, is judged on the same four dimensions. They are
reported side by side, never added into one figure, so a gain on one cannot hide a loss
on another: booking that works but needs too much human help, or AI that cuts cost but
raises complaints.

| Dimension | Measured by |
|---|---|
| **Service quality** | Material errors per 1,000 calls, from validated checks |
| **Operational effectiveness** | Unfinished calls with an assigned, notified owner and a documented next action |
| **Customer value** | Complaints, credits, satisfaction, retention |
| **Unit economics** | Contribution margin per firm; cost per correctly handled call |

Where we can, we compare against calls handled by the human service.

## The demonstration

An attorney is in court and cannot take a new client's call. The receptionist answers
the firm's way, captures what it needs, tries the agreed transfer, finds nobody, and
starts the firm's follow-up. The right person gets an accurate message with a clear
next step, and the firm can see exactly what happened.

## The year, and what each quarter must prove

Each quarter ends with a decision, and each gate can expand, revise or stop the work.
Finishing the work does not earn the next step; the evidence does.

| Quarter | The question | Evidence required | Decision |
|---|---|---|---|
| First | Is the call center the right foundation, and where is the money? | Current economics by segment, customer discovery, delivery baselines, and the call center's readiness against operational, quality and economic criteria: ready for production, matching what firms rely on today, with someone running it | Choose target segments; confirm the call center as the foundation, or revise the plan |
| Second | Is the improved service better than today's? | Independently validated quality improvements, callback outcomes, cost compared with today | Expand, revise or stop each pilot |
| Third | Which customers and features justify a higher price? | Actual willingness to pay, retention evidence, contribution margin | Approve packaging and pricing |
| Fourth | What do we scale, move across, or retire? | Repeatable quality, reliability, margins, and customers accepting the move | Scale selectively, keep hybrid arrangements, or retire systems |

## Targets

Leadership should approve the plan with targets, not just activities:

- fewer complaints about messages;
- more missed transfers followed through to a next step;
- better retention, and more firms paying for more;
- a lower cost per call handled well, without losing quality.

The numbers come after the first quarter, once we have today's baselines and a
financial model. Setting them before that would be guessing.

## What we ask leadership to decide

For the direction above (dependable intake by AI and people together, built on the call
center as the source of truth):

1. **Authorise a focused first-quarter validation effort:** the economics by segment, customer discovery, production delivery verification, the call center assessment and the chat pilot.
2. **Appoint one accountable owner** for the caller's whole journey.
3. **Agree in advance what evidence permits further investment,** using the quarterly gates and the pilots' scorecard.

| | Status |
|---|---|
| **The customer problem and proposition** | Clear and ready to present |
| **The strategic direction** | A strong hypothesis, supported by historical evidence; needs current customer and production validation |
| **Authorising a substantial engineering roadmap** | Not yet: needs baselines, owners, verified call center readiness and pilot results |

Bringing the systems together is the means, not the goal: on its own, it does not make
a product firms want.

This plan is ready for leadership to discuss, not yet for a large migration or
investment. That commitment should follow the evidence the first two quarters bring:
how firms behave, how good the service measurably is, and what it earns. The
year succeeds if we can say, and show:

> **"We follow your instructions, get your messages right, and make sure callers are
> not abandoned when your staff are busy, and here is the evidence."**
