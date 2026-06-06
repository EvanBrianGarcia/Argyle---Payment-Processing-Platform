# Phase 1 Build — Resume Note

**Last session ended:** 2026-06-05, mid-Task 6 (API host). Tasks 3, 4, 5 are done and committed; Task 6 was paused **before any files were written** to preserve session budget after a cost spike to $138.98.
**Branch:** `main`
**Latest commits (newest first):**
- `d2d52d7` feat: implement CreatePayment and GetPayment vertical slices
- `5988c25` test: add create payment command validator reproducers
- `7e734a9` feat: implement canonical json hashing and idempotency store
- `5dcc023` test: add canonical json reproducers for idempotency hashing
- `fcd38a4` feat: add EF Core DbContext, configurations, and initial migration
- `60f0212` feat: implement Phase 1 domain model
- `c9ec083` test: add domain model reproducers
- `6e231f0` chore: add NUlid to Domain for IdGenerator
- `f489975` chore: scaffold Phase 1 solution and 7 projects on net10.0
- `deefba6` Initial commit

(Confirm with `git log --oneline -10`.)

## How to resume in a fresh session

Relaunch Claude Code with the gate disabled, then paste:

> Resume Phase 1 build per `.claude/plans/payment-platform-phase-1.plan.md`. Tasks 1–5 are done and committed. Continue with **Task 6 (API host: Serilog, middleware, endpoints, health)**. Read this resume note first: `.claude/plans/payment-platform-phase-1-resume.md`. Working tree is clean. Skip the `dotnet-skills:csharp-api-design` skill — it's about library API stability, not ASP.NET Core APIs.

Two ways to launch with the gate off:
- One-shot: `ECC_GATEGUARD=off claude` from a fresh terminal in the project directory.
- Persistent: add `pre:edit-write:gateguard-fact-force` to `ECC_DISABLED_HOOKS` in `~/.claude/settings.json` (keeps Bash gate intact).

## Where we are (state machine for the build)

| Task | Status | Notes |
|---|---|---|
| 1 — Scaffold solution + 7 projects | Done (`f489975`) | 7 projects target `net10.0`. SDK pinned 10.0.300. |
| 2 — Domain model with TDD | Done (`c9ec083` RED, `60f0212` GREEN) | 33 unit tests. |
| 3 — DbContext + initial migration | Done (`fcd38a4`) | All 3 configurations, migration `20260605222437_Initial`, seed merchants. Added private parameterless ctor to `Payment` for EF backing-field access. |
| 4 — Idempotency store with TDD | Done (`5dcc023` RED, `7e734a9` GREEN) | 12 CanonicalJson unit tests added. |
| 5 — CreatePayment + GetPayment slices with TDD | Done (`5988c25` RED, `d2d52d7` GREEN) | 16 validator tests. Handler does atomic save + DbUpdateException race fallback. |
| 6 — API host: Serilog, middleware, endpoints, health | **NEXT** | Nothing written yet. See "Task 6 plan" below. |
| 7 — Docker Compose | Pending | |
| 8 — Integration tests | Pending | Resolves MSB3277 warning by pinning `EFCore.Relational 10.0.8` in IntegrationTests csproj. |
| 9 — README + acceptance | Pending | |

Test count: 61/61 unit tests passing. Build clean (only the known MSB3277 EFCore.Relational warnings — fix in Task 8).

The TaskList is in-memory and resets per session. On resume, create Tasks 6–9, mark 1–5 completed.

## Task 6 plan (the next thing to do)

**Goal:** Wire the slices into ASP.NET Core 10 with auth, correlation, error mapping, and structured logging.

### Files to write (all new — none exist yet)

**Infrastructure (2):**
- `backend/src/PaymentPlatform.Infrastructure/Clock/SystemClock.cs` — `IClock` impl: `DateTimeOffset.UtcNow`.
- `backend/src/PaymentPlatform.Infrastructure/DependencyInjection.cs` — `AddInfrastructure(IConfiguration)`: registers `PaymentsDbContext` (UseNpgsql with `ConnectionStrings:Payments`), `IPaymentsDbContext` → same instance, `IIdempotencyStore` → `IdempotencyStore` (scoped), `IClock` → `SystemClock` (singleton).

