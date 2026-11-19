# RFC-006: The Integration Lab: Prove Every Change Before Production

**Answering AI — Product and Technical Proposal**

**Date:** November 19, 2026
**Status:** Proposed — Awaiting stakeholder approval
**Decision owner:** Engineering Leadership
**Reviewers:** Engineering, Operations, Product, Security, Management
**Related:** RFC-001 to RFC-005 are each proven here first
**Detail:** [lessons](../lead-delivery/lessons.md) · [code map](../lead-delivery/code-map.md) · [README](../../README.md)

---

## 1. Executive Summary

Every proposal in RFC-001 to RFC-005 was built and run in a lab before it was
written down: delivery with retries, the window to act and fallback, failure
classes, the operator console, call categories, the firm report, billing with
side-by-side bills, and terms of service versions. The screens shown to
leadership are screenshots of it working.

Building it also showed what the lab cannot yet do: it runs on a development copy
of mostly test traffic, its CRMs are fakes, and its demo data took as much work
as the features.

**We propose to keep the lab as the place every change to lead delivery, billing
and terms is proven before it reaches production, and to give it what it lacks:
an anonymised feed of production data, real CRM sandboxes, a library of named
scenarios, and automatic checks on every change.**

### Recommended decision

Approve:

1. **The lab as the required proving ground** for changes to delivery, categories, billing and terms.
2. **A read-only, anonymised production feed** into the lab.
3. **Scenarios as a product:** named, repeatable, fictional by default, with a reset.
4. **Automatic checks** on every change: tests, contract tests against CRM sandboxes (RFC-005), and screenshot capture.
5. **One command** to run the lab.

---

## 2. What the Lab Is

| Part | What it does |
|---|---|
| **Importers** | Read firms, destinations and call types from api-2 (read-only), the chat system and the client portal. Every secret is reduced to "present" and a fingerprint the moment it is read |
| **Delivery engine** | The proposed model: record first, one delivery per destination, retries, failure classes, switch-off, resend, window to act and fallback (RFC-001) |
| **Fake receivers** | One per CRM kind, checking what that CRM requires, with modes: accepts, refuses, flaky, gone, slow, down. Imported connections only ever deliver here |
| **Simulator** | A deterministic week of leads through the same rules as the engine, with scenarios for every operator situation: an outage that recovers, a dead endpoint switched off, an expired sign-in, a mapping gap fixed and resent, a paused destination, routing decisions, an outage with and without a fallback |
| **Screens** | Delivery Center, Destinations, Leads, delivery graph, firm report, call categories, onboarding, firm portal (statement, plan change, terms), billing operations, terms admin, and the vision page |
| **Billing and terms** | Price book, subscriptions, statements, locked cycles, shadow comparison, controls; terms versions, the sign-in gate, coverage |
| **Demo names** | Replaces every real firm name with a stable fictional one, keeping trades businesses as trades |
| **Deck capture** | Regenerates the leadership screenshots from the running lab |
| **Tests** | 120 tests: classification, retries, every payload against its receiver, every reference lead's category, every failure class in a simulated week, destination health, windows and fallback, billing and the terms gate |

Rules that hold in the lab and must hold in production:

- **Credentials are never stored or shown.**
- **No caller information:** leads are synthetic; imports read call types for labels only.
- **The production import is read-only** at the database session level.
- **Real firm names never leave the machine;** decks use fictional names.

---

## 3. Problem Statement: What Building It Taught Us

| Lesson | Consequence |
|---|---|
| **We could not see reality** | No production outcome was ever seen; durations, timestamps, firm response times and prices were invented. Every figure is a signal, not a fact |
| **Fake CRMs prove shape, not arrival** | A lead accepted by a fake receiver may still fail in the real CRM |
| **Features were invisible until a scenario existed** | The simulator became a second implementation of the rules, kept honest only by sharing the engine's functions |
| **Repeatability was hard** | Reruns had to reset append-only tables, in the right order; random data made a test flaky; capturing screenshots changed the data |
| **Time runs at two speeds** | Retries run in compressed lab time; windows to act in real minutes |
| **Real names leaked everywhere** | Firm names, staff names inside call types, contacts in pasted threads; a tool had to be built to remove them |
| **Words drifted** | Code says connection, replay, skipped; the business says destination, resend, not sent |
| **The build fought itself** | The running server locks its own files; restarting caused port conflicts |
| **It runs in memory** | Filters work at 600 leads, not at production volume |

