# Call categories: the missing layer of value

Call categories may be the most under-used part of what Answering AI has. Today
they are a free-text field used mostly as a switch for CRM posting. Treated as
the shared language of a call, they connect intake, delivery and the value firms
see. This page sets out the case; the model and rules are in [README.md](README.md),
the words in [GLOSSARY.md](../../GLOSSARY.md), and the wider direction in
the [strategy](../lead-delivery/strategy.md#29-call-categories-are-the-missing-language).

Figures come from the lab's copy of the source systems ([coverage.json](coverage.json)).
Every rule marked *Proposed* is a direction to test, not something built.

## 1. What they are today: one field doing four jobs

Each firm names its kinds of calls in free text (api-2 calls them "contact
types"). Across about 2,800 firms there are 4,390 distinct names, 3,987 of them
used by only one firm. Mapped onto the [taxonomy](taxonomy.json), the entries
show the field doing four different jobs:

| Job the field is doing | Share of entries | Examples | Where it belongs |
|---|---|---|---|
| **The kind of call** | about 55% | *New Personal Injury Case, *Court Calls, *Existing Client Calls | Call category and practice area |
| **System and billing flags** | 31% | - Admin, * No Charge, ***POP-UP | Account and billing settings |
| **People** | 9.8% (2,172 distinct names) | a staff member's name | A routing rule to a person, from a staff directory |
| **Phone handling** | 4.3% | NO PATCH, Away No Patch, AH | Line-handling rules (patch, after hours) |

And of the kinds of calls that matter most, much is missing:

- **New matters without a practice area:** 2,279 firm entries are just "New Client Calls", plus 583 "Other New Case". The conversation almost certainly says what the matter is; the record does not.
- **One use:** in production, the "active" flag only decides whether a lead goes to the firm's CRMs at all. 99.8% of active call types are new matters, so the flag really means "this is a new matter".
- **Chosen, not understood:** the call type is a label picked during the call, then interpreted afterwards. Nothing records what the call was actually about.
- **Ordering by punctuation:** the `*` prefix exists only to sort the list in the legacy screen.

So the most important fact about each call (what it is, how urgent, for which
area of law) is the least structured data the business holds.

## 2. What they could be: the language of a call

> A **call category** says what kind of call it was; a **practice area** says which area of law; **modifiers** say how it was handled. Together they are what Answering AI tells the firm about every call, in the same words for every firm.

The firm's own wording does not disappear: "*New PI Case" stays the label the
firm sees, attached to `new-matter · personal-injury`. What changes is that the
meaning is standard, so everything downstream can rely on it.

## 3. What that unlocks

| Use | Today | With categories as the language (*Proposed*) | Who gains |
|---|---|---|---|
| **Routing** | Text match against active call types, CRMs only | A rule per destination by category: "new matters to the CRM, everything to email" | Firms, operations |
| **Urgency** | Every lead treated the same | The **window to act** follows the category: 24 hours for an emergency or custody, 72 for everything else, with the fallback after 15 minutes or an hour ([model](../lead-delivery/README.md#window-to-act-and-fallback)) | Firms, callers |
| **How the firm is reached** | One route for all | Emergencies and custody calls to a phone or SMS on-call; billing to the office inbox | Firms |
| **Intake questions** | Per firm, hand-configured | Questions per category and practice area: a car accident asks about injuries and insurance; custody asks about the facility and charges | Callers, firms |
| **CRM fields** | Matter type often blank or free text | Practice area and category filled consistently in every CRM | Firms |
| **Proof of value** | Firms see calls, not outcomes | A monthly report: new matters by practice area, after-hours share, emergencies handled, time to hand-off | Firms, account management |
| **Benchmarks** | None | Aggregated across firms (never caller details): "your personal-injury inquiries this quarter against similar firms in your region" | Firms, sales |
| **What a firm does not take** | Calls arrive anyway | The firm marks practice areas it does not handle; those calls are handled kindly and recorded, and (within bar rules on referrals) could be referred | Firms, callers |
| **Quality** | No feedback loop | Firms correct a category ("that was not a new matter"); corrections improve classification | Everyone |
| **Other verticals** | Legal-specific | Categories are vertical-neutral; practice areas swap for service lines (trades) | The business |

The common thread: once every call carries a standard meaning, routing, urgency,
reporting and quality all read the same fact instead of each guessing from text.

## 4. Separate the jobs

The first step is not new technology but untangling the field:

| Today in "call type" | Moves to |
|---|---|
| Kinds of calls | Call category plus practice area, with the firm's label kept |
| System and billing flags | Account and billing settings |
| Staff names | A staff directory, with routing rules to a person (an existing need, today hidden in the call types) |
| Phone handling | Line-handling settings (patch, no patch, after hours, away) |

The mapping already exists for every current name
(`backend/data/contact-type-mapping.tsv`, generated by `tools/call_categories.py`).

## 5. Steps, cheapest first

| Step | What | Cost | Proves |
|---|---|---|---|
| **1. Show it** | A monthly category report per firm from data already held: new matters by practice area, after hours, emergencies | Low; no change to intake | Whether firms value seeing their calls as matters |
| **2. Confirm it** | Each firm confirms its mapping once; new firms choose categories and practice areas at setup instead of writing labels | Low to medium; onboarding change | The mapping is right; ends the free-text sprawl |
| **3. Untangle it** | Move flags, people and phone handling out of call types (section 4) | Medium | One field, one meaning |
| **4. Classify at intake** | Answering AI sets category, practice area and urgency from the conversation, with a confidence, alongside the firm's label; low-confidence calls are reviewed | Medium to high | Fills the 2,279 new matters without a practice area |
| **5. Drive everything from it** | Routing, window to act, how the firm is reached, intake questions and CRM fields all read the category | High, in steps | The language carries the product |
| **6. Learn from it** | Firm corrections, benchmarks, vertical packs | Ongoing | Value grows with data |

Step 1 can be built in this lab first: it already maps every call type and holds
a week of simulated leads.

## 6. Risks and questions

- **Classification errors.** A wrong category can send a lead the wrong way or set the wrong urgency. Keep the firm's label beside it, record a confidence, review low-confidence calls, and let firms correct.
- **Firms' own meanings.** Some firms use a category differently. The firm's label and its own routing rule stay in charge of its leads.
- **Privacy.** Reports and benchmarks are aggregates; never caller details across firms.
- **Referrals.** Passing on calls a firm does not take is subject to bar rules on referrals and fees; check before offering it.
- **People's names.** The staff-routing long tail contains real names; it stays out of shared documents and the repository (`backend/data/` is git-ignored).

## 7. Our answer

Yes: categories become the standard language of every call. What we decided,
step by step, and what would change it: [answers.md](../lead-delivery/answers.md#call-categories).
