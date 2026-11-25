# The intake lead contract: how delivery takes a lead

**Status: Lab (milestone 1), 2026-11-25.** The first seam across repositories: a lead the call
center's simulator produced reaches a fake CRM through delivery. Delivery takes leads in
`intake-lead/draft-2`, the contract intake owns, and changes nothing about it.

| File | What it is |
|---|---|
| [draft-2.schema.json](draft-2.schema.json) | An **exact copy** of intake's schema (`../intake-policy-lab/docs/lead-contract.schema.json`). Never edited here: `python tools/check_contracts.py` fails if it differs from intake's. A new draft is a new file and a new route |
| [draft-3.schema.json](draft-3.schema.json) | An exact copy of intake's draft 3 (`lead-contract.draft-3.schema.json`), checked the same way |
| [examples/](examples/) | Draft 2: two leads as the simulators wrote them, a person's call and the AI's. Draft 3 (`draft-3-*`): two from intake's conformance suite, under canonical firm ids. Synthetic |
| `POST /api/ingest/intake-lead/draft-2` | The route ([api.md](../../api.md)) |
| [tools/publish_leads.py](../../../tools/publish_leads.py) | Posts a folder of lead files to it, unchanged. Reads the files only |

## Run it

```sh
cd backend && dotnet run --project src/Api          # the API, with its worker
# a firm with a destination, then map each sender firm id to it:
python tools/publish_leads.py --alias injury-firm=firm_… --alias general-firm=firm_… ../call-center-lab/out/leads
```

`cargo run -p center-sim --bin play` then `node tools/judge.ts out/judge` in `../call-center-lab`
writes `out/leads/`; the voice agent's `python -m agent_lab.play` writes its own. Run on
2026-11-25 against a fresh database with one webhook destination: 22 leads (6 from the call
center, 16 from the voice agent) recorded and delivered, 22 accepted by the receiver; sent again,
each was recognised as already recorded.

## Who owns what

| Domain | Source of truth | Here |
|---|---|---|
| The call's lifecycle, queues, transfers | Call center | Never kept |
| What a lead must ask, approved policy revisions | Intake policy | The revision number, as the sender wrote it |
| Building the lead; the contract's shape | Intake policy (contract and builder); who runs the builder in production is open | Never built here; validated against the contract only |
| Running the conversation | Voice agent, as one participant in the call center | Never kept |
| Delivery, retries, failure classes | **Delivery (this repository)** | Everything after the lead is accepted |
| Raw usage | Call center | Not read yet (a gap below) |
| Rating and invoicing | Delivery (billing) | Today from leads, not usage (a gap below) |
| Firm identity | **The call center**, through the provisioning boundary: canonical, opaque ids (M2) | Each firm here carries its canonical id; aliases remain for senders not yet on canonical ids |

## What happens to a lead

1. The body is parsed and checked against the contract's schema
   ([LeadContract.fs](../../../backend/src/Core/LeadContract.fs): the keywords the schema uses,
   and a refusal for any it doesn't, so a new draft can't be half-checked). **A tolerant
   reader:** a field this copy of the schema doesn't know is accepted and kept in the payload as
   sent, because contracts change additively while in use; every field it knows is checked. The category and
   practice area must be in the taxonomy. Any refusal is a **422** and records nothing.
2. In one transaction ([Dispatch.ingest](../../../backend/src/Core/Dispatch.fs)):
   - `firm.id` is resolved through its **alias**; none is a 422;
   - a lead already recorded for the same firm and `conversation.id` is a **duplicate** when its
     canonical payload (sorted keys, no whitespace, sha256) is the same, and a **409 conflict**
     when it differs; the same `leadId` under another interaction is a 409 too;
   - otherwise the lead, its deliveries (the outbox) and the original payload are written
     together: the existing recording, routing and outbox, unchanged.
3. The worker sends it as any other lead: the same retries, failure classes, switch-off and
   fallback. A database that does not take the write is a **503**: nothing is recorded, and the
   sender keeps the lead and sends it again.

Kept for each lead (`GET /api/leads/{id}/contract`): the payload byte for byte, its hash, the
contract, `leadId`, the interaction, the sender's firm id, and the policy revision and whether it
was approved.

## The mapping

