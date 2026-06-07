# ADR-0017: snake_case ↔ camelCase translation in the API client

**Status:** Accepted
**Date:** 2026-06-06
**Phase:** 5 (post-verification)
**Related:** [ADR-0014](0014-frontend-stack.md), [ADR-0016](0016-openapi-codegen-strategy.md)

## Context

The backend's OpenAPI surface uses `snake_case` for every wire-level field name (`amount_minor`, `customer_reference`, `created_at`, `card_token`, `from_status`, `to_status`, …). ASP.NET Core's default System.Text.Json policy is configured that way and the brief leaves the naming convention to the implementer; switching would touch every contract, every integration test fixture, every Postgres column-mapping, and every shipped trace and log line.

The frontend, by contrast, types its domain in idiomatic JS `camelCase` (`amountMinor`, `customerReference`, `createdAt`, …). The Phase 5 component code, hooks, and tests all reach for the camel form.

When Phase 5 was first wired up the MSW handlers handed the React tree already-camelCase fixtures, so unit tests passed and disguised the mismatch. The first live load against the running backend rendered `NaN.NaN` for every amount and em-dashes for every date — `amount_minor` is not the same key as `amountMinor`. That is the bug this ADR documents the fix for.

Three places the boundary could live: backend response serializer, generated client, or hand-written request wrapper. The backend serializer answer is the largest blast radius and would invalidate the OpenAPI snapshot we just committed. The generated-client answer requires a heavier codegen tool than `openapi-typescript` (we'd need a runtime client, see ADR-0016). The wrapper is one file we already own.

## Decision

The runtime wrapper `frontend/src/lib/api/client.ts` recursively converts request bodies from camelCase → snake_case on serialize, and recursively converts response bodies from snake_case → camelCase on parse. Both transforms are anchored to plain object literals (`value.constructor === Object`) so arrays of primitives, Dates, `null`, and other non-plain values pass through untouched.

Backend stays snake_case end-to-end. OpenAPI snapshot stays the source of truth. Frontend reads and writes the camel form everywhere else.

## Consequences

**Positive.** A single 30-line addition fixes the rendering bug without touching the backend, the OpenAPI snapshot, or any of the 40 frontend unit tests (the fixtures don't change shape under either transform — camel keys pass camel→snake→camel and stay camel). The schema-drift check in CI continues to enforce the snake_case contract; this transform is purely a presentation-layer convenience.

**Negative.** The transform is unaware of *intent*. It rewrites every plain-object key it sees — including user-supplied `metadata` maps and `payload` records on `PaymentEvent`. Today the dashboard reads metadata (`merchant`, `traceId`) but never writes one, so the rewrite goes one direction and the contract is honored. A future surface that accepts merchant-authored metadata keys (e.g. a "create payment" form, a webhook configurator) would round-trip those keys through the case transform and corrupt anything containing an underscore or a capital letter. That is a Phase 6+ concern that needs an opt-out: either skip nested objects under a known key list (`metadata`, `payload`), or move the transform out of the wrapper and into the typed query keys.

**Neutral.** The transform allocates new objects on every request and response. For dashboard traffic this is unmeasurable. For high-throughput surfaces it would be worth profiling — but a TanStack Query-fronted dashboard is not that surface.

## Alternatives

**Switch the backend to camelCase JSON.** Cheapest at the contract layer (one `JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase` line) but rewrites every integration-test fixture, every `payment_events` log line, every committed example in the README and ADRs, and bumps the OpenAPI snapshot. Considered and rejected as cosmetic churn with no business value.

**Switch the frontend to snake_case types.** Mechanically possible but reads as wrong-language code inside React components (`payment.amount_minor` everywhere). Rejected on readability grounds.

**Generate a typed client with built-in case translation.** Tools like `orval` and `kiota` can do this. Adopting one would supersede ADR-0016's "types-only, hand-written wrapper" decision and add a runtime dependency. Rejected as disproportionate to a 30-line fix.

**Manual mapping per endpoint.** Five endpoints × create/list/get/capture/refund × request and response = ten hand-written mappers, each of which silently rots when a field is added. Rejected as the highest-maintenance answer with the smallest blast radius if it goes wrong.