The full account: [lessons.md](../lead-delivery/lessons.md).

---

## 4. Proposal

### 4.1 Required proving ground

A change to delivery rules, categories, billing rating or the terms gate is
merged to production only when:

1. its rule is in the lab and covered by a test;
2. the simulated week still reaches every scenario;
3. the affected screens are regenerated and reviewed.

### 4.2 Anonymised production feed

- **Read-only**, from production replicas, on a schedule.
- **Aggregates and shapes only:** delivery outcomes, timings, status codes, call-category mix, billing account fields; caller details never leave production.
- **Firm names replaced** by stable fictional names at the source.
- **Approved by Security and Privacy** before the first run.

This replaces invented figures with measured ones, and lets the simulator replay
real patterns (outage lengths, failure mixes, call timing).

### 4.3 Scenarios as a product

- A library of **named scenarios**, each with its story, the screen it shows and the test that checks it.
- **Fictional names by default;** real names only behind an explicit flag on a developer's machine.
- **A reset** that restores a known state in seconds; screenshot runs leave no trace.
- **One clock,** so retries and windows to act can both be compressed together.

### 4.4 Automatic checks

| Check | On |
|---|---|
| Unit and rule tests (including F# and Python category rules agreeing) | Every change |
| Contract tests against the six CRM sandboxes (RFC-005) | Every change to payloads; nightly |
| Simulated week reaches every scenario | Every change |
| Screenshot capture and visual review of the leadership deck | Every change to a screen in it |
| Plain-language check of screen text against the glossary | Every change to screen text |

### 4.5 One command

One command starts the backend, frontend and worker on free ports, rebuilds
without a restart, and opens the lab.

### 4.6 One vocabulary

Rename the code to the glossary's words (destination, resend, not sent) as each
area is next touched, so code, screens and documents agree.

---

## 5. Scope and Rollout

| Phase | When | Deliver |
|---|---|---|
| **1. Hygiene** | Weeks 1–2 | One command; reset; screenshot runs without side effects; checks on every change |
| **2. Real data** | Weeks 3–8 | The anonymised production feed; figures in the deck from it |
| **3. Real CRMs** | Months 2–4, with RFC-005 | Contract tests against the six sandboxes |
| **4. Scale** | Months 3–5 | Queries and indexes sized for production volume; the scenario library complete |

### Out of scope

- Running the lab against real firm CRMs or real credentials.
- Caller details in the lab, ever.
- The lab as a production system.

---

## 6. Success Criteria

| Metric | Proposed target |
|---|---|
| Delivery, billing and terms changes proven in the lab before production | 100% |
| Figures in the leadership deck from measured data | 100% after phase 2 |
| Time to reset the lab to a known state | Under a minute |
| Production incidents in an area the lab covers that the lab would have caught | Zero, reviewed after each incident |
| Time from clone to a running lab | Under ten minutes |

---

## 7. Key Risks and Mitigations

**Production data exposure.** *Mitigation:* read-only, aggregates and shapes only, names replaced at source, approved by Security and Privacy.

**The lab drifts from production.** *Mitigation:* contract tests, the production feed, and the rule that changes are proven here first.

**Maintenance cost.** *Mitigation:* the lab is the test harness for every RFC, not a separate product; scenarios replace manual QA.

---

## 8. Decisions and Approval Request

| # | Decision | Proposed resolution |
|---|---|---|
| D1 | Role of the lab | Required proving ground for delivery, categories, billing and terms |
| D2 | Production data | Read-only, anonymised feed of aggregates and shapes; no caller details |
| D3 | Demo data | Named scenarios, fictional by default, with a reset |
| D4 | Checks | Tests, CRM contract tests, scenario coverage and screenshots on every change |
| D5 | Vocabulary | The glossary's words in code, screens and documents |

### Approval requested

1. The lab as the proving ground.
2. The anonymised production feed, subject to Security and Privacy approval.
3. Phase 1 now.

### Specialist sign-off

- **Security and Privacy:** the production feed.
- **Engineering:** checks on every change; the one-command setup.

---

## 9. Next Steps

1. Ship one command, reset, and checks on every change.
2. Specify the production feed's fields for Security and Privacy review.
3. Turn the simulator's scenario list into the documented library.

---

## Final Recommendation

**Keep proving every change in the lab, and feed the lab reality.**

**Guiding principle: nothing reaches a firm that has not run here first.**