| Contract | Delivery's lead | Note |
|---|---|---|
| `contact.firstName`, `lastName` | `firstName`, `lastName` | Only `fullName`: split at the first space |
| `contact.phone`, `email` | `phone`, `email` | null → empty |
| `classification.callType.name` | `callType` | Shown only, never parsed |
| `classification.category`, `practiceArea` | `category`, `practiceArea` | **Used as given** for routing, windows and billing (`Categories.ofLead`). null → `unmapped`: a sender that did not classify gets no category from the name |
| The `message` fact | `message` | None: what happened, in words, from `outcome` ("Existing client. Put through to intake.") |
| `facts[]`, answered | `custom[]` | Label from the field id; contact and message fields are not repeated |
| `conversation.channel` | `webchat`, `source` | `chat` → a chat lead; source "Answering AI" or "Answering AI chat" |
| `leadId`, `conversation.id`, `firm.id`, `policy` | Kept beside the lead | The idempotency key is firm + `conversation.id` |
| `classification.taken`, `qualification` | Kept in the payload | Never hold a lead back: a matter the firm does not take is routed like any other ([answers](../../lead-delivery/answers.md#routing-and-integrations)) |
| `urgency[]` | Kept in the payload | Not acted on, by choice: the window to act comes from the category alone, and firms get to leads when they can |
| Everything else | Kept in the payload | Not acted on yet |

## Draft 3

**Lab (milestone 3, delivery's side), 2026-11-25.** The lead the call center publishes: draft 2
plus `readyAt` and `provenance` (which builder, evaluated when, under which taxonomy). Intake owns
it ([its contract](../../../../intake-policy-lab/docs/lead-contract.md), "Draft 3"); the copy
here is checked against intake's like draft 2's.

- **Route:** `POST /api/ingest/intake-lead/draft-3`, beside draft 2's. Each takes only its own
  contract.
- **Idempotency on the lead's content only.** When an attempt was sent travels in headers
  (`Lead-Published-At`, `Lead-Publication-Attempt`), never in the payload, so a retry of the
  stored lead is a duplicate (200), never a conflict. The first and latest publication and the
  highest attempt are kept beside the lead.
- **The window to act starts at the interaction's completion** (`conversation.endedAt`), and so
  do the fallback and escalation clocks (`Store.clockStart`): a lead published late is late, and
  one published after its window closed expires.
- **Acceptance across repositories:** `python tools/acceptance_m3.py --api …` runs intake's worker
  on every conformance request and publishes each lead twice to a running API. Run on
  2026-11-25 on Node 22.18 against a fresh database: 10 firms provisioned under canonical ids, 65
  leads built and published twice, every retry a duplicate, 65 delivered once each to their own
  firm.

## Firm identity

**Lab (milestone 2), 2026-11-25.** A firm is an operational tenant, apart from the commercial
billing account that holds it; one account may hold several firms. Both have canonical,
opaque ids issued outside delivery and never parsed: 1 to 64 letters, digits and `. _ : -`.

- **The provisioning boundary** (`PUT /api/provisioning/firms/{canonicalId}`,
  [Provisioning.fs](../../../backend/src/Core/Provisioning.fs)) is the only way a firm here gets
  a canonical id. A new id creates the firm, or links an existing one (`adopt`, for a firm
  imported from the legacy systems); the same id again is idempotent and sets the name, time
  zone and account to what provisioning says. A firm never carries two canonical ids, and a
  canonical id never moves to another firm. Each provisioning that creates or links emits
  `FirmProvisioned`.
- **A lead names its firm** by `firm.id`: resolved as a canonical id first, then as an alias.
  An alias may never equal a canonical id, so the two can't disagree.
- **The firm's own id here** (`firm_…`) stays delivery's: every table keys on it, and only the
  boundary and the contract lead see the canonical one.

## Temporary, until the suite decides

- **Firm aliases.** For a sender not yet on canonical ids (the simulators send readable
  fixture names like `general-firm`), its firm id maps by hand to a firm here
  (`PUT /api/firm-aliases/{alias}`); an alias never moves to another firm. Retired when every
  sender sends canonical ids.
- **Draft 2's window to act runs from receipt,** not from `conversation.endedAt`: simulated
  calls carry simulated times. Draft 3 starts it at completion (above).
- **A file-based publisher.** The call center writes files; `publish_leads.py` posts them. No
  broker, no new service.

## Gaps this slice leaves

| Gap | Effect today |
|---|---|
| The call center does not publish leads itself | A person or script runs the publisher |
| `followUp` is kept, not shown | The follow-up's owner and deadlines are not in the lead's view (RFC-010) |
| `classification.needsReview`, `confidence` | Not used to hold a lead for review |
| Usage evidence is not read | Billing still rates leads, not the center's usage |
| No authentication on the route | Lab only |
| Senders still send fixture names | Aliases by hand until the center and intake key firms by canonical id |
| One billing account per firm in billing | Provisioning records each firm's account; billing does not group firms by it yet (suite X-5, M6) |
| No authentication on the provisioning boundary | Lab only: in production only the provisioning service may call it |
