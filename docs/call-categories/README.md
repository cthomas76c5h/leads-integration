# Call categories

Every firm names its kinds of calls in free text ("contact types" in api-2).
Across 2,771 firms there are 4,390 distinct names, and 3,987 of them are used by
only one firm. Underneath, they describe a small, stable set of things. This
folder defines that set and the rules that map any name onto it.

| File | What it is |
|---|---|
| [why-it-matters.md](why-it-matters.md) | Why categories may be the missing layer of value: one field doing four jobs today, and what a shared language of calls unlocks. Start here for the business case. |
| [taxonomy.json](taxonomy.json) | Categories, practice areas, modifiers and the matching rules. The source of truth. |
| [reference-leads.json](reference-leads.json) | 42 synthetic leads (realistic intake, invented people), each with the mapping it must get. Use them to test a mapping or a CRM payload. |
| [coverage.json](coverage.json) | Generated. Aggregate counts, plus example names used by 5+ firms. |
| `backend/data/contact-type-mapping.tsv` | Generated, git-ignored. Every raw name and its mapping, for review. The long tail contains staff names. |

```
python tools/call_categories.py      # regenerate; exits 1 if a reference lead maps wrong
```

The backend embeds these three JSON files (`backend/src/Core/Categories.fs`,
the same algorithm in F#). It serves them at `/api/call-categories` and
`/api/reference-leads`, and maps each firm's contact types. The app's **Call
categories** page shows all of it and sends any reference lead to a firm. The
Expecto test "every reference lead maps as it says" holds the F# rules to the
same 42 cases as the script.

## The model

A contact-type name maps to **one category**, a **practice area** (new matters
only) and any number of **modifiers**.

```
"*New Criminal Case (Jail)"  →  new-matter · criminal · [in-custody]
"AH NO PATCH"                →  line-handling · [after-hours, no-patch]
"*General Calls Morgan"      →  general · [named-person]
```

### Categories

`crm` is the default for delivery to a CRM: **send**, **skip** (a message, not a
lead), **review** (the firm decides) or **never** (not a kind of call).

| Category | crm | Share of names in use* | Example |
|---|---|---|---|
| `new-matter` New matter | send | 27.7% | *New Client Calls, *New Personal Injury Case |
| `general` General | review | 13.1% | *General Calls, *Spanish Speaking Calls |
| `legal-professional` Court, attorney or agency | skip | 10.1% | *Court Calls, *Opposing Counsel Calls |
| `insurance-medical` Insurance or medical provider | skip | 2.7% | *Insurance/Medical Calls, *Insurance Adjuster Calls |
| `existing-client` Existing client | skip | 0.5% | *Existing Client Calls |
| `emergency` Emergency | skip | 0.4% | *Emergency Calls |
| `jail-custody` Jail / custody | review | 0.2% | *Jail Calls |
| `billing` Billing | skip | 0.1% | *Billing/Payment Calls |
| `vendor` Vendor or solicitation | skip | <0.1% | |
| `system` System entry | never | 31.0% | - Admin, * No Charge, ***POP-UP |
| `line-handling` Line handling | never | 4.3% | NO PATCH, Away No Patch |
| `staff-routing` Named person | review | 9.8% | a person's name only |

\* Of 23,316 firm × name occurrences. 0.1% (30 names, each used by one firm) is unmapped.

The last three are **not kinds of calls**. They're the platform's own entries,
how the line is handled, or a staff member's name. They make up 45% of
everything and must never decide whether a lead is delivered.

### Practice areas (new matters)

| Area | Firms × names | | Area | Firms × names |
|---|---|---|---|---|
| unspecified ("New Client Calls") | 2,279 | | immigration | 132 |
| personal-injury | 852 | | business | 103 |
| criminal | 765 | | bankruptcy | 93 |
| family | 628 | | employment | 92 |
| other ("Other New Case") | 583 | | medical-malpractice | 89 |
| estate-planning | 234 | | workers-comp | 75 |
| traffic | 211 | | disability | 68 |
| real-estate | 181 | | civil-litigation, consumer, insurance-claims, tax | 74 |

### Modifiers

`named-person` (844), `patch` (612), `no-patch` (587), `away` (354),
`after-hours` (281), `spanish` (128), `in-custody` (45), `business-hours` (28),
`other-language` (11), `webchat` (5).

Modifiers never change the category. "*New Client Calls Spanish" is a new
matter, and "*Spanish Calls" is General.

## How to map a name (the rule order)

1. **System.** Admin, no charge, pop-up, test, number and recording entries. A
   name starting with `-` is also a system entry unless it names a category.
2. **New matter** if the name says *new / potential / prospective / intake*. This
   check comes early on purpose: "New Divorce/Custody Call" is family, not jail,
   and "New Property Insurance" is an insurance claim, not an insurer calling.
3. Existing client → jail / custody → court, attorney or agency → insurance or
   medical → emergency → billing → vendor.
4. **General** (general, all calls, messages, returning a call, other calls).
5. **New matter by practice area alone** ("Slip and Fall", "After Hours Personal
   Inj"). This never applies to "other": "OTHER Calls" is General.
6. **Line handling** if only handling words are left (patch, away, after hours).
7. **Named person** if only a name is left.

Practice areas are checked in the order in taxonomy.json. More specific areas
come first: medical malpractice before personal injury, real estate before
estate planning.

The source system cuts names at 25 characters ("*New Motor Vehicle Accide",
"*New Social Security/Disa"). Patterns accept the stubs, and the last word of a
long name is never read as a person's name.

## Evidence the rules are right

api-2 marks a contact type **active** when that kind of call counts as a lead.
The rules never see that flag, so it serves as an independent check. Across
active firms:

- **99.8%** of active types classify as `new-matter` (2,672 of 2,678).
- **98.4%** of types classified `new-matter` are active (2,672 of 2,716). The
  rest are mostly firms that switched off one practice area.

At the 122 active firms with a CRM integration, **every** firm's active types
include new matters, and at most 4 of them have anything else active as well. Today's CRM routing
rule is already "send new matters, optionally only these practice areas". The
taxonomy lets it be stated that way instead of as a list of free-text names.

## What this means for routing

A firm's CRM rule becomes:

```
send: new-matter
practice areas: all | [personal-injury, criminal, ...]
also: jail-custody? general?   (the review categories, the firm's choice)
chat leads: yes | no
```

That is a short checkbox list a client can approve, instead of approving 4,390
possible strings. Matching by text (`Dispatch.routingExclusion`) can stay as the
fallback for unmapped names.

## Open questions

- **General as a lead.** Today it almost never is: General is active at only 2
  firms, both alongside new matters. It could default to `skip`. It stays
  `review` until someone confirms that no firm relies on it.
- **Named-person types (2,172 names).** Mostly "calls for this attorney".
  *Answered:* they move to a staff directory with routing rules to a person
  ([answers](../lead-delivery/answers.md#call-categories)); assigning the CRM
  user is a per-firm mapping, not taxonomy.
- **Jail calls.** Criminal firms likely want them in the CRM, and others don't.
- **Practice area "unspecified"** is the most common. The real area is in the
  intake answers ("Case Type", "Select Reason For Call:"), not in the call type.
  *Answered:* Answering AI sets it at intake from the conversation
  ([answers](../lead-delivery/answers.md#call-categories)); mapping it from the
  intake answers is the step before.
