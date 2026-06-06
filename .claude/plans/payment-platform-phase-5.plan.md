# Plan: Payment Platform — Phase 5 (Frontend Dashboard)

**Source plan**: `.claude/plans/payment-platform.plan.md` (master plan §11 repo layout `/frontend`, §12 frontend tests, §13 Phase 5 deliverable, §6 error envelope contract, §15 Assumption #16 "Built as a static SPA")
**Builds on**: Phase 4 (`.claude/plans/payment-platform-phase-4.plan.md`) — API exposes payments endpoints, error envelope, correlation headers, redaction, and `/metrics`/`/health/*`. This phase consumes that surface from a browser.
**Goal**: Ship a usable, deliberately-designed dashboard that lets a reviewer (a) list payments filtered by status with cursor pagination, (b) open one payment and walk its event timeline, (c) see loading/empty/error states that feel designed — not generic Tailwind card-grid filler. Frontend reads from a **generated TypeScript client built from the API's OpenAPI schema**, so contract drift breaks CI before it breaks the UI.
**Complexity**: Medium-Large (~10–12 hours, 1 new project tree at `/frontend`, 1 backend wiring task for OpenAPI + CORS, ~40 new files, mostly UI + tests + tooling)

## 1. Summary

Phase 5 is the "credible UI" phase. The backend can already do everything; this phase makes it visible. Three deliverables anchor it:

1. **OpenAPI surface on the backend + generated TS client**. The API today has no Swagger/OpenAPI registered (`PaymentPlatform.Api.csproj` confirms this). We add `Microsoft.AspNetCore.OpenApi` (the .NET 10 first-party generator), expose `/openapi/v1.json` (dev only), and wire `openapi-typescript` / `orval` so `pnpm run codegen` writes `src/lib/api/generated.ts`. CI runs codegen; a drift between the committed schema and the live API fails the build.
2. **Two screens that look deliberate**. A list view (status filter chips + cursor pagination + a dense, monospace-friendly table) and a detail view (a state badge with semantic color, a vertical event timeline, and a copy-as-curl affordance for the payment). Both share the same design tokens. No card-grid. No purple gradient hero. Swiss / data-dense, per the master plan.
3. **A real test pyramid**. Vitest + React Testing Library for components and hooks, MSW (Mock Service Worker) for the API layer, and one Playwright happy-path E2E that boots the whole `docker compose` stack and walks "load → filter Failed → open detail → see timeline."

Phase 5 deliberately does NOT add: auth UI (the dev bearer token is hard-coded for the exercise, master plan §15 assumption #7), SSR / Next.js (master plan §15 #16: static SPA), real-time updates (Phase 6 polish at most), bulk actions or exports (master plan §17 #6 explicitly out of scope), or charts beyond a tiny status summary strip. The dashboard is read-mostly with one "Capture" action so reviewers can drive a state transition without curl.

## 2. What "done" looks like (the acceptance walkthrough)

Against `docker compose up` + `cd frontend && pnpm dev`:

1. Browser at `http://localhost:5173`. Page loads in < 1.5s on cold cache; LCP is the table header, not a spinner.
2. The list view shows the seeded payments. Top-left: a small **status-rail** strip showing `Pending: 3 · Authorized: 5 · Captured: 2 · Settled: 8 · Failed: 1 · Refunded: 0` driven by `/v1/payments?status=...` aggregated client-side over the first page. (We don't add a backend aggregate endpoint for this — master plan §17 #6 keeps it out.)
3. Filter chips (`All`, `Pending`, `Authorized`, `Captured`, `Settled`, `Failed`, `Refunded`) update the URL: `?status=failed`. Reloading the page preserves the filter. Cursor lives in `?cursor=...`. URL-as-state per `ecc:frontend-patterns` "URL As State" section.
4. The table is dense: ID (monospace, truncated mid with `…`), Amount (right-aligned, decimal-aligned, currency suffix), Status (badge), Created (relative `2m ago` with tooltip showing ISO UTC), Updated, Customer Ref. Row click → detail. Keyboard: ↑/↓ moves the row cursor, Enter opens, `/` focuses the search/filter, `Esc` clears.
5. Detail view at `/payments/pay_01H...`:
   - **Header block**: the state badge (Settled = solid green, Failed = solid red, Refunded = solid amber, Pending/Authorized/Captured = outlined), amount centered with currency, customer reference, and a `Copy as curl` button that copies a `curl -H "Authorization: Bearer ..." http://localhost:8080/v1/payments/{id}` line.
   - **Event timeline**: vertical, top-down chronological. Each event renders `from_status → to_status` as a connector, the actor (`system` / `merchant`), the reason text, and a small `<details>` for the payload JSON.
   - **Capture action** (only visible when current status is `Authorized`): a button that POSTs `/v1/payments/{id}/capture` with an auto-generated idempotency key, optimistically updates the badge to `Captured`, rolls back on failure.
6. Hit the `Failed` filter chip → table shows only the seeded Failed payment. Open it. See the timeline with the failure reason from `PaymentEvent.Reason`. The acceptance walkthrough in master plan §13 Phase 5 is met.
7. Loading: a dense skeleton table (no center-screen spinner). Empty: an intentional empty state with copy ("No payments match this filter — try All, or POST one via the API."). Error: an inline error envelope reading from the backend's `ErrorEnvelope` contract (`error.code`, `error.message`, `error.requestId`) — the request id is shown and selectable so the reviewer can grep logs by it.
8. `pnpm test` runs and passes — Vitest + RTL component tests, MSW-backed hook tests, and one Playwright E2E. Coverage report shows ≥70% line coverage (master plan §12 target). `pnpm build` produces a static bundle under 150 kB gzip JS (master plan §15 #16 + ECC web/performance.md budget).
9. `pnpm codegen` regenerates `src/lib/api/generated.ts` from the live backend at `http://localhost:8080/openapi/v1.json`. Committing a schema diff without regenerating fails CI.
10. README's "Run the dashboard" section is one paragraph and one command. The screenshot in the README matches what runs.

## 3. Skills and TDD discipline

Skills load on demand at the start of each task, not all up front.

### Always-on for the phase

| Skill | Where it applies |
|---|---|
| `ecc:frontend-patterns` | Already loaded for this plan. Anchors composition / custom hooks / URL-as-state / virtualization / error-boundary / a11y patterns. |
| `ecc:tdd-workflow` | Every task. RED → GREEN → REFACTOR → REVIEW → VALIDATE, same cadence as Phases 1–4. Tests precede UI. |
| `ecc:react-testing` | RTL queries, MSW patterns, accessibility-first selectors over CSS selectors. |
| `ecc:typescript-reviewer` (via `ecc:code-review`) | After every task's GREEN. |
| `ecc:accessibility` | Every interactive control, every state badge color choice (contrast), every keyboard handler. |

### Task-targeted skills

| Skill | Used in | Why |
|---|---|---|
| **`ecc:react-patterns`** | Task 3, 4, 5 | Component composition and compound-component patterns for the detail view (timeline, badge, action surface). |
| **`ecc:vite-patterns`** | Task 2, 7 | Vite config (env handling, proxy to backend, build target). |
| **`ecc:react-performance`** | Task 4, 5 | Memoization in the table render, list virtualization if the seed set crosses 100 rows. |
| **`ecc:design-system`** | Task 3 | CSS custom-property token layer, typography pairing, color tokens. |
| **`ecc:motion-foundations`** | Task 3, 5 | Subtle motion on row hover / detail enter — Swiss restraint, no flashy entrances. |
| **`ecc:frontend-a11y`** | Task 4, 5 | Roving tabindex for the table, aria-live on the empty/error states, focus trap is unnecessary (no modals). |
| **`ecc:react-testing`** | Task 4, 5, 6, 7 | Vitest + RTL component and hook tests. |
| **`ecc:e2e-testing`** | Task 7 | Playwright happy-path against the running docker compose stack. |
| **`ecc:api-design`** | Task 1 | Confirms the OpenAPI surface respects the error envelope contract (Phase 2 / §6 of the master plan) and stable URLs (`/v1/...`). |
| **`ecc:architecture-decision-records`** | Task 0 | ADR-0014 (frontend stack + bundler), ADR-0015 (visual direction: Swiss / data-dense, anti-template), ADR-0016 (OpenAPI codegen strategy: schema-in-repo vs. live-fetch). |
| **`docs-lookup` (Context7)** | Task 1, 2 | `Microsoft.AspNetCore.OpenApi` v10 setup, `orval` or `openapi-typescript` current API, MSW v2 handler signatures. Pull docs on demand — don't guess. |
| **`ecc:code-review`** | After every task | Auto-spawns `typescript-reviewer` + `react-reviewer`. |
| **`ecc:security-review`** | Task 1, 2 | CORS allow-list (no `*` even in dev), CSP header on the static host, no secret bundled by Vite (`VITE_*` env discipline — ECC web/security.md). |

### Skills explicitly NOT used in Phase 5 (with reasons)

- `ecc:nextjs-turbopack`, `ecc:nuxt4-patterns` — master plan §15 #16 mandates a static SPA; SSR is out.
- `ecc:react-build` resolver — only invoke if a build error blocks us; not a planned task.
- `ecc:flutter-*`, `ecc:swift-*`, `ecc:kotlin-*` — wrong stack.
- `ecc:multi-frontend` — single-stack work; multi-model orchestration is overkill.
- `ecc:remotion-video-creation`, `ecc:manim-video` — no video assets.
- `dotnet-skills:*` (apart from a quick touch in Task 1) — backend is in maintenance for this phase.

### TDD cadence (same shape as Phase 4)

For every task below:

1. **RED** — Write the failing test(s) first. RTL `getByRole` / `findByRole` queries against the not-yet-existing component or a hook test with MSW returning a fixture.
2. **GREEN** — Minimum component / hook code to make those tests pass.
3. **REFACTOR** — Extract presentational components from container, name the design tokens.
4. **REVIEW** — `ecc:code-review` on the diff. Address CRITICAL/HIGH first.
5. **VALIDATE** — Task-specific check (Lighthouse pass, Playwright pass, screenshot diff, etc.).

UI tests are flake risks. Every async assertion uses `findBy*` (not `getBy*` + sleep). Every fetch test uses MSW (not `vi.fn()` of `fetch`). The Playwright test is the only thing that touches a live network port; everything below it is hermetic.

## 4. Architectural decisions to record (Task 0)

Three ADRs land first. Format mirrors 0011–0013 from Phase 4. ~30 lines each. Files go under `docs/adr/` as `0014-*.md`, `0015-*.md`, `0016-*.md`.

### ADR-0014: Frontend stack — Vite + React 18 + TypeScript, no SSR

**Choice.** A static SPA built with Vite, React 18, TypeScript 5.x. State: TanStack Query for server state, plain `useState`/`useReducer` for local UI. Styling: CSS Modules + design tokens via CSS custom properties — no Tailwind, no CSS-in-JS runtime. Forms: native `<form>` + a small validator (Zod), no React Hook Form for the single capture button we ship.

**Why.** Master plan §15 #16 is explicit ("Built as a static SPA … No SSR"). React 18 is the broad-est-supported version that still gets Concurrent features we use (Suspense for code-split). Vite gives us instant HMR with zero bundler config. CSS Modules + tokens keeps the "looks deliberate, not template" promise enforceable in code review — Tailwind invites the exact card-grid Tailwind look master plan §13 Phase 5 warns off.

**Consequence.** No Tailwind class soup in the diff. The visual direction (ADR-0015) is reinforced by the styling choice. TanStack Query handles request dedupe / cache / retry instead of us writing it.

### ADR-0015: Visual direction — Swiss / data-dense, anti-template

**Choice.** The dashboard is a **monitoring instrument**, not a marketing page. Typography pair: a humanist sans (Inter Variable, system fallback) at 14px base for tables, with a slab-serif accent (Source Serif 4, fallback Georgia) for headers. Color: single accent (`oklch(58% 0.18 250)` blue), semantic state colors (settled green, failed red, refunded amber), with the rest of the palette desaturated neutrals. Layout: 12-column grid with a left-aligned filter rail, a dense table, and a slide-in detail panel — no card grids. No purple gradients. No drop-shadow stacking.

**Why.** ECC web/design-quality.md "Anti-Template Policy" — banned patterns include "stock hero section with centered headline, gradient blob," "default card grids," "safe gray-on-white styling with one decorative accent color." A payments dashboard for engineers should look like Linear or Vercel's observability surface, not a Stripe lookalike.

**Consequence.** Required qualities checklist from ECC design-quality.md applies — hierarchy via scale contrast (numbers larger and tabular), intentional rhythm in spacing (8px base unit), real type pairing, hover/focus/active states that feel designed.

### ADR-0016: OpenAPI codegen — schema committed, regenerated in CI

**Choice.** Backend exposes `/openapi/v1.json` via `Microsoft.AspNetCore.OpenApi`. A copy of the schema is committed at `frontend/api/openapi.v1.json`. `pnpm run codegen` runs `openapi-typescript` (preferred over `orval` — leaner output, no runtime) against the committed schema and writes `src/lib/api/generated.ts`. A CI step fetches the live schema from a temporary backend container and diffs it against the committed copy — drift fails CI.

**Why.** Two failure modes to defend against: (a) silent UI breakage when a contract field is renamed, (b) generator output committed but built against a different schema than the API actually serves. Schema-in-repo + drift check in CI catches both. `openapi-typescript` over `orval` because we want types only — TanStack Query already gives us the runtime layer.

**Consequence.** Committing a `.csproj` change to a request/response DTO and forgetting `pnpm codegen` is a CI failure, not a runtime mystery.

**Acceptance for Task 0.** Three ADR files exist, each ≤ 50 lines, written in the project's house voice.

## 5. Architectural shape of the frontend

```
/frontend
├── package.json
├── pnpm-lock.yaml
├── vite.config.ts
├── tsconfig.json
├── tsconfig.node.json
├── index.html
├── playwright.config.ts
├── vitest.config.ts
├── api/
│   └── openapi.v1.json              # committed schema
├── public/
│   └── favicon.svg
└── src/
    ├── main.tsx                     # bootstrap; QueryClient, ErrorBoundary, Router
    ├── App.tsx                      # route surface
    ├── styles/
    │   ├── tokens.css               # design tokens (CSS custom props)
    │   ├── reset.css
    │   ├── typography.css
    │   └── global.css
    ├── components/ui/               # primitives shared across features
    │   ├── StatusBadge/
    │   ├── Skeleton/
    │   ├── KbdHint/
    │   ├── CopyButton/
    │   ├── RelativeTime/
    │   ├── Money/
    │   └── ErrorEnvelope/
    ├── features/
    │   ├── payments-list/
    │   │   ├── PaymentsListPage.tsx
    │   │   ├── PaymentsTable.tsx
    │   │   ├── StatusFilterChips.tsx
    │   │   ├── StatusRail.tsx
    │   │   ├── usePaymentsList.ts
    │   │   ├── paymentsList.test.tsx
    │   │   └── paymentsList.module.css
    │   ├── payment-detail/
    │   │   ├── PaymentDetailPage.tsx
    │   │   ├── EventTimeline.tsx
    │   │   ├── CaptureAction.tsx
    │   │   ├── usePaymentDetail.ts
    │   │   ├── paymentDetail.test.tsx
    │   │   └── paymentDetail.module.css
    │   └── filters/
    │       └── useQueryParamFilter.ts
    ├── hooks/
    │   ├── useDebounce.ts
    │   ├── useKeyboardShortcuts.ts
    │   └── useReducedMotion.ts
    ├── lib/
    │   ├── api/
    │   │   ├── client.ts            # fetch wrapper + bearer header + error-envelope parser
    │   │   ├── generated.ts         # codegen output (gitignored? no — committed for review)
    │   │   └── queryKeys.ts
    │   ├── format/
    │   │   ├── money.ts
    │   │   ├── time.ts
    │   │   └── id.ts
    │   └── env.ts                   # VITE_API_BASE_URL etc.
    └── test/
        ├── setup.ts                 # vitest setup, MSW server
        ├── msw/
        │   ├── server.ts
        │   └── handlers.ts
        └── fixtures/
            └── payments.ts
└── e2e/
    └── payments.spec.ts
```

The `/frontend` tree lives alongside `/backend`, matching master plan §11. The root `package.json` is pnpm-managed (`packageManager: "pnpm@9..."`).

## 6. Tasks

Seven tasks, sequenced. Tasks 0–2 unblock everything. Tasks 3–6 ship the UI. Task 7 is the test/CI cap.

### Task 0 — ADRs and visual direction note (≈ 30 min)

Write `docs/adr/0014-*`, `0015-*`, `0016-*` as drafted above. Add `docs/visual-direction.md` with: typography choices, color tokens (oklch swatches), spacing scale, three screenshot references (Linear, Vercel Observability, Stripe Sigma — references only, not assets to copy).

**Validation.** ADRs review-clean, visual direction doc readable on its own.

### Task 1 — Backend: OpenAPI surface, CORS, dev-stable bearer (≈ 1 h)

**Goal.** Frontend can hit the API and codegen has a schema to chew on.

**Changes (backend).**
- Add `Microsoft.AspNetCore.OpenApi` package to `PaymentPlatform.Api.csproj` (and only this — no Swashbuckle; .NET 10's first-party gen is enough for this exercise).
- `Program.cs`: `builder.Services.AddOpenApi("v1")` and, in dev only, `app.MapOpenApi("/openapi/{documentName}.json")`. Add OpenAPI metadata (`.WithName`, `.WithSummary`, `.WithDescription`, `.ProducesProblem`) on each route in `PaymentsEndpoints.cs` so the generated client gets named operations (`createPayment`, `listPayments`, `getPayment`, `capturePayment`, `refundPayment`).
- Wire CORS in dev: a named policy `frontend-dev` allowing `http://localhost:5173`, methods `GET, POST`, headers `Authorization, Content-Type, Idempotency-Key`, and exposing `traceparent` (Phase 4) so the frontend can read it back. `app.UseCors("frontend-dev")` gated to `Development`.
- Add an `appsettings.Development.json` `Auth:DevBearerToken` entry and document it in README's frontend section.

**Tests (backend).**
- `OpenApiSchemaTests` (xUnit, `WebApplicationFactory`): GET `/openapi/v1.json` returns 200, contains paths `/v1/payments`, `/v1/payments/{id}`, `/v1/payments/{id}/capture`, `/v1/payments/{id}/refund`. Asserts the operation IDs by name.
- `CorsPolicyTests`: an OPTIONS preflight from `Origin: http://localhost:5173` with `Access-Control-Request-Method: POST` returns 204 with the expected headers.

**Skills.** `docs-lookup` (Context7: `Microsoft.AspNetCore.OpenApi` v10 surface), `ecc:api-design`, `ecc:security-review` (CORS allow-list, no `*`).

**Validation.** `curl -s localhost:8080/openapi/v1.json | jq '.paths | keys'` shows the 5 routes. Phase 1–4 integration tests still green.

### Task 2 — Frontend scaffold + codegen + API client (≈ 1.5 h)

**Goal.** A blank Vite app that boots, with a typed API client wired against the live schema.

**Changes.**
- `pnpm create vite frontend --template react-ts` (executed under the repo root, paths normalized).
- Dependencies: `react@18`, `react-dom@18`, `react-router-dom@6`, `@tanstack/react-query@5`, `zod@3`, `clsx`. Dev: `vitest`, `@testing-library/react`, `@testing-library/user-event`, `@testing-library/jest-dom`, `jsdom`, `msw@2`, `@playwright/test`, `openapi-typescript`.
- `vite.config.ts`: dev server proxy `/v1/* → http://localhost:8080`, env prefix `VITE_`, build target `es2022`, source maps in dev only.
- `package.json` scripts: `dev`, `build`, `preview`, `test`, `test:watch`, `test:ui`, `lint`, `typecheck`, `codegen`, `e2e`.
- `pnpm run codegen` script: `openapi-typescript api/openapi.v1.json -o src/lib/api/generated.ts`.
- `src/lib/api/client.ts`: a thin fetch wrapper that (a) reads `VITE_API_BASE_URL`, (b) sets `Authorization: Bearer ${VITE_DEV_BEARER_TOKEN}`, (c) generates an `Idempotency-Key` ULID per mutation, (d) parses the backend `ErrorEnvelope` (`{ error: { code, message, requestId, traceId } }`) into a typed `ApiError` class, (e) re-exports operation-keyed functions whose param/return types come from `generated.ts`. This is the only file that touches `fetch`.
- `src/main.tsx`: `QueryClientProvider` (5-min stale, 2 retries on network only), `BrowserRouter`, `ErrorBoundary` (the `ecc:frontend-patterns` "Error Boundary Pattern" reference impl).
- `.env.example`, `.env.development` with `VITE_API_BASE_URL=http://localhost:8080`, `VITE_DEV_BEARER_TOKEN=dev-only-not-a-real-secret`.
- A `.gitignore` for `node_modules`, `dist`, `coverage`, `playwright-report`, `.vite`.

**Tests.**
- `client.test.ts`: hits MSW for each operation, asserts (i) bearer header is sent, (ii) `Idempotency-Key` is sent on POSTs and is a valid ULID, (iii) a `503` with the envelope body throws an `ApiError` whose `requestId` matches what MSW returned.

**Skills.** `ecc:vite-patterns`, `ecc:react-patterns`, `docs-lookup` (Context7: `openapi-typescript` v7, MSW v2 handler signatures).

**Validation.** `pnpm dev` renders a placeholder page. `pnpm codegen && pnpm typecheck` succeeds. `pnpm test` runs the API-client unit test.

### Task 3 — Design tokens, typography, status badge, shared primitives (≈ 1.5 h)

**Goal.** The "looks deliberate" promise lands here. Tokens are the single source of truth before any feature renders.

**Changes.**
- `src/styles/tokens.css`: CSS custom properties for color (8 neutral steps, accent, status colors), typography (sizes `--text-sm`, `--text-base`, `--text-md`, `--text-lg`, `--text-display`; weights), spacing (4px base, `--space-1`..`--space-12`), radius, motion (`--duration-fast`, `--ease-out-expo` per ECC web/coding-style.md), z-index, shadows (one elevation step only — Swiss restraint).
- `src/styles/typography.css`: imports Inter Variable + Source Serif 4 via `font-display: swap`. Sets tabular numerals (`font-variant-numeric: tabular-nums`) globally for the table.
- `src/styles/reset.css`: modern reset (Andy Bell's variant), keyboard-focus visibility.
- `src/components/ui/StatusBadge.tsx`: takes a `PaymentStatus` and renders a semantic badge. Solid for terminal states (`Settled`, `Failed`, `Refunded`), outlined for in-flight (`Pending`, `Authorized`, `Captured`). Contrast checked: WCAG AA on every variant.
- `src/components/ui/Money.tsx`: takes `{ amountMinor, currency }`, renders `12,000.00 USD` with tabular numerals.
- `src/components/ui/RelativeTime.tsx`: `2m ago` with a `<time title>` of full ISO UTC.
- `src/components/ui/Skeleton.tsx`: shimmer-free flat skeleton (a single CSS animation, no JS), respects `prefers-reduced-motion`.
- `src/components/ui/CopyButton.tsx`: reads from a `value` prop, uses `navigator.clipboard.writeText`, shows a "Copied" affordance for 1.2s.
- `src/hooks/useReducedMotion.ts`.

**Tests.**
- `StatusBadge.test.tsx`: renders each status, asserts `role="status"` and accessible name. Snapshot on the rendered class to lock the styling intent.
- `Money.test.tsx`: `{ amountMinor: 1234567, currency: 'USD' }` renders `12,345.67 USD`. Zero, negative (refund), JPY (no decimals — handle currency exponent table).
- `RelativeTime.test.tsx`: at `now=2026-06-06T12:00Z` and `at=2026-06-06T11:58Z`, renders `2m ago` and tooltip shows the ISO.
- `Skeleton.test.tsx`: with `prefers-reduced-motion: reduce`, asserts no animation class.

**Skills.** `ecc:design-system`, `ecc:accessibility`, `ecc:motion-foundations`.

**Validation.** Story-less but still verifiable: a tiny `/dev/components` route renders all primitives in a single page; visual scan against the visual-direction doc.

### Task 4 — Payments list view + status filter + cursor pagination (≈ 2 h)

**Goal.** The first real screen.

**Changes.**
- `src/features/payments-list/usePaymentsList.ts`: a TanStack Query hook keyed by `['payments', { status, cursor, limit }]`. Reads filter + cursor from URL. Returns `{ data, isLoading, isError, error, fetchNextPage, hasNextPage }`. Composes with `useQueryParamFilter` (a small custom hook that wraps `useSearchParams` per ECC `ecc:frontend-patterns` URL-As-State).
- `src/features/payments-list/StatusFilterChips.tsx`: 7 chips (`All`, six statuses). The active chip uses the accent token; inactive uses neutral. Keyboard: arrow keys cycle, `Enter`/`Space` activates.
- `src/features/payments-list/StatusRail.tsx`: a horizontal strip of `Status: N` pairs, computed client-side from the loaded page. Each pair is clickable, behaves identically to the chip with the same status. (Client-side aggregation is honest about its limit — a tooltip explains "Counts for the current page; backend aggregate endpoint not implemented.")
- `src/features/payments-list/PaymentsTable.tsx`: a semantic `<table>` with `<thead>`/`<tbody>`. Columns: ID, Amount, Status, Created, Updated, Customer Ref. Roving `tabindex` on rows; row click and `Enter` navigate to detail. Empty state and loading skeleton rendered inside the same table shell to avoid layout shift (CLS budget — ECC web/performance.md). Virtualization is intentionally **not** added in this task — seed data is < 50 rows; add `@tanstack/react-virtual` only when row count crosses 200 (ECC `ecc:react-performance` recommendation, sized to data).
- `src/features/payments-list/PaymentsListPage.tsx`: pulls it all together — status rail above the table, chips above the table, pagination footer below (`← Prev` is grayed since cursor pagination is forward-only; `Next` enabled when `nextCursor` is present).
- URL contract: `/payments?status=failed&cursor=eyJ...`. Filter clears the cursor (no stale page).

**Tests.**
- `paymentsList.test.tsx`:
  - **RED first**: a test asserting the table renders 3 rows when MSW returns 3 fixtures — fails because component doesn't exist.
  - GREEN: build minimum component. Test passes.
  - Add: filter chip click updates the URL and triggers a refetch (asserted via MSW receiving the `?status=Failed` query string).
  - Add: pressing `Next` advances the cursor; the URL updates; the second page's rows render.
  - Add: empty result renders the empty-state copy with `role="status"`.
  - Add: error result renders the inline `ErrorEnvelope` with `requestId` visible.
  - Add: keyboard — `ArrowDown` moves the row focus; `Enter` calls `navigate('/payments/pay_...')` (mocked via `MemoryRouter`).
- `usePaymentsList.test.ts`: hook isolation. Asserts the query key changes with filter and cursor.

**Skills.** `ecc:react-patterns` (compound components for table sections), `ecc:react-testing`, `ecc:frontend-a11y` (roving tabindex), `ecc:react-performance` (memoize row component).

**Validation.** Manually: open `/payments`, click chips, paginate, keyboard-walk. Lighthouse on the list page: Performance ≥ 90, Accessibility ≥ 95.

### Task 5 — Payment detail view + event timeline + capture action (≈ 2 h)

**Goal.** The second real screen — the one master plan §13 Phase 5 acceptance walkthrough hangs on.

**Changes.**
- `src/features/payment-detail/usePaymentDetail.ts`: `useQuery(['payment', id])`. Returns `{ data, isLoading, isError, error }`. Refetches when the route param changes.
- `src/features/payment-detail/PaymentDetailPage.tsx`:
  - Header block: status badge (huge — `--text-display`), amount centered with `Money`, `Created at` + `Updated at` (RelativeTime + ISO), `Copy as curl` button.
  - Body: two-column at ≥ 1024px (timeline left, metadata table right); single-column at < 768px.
  - `<header role="banner">` and `<main>` semantic landmarks.
- `src/features/payment-detail/EventTimeline.tsx`:
  - `<ol>` of events sorted ascending by `at`. Each `<li>` is a vertical stripe with a dot, the `from_status → to_status` arrow (rendered with a CSS `::before` connector), the actor, the reason, and a `<details>` for the payload JSON (rendered via `<pre>` with monospace stack).
  - First entry has no `from_status` (initial create); render `— → Pending` with the connector half-length.
- `src/features/payment-detail/CaptureAction.tsx`:
  - Visible only when `data.status === 'Authorized'`.
  - Button POSTs `/v1/payments/{id}/capture` with a fresh ULID idempotency key. Optimistic update via TanStack Query `setQueryData`. On 200, the new payment payload (with the new event row) replaces the cache. On error, the optimistic state rolls back and the error envelope renders.
- The `Copy as curl` button copies a string built against `VITE_API_BASE_URL` and `VITE_DEV_BEARER_TOKEN`.

**Tests.**
- `paymentDetail.test.tsx`:
  - Renders the header (status, amount, customer ref) given a fixture.
  - Renders the event timeline in chronological order with each event's actor and reason.
  - When status is `Authorized`, the Capture button is visible and enabled. After a successful POST (MSW), the badge re-renders as `Captured` and a new event entry appears.
  - When status is `Settled`, the Capture button is not in the tree.
  - On a 409 from MSW (state conflict), the optimistic update rolls back and the error envelope renders with the backend's `requestId`.
  - `Copy as curl` writes a string containing the bearer token to the clipboard (via a mocked `navigator.clipboard`).
- `EventTimeline.test.tsx`: renders the connector for non-first entries; renders `— → Pending` for the first entry; expanding `<details>` reveals the payload JSON.

**Skills.** `ecc:react-patterns`, `ecc:react-testing`, `ecc:accessibility` (the `<details>` works with keyboard out of the box; the timeline is an `<ol>` so screen readers read it as a list).

**Validation.** Manually: open a Failed payment from the list, read the timeline, confirm the failure reason is on-screen. Open an Authorized one (capture it via the button, watch the badge flip). Lighthouse: Performance ≥ 90, Accessibility ≥ 95.

### Task 6 — Loading / empty / error states across the app + status rail + URL routing polish (≈ 1 h)

**Goal.** The "states that look intentional" promise in master plan §13.

**Changes.**
- Audit the list + detail screens for every state. Replace any default browser error with the typed `ErrorEnvelope` component.
- Wire the `ErrorBoundary` at the route surface so a render error per route renders a localized envelope (with the trace id pulled from the last failing response if available — best-effort).
- A 404 route for unknown payment IDs (the `getPayment` endpoint already returns `payment_not_found`; surface that envelope as the page body, not a stub).
- A status-rail tooltip explaining the client-side aggregation honesty (mentioned in Task 4).
- Add a `Skip to main content` link as the first focusable element (a11y).
- `<title>` per route (`Payments — Argyle` / `pay_01H… — Argyle`).

**Tests.**
- `errorBoundary.test.tsx`: a child component throws on render; the boundary renders the fallback with the message visible.
- `notFound.test.tsx`: navigating to `/payments/pay_DOESNOTEXIST` with MSW returning 404 envelope renders the envelope with `payment_not_found` code visible and a back link.

**Skills.** `ecc:accessibility`, `ecc:frontend-a11y`.

**Validation.** Manual a11y check: tab through both screens, confirm focus order is logical and visible. `axe-core` via `@axe-core/playwright` run as part of the E2E in Task 7.

### Task 7 — Vitest coverage + Playwright E2E + CI integration + README (≈ 1.5 h)

**Goal.** Lock the test pyramid and make the dashboard part of CI's definition of green.

**Changes.**
- `vitest.config.ts`: `environment: 'jsdom'`, `setupFiles: ['src/test/setup.ts']`, coverage provider `v8`, thresholds `lines: 70, branches: 65, functions: 70, statements: 70` (matches master plan §12 target).
- `playwright.config.ts`: one project (`chromium`), one base URL (`http://localhost:5173`), retries `1` in CI / `0` locally, screenshots `on-failure`, trace `on-first-retry`.
- `e2e/payments.spec.ts`:
  1. Boot precondition: a `globalSetup` runs `docker compose up -d` if not already, polls `/health/ready` until 200, seeds via a tiny test fixture script (`POST /v1/payments` 3× — one of which we then force-fail via the stub processor's failure header).
  2. Visit `/payments`. Expect the table with 3 rows.
  3. Click the `Failed` chip. Expect 1 row.
  4. Click the row. Expect the detail page with the failure reason in the timeline.
  5. Run `@axe-core/playwright` against both pages; assert no `violations` of `serious` or `critical` impact.
- A `frontend/README.md` (or expand the root README's frontend section) with: prerequisites, env file, `pnpm dev`, `pnpm test`, `pnpm e2e`, and the one paragraph that explains the visual direction so a future contributor doesn't drift it.
- Root README: add a screenshot of the dashboard (committed as a PNG under `docs/`).
- CI: extend `.github/workflows/ci.yml` (creating it if not present) with a `frontend` job: `pnpm install --frozen-lockfile`, `pnpm typecheck`, `pnpm lint`, `pnpm test --coverage`, then a `pnpm codegen` step that diffs the regenerated `generated.ts` against the committed copy (failing on diff), then `pnpm build`, then `pnpm e2e` against a `docker compose up`-built backend in a service container.

**Tests.** All of Tasks 1–6 must already be green here; the validation is that the threshold is met and the E2E passes.

**Skills.** `ecc:react-testing`, `ecc:e2e-testing`, `ecc:accessibility`.

**Validation.**
- `pnpm test --coverage` reports ≥ 70% lines and prints a green summary.
- `pnpm e2e` passes locally and in CI.
- A drift in `PaymentResponse` shape (test it: rename `customerReference` to `customer_ref` in the contract) fails CI before `pnpm build` even runs.
- README screenshot matches what the app renders.

## 7. Files to change

| File | Action | Why |
|---|---|---|
| `backend/src/PaymentPlatform.Api/PaymentPlatform.Api.csproj` | UPDATE | Add `Microsoft.AspNetCore.OpenApi` package. |
| `backend/src/PaymentPlatform.Api/Program.cs` | UPDATE | `AddOpenApi`, `MapOpenApi` (dev), CORS policy. |
| `backend/src/PaymentPlatform.Api/Endpoints/PaymentsEndpoints.cs` | UPDATE | OpenAPI metadata per route (`.WithName`, `.Produces<T>`, `.ProducesProblem`). |
| `backend/src/PaymentPlatform.Api/appsettings.Development.json` | UPDATE | `Auth:DevBearerToken` + the documented frontend CORS origin. |
| `backend/tests/PaymentPlatform.IntegrationTests/Api/OpenApiSchemaTests.cs` | CREATE | Verify schema surface. |
| `backend/tests/PaymentPlatform.IntegrationTests/Api/CorsPolicyTests.cs` | CREATE | Verify preflight. |
| `docs/adr/0014-frontend-stack.md` | CREATE | ADR. |
| `docs/adr/0015-visual-direction-swiss-data-dense.md` | CREATE | ADR. |
| `docs/adr/0016-openapi-codegen-strategy.md` | CREATE | ADR. |
| `docs/visual-direction.md` | CREATE | Typography, color, spacing, references. |
| `docs/dashboard-screenshot.png` | CREATE | README asset. |
| `frontend/**` | CREATE | All ~40 frontend source/test/config files per §5 layout. |
| `.github/workflows/ci.yml` | CREATE or UPDATE | Add frontend job. |
| `README.md` | UPDATE | "Run the dashboard" paragraph + screenshot reference. |
| `docker-compose.yml` | UPDATE (small) | Add a `frontend` service for the `pnpm preview` build, dev-profile only, to make the demo a one-command experience. (Optional polish; if it bloats compose, leave to Phase 6.) |

## 8. Patterns to mirror

| Category | Source | Pattern |
|---|---|---|
| Phase plan structure | `.claude/plans/payment-platform-phase-4.plan.md` | Goal → §1–§9 sections → ADRs → tasks → files → validation. |
| Test-first cadence | `.claude/plans/payment-platform-phase-3.plan.md` Task 1 | RED → GREEN → REFACTOR → REVIEW → VALIDATE, explicit per task. |
| Error envelope contract | `backend/src/PaymentPlatform.Contracts/Common/ErrorEnvelope.cs` | Frontend's `ApiError` is built directly off this shape. |
| OpenAPI route metadata | `backend/src/PaymentPlatform.Api/Endpoints/PaymentsEndpoints.cs:23–28` | Add `.WithName("createPayment")` etc. inline at each `.MapPost/.MapGet`. |
| URL-as-state | `ecc:frontend-patterns` "URL As State" section | Filter + cursor live in the URL, not in React state. |
| Compound components | `ecc:frontend-patterns` Tabs example | EventTimeline parent + items, PaymentsTable + Row + HeaderCell. |
| Custom hook for fetching | `ecc:frontend-patterns` `useQuery` example, but with TanStack Query | `usePaymentsList`, `usePaymentDetail`. |
| Error boundary | `ecc:frontend-patterns` "Error Boundary Pattern" | At the route surface. |
| Anti-template visual policy | ECC `web/design-quality.md` | Banned: card grid + gradient blob + safe gray-on-white. Required: hierarchy, rhythm, depth, hover/focus/active states that feel designed. |
| Test naming | ECC `common/testing.md` | `renders empty state when filter matches nothing`, not `test_renders`. |

## 9. Validation commands

```bash
# Backend
cd backend
dotnet build
dotnet test
curl -s localhost:8080/openapi/v1.json | jq '.paths | keys'

# Frontend
cd frontend
pnpm install --frozen-lockfile
pnpm codegen
pnpm typecheck
pnpm lint
pnpm test --coverage
pnpm build
pnpm preview &       # serve the production bundle
pnpm e2e             # Playwright against preview + docker compose

# Full stack demo
docker compose up -d
cd frontend && pnpm dev
# open http://localhost:5173
```

## 10. Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| The .NET 10 first-party OpenAPI generator's surface differs from what's documented for older versions | Medium | Pull current docs via `docs-lookup` (Context7) before Task 1 implementation; if it falls short on operation IDs / examples, fall back to Swashbuckle (single config swap, ~30 min). |
| `openapi-typescript` output uses union types that TanStack Query mutation hooks consume awkwardly | Low | We don't use a fancy mutations generator — `client.ts` wraps `fetch` and hand-types the 5 operations. The generated types are *types only*. |
| Visual direction slips into "default Tailwind look" anyway | Medium | ADR-0015 is the contract. Code review checks for: no Tailwind dep, no purple gradient, no card grid, no `box-shadow` stacking. The component checklist in ECC `web/design-quality.md` runs as part of the Task 3 + Task 4 + Task 5 review. |
| Playwright flakes on `docker compose` warm-up timing | Medium | `globalSetup` polls `/health/ready` with a 60s timeout; no naked sleeps. Test asserts on `findBy*`, not `getBy*`. |
| CORS misconfig allows `*` and we ship that to the README as "the way" | Low | Allow-list is the named `frontend-dev` policy, dev-only. ECC `web/security.md` review catches this. ADR-0014 explicitly notes the policy is dev-only and prod requires its own. |
| Bundle bloat — 150 kB gzip budget exceeded by TanStack Query + Inter Variable | Low | Inter Variable can be self-hosted as a subset; TanStack Query v5 is ~13 kB gzip. Lighthouse + a CI bundle-size check (size-limit, 1 line) defends the budget. |
| Generated TS client + committed schema drift gets re-committed silently in code review | Medium | The CI drift check (Task 7) makes the only way to land a contract change be: regenerate + commit both files. |
| The "Capture" optimistic update + 409 conflict path is subtly broken | Medium | Explicit test in Task 5 for the 409 rollback; this is the same kind of test we already have on the backend, mirrored on the client. |
| Time pressure pushes us to skip Task 0 ADRs | High | Master plan §13 Phase 5 deliverable is explicit; ADRs are 30 min total and unblock review-time debate. Don't skip. |

## 11. Acceptance checklist

- [ ] ADR-0014, 0015, 0016 land before any frontend code.
- [ ] `pnpm run codegen` produces `src/lib/api/generated.ts` from the committed schema; CI drift check enforces the round-trip.
- [ ] Backend exposes `/openapi/v1.json` (dev only) with the 5 named operations.
- [ ] CORS allow-list is the named `frontend-dev` policy, scoped to dev, no `*`.
- [ ] `/payments` list view: status filter chips + cursor pagination + dense semantic table + keyboard navigation.
- [ ] `/payments/:id` detail view: state badge + event timeline + capture action visible only when `Authorized` + copy-as-curl.
- [ ] Loading state is a dense skeleton, not a center spinner.
- [ ] Empty state is intentional copy with `role="status"`.
- [ ] Error state renders the typed `ErrorEnvelope` with `requestId` visible.
- [ ] No Tailwind dependency. No purple gradient. No default card-grid layout. (ADR-0015 spot-check.)
- [ ] WCAG AA contrast on every status badge variant. Focus visibility everywhere. Axe-clean.
- [ ] `pnpm test --coverage` ≥ 70% lines, branches, functions, statements.
- [ ] One Playwright happy-path E2E green: load → filter Failed → open detail → see timeline.
- [ ] LCP < 1.5s on the list page in Lighthouse on `pnpm preview`.
- [ ] Bundle JS gzip ≤ 150 kB on the list page (size-limit).
- [ ] README has a "Run the dashboard" section and a committed screenshot.
- [ ] CI extends to run the frontend pipeline + the schema-drift check.
- [ ] All Phase 1–4 backend tests still green.

---

**WAITING FOR CONFIRMATION**: Proceed with this Phase 5 plan? (yes / modify: <changes> / different approach: <alternative>)
