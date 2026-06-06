# Phase 1 Build — Task 8 Resume Note

**Last session ended:** 2026-06-05, after Task 7 (Docker Compose) committed clean. Tasks 1–7 done. Task 8 (Integration tests with Testcontainers) is next.
**Branch:** `main`
**Latest commits (newest first):**
- `927c5bd` feat: add docker compose with postgres and api service
- `31a051c` feat: add api host with middleware pipeline and endpoints
- `6d8e0f3` feat: add infrastructure dependency injection and system clock
- `d2d52d7` feat: implement CreatePayment and GetPayment vertical slices
- `5988c25` test: add create payment command validator reproducers
- `7e734a9` feat: implement canonical json hashing and idempotency store
- `5dcc023` test: add canonical json reproducers for idempotency hashing
- `fcd38a4` feat: add EF Core DbContext, configurations, and initial migration
- `60f0212` feat: implement Phase 1 domain model
- `c9ec083` test: add domain model reproducers

(Confirm with `git log --oneline -10`.)

## How to resume in a fresh session

Relaunch Claude Code with the gate disabled, then paste:

> Resume Phase 1 build per `.claude/plans/payment-platform-phase-1.plan.md`. Tasks 1–7 are done and committed (latest `927c5bd`). Continue with **Task 8 (Integration tests: Testcontainers + WebApplicationFactory)**. Read this resume note first: `.claude/plans/payment-platform-phase-1-task-8-resume.md`. Working tree is clean except for `.claude/` and `Payment-Platform-Exercise.md` (both untracked and intentional). Load the `dotnet-skills:testcontainers` skill — it's essential for the fixture pattern.

Two ways to launch with the gate off:
- One-shot: `ECC_GATEGUARD=off claude` from a fresh terminal in the project directory.
- Persistent: add `pre:edit-write:gateguard-fact-force` to `ECC_DISABLED_HOOKS` in `~/.claude/settings.json` (keeps Bash gate intact).

## Where we are (state machine for the build)

| Task | Status | Notes |
|---|---|---|
| 1 — Scaffold solution + 7 projects | Done (`f489975`) | 7 projects target `net10.0`. SDK pinned 10.0.300. |
| 2 — Domain model with TDD | Done (`c9ec083` RED, `60f0212` GREEN) | 33 unit tests. |
| 3 — DbContext + initial migration | Done (`fcd38a4`) | Configurations, migration `20260605222437_Initial`, seed merchants. |
| 4 — Idempotency store with TDD | Done (`5dcc023` RED, `7e734a9` GREEN) | 12 CanonicalJson unit tests. |
| 5 — CreatePayment + GetPayment slices with TDD | Done (`5988c25` RED, `d2d52d7` GREEN) | 16 validator tests. |
| 6 — API host: Serilog, middleware, endpoints, health | Done (`6d8e0f3` + `31a051c`) | Middleware order: Exception → CorrelationId → DevBearerAuth → endpoints. |
| 7 — Docker Compose | Done (`927c5bd`) | postgres + api (port 5000:8080). **Not yet validated live** — Docker daemon was down. |
| 8 — Integration tests | **NEXT** | Resolves MSB3277 warning by pinning `EFCore.Relational 10.0.8` in IntegrationTests csproj. |
| 9 — README + acceptance | Pending | |

Test count: 61/61 unit tests passing. Build clean (0 errors; only the known MSB3277 EFCore.Relational warnings in IntegrationTests, fixed as part of Task 8).

The TaskList is in-memory and resets per session. On resume, create Task 8 sub-tasks and Task 9.

## Task 8 plan (the next thing to do)

**Goal:** Spin up a real Postgres via Testcontainers, boot the API via `WebApplicationFactory<Program>`, hit it with `HttpClient`, and assert end-to-end behavior. Includes verifying that `card_token` never appears in log output.

### Files to write (all new)

