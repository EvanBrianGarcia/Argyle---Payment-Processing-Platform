# ADR-0014: Frontend stack — Vite + React 18 + TypeScript, static SPA

**Status:** Accepted
**Date:** 2026-06-06
**Phase:** 5
**Related:** master plan §11 (repo layout `/frontend`), §15 #16 (static SPA assumption), `.claude/plans/payment-platform-phase-5.plan.md`

## Context

Phase 5 introduces a dashboard so reviewers can list payments, filter by status, and walk a single payment's audit trail. The master plan fixes the high-level constraint: "Built as a static SPA served by any static host (or by ASP.NET in dev). No SSR." (Assumption #16.) Inside that constraint we still owe an explicit stack choice — React framework version, bundler, state library, styling — because each of those defaults can quietly drag the implementation into a card-grid-Tailwind look (banned in ADR-0015) or into SSR territory (banned by the assumption).

## Decision

- **Bundler / dev server:** Vite 5+. Zero bundler config, native ESM HMR, first-class TypeScript.
- **Framework:** React 18 with `react-dom/client`. Concurrent features available for `Suspense` + lazy loading without committing to React 19 ahead of its broad adoption curve.
- **Language:** TypeScript 5.x, `strict: true`, `noUncheckedIndexedAccess: true`. The generated OpenAPI client (ADR-0016) leans on strictness to surface contract drift at compile time.
- **Routing:** `react-router-dom` v6 with the data-router-less plain `BrowserRouter`. Two routes (`/payments` and `/payments/:id`) plus a 404 catch-all — a heavier router is unjustified.
- **Server state:** TanStack Query v5. Handles dedupe, retry on network errors, optimistic mutations for the Capture action (Phase 5 plan Task 5), and the cache shape we need to roll back a 409.
- **Local UI state:** Plain `useState` / `useReducer` plus URL search params via `useSearchParams`. Filter and cursor live in the URL per ECC `frontend-patterns` "URL As State" guidance.
- **Styling:** CSS Modules over plain CSS files with a single tokens layer (CSS custom properties) imported once in `main.tsx`. **No Tailwind, no CSS-in-JS runtime.** Tokens come straight from `stitch/argyle_operations_system/DESIGN.md`.
- **Forms:** Native `<form>` + Zod for the single capture action. React Hook Form is unjustified for one button.
- **Testing:** Vitest + React Testing Library + MSW v2 for hermetic unit/component coverage; Playwright for one happy-path E2E (Task 7).

## Consequences

**Positive.** No Tailwind class soup invites the exact "card-grid Tailwind template" the master plan warns off in Phase 5. CSS Modules + tokens makes the visual direction (ADR-0015) enforceable in code review by simple file-level rules. TanStack Query absorbs the request-lifecycle boilerplate we would otherwise have to test by hand. Vite's dev server proxies `/v1/*` to the dotnet backend without extra dependencies.

**Negative.** A future contributor reaching for `tailwindcss` to add a one-off utility is mildly inconvenienced — they have to add a token instead. That is the intended friction.

**Neutral.** React 19 ships eventually; the upgrade is a single dependency bump and TanStack Query already supports both majors.

## Alternatives

- **Next.js App Router.** Adds SSR machinery the assumption explicitly excludes; the `/openapi/v1.json` codegen story still works, but the dev server boots slower and Server Components muddy the bearer-token + CORS story for this exercise. Rejected.
- **Remix.** Same SSR objection plus an additional learning surface (loaders/actions) that buys us nothing for a read-mostly dashboard.
- **Svelte + Kit / SolidStart.** Stack diversity for its own sake; the rest of the dotnet repo points at React as the long-term frontend choice per master plan §1.
- **TanStack Router.** More opinionated about type-safe routes than react-router. Worth considering on a larger surface; two routes do not justify the migration risk this late in the exercise.

## Notes

The dev server runs on port 5173. The backend dev port is 8080. The dev bearer token (`Auth:DevBearerToken`) is non-secret — it exists to make the exercise runnable cold, not for production. Production auth (OAuth2 / OIDC) is out of scope for Phase 5 per master plan §15 #7 and §13 Phase 5 deliverable.
