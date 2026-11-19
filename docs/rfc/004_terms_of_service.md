# RFC-004: Terms of Service Versions and Acceptance

**Answering AI — Product and Technical Proposal**

**Date:** November 19, 2026
**Status:** Proposed — Awaiting stakeholder approval
**Decision owner:** Legal / Product Leadership
**Reviewers:** Legal counsel, Customer Success, Engineering, Billing Operations, Management
**Related:** RFC-003 (price changes reach firms as a terms version)
**Detail:** [terms strategy](../terms/strategy.md) · [answers](../lead-delivery/answers.md#terms-of-service)

---

## 1. Executive Summary

Every change to the terms of service, even a grammar fix, forced every user at
every firm to accept again before signing in. Firms were locked out and fell back
to text and email. A new promotions clause then waited on a software release,
because publishing it would have locked everyone out again.

**We propose one global terms document with numbered versions, each saying what
kind of change it is and who, if anyone, must accept it; acceptance asked only of
the users a change concerns, with notice before it applies; and every acceptance
kept as a permanent record.**

### Recommended decision

Approve:

1. **Versions with a kind of change**: editorial, pricing or material.
2. **Scoped acceptance**: editorial asks nobody; pricing asks firm admins; material asks every user.
3. **Notice before change**: an effective date, with a notice until then.
4. **One global version**, in the portal; the app and website display it.
5. **An append-only record** of every version and every acceptance.
6. **Promotions as their own terms**, accepted when taken up.

---

## 2. Problem Statement

| Problem | What it caused |
|---|---|
| Any update forced every user to accept again before signing in | A grammar fix locked everyone out; firms pushed back |
| Editorial and real changes treated the same | No way to fix wording without disrupting every firm |
| Everyone asked, whatever the change concerned | Members asked to accept pricing terms they do not pay |
| Terms in three places (app, website, portal), each with its own logic | Inconsistent behaviour, duplicated work |
| A ready promotions clause could not ship without locking everyone out | A content change waited on a software release |
| Counsel's Word document copied by hand | Copy errors, lost formatting |
| Acceptance was a lock, not a record | No answer to "who agreed to what, and when?" |
| Users learned of changes by being blocked | Confusion and support calls |

### Business impact

Lost access for paying firms, support load, delayed commercial changes, and weak
evidence if acceptance is ever disputed.

---

## 3. Product Vision

**Accept once, be asked again only when it matters, and never be surprised.**

### Firm users

- Accept the current terms at first sign-in.
- See a plain summary of every new version in the portal, whether or not it needs acceptance.
- Are asked again only for a change that concerns them, with its effective date shown in advance.

### Firm admins

- Accept pricing and billing terms on the firm's behalf; the box says so.
- Can review a change with their own counsel before it applies.

### Our staff

- Publish a version as content, the same day, without a release.
- See coverage: who has accepted each version that asked for it.
- Answer "who agreed to what, and when" from the record.

---

## 4. Proposed Model

| Part | What it holds |
|---|---|
| **Version** | Number, title, summary of what changed, full text, kind (editorial, pricing, material), who must accept (nobody, admins, all), published at, takes effect at, source |
| **Acceptance** | Version, firm, user, role, time; append-only |
| **Gate at sign-in** | *Block*: first sign-in, or a version that asks this user and has taken effect. *Notice*: a version that asks this user but takes effect later (with the date), or one that asks nobody. *Nothing*: all accepted |
| **Coverage** | For each version that asks: how many of those it asked have accepted |

| Kind of change | Who is asked by default | Before the effective date | From the effective date |
|---|---|---|---|
| Editorial | Nobody | Notice | Notice |
| Pricing | Firm admins | Notice with the date | Admins accept before continuing |
| Material | Every user | Notice with the date | Each user accepts before continuing |

The publisher may override who is asked; the choice is recorded with the version.

### How law firms accept

- **Clear consent:** the full text shown, an unticked box ("I have read and accept these terms on behalf of my firm" for admins), a button that says what it does. No acceptance by continuing to browse.
- **The admin accepts for the firm** on commercial terms; each user accepts the terms of their own use.
- **Records on request:** a firm, or our counsel, can see which version each user accepted and when.

---

## 5. Publishing Workflow

1. Counsel edits the Word document; the operator imports it and previews it.
2. The operator writes the plain summary and picks the kind of change; who must accept follows from the kind and can be overridden.
3. For pricing or material changes, an effective date gives firms time (for example the first of next month, with the price-book version that starts then, RFC-003).
4. Publish. Users see the notice at their next sign-in; coverage shows who has accepted; on the effective date, those who have not are asked before continuing.

Promotions, pilots and discounts carry their own short terms, accepted when the
offer is taken up, with an end date; the main terms keep one general clause.

---

## 6. Technical Approach

- **The portal is the source:** one global version; the app and website display it as static text.
- **Versions and acceptances are append-only;** a published version's text never changes; a correction is a new editorial version.
- **The gate is a pure function** of (user, role, versions, the user's acceptances, now), so every case is testable.
- **Notifications in the portal at sign-in;** email may follow for pricing changes as a courtesy, never as the record.

Built in the lab: versions with kinds and scopes; the gate for admin, member and
new user; coverage; publishing with a preview of exactly what users will see;
the acceptance record. The lab's history replays the real one: the first terms,
a grammar fix, the promotions clause, and a price change with an effective date.

---

## 7. Scope and Rollout

| Phase | When | Deliver |
|---|---|---|
| **1. Unblock** | Weeks 1–2 | Scoped acceptance and notice in the portal; the promotions clause published as a version for firm admins |
| **2. Record** | Weeks 3–8 | The append-only acceptance record; coverage; Word import with preview |
| **3. One place** | Months 3–5 | App and website display the portal's version; promotions as their own terms |

### Out of scope

- E-signature workflows for negotiated enterprise contracts (handled outside the portal).
- Email as the record of acceptance.

---

## 8. Success Criteria

| Metric | Proposed target |
|---|---|
| Users locked out by an editorial change | Zero |
| Time from counsel's final text to publication | Same day |
| Acceptance coverage of a pricing version by its effective date | 95% of firm admins |
| Support contacts about terms | Below the baseline from the last forced re-acceptance |
| Acceptance questions answered from the record | 100% |

---

## 9. Key Risks and Mitigations

**An admin's acceptance does not bind the firm.** *Mitigation:* counsel confirms the wording of the admin box before phase 1.

**A change marked editorial that is material.** *Mitigation:* the kind is chosen with counsel; overrides recorded; a second reviewer for anything not editorial.

**Firms ignore notices until blocked.** *Mitigation:* effective dates with lead time; coverage lets Customer Success reach admins who have not accepted.

---

## 10. Decisions and Approval Request

| # | Decision | Proposed resolution |
|---|---|---|
| D1 | Re-acceptance on every change | No: only versions marked to ask, only the users they concern |
| D2 | Who accepts what | Editorial: nobody. Pricing: firm admins. Material: every user. Overridable and recorded |
| D3 | When the portal waits | From the version's effective date; a notice before it |
| D4 | Promotions | Their own terms, accepted when taken up, with an end date |
| D5 | Where the terms live | One global version in the portal |
| D6 | Notifications | In the portal at sign-in; email as courtesy only |
| D7 | The record | Every version in full and every acceptance, append-only |

The reasoning, and what would change each: [answers.md](../lead-delivery/answers.md#terms-of-service).

### Approval requested

1. Scoped acceptance, shipped in phase 1 so the promotions clause can go out.
2. The record and the single global version.

### Specialist sign-off

- **Legal counsel:** admin acceptance wording; the classification of the promotions clause.
- **Customer Success:** how firms are told about the first scoped version.

---

## 11. Next Steps

1. Counsel confirms the admin acceptance wording.
2. Ship scoped acceptance and publish the promotions clause for firm admins.
3. Backfill existing acceptances as the first records, marked as imported.

---

## Final Recommendation

**Treat the terms as versioned content with a permanent record, and ask only the people a change concerns.**

**Guiding principle: never lock a firm out over a change that does not concern it.**