**Project file fix (1):**
- Edit `backend/tests/PaymentPlatform.IntegrationTests/PaymentPlatform.IntegrationTests.csproj`:
  - Add explicit `<PackageReference Include="Microsoft.EntityFrameworkCore.Relational" Version="10.0.8" />` to override the transitive 10.0.4 from `Microsoft.AspNetCore.Mvc.Testing`. This eliminates the MSB3277 warning.
  - Confirm these packages exist (add if missing): `Microsoft.AspNetCore.Mvc.Testing`, `Testcontainers.PostgreSql`, `xunit`, `xunit.runner.visualstudio`, `FluentAssertions`, `Microsoft.NET.Test.Sdk`, `coverlet.collector`.
  - Add `<InternalsVisibleTo>` is **not** needed — `CurrentMerchant.Set` is `internal` to the Api assembly, but tests call the API over HTTP, not directly.

**Fixtures (3):**
- `Fixtures/PostgresFixture.cs` — `IAsyncLifetime` that starts a single `PostgreSqlContainer` (image `postgres:16-alpine`) per test collection. Exposes the connection string via a property. Use `Testcontainers.PostgreSql.PostgreSqlBuilder` with image, database name `payments`, username/password `postgres`. Call `StartAsync` in `InitializeAsync`, `DisposeAsync` in `DisposeAsync`.

- `Fixtures/PaymentApiFactory.cs` — `WebApplicationFactory<Program>` parameterized on the fixture's connection string. Override `ConfigureWebHost`:
  1. Inject `ConnectionStrings:Payments` via `UseSetting` or `ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(...))`.
  2. Replace any registered `DbContextOptions<PaymentsDbContext>` so the test connection wins (or just let the config override flow — `AddInfrastructure` reads `GetConnectionString("Payments")` at startup, so config injection alone should work).
  3. Add an in-memory log sink for `LoggingTests` to read back. Register it via `services.Configure<...>` or override `UseSerilog` in the test host. Simplest: add a small `ITestSink` that captures formatted JSON lines into a `ConcurrentQueue<string>`, expose it via `factory.Services.GetRequiredService<ITestSink>()`.

- `Fixtures/IntegrationTestCollection.cs` — `[CollectionDefinition("Integration")]` referencing `ICollectionFixture<PostgresFixture>`. All test classes get `[Collection("Integration")]`.

**Test files (4):**

- `HealthTests.cs` — `[Collection("Integration")]`. One test: GET `/health/live` returns 200 with body `{"status":"alive"}`. No auth header required.

- `CreatePaymentTests.cs` — `[Collection("Integration")]`. Each test gets a fresh `HttpClient` from the factory.
  - `Returns201_WithPaymentId_OnHappyPath` — POST with `dev-key-mrc-acme` + Idempotency-Key + valid body → 201 + `pay_...` id + DB has one row.
  - `Returns200_WithSameBody_OnReplay` — same key/body twice → both responses byte-identical, still one row in DB.
  - `Returns400_WhenIdempotencyKeyMissing` — no header → 400 with envelope code `validation_failed` and the failure on `IdempotencyKey`.
  - `Returns401_WhenBearerMissing` — no Authorization header → 401 with envelope code `unauthorized`.
  - `Returns401_WhenBearerUnknown` — `Bearer junk` → 401.
  - `Returns400_WhenCurrencyInvalid` — `"usd"` lowercase → 400 with envelope code `validation_failed` and failure on `Currency`.
  - **Use a DB cleanup helper** between tests (or accept that the merchant seed + auto-increment IDs make this idempotent). Truncating `payments` and `idempotency_keys` in a per-test `IAsyncLifetime` is the safest choice — the resume note for Task 6 already locked in "test isolation matters."

- `GetPaymentTests.cs` — `[Collection("Integration")]`.
  - `Returns200_ForOwningMerchant` — POST as acme, GET as acme → 200 + same body.
  - `Returns404_ForOtherMerchant` — POST as acme, GET as pied → 404 with code `payment_not_found` (cross-tenant isolation).
  - `Returns404_ForUnknownId` — GET `pay_doesnotexist` → 404.
  - `Returns401_WhenBearerMissing` — no header → 401.

