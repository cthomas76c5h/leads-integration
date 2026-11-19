# Lead delivery: what we see, and what we will do

A proposal to leadership, shown in a working lab. For each point we show what we
see, say what we will do, and when. The one-page version is [summary.md](summary.md).

**About what you are looking at.** The lab runs a simulated week on a development
copy of our systems. **Firm names are fictional.** Figures show patterns, not
totals; real numbers from today's systems replace them before we build.

## Ten words used on the screens

| Word | Means |
|---|---|
| **Lead** | One call or chat, written up for the firm: who called, about what, and their answers |
| **Kind of call** (call category) | What the call was about: a new client, an existing client, a court, billing, a sales call… The same dozen kinds for every firm |
| **Connection** (destination) | Where a firm's leads go: its CRM, an email inbox, a text message, or our portal |
| **Reached the firm in time** | A person at the firm had the lead within its time limit: 72 hours, or 24 for an emergency or someone in custody; a lead stuck behind a failing system goes to the firm's backup route within the hour |
| **Too late** (expired) | The time limit passed before anyone at the firm had the lead; by then the caller has often called someone else |
| **Backup route** (fallback) | A second way to reach the firm, agreed in advance (usually a text to its intake team), used when the usual route fails |
| **Never arrived** (missed lead) | A lead that came in while a firm's connection was switched off |
| **Real conversation** | What we propose to charge for: a call or chat that reached a person or produced a lead. Sales calls, wrong numbers and instant hang-ups are free |
| **Running the bills side by side** (shadow billing) | Working out each firm's bill both the old way and the new way, before anyone is charged differently |
| **Terms version** | One published copy of the terms of service, saying what changed and who, if anyone, must accept it again |

## The core story: 15 screens, about 45 minutes

