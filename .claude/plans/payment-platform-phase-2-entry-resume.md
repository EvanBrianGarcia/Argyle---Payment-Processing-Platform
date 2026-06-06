# Phase 2 Entry Resume Note

**Phase 1 status:** Complete. Acceptance §2 steps 1–7 all walked against live `docker compose` stack on 2026-06-05; 77/77 tests green (61 unit + 16 integration). Latest commit `c62c064`.

## What's done

Phase 1 deliverables — `POST /v1/payments`, `GET /v1/payments/{id}`, idempotent replay, dev bearer auth with hashed seed keys, JSON logs carrying `request_id` + `merchant_id`, `X-Request-Id` header, `card_token` redaction, `/health/live`, EF Core + Postgres migration, Testcontainers integration tests, Docker Compose.

See [`payment-platform-phase-1.plan.md`](./payment-platform-phase-1.plan.md) for the full task breakdown and what each task shipped.

## Phase 1 polish bugs to address in Phase 2

These don't break Phase 1 acceptance — the README, the §2 acceptance walkthrough, and the 77 tests all pass with them present. Fix during Phase 2 because Phase 2 touches the same surfaces.

1. **`X-Request-Id` is missing on 400 / 404 / 409 / 422 / 500 error responses.**
   `ExceptionHandlingMiddleware.WriteAsync` calls `context.Response.Clear()` (`backend/src/PaymentPlatform.Api/Middleware/ExceptionHandlingMiddleware.cs:101`) which wipes every header `CorrelationIdMiddleware` set. The `request_id` is still in the response body, so clients can correlate, but the header convention is broken for errors. The 401 path inside `DevBearerAuthMiddleware` does NOT call `Clear()` and keeps the header, which is why the asymmetry exists.
   **Fix:** after `Response.Clear()`, re-set the header: `context.Response.Headers[CorrelationIdMiddleware.RequestIdHeader] = context.Items[CorrelationIdMiddleware.RequestIdItemKey] as string ?? string.Empty;`. Add an integration test that asserts the header is present on a 404 response.

2. **`UseSerilogRequestLogging()` is outside the `request_id` scope.**
   `Program.cs:36` registers `UseSerilogRequestLogging()` BEFORE `UseMiddleware<CorrelationIdMiddleware>()` at line 38. The canonical Serilog "HTTP request handled" log line emits AFTER `_next` returns, by which time `CorrelationIdMiddleware`'s `LogContext.PushProperty("request_id", ...)` scope has disposed. The line is missing `request_id`. Other in-handler log lines emitted from inside the request still carry it.
   **Fix:** swap the order so `UseSerilogRequestLogging()` runs INSIDE the correlation scope.

3. **`trace_id` is not in regular log lines, only in error envelopes.**
   `Activity.Current.TraceId` is set per-request by ASP.NET Core but Serilog does not see it without `Serilog.Enrichers.Span` (or equivalent). Acceptance §2 step 6 ("Every API log line is a single JSON object containing trace_id, request_id, merchant_id, level, and message") is technically not satisfied for `trace_id`. The compromise was documented in the README — this lands properly in Phase 4 with OpenTelemetry. If Phase 2 wants to fix it sooner, add `Serilog.Enrichers.Span` and enable it via `.Enrich.WithSpan()` in the `UseSerilog` pipeline.

## Phase 2 plan summary (from master plan §13)

**Goal:** All payment lifecycle endpoints working with audit events.

Deliverables:
- `Payment` aggregate enforces transitions (`Pending → Authorized → Captured → Settled`, plus `Failed` / `Refunded` terminal branches)
- `payment_events` table populated on every state change (the audit trail mentioned in exercise §4.4)
- `POST /v1/payments/{id}/capture`
- `POST /v1/payments/{id}/refund`
- `GET /v1/payments` with cursor pagination and status filter
- Idempotency fully wired on every state-changing endpoint, not just create
- Optimistic concurrency via the existing `payments.version` column

Validation: run the full state machine end-to-end via curl. Inspect `payment_events` to see a clean transition log. Re-send a capture with the same key → identical response, no second event.

The master plan estimates ~1 day of focused work. Suggest writing a Phase 2 task plan as the first step (mirror `payment-platform-phase-1.plan.md`'s structure).

## Open architectural decisions Phase 2 should record

These are flagged in the master plan but not yet decided:

- **Where state transitions live.** Inline in the aggregate vs. a dedicated `IPaymentStateMachine` service. The master plan implies aggregate-owned. Confirm and document the choice in an ADR before writing the first handler.
- **How `payment_events` participates in transactions.** Insert in the same transaction as the `payments` update (recommended — atomic audit) vs. outbox-deferred. Outbox arrives in Phase 3 for queue dispatch, but events are a domain audit, not a queue message. Default to in-transaction.
- **Idempotency for state-change endpoints.** Capture / refund need their own idempotency keys (separate from create's). Decide whether to scope keys per-endpoint or share one namespace. Master plan §10 leans toward per-endpoint.

## Where things live (for first orientation in Phase 2)

- Vertical slices: `backend/src/PaymentPlatform.Application/Features/<FeatureName>/`. Mirror `CreatePayment` and `GetPayment` for `CapturePayment`, `RefundPayment`, `ListPayments`.
- Endpoint registration: `backend/src/PaymentPlatform.Api/Endpoints/PaymentsEndpoints.cs`.
- EF Core entity configs: `backend/src/PaymentPlatform.Infrastructure/Persistence/Configurations/`.
- Domain aggregate: `backend/src/PaymentPlatform.Domain/Payments/` — currently shape-only, no transition logic.
- Migration command (run from `backend/`): `dotnet ef migrations add <Name> --project src/PaymentPlatform.Infrastructure --startup-project src/PaymentPlatform.Api`.
- Integration tests: see `backend/tests/PaymentPlatform.IntegrationTests/Fixtures/IntegrationTestBase.cs` for the per-test reset pattern.

## Toolchain reminders (carried forward)

1. `dotnet` not on Bash subshell PATH — use `/bin/zsh -lc 'dotnet ...'`.
2. No Claude attribution in commits — user disabled globally.
3. Docker Desktop must be running for integration tests. Confirm with `docker info`.
4. Cost-warning hook fires at $50+ — pause and ask before continuing.
5. macOS host already has native PostgreSQL 12 on `:5432` and AirPlay Receiver on `:5000`. `docker compose up` uses overridable host ports: `POSTGRES_HOST_PORT=15432 API_HOST_PORT=5050 docker compose up -d`. Defaults are 5432 / 5000 so fresh machines work without env vars.
6. Testcontainers fixture sets `POSTGRES_HOST_AUTH_METHOD=trust` and the API factory injects `ConnectionStrings__Payments` via env var (NOT `IWebHostBuilder.ConfigureAppConfiguration` — that path is unreliable in the WebApplication minimal-hosting model when the same key exists in `appsettings.json`).
