# RFC-005: One Standard Lead and the Integration Platform

**Answering AI — Product and Technical Proposal**

**Date:** November 19, 2026
**Status:** Proposed — Awaiting stakeholder approval
**Decision owner:** Product / Engineering Leadership
**Reviewers:** Engineering, Onboarding, Operations, Customer Success, Security, Management
**Related:** RFC-001 (delivery in time) · RFC-002 (categories on every lead) · RFC-003 (the usage record reads the lead)
**Detail:** [strategy: direction and roadmap](../lead-delivery/strategy.md#4-direction-a-platform-for-leads) · [domain model](../lead-delivery/README.md) · [answers](../lead-delivery/answers.md#routing-and-integrations)

---

## 1. Executive Summary

Answering AI's lead systems grew one at a time: phone intake, chat, api-2, the
client portals. Each has its own lead shape, keys and delivery code. There are 9
hand-written CRM handlers, every firm's setup is a bespoke configuration made by
staff, and changes to what a firm receives have no approval history.

**We propose one standard lead for every channel, one delivery service every
channel uses, a short list of deeply supported integrations with everything else
through Zapier and webhooks on a published contract, and a setup and change
workflow in which the firm approves every change to what it receives.**

### Recommended decision

Approve:

1. **One standard lead**, versioned, produced the same way by phone and chat, with its call category set at intake.
2. **Six native integrations** (Clio Grow, Lawmatics, MyCase, Lead Docket, PracticePanther, Smokeball); Lawruler and the legacy Lawmatics forms retired; everything else through Zapier and webhooks.
3. **A published, versioned contract**: the lead schema and signed webhooks with retries and resend.
4. **Setup and change by revision and approval**: operators prepare, firms approve, nothing live changes until then.
5. **Integrations verified continuously**: CRM sandboxes, scheduled test leads, and confirming the record arrived where a CRM allows it.
6. **Gradual replacement**, never a rewrite.

---

## 2. Problem Statement

| Gap | What firms experience | Evidence |
|---|---|---|
| **Several lead shapes** | The result depends on which system the lead went through | Phone, chat, api-2 and the portals each have their own lead, keys and delivery code; 18 chat firms share 4 keys |
| **Integrations wide but shallow** | An integration may be offered but not work | 9 hand-written handlers; 8 of 444 CRM rows enabled in the copy; Lawruler's handler off; Clio 0 of 71 accepted |
| **Setup and changes through people** | Every change is a support request | 4,390 free-text call-type names; destinations configured by staff |
| **No approval history** | Live behaviour can change without the firm agreeing | No revisions or approvals in production |
| **Untestable against real CRMs** | Problems found by firms | No sandbox accounts; fake receivers prove shape, not arrival |
| **Legal-only in places** | Trades businesses get a model built for law | Heating, plumbing and investigation firms are customers |

### Business impact

Every new channel, CRM or firm adds bespoke work, so support grows with the
customer base. The platform cannot be offered to other software, or through CRM
marketplaces, without one contract.

---

## 3. Product Vision

**Build integrations once, configure them for each firm, approve every change, and operate by exception.**

The closest model is a payments platform: hard infrastructure made invisible and
trustworthy, with one object model, a versioned API, signed webhooks with retries
and replay, a test mode apart from live, and a record of every event. Then,
distribution: reaching firms inside the CRMs they already use.

**Trust → self-serve → platform → distribution:** proof of delivery earns the
trust self-serve needs; self-serve forces the single model the platform needs;
the platform makes marketplace distribution cheap.

### Operators

- Set up a destination from a standard recipe, without code.
- Prepare changes as draft revisions; test; activate once approved.

### Firms

- See which systems receive their leads and what information each gets.
- Approve setups and changes in business words, not raw data.
- Connect and reconnect their own CRM; never hand a password to our staff.
- Later: set up and change destinations themselves, with the same approval.

### Other software

- Receive Answering AI leads through a published contract, or through Zapier.

---

## 4. The Standard Lead

| Part | Holds |
|---|---|
| **Identity** | Lead id (stable across resends), firm, channel (call or chat), times, test or production |
| **Caller** | What the caller told us: name, phone, email, and their answers to intake questions |
| **Meaning** | Call category, practice area, modifiers, the firm's own label, confidence, taxonomy version (RFC-002) |
| **Urgency** | Window to act, from the category (RFC-001) |
| **Content** | Summary, transcript link, recording link where the firm has them |

- **Versioned:** additive changes keep the version; breaking changes publish a new one, and both run during migration.
- **Business-neutral core:** practice areas (law) or service lines (trades) sit on top as a vertical pack.
- **Minimal:** only what the caller told us; nothing about the caller from elsewhere.

---

## 5. Integrations

| Path | For | How |
|---|---|---|
| **Native** | Clio Grow, Lawmatics (API), MyCase, Lead Docket, PracticePanther, Smokeball | Described as catalog and mapping data, not code per CRM; certified with each vendor in time |
| **Zapier** | The long tail of tools | A published Answering AI app on the contract |
| **Webhook** | Firms and partners with their own systems | Signed, retried, resendable |
| **Email, text, portal** | Every firm | Notifications and fallback (RFC-001) |
| **Retired** | Lawruler (handler already off), legacy Lawmatics forms (none enabled) | Firms moved to a supported path first |

The list is confirmed against production usage in the first two weeks (RFC-001
phase 1): a retired kind in real use, or a kept one unused, changes it.

### Verification

- **A sandbox account with each native CRM,** and contract tests against it on every change.
- **Scheduled test leads** through every live connection, so a stopped connection is caught by us within minutes.
- **Delivery stages reported honestly:** accepted by the receiver; processed by the intermediary where visible; **verified in the CRM** only when an API or the firm confirms it.

---

## 6. Setup and Change Workflow

### 6.1 Setting up a destination

| State | Means | Next |
|---|---|---|
| **Draft** | The operator picks the firm, destination kind and standard recipe; the system fills recommended fields and flags anything missing | Request the connection |
| **Connection needed** | The firm connects its account through the supported sign-in; staff never handle passwords | Send for review |
| **Awaiting approval** | The firm sees a business summary: which calls go there, what information is shared, whether existing records may be updated, which notifications stay on | Approve, or request changes (back to draft with comments) |
| **Ready to test** | Approved | A representative test lead is sent |
| **Test accepted** | The receiver accepted it | The firm confirms the record is in its system |
| **Live** | Receives production leads, monitored | Changes go through 6.2 |

Until live, a destination receives test leads only, and no totals count them.
Every issue found in setup (a missing key, a missing staff id, a key shared with
another firm, a missing email recipient) must be cleared before go-live.

### 6.2 Changing what a destination receives

**Request → Draft revision → Review → Approval → Test → Activate**

1. The firm asks in plain words ("send family-law inquiries to our family-law intake"), or the operator proposes a change.
2. The operator copies the live configuration into a draft revision and changes it. **The live configuration does not change.**
3. The firm sees what changes, side by side, in business words:

   | Setting | Current | Proposed |
   |---|---|---|
   | Kinds of call | All new matters | Family law only |
   | Destination | General intake | Family law intake |
   | Caller details | Name, phone, summary | Unchanged |

4. The firm approves that exact revision, or asks for another. Changing a revision after approval voids the approval.
5. The revision is tested, then activated. The previous revision stays for audit and rollback. If the test fails, the live configuration stays.

### 6.3 What needs the firm's approval

| Change | Firm approval |
|---|---|
| Destination, kinds of call routed, shared caller fields, creating or updating CRM records | **Required** |
| Fallback destination (RFC-001) | **Required** |
| Retry timing; repairing a format without changing meaning | Not required; logged with before and after |
| Resending a failed lead | Not required; audited |
| Pausing a failing or compromised destination | Done at once; the firm told; **never silently rerouted** |
| Reconnecting an expired sign-in with unchanged behaviour | The firm signs in; no new approval |
| Rolling back | Only to a revision still approved and still authorised |

Every approval records the revision, the firm, the approver and their role, the
time, the exact configuration approved, the test result, and who activated it.
These records are append-only.

---

## 7. Technical Approach

**Replace gradually, never rewrite.**

1. **Agree the contract first:** the lead schema and delivery states.
2. **Add the delivery worker and visibility inside api-2** (RFC-001).
3. **Move channels one at a time** onto the standard lead and delivery service, behind feature flags: phone first, then chat (giving each chat firm its own key).
4. **Retire each old path** once its traffic has moved.
5. **Prove every step in the lab first** (RFC-006).

### Security and privacy

- **Credentials are never stored in readable form or shown;** staff see only that a credential is present and its fingerprint.
- Firm-scoped keys for the contract and Zapier; the internal intake key kept separate.
- Signed webhooks; destination addresses validated (no internal addresses).
- Role-based access and tenant isolation; every operator action audited.
- Caller data minimised in logs and samples; retention periods defined before production.
- In time, a recognised security certification (for example SOC 2).

---

## 8. Scope and Rollout

| Phase | When | Deliver |
|---|---|---|
| **1. Contract** | Months 1–2 | The lead schema v1; the integration list confirmed from production; sandbox accounts for the six CRMs |
| **2. Setup and change** | Months 2–4 | Setup states, revisions and approvals; the firm's review screen; recipes for the six CRMs, email, text and webhook |
| **3. One lead** | Months 3–5 | Phone and chat produce the standard lead with its category; chat keys per firm; usage record for billing (RFC-003) |
| **4. Self-serve** | After | Firms connect and change destinations themselves, with the same approval; the published Zapier app |
| **5. Distribution** | Later | Listings in CRM marketplaces; a trades pack |

### Out of scope

- A general-purpose workflow automation builder.
- A native integration for every CRM.
- Editing mappings inside a firm's own Zapier zaps.
- Guaranteed visibility inside every CRM.

---

## 9. Success Criteria

| Metric | Proposed target |
|---|---|
| Routine setups needing engineering | Zero for supported recipes |
| Setups completed without escalation | 90% |
| Business changes with a recorded firm approval | 100% |
| Channels on the standard lead | Phone and chat by month 5 |
| Time from signup to first delivered lead | Measured from phase 1; falling |
| Share of setups done self-serve | Measured from phase 4 |
| Stopped connections found by scheduled tests before a firm reports them | 100% |

---

## 10. Key Risks and Mitigations

**Scope grows into a general integration platform.** *Mitigation:* the contract, six native kinds, and Zapier for the rest.

**Approval slows support.** *Mitigation:* risk-based approval: repairs and resends are logged, not approved.

**False confidence in delivery.** *Mitigation:* stages reported as observed; "verified in the CRM" only when confirmed.

**Migration disrupts live integrations.** *Mitigation:* additive changes, feature flags, both lead versions during migration, existing integrations kept working.

**Drift outside our control** (a firm edits its own zap). *Mitigation:* externally managed settings shown as unverified; approval covers what we send.

---

## 11. Decisions and Approval Request

| # | Decision | Proposed resolution |
|---|---|---|
| D1 | The lead | One standard, versioned lead for every channel, with its category set at intake |
| D2 | Native integrations | Clio Grow, Lawmatics (API), MyCase, Lead Docket, PracticePanther, Smokeball; retire Lawruler and legacy Lawmatics forms |
| D3 | The long tail | Zapier and signed webhooks on a published contract |
| D4 | Setup | Operator-prepared from recipes; the firm connects, approves and confirms; test before live |
| D5 | Changes | Revisions with the firm's approval for business changes; repairs logged |
| D6 | Architecture | Replace gradually inside existing systems; no rewrite |
| D7 | Verification | CRM sandboxes, scheduled test leads, honest delivery stages |
| D8 | Other businesses | A business-neutral core with vertical packs |

The reasoning, and what would change each: [answers.md](../lead-delivery/answers.md#routing-and-integrations).

### Approval requested

1. The standard lead and the published contract.
2. The integration list, confirmed against production in two weeks.
3. The setup and change workflow and approval policy.

### Specialist sign-off

- **Security:** credentials, keys, address validation, certification plan.
- **Privacy:** fields in the lead; retention.
- **Onboarding and Operations:** recipes, permissions, escalation.
- **Engineering:** schema versioning and migration plan.

---

## 12. Next Steps

1. Publish the lead schema v1 for review.
2. Open sandbox accounts with the six CRMs.
3. Write recipes for the six CRMs, email, text and webhook.
4. Validate the firm's review screen with five onboarding cases.

---

## Final Recommendation

**One lead, one contract, a few integrations done properly, and no change to what a firm receives without its approval.**

**Guiding principle: build once, configure many times, approve changes explicitly, and operate by exception.**