| # | Screen | What we say | What we will do |
|---|---|---|---|
| 1 | [The thesis, with live numbers](01-vision-thesis-and-live-numbers.png) | Firms buy one thing: every new client, reaching them in time. This week, leads arrived too late, leads never arrived, and connections stopped without anyone being told. | Make every lead's journey visible, and measured. |
| 2 | [What day-to-day operations hides](02-vision-what-operations-hides.png) | Ten things that are hard to see while keeping operations running. Each one links to the screen that shows it. | Our answer to each is in [answers](../lead-delivery/answers.md). |
| 3 | [One field, four jobs](04-categories-one-field-four-jobs.png) | The field that should say what a call was about is also used for billing flags, staff names and phone settings. Only about half of it describes the call. | Standard kinds of call for every firm; each other job gets its own place. |
| 4 | [A firm's week in calls](05-firm-report-week-in-calls.png) | The same calls, told the way a firm wants to see them: new clients by area of law, urgent calls, after-hours calls, and how fast each reached the firm. | Every firm receives this weekly. |
| 5 | [Leads that arrived too late](07-leads-expired.png) | Each one says why: it reached the firm hours late, or no connection at that firm accepts that kind of call. | A time limit for each kind of call, and a backup route when it is at risk. |
| 6 | [A lead saved by the backup route](08-lead-reached-by-fallback.png) | The usual routes were down; the firm's agreed text message reached its intake team in 20 minutes, inside the hour. | Every firm agrees a backup route at setup. |
| 7 | [Connections that stopped](11-destinations-stopped-and-missed.png) | Every firm connection, most urgent first: the ones that stopped, why (in the CRM's own words), and how many leads never arrived while they were off. | Alerts the moment a connection stops; missed leads sent on once it is back. |
| 8 | [The daily operations view](12-delivery-center-expired-first.png) | Late leads first, then what needs a person, then what is retrying by itself. | One owner starts every day here. |
| 9 | [The new billing, on our story](19-vision-billing.png) | The old bill is minutes with a hidden multiplier and account-by-account rates; nothing on it shows what the firm got. | Three packages, priced per real conversation, from one record of every call. |
| 10 | [A firm's statement](20-portal-billing-statement.png) | What a firm's admin sees: what this month costs so far, which calls were free and why, and what it got (new clients, leads in time). This firm upgraded mid-month: each plan is charged for its days. | Every invoice explains itself; upgrades start at once. |
| 11 | [Billing operations](23-billing-operations.png) | Every account's bill worked out both ways. Accounts whose old billing data cannot be read come first; they are a work list, not a blocker. | Run the bills side by side for two months, then switch in groups. |
| 12 | [Terms of service: versions](25-terms-versions-and-coverage.png) | Our real history: the first terms, a grammar fix, a promotions clause, a price change. Each says who had to accept it, and how many have. | Ask people to accept again only when a change concerns them. |
| 13 | [A new user's first sign-in](27-portal-first-sign-in.png) | A new team member sees the terms before anything else; acceptance is recorded with the version and time. | Accept once at the start; every acceptance kept on record. |
| 14 | [Three moves, direction, roadmap](03-vision-moves-direction-roadmap.png) | Name an owner; measure before building; build to one standard. Then: trust, self-serve, a platform, and reaching firms inside the CRMs they already use. | A new platform built to one model, in steps a firm can use; the current system is not extended. |
| 15 | [Our answers](28-vision-our-answers.png) | Every open question, answered. | The reasons, and what would change each: [answers](../lead-delivery/answers.md). |

## Backup screens: for detailed questions

| Screen | Shows |
|---|---|
| [Proof of delivery](06-firm-report-proof-of-delivery.png) | For one firm, what each of its connections did with this week's leads |
| [A lead past its time limit](09-lead-expired-past-window.png) | The same outage at a firm without a backup route: the lead arrived over two hours late |
| [The backup firm's week](10-firm-report-fallback-in-time.png) | Every lead reached that firm in time, two of them by the backup route |
| [A hang-up the CRM refused](13-drawer-lead-problem.png) | Why it is not treated as a fault: the caller gave no name |
| [A setup problem](14-drawer-setup-problem-edit-mapping.png) | The CRM's own words ("missing Phone"), what to do, and the button that does it |
| [Fixing it as a draft](15-edit-mapping-draft-revision.png) | The fix waits for the firm's approval; nothing live changes until then |
| [What we send a CRM](16-see-what-we-send.png) | The exact information sent, and what the CRM would refuse |
| [A connection only the firm can fix](17-drawer-connection-problem.png) | The CRM link was removed at the firm's end; the one action is to reconnect |
| [One firm's connections, drawn](18-delivery-graph-and-firm-picker.png) | A lead fanning out to every connection; the search suggests firms needing attention |
| [Last month, locked](21-portal-previous-cycle-locked.png) | A closed month's statement, exactly as billed; later changes never rewrite it |
| [Changing plan](22-portal-change-plan.png) | The rule shown before confirming: an upgrade today, a downgrade from next month |
| [The price list](24-billing-price-book.png) | Plans as published, numbered entries; next month's prices are a new entry, not an edit |
| [Publishing new terms](26-terms-publish-with-preview.png) | Choosing who must accept, with a preview of exactly what users will see |

## What we will deliver

A new lead platform, built to one model in steps a firm can use, each proven in
this lab first. The current lead system is not extended; firms move across one
at a time. The engineering detail is the build order in
[RFC-007](../rfc/007_domain_model_and_build_order.md#8-build-order-thirteen-vertical-slices).

| When | We deliver | You will see |
|---|---|---|
| **From the start** | Real numbers from today's systems: what is lost, what is late, which CRMs are used, the mix of calls | Real numbers instead of the lab's |
| **About two months** | Reliable delivery: every lead recorded before it is sent, retried, and accounted for; the daily operations screens; an alert whenever a firm's connection stops | No lead lost without a trace; problems found by us, not reported by firms |
| **Three to four months** | Delivery in time: calls sorted by kind; the backup route; held leads sent on when a connection returns; nothing a firm receives changes without its approval | Every new client reaching a person at the firm in time |
| **Then** | The CRMs firms use, added one at a time, each tested against its own test account; firms seeing their week and proof of delivery; calls sorted as they come in; the new bill beside the old for two months; terms without lock-outs | Firms seeing what they get; every firm's new bill checked against its old one |
| **After that** | The three packages, launched in groups; firms setting up their own connections; a published way for other software to receive leads; a package for trades businesses | A product suite with predictable revenue, and firms onboarding themselves |

## Questions we expect, and our answers

| Question | Answer |
|---|---|
| **How sure are these numbers?** | They come from a development copy and a simulated week, so they show patterns, not totals. Real numbers from today's systems replace them before we build; we will revise any answer they overturn. |
| **Why build new instead of fixing the current system?** | The current system makes one attempt per lead, keeps no record of what it skips and has no tests; retrofitting that costs more than building to the model we already have. We build in steps a firm can use, prove each in this lab first, and move firms across one at a time, so nothing waits for a big switch. |
| **What does it take?** | Three to four months for three engineers to delivery firms can rely on: reliable, in time, changed only with the firm's approval. The full product in five to seven months with four. Billing buys its invoicing software and builds the call record. These are estimates; the real numbers firm them up. |
| **Will firms lose their own names for kinds of call?** | No. They keep their wording; the standard kind sits underneath, for reports, routing and billing. |
| **Does the backup route send client information somewhere new?** | Only somewhere the firm agreed in advance, and the firm sees every time it is used. |
| **What happens at night?** | Retries and the backup route work around the clock, and urgent calls have a 15-minute limit with a text message. Late leads are the first thing the owner sees the next morning. |
| **Will the new billing lose revenue?** | Every account's bill is worked out both ways for two months before anyone pays differently. Prices are set from what accounts pay today, and the review of unusual terms finds under-charging as well as over-charging. |
| **Why not keep billing by the minute?** | Firms cannot see a hidden multiplier, and they pay for hang-ups and sales calls they did not want. Conversations are what firms recognise, and every free one says why it was free. |
| **What happens to firms on unusual terms?** | Each one is on a work list with an owner; most move to the standard price list within two months; any contract term is honoured until its end date; every change is explained before it is charged. |
| **Why not ask everyone to re-accept the terms every time, to be safe?** | We did: it locked firms out over grammar fixes and pushed them back to text and email. Asking only the people a change concerns, with notice and a record, is safer and kinder. |
| **Does a firm admin's acceptance bind the firm?** | For prices and billing, the admin accepts for the firm and the box says so; each user accepts the terms of their own use. Our counsel confirms the wording. |
| **Is charging per new client allowed for law firms?** | We are not proposing it now; it waits for six months of data and a legal review. |
| **How does this help an IPO or a sale?** | Predictable revenue by package, invoices anyone can check, retention we can show per firm, costs that do not grow with every new firm, and a unique record of what callers need across thousands of firms. |
| **What do operators do differently on Monday?** | Start the day on one screen: late leads, then stopped connections, then what needs a person, each with its cause and the button that fixes it. |
| **What if a CRM changes how it works?** | Every CRM connection is tested against what it expects; a change shows up as a named problem with an alert, not as leads quietly going missing. |
| **What if the AI gets the kind of call wrong?** | Each call carries how sure the AI was; uncertain ones are reviewed, the firm's own wording stays visible, and firms can correct it. |
| **Is client data safe?** | We never store CRM passwords or keys, leads carry only what the caller told us, and every staff action is recorded. |

---

### Running the lab and refreshing these screenshots

In `backend/`: `dotnet run --project src/Import -- demo-names` (fictional firm
names), then `dotnet run --project src/Import -- simulate`, then
`dotnet run --project src/Api`, and open http://localhost:5080/#/vision.
From the repo root, with the API running: `python tools/capture_vision_deck.py`.
Detail behind every claim: [docs/lead-delivery/](../lead-delivery/README.md),
[billing](../billing/strategy.md), [terms](../terms/strategy.md).
