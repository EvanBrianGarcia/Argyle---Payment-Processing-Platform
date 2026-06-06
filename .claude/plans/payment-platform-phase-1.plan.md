# Plan: Payment Platform — Phase 1 (Thin Vertical Slice)

**Source plan**: `.claude/plans/payment-platform.plan.md` (master plan §13, Phase 1)
**Goal**: Prove the spine works end-to-end with one persisted record.
**Complexity**: Medium (~1 day of focused work, ~6 projects, ~30 files)

## 1. Summary

Phase 1 builds the minimum viable backend: ASP.NET Core 10 + EF Core 10 + PostgreSQL on .NET 10 (LTS, support window to Nov 2028), with two endpoints (`POST /v1/payments`, `GET /v1/payments/{id}`), structured JSON logging, correlation IDs, dev-key bearer auth, a foundational idempotency mechanism, and `/health/live`. No state machine transitions, no async worker, no frontend — those land in later phases. Everything Phase 1 builds is foundational architecture later phases extend.

## 2. What "done" looks like (the acceptance walkthrough)

A reviewer cloning the repo cold should be able to:

1. `docker compose up` from a clean clone — API and Postgres come up green.
2. `POST /v1/payments` with a dev bearer key, `Idempotency-Key` header, and a JSON body returns `201` with a `pay_...` ULID and a single new DB row.
3. Repeating the same `POST` with the same `Idempotency-Key` returns the **same** response body; the DB still has exactly one row.
4. `GET /v1/payments/{id}` returns `200` with the payment, for the owning merchant.
5. The same `GET` from a *different* merchant's bearer key returns `404` — never a cross-tenant leak.
6. Every API log line is a single JSON object containing `trace_id`, `request_id`, `merchant_id`, level, and message.
7. `GET /health/live` returns `200`.
8. `dotnet test` runs and passes.

## 3. Skills to leverage

The `dotnet-skills` plugin is installed. Relevant skills for Phase 1:

| Skill | When |
|---|---|
| `dotnet-skills:project-structure` | Setting up the solution + 6 projects |
| `dotnet-skills:csharp-coding-standards` | All C# we write |
| `dotnet-skills:csharp-api-design` | Minimal API endpoint shape, validation, error envelope |
| `dotnet-skills:efcore-patterns` | DbContext config, value conversions, migrations |
| `dotnet-skills:testcontainers` | Integration test DB fixture |
| `dotnet-skills:dotnet-devcert-trust` | Local HTTPS if we wire it (optional in Phase 1) |

Skills NOT used in Phase 1 but referenced for later phases: `opentelementry-dotnet-instrumentation` (Phase 4), `aspire-service-defaults` (consider in Phase 4 retro).

## 4. Solution & project layout

Matches master plan §11. Phase 1 creates the structure; later phases fill the remaining feature folders.

```
/payment-platform
├── README.md
├── docker-compose.yml
├── .editorconfig
├── .gitignore
└── /backend
    ├── PaymentPlatform.sln
    ├── /src
    │   ├── /PaymentPlatform.Api               # ASP.NET Core 8 host, DI, middleware, endpoints
    │   ├── /PaymentPlatform.Application       # MediatR commands/handlers, validators, abstractions
    │   ├── /PaymentPlatform.Domain            # Payment aggregate, value objects, domain exceptions
    │   ├── /PaymentPlatform.Infrastructure    # EF Core DbContext, configurations, idempotency store
    │   └── /PaymentPlatform.Contracts         # Public DTOs (request/response shapes)
    └── /tests
        ├── /PaymentPlatform.UnitTests
        └── /PaymentPlatform.IntegrationTests
```

**Project references:**
- `Api` → `Application`, `Infrastructure`, `Contracts`
- `Application` → `Domain`, `Contracts`
- `Infrastructure` → `Application`, `Domain`
- `Contracts` → (none)
- `Domain` → (none)
- `UnitTests` → `Domain`, `Application`
- `IntegrationTests` → `Api`, `Application`, `Infrastructure`, `Contracts`

## 5. NuGet packages (target .NET 10)

All projects target `net10.0`.