**Api (~10):**
- `Auth/CurrentMerchant.cs` — concrete impl of `ICurrentMerchant` with an internal `Set(merchantId)` method. Register as scoped, register `ICurrentMerchant` → same instance.
- `Middleware/CorrelationIdMiddleware.cs` — read `X-Request-Id` header or generate ULID; push `request_id` to `Serilog.Context.LogContext`; echo to response header. Add `merchant_id` to context once auth fills it in (or in the auth middleware — see below).
- `Middleware/DevBearerAuthMiddleware.cs` — skip if path starts with `/health/`. Otherwise read `Authorization: Bearer <token>`, SHA-256 + lowercase hex, `SELECT m FROM merchants WHERE api_key_hash = @hash`, populate `CurrentMerchant.Set(id)`, push `merchant_id` to `LogContext`. On missing/unknown token: write 401 with `ErrorEnvelope` and short-circuit.
- `Middleware/ExceptionHandlingMiddleware.cs` — try/catch around `next(context)`. Exception → status mapping:
  - `ValidationException` → 400, code `validation_failed`, `details` = `Failures`
  - `NotFoundException` → 404, code = `ex.Code`
  - `IdempotencyConflictException` → 409, code `idempotency_key_conflict`
  - `DomainException` → 422, code = `ex.Code`
  - anything else → 500, code `internal_error`, log full exception with structured properties
  All emit `ErrorEnvelope` JSON with `trace_id` from `Activity.Current?.TraceId.ToString()` and `request_id` from `HttpContext.Items["request_id"]`. **Never** leak stack traces or `card_token` values.
- `Endpoints/HealthEndpoints.cs` — `MapHealthEndpoints(IEndpointRouteBuilder)` registering `GET /health/live` → 200 `{"status":"alive"}`. No auth.
- `Endpoints/PaymentsEndpoints.cs` — `MapPaymentsEndpoints(IEndpointRouteBuilder)` with a `/v1/payments` group:
  - `POST /` — handler reads `[FromBody] CreatePaymentRequest`, `[FromHeader(Name="Idempotency-Key")] string?`, builds `CreatePaymentCommand`, runs `IValidator<CreatePaymentCommand>.ValidateAsync` (throws `ValidationException` on failure), `mediator.Send`, returns `Results.Created($"/v1/payments/{id}", response)`.
  - `GET /{id}` — `mediator.Send(new GetPaymentQuery(id))`. If null → throw `NotFoundException("payment_not_found", ...)`. Else `Results.Ok`.
- `Serialization/JsonOptions.cs` — static helper that configures `JsonSerializerOptions`: `PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower`, `PropertyNameCaseInsensitive = true`, `JsonStringEnumConverter` for enum-as-string. Or just configure inline in `ConfigureHttpJsonOptions` — either works.
- `DependencyInjection.cs` — `AddApiServices(IServiceCollection)`:
  - `AddHttpContextAccessor()`
  - `AddScoped<CurrentMerchant>()` + `AddScoped<ICurrentMerchant>(sp => sp.GetRequiredService<CurrentMerchant>())`
  - `AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(CreatePaymentCommand).Assembly))`
  - `AddValidatorsFromAssemblyContaining<CreatePaymentCommandValidator>()`
  - `ConfigureHttpJsonOptions(...)` with snake_case + enum converter
- `Program.cs` — replace the `Hello World!` stub:
  ```
  Serilog bootstrap logger
  builder.Host.UseSerilog((ctx, sp, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration).Enrich.FromLogContext().WriteTo.Console(new CompactJsonFormatter()))
  builder.Services.AddApiServices()
  builder.Services.AddInfrastructure(builder.Configuration)
  if (Dev) Database.Migrate()
  app.UseSerilogRequestLogging()
  app.UseMiddleware<ExceptionHandlingMiddleware>()
  app.UseMiddleware<CorrelationIdMiddleware>()
  app.UseMiddleware<DevBearerAuthMiddleware>()
  app.MapHealthEndpoints()
  app.MapPaymentsEndpoints()
  app.Run()
  public partial class Program { }  // needed for WebApplicationFactory in Task 8
  ```
- Update `appsettings.json` — add `"ConnectionStrings": { "Payments": "Host=localhost;Port=5432;Database=payments;Username=postgres;Password=postgres" }` and a `Serilog` section if needed.

