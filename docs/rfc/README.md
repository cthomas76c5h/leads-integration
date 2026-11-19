# RFCs

The proposals for approval. Each states the problem, the proposal, the rollout,
how success is measured, the risks, and the decisions it asks stakeholders to
approve. The depth behind each lives in the docs it links to; the decisions,
with why and what would change them, live in
[answers.md](../lead-delivery/answers.md).

| RFC | Title | Decision owner | Asks for |
|---|---|---|---|
| [001](001_lead_delivery_in_time.md) | Every Lead to the Firm, in Time | Product / Engineering | Measurement now; windows to act, fallback, held deliveries, failure classes; a lead delivery owner |
| [002](002_call_categories.md) | Call Categories as the Standard Language of a Call | Product | The taxonomy as company standard; classification at intake; the weekly firm report |
| [003](003_billing_on_value.md) | Billing on Value | Finance / Product | Three packages per handled conversation; one usage record; shadow billing, then switch |
| [004](004_terms_of_service.md) | Terms of Service Versions and Acceptance | Legal / Product | Scoped acceptance with notice; one version; a permanent record |
| [005](005_standard_lead_and_integrations.md) | One Standard Lead and the Integration Platform | Product / Engineering | One lead and contract; six native integrations; setup and change by approval |
| [006](006_integration_lab.md) | The Integration Lab | Engineering | The lab as proving ground; an anonymised production feed; automatic checks |
| [007](007_domain_model_and_build_order.md) | The Domain Model and the Order to Build It | Engineering / Product | Nine bounded contexts; the hand-off as an aggregate; classification at intake; billing from conversations; thirteen slices and one method per feature |

## How they fit together

```mermaid
flowchart LR
  L["006 Lab<br/>proves every change"] -.-> D & C & B & T & P
  C["002 Call categories"] --> D["001 Delivery in time"]
  C --> B["003 Billing"]
  P["005 Standard lead<br/>and integrations"] --> D
  P --> B
  B --> T["004 Terms"]
```

- **Call categories** (002) set each lead's window to act and routing (001), and billing's free-call rules (003).
- **The standard lead** (005) carries the category and hand-off that delivery (001) and billing (003) read; its setup and change workflow approves fallbacks (001) and category routing (002).
- **Price changes** (003) reach firms as a terms version that firm admins accept (004).
- **The lab** (006) is where each is built and proven before production.
- **The domain model** (007) is the shape all six share: the contexts each belongs to, and the order their features depend on each other.

## Order

| When | Start |
|---|---|
| Weeks 1–2 | 001 phase 1 (measure), 003 phase 1 (audit), 004 phase 1 (unblock), 006 phase 1 (hygiene), 007 phase 1 (domain types) |
| Weeks 3–8 | 001 reliability, 002 show and confirm, 003 clean, 004 record, 006 real data |
| Months 2–5 | 005 contract, setup and one lead; 002 classify at intake; 003 shadow billing |
| After | 005 self-serve and distribution; 003 switch; 002 benchmarks |

The leadership version of all six is [vision-meeting/](../vision-meeting/README.md),
with a [one-page summary](../vision-meeting/summary.md).
