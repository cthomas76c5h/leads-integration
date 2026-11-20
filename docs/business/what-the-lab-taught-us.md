# From technical correctness to commercial value

What building this lead-delivery lab taught the business, and how its way of working
can serve [the next 12 months](next-12-months.md). In one line: building the right
system correctly first requires establishing that it is the right system to build.

## Prove the problem deserves solving first

The lab showed how to make strong guarantees about a system's behaviour. Those
guarantees did not show that customers valued that behaviour, that the company lacked
it, or that building it was the best use of engineering time. Proving a solution
correct is worth little until the problem is proven worth solving. Customer evidence,
knowing what the company already has, and precise specifications have to inform each
other from the start.

## About the business

**Building first answers the wrong questions well.** The lab answered every engineering
question precisely: how to never lose a lead, how to stay correct when things happen at
once, how to prove it. The questions that mattered most commercially (what firms
complain about, how they use us, where callers are lost) were answered far faster by
looking at how firms actually use the service, and the answers overturned several of
our assumptions.

**The real gap was never delivery.** It was handling each call the way the firm wants,
and following through when nobody at the firm can pick up.

**Much of the answer was already built elsewhere.** The lab was one of several projects
that did not know about each other. That fragmentation, not any missing technology, is
the biggest risk to the year.

## About the way of working

**Model the domain first, in the business's own words.** Calls, leads, hand-offs, what
counts as evidence, and the states each can be in with the rules for moving between
them. Everyone could reason about the model, and the code followed it.

**Keep decisions pure.** The important rules (where a lead goes, whether to try again,
what happens next) take facts in and give a decision out, with no side effects. That
makes them easy to test, easy to explain, and easy to record with the reason they were
made.

**Write the rules down as scenarios anyone can run.** Each rule became a short story
with an expected outcome, written as data rather than code, that any implementation in
any language must pass. They caught wrong assumptions, including a contradiction
between two of them.

**Hold the documents to the code.** One of our own documents stated something false
about how the system behaved; a test found it. Descriptions that are checked against
the code stay true; ones that are not drift.

**Make sure checks can fail.** A search through every possible ordering of events found
a real bug that no hand-written scenario had. Bugs planted on purpose proved the checks
would catch problems, rather than just reporting that all was well.

**Evidence beats status.** "Sent", "accepted", "confirmed" and "unknown" are different
claims, and an honest system never merges them.

**Best guesses, marked as best guesses.** Deciding from what we knew, and saying what
would change the decision, kept work moving and made the guesses easy to overturn when
real data arrived.

## Using it over the next 12 months

Carry the way of working across, not the code or the language. It pays off where the
plan's decisions and risks are:

- **Each firm's rules as a model, applied step by step.** A call unfolds: facts arrive as it goes, and the right next step depends on what has already happened. So the rules are applied one event at a time: given where the call is, what just happened, and the firm's approved rules, decide the next step, what is allowed, and why. The same rules then govern the greeting, a missing answer, a failed transfer and an acknowledged callback alike. They say what is **required**, **permitted** and **prohibited**, without scripting every word. The receptionist handles the conversation; the rules control the decisions that matter. The rules are data, versioned and approved by the firm.
- **Quality checks with independent points of reference.** Comparing what the rules said should happen with what the call actually did settles most questions for certain, and separates the three ways a call goes wrong: the rules set up wrongly, not followed, or the next step never done. But if the receptionist and the checker share the same mistaken reading of a firm's instructions, both agree and both are wrong. So the checks rest on three references that do not depend on each other: what the firm actually approved, what the system did according to its records, and an independent judgement of whether that matched what the firm meant. AI review covers what only the conversation can show, and people's review keeps all of it honest. The expected outcomes in our scenarios are written independently, never worked out by the same rules being tested.
- **The follow-up after a missed transfer as a clear set of states,** each with a deadline: waiting, someone responsible, acknowledged, done, or escalated. And the steps stay distinct: a callback task acknowledged is not a callback made, and a callback made is not a successful conversation. Keeping them apart lets us measure stronger and stronger outcomes without claiming more than happened. The lab already has this model, and the checks for when two things happen at once.
- **Scenarios as the shared standard.** The voice platform's test conversations also check what should happen next, not only what the receptionist says. The same scenarios hold voice, chat and the human service to one standard, and give each quarter's decision its evidence.
- **One description of a lead** between voice, chat, the human service and wherever leads go, with a translation at each edge.
- **Which way each call is served** (AI first, AI with a person, or a person first) as a recorded decision with its reason, so the pilots can compare fairly.
- **Billing from the same records,** so the trial bills come from the calls themselves.

## The guard rail

Use this way of working only where the decisions and the risks are: firm rules, call
quality, follow-up and billing. Measure before building. The clearest lesson of the lab
is that a rigorous method can just as easily perfect the wrong thing. This year it
serves the three promises to firms; it is not the product.

## Four disciplines, together

Neither building first nor asking the market first works alone. The way forward joins
four disciplines, each informing the others:

1. **Commercial discovery:** what firms need, value and will pay for.
2. **Domain modelling:** the business's rules, responsibilities and outcomes, stated precisely.
3. **Executable verification:** proof that the system behaves as independently confirmed requirements say it should.
4. **Production feedback:** the service's real quality, its commercial results, and the assumptions that need revising.
