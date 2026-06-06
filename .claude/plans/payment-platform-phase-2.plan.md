# Plan: Payment Platform — Phase 2 (State Machine + Lifecycle Endpoints)

**Source plan**: `.claude/plans/payment-platform.plan.md` (master plan §13, Phase 2)
**Entry note**: `.claude/plans/payment-platform-phase-2-entry-resume.md` (Phase 1 status, polish bugs, open decisions)
**Goal**: Every payment lifecycle endpoint working with a persisted audit trail and safe concurrent retries.
**Complexity**: Medium (~1 day of focused work, 3 new vertical slices, 1 new aggregate type, 1 schema migration, ~25 new files)

## 1. Summary

Phase 2 turns the static Phase 1 payment record into a moving aggregate: `Pending → Authorized → Captured → Settled` plus `Failed` and `Refunded` terminal branches. Every transition appends a row to a new `payment_events` table, mutations are guarded by optimistic concurrency on the existing `payments.version` column, and idempotency is extended to every state-changing endpoint — not just `POST /v1/payments` as in Phase 1. We also add the list endpoint with cursor pagination and a status filter so the dashboard (Phase 5) has something to read.

Three Phase 1 polish bugs ride along because Phase 2 touches the same middleware and error envelopes. They land in Task 1, before any state-machine work, so the rest of Phase 2 runs against a clean baseline.

Phase 2 deliberately does NOT add: RabbitMQ, the settlement worker, the outbox table, OpenTelemetry tracing, `/health/ready`, or the frontend. Those land in Phases 3–5. The `Settled` transition exists in code (the aggregate accepts it) but is only callable by an in-process test in this phase; Phase 3 wires it to the worker.

## 2. What "done" looks like (the acceptance walkthrough)

Against `docker compose up`:

