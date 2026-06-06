# Phase 2 Task 7 Resume Note — CreatePayment Event + Response Shape Backfill

**Last session ended:** 2026-06-05, right after committing Task 6 (`29c922d`). Phase 2 Tasks 0–6 done. Task 7 is next.
**Branch:** `main`
**Working tree:** clean except `.claude/`.
**Latest commits (newest first):**
- `29c922d` feat: list payments endpoint with cursor pagination
- `72120a6` feat: refund payment endpoint and extract idempotency executor
- `59edd8b` feat: capture payment endpoint with optimistic concurrency
- `5f41413` feat: payment_events table and per-operation idempotency keys
- `303a3df` feat: payment state machine on the aggregate, with PaymentEvent

Confirm with `git log --oneline -5`.

## Where Phase 2 stands

| Task | Status | Commit |
|---|---|---|
| 0 — ADRs 0005/0006/0007 | Done | `6706697` |
| 1 — Phase 1 polish bugs | Done | `76bb208` |
| 2 — Domain state machine + PaymentEvent | Done | `303a3df` |
| 3 — PaymentEvent config + per-op idempotency + migration | Done | `5f41413` |
| 4 — CapturePayment slice + concurrency fix | Done | `59edd8b` |
| 5 — RefundPayment slice + IdempotencyExecutor | Done | `72120a6` |
| 6 — ListPayments + cursor pagination | Done | `29c922d` |
| **7 — CreatePayment event + response shape backfill** | **NEXT** | — |
| 8 — Cross-cutting state-machine E2E test | Pending | — |
| 9 — Acceptance walkthrough + README | Pending | — |

**Test count: 124 unit + 52 integration, all green.** First integration run takes ~20s (testcontainer cold start); subsequent runs reuse it.

## Why this task is small

Task 7's planned production scope is mostly **already done in prior tasks**. Verify each before doing anything:

| Plan item | Status | File |
|---|---|---|
| Append initial `null → Pending` event in `CreatePaymentCommandHandler` | **NOT DONE** — this is the only real production change | `Application/Features/CreatePayment/CreatePaymentCommandHandler.cs` |
| `GetPaymentQueryHandler` includes events list | Done in Task 4 — already loads events with `OrderBy(e => e.At)` | `Application/Features/GetPayment/GetPaymentQueryHandler.cs` |
| `PaymentResponse` exposes `UpdatedAt` and `Events` | Done in Task 4 — record already has both fields | `Contracts/Payments/PaymentResponse.cs` |
| `ConcurrencyConflictException` + `InvalidTransitionException` → 409 in middleware | Done in Task 4 — catches in correct order (InvalidTransition BEFORE general DomainException) | `Api/Middleware/ExceptionHandlingMiddleware.cs` |
| Route order: POST `/`, GET `/`, GET `/{id}`, POST `/{id}/capture`, POST `/{id}/refund` | Done in Task 6 — verify with a grep, no change expected | `Api/Endpoints/PaymentsEndpoints.cs` |
| Extract `PaymentResponseFactory.From(payment, events)` if duplication is heavy | Done in Task 4 — `PaymentResponseSerializer.ToResponse(payment, events)` already exists and is used by all four handlers | `Application/Common/PaymentResponseSerializer.cs` |

**The actual production diff is ~3 lines** in `CreatePaymentCommandHandler.CreateAsync`:

```csharp
_db.Payments.Add(payment);

// Replace this:
//   var response = PaymentResponseSerializer.ToResponse(payment, Array.Empty<PaymentEvent>());
//   return Task.FromResult(response);

// With this:
var initialEvent = payment.CreateInitialEvent(_clock.UtcNow);
_db.PaymentEvents.Add(initialEvent);
var response = PaymentResponseSerializer.ToResponse(payment, new[] { initialEvent });
return Task.FromResult(response);
```

`Payment.CreateInitialEvent(now, actor = "api")` already exists (verified in `Domain/Payments/Payment.cs:75`) and produces the `null → Pending` event with `reason = PaymentEventReason.Created` and empty payload. The default actor `"api"` is the right choice for the Create slice.

Drop the stale `// Task 7 will append the initial null → Pending event here` comment when you make the change.

