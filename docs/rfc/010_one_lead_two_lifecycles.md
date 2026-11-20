# RFC-010: One Lead, Two Lifecycles

**Answering AI — Product and Technical Proposal**

**Date:** November 20, 2026
**Status:** Proposed — Awaiting stakeholder approval
**Decision owner:** Product / Engineering
**Reviewers:** Operations, Onboarding, Engineering, the voice platform team
**Related:** RFC-001 (the window to act, the fallback) · RFC-002 (classification at intake) · RFC-005 (one standard lead) · RFC-007 (§5 the Intake and Delivery contexts) · RFC-008 (§3 evidence levels, §3.2 escalation) · RFC-009 (routing by category)
**Detail:** the intake policy lab (`../intake-policy-lab`): [lead-contract.md](../../../intake-policy-lab/docs/lead-contract.md) and its [schema](../../../intake-policy-lab/docs/lead-contract.schema.json), [lead-compatibility.md](../../../intake-policy-lab/docs/lead-compatibility.md) · scenario [follow-up-without-delivery](../contracts/scenarios/follow-up-without-delivery.json)

---

## 1. Executive Summary

A lead today is a name, a phone, an email, a **call-type name** and a message.
Delivery works out what the call was by matching that name against the taxonomy.
The intake policy lab checked six realistic firms, built from the platform's own
call-type templates, against the taxonomy:

- **33 of 41 call types** get the category intake would give them.
- **"Sales / marketing"**, the platform's always-on template, was read as a person's name at every firm, so every sales call went to staff-routing review. *Fixed in the taxonomy since; agreement is now 39 of 41.*
- **"Emergency repair"** becomes the *emergency* category, which never goes to a CRM.
- What a name **cannot** carry is what matters most. Is the matter one the firm takes? Did the caller say someone is in custody, or that it is an emergency? Was the call put through, and was the target in its hours? Intake knows each of these as a fact; delivery can only guess.

There is a second gap. "**Acknowledged**" means *the firm received the lead* here, and
*someone took responsibility for calling back* in intake. The callback itself, the
thing the caller is waiting for, has an owner in none of the three channels.

**We propose** that the lead becomes the contract between Intake and Delivery
(`intake-lead/draft-1`), and that the product runs **two lifecycles on one lead ID**:
delivery, which exists today, and **follow-up**, which is new. Each has its own
events and its own escalation, and neither waits for the other.

### Recommended decision

Approve the lead contract as the direction for RFC-005's standard lead. Approve the
follow-up lifecycle and its events as part of the Intake context. Keep
**acknowledged** with its meaning here, and name the follow-up's step **responsibility
accepted**.

## 2. Problem Statement

### 2.1 Delivery re-derives what intake already knew