### Design decisions locked in (don't re-litigate)

1. **Dev auth is plain middleware**, not an `AuthenticationHandler`. No `[Authorize]` / `RequireAuthorization()`. Health endpoints are whitelisted by path prefix in the middleware itself.
2. **Endpoint missing-header check is the validator's job.** Don't add a separate `if (string.IsNullOrWhiteSpace(idempotencyKey))` in the endpoint — pass `idempotencyKey ?? ""` to the command, let `CreatePaymentCommandValidator` fail it → `ValidationException` → 400. Consistent with all other field validation.
3. **`GetPaymentQuery` stays `IRequest<PaymentResponse?>`.** The endpoint converts null → `NotFoundException`. Don't change the handler to throw.
4. **`Database.Migrate()` only in Development**, gated by `app.Environment.IsDevelopment()`. Docker Compose sets `ASPNETCORE_ENVIRONMENT=Development` for Phase 1 simplicity (master plan §16 covers prod migration strategy).
5. **Middleware order:** `ExceptionHandling` → `CorrelationId` → `DevBearerAuth` → endpoints. Exception handler outermost so it catches everything, including auth failures. Correlation before auth so 401 responses get a `request_id`.
6. **Logging:** `Serilog.Formatting.Compact.CompactJsonFormatter` for single-line JSON. Push `request_id` and `merchant_id` via `LogContext.PushProperty` in middleware. **Never log `card_token`** — handler only logs structured `payment_id` + `merchant_id`. Request logging via `app.UseSerilogRequestLogging()` is fine as long as it doesn't dump the request body (it doesn't by default).
7. **JSON snake_case** via `JsonNamingPolicy.SnakeCaseLower` (built-in since .NET 9). Wire format matches the schema field names; in-code stays PascalCase.

### Packages — API csproj already has everything

Verified in current `PaymentPlatform.Api.csproj`:
- `Serilog.AspNetCore 10.0.0`
- `Serilog.Enrichers.Environment 3.0.1`
- `Serilog.Formatting.Compact 3.0.0`
- `Serilog.Sinks.Console 6.1.1`
- `MediatR 14.1.0`
- Project refs to Application, Infrastructure, Contracts

`FluentValidation.DependencyInjectionExtensions` flows transitively from Application (verified). If `AddValidatorsFromAssemblyContaining` isn't resolvable, add the package explicitly to the API csproj.

### Validation gate (plan §8 for Task 6)

> `dotnet run` against a local Postgres. `curl /health/live` returns 200. Log output is single-line JSON. `POST /v1/payments` without bearer returns 401 with the error envelope.

So after writing the files:
1. `cd backend && /bin/zsh -lc 'dotnet build --nologo'` — clean
2. Start Postgres locally: `docker run -d --name pg-payments -e POSTGRES_PASSWORD=postgres -p 5432:5432 postgres:16-alpine`
3. `dotnet run --project src/PaymentPlatform.Api`
4. `curl http://localhost:5005/health/live` → 200 with `{"status":"alive"}`
5. `curl http://localhost:5005/v1/payments/pay_xxx` → 401 with ErrorEnvelope JSON
6. `curl -H "Authorization: Bearer dev-key-mrc-acme" -H "Idempotency-Key: $(uuidgen)" -H "Content-Type: application/json" -d '{"amount_minor":100,"currency":"USD","card_token":"tok"}' http://localhost:5005/v1/payments` → 201 with `pay_...` ULID
7. Repeat with same key → identical response, no second row in DB
8. Stop container after: `docker rm -f pg-payments`

### Suggested commit shape for Task 6

One commit is fine — it's a tightly-coupled wiring change. Message:
```
feat: wire HTTP host with serilog, middleware, endpoints, and health
```

Or split if it feels too big:
1. `feat: add infrastructure dependency injection and system clock` (Infrastructure DI + SystemClock)
2. `feat: add api host with middleware pipeline and endpoints` (everything in Api/)

## Decisions already locked in (do NOT re-litigate)

These were settled across the planning + Tasks 3–5. Don't re-ask, don't redesign:

