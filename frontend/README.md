# Argyle Payments Dashboard

React + Vite + TypeScript dashboard for the Argyle Payment Processing Platform. See ADR-0014 for the stack rationale, ADR-0015 for the visual direction, and ADR-0016 for the OpenAPI codegen strategy.

## Quick start

```bash
# 1. Start the backend (from repo root)
docker compose up -d
# or: cd backend && dotnet run --project src/PaymentPlatform.Api

# 2. Install + run the dashboard
cd frontend
pnpm install
pnpm dev
# open http://localhost:5173
```

The dev server proxies `/v1/*` and `/openapi/*` to `http://localhost:8080`. The dev bearer token is `dev-key-mrc-acme` and lives in `.env.development`. It is not a real secret — the seeded merchant in the backend recognizes its SHA-256 hash.

## Scripts

| Script | What it does |
|---|---|
| `pnpm dev` | Vite dev server with HMR on http://localhost:5173 |
| `pnpm build` | Type-check then bundle for production into `dist/` |
| `pnpm preview` | Serve the built bundle (used by Playwright) |
| `pnpm test` | Run Vitest with MSW-backed component + hook tests |
| `pnpm test:coverage` | Run tests + emit a coverage report (gate: 70% lines) |
| `pnpm typecheck` | `tsc --noEmit` |
| `pnpm lint` | ESLint over `src/` + `e2e/` |
| `pnpm codegen` | Regenerate `src/lib/api/generated.ts` from the committed `api/openapi.v1.json` |
| `pnpm codegen:fetch` | Curl the live backend's `/openapi/v1.json` into `api/` then run codegen |
| `pnpm e2e` | Run the Playwright happy-path against a backend at `E2E_API_BASE_URL` (default `http://localhost:8080`) |

## Codegen workflow

`api/openapi.v1.json` is the contract. When backend DTOs change:

```bash
# from frontend/ with backend running
pnpm codegen:fetch
git add api/openapi.v1.json src/lib/api/generated.ts
```

CI fails on drift between the committed schema and the live backend (Task 7 of the Phase 5 plan).

## Visual direction

This dashboard inherits Argyle's brand chrome (the diamond pattern strip, the indigo and lavender palette, the humanist sans typography) but its working surfaces are data-dense. The token set lives in `src/styles/tokens.css` mirrored from `stitch/argyle_operations_system/DESIGN.md`. See `docs/visual-direction.md` for the everyday reference.

**Hard rules** (code review enforces these):

- No Tailwind dependency.
- No hex literals in component files. Tokens only.
- No center-screen spinners; skeletons match the real layout to prevent CLS.
- No purple gradient hero or card-grid layout.
- Status colors are semantic. They never appear on non-status elements.

## Test pyramid

- **Unit + component** — Vitest + React Testing Library + MSW. Run with `pnpm test`. Includes the API client, format helpers, every shared UI primitive, the list page, and the detail page.
- **E2E** — Playwright + axe-core. One happy-path that walks list → detail and one a11y assertion. Run with `pnpm e2e` against a running backend.
- **Coverage gate** — `pnpm test:coverage` fails below 70% lines / 65% branches.

## Environment variables

| Variable | Default | Purpose |
|---|---|---|
| `VITE_API_BASE_URL` | `http://localhost:8080` | Backend base URL. All requests go here. |
| `VITE_DEV_BEARER_TOKEN` | `dev-key-mrc-acme` | Dev-only bearer token; not a real secret. |
| `VITE_ENV_LABEL` | `dev` | Environment pill shown in the nav. |

Production deployment is out of scope for Phase 5 — the build artifact is a static SPA that any static host can serve.

## Where things live

```
frontend/
├── api/                    # Committed OpenAPI schema
├── e2e/                    # Playwright tests
├── public/                 # Static assets (favicon)
└── src/
    ├── components/ui/      # Shared primitives (StatusBadge, Money, …)
    ├── features/
    │   ├── payments-list/  # List page + hook + table + chips + rail
    │   ├── payment-detail/ # Detail page + timeline + capture action
    │   └── filters/        # URL-as-state filter hook
    ├── hooks/              # Cross-feature hooks
    ├── lib/
    │   ├── api/            # client.ts, types, generated, queryKeys
    │   ├── format/         # money, time, id (ULID generator)
    │   └── env.ts          # Validated environment access
    ├── styles/             # tokens, typography, reset, global
    └── test/               # vitest setup, MSW handlers, fixtures
```
