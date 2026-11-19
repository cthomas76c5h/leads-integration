# Lessons: what building the lab taught us

We built the lab's operator console, firm report, billing, terms of service and
leadership deck by using them, on a simulated week of leads. This page records
what got in the way and what each problem says a 10/10 product needs. It is our
own dogfooding report: the features came quickly; the hard part was always not
knowing what was true.

It complements the [strategy](strategy.md) (the direction) and
[answers](answers.md) (the decisions). Domain-specific lessons stay with their
domain: billing's rollout in [billing strategy §9](../billing/strategy.md#9-what-the-rollout-taught-us)
and terms of service in [terms strategy §1](../terms/strategy.md#1-the-story-so-far-problems-and-what-solved-them).

Each lesson ends with **Needs** (what a 10/10 product needs) and **Where**
(the roadmap step or document that carries it).

## 1. We could not see reality

- **No production truth.** We never saw a real delivery outcome. The local api-2 copy may be a scrubbed restore and is mostly test traffic: 7 of the 8 enabled CRMs are on one internal test firm. Every rate in the deck is a signal, not a fact.
- **Data we had to invent:**
  - call durations (synthetic talk time, so billing's free-call rules are untested);
  - realistic lead timestamps (spread over a simulated week);
  - billing data (the billing system's code is not here, so its model came from screenshots of its portal);
  - how fast firms act on a lead (the windows to act are starting values);
  - prices (illustrative).
- **Real names everywhere.** Firm names, staff names hidden in call types, and contacts in pasted threads. Each needed manual care, and in the end a separate tool (`Import -- demo-names`) to replace firm names with fictional ones before any screenshot.

**Needs:** a read-only, anonymised feed of production into the lab, so the demo
is reality; and delivery records in production that answer "what happened to
this lead?". **Where:** roadmap step 1 (measure), which every later step depends on.

## 2. Every system describes things differently

- **No standard lead.** api-2, the chatbot and the portals each have their own lead shape, so the lab defined one ([api.md](../api.md)).
- **One field doing four jobs.** Call types carry kinds of call, billing flags, staff names and phone settings, so every rule had to reverse-engineer meaning from free text ([why-it-matters.md](../call-categories/why-it-matters.md)).
- **Configuration with no owner.** Plan minutes read from product names, overage rates stored as text, the billing cycle start held by a different system from the invoice, a billing category left blank.

**Needs:** one published, versioned lead format, and one owner per piece of
configuration. Free text is what a firm *sees*, never what a system *decides* on.
**Where:** roadmap step 3; [answers: call categories and billing](answers.md#call-categories).

## 3. We could not test against the real world

- **Fake CRMs only.** The lab's fake receivers prove what we send has the right shape, not that the lead lands in Clio or MyCase. "In the CRM: not visible to us" is honest, and a gap.
- **Credentials cannot be used,** rightly, so no end-to-end test touches a real system.

**Needs:**

- a sandbox account with each of the six supported CRMs, and automatic tests against them;
- test leads sent through every live connection on a schedule, so a stopped connection is caught by us within minutes, not by the firm;
- where a CRM allows it, checking that the record actually arrived (**verified in CRM**).

**Where:** roadmap step 2 (reliability) and the certified integrations in [strategy §4.2](strategy.md#42-what-it-means).

## 4. Problems only show up across many leads

The original operator designs showed one refused lead at a time. A simulated
week of traffic showed problems that only appear in aggregate:

- **A stopped destination is the biggest problem, and it is quiet:** leads stop arriving rather than failing. It needed its own screen (Destinations), ahead of individual deliveries, and needs an alert.
- **Outages hit a destination kind, not a firm.** Forty deliveries retrying to one CRM read as forty problems instead of one outage.
- **Lead problems were noise.** Hang-ups refused by a CRM looked like mapping problems, filled "needs attention", and counted towards switching the destination off. They needed their own class.
- **Age matters.** "Retrying" for 3 minutes and for 3 hours are different situations; late ones must be flagged.
- **Late is a kind of lost.** Missed leads sent days later arrived looking new. That led to the window to act, the expired outcome and the fallback.

**Needs:** a console that operates on patterns (destinations, kinds, age, window
to act), not only on rows. **Where:** acted on in the lab ([operations.md](operations.md#screens));
alerts and outage grouping are still to build.

## 5. The demo data became a product of its own

- **A feature was invisible until the simulator created a scenario for it.** That made the simulator a second implementation of the delivery rules, kept from drifting only by sharing `Dispatch.transition` and the same routing, payload and receiver checks.
- **Repeatability was hard:**
  - append-only tables meant reruns had to reset, not delete;
  - deletes had to run in the right order (invoices before subscriptions before the price list);
  - random values made a test flaky;
  - the screenshot run itself changes the data (a mapping draft).
- **Time runs at two speeds.** Retries run in compressed lab time; windows to act run in real minutes.

**Needs:** demo and test data treated as a first-class product:

- a library of named scenarios, each with its story;
- fictional names by default;
- a reset button;
- the leadership deck captured automatically on every change, so it cannot drift from the product.

**Where:** the lab (`Simulate.run` and `tools/capture_vision_deck.py`, see [code-map](code-map.md#6-where-the-labs-data-comes-from)).

## 6. Words and screens drifted

- **The code says one thing, the business another.** The code says connection, replay and skipped; the business says destination, resend and not sent. We documented the difference ([code-map](code-map.md#names-the-code-still-uses)) instead of fixing it.
- **Jargon leaked into the screens:** HTTP codes, "prorated", "ledger", "api-2". Every screen needed a second, plain-language pass before leadership could read it.
- **Bugs that only real use found:**
  - totals counted only the 100 rows on screen (now server totals);
  - filters ignored links;
  - the firm picker was hard to use (now ranked by live destinations);
  - a firm's "paused" connections were in fact switched off in the old system;
  - "Home Services" businesses became law firms on a second rename.

**Needs:**

- one vocabulary across code, screens and docs ([GLOSSARY.md](../../GLOSSARY.md));
- separate views for operators, firms and executives, each in its own language;
- someone using the tools every day (the lead delivery owner) and feeding back weekly.

**Where:** roadmap step 2 (the daily operations screens) and the owner in [strategy §6](strategy.md#6-operations-scale-through-product-not-headcount).

## 7. Built to explore, not to run at scale

- **Filtering happens in memory.** Fine at 600 leads; not at production volume.
- **Proposed, not built:**
  - held deliveries while a destination is stopped (missed leads stand in);
  - firm-set windows to act;
  - categories set at intake (they still come from labels);
  - the usage record billing reads, plus payments and tax;
  - a record of operator actions beyond setups;
  - reconnect links for firms, and alerts.
- **Legal questions open:** the terms wording for admins accepting for the firm, rules on paying per lead, and how long data is kept.

**Needs:** production engineering, not more lab features: queries and indexes
sized for volume, held deliveries, the audit trail, and counsel's answers.
**Where:** roadmap steps 2–3; the Proposed rules in [README.md](README.md).

## 8. Friction in the build itself

- **Restarting the server to rebuild.** The running API locks its own files, which also caused a port conflict.
- **Too many documents before a summary.** Fourteen documents existed before the one-page summary; several repeated each other until they were consolidated into this set.

**Needs:**

- one command to run the lab;
- tests and screenshot checks on every change;
- the summary written first, and each fact stated in one place.

## In short

What stands between this and a 10/10 is not features. It is four things:

| | What | First step |
|---|---|---|
| 1 | **Real data** flowing in | Roadmap step 1: the production report and delivery records |
| 2 | **Real CRMs** to test against | Sandboxes for the six supported CRMs; scheduled test leads |
| 3 | **One shared language**, in the data and in the words | Call categories at intake; one lead format; the glossary in code |
| 4 | **Someone using it every day** | Name the lead delivery owner; a weekly feedback review |
