# Answering service: where we are, what we're building, and why

A brief for leadership. It summarises [what firms need](what-firms-need.md), the
[commercial case](commercial-case.md), [the next 12 months](next-12-months.md) and
[what the lab taught us](what-the-lab-taught-us.md), with a reviewer's changes: quality
checking validated before it expands, callback rules agreed with firms, one scorecard
for every pilot, and gates that end in a decision.

## In one paragraph

Law firms use us to answer the calls they can't take, mostly during the working day
while attorneys are in court or with clients. They judge us by the message we leave,
and their main complaint is a call handled differently from how they asked. Lost leads
are a much smaller complaint. What they buy is **dependable reception and accurate
handling of the calls they can't take themselves**. New clients are an especially
valuable part of that, but only about one call in five. We already have most of the
technology needed for a better service, but it's spread across separate projects,
nobody owns the caller's whole journey, and nobody consistently measures how well we
perform. The plan builds on the call center as the source of truth, and everything
else integrates with it ([where this sits](../../README.md#where-this-sits)).

## What firms need

The figures are rough, and our data on firms is not recent.

- **Someone to answer when they can't.** About 80% of calls come during business hours.
- **A message they can act on.** Well over half of what firms raise with us is about the message:
  - more than a third: a call handled differently from how the firm asked (booking, fees, when to put a call through);
  - about a quarter: calls the firm didn't want, or wants credited;
  - about one in ten: a wrong or missing detail.
- **The right person, reached in time.** About two in three attempted transfers go unanswered.
- **Information where they already look.** That means email and text. Fewer than one lead in ten is ever opened in the portal.
- **Paying only for calls that matter.** About 40% of calls are hang-ups, wrong numbers or sales calls.
- **Calls sorted the way they think about them.** Existing clients, courts, insurers, messages for a named person, and new clients.
- **Confidence nothing slips through.** Firms assume dependable delivery. They don't buy it separately.

## What we changed our minds about

The evidence contradicted our original assumptions, and the strategy changed because of it.

| We assumed | We now think |
|---|---|
| Lead delivery is the product | Dependable, accurate call handling is the core service |
| Lost leads are the main pain | The message we leave is the pain firms feel |
| After-hours cover is the main use | Daytime overflow matters more |
| CRM integrations are central | Most firms work from email and text |
| Firms want a portal or dashboard | Information has to reach them where they already look |
| Reliability is a premium tier | Reliability belongs in the core service |

## The product

> **A dependable receptionist that follows your firm's instructions, knows when to
> involve a person, and makes sure every call reaches its next action.**

It makes three promises we must be able to show, not just claim:

- **Consistency:** the firm's instructions were followed.
- **Accuracy:** the caller's details were right.
- **Continuity:** when a call couldn't be finished, someone took responsibility.

Firms can be served in three ways, chosen per firm and per kind of call by the
evidence: **AI first**, **AI with a person**, or **people first**.

## The plan, in order

**Straight away**

- Fix the security issues the review found.
- Name one accountable owner for the caller's whole journey: voice, chat, human reception and delivery.
- **Verify production delivery.** The existing AI voice system has retries, a reconciliation sweep and a staff retry, but the sweep and the staff retry are off by default, and nothing records whether production has them on. Confirm the deployed settings, how failures become visible, and who acts on them. Do the same for the older and human-service delivery paths. The absence of complaints doesn't prove delivery works.
- Pause new work that duplicates what the call center owns, without throwing anything away.

**First**

- **Customer-segment contribution-margin analysis.** For each firm: revenue, billable calls, human time, AI cost, escalations, tickets, credits and retention. Group firms into segments: solo attorneys, growing practices with intake staff, and higher-volume firms. Rank segments by contribution margin, not by revenue or call volume.
- Talk to customers, including recent cancellations and firms that turned down the AI service.
- Assess the call center's readiness against explicit acceptance criteria (see Q1).
- **Validate quality checking** against independently reviewed real conversations, starting with the 25-conversation chat pilot. Expand coverage only once it reliably catches failures that affect customers.
- **Agree callback ownership and escalation rules with firms,** then implement and measure them. The firm controls whether its people act, so the rule must be the firm's.
- Trial consultation booking with a few firms. It's already built.

**Next**

- One shared description of a lead, published by the call center, across voice, chat and the human service.
- Firm rules with a change history, approved by the firm.

**Later**

- Move firms across and retire duplicate systems, once the new path has proven itself.

## Pilots, and one scorecard for all of them

1. **Accurate messages:** do errors fall when each firm's rules are applied every time?
2. **Missed transfers recovered:** does every unanswered transfer get a next step?
3. **Consultations booked:** how many qualified callers book without waiting for staff?

Every pilot, and the economic analysis, is judged on the same four dimensions. They're
reported side by side, never added into one figure, so a gain on one can't hide a loss
on another. For example, booking that works but needs too much human help, or AI that
cuts cost but raises complaints.

| Dimension | Measured by |
|---|---|
| **Service quality** | Material errors per 1,000 calls, from validated checks |
| **Operational effectiveness** | Unfinished calls with an assigned, notified owner and a documented next action |
| **Customer value** | Complaints, credits, satisfaction, retention |
| **Unit economics** | Contribution margin per firm; cost per correctly handled call |

## Quarterly gates

Each gate can expand, revise or stop the work. Finishing the work doesn't earn the next step.

| Quarter | Evidence required | Decision |
|---|---|---|
| Q1 | Current economics by segment, customer discovery, delivery baselines, and the call center's readiness against operational, quality and economic criteria | Choose target segments; confirm the call center as the foundation, or revise the plan |
| Q2 | Independently validated quality improvements, callback outcomes, cost compared with today | Expand, revise or stop each pilot |
| Q3 | Actual willingness to pay, retention evidence, contribution margin | Approve packaging and pricing |
| Q4 | Repeatable quality, reliability, margins, and customers accepting the move | Scale selectively, keep hybrid arrangements, or retire systems |

Numeric targets get set after Q1, once today's baselines exist.

## What the labs are for now

They're experimental tools, not the product. They've already been worth building: they
made us separate what we promise, what we can prove, what we control and what customers
value.

- **Call center lab:** the source of truth. It owns every interaction, the people who answer, and what each produced, and publishes them through its own versioned contracts.
- **Intake policy lab:** turns a firm's instructions into approved, versioned rules that feed the call center, and checks each conversation against them. Its first real test is the chat pilot, because AI voice has no real customer calls yet. Chat can test required questions and callback ownership. It can't test transfers or fee rules, which make up the largest group of complaints, so the voice pilot has to test those.
- **Lead delivery lab:** worked out how to deliver leads with retries, fallbacks and proof, taking each lead from the call center (Proposed). What carries over is its method: domain modelling, pure decision rules, and scenarios anyone can run. Not its code.

## Open questions

- **Which customer segment gives us the best chance to improve quality and economics at the same time?** The answer could change the roadmap, because segments may need different mixes of AI and people.
- Why do firms cancel or upgrade?
- How often is a caller actually lost, and is production delivery dependable today?
- What will firms pay, and for what?

## Where this stands

| | Status |
|---|---|
| **The customer problem and proposition** | Clear and ready to present |
| **The strategic direction** | A strong hypothesis, supported by historical evidence; needs current customer and production validation |
| **Authorising a substantial engineering roadmap** | Not yet: needs baselines, owners, verified call center readiness and pilot results |

## What we ask leadership to decide

1. **Authorise a focused Q1 validation effort:** the economics by segment, customer discovery, production delivery verification, the call center assessment and the chat pilot.
2. **Appoint one accountable owner** for the caller's whole journey.
3. **Agree in advance what evidence permits further investment,** using the gates and scorecard above.

> **The year succeeds if we can say, and show:** "We follow your instructions, get your
> messages right, and make sure callers are not abandoned when your staff are busy, and
> here is the evidence."