| Project | Packages |
|---|---|
| `Api` | `Serilog.AspNetCore`, `Serilog.Sinks.Console`, `Serilog.Formatting.Compact`, `Serilog.Enrichers.Environment`, `MediatR` (host registers handlers) |
| `Application` | `MediatR`, `FluentValidation`, `FluentValidation.DependencyInjectionExtensions` |
| `Domain` | (none — pure C#) |
| `Infrastructure` | `Microsoft.EntityFrameworkCore` 10.x, `Microsoft.EntityFrameworkCore.Design` 10.x, `Npgsql.EntityFrameworkCore.PostgreSQL` 10.x (or latest compatible), `NUlid` |
| `Contracts` | (none) |
| `UnitTests` | `xunit`, `xunit.runner.visualstudio`, `FluentAssertions`, `Microsoft.NET.Test.Sdk` |
| `IntegrationTests` | same as `UnitTests` + `Microsoft.AspNetCore.Mvc.Testing`, `Testcontainers.PostgreSql` |

Pin to current stable versions when running `dotnet add package`. If a package's latest stable doesn't yet ship a `net10.0` target, fall back to the highest `net9.0` build — they run on .NET 10 fine.

## 6. Database schema (Phase 1 subset)

Only the three tables Phase 1 actually uses. `payment_events` (audit trail) lands in Phase 2; `payment_outbox` lands in Phase 3.

### `merchants`
| Column | Type | Notes |
|---|---|---|
| `id` | `text PK` | `mrc_...` ULID |
| `name` | `text NOT NULL` | |
| `api_key_hash` | `text NOT NULL UNIQUE` | SHA-256 of dev key |
| `created_at` | `timestamptz NOT NULL DEFAULT now()` | |

### `payments`
| Column | Type | Notes |
|---|---|---|
| `id` | `text PK` | `pay_...` ULID |
| `merchant_id` | `text NOT NULL FK → merchants(id)` | |
| `status` | `text NOT NULL` | `CHECK IN ('Pending','Authorized','Captured','Settled','Failed','Refunded')` |
| `amount_minor` | `bigint NOT NULL` | `CHECK > 0` |
| `currency` | `char(3) NOT NULL` | ISO 4217 |
| `customer_reference` | `text NULL` | |
| `card_token` | `text NOT NULL` | Stub token. Never logged. |
| `metadata` | `jsonb NOT NULL DEFAULT '{}'` | |
| `created_at` | `timestamptz NOT NULL DEFAULT now()` | |
| `updated_at` | `timestamptz NOT NULL DEFAULT now()` | |
| `version` | `int NOT NULL DEFAULT 0` | optimistic concurrency, used Phase 2+ |

Index: `(merchant_id, created_at DESC, id DESC)` — preps for Phase 2's list/search.

### `idempotency_keys`
| Column | Type | Notes |
|---|---|---|
| `merchant_id` | `text NOT NULL` | composite PK |
| `key` | `text NOT NULL` | composite PK |
| `request_hash` | `text NOT NULL` | SHA-256 of canonical request body |
| `response_status` | `int NOT NULL` | cached HTTP status |
| `response_body` | `jsonb NOT NULL` | cached response body |
| `created_at` | `timestamptz NOT NULL DEFAULT now()` | |

PK: `(merchant_id, key)`. Index on `created_at` for the future TTL sweep.

### Seed data (in migration `HasData`)
```
mrc_acme  Acme Corp    api_key_hash = sha256('dev-key-mrc-acme')
mrc_pied  Pied Piper   api_key_hash = sha256('dev-key-mrc-pied')
```

Two merchants so the cross-tenant isolation test in §9 is real.

## 7. Files to create (grouped by project)

### PaymentPlatform.Domain
- `Payments/Payment.cs` — aggregate root. `Create(merchantId, money, cardToken, customerReference, metadata)` factory returns a `Pending` payment. Read-only properties; mutations go through methods (none in Phase 1).
- `Payments/PaymentStatus.cs` — enum with all 6 values defined; only `Pending` reachable in Phase 1.
- `Payments/Money.cs` — value object: `AmountMinor` (long), `Currency` (string, validated to 3 uppercase letters). Throws `DomainException` on bad input.
- `Common/DomainException.cs` — base exception with `Code` and `Message`.
- `Common/IdGenerator.cs` — static helper wrapping `NUlid.Ulid.NewUlid()` and prefixing (`pay_`, `mrc_`, `evt_`).

### PaymentPlatform.Application
- `Abstractions/IPaymentsDbContext.cs` — DbContext interface with `DbSet<Payment>`, `DbSet<Merchant>`, `DbSet<IdempotencyKeyRecord>`, `SaveChangesAsync`.
- `Abstractions/IIdempotencyStore.cs` — `Task<IdempotencyOutcome> TryClaimAsync(merchantId, key, requestHash, ct)` and `Task WriteResponseAsync(...)`.
- `Abstractions/IClock.cs` — `DateTimeOffset UtcNow` (injectable for tests).
- `Abstractions/ICurrentMerchant.cs` — `string MerchantId` (per-request scoped, populated by auth middleware).
- `Features/CreatePayment/CreatePaymentCommand.cs` — `IRequest<CreatePaymentResponse>` with the fields from `CreatePaymentRequest` plus `IdempotencyKey`.
- `Features/CreatePayment/CreatePaymentCommandHandler.cs` — claim key → create payment → write response → commit, all in one transaction. On replay, return cached response.
- `Features/CreatePayment/CreatePaymentCommandValidator.cs` — FluentValidation: amount > 0, currency length 3 uppercase, card_token non-empty.
- `Features/GetPayment/GetPaymentQuery.cs` — `IRequest<PaymentResponse?>` with `PaymentId`.
- `Features/GetPayment/GetPaymentQueryHandler.cs` — `_db.Payments.Where(p => p.MerchantId == _currentMerchant.MerchantId && p.Id == query.PaymentId).FirstOrDefaultAsync()`.
- `Common/NotFoundException.cs`, `Common/ValidationException.cs`.

### PaymentPlatform.Infrastructure
- `Persistence/PaymentsDbContext.cs` — implements `IPaymentsDbContext`. Applies all configurations via `modelBuilder.ApplyConfigurationsFromAssembly`.
- `Persistence/Configurations/PaymentConfiguration.cs` — table name, ULID conversion (string ↔ Ulid), `PaymentStatus` enum-to-text conversion, JSONB for `metadata`, indexes.
- `Persistence/Configurations/MerchantConfiguration.cs` — table name, seed `HasData` rows.
- `Persistence/Configurations/IdempotencyKeyConfiguration.cs` — composite PK, JSONB for `response_body`.
- `Persistence/Migrations/<timestamp>_Initial.cs` — generated via `dotnet ef migrations add Initial`.
- `Idempotency/IdempotencyStore.cs` — implements `IIdempotencyStore`. Hash via SHA-256 of canonical JSON (keys sorted). Claim via insert + catch unique-violation → replay path.
- `Idempotency/CanonicalJson.cs` — deterministic JSON serialization for hashing.
- `Clock/SystemClock.cs` — `IClock` impl using `DateTimeOffset.UtcNow`.
- `DependencyInjection.cs` — `AddInfrastructure(IConfiguration)` extension.

### PaymentPlatform.Contracts
- `Payments/CreatePaymentRequest.cs` — `amount_minor`, `currency`, `card_token`, `customer_reference?`, `metadata?`.
- `Payments/CreatePaymentResponse.cs` — full `Payment` shape from master plan §6.
- `Payments/PaymentResponse.cs` — same shape (used by GET).
- `Common/ErrorEnvelope.cs` — `{ error: { code, message, details?, trace_id, request_id } }`.

### PaymentPlatform.Api
- `Program.cs` — host bootstrap. Serilog bootstrap logger. DI registration. Middleware pipeline. Endpoint registration. `Database.Migrate()` on startup (Dev only).
- `appsettings.json`, `appsettings.Development.json` — connection string, log levels.
- `Endpoints/PaymentsEndpoints.cs` — `MapPaymentsEndpoints(IEndpointRouteBuilder)` registering `POST /v1/payments` and `GET /v1/payments/{id}`. Both `RequireAuthorization()` (the dev bearer middleware enforces).
- `Endpoints/HealthEndpoints.cs` — `MapGet("/health/live", () => Results.Ok(new { status = "alive" }))`.
- `Middleware/CorrelationIdMiddleware.cs` — read `X-Request-Id` or generate one. Push to `Serilog.Context.LogContext`, `Activity.Current?.AddBaggage`, and the response header.
- `Middleware/DevBearerAuthMiddleware.cs` — read `Authorization: Bearer <token>`, SHA-256 it, look up merchant by `api_key_hash`, attach to `ICurrentMerchant`. On miss, return 401.
- `Middleware/ExceptionHandlingMiddleware.cs` — try/catch around `next(context)`. Map `ValidationException` → 400, `NotFoundException` → 404, `DomainException` → 422, anything else → 500. All emit the error envelope. Never leak internals.
- `Auth/CurrentMerchant.cs` — scoped `ICurrentMerchant` impl.
- `Serialization/JsonOptions.cs` — snake_case property naming, enum-as-string converter.
- `DependencyInjection.cs` — `AddApiServices()` extension.

### Tests — UnitTests
- `Payments/PaymentTests.cs` — `Create` happy path; `Create` throws on `amount_minor <= 0`; throws on bad currency; resulting payment has `PaymentStatus.Pending`.
- `Payments/MoneyTests.cs` — currency normalization, amount validation.
- `Idempotency/CanonicalJsonTests.cs` — same body → same hash; reordered keys → same hash; nested objects normalized.

### Tests — IntegrationTests
- `Fixtures/PostgresFixture.cs` — `IAsyncLifetime` starting a `PostgreSqlContainer` once per test collection. Exposes connection string.
- `Fixtures/PaymentApiFactory.cs` — `WebApplicationFactory<Program>` that overrides the DB connection to point at the container's port and applies migrations on startup.
- `Fixtures/IntegrationTestCollection.cs` — `[CollectionDefinition]` to share the fixture.
- `CreatePaymentTests.cs` — 201 happy path; same-key replay returns identical body; missing `Idempotency-Key` → 400; missing bearer → 401; bad currency → 400 validation error.
- `GetPaymentTests.cs` — 200 own-merchant; 404 cross-merchant; 404 unknown ID; 401 no bearer.
- `LoggingTests.cs` — captures stdout, asserts one JSON line per request, contains `trace_id` and `request_id`, **does not contain** the `card_token` value.
- `HealthTests.cs` — `/health/live` returns 200 with body `{"status":"alive"}`.

### Repo root
- `docker-compose.yml` — `postgres:16-alpine` with healthcheck, `api` service depending on Postgres healthy. Maps API on port `5000`.
- `backend/src/PaymentPlatform.Api/Dockerfile` — multi-stage build (SDK → restore → publish → runtime image).
- `.editorconfig` — standard C# formatting + `csharp_new_line_before_open_brace = all`.
- `.gitignore` — standard .NET + macOS + JetBrains.
- `README.md` — prereqs, `docker compose up`, sample curl commands, dev bearer keys, troubleshooting.

## 8. Task order (the build sequence)

Each task ends with a validation step. **Do not start the next task until the previous validates.** This is what "thin vertical slice" actually buys you — fail fast at each layer.

### Task 1 — Skeleton solution (30 min)
Create `.sln`, 6 projects with correct references, add NuGet packages, `.editorconfig`.
**Validate:** `dotnet build` succeeds. Solution opens in IDE without errors.

### Task 2 — Domain model + unit tests (45 min)
`Payment`, `Money`, `PaymentStatus`, `DomainException`, `IdGenerator`. Tests for `Payment.Create` happy + invariant paths.
**Validate:** `dotnet test PaymentPlatform.UnitTests` passes.

### Task 3 — Infrastructure: DbContext + configurations + initial migration (1 hr)
`PaymentsDbContext`, three configurations, value conversions for ULID and enum. Generate `Initial` migration. Seed merchants via `HasData`.
**Validate:** Start a local `postgres:16` container, run `dotnet ef database update`, inspect schema with `psql \d+ payments` — columns and indexes match §6. Two merchant rows present.

### Task 4 — Idempotency store (45 min)
`IIdempotencyStore`, `IdempotencyStore` impl, `CanonicalJson`. Unit tests for hashing.
**Validate:** Unit tests pass. Manual: call `TryClaimAsync` twice in a row from a throwaway script, second call returns `Replay`.

### Task 5 — Application: CreatePayment + GetPayment slices (1.5 hr)
MediatR command/query/handler/validator. Handler wraps work in a single DB transaction. Replay path returns the cached body verbatim.
**Validate:** No integration tests yet — just `dotnet build` clean and reading the handler code with a senior engineer's eye. Look for: explicit transaction scope, no `.ToList()` before filter, merchant_id filter on GET.

### Task 6 — API host: Serilog, middleware, endpoints, health (1.5 hr)
`Program.cs`, all three middlewares, both endpoint groups, JSON options, error envelope mapping.
**Validate:** `dotnet run` against a local Postgres. `curl /health/live` returns 200. Log output is single-line JSON. `POST /v1/payments` without bearer returns 401 with the error envelope.

### Task 7 — Docker Compose (30 min)
Postgres + API services. Healthcheck on Postgres. API waits for healthy.
**Validate:** Fresh `docker compose down -v && docker compose up --build` — API container comes up healthy and serves the same curl flow as Task 6.

### Task 8 — Integration tests (1.5 hr)
Testcontainers fixture, `WebApplicationFactory`, the 4 test files listed in §7.
**Validate:** `dotnet test` runs both projects green. First run includes 20–30s of Testcontainers cold start; subsequent runs reuse the container.

### Task 9 — README + acceptance walkthrough (30 min)
Document setup, dev keys, sample curls, troubleshooting (e.g., port 5000 in use, Docker not running). Walk through §2 acceptance manually.
**Validate:** A teammate (or you in a fresh shell) follows the README and gets to step 7 of §2 without asking questions.

**Total estimated time: ~8 hours of focused work.** This matches the "1 day" estimate in the master plan with realistic buffer for unfamiliar tooling.

## 9. Validation commands (the "did Phase 1 work" runbook)

```bash
# From repo root
docker compose up -d
docker compose logs api | head -10                       # JSON log lines visible

# 1. Create a payment
RESP=$(curl -s -X POST http://localhost:5000/v1/payments \
  -H "Authorization: Bearer dev-key-mrc-acme" \
  -H "Idempotency-Key: $(uuidgen)" \
  -H "Content-Type: application/json" \
  -d '{"amount_minor":12500,"currency":"USD","card_token":"tok_stub_visa","customer_reference":"order-1"}')
echo "$RESP" | jq .
PAYMENT_ID=$(echo "$RESP" | jq -r .id)

# 2. Fetch it back
curl -s http://localhost:5000/v1/payments/$PAYMENT_ID \
  -H "Authorization: Bearer dev-key-mrc-acme" | jq .

# 3. Idempotent replay — same key + same body → same response, no second row
KEY=$(uuidgen)
RESP1=$(curl -s -X POST http://localhost:5000/v1/payments \
  -H "Authorization: Bearer dev-key-mrc-acme" \
  -H "Idempotency-Key: $KEY" \
  -H "Content-Type: application/json" \
  -d '{"amount_minor":5000,"currency":"USD","card_token":"tok_stub_visa"}')
RESP2=$(curl -s -X POST http://localhost:5000/v1/payments \
  -H "Authorization: Bearer dev-key-mrc-acme" \
  -H "Idempotency-Key: $KEY" \
  -H "Content-Type: application/json" \
  -d '{"amount_minor":5000,"currency":"USD","card_token":"tok_stub_visa"}')
diff <(echo "$RESP1") <(echo "$RESP2")                   # should be no output

# 4. Cross-merchant isolation — should be 404
curl -i http://localhost:5000/v1/payments/$PAYMENT_ID \
  -H "Authorization: Bearer dev-key-mrc-pied"

# 5. Auth failure — should be 401 with error envelope
curl -i http://localhost:5000/v1/payments/$PAYMENT_ID

# 6. Health
curl http://localhost:5000/health/live

# 7. Tests
cd backend && dotnet test
```

## 10. Risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| EF Core ULID value conversion mis-mapped — IDs round-trip wrong | Medium | High | Integration test writes then reads, asserts equality on the ID. |
| Idempotency race: two concurrent POSTs with same key both claim | Medium | High | Unique constraint on `(merchant_id, key)` is the hard guarantee. Loser catches `DbUpdateException` (Npgsql code `23505`) and falls into the replay branch. Integration test simulates concurrency. |
| Forgetting `merchant_id` filter on GET → cross-merchant data leak | Low | Critical | All queries go through `IPaymentsDbContext` with merchant filtering explicit in the handler. Integration test asserts the cross-merchant 404 case. |
| Migration drift between dev and the checked-in migration | Low | Medium | Migration files are checked in. `Database.Migrate()` runs on startup (Dev). Consider a CI check that `dotnet ef migrations has-pending-model-changes` returns clean. |
| `card_token` accidentally appears in a log line | Medium | High | Phase 1 rule: never log the full request body. Log only the fields we explicitly include in our structured log message (no `card_token`). `LoggingTests.cs` greps captured output to enforce. |
| Testcontainers cold start makes `dotnet test` feel broken | High | Low | Document the ~20–30s first-run cost in README. Use `[Collection]` to share one container per test session. |
| Snake_case JSON config not applied to enum values | Medium | Low | Configure `JsonStringEnumConverter` with `JsonNamingPolicy.SnakeCaseLower` (or keep Pascal for `status` and document it). Integration test asserts the on-wire shape. |
| Docker `depends_on: condition: service_healthy` not respected in older Compose | Low | Medium | Specify Compose file version `3.8`+ and document `docker compose` (not `docker-compose`). Add an API-side retry on initial DB connect. |

## 11. Acceptance criteria

- [ ] Solution builds clean (`dotnet build`).
- [ ] All unit + integration tests pass (`dotnet test`).
- [ ] `docker compose up` produces a working API from a clean clone.
- [ ] `POST /v1/payments` returns 201 with a `pay_...` ID and persists exactly one row.
- [ ] Same `Idempotency-Key` + same body returns identical response, still one row in DB.
- [ ] `GET /v1/payments/{id}` returns 200 for the owning merchant.
- [ ] `GET /v1/payments/{id}` returns 404 for any other merchant (no leak).
- [ ] Missing `Idempotency-Key` on POST returns 400 with the error envelope.
- [ ] Missing or invalid bearer token returns 401 with the error envelope.
- [ ] `/health/live` returns 200.
- [ ] Every API log line is single-line JSON with `trace_id`, `request_id`, `merchant_id` (where applicable), level, message.
- [ ] No `card_token` value appears in any captured log line.
- [ ] README walks a teammate through setup → curl → green tests without questions.

## 12. What's intentionally NOT in Phase 1

| Out | Lands in |
|---|---|
| Lifecycle transitions (Authorized / Captured / Settled / Failed / Refunded) | Phase 2 |
| `POST /capture`, `POST /refund`, `GET /v1/payments` (list + filter) | Phase 2 |
| `payment_events` audit table | Phase 2 |
| Cross-cutting idempotency middleware (Phase 1 uses inline-in-handler) | Phase 2 |
| RabbitMQ, settlement worker, `payment_outbox` table | Phase 3 |
| OpenTelemetry tracing + Prometheus metrics + `/health/ready` rich checks | Phase 4 |
| React + Vite frontend dashboard | Phase 5 |
| OAuth2 / OIDC (Phase 1 uses dev bearer scheme) | Production (master plan §16) |
| Read-replica routing, Redis idempotency cache, rate limiting | Production (master plan §16) |
| Multi-region deployment artifacts | Production (master plan §16) |

## 13. Notes for the implementer (future me / Claude)

- **Minimal APIs, not Controllers.** Vertical slice + Minimal APIs is the cleanest .NET 10 pairing. Each feature folder owns its endpoint registration via an extension method.
- **MediatR is the seam between the endpoint and the handler.** Endpoint code should be ~5 lines: pull headers, build command, send via `IMediator`, map result to `Results.*`.
- **`Program.cs` is the only place that knows about Postgres.** Application + Domain never reference EF Core types.
- **No service locator.** Everything via constructor injection. `ICurrentMerchant`, `IClock`, `IPaymentsDbContext`, `IIdempotencyStore` all injectable for tests.
- **Snake_case on the wire, PascalCase in code.** Configure JSON options once in `Program.cs`. Property names in DTOs stay PascalCase; the serializer converts.
- **Don't optimize what doesn't exist yet.** No caching, no Polly, no Mapster. Phase 1 is the spine.

---

**WAITING FOR CONFIRMATION**: Proceed with this Phase 1 plan? (yes / no / modify)