- `LoggingTests.cs` — `[Collection("Integration")]`.
  - `LogLines_AreSingleLineJson` — POST a payment, assert every captured log line is valid JSON via `JsonDocument.Parse`.
  - `LogLines_ContainTraceAndRequestId` — assert lines have `request_id` property (Serilog enricher) and at least one has `trace_id` (Activity).
  - `LogLines_NeverContainCardToken` — POST with `card_token: "tok_secret_xyz_12345"` → assert `tok_secret_xyz_12345` substring is absent from all captured log lines. This is the load-bearing assertion.

### Design decisions locked in (don't re-litigate)

1. **One container per test collection**, not per test. `[CollectionDefinition("Integration")]` with `ICollectionFixture<PostgresFixture>`. Cold start is ~20–30s, amortized across all tests.

2. **Migrations run on startup** via the existing `Database.Migrate()` in `Program.cs` (already gated to `IsDevelopment()`). The `WebApplicationFactory` defaults to `Development` environment unless overridden, so migrations will apply automatically. **Do not** also call `EnsureCreated` — it bypasses migrations and creates an unmanaged schema.

3. **Test DB isolation: truncate between tests, not full recreate.** Add a `ResetDatabaseAsync()` helper on the fixture that runs `TRUNCATE payments, idempotency_keys RESTART IDENTITY CASCADE;`. Merchants stay (they were seeded by the migration's `HasData`). Call it from each test class's `IAsyncLifetime.InitializeAsync` or a custom base class.

4. **WebApplicationFactory environment.** Use `factory.WithWebHostBuilder(b => b.UseEnvironment("Development"))` if needed, but default should be Development. **Do not** flip to Production — that would skip `Database.Migrate()`.

5. **In-memory log sink for `LoggingTests`.** Two reasonable approaches:
   - Custom Serilog sink (`ILogEventSink`) registered in the test host via `WithWebHostBuilder` → `UseSerilog((ctx, sp, cfg) => cfg.WriteTo.Sink(testSink))`. Captures `LogEvent`s; format them with `CompactJsonFormatter` to a `StringBuilder` for assertion.
   - **Or** redirect `Console.Out` to a `StringWriter` before the factory boots and read from it after. Simpler but flakier under parallel test execution.
   - Pick the sink approach. xUnit runs collection fixtures serially within a collection, so concurrency isn't an issue there.

6. **HTTP client setup.** `var client = factory.CreateClient();` per test. Add headers per request — don't bake the bearer into `DefaultRequestHeaders` because some tests exercise the no-auth path.

7. **FluentAssertions over xUnit asserts** — `response.Should().Be200Ok()` reads cleaner than `Assert.Equal(200, ...)`. The ECC C# testing rule explicitly recommends FluentAssertions.

### Packages to add to IntegrationTests csproj

If not already present:
```xml
<PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.0" />
<PackageReference Include="Microsoft.EntityFrameworkCore.Relational" Version="10.0.8" />  <!-- fixes MSB3277 -->
<PackageReference Include="Testcontainers.PostgreSql" Version="4.7.0" />  <!-- check latest -->
<PackageReference Include="FluentAssertions" Version="8.7.1" />  <!-- check latest free version -->
<PackageReference Include="xunit" Version="2.9.2" />
<PackageReference Include="xunit.runner.visualstudio" Version="3.1.7" />
<PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.0" />
<PackageReference Include="coverlet.collector" Version="6.0.4" />
```

Pin to current stable when running `dotnet add package`. **FluentAssertions 8+ went paid-license** — if that's a concern, use `Shouldly` instead or pin FluentAssertions to `7.x` (last fully free version). Plan §5 says FluentAssertions but didn't account for the license change; ask the user before paying or pinning.

### Validation gate (plan §8 for Task 8)

> `dotnet test` runs both projects green. First run includes 20–30s of Testcontainers cold start; subsequent runs reuse the container.

So after writing the files:
1. `cd backend && /bin/zsh -lc 'dotnet build --nologo'` — clean (0 warnings now that MSB3277 is fixed)
2. Ensure Docker is running (`docker info`)
3. `cd backend && /bin/zsh -lc 'dotnet test'` — both UnitTests and IntegrationTests green
4. Re-run to confirm hot path < 10s for IntegrationTests

### Suggested commit shape for Task 8

Two commits feels right for this size:
1. `chore: add testcontainers + mvc testing dependencies and pin efcore.relational`
2. `test: add integration tests for payments, health, and logging redaction`

Or one if you prefer atomicity:
`test: add integration tests with testcontainers postgres fixture`

## Decisions already locked in (do NOT re-litigate)

These were settled across the planning + Tasks 3–7. Don't re-ask, don't redesign:

1. **Backend:** ASP.NET Core 10 + EF Core 10 + Postgres. `.NET 10` LTS (SDK 10.0.300 installed).
2. **Solution file:** `PaymentPlatform.slnx` (new XML format introduced in .NET 9).
3. **API style:** **Minimal APIs**, not Controllers.
4. **IDs:** **ULID** (prefixed `pay_`, `mrc_`, `evt_`), not Guid.
5. **Integration tests:** **Testcontainers** (real Postgres in Docker), not in-memory SQLite.
6. **Idempotency in Phase 1:** Inline in the `CreatePayment` handler.
7. **Auth in Phase 1:** Dev bearer middleware. Static keys per merchant (`dev-key-mrc-acme`, `dev-key-mrc-pied`).
8. **Architecture:** Vertical Slice + MediatR. Feature folders own their command, handler, validator, endpoint.
9. **State machine:** Enum defines all 6 statuses; only `Pending` is reachable in Phase 1.
10. **Geographic scope:** US-only. Production target is multi-region active-passive.
11. **JSON wire format:** snake_case via `JsonNamingPolicy.SnakeCaseLower`. Enum values via `JsonStringEnumConverter` with snake_case naming policy.
12. **Error envelope shape:** `{ error: { code, message, details?, trace_id, request_id } }` — see `PaymentPlatform.Contracts.Common.ErrorEnvelope`.
13. **Status codes by exception:** ValidationException→400 (`validation_failed`), NotFoundException→404 (`ex.Code`), IdempotencyConflictException→409 (`idempotency_key_conflict`), DomainException→422 (`ex.Code`), other→500 (`internal_error`).
14. **Middleware order:** ExceptionHandling → CorrelationId → DevBearerAuth → endpoints. `/health/*` whitelisted in DevBearerAuth.
15. **Container port:** API binds `http://+:8080` inside; Compose maps to `5000:8080` externally per plan §2.

## Toolchain quirks (the gotchas)

1. **`dotnet` is not on PATH for the Bash tool's subshell.** Use `/bin/zsh -lc 'dotnet ...'` to get the login shell PATH.
2. **SDK 10.0.300 generated `.slnx`, not `.sln`.** New XML format. All `dotnet` subcommands work the same.
3. **NUlid is added to Domain** and transitively available in Api (used by `CorrelationIdMiddleware`).
4. **Fact-forcing gate hooks fire on Write/Edit.** Disable with `ECC_GATEGUARD=off` (one-shot) or add `pre:edit-write:gateguard-fact-force` to `ECC_DISABLED_HOOKS`.
5. **Cost warning hook fires aggressively** at $50+. Last session paused at $51.98 after Task 7. Resume in a fresh session for Task 8 to reset budget.
6. **`dotnet new tool-manifest` in .NET 10 puts the file at the repo root.** Move to `.config/dotnet-tools.json` if you scaffolded one (not needed for Task 8).
7. **Commit messages must NOT include Claude attribution** — user disabled it globally.
8. **`ValidationException` ambiguity** — `FluentValidation.ValidationException` and `PaymentPlatform.Application.Common.ValidationException` collide. `PaymentsEndpoints.cs` uses `using AppValidationException = ...` aliases. Tests should reference `PaymentPlatform.Application.Common.ValidationException` directly when needed (or use fully qualified name).
9. **Docker daemon was down through Tasks 6 and 7.** Task 8 NEEDS Docker (Testcontainers spawns containers). Confirm `docker info` works before running `dotnet test` on IntegrationTests.

## Skill firing schedule

| Task | Skill(s) | Notes |
|---|---|---|
| 8 — Integration tests | **`dotnet-skills:testcontainers`** | Essential — Testcontainers fixture pattern, container reuse via `[CollectionDefinition]`. Load this first. |
| 8 — (optional) | `ecc:csharp-testing` | Reference for xUnit + FluentAssertions patterns. Probably skip — the rules already cover it. |
| 9 — README | (none specific) | |

**Do NOT load `dotnet-skills:csharp-api-design`** — confirmed irrelevant in prior sessions (it's about library/NuGet API stability, not ASP.NET Core).

## Useful commands (paste-ready)

```bash
# Verify SDK
/bin/zsh -lc 'dotnet --version'           # 10.0.300

# Build
cd backend && /bin/zsh -lc 'dotnet build --nologo'

# Run unit tests only (fast, no Docker)
cd backend && /bin/zsh -lc 'dotnet test tests/PaymentPlatform.UnitTests'

# Run integration tests only (needs Docker)
cd backend && /bin/zsh -lc 'dotnet test tests/PaymentPlatform.IntegrationTests'

# Run all tests
cd backend && /bin/zsh -lc 'dotnet test'

# Check Docker is up before integration tests
docker info >/dev/null 2>&1 && echo "ok" || echo "start docker first"

# Run full stack via Compose (smoke-test Task 7)
docker compose down -v && docker compose up --build -d
curl http://localhost:5000/health/live
curl -H "Authorization: Bearer dev-key-mrc-acme" -H "Idempotency-Key: $(uuidgen)" \
     -H "Content-Type: application/json" \
     -d '{"amount_minor":100,"currency":"USD","card_token":"tok"}' \
     http://localhost:5000/v1/payments
docker compose down -v

# Check what's uncommitted
git status --short
```

## Files you'll touch in Task 8

- `backend/tests/PaymentPlatform.IntegrationTests/PaymentPlatform.IntegrationTests.csproj` (edit — add packages, pin EFCore.Relational)
- `backend/tests/PaymentPlatform.IntegrationTests/Fixtures/PostgresFixture.cs` (new)
- `backend/tests/PaymentPlatform.IntegrationTests/Fixtures/PaymentApiFactory.cs` (new)
- `backend/tests/PaymentPlatform.IntegrationTests/Fixtures/IntegrationTestCollection.cs` (new)
- `backend/tests/PaymentPlatform.IntegrationTests/Fixtures/InMemoryLogSink.cs` (new — Serilog test sink)
- `backend/tests/PaymentPlatform.IntegrationTests/HealthTests.cs` (new)
- `backend/tests/PaymentPlatform.IntegrationTests/CreatePaymentTests.cs` (new)
- `backend/tests/PaymentPlatform.IntegrationTests/GetPaymentTests.cs` (new)
- `backend/tests/PaymentPlatform.IntegrationTests/LoggingTests.cs` (new)

## Acceptance bar (Phase 1 plan §11, restated)

When all 9 tasks done, the reviewer should be able to:
1. `docker compose up` from a clean clone.
2. POST a payment → 201 with `pay_...` ID.
3. Repeat POST with same `Idempotency-Key` → identical response, 1 DB row.
4. GET as owning merchant → 200.
5. GET as other merchant → 404.
6. Every log line is one JSON object with `trace_id`, `request_id`, `merchant_id`, level.
7. No `card_token` value in any log line.
8. `/health/live` → 200.
9. `dotnet test` → green.

After Task 8, items 7 and 9 are mechanically enforced.
