# RFC-002: Call Categories as the Standard Language of a Call

**Answering AI — Product and Technical Proposal**

**Date:** November 19, 2026
**Status:** Proposed — Awaiting stakeholder approval
**Decision owner:** Product Leadership
**Reviewers:** Operations, Onboarding, Engineering, Customer Success, Sales, Management
**Related:** RFC-001 (windows to act and routing read the category) · RFC-003 (free-call rules read it)
**Detail:** [taxonomy and rules](../call-categories/README.md) · [why it matters](../call-categories/why-it-matters.md) · [answers](../lead-delivery/answers.md#call-categories)

---

## 1. Executive Summary

The most important fact about each call (what it was about, how urgent it is,
which area of law) is the least structured data Answering AI holds.

Each firm names its kinds of call in free text. Across 2,771 firms there are
**4,390 different names**, 3,987 used by only one firm. Underneath, they describe
**about a dozen kinds of call**. The same field is also used for billing flags,
staff names and phone settings, so only about half of it describes the call.

**We propose a standard set of call categories, practice areas and modifiers for
every call, set by Answering AI at intake from the conversation, with each firm's
own wording kept.** Routing, urgency, billing's free-call rules and the firm's
report then all read the same fact instead of guessing from text.

### Recommended decision

Approve:

1. The **taxonomy**: twelve categories, practice areas for new matters, and modifiers, versioned in one file.
2. **Untangling the call-type field**: billing flags, staff names and phone settings move to their own places.
3. **Classification at intake** by Answering AI, with a confidence, review of uncertain calls, and firm corrections.
4. Categories as the input to **routing, windows to act, free-call rules and reports**.
5. The **firm report** ("your week in calls") for every firm, and benchmarks in the Insight package.

---

## 2. Problem Statement

### One field doing four jobs

| Job the call-type field does | Share of entries | Examples | Where it belongs |
|---|---|---|---|
| **The kind of call** | about 55% | *New Personal Injury Case, *Court Calls | Call category and practice area |
| **System and billing flags** | 31% | - Admin, * No Charge, ***POP-UP | Account and billing settings |
| **People** | 9.8% (2,172 distinct names) | A staff member's name | A staff directory, with routing to a person |
| **Phone handling** | 4.3% | NO PATCH, Away No Patch, AH | Line-handling settings |

### What is missing

- **New matters without a practice area:** 2,279 firm entries are just "New Client Calls", plus 583 "Other New Case". The conversation says what the matter is; the record does not.
- **One use only:** in production, the "active" flag decides whether a lead goes to the firm's CRMs at all. 99.8% of active call types are new matters, so the flag really means "this is a new matter".
- **Chosen, not understood:** the call type is a label picked during the call. Nothing records what the call was actually about.
- **"No charge" is a call type:** whether a call is billed depends on a label someone chose (RFC-003).

### Business impact

- Every firm is a bespoke configuration; most support work is the same few needs expressed thousands of ways.
- Urgency cannot follow the call: an emergency and a vendor call are treated alike (RFC-001).
- The value we deliver (new clients, by area of law, in time) cannot be shown to the firm, at renewal or to investors.

---

## 3. Product Vision

> A **call category** says what kind of call it was; a **practice area** says which area of law; **modifiers** say how it was handled. Together they are what Answering AI tells the firm about every call, in the same words for every firm.

The firm's wording does not disappear: "*New PI Case" stays the label the firm
sees, attached to `new-matter · personal-injury`.

| Use | Today | With categories |
|---|---|---|
| **Routing** | Text match against active call types, CRMs only | A rule per destination by category: "new matters to the CRM, everything to email" |
| **Urgency** | Every lead the same | The window to act follows the category (RFC-001) |
| **How the firm is reached** | One route for all | Emergencies and custody calls also by text; billing to the office inbox |
| **Intake questions** | Per firm, hand-configured | Per category and practice area |
| **CRM fields** | Matter type often blank | Practice area and category filled consistently |
| **Billing** | "No charge" as a label | Free-call rules by category (RFC-003) |
| **Proof of value** | Firms see calls | A weekly report: new clients by area of law, urgent calls, after-hours share, time to reach the firm |
| **Benchmarks** | None | Aggregated across firms, never caller details, with an opt-out |
| **Other businesses** | Legal-specific | Categories are business-neutral; practice areas swap for service lines (trades) |

---

## 4. The Taxonomy

| Category | Default to a CRM | Share of names in use | Example |
|---|---|---|---|
| New matter | Send | 27.7% | *New Client Calls, *New Personal Injury Case |
| General | Firm decides | 13.1% | *General Calls |
| Court, attorney or agency | Skip | 10.1% | *Court Calls |
| Insurance or medical provider | Skip | 2.7% | *Insurance Adjuster Calls |
| Existing client | Skip | 0.5% | *Existing Client Calls |
| Emergency | Skip | 0.4% | *Emergency Calls |
| Jail or custody | Firm decides | 0.2% | *Jail Calls |
| Billing | Skip | 0.1% | *Billing/Payment Calls |
| Vendor or solicitation | Skip | <0.1% | |
| System entry | Never a kind of call | 31.0% | - Admin, * No Charge |
| Line handling | Never a kind of call | 4.3% | NO PATCH |
| Named person | Firm decides | 9.8% | A person's name only |

- **Practice areas** (new matters only): personal injury, criminal, family, estate planning, traffic, real estate, immigration, business, bankruptcy, employment, medical malpractice, workers' compensation, disability and others, plus "unspecified".
- **Modifiers**: in custody, after hours, Spanish or other language, named person, patch or no patch, away, web chat. Modifiers never change the category.

**Evidence the rules are right:** api-2's "active" flag is never read by the
rules, so it is an independent check. 99.8% of active types classify as new
matter (2,672 of 2,678), and 98.4% of types classified as new matter are active.
At every one of the 122 active firms with a CRM integration, the active types
include new matters.

The full rule order and reference leads: [call-categories/README.md](../call-categories/README.md).

---

## 5. Proposed Workflow

### 5.1 Show it (no change to intake)

A weekly report per firm from data already held: new clients by area of law,
urgent calls, after-hours share, how fast each reached the firm, and proof of
delivery per destination. Built in the lab as the firm report.

### 5.2 Confirm it

Each firm confirms its mapping once, in the portal, as a business-readable list
("these labels are new clients for personal injury"). New firms choose categories
and practice areas at setup instead of writing labels. A confirmation is an
approved revision of the firm's setup (RFC-005).

### 5.3 Untangle it

| Today in "call type" | Moves to |
|---|---|
| Kinds of calls | Category and practice area, with the firm's label kept |
| System and billing flags | Account and billing settings (RFC-003) |
| Staff names | A staff directory, with routing rules to a person |
| Phone handling | Line-handling settings (patch, no patch, after hours, away) |

### 5.4 Classify at intake

Answering AI sets the category, practice area and urgency from the conversation,
with a **confidence**, alongside the label chosen. Low-confidence calls are
reviewed; firms can correct a category ("that was not a new matter"), and
corrections improve classification.

### 5.5 Drive everything from it

Routing (per destination), the window to act, how the firm is reached, intake
questions, CRM fields and free-call rules all read the category.

A firm's CRM rule becomes a short list it can approve:

```
send: new-matter
practice areas: all | [personal-injury, criminal, ...]
also: jail-custody? general?   (the firm's choice)
chat leads: yes | no
```

The default (CRMs take new matters; email and the portal take everything;
emergencies and custody also by text) reproduces today's behaviour, explicitly.

---

## 6. Technical Approach

- **One source of truth:** `taxonomy.json`, versioned, embedded in the backend and read by the analysis script. A test holds both to the same 40 synthetic reference leads.
- **Every lead carries** category, practice area, modifiers, the firm's label, the source (label mapping or intake classification), the confidence and the taxonomy version.
- **Label mapping stays as the fallback** for unmapped names and channels that cannot classify yet.
- **Corrections are records,** not edits: who corrected, from what, to what, when.
- **Benchmarks are aggregates only,** with minimum group sizes, never caller details, with a per-firm opt-out.

Built in the lab: the taxonomy, the mapping of every current name, the reference
leads, the Call categories page, and the firm report.

---

## 7. Scope and Rollout

| Phase | When ([RFC-007](007_domain_model_and_build_order.md#8-build-order-thirteen-vertical-slices) slice) | Deliver |
|---|---|---|
| **1. Show and confirm** | Slices 4 and 8 | Weekly firm report; mapping confirmation in onboarding; categories chosen at setup for new firms |
| **2. Untangle** | Slice 4 | Flags to billing settings; staff directory; line-handling settings |
| **3. Classify at intake** | Slice 9, with the conversation engine | Category, practice area, urgency and confidence on every lead; review queue; firm corrections |
| **4. Drive** | Slices 4 (routing), 6 (windows), 10 (free-call rules), 12 (benchmarks) | Routing by category (RFC-001), windows to act, free-call rules (RFC-003), Insight benchmarks |

### Out of scope

- Referring calls a firm does not take to other firms (bar rules on referrals; separate review).
- Removing firms' own labels.
- Benchmarks before privacy review.

---

## 8. Success Criteria

| Metric | Proposed target |
|---|---|
| Firms with a confirmed mapping | 90% of active firms within two months of phase 1 |
| New matters with a practice area | From about half today to 95% after phase 3 |
| Call types used for flags, staff names or phone settings | Zero for new firms; falling for existing ones |
| Classification agreement with firm corrections | Measured from phase 3; corrections under 2% of leads |
| Firms that open their weekly report | Measured; used at renewal |

---

## 9. Key Risks and Mitigations

**Classification errors.** A wrong category can send a lead the wrong way or set
the wrong urgency. *Mitigation:* the firm's label kept beside it, a confidence on
every call, review of uncertain calls, firm corrections.

**Firms' own meanings.** *Mitigation:* the firm's routing rule and label stay in charge of its leads.

**Privacy.** *Mitigation:* reports and benchmarks are aggregates; never caller details across firms.

**Staff names in data.** *Mitigation:* the named-person long tail stays out of shared documents and the repository.

---

## 10. Decisions and Approval Request

| # | Decision | Proposed resolution |
|---|---|---|
| D1 | Standard language | Category, practice area and modifiers on every call; the firm's label kept |
| D2 | Who sets it | Answering AI at intake, with a confidence; uncertain calls reviewed; firms correct |
| D3 | Flags, staff names, phone settings | Out of the call-type field, each to its own setting |
| D4 | Routing | By category per destination; the default reproduces today's behaviour |
| D5 | Firm report | Weekly, for every firm |
| D6 | Benchmarks | In the Insight package; aggregate only; opt-out |
| D7 | Other businesses | The core stays business-neutral; a trades pack later |

The reasoning, and what would change each: [answers.md](../lead-delivery/answers.md#call-categories).

### Approval requested

1. The taxonomy as the company standard, owned by Product.
2. Phase 1 (show and confirm) now.
3. Classification at intake as part of the standard lead (RFC-005).

### Specialist sign-off

- **Privacy:** benchmarks and correction records.
- **Legal:** referrals of calls a firm does not take (if ever offered).
- **Onboarding:** the confirmation step.

---

## 11. Next Steps

1. Show five firms their own weekly report, built from data already held.
2. Add mapping confirmation to onboarding.
3. Agree the destination of each untangled job with Billing and Operations.
4. Specify the intake classification output with the voice and chat teams.

---

## Final Recommendation

**Make the kind of call a standard fact, set once at intake, and let everything read it.**

**Guiding principle: free text is what a firm sees; never what a system decides on.**
