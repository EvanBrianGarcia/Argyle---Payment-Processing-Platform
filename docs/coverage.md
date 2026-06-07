# Test Coverage

Last captured: 2026-06-06.

## Backend (.NET 10)

`dotnet test PaymentPlatform.slnx --collect:"XPlat Code Coverage"`

| Suite               | Tests       | Lines                          | Branches |
| ------------------- | ----------- | ------------------------------ | -------- |
| UnitTests           | 161 / 161   | 20.21% (644 / 3186)            | 48.17%   |
| IntegrationTests    | 102 / 103¹  | 85.48% (3715 / 4346)           | 52.89%   |
| **Combined**        | **263**     | weighted ~58%                  | —        |

¹ One MassTransit harness test is skipped — it needs a real RabbitMQ at the
in-memory bus address and is exercised end-to-end in CI instead.

The unit-test suite reports a low line rate because it covers the pure
domain layer (`Payment`, `Money`, value objects, validators); the
integration suite is the one that drives controllers, EF Core, the
outbox dispatcher, and the worker over a real Postgres + RabbitMQ via
Testcontainers — that's where most of the surface area lives. Look at
the combined picture, not either number in isolation.

## Frontend (Vite + React + TypeScript)

`cd frontend && pnpm test:coverage`

| Metric     | Result    | Threshold (vitest.config.ts) |
| ---------- | --------- | ---------------------------- |
| Tests      | 40 / 40   | —                            |
| Lines      | 78.08%    | ≥ 70%                        |
| Branches   | 82.75%    | ≥ 65%                        |
| Functions  | 79.48%    | ≥ 70%                        |
| Statements | 78.08%    | ≥ 70%                        |

Coverage gates are enforced in CI. Threshold is set in
[`vitest.config.ts`](../frontend/vitest.config.ts) and is intentionally
above the 70% floor cited in
[ADR-0014](adr/0014-frontend-stack.md).

## E2E (Playwright)

`cd frontend && pnpm e2e`

| Test                                              | Status   |
| ------------------------------------------------- | -------- |
| payments list → detail happy path                 | passing  |
| axe-core has no serious/critical violations       | passing  |

Run against a live backend (CI brings one up; locally use
`docker compose up -d`).
