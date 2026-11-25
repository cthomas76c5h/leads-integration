# Discovery scenarios

Real situations we want to walk through with the people they happen to, so the
requirements come from what actually happens. For each one we ask: *the last
time this happened, what did you do?* What we find confirms or changes the
assumptions in [strategy §8](../lead-delivery/strategy.md#8-what-we-know-what-we-assume-and-how-we-check)
and the decisions in [answers.md](../lead-delivery/answers.md).

What happened on a call is the call center's record, not delivery's
([where this sits](../../README.md#where-this-sits)); "call recordings" below are
evidence the center holds.

**First five:** S1, S5, S8, S10 and S19.

## When delivery breaks

| # | Scenario | What we want to learn | Who |
|---|---|---|---|
| S1 | A firm's CRM sign-in expires | How long until someone notices, and how many leads are lost meanwhile | Support, the firm |
| S2 | A CRM vendor has an outage | Whether we see it before firms do, and whether every lead arrives afterwards | Support, engineering |
| S3 | A firm deletes its Zap or webhook | Who notices first, and how the new connection is set up | Support, the firm |
| S4 | A firm changes its CRM or intake email | How we hear about it, and how long leads go to the old place | Onboarding, the firm |
| S5 | A firm says "we never got that lead" | Whether we can prove what happened to it, and how long that takes | Support, the firm |
| S6 | A CRM refuses every lead after a form change | How the cause is found and fixed, and whether refused leads are resent | Support, onboarding |
| S7 | A lead appears twice in a firm's CRM | How often, why, and who cleans it up | Firms, engineering |

## Speed

| # | Scenario | What we want to learn | Who |
|---|---|---|---|
| S8 | A new client calls on Friday evening | When the firm sees it and calls back; whether 72 hours is the right promise | Firms |
| S9 | Someone calls at 2 am about a person in custody | Who can act at night, how to reach them, and what is too late | Criminal defence firms |
| S10 | A firm calls back late and blames the lead | Where our job (reaching the firm) ends and theirs (acting on it) begins | Customer success, the firm |

## Routing

| # | Scenario | What we want to learn | Who |
|---|---|---|---|
| S11 | A call that is not a new matter | Where firms want existing clients, courts and billing calls to go | Firms, call recordings |
| S12 | A firm wants some calls kept out of its CRM | Which calls, and who sets that rule | Firms, onboarding |
| S13 | A call type names a person ("calls for Sarah") | How calls for a named person should be routed | Onboarding, firms |

## Setup and change

| # | Scenario | What we want to learn | Who |
|---|---|---|---|
| S14 | A new firm is set up with a CRM | Every step, hand-off and wait from signup to first lead | Onboarding, a new firm |
| S15 | A firm asks to change what it receives | How changes arrive, who makes them, and whether firms want to approve them | Support, onboarding |
| S16 | A firm closes for a holiday or pauses service | What firms expect to happen to their leads meanwhile | Support, a firm that paused |

## The call itself

| # | Scenario | What we want to learn | Who |
|---|---|---|---|
| S17 | A caller rings back within minutes | How often, and whether it should count as one call or two | Call recordings, billing |
| S18 | A hang-up, wrong number or sales call | How common they are, and whether firms want them or should pay for them | Call recordings, firms |

## Billing and terms

| # | Scenario | What we want to learn | Who |
|---|---|---|---|
| S19 | A firm disputes a call on its bill | Which calls firms expect to be free, and whether a bill can be explained today | Finance, support |
| S20 | A firm changes plan mid-month | What firms expect on their next invoice | Billing, sales |
| S21 | Prices or terms change | Who must accept, who should just be told, and what goes wrong | Legal, customer success |

## Running it at scale

| # | Scenario | What we want to learn | Who |
|---|---|---|---|
| S22 | One outage hits many firms at once | Whether operators see one incident or hundreds of problems, and what firms are told | Support |
| S23 | A problem carries over to the next shift | What gets handed over, and what gets dropped | Operators |