## TDD discipline

Same RED → GREEN → REFACTOR → REVIEW cycle as Tasks 4–6. Load `ecc:tdd-workflow` first.

### RED

Two test files to add/modify. Write them BEFORE touching the handler.

**`tests/PaymentPlatform.IntegrationTests/CreatePaymentTests.cs`** — add two new tests, leave the existing 6 alone:

1. `HappyPath_ResponseIncludesInitialPendingEvent` — POST a valid payment; assert `events` array has length 1, `events[0].from_status` is null (JsonValueKind.Null), `events[0].to_status == "Pending"`, `events[0].reason == "Created"`, `events[0].actor == "api"`. Also verify by reading the `payment_events` table directly that exactly one row exists for the new payment id.
2. `HappyPath_ResponseIncludesUpdatedAt` — POST a valid payment; assert response has `updated_at`, parseable as `DateTimeOffset`, equal to `created_at` on initial create. (UpdatedAt is already on the response — this test just locks the contract.)

Style: mirror the existing `Returns201_WithPaymentId_OnHappyPath` test in that file. Use `TestJson.ParseAsync`, `FluentAssertions`, and the same `BuildCreateRequest`/`ValidPayload` helpers.

**`tests/PaymentPlatform.IntegrationTests/GetPaymentTests.cs`** — add one new test:

3. `HappyPath_ResponseIncludesEventTimeline` — use `TestDataBuilder.SeedCapturedPaymentAsync(AcmeKey)` then issue a Refund via HTTP (you'll mirror the BuildRefundRequest pattern from `RefundPaymentTests.cs`). GET the payment. Assert `events` array length is 4 with statuses in chronological order: `(null → Pending)`, `(Pending → Authorized)`, `(Authorized → Captured)`, `(Captured → Refunded)`. Assert `at` timestamps are strictly increasing.

Look at `GetPaymentTests.cs` first to see its existing style/helpers — don't duplicate setup if it already has request builders.

### Compile-time RED?

Probably runtime RED only — the test types don't reference any new C# types, just new assertions on the response shape. After writing tests, run them and confirm:
- `HappyPath_ResponseIncludesInitialPendingEvent` fails because `events` is currently empty (`Array.Empty<PaymentEvent>()` in the handler).
- `HappyPath_ResponseIncludesUpdatedAt` may already pass (the field exists). That's fine — write the test anyway to lock the contract; per ecc:tdd-workflow, a test that's already green still counts as documentation. Note this in the commit message.
- `HappyPath_ResponseIncludesEventTimeline` fails because the Create event is missing from the timeline (it currently shows 3 events: Authorize/Capture/Refund, not 4).

### GREEN

Three-line change in `CreatePaymentCommandHandler.CreateAsync` as shown above. Then `dotnet test`.

**Expected end state: 124 unit + 55 integration tests** (existing 52 + 3 new).

### REFACTOR

None expected. The plan says extract `PaymentResponseFactory.From(payment, events)` "if duplication is heavy" — it isn't, `PaymentResponseSerializer.ToResponse` already exists and is used everywhere. Skip.

### REVIEW

Trivial diff — `/code-review low` is enough, or self-review and skip the agent given recent cost levels. (Last session, the previous code-review agent call ran ~$8 worth of tokens for a much larger diff. A three-line change doesn't justify it.)

### COMMIT

Suggested message:

```
feat: write initial payment event on create

CreatePaymentCommandHandler now appends the null -> Pending event
inside the same transaction as the payment insert, closing the
loop on Phase 2's event-sourcing pattern. GetPayment's timeline
now starts at the create event instead of the first transition.

PaymentResponse.UpdatedAt and PaymentResponse.Events were added
in Task 4; the create-path tests added here lock those fields'
contracts so a future change to the serializer can't quietly drop
them.

Tests: 124 unit + 55 integration, all green.
```

## Decisions locked in (from prior tasks — do NOT re-litigate)

1. `PaymentResponseSerializer` in `Application/Common` is the one place that projects aggregates to DTOs. Don't add a parallel projection.
2. `Payment.CreateInitialEvent(now, actor)` defaults `actor` to `"api"` — that's the right value for the Create slice (the user-facing API is the originating actor).
3. `_idempotency.SaveAsync(record)` flushes everything tracked (payment, event, idempotency record) in one transaction. The `IdempotencyExecutor.work` callback runs against the same scoped `IPaymentsDbContext`. Adding `_db.PaymentEvents.Add(initialEvent)` inside `CreateAsync` will be flushed by the same SaveChangesAsync — no extra wiring needed.
4. The cached idempotency response body is the serialized `PaymentResponse`. Replays therefore include the initial event automatically once the handler emits it — no separate replay-path change needed.
5. `_clock.UtcNow` is the right timestamp source. `Payment.Create` already takes `now`; reuse the same `_clock.UtcNow` for `CreateInitialEvent` so `created_at == event.at` on the initial event.
6. No Claude attribution in commits — globally disabled.
7. `dotnet` not on PATH for the Bash subshell — wrap commands in `/bin/zsh -lc '...'`.
8. Docker daemon must be running for integration tests.

## Toolchain quirks (carried forward)

1. `dotnet test --no-build` reuses stale binaries — if you make a code change and want to re-run tests, drop `--no-build` to force a rebuild. (Bit us multiple times.)
2. The API uses `JsonIgnoreCondition.WhenWritingNull` globally — null properties are OMITTED, not emitted as `"name": null`. `events` is `IReadOnlyList<>` (never null, possibly empty), so it's always emitted. `updated_at` is `DateTimeOffset` (non-nullable), so it's always emitted. `from_status` on the initial event IS nullable and WILL be omitted entirely when null — assert via `TryGetProperty` or assert it's NOT present, not that it equals null. This bit us in Task 6's tests.
3. Tests use `JsonDocument` + `TestJson.ParseAsync`, not record deserialization — surface area is small even when DTO shape changes.
4. Cost warning hook fires at $50 (warning) and $75 (critical). Previous Task 5 review used ~$10 of agent tokens. For Task 7's tiny diff, skip the agent reviewer.

## Skills to load

| Skill | When | Why |
|---|---|---|
| `ecc:tdd-workflow` | First. | RED → GREEN → REFACTOR discipline. |
| `dotnet-skills:efcore-patterns` | Optional — only if the handler change surprises you. | Confirms the one-SaveChangesAsync-per-command rule, which is exactly what makes the three-line change work without a second db round-trip. |

**Do NOT load:**
- `dotnet-skills:csharp-api-design` (confirmed irrelevant — NuGet wire-compat, not minimal APIs).
- `ecc:code-review` agent unless something surprises you mid-task. Cost-justified to self-review a three-line diff.

## Useful commands

```bash
cd "/Users/evangarcia/Programing/Argyle - Payment Processing Platform"
git log --oneline -5
git status --short  # should show only `.claude/`

# Confirm GetPaymentQueryHandler already loads events (it does — Task 4 added it):
grep -A 10 "PaymentEvents" backend/src/PaymentPlatform.Application/Features/GetPayment/GetPaymentQueryHandler.cs

# Confirm route order:
grep -n "MapPost\|MapGet" backend/src/PaymentPlatform.Api/Endpoints/PaymentsEndpoints.cs

# Confirm Payment.CreateInitialEvent exists:
grep -n "CreateInitialEvent" backend/src/PaymentPlatform.Domain/Payments/Payment.cs

# Build
cd backend && /bin/zsh -lc 'dotnet build --nologo'

# Run only the new tests during development
cd backend && /bin/zsh -lc 'dotnet test --filter "FullyQualifiedName~CreatePaymentTests.HappyPath_ResponseIncludesInitialPendingEvent|FullyQualifiedName~CreatePaymentTests.HappyPath_ResponseIncludesUpdatedAt|FullyQualifiedName~GetPaymentTests.HappyPath_ResponseIncludesEventTimeline"'

# Full suite (no --no-build so changes take effect)
cd backend && /bin/zsh -lc 'dotnet test'
```

## Expected total time

15–25 minutes if nothing surprises you. The plan budgets 1 hour but most of Task 7's planned work was front-loaded into Tasks 4 and 6.