1. `POST /v1/payments` returns 201 in `Pending` (Phase 1 behavior, still passing).
2. (Test-only seam: an internal call drives `Pending → Authorized`. Real authorization lands when we wire a stub processor; for Phase 2 the integration tests reach into the DB and call the aggregate method directly, the same way Phase 3's worker will.) The `payment_events` table now shows two rows: `null → Pending` and `Pending → Authorized`.
3. `POST /v1/payments/{id}/capture` with `Idempotency-Key` on an `Authorized` payment returns 200 with the payment now in `Captured`. `payment_events` shows a third row.
4. The same `POST .../capture` call with the same key + body returns the byte-identical cached response. No new event row. No second `Captured` transition.
5. The same `POST .../capture` call with the same key but a different body returns 409 `idempotency_key_conflict`.
6. `POST .../capture` on a `Pending` payment returns 409 `invalid_state_transition` and **does not** append an event row.
7. `POST .../capture` on a `Captured` payment returns 409 `invalid_state_transition`.
8. `POST .../refund` on a `Captured` payment returns 200 with `Refunded`. `payment_events` shows a fourth row. Idempotency works the same way as capture.
9. `POST .../refund` on a `Pending` or `Failed` payment returns 409.
10. `GET /v1/payments?status=Captured&limit=2` returns 200 with `{ data: [...], next_cursor: "..." }`. Following the cursor returns the next page; pagination is stable across new rows being inserted (created_at, id ordering).
11. Two concurrent `POST .../capture` calls against the same `Authorized` payment: one wins (returns 200 + `Captured`), one loses with 409 `concurrent_modification`. The event table has exactly one new row.
12. The Phase 1 polish bugs are fixed:
    - Every error response (400/404/409/422/500) carries an `X-Request-Id` header matching the `request_id` in the body.
    - The Serilog "HTTP request handled" log line carries `request_id`.
    - (Optional in Phase 2 — see Task 1 polish-fix 3) `trace_id` appears in every regular log line, not just error envelopes.
13. `dotnet test` runs and passes. Phase 1's 77 tests still green; Phase 2 adds ~25 new tests.

## 3. Skills and TDD discipline

### Skills to leverage

| Skill | Where |
|---|---|
| `ecc:tdd-workflow` | Every task. Write the failing test first, run it red, write the minimum code to green, refactor. Each task below is structured Red → Green → Refactor. |
| `dotnet-skills:csharp-coding-standards` | All C# we touch — records for DTOs, immutability, sealed types, explicit access modifiers. |
| `dotnet-skills:csharp-api-design` | New minimal-API endpoint shapes, request/response DTOs, error envelope consistency. |
| `dotnet-skills:efcore-patterns` | `payment_events` configuration, value converters, the concurrency token, the tracking-vs-AsNoTracking call on the mutation path. |
| `dotnet-skills:testcontainers` | New integration tests reuse the Phase 1 fixture pattern (`IntegrationTestBase` → `PostgresFixture` → `PaymentApiFactory`). |
| `ecc:database-migrations` | Reviewing the generated migration before commit — column add, default backfill, PK swap order, reversible `Down()`. |
| `ecc:architecture-decision-records` | The format for ADR-0005/0006/0007 in Task 0. |
| `ecc:api-design` | Confirming the list endpoint's cursor and filter conventions are consistent with the existing endpoints. |
| `ecc:code-review` | Run after each feature task completes (after green tests). |
| `csharp-reviewer` agent | Auto-fired by `code-review` skill for any C# changes. |

Skills NOT used in Phase 2 but referenced for later phases:
- `dotnet-skills:opentelementry-dotnet-instrumentation` (Phase 4)
- `dotnet-skills:akka-net-specialist` and friends (not used — we're on ASP.NET hosted services, not Akka)
- `ecc:e2e-testing` (Phase 5, frontend)
- `ecc:opensource-pipeline` (post-MVP)

### TDD ordering (universal rule for Phase 2)

For every task below, the order is:

1. **RED** — Write the failing test(s) first. Confirm they fail with the expected error message, not a compile error.
2. **GREEN** — Write the minimum production code to make those tests pass.
3. **REFACTOR** — Clean up names, extract helpers if duplication appeared, re-run tests.
4. **REVIEW** — Invoke `ecc:code-review` (which spawns `csharp-reviewer`) on the diff. Address CRITICAL and HIGH findings before moving on.

Each task in §8 has its tests listed BEFORE the implementation work for that task, even when that means writing a test file that points at code that doesn't compile yet. The compile error IS the first red — once the code skeleton exists, the assertion red follows.

The Phase 1 plan put unit tests in the same task as the code they test but did not explicitly say "test first". Phase 2 makes the ordering explicit. If a task says "implement X, then add tests for X", that's a bug in this plan — flag it.

## 4. Architectural decisions to record (Task 0)

The entry resume note flags three open decisions. Resolving them up front avoids halfway rewrites mid-implementation. **Decisions go in three small ADRs under `docs/adr/`** (we already reference an `/docs/adr/` directory in master plan §11; this is the first time it gets populated). Keep each ADR to ~30 lines.

### ADR-0005: Payment state transitions live on the aggregate

**Choice.** State transitions are methods on the `Payment` aggregate (`Authorize`, `Capture`, `Refund`, `Settle`, `Fail`). Each method validates the current state, mutates only when legal, and returns a `PaymentEvent` describing the transition. Illegal transitions throw `InvalidTransitionException` (a `DomainException` subtype).

**Why not a separate `IPaymentStateMachine` service.** The transition logic is small (5 methods, a fixed transition table), genuinely belongs to the aggregate's invariants, and decoupling it would split one cohesive thing across two files for no reuse benefit. Phase 1 already established `Payment` as a private-setter aggregate with a `Create` factory; methods slot in naturally.

**Consequence.** The aggregate is the only place that knows the transition rules. The DB `CHECK` constraint is a second-line safety net. Handlers do NOT branch on status before calling — they call and catch.

### ADR-0006: `payment_events` is written in the same DB transaction as the `payments` update

**Choice.** When a handler mutates a `Payment` and appends a `PaymentEvent`, both rows persist (or roll back) together in a single EF Core `SaveChangesAsync` call.

**Why not the outbox pattern.** The outbox arrives in Phase 3, but it carries **queue messages** for cross-process delivery (settlement jobs). `payment_events` is a **domain audit log** — it's read by the same service that writes it, and we never want a state change without its event row. Atomic write is simpler and correct.

**Consequence.** Phase 2 handlers do not coordinate two stores. Phase 3's outbox table is additive — the settlement message goes to the outbox in the same `SaveChangesAsync`, alongside the event row.

### ADR-0007: Idempotency keys are scoped per-operation, not shared across endpoints

**Choice.** Extend the `idempotency_keys` composite PK from `(merchant_id, key)` to `(merchant_id, operation, key)`. `operation` is a short string (`create_payment`, `capture_payment`, `refund_payment`). Each endpoint passes its own operation discriminator when it claims a key.

**Why not share one namespace.** A merchant might reasonably use the same UUID across "create then capture" calls without intending it as a duplicate. Per-operation keys let us key on UUIDs the merchant generates per logical operation without false collisions. Master plan §10 leans this way; we make it explicit.

**Migration impact.** Phase 1 wrote rows without an `operation` column. Backfill the new column to `'create_payment'` for any existing rows, then make it non-nullable with no default and reconstitute the composite PK.

**Consequence.** Capture, refund, and create can share an idempotency key string without collision. The error envelope's `idempotency_key_conflict` is unambiguous — it's always *within* an operation.

## 5. Phase 1 polish bugs (Task 1)

Listed in the entry resume note; integrated here so the task list is self-contained.

| # | Bug | Fix |
|---|---|---|
| 1 | `X-Request-Id` missing on error responses because `Response.Clear()` wipes headers | After `Response.Clear()` in `ExceptionHandlingMiddleware.WriteAsync`, restore the header from `context.Items[CorrelationIdMiddleware.RequestIdItemKey]`. Add integration test that asserts the header is present on a 404 response. |
| 2 | `UseSerilogRequestLogging()` outside the correlation scope, so the canonical request-handled log line is missing `request_id` | Swap `Program.cs` middleware order: register `UseMiddleware<CorrelationIdMiddleware>()` *before* `UseSerilogRequestLogging()`. Adjust existing `LoggingTests` to assert the request-completion line now carries `request_id`. |
| 3 | `trace_id` absent from regular log lines (only present in error envelopes) | Add `Serilog.Enrichers.Span` package and `.Enrich.WithSpan()` to the Serilog pipeline in `Program.cs` + `PaymentApiFactory`. Acceptance test asserts `trace_id` appears in non-error log lines. **Optional in Phase 2**: if it adds material time, defer to Phase 4 where OpenTelemetry properly owns trace context. Decision: do it — it's a one-package add and removes a footnote from the README. |

## 6. Solution-level additions

No new projects. Phase 2 adds files inside the existing structure.

```
backend/src/
├── PaymentPlatform.Domain/
│   └── Payments/
│       ├── PaymentEvent.cs              [NEW]
│       ├── PaymentEventReason.cs        [NEW] (string-constant class)
│       └── Payment.cs                   [EXTEND with Authorize/Capture/Refund/Settle/Fail]
├── PaymentPlatform.Domain/Common/
│   └── InvalidTransitionException.cs    [NEW]
├── PaymentPlatform.Application/
│   ├── Abstractions/
│   │   ├── IPaymentsDbContext.cs        [EXTEND with DbSet<PaymentEvent>]
│   │   └── IIdempotencyStore.cs         [EXTEND with operation param]
│   ├── Common/
│   │   ├── ConcurrencyConflictException.cs   [NEW]
│   │   └── Cursor.cs                          [NEW] (base64 encode/decode helper)
│   └── Features/
│       ├── CapturePayment/
│       │   ├── CapturePaymentCommand.cs
│       │   ├── CapturePaymentCommandHandler.cs
│       │   └── CapturePaymentCommandValidator.cs
│       ├── RefundPayment/
│       │   ├── RefundPaymentCommand.cs
│       │   ├── RefundPaymentCommandHandler.cs
│       │   └── RefundPaymentCommandValidator.cs
│       └── ListPayments/
│           ├── ListPaymentsQuery.cs
│           └── ListPaymentsQueryHandler.cs
├── PaymentPlatform.Contracts/
│   └── Payments/
│       ├── CapturePaymentRequest.cs     [NEW]
│       ├── RefundPaymentRequest.cs      [NEW]
│       ├── PaymentEventDto.cs           [NEW] (events[] item shape from master plan §6)
│       ├── PaymentResponse.cs           [EXTEND: add UpdatedAt, Events]
│       └── PaymentListResponse.cs       [NEW] ({ data, next_cursor })
├── PaymentPlatform.Infrastructure/
│   ├── Persistence/
│   │   ├── Configurations/
│   │   │   ├── PaymentEventConfiguration.cs   [NEW]
│   │   │   └── IdempotencyKeyConfiguration.cs [EXTEND: add Operation to PK]
│   │   └── Migrations/
│   │       └── <ts>_PaymentEventsAndOperationKey.cs  [GENERATED]
│   └── Idempotency/
│       └── IdempotencyStore.cs          [EXTEND with operation param]
└── PaymentPlatform.Api/
    ├── Endpoints/
    │   └── PaymentsEndpoints.cs         [EXTEND: capture, refund, list]
    ├── Middleware/
    │   └── ExceptionHandlingMiddleware.cs [EXTEND: header restore + new exception mappings]
    └── Program.cs                       [EXTEND: middleware order swap + span enricher]
```

## 7. Database schema additions (one migration)

### `payment_events` (new table — append-only audit log)
| Column | Type | Notes |
|---|---|---|
| `id` | `text PK` | `evt_...` ULID |
| `payment_id` | `text NOT NULL FK → payments(id)` | indexed |
| `from_status` | `text NULL` | null for the creation event |
| `to_status` | `text NOT NULL` | |
| `actor` | `text NOT NULL` | `api`, `worker`, `processor_stub`, `system` |
| `reason` | `text NOT NULL` | short code, e.g. `created`, `captured`, `refunded`, `auth_ok` |
| `payload` | `jsonb NOT NULL DEFAULT '{}'` | optional context (refund amount, failure detail) |
| `at` | `timestamptz NOT NULL DEFAULT now()` | indexed |

Indexes:
- `(payment_id, at DESC)` — drives the timeline in `GET /v1/payments/{id}`.

No `UPDATE` or `DELETE` is ever issued against this table from the application layer. We're not adding a DB-side trigger to enforce that in Phase 2 — the application discipline is sufficient at this scale. (Documented in the ADR.)

### `idempotency_keys` (modify existing)
- Add column `operation text NOT NULL DEFAULT 'create_payment'`.
- Drop existing PK `pk_idempotency_keys` on `(merchant_id, key)`.
- Recreate PK on `(merchant_id, operation, key)`.
- Drop the default after the migration completes (defaults exist only for backfill).

The migration is reversible — `Down()` restores the original PK and drops the column.

### `payments` — no changes
The `version` shadow property + `IsConcurrencyToken()` on the existing column does the optimistic concurrency work for free. EF Core auto-increments `version` on update and throws `DbUpdateConcurrencyException` if the row's version has moved since we loaded it.

## 8. NuGet packages

| Project | Add |
|---|---|
| `PaymentPlatform.Api` | `Serilog.Enrichers.Span` (for polish-fix 3) |

That's it. Everything else is already on disk from Phase 1.

## 9. Task order (the build sequence)

Every task follows Red → Green → Refactor → Review. Tests are written and confirmed failing before the corresponding production code lands. Where a task adds both a domain class and its tests, the test file is committed first (or in the same WIP step) so the failing red is visible.

Each task ends with a validation step. Stop and verify before moving on.

### Task 0 — Architectural decisions (45 min)

Write `docs/adr/0005-payment-state-transitions-on-aggregate.md`, `docs/adr/0006-payment-events-same-transaction.md`, `docs/adr/0007-idempotency-keys-per-operation.md`. Each follows the format: Context, Decision, Consequences, Alternatives considered. Keep them to ~30 lines.

**Validate:** All three files exist. Re-read each one cold — does the rationale survive a skeptical second read?

### Task 1 — Phase 1 polish bug fixes (1 hr)

Three small fixes. Test-first: each red test asserts the bug, then the fix turns it green.

**Red.** Add the failing tests first:
- `ErrorResponseHeadersTests.cs`: GET an unknown payment ID returns 404 AND a non-empty `X-Request-Id` header that equals the body's `error.request_id`. Run — fails because the header is empty after `Response.Clear()`.
- Extend `LoggingTests` with one test asserting the "HTTP request handled" line carries `request_id`. Run — fails because Serilog request logging runs outside the correlation scope.
- Extend `LoggingTests` with one test asserting any non-error log line carries `trace_id`. Run — fails because `Serilog.Enrichers.Span` isn't wired.

**Green.** Apply the minimal fixes:
1. `ExceptionHandlingMiddleware.WriteAsync`: after `Response.Clear()`, restore the request-id header from `context.Items[CorrelationIdMiddleware.RequestIdItemKey]`.
2. `Program.cs`: move `UseMiddleware<CorrelationIdMiddleware>()` above `UseSerilogRequestLogging()`. Confirm final order: `Exception → Correlation → Serilog request logging → DevBearerAuth`.
3. Add `Serilog.Enrichers.Span` package to `PaymentPlatform.Api.csproj`. Add `.Enrich.WithSpan()` to both Serilog configs (`Program.cs` and `PaymentApiFactory.cs`).

**Refactor.** Re-read the diff. The header restoration is one line; if it grows, extract a helper.

**Review.** `ecc:code-review` on the diff.

**Validate:** Run `/bin/zsh -lc 'cd backend && dotnet test'`. The new tests pass. All 77 prior tests still pass. Commit as one `fix:` commit.

### Task 2 — Domain: state machine on the aggregate + PaymentEvent (1.5 hr)

**Red first.** Write `UnitTests/Domain/Payments/PaymentStateMachineTests.cs` and `UnitTests/Domain/Payments/PaymentEventTests.cs` before any production code. The test file will not compile (no `Payment.Capture`, no `PaymentEvent` type) — that compile error IS the first red. Fix it with stub method signatures only (throw `NotImplementedException`); now tests fail at runtime with the right errors.

Required tests, written FIRST:
- Table-driven `[Theory]`: every legal transition `(fromStatus, methodCall)` → succeeds, returns a `PaymentEvent` with the right `fromStatus`/`toStatus`/`reason`.
- Table-driven `[Theory]`: every illegal `(fromStatus, methodCall)` → throws `InvalidTransitionException`, payment status unchanged.
- One `[Fact]` per transition method asserting `UpdatedAt` advances to the supplied `now`.
- `PaymentEventTests`: factory rejects null payload, normalizes empty-string actor/reason to throw.

Run — all red.

**Green.** Implement to make them pass:

`PaymentEvent.cs`: sealed class, similar shape to `Payment` (private setters, factory `Create(paymentId, fromStatus, toStatus, actor, reason, payload, at)`). Pure domain — no EF awareness.

`PaymentEventReason.cs`: static class of string constants (`Created`, `AuthOk`, `AuthFailed`, `Captured`, `Refunded`, `Settled`, `Failed`). Keeping these as string constants (not an enum) makes the audit table human-readable and lets Phase 3 add new reasons without a code change here.

`InvalidTransitionException.cs`: `DomainException` with code `invalid_state_transition`. Constructor takes `from` and `to`.

Extend `Payment.cs`:
```csharp
public PaymentEvent Authorize(DateTimeOffset now, string actor = "system") { ... }
public PaymentEvent Capture(DateTimeOffset now, string actor = "api") { ... }
public PaymentEvent Refund(DateTimeOffset now, string reason, string actor = "api") { ... }
public PaymentEvent Settle(DateTimeOffset now, string actor = "worker") { ... }
public PaymentEvent Fail(DateTimeOffset now, string reason, string actor = "system") { ... }
```

Each method:
1. Validates the current state allows the transition. Throws `InvalidTransitionException` if not.
2. Mutates `Status` and the shadow `UpdatedAt`.
3. Returns the `PaymentEvent` describing the transition. (Handlers append it to `IPaymentsDbContext.PaymentEvents`.)

Also add a `Payment.Create` overload (or extend the existing one) to return both the payment and the initial `null → Pending` event, since Phase 1 currently creates a payment without recording the creation event. Decision: the existing `Create` returns the payment; add `CreateInitialEvent(now)` on the aggregate (idempotent — just constructs an event with the current state). The `CreatePaymentCommandHandler` calls both. This avoids changing Phase 1's `Create` signature.

Transition table (legal transitions):
```
null         → Pending       (Created)
Pending      → Authorized    (AuthOk)
Pending      → Failed        (AuthFailed)
Authorized   → Captured      (Captured)
Authorized   → Failed        (Voided / Failed)
Captured     → Settled       (Settled)
Captured     → Refunded      (Refunded)
Settled      → Refunded      (Refunded)
```

Anything else throws.

**Refactor.** Once green, consolidate the transition switch into one private method `EnsureTransitionAllowed(PaymentStatus from, PaymentStatus to)` if the per-method branches duplicate.

**Review.** `ecc:code-review` on the diff.

**Validate:** `dotnet test PaymentPlatform.UnitTests` passes. State machine tests alone should be ~25 cases (8 legal × 1 + ~15 illegal). Commit as `feat: payment state machine on the aggregate`.

### Task 3 — Infrastructure: PaymentEvent config + idempotency op column + migration (1 hr)

This task is mostly schema + EF wiring; the verification is integration-test-shaped rather than unit-test-shaped (the migration is the artifact under test). Apply `ecc:database-migrations` skill while reviewing the generated migration.

**Red.** Add one new integration test `Persistence/MigrationSmokeTests.cs` that:
- Builds the `PaymentsDbContext` against the testcontainer.
- Inserts a `PaymentEvent` row referencing a seeded payment, reads it back, asserts round-trip equality on all columns including the `payload` jsonb.
- Inserts two `IdempotencyKeyRecord` rows with the same `(merchant_id, key)` but different `operation` — expects success (proves the new PK shape lets per-operation keys coexist).
- Inserts a duplicate `(merchant_id, operation, key)` — expects `DbUpdateException` from the unique constraint.

Run — fails to compile (no `DbSet<PaymentEvent>`, no `Operation` property), then fails at migration apply (no `payment_events` table).

**Green.** Implement:

`PaymentEventConfiguration.cs`: table `payment_events`, ULID id, `from_status`/`to_status` as text with the same `ToString()` enum conversion as `Payment.Status`, `payload` as jsonb with the dictionary value converter pattern from `PaymentConfiguration`, `at` with default `now()`, indexes from §7.

Extend `IdempotencyKeyRecord` to carry an `Operation` field. Update its constructor.

Extend `IdempotencyKeyConfiguration`: new `operation` column, recompose composite PK to `(merchant_id, operation, key)`.

Extend `IPaymentsDbContext` and `PaymentsDbContext` with `DbSet<PaymentEvent> PaymentEvents`.

Extend `IIdempotencyStore`:
```csharp
Task<IdempotencyKeyRecord?> FindAsync(string merchantId, string operation, string key, CancellationToken ct);
Task SaveAsync(IdempotencyKeyRecord record, CancellationToken ct);  // record already carries operation
```

Update `IdempotencyStore` impl. Update `CreatePaymentCommandHandler` to pass `operation: "create_payment"` (introduce an `IdempotencyOperations` static class with constants).

Generate migration:
```
/bin/zsh -lc 'cd backend && dotnet ef migrations add PaymentEventsAndOperationKey \
  --project src/PaymentPlatform.Infrastructure --startup-project src/PaymentPlatform.Api'
```

Manually inspect the generated migration (the `ecc:database-migrations` skill checklist):
- `payment_events` table created with FK to `payments(id)`.
- `idempotency_keys` PK drop + recreate is split into the right ordered operations.
- The new `operation` column is created with `DEFAULT 'create_payment'`, the existing rows pick up the default, and then the default is dropped after the PK is recreated.
- `Down()` is the inverse and won't crash on data.

**Refactor.** If migration ordering is hand-fixable to be safer, do it. Don't ship a destructive migration even if no production data exists yet — the discipline matters.

**Review.** `ecc:code-review` on the diff. `ecc:database-migrations` skill checklist.

**Validate:**
- `dotnet build` clean.
- `MigrationSmokeTests` pass.
- `/bin/zsh -lc 'cd backend && dotnet test'` — Phase 1's 77 tests still pass (they exercise the create flow which now writes `operation = 'create_payment'`).

### Task 4 — Application: CapturePayment slice (1.5 hr)

**Red first.** Write `IntegrationTests/CapturePaymentTests.cs` BEFORE the handler. Tests reference `CapturePaymentCommand`, `CapturePaymentRequest`, the endpoint `POST /v1/payments/{id}/capture`. Compile error is the first red.

Required tests:
- 200 happy path (`Authorized → Captured`) creates one event row in `payment_events`.
- Replay with same key + same body returns byte-identical response, no new event row, payment still `Captured`.
- Replay with same key + different body returns 409 `idempotency_key_conflict`.
- Capture on `Pending` returns 409 `invalid_state_transition`, no event row appended, status unchanged.
- Capture on `Captured` returns 409 `invalid_state_transition`, no event row appended.
- 404 for unknown payment id.
- 404 for cross-merchant.
- Missing `Idempotency-Key` returns 400.
- Concurrent captures: two parallel `HttpClient.PostAsync` calls against the same `Authorized` payment — one returns 200, one returns 409 `concurrent_modification`. Exactly one new event row in `payment_events` for this payment.

Use a `TestDataBuilder` helper in `Fixtures/` to seed `Authorized` payments (create via POST then mutate via `using var scope = Factory.Services.CreateScope()` + `payment.Authorize(now)` + `_db.PaymentEvents.Add(...)` + save). This helper is reused by RefundPayment tests in Task 5.

Run — all red.

**Green.** Implement:

`CapturePaymentCommand`: `IRequest<PaymentResponse>` with `PaymentId`, `IdempotencyKey`, and optional `AmountMinor` (Phase 2 ignores partial captures — see master plan §17; the field is accepted for forward compat but must equal the original amount or be null/zero).

`CapturePaymentCommandValidator`: idempotency key required, amount (if provided) > 0.

`CapturePaymentCommandHandler`:
1. Find existing idempotency record by `(merchantId, "capture_payment", key)`. If exists and request hash matches → return cached response. If exists and hash mismatches → throw `IdempotencyConflictException`.
2. Load `Payment` with tracking, filtered by merchant. If null → throw `NotFoundException`.
3. Call `payment.Capture(_clock.UtcNow)`. `InvalidTransitionException` propagates as 409 via the exception middleware.
4. `_db.PaymentEvents.Add(theEventFromTheAggregate)`.
5. Build response, serialize, build idempotency record, `_db.IdempotencyKeys.Add(record)`.
6. `await _db.SaveChangesAsync(ct)` — single transaction commits payment update + event row + idempotency row, EF concurrency check on `payments.version` fires here.
7. Catch `DbUpdateConcurrencyException` → throw `ConcurrencyConflictException` (mapped to 409 by middleware). Catch `DbUpdateException` with PK violation on idempotency_keys → re-read cache and return cached response.

Add `ConcurrencyConflictException` with code `concurrent_modification`.

Wire endpoint in `PaymentsEndpoints.cs`:
```csharp
group.MapPost("/{id}/capture", CapturePaymentAsync);
```

Extend the response shape (`PaymentResponse`): add `UpdatedAt` and `Events: IReadOnlyList<PaymentEventDto>`. Backfill the events list in handlers by querying `PaymentEvents` for the payment ordered by `at`.

**Refactor.** Idempotency handling now appears in 2 handlers — if duplication is heavy, extract an `IdempotencyExecutor<TResponse>` helper. Don't preemptively extract; let two be the first signal.

**Review.** `ecc:code-review`. `csharp-reviewer` auto-fires.

**Validate:** `dotnet test` — all new CapturePayment tests pass; all 77 prior tests still pass.

### Task 5 — Application: RefundPayment slice (1 hr)

**Red first.** Write `IntegrationTests/RefundPaymentTests.cs` BEFORE the handler. Reuse the `TestDataBuilder` to seed `Captured` payments (chain `Authorize` then `Capture` in the seed scope).

Required tests:
- 200 happy path (`Captured → Refunded`) creates one event row whose `payload` jsonb has `{"reason": "..."}`.
- Refund on `Pending` returns 409 `invalid_state_transition`.
- Refund on `Authorized` returns 409 `invalid_state_transition`.
- Replay parity with capture tests (same key same body → cached; same key different body → conflict).
- Required `reason` field — missing returns 400 `validation_failed`.
- 404 cross-merchant.

Run — all red.

**Green.** Mirror Task 4 with three differences:
- `RefundPaymentCommand` carries a required `Reason` field (short string, validated non-empty). Stored in the event's `payload` jsonb as `{"reason": "..."}`.
- Handler calls `payment.Refund(_clock.UtcNow, reason)`.
- Idempotency operation: `"refund_payment"`.
- Endpoint: `group.MapPost("/{id}/refund", RefundPaymentAsync);`.

The aggregate's `Refund` accepts the transition from `Captured` *or* `Settled` → `Refunded`. Phase 2 only reaches `Captured → Refunded` from the API; the `Settled → Refunded` path is unit-tested but not integration-tested until Phase 3 puts a payment in `Settled` via the worker.

**Refactor.** If the idempotency-executor extraction signal fired in Task 4, do it here. If not, leave it.

**Review.** `ecc:code-review`.

**Validate:** `dotnet test` green.

### Task 6 — Application: ListPayments slice with cursor pagination (2 hr)

**Red first.** Two test files BEFORE any production code:
- `UnitTests/Application/Common/CursorTests.cs`: round-trip encode/decode, malformed input returns null, special characters in payment IDs survive base64.
- `IntegrationTests/ListPaymentsTests.cs`:
  - Empty list → `{ data: [], next_cursor: null }`.
  - Single page (3 payments, limit 10) → all returned in `created_at DESC, id DESC` order, `next_cursor: null`.
  - Multi-page (5 payments, limit 2) → first call returns 2 + cursor, second returns 2 + cursor, third returns 1 + null cursor. Verify no duplicates across pages and ordering is stable.
  - `status` filter returns only matching payments.
  - Cross-merchant isolation: merchant A's list never returns merchant B's payments.
  - Bad cursor returns 400 `validation_failed`.
  - Timestamp-tie edge case: 3 payments inserted with identical `created_at` (force the clock to a fixed value via the seam) — pagination still produces them all exactly once across pages, ordered by id desc.

Run — all red.

**Green.** Implement:

`ListPaymentsQuery`: `IRequest<PaymentListResponse>` with `Status?` (enum), `Cursor?` (string), `Limit` (int, default 20, max 100).

`Cursor.cs` in `Application/Common/`:
```csharp
public static string Encode(DateTimeOffset createdAt, string paymentId);
public static (DateTimeOffset CreatedAt, string PaymentId)? Decode(string cursor);  // returns null on malformed input
```

Encoding: `Convert.ToBase64String(Encoding.UTF8.GetBytes($"{createdAt:O}|{paymentId}"))` — opaque to clients, deterministic for us.

`ListPaymentsQueryHandler`:
1. Build base query: `_db.Payments.AsNoTracking().Where(p => p.MerchantId == merchantId)`.
2. If `query.Status` is set: `.Where(p => p.Status == query.Status.Value)`.
3. If `query.Cursor` is set and decodes: `.Where(p => p.CreatedAt < cursorCreatedAt || (p.CreatedAt == cursorCreatedAt && string.Compare(p.Id, cursorPaymentId) < 0))`.
4. `.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id).Take(limit + 1)`.
5. Materialize. If we got `limit + 1` rows, the last one is the next-page anchor — drop it from `data`, encode the previous row's `(CreatedAt, Id)` as `next_cursor`. Otherwise `next_cursor` is null.
6. Build `PaymentListResponse` — each payment's `events` list is **NOT** populated on the list endpoint (avoids N+1; clients fetch detail for the timeline). Document this in the response DTO comment.

Endpoint: `group.MapGet("/", ListPaymentsAsync);`. Bind `[FromQuery] string? status`, `[FromQuery] string? cursor`, `[FromQuery] int? limit`. Parse `status` to enum with case-insensitive `Enum.TryParse`; bad value → throw `ValidationException`.

**Refactor.** Inline the where-clause cursor predicate as a private method `ApplyCursor(IQueryable<Payment>, decoded)` if the inline expression strains readability.

**Review.** `ecc:code-review`. `ecc:api-design` skill checks the cursor and filter conventions match REST norms.

**Validate:** `dotnet test` green.

### Task 7 — CreatePayment event + response shape backfill (1 hr)

This task closes the loop on Phase 1: `CreatePaymentCommandHandler` should now append the initial `null → Pending` event, and `PaymentResponse` should expose `UpdatedAt` and `Events`.

**Red first.** Update existing Phase 1 integration tests to assert the new shape:
- `CreatePaymentTests.HappyPath_ResponseIncludesInitialPendingEvent` — new test.
- `CreatePaymentTests.HappyPath_ResponseIncludesUpdatedAt` — new test.
- `GetPaymentTests.HappyPath_ResponseIncludesEventTimeline` — new test (uses a payment with capture + refund history via TestDataBuilder).

Existing Phase 1 tests that assert exact response shapes get their assertions updated to allow `events` and `updated_at`. (Phase 1 used `JsonDocument` parsing for specific fields rather than full-object equality, per `Fixtures/TestJson.cs`, so the surface area is small.)

Run — all new tests red, some existing tests still green.

**Green.** Update `CreatePaymentCommandHandler` to write the initial event before `SaveChangesAsync`. Update `GetPaymentQueryHandler` to include the events list (single extra query: `_db.PaymentEvents.Where(e => e.PaymentId == id).OrderBy(e => e.At).ToList()`).

Map `ConcurrencyConflictException` and `InvalidTransitionException` in `ExceptionHandlingMiddleware` → 409 with their respective error codes. Order matters: catch `InvalidTransitionException` BEFORE the general `DomainException` clause.

In `PaymentsEndpoints.cs` confirm route order: `POST /` (create), `GET /` (list), `GET /{id}`, `POST /{id}/capture`, `POST /{id}/refund`.

**Refactor.** The response-builder logic (load payment + events + shape into DTO) now appears in 4 handlers — extract `PaymentResponseFactory.From(payment, events)` if duplication is heavy.

**Review.** `ecc:code-review`.

**Validate:** `dotnet test` — all 77 prior tests still pass with the updated assertions; all new tests pass.

### Task 8 — Cross-cutting state-machine end-to-end test (45 min)

Tasks 4–7 already cover capture, refund, and list integration testing. This task adds one cross-cutting test that exercises the full lifecycle in one go — useful for the next session's acceptance walkthrough and for catching event-ordering bugs that per-endpoint tests miss.

**Red.** `IntegrationTests/StateMachineEndToEndTests.cs`:
- Create a payment via `POST /v1/payments` → Pending.
- Drive `Pending → Authorized` via direct aggregate call inside `using var scope = Factory.Services.CreateScope()` (the way Phase 3's worker will).
- Capture via `POST /v1/payments/{id}/capture` → Captured.
- Refund via `POST /v1/payments/{id}/refund` → Refunded.
- `GET /v1/payments/{id}` returns the payment with 4 event rows in order: `null→Pending`, `Pending→Authorized`, `Authorized→Captured`, `Captured→Refunded`.
- Each event has a sensible `actor` and `reason`.
- `at` timestamps are strictly increasing.

**Green.** No new production code — this test exercises code from Tasks 2–7. Any failure here indicates a bug in those tasks.

**Refactor.** N/A.

**Review.** `ecc:code-review`.

**Validate:** `/bin/zsh -lc 'cd backend && dotnet test'` — all prior 77 tests + ~50 new tests green. First run will be slow (testcontainers cold start ~30s); subsequent runs reuse the container per Phase 1's collection setup.

### Task 9 — Acceptance walkthrough + README diff + commit (30 min)

Walk the §10 acceptance runbook manually against `docker compose up`. Capture the curl session for the next session's reference.

README diff:
- Add a "Phase 2 endpoints" section under the curl examples: capture, refund, list with cursor.
- Add a "State machine" section with the transition diagram from master plan §5 in ASCII.
- Note the per-operation idempotency scoping in the auth/idempotency section.

Commit Phase 2 in 5-6 thematic commits (matching the Phase 1 commit cadence — TDD means each feature commit bundles tests + implementation together):
1. `docs:` ADR-0005/0006/0007 (Task 0).
2. `fix:` Phase 1 polish bugs (Task 1, with tests).
3. `feat:` payment state machine on the Payment aggregate (Task 2, with unit tests).
4. `feat:` payment_events table and per-operation idempotency keys (Task 3, with migration smoke test).
5. `feat:` capture and refund endpoints (Tasks 4–5, with integration tests).
6. `feat:` list endpoint with cursor pagination + lifecycle event surfacing (Tasks 6–8, with integration tests).

Skip Claude attribution per global git settings.

**Validate:** `git log --oneline | head -10` reads cleanly. Working tree clean except `.claude/`.

**Total estimated time: ~10 hours of focused work.** The master plan estimates 1 day; the extra two hours absorb the polish fixes and the ADR writeups, which weren't in the original phase estimate.

## 10. Validation runbook (the "did Phase 2 work" curls)

```bash
# Already documented in Phase 1's runbook for create + get. New endpoints below.

# Seed: create a payment in Pending
RESP=$(curl -s -X POST http://localhost:5000/v1/payments \
  -H "Authorization: Bearer dev-key-mrc-acme" \
  -H "Idempotency-Key: $(uuidgen)" \
  -H "Content-Type: application/json" \
  -d '{"amount_minor":12500,"currency":"USD","card_token":"tok_stub_visa"}')
PID=$(echo "$RESP" | jq -r .id)

# Step 1 — TEMPORARY for Phase 2 only: drive Pending → Authorized in the DB.
# Phase 3 wires a stub processor that does this naturally. For Phase 2 acceptance,
# use psql to flip the status. (This is the same shape as Phase 3's worker.)
docker compose exec postgres psql -U paymentplatform paymentplatform -c \
  "UPDATE payments SET status='Authorized', version=version+1 WHERE id='$PID';"
docker compose exec postgres psql -U paymentplatform paymentplatform -c \
  "INSERT INTO payment_events (id, payment_id, from_status, to_status, actor, reason, payload, at) \
   VALUES ('evt_' || gen_random_uuid(), '$PID', 'Pending', 'Authorized', 'system', 'auth_ok', '{}', now());"

# Step 2 — Capture
KEY=$(uuidgen)
curl -i -X POST "http://localhost:5000/v1/payments/$PID/capture" \
  -H "Authorization: Bearer dev-key-mrc-acme" \
  -H "Idempotency-Key: $KEY" \
  -H "Content-Type: application/json" \
  -d '{}'
# → 200, status: "Captured", events array has 3 entries

# Step 3 — Replay same capture
curl -i -X POST "http://localhost:5000/v1/payments/$PID/capture" \
  -H "Authorization: Bearer dev-key-mrc-acme" \
  -H "Idempotency-Key: $KEY" \
  -H "Content-Type: application/json" \
  -d '{}'
# → 200, byte-identical body

# Step 4 — Capture an already-captured payment
curl -i -X POST "http://localhost:5000/v1/payments/$PID/capture" \
  -H "Authorization: Bearer dev-key-mrc-acme" \
  -H "Idempotency-Key: $(uuidgen)" \
  -H "Content-Type: application/json" \
  -d '{}'
# → 409 invalid_state_transition

# Step 5 — Refund
curl -i -X POST "http://localhost:5000/v1/payments/$PID/refund" \
  -H "Authorization: Bearer dev-key-mrc-acme" \
  -H "Idempotency-Key: $(uuidgen)" \
  -H "Content-Type: application/json" \
  -d '{"reason":"customer_request"}'
# → 200, status: "Refunded"

# Step 6 — List with filter
curl -s "http://localhost:5000/v1/payments?status=Refunded&limit=10" \
  -H "Authorization: Bearer dev-key-mrc-acme" | jq .
# → { data: [...], next_cursor: null }

# Step 7 — Error response carries X-Request-Id (polish fix verification)
curl -i "http://localhost:5000/v1/payments/pay_nonexistent" \
  -H "Authorization: Bearer dev-key-mrc-acme"
# → 404 AND a non-empty X-Request-Id header matching the body's request_id

# Step 8 — Full test suite
/bin/zsh -lc 'cd backend && dotnet test'
```

## 11. Risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Migration of `idempotency_keys` PK fails on a non-empty DB if any existing rows have a NULL operation column briefly | Low | Medium | Generate migration with explicit column-add (default `'create_payment'`) BEFORE the PK swap. Test against a clean DB and against a DB with one create-payment row. |
| State machine has a transition I missed in the table-driven tests | Medium | High | The `[Theory]` data source enumerates ALL `(from, to)` pairs from `PaymentStatus.GetValues()` minus the legal set — illegal transitions are exhaustive by construction. |
| Concurrent capture race: optimistic concurrency throws but the loser also wrote an idempotency row that survives the rollback | Low | High | Both writes happen in one `SaveChangesAsync` — the EF Core transaction rolls back the entire batch on `DbUpdateConcurrencyException`. Integration test asserts the loser's response is 409 AND no orphan idempotency row exists for the losing key. |
| Idempotency replay on capture returns a cached response that is stale (the payment moved further after capture) | Low | Low | The cached response represents the state at the moment the capture succeeded. That's the contract: idempotent retries return the original response. Documented in the ADR. |
| Cursor pagination drift: a new payment inserted between page 1 and page 2 sneaks in | Medium | Low | Cursor is `(created_at, id)` and we sort descending. Newer payments have larger `created_at`/`id` and fall on page 1, not in the middle of an in-flight pagination. Edge case documented; tested. |
| `Serilog.Enrichers.Span` package not yet shipping for net10.0 | Low | Low | If true, use the highest stable net9.0 build per the Phase 1 NuGet rule. Fall back: emit `Activity.Current.TraceId` from a custom enricher (10 lines). |
| Extending `PaymentResponse` with `events[]` breaks Phase 1 integration tests | High | Low | The Phase 1 tests assert specific fields (`id`, `amount_minor`, `status`); adding new fields is non-breaking for those assertions. Audit the test bodies in Task 1 before merging the response-shape extension. |
| The `Settled` transition is reachable in the aggregate but has no caller in Phase 2 — looks dead | High | Low | This is intentional; Phase 3 calls it from the worker. Add a unit test that calls `Settle()` and asserts the transition works — proves it's wired and prevents premature deletion. |
| The polish fix #2 (Serilog request logging order swap) changes the request-handled line's structure and breaks `LoggingTests` regex assertions | Medium | Low | Re-read `LoggingTests.cs` while making the swap; update its assertions in the same commit. |

## 12. Acceptance criteria

- [ ] All three ADRs (`0005`, `0006`, `0007`) committed under `docs/adr/`.
- [ ] All three Phase 1 polish bugs fixed; new tests assert each fix.
- [ ] `Payment` aggregate has `Authorize`, `Capture`, `Refund`, `Settle`, `Fail` methods.
- [ ] State machine unit tests cover every legal transition + every illegal transition.
- [ ] `payment_events` table populated on every transition (verified via integration test).
- [ ] `POST /v1/payments/{id}/capture` works happy + replay + state-conflict + cross-merchant + missing-key.
- [ ] `POST /v1/payments/{id}/refund` works happy + replay + state-conflict + cross-merchant + missing-key.
- [ ] `GET /v1/payments?status=...&cursor=...&limit=...` returns cursor-paginated results with stable ordering.
- [ ] Idempotency keys are per-operation (verified by reusing the same key string across create + capture without collision).
- [ ] Concurrent state changes on the same payment: one wins, one returns 409 `concurrent_modification`, exactly one event row.
- [ ] `dotnet test` green: Phase 1's 77 tests still passing + ~25 new tests.
- [ ] Manual curl walk through §9 produces the expected responses.
- [ ] README updated with Phase 2 endpoint examples and the state-machine diagram.
- [ ] Commits split thematically per Task 9; no Claude attribution.

## 13. What's intentionally NOT in Phase 2

| Out | Lands in |
|---|---|
| `payment_outbox` table | Phase 3 |
| RabbitMQ + MassTransit + settlement worker | Phase 3 |
| Stub `IPaymentProcessor` interface and adapter | Phase 3 (Phase 2 drives transitions via direct aggregate calls in tests + psql in the acceptance walkthrough) |
| OpenTelemetry SDK and the OTLP exporter | Phase 4 |
| Prometheus `/metrics` endpoint | Phase 4 |
| `/health/ready` with DB + MQ checks | Phase 4 |
| Log redaction enricher (Phase 1 logs no card token by hand-built log messages) | Phase 4 |
| Frontend dashboard | Phase 5 |
| Partial captures (amount < original) and partial refunds | Documented as a future improvement (master plan §17) |
| Background TTL sweep for `idempotency_keys` rows older than 24h | Phase 4 polish or production hardening |

## 14. Notes for the implementer

- **State transitions return events, handlers persist them.** The aggregate doesn't reach into a DbSet. Handler owns persistence.
- **Tracking, not AsNoTracking, on the load-before-mutate path.** Optimistic concurrency requires EF to track the original `version`. Forgetting this is a silent failure.
- **One `SaveChangesAsync` per command.** Don't sprinkle multiple saves. The whole transition + event + idempotency triple is one atomic write.
- **`InvalidTransitionException` is a `DomainException`** — the existing exception middleware maps `DomainException` → 422, but we want state conflicts to surface as 409. Add an explicit `catch (InvalidTransitionException)` clause in the middleware that returns 409, before the general `catch (DomainException)`.
- **Cursor pagination has one edge case that bites: timestamp ties.** Two payments with the same `created_at` (possible if the clock resolution rounds, or if seed data inserts in a single SQL batch). The tie-breaker on `id` desc handles this; tests must exercise it.
- **The capture endpoint accepts a body but the body is essentially `{}` in Phase 2** — the optional `amount_minor` field is forward-compat. Document it in the contract DTO comment.
- **Migration discipline.** Don't hand-edit the generated migration file unless something's clearly wrong. If you do, regenerate (`dotnet ef migrations remove` + `add` again) and re-inspect.

---

**WAITING FOR CONFIRMATION**: Proceed with this Phase 2 plan? (yes / no / modify)