1. **Backend:** ASP.NET Core 10 + EF Core 10 + Postgres. `.NET 10` LTS (SDK 10.0.300 installed).
2. **Solution file:** `PaymentPlatform.slnx` (new XML format introduced in .NET 9). Same semantics as legacy `.sln`.
3. **API style:** **Minimal APIs**, not Controllers.
4. **IDs:** **ULID** (prefixed `pay_`, `mrc_`, `evt_`), not Guid.
5. **Integration tests:** **Testcontainers** (real Postgres in Docker), not in-memory SQLite.
6. **Idempotency in Phase 1:** Inline in the `CreatePayment` handler (not cross-cutting middleware). Refactor to middleware in Phase 2.
7. **Auth in Phase 1:** Dev bearer middleware. Static keys per merchant. Real OAuth2 is production-only.
8. **Architecture:** Vertical Slice + MediatR. Feature folders own their command, handler, validator, endpoint.
9. **State machine:** Enum defines all 6 statuses; only `Pending` is reachable in Phase 1.
10. **Geographic scope:** US-only. Production target is multi-region active-passive. Code must not assume single-region (no in-memory caches, no local-disk state).

## Toolchain quirks (the gotchas)

1. **`dotnet` is not on PATH for the Bash tool's subshell.** Use `/bin/zsh -lc 'dotnet ...'` to get the login shell PATH.
2. **SDK 10.0.300 generated `.slnx`, not `.sln`.** New XML format. All `dotnet` subcommands work the same.
3. **NUlid is added to Domain.** Plan §5 said no NuGet deps in Domain but §7 needed `IdGenerator.cs` wrapping NUlid. Resolution: NUlid (small pure-C# leaf dep) is in Domain.
4. **Fact-forcing gate hooks fire on Write/Edit.** Disable with `ECC_GATEGUARD=off` (one-shot) or add `pre:edit-write:gateguard-fact-force` to `ECC_DISABLED_HOOKS` in `~/.claude/settings.json` (persistent, keeps Bash gate intact).
5. **Cost warning hook fires aggressively and becomes more strident over the session.** User is on Max plan, so the dollar number is API-rate equivalent — but it still correlates with context tokens burned in the 5-hour Max window. This session hit $138.98 mid-Task 6 → user chose to pause. Don't gatekeep on the number, but DO surface it transparently when it jumps.
6. **`dotnet new tool-manifest` in .NET 10 puts the file at the repo root, not `.config/dotnet-tools.json`.** Move it manually: `mv dotnet-tools.json .config/dotnet-tools.json` (already done for backend; verify on resume).
7. **Commit messages must NOT include Claude attribution** — user disabled it globally.

## Skill firing schedule (revised)

| Task | Skill(s) | Notes |
|---|---|---|
| 6 — API host | `dotnet-skills:microsoft-extensions-dependency-injection` (optional) | **Do NOT load `csharp-api-design`** — it's about library/NuGet API stability (extend-only, wire compat, Obsolete deprecation cycles), NOT about ASP.NET Core endpoint design. Was a wasted load in the prior session. |
| 7 — Docker Compose | (none specific) | |
| 8 — Integration tests | `dotnet-skills:testcontainers` | Essential — Testcontainers fixture pattern, container reuse via `[CollectionDefinition]`. |
| 9 — README | (none specific) | |

**Lesson from prior session:** the `dotnet-skills:csharp-api-design` skill name is misleading. If a skill name sounds relevant but you haven't loaded it before, peek at its description in the skill list first (it's usually a one-liner) before invoking. Loading a wrong skill adds ~4k tokens of irrelevant context.

## Useful commands (paste-ready)

```bash
# Verify SDK
/bin/zsh -lc 'dotnet --version'           # 10.0.300

# Build
cd backend && /bin/zsh -lc 'dotnet build --nologo'

# Run unit tests only (fast, no Docker)
cd backend && /bin/zsh -lc 'dotnet test tests/PaymentPlatform.UnitTests'

# Run all tests (slow first time — Testcontainers cold start)
cd backend && /bin/zsh -lc 'dotnet test'

# Start a local Postgres for Task 6 smoke test
docker run -d --name pg-payments -e POSTGRES_PASSWORD=postgres -p 5432:5432 postgres:16-alpine
# Stop & remove when done
docker rm -f pg-payments

# Run the API
cd backend && /bin/zsh -lc 'dotnet run --project src/PaymentPlatform.Api'

# Check what's uncommitted
git status --short
```

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

That's the bar.
