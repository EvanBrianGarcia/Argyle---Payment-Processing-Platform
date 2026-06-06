# ADR-0016: OpenAPI codegen — schema committed in-repo, drift checked in CI

**Status:** Accepted
**Date:** 2026-06-06
**Phase:** 5
**Related:** master plan §12 "Generate OpenAPI from the API. Frontend types are generated from that schema. CI fails if the schema drifts without an intentional bump.", `.claude/plans/payment-platform-phase-5.plan.md` Task 1 and Task 7

## Context

The Phase 5 frontend talks to five backend endpoints (`POST /v1/payments`, `GET /v1/payments`, `GET /v1/payments/{id}`, `POST /v1/payments/{id}/capture`, `POST /v1/payments/{id}/refund`). Hand-typing the request and response shapes on the client invites silent drift the moment a backend DTO changes. The master plan §12 anchors the answer: generate types from the API's OpenAPI schema, and let CI fail on drift.

Two structural questions remain. (1) Should the schema be generated and committed (schema-in-repo) or fetched at codegen time? (2) Which codegen tool — types-only (`openapi-typescript`) or types + runtime client (`orval`, `kiota`)?

## Decision

**Schema-in-repo.** The backend writes `frontend/api/openapi.v1.json` whenever a maintainer runs `pnpm run codegen`. That file is committed. CI re-runs codegen against a fresh backend container and diffs the result against the committed copy; any diff fails the build. A backend DTO change therefore requires the same PR to update both the schema and the regenerated TypeScript types — exactly the round-trip we want.

**`openapi-typescript` for types only.** The generated artifact is a single `src/lib/api/generated.ts` containing the OpenAPI `paths` and `components` as TypeScript types. The runtime client (`src/lib/api/client.ts`) is a small hand-written `fetch` wrapper that consumes those types. TanStack Query supplies the cache and retry runtime; we do not need a generated client wrapper.

**Operation IDs are stable contract.** Each backend endpoint declares `.WithName("createPayment")`, `.WithName("listPayments")`, etc. (Phase 5 plan Task 1). Renaming an operation ID is a breaking schema change and shows up as a diff.

## Consequences

**Positive.** A reviewer can read the committed `openapi.v1.json` to see the contract without booting the backend. The drift check makes "I forgot to regenerate" impossible to land. Types-only output keeps the bundle minimum-viable — TanStack Query is the only runtime layer for HTTP.

**Negative.** Backend DTO changes touch three files (the C# record, the schema JSON, the generated TS). That cost is small and discoverable; the CI failure is loud.

**Neutral.** `openapi-typescript` updates occasionally rename internal types; pinning the major version is enough to manage churn.

## Alternatives

- **Live-fetch schema at codegen time, do not commit.** Faster steady-state, but PR review loses the contract artifact and the drift check requires a backend service in CI for every PR — heavier and not noticeably more correct.
- **`orval` for types + runtime client.** Produces a TanStack Query–shaped client out of the box. Bigger output, more configuration, and our endpoint surface is five operations — the hand-written wrapper is shorter than the orval config.
- **Microsoft Kiota.** Excellent for multi-language consumers; overkill for a single TS frontend.
- **Swashbuckle on the backend.** The .NET 10 first-party `Microsoft.AspNetCore.OpenApi` package is the default going forward. Swashbuckle remains a fallback if the first-party generator falls short of our metadata needs (operation IDs, examples, error envelopes) — kept in reserve, not adopted up front.

## Notes

The schema file lives at `frontend/api/openapi.v1.json` rather than under `backend/` because it is consumed by the frontend toolchain. The backend treats `/openapi/v1.json` (served only in `Development`) as a build artifact, not a versioned interface. The generated TS file `frontend/src/lib/api/generated.ts` is committed so code review sees the type changes alongside the API change.
