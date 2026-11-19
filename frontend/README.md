# Lead integrations: frontend

Operator console and client review page for the lead-integrations MVP.
Vite + React 19 + TypeScript (strict). No router, CSS framework or component library.
The API contract is `../docs/api.md`; its types live in `src/api/types.ts`.

## Run

```sh
npm install
npm run dev        # http://localhost:5173
npm run typecheck
npm run build      # typecheck, then build to dist/
npm run preview    # serve dist/ on http://localhost:4173
```

The dev and preview servers proxy `/api` and `/receiver` to the backend on
`http://localhost:5080`. Without the backend the pages show a "could not load"
notice and keep retrying.

## Pages

Pages poll every 2 seconds while the tab is visible.

| Route | Page | Calls |
|---|---|---|
| `#/` | Integrations overview: firms per source, kinds × sources, issues, import runs | `GET /api/overview`, `GET /api/catalog` |
| `#/firms` | Firm list with search, kind, source and has-issues filters | `GET /api/firms` |
| `#/firms/{id}` | Firm detail: sources, routing, connections, synthetic lead, recent deliveries | `GET /api/firms/{id}`, `GET /api/deliveries?firmId=`, `POST /api/firms/{id}/leads`, `GET /api/connections/{id}/preview`, `POST /api/connections/{id}/test`, `PATCH /api/connections/{id}`, `POST /api/deliveries/{id}/replay` |
| `#/flow?firm={id}` | Delivery graph (React Flow + dagre), with the connection actions in a side panel | as the firm page |
| `#/deliveries` | Delivery Center: needs attention first, production / tests / received tabs, attempt journey, replay | `GET /api/deliveries`, `GET /api/deliveries/{id}`, `GET /api/connections?status=disabled`, `GET /api/received` |
| `#/onboarding` | Onboarding cockpit: queue, setup record, draft revisions, new setup | `GET/POST/PATCH /api/setups…`, `GET /api/setups/{id}/fields` |
| `review.html?t=TOKEN` | Client secure-link page (separate entry) | `/api/client/setups/{token}…` only |

"Accepted by receiver" means the receiver answered 2xx. We never claim a lead is in the CRM.
Credentials are shown only as present/missing plus a masked fingerprint.

## Layout

- `src/api/`: contract types and the typed fetch client
- `src/lib/`: polling, toast + aria-live status, hash routing, delivery and setup helpers
- `src/components/`: shell, connection card, delivery list and journey, firm picker
- `src/pages/`: one file per page (`FlowPage` is lazy-loaded)
- `src/styles/`: `base.css` and `console.css` are ported from the webhook-lab prototype; `app.css` adds what is new