| What the call was | What delivery sees today | Consequence |
|---|---|---|
| A matter the firm declines (workers' comp at an injury firm) | "Workers' compensation": a new matter | A declined caller becomes a CRM prospect |
| A criminal call where the caller said "yes, he's in custody" | "Criminal": a new matter | 72 hours and a 1-hour fallback, not 24 hours and 15 minutes |
| A lunch-hour message because the intake team was out | Nothing, unless a label says "after hours" | Patch, no-patch and after-hours are guessed from words |
| A sales call | "Sales / marketing": a person's name | Staff-routing review on every one |

[answers.md](../lead-delivery/answers.md#call-categories) already decides that
Answering AI sets the category at intake, from the conversation. This RFC says what
else intake must hand over so delivery never has to parse a name.

### 2.2 Two meanings of "acknowledged", and an obligation nobody holds

Delivery's *acknowledged* is receipt: a person at the firm pressed the button or
replied to the text (RFC-008 §3.1). Intake's was responsibility: the person who owns
the callback accepted it. Merging them would let a broken CRM, or an unanswered text,
stop a callback from ever being owed. The callback is the obligation the caller cares
about, and today it has no owner, no deadline and no escalation anywhere.

## 3. Proposal

### 3.1 The lead contract

The full field list is in [lead-contract.md](../../../intake-policy-lab/docs/lead-contract.md).
**Intake says what happened; delivery decides what to do about it.** Beyond today's
lead, intake sends:

- the policy revision the call ran on;
- the category, practice area and their source, set at intake;
- whether the firm **takes** the matter;
- every applicable question: answered (with its source), declined or not asked;
- the outcome the policy **required** and the one that **happened**;
- whether a transfer target was **available** by the firm's own schedule;
- **urgency signals with their source** (for example, *in-custody, the caller's answer*);
- whether a **follow-up is owed**, with the firm's rules or "none agreed";
- evidence references.

Intake **never names destinations** and **never sets delivery's clocks**. Routing
reads the category, practice area and `taken`, never the name. The window to act,
the fallback and delivery escalation are applied by delivery, to intake's signals.

On the voice side, every new field is additive to contract v1. The one field that
goes away in time is `destination_ids`, because routing is delivery's.

### 3.2 Two lifecycles, one lead ID

```
Delivery   LeadRecorded → attempted → accepted / verified / unknown → HandOffAcknowledged
Follow-up  FollowUpOwed → FollowUpAssigned → ResponsibilityAccepted → CallbackCompleted → FollowUpResolved
```

| Shared name | Event | Context | Status |
|---|---|---|---|
| `delivery_received` | `HandOffAcknowledged` | Delivery | exists |
| delivery escalation | `HandOffEscalated` | Delivery | exists |
| — | `FollowUpOwed` | Intake | **proposed** |
| — | `FollowUpAssigned` | Intake | **proposed** |
| `responsibility_accepted` | `ResponsibilityAccepted` | Intake | **proposed** |
| `callback_completed` | `CallbackCompleted` (reached or not) | Intake | **proposed** |
| `followup_resolved` | `FollowUpResolved` | Intake | **proposed** |
| follow-up escalation | `FollowUpEscalated` | Intake | **proposed** |

The follow-up's owner, deadlines and escalation contact are **the firm's**, from its
approved intake policy. Delivery's windows are **Answering AI's**
([answers](../lead-delivery/answers.md#speed-window-to-act-fallback-holding)).
They answer different questions and never stand in for each other. At the injury
firm in the comparison, the callback is due within 60 minutes, before delivery's
1-hour fallback has started.

### 3.3 Invariants

1. **A follow-up never waits for delivery.** It is owed from the moment the conversation ends with one owed, even if every destination is down.
2. **Delivery never needs the follow-up.** A lead is delivered, held, fallen back and escalated exactly as today.
3. **Each lifecycle escalates under its own name.** One lead can raise both escalations; an operator sees two different failures.
4. **Acknowledged means received.** Taking responsibility is *accepted*, and is never shown as acknowledged.
5. **Routing never parses the call-type name.** The name is shown to people and kept for the record.
6. **A firm with no follow-up rules is visible**, not silent: the lead says "owed, nobody responsible".

## 4. Rollout

1. **Now:** the scenario [follow-up-without-delivery](../contracts/scenarios/follow-up-without-delivery.json) is added as `proposed`. It fails against the lab until the follow-up lifecycle exists. The glossary marks the new terms *Proposed*.
2. **Taxonomy:** "Sales / marketing" is **done** (the vendor pattern, with reference lead `vendor-template`; no imported name changed category). Still to do in slice 4, with RFC-009: treat emergency as a fact about a call rather than a kind of call.
3. **Contract:** the voice platform adds the contract's fields to its v1 request (additive). The lab's lead step gains `taken`, `urgency` and `followUp`. Routing moves from the name to the category, as RFC-009 already plans.
4. **Follow-up lifecycle:** built in the Intake context. Its owner, deadlines and escalation come from each firm's approved intake policy. Operators see both escalations in one queue, labelled.

### Out of scope

How intake decides the call type and checks the call (the intake policy lab); staff
assignment (RFC-009 §3); changing any delivery window.

## 5. Success Criteria

- `follow-up-without-delivery` passes and is promoted to `lab`.
- No routing decision reads a call-type name (RFC-009's routing decisions record the category).
- Every lead from a firm without follow-up rules is flagged "nobody responsible" in the weekly report.
- The six realistic firms' compatibility report shows 41 of 41 call types agreeing (39 today), and every urgency signal arriving with its source.

## 6. Key Risks and Mitigations

| Risk | Mitigation |
|---|---|
| Two escalations for one lead feel like noise | One queue, two labels; the follow-up's deadlines are the firm's own choice |
| Firms without follow-up rules see a new red flag | Onboarding agrees rules with the firm; until then it is reported, not alarmed |
| The voice platform cannot send every field at once | Every field is optional in the additive phase; routing falls back to today's name match, recorded as such in the routing decision |
| "Accepted" is confused with a delivery *accepted* by a destination | The follow-up's event is `ResponsibilityAccepted`, never bare "accepted", in shared views |

## 7. Decisions and Approval Request

| # | Decision | Recommendation |
|---|---|---|
| 1 | The lead contract as the direction for RFC-005's standard lead | Approve |
| 2 | The follow-up lifecycle, its events and its escalation, in the Intake context | Approve |
| 3 | *Acknowledged* keeps its meaning (received); intake's step is *responsibility accepted* | Approve |
| 4 | Routing never reads the call-type name once the contract's category arrives | Approve, with RFC-009 |
| 5 | Declined matters (`taken: false`) skip CRMs by default and still reach the firm's notifications | Approve, to confirm with Onboarding |

## Final Recommendation

Approve the contract and the follow-up lifecycle together. The contract stops delivery
guessing what a call was. The follow-up lifecycle gives the callback the caller is
waiting for an owner, a deadline and an escalation, without making it depend on
whether a CRM is up.
