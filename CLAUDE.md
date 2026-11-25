# lead-integrations (CLAUDE.md)

MVP for agreeing which lead-delivery integrations Answering AI supports, and
proving each one end to end. F# backend + PostgreSQL (`backend/`, RFC-011), Vite + React +
TypeScript frontend (`frontend/`).

- **The call center (`../call-center-lab`) is the source of truth.** It owns every
  interaction and publishes the lead, follow-up progress and usage evidence through
  its versioned contracts; delivery, categories, billing and terms here are built on
  top of it and integrate through them (README, "Where this sits"). Never keep a
  second copy of what happened on a call.
- **Docs are generic.** Never name legacy internal systems or repositories, their
  file paths, tables or commands; say "the legacy system" and describe the behaviour.
  Record ideas as reasoning, not as references to where they came from.
- `docs/api.md` is the contract between backend and frontend. Change it first.
- Domain words are in `GLOSSARY.md` (model and operator workflows in
  `docs/lead-delivery/`). Use them in code, UI and docs; update the glossary
  when a term changes, and mark rules Lab / Production / Proposed. When a
  delivery flow or operator screen changes, update `docs/lead-delivery/code-map.md`
  (and `concurrency.md` with its explorer when the worker, a lease or a transaction boundary changes),
  and regenerate `docs/vision-meeting/` with `python tools/capture_vision_deck.py`
  if a screen in it changed. Decisions live only in `docs/lead-delivery/answers.md`,
  direction and roadmap in `strategy.md`, proposals for approval in `docs/rfc/`; state each fact in one place and link to it.
- Backend style: Oxpecker, plain SQL,
  Expecto tests, `TreatWarningsAsErrors`. F# compiles in file order.
- Backend: `cd backend && docker compose up -d && dotnet run --project src/Api` (port 5080; its
  database is delivery's PostgreSQL on 55433, or `DB_PATH`, another connection string; SQLite is refused) ·
  tests (the PostgreSQL container running; a database per test): `dotnet run --project tests/Tests --artifacts-path ../.testbuild`
  (the running API locks `bin/`; `DELIVERY_TEST_PG` names another server). SQLite remains only as the
  interleaving explorer's model, so write SQL both accept: no summing conditions, no ungrouped
  columns, cast a parameter tested only for null.
  Import: `dotnet run --project src/Import -- all`
- Contracts: `python tools/check_contracts.py` (files consistent) · `python tools/run_scenarios.py`
  (every scenario against a fresh lab; `lab` ones must pass). A rule change updates its scenario first.
- Frontend: `cd frontend && npm run dev` (proxies to 5080) · `npm run build`
- **Credentials are never stored or shown.** Importers reduce every secret to
  present + fingerprint the moment they read it. Imported connections deliver
  only to the local fake receivers (`/receiver/crm/{kind}`), never to a real CRM.
- **Time is UTC milliseconds; calendars use IANA zones** (`Zones.fs`), never fixed
  offsets: billing on the company's zone, a firm's hours and dates on its own.
- **No caller PII.** Leads are synthetic; importers read lead rows for intake
  *labels* only. `backend/data/` (real firm names) is git-ignored.
- The legacy import is read-only (`default_transaction_read_only`). Keep it so.
- A new integration kind = a row in `Catalog.fs` + a builder in `Payloads.fs`;
  the "every catalog kind builds a body its receiver accepts" test enforces it.
- Call categories: rules in `docs/call-categories/taxonomy.json`, embedded into
  Core (`Categories.fs`) and read by `tools/call_categories.py`. After changing
  a rule, run the script (regenerates `coverage.json`) and the tests: both check
  every reference lead, so the F# and Python rules must agree.
- Frontend target is Baseline 2025; newer features only as progressive enhancement.
- User-facing strings say "Answering AI"; never name voice or telephony vendors.
- Never commit or push unless asked.
