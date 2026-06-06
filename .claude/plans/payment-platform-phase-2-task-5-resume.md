# Phase 2 Task 5 Resume Note — RefundPayment Slice

**Last session ended:** 2026-06-06, immediately after Task 4 committed (`59edd8b`). Phase 2 Tasks 0–4 done. Task 5 (Refund) is next.
**Branch:** `main`
**Working tree:** clean except `.claude/`.
**Latest commits (newest first):**
- `59edd8b` feat: capture payment endpoint with optimistic concurrency
- `5f41413` feat: payment_events table and per-operation idempotency keys
- `303a3df` feat: payment state machine on the aggregate, with PaymentEvent
- `76bb208` fix: carry request_id and trace_id through every log line and error response
- `6706697` docs: add ADRs 0005-0007 for Phase 2 state machine, events, idempotency

Confirm with `git log --oneline -5`.

## Where Phase 2 stands

| Task | Status | Commit |
|---|---|---|
| 0 — ADRs 0005/0006/0007 | Done | `6706697` |
| 1 — Phase 1 polish bugs | Done | `76bb208` |
| 2 — Domain state machine + PaymentEvent | Done | `303a3df` |
| 3 — PaymentEvent config + per-op idempotency + migration | Done | `5f41413` |
| 4 — CapturePayment slice + concurrency fix | Done | `59edd8b` |
| **5 — RefundPayment slice** | **NEXT** | — |
| 6 — ListPayments + cursor pagination | Pending | — |
| 7 — CreatePayment event + response shape backfill | Pending | — |
| 8 — Cross-cutting state-machine E2E test | Pending | — |
| 9 — Acceptance walkthrough + README | Pending | — |

**Test count: 114 unit + 32 integration, all green.** First integration run takes ~20s (testcontainer cold start); subsequent runs reuse it.

## TDD discipline (load `ecc:tdd-workflow` first)

Every task in Phase 2 — including Task 5 — follows **RED → GREEN → REFACTOR → REVIEW**. Plan §3 makes this explicit. Concretely for Task 5:

1. **RED.** Write `IntegrationTests/RefundPaymentTests.cs` BEFORE the handler. The file references types that don't exist (`RefundPaymentCommand`, `RefundPaymentRequest`, the endpoint route). Compile error → first red. Then runtime red as the assertions fail. Confirm tests run and fail BEFORE writing any production code.
2. **GREEN.** Implement the minimum production code to make those tests pass. Mirror Task 4's `CapturePayment` slice with Refund-specific differences (see below).
3. **REFACTOR.** This is the **third instance** of the idempotency dance (`Find` → conflict-check → `Add` → catch `DbUpdateException` → re-find). Plan §8 Task 4 says "let two be the first signal" — three is the extract signal. Extract `IdempotencyExecutor<TCommand, TResponse>` in `Application/Common/` and refactor CreatePayment + CapturePayment + RefundPayment to use it. Tests must stay green after each step.
4. **REVIEW.** Run `/code-review medium` on the diff. Address CRITICAL/HIGH; record LOW/MEDIUM judgments inline. Then commit.

## Skills to load explicitly

| Skill | When | Why |
|---|---|---|
| `ecc:tdd-workflow` | First. | Sets the Red → Green → Refactor discipline. Pin the workflow for the whole task. |
| `dotnet-skills:testcontainers` | Before writing the test file. | Reminds you the per-collection fixture pattern + the `TestDataBuilder` helper from Task 4 (`SeedCapturedPaymentAsync`) is the right way to seed Refund tests. |
| `dotnet-skills:efcore-patterns` | While writing the handler. | Tracking vs `AsNoTracking()` on the mutation path (must be tracking for the version concurrency token to fire). One `SaveChangesAsync` per command. The `PaymentVersionInterceptor` already handles the version bump — handler doesn't touch it. |
| `dotnet-skills:csharp-coding-standards` | While writing. | Sealed records, named args, immutable `IReadOnlyList<>` returns. |
| `ecc:code-review` | After GREEN. | Spawns `csharp-reviewer` on the diff. |
| `ecc:database-migrations` | NOT needed for Task 5. | No schema changes. |

**Do NOT load** `dotnet-skills:csharp-api-design` — Phase 1 confirmed it's about NuGet wire compatibility, not ASP.NET Core minimal APIs.

## Task 5 plan (the Refund slice)

**Goal.** Mirror Task 4 with three differences: required `reason` field, transition allowed from both `Captured` and `Settled` to `Refunded`, idempotency operation `"refund_payment"`.

### Files to write (new)

**Application/Features/RefundPayment/**:
- `RefundPaymentCommand.cs` — `IRequest<PaymentResponse>` with `PaymentId`, `IdempotencyKey`, `Reason` (required, non-empty string).
- `RefundPaymentCommandValidator.cs` — IdempotencyKey + PaymentId + Reason all non-empty.
- `RefundPaymentCommandHandler.cs` — nearly identical to `CapturePaymentCommandHandler`:
  - `IdempotencyOperations.RefundPayment` (already exists in `IdempotencyOperations.cs` from Task 3).
  - Calls `payment.Refund(_clock.UtcNow, reason)` instead of `Capture`. The aggregate's `Refund` accepts `Captured → Refunded` AND `Settled → Refunded`; only `Captured → Refunded` is reachable through the API in Phase 2 because nothing transitions to `Settled` yet (Phase 3's worker does).
  - Request hash includes `paymentId + reason` (NOT amountMinor — Refund doesn't accept amount in Phase 2).

**Contracts/Payments/**:
- `RefundPaymentRequest.cs` — `public sealed record RefundPaymentRequest(string Reason);`

**Tests/IntegrationTests/**:
- `RefundPaymentTests.cs` (`[Collection(IntegrationTestCollection.Name)]`, inherits `IntegrationTestBase`):
  - `Returns200_OnCapturedPayment_TransitionsToRefundedAndAppendsEvent` — happy path. Verify event row's `payload` jsonb contains `{"reason": "..."}`.
  - `Returns200_WithIdenticalBody_OnReplaySameKeyAndBody_NoNewEventRow` — replay parity.
  - `Returns409_OnSameKeyDifferentBody_IdempotencyKeyConflict` — different reason same key → 409.
  - `Returns409_OnPendingPayment_InvalidStateTransition_AppendsNoEvent`.
  - `Returns409_OnAuthorizedPayment_InvalidStateTransition_AppendsNoEvent` — Refund rejects Authorized too.
  - `Returns404_OnUnknownPaymentId`.
  - `Returns404_OnCrossMerchant`.
  - `Returns400_WhenIdempotencyKeyMissing`.
  - `Returns400_WhenReasonMissing` — Refund-specific (Capture doesn't require body fields).
  - **Skip** the concurrent-refund race + deterministic version-mismatch tests — Task 4 already proved the concurrency mechanism; duplicating it for Refund doesn't add signal.

Use `TestDataBuilder.SeedCapturedPaymentAsync(AcmeKey)` from `Fixtures/TestDataBuilder.cs` — it already chains Pending → Authorized → Captured via in-process aggregate calls, the way Phase 3's worker will.

### Files to extend

- `Api/Endpoints/PaymentsEndpoints.cs`:
  - Add `group.MapPost("/{id}/refund", RefundPaymentAsync);`
  - Implement `RefundPaymentAsync` mirroring `CapturePaymentAsync`. Note the request body IS required (Reason) — don't default to empty.

- No changes needed to `ExceptionHandlingMiddleware.cs` — InvalidTransitionException + ConcurrencyConflictException already mapped from Task 4.

### Then the REFACTOR step (third-instance extraction)

After Refund's GREEN passes, extract the idempotency pattern. Three handlers will share the same dance:
1. Compute request hash.
2. `_idempotency.FindAsync(merchantId, operation, key, ct)`.
3. If existing AND hash mismatches → throw `IdempotencyConflictException`.
4. If existing AND hash matches → return `PaymentResponseSerializer.Deserialize(existing.ResponseBody)`.
5. Run the work (handler-specific).
6. Build response + responseBody, build `IdempotencyKeyRecord`, call `_idempotency.SaveAsync(record, ct)`.
7. Catch `DbUpdateConcurrencyException` → throw `ConcurrencyConflictException`.
8. Catch `DbUpdateException` → re-find + return cached body (defensive for same-key races).

Sketch of the extracted class in `Application/Common/IdempotencyExecutor.cs`:

```csharp
public sealed class IdempotencyExecutor
{
    private readonly IIdempotencyStore _idempotency;
    private readonly IClock _clock;
    // ctor injects both

    public async Task<TResponse> RunAsync<TResponse>(
        string merchantId,
        string operation,
        string idempotencyKey,
        string requestHash,
        int successStatus,
        Func<CancellationToken, Task<TResponse>> work,
        CancellationToken ct)
    {
        var existing = await _idempotency.FindAsync(merchantId, operation, idempotencyKey, ct);
        if (existing is not null)
        {
            if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
                throw new IdempotencyConflictException();
            return Deserialize<TResponse>(existing.ResponseBody);
        }

        var response = await work(ct);
        var record = new IdempotencyKeyRecord(
            merchantId, operation, idempotencyKey, requestHash,
            successStatus, Serialize(response), _clock.UtcNow);

        try { await _idempotency.SaveAsync(record, ct); }
        catch (DbUpdateConcurrencyException) { throw new ConcurrencyConflictException(); }
        catch (DbUpdateException)
        {
            var winner = await _idempotency.FindAsync(merchantId, operation, idempotencyKey, ct);
            if (winner is not null) return Deserialize<TResponse>(winner.ResponseBody);
            throw;
        }
        return response;
    }
}
```

This is a sketch — adapt to whatever generic constraints and serialization signature actually compile. The key constraint: the `work` callback must use the SAME injected `IPaymentsDbContext` so the aggregate update + new event row land in the same `SaveChangesAsync` as the idempotency record.

After the extraction:
- CreatePayment, CapturePayment, RefundPayment all shrink by ~30 lines each.
- All 32 (now 38+) integration tests must still pass — extraction is behavior-preserving.

**If the extraction looks shaky** (subtle generic typing, dependency-graph tangle), back it out and leave the three handlers duplicated. The plan calls for this only when it cleanly fits.

### Validate

1. `/bin/zsh -lc 'cd backend && dotnet build --nologo'` — 0 warnings, 0 errors.
2. `docker info` — Docker daemon up.
3. `/bin/zsh -lc 'cd backend && dotnet test --nologo'` — Phase 1's 16 integration tests still pass, Phase 2's 32 still pass, ~9 new Refund tests pass. Expected total: 114 unit + 41 integration.

### Suggested commit message (plan §9 Task 9 commit cadence)

```
feat: refund payment endpoint and extract idempotency executor

Adds POST /v1/payments/{id}/refund mirroring the capture slice
with a required reason field stored in the event payload jsonb.
Refund accepts Captured → Refunded and Settled → Refunded; only
Captured is reachable through the API in Phase 2 (Phase 3's
worker will reach Settled).

Refund is the third handler to repeat the idempotency dance
(Find → conflict-check → Add → catch DbUpdateException → re-find).
Extracts IdempotencyExecutor in Application/Common and refactors
CreatePayment, CapturePayment, and RefundPayment to use it. The
work callback runs inside the executor's atomic save so the
payment update + event row + idempotency record commit together.

Tests: 114 unit + 41 integration, all green.
```

If the executor extraction doesn't fly, commit just the Refund slice and drop the executor mention from the message.

## Decisions locked in (from Phase 1 + Phase 2 so far — do NOT re-litigate)

Carried forward from prior resume notes plus this session:

1. **Optimistic concurrency: `PaymentVersionInterceptor` (Task 4) bumps the `Version` shadow property on every modified `Payment`.** This was needed because EF Core's `IsConcurrencyToken()` only generates WHERE clauses; it does NOT auto-increment. Without the interceptor two concurrent captures both succeeded silently. Refund inherits this behavior automatically — the handler doesn't touch version, the interceptor does it on save.
2. **`PaymentResponseSerializer` in `Application/Common`** centralizes JSON options and aggregate→DTO projection. Use it from Refund's handler too. Don't reinvent the JSON options anywhere.
3. **`InvalidTransitionException` and `ConcurrencyConflictException` are mapped to 409 in `ExceptionHandlingMiddleware`** — Task 4 added the catches BEFORE the general `DomainException → 422` catch. Refund inherits these mappings.
4. **The handler must use tracking on the load-before-mutate path** so EF retains the original Version. `AsNoTracking()` breaks optimistic concurrency.
5. **One `SaveChangesAsync` per command.** `_idempotency.SaveAsync(record)` flushes everything (payment update, event row, idempotency record) in one transaction. Don't split.
6. **No Claude attribution in commits** — globally disabled.
7. **`dotnet` not on PATH for the Bash subshell** — wrap commands in `/bin/zsh -lc '...'`.
8. **Docker daemon must be running** for integration tests.

## Toolchain quirks (carried forward)

1. `dotnet test --no-build` reuses stale binaries — if you make a code change and want to re-run tests, drop `--no-build` to force a rebuild. (Bit us once this session.)
2. The cost warning hook fires aggressively at $50+. Plan time for it.
3. `dotnet build` after the version interceptor + EF model changes occasionally reports `PendingModelChangesWarning` if you try a non-Npgsql concurrency token approach. The current approach (the interceptor) does NOT trigger this — no migration needed.

## Why Task 5 is "routine" — and what to watch for anyway

The Capture and Refund slices are 90% identical. The interesting work is the third-instance refactor. Things that bit us in Task 4 that DON'T apply to Task 5:
- Optimistic concurrency design gap (fixed in Task 4).
- `PaymentResponse` shape extension (done in Task 4).
- Wiring exception → 409 mapping (done in Task 4).

Things to watch:
- Refund's `Reason` field must end up in the `payment_events.payload` jsonb. The aggregate's `Refund(now, reason, actor)` already does this (`{"reason": reason}`). Verify the round-trip in the happy-path integration test.
- The validator must reject missing `Reason` with 400 (Refund-specific test).
- If you extract `IdempotencyExecutor`, verify the cached-response deserialization path still uses `PaymentResponseSerializer.Deserialize` (not a generic deserializer with different JSON options).

## Useful commands

```bash
# Confirm starting state
cd "/Users/evangarcia/Programing/Argyle - Payment Processing Platform"
git log --oneline -5
git status --short  # should show only `.claude/`

# Build
cd backend && /bin/zsh -lc 'dotnet build --nologo'

# Run unit tests only (fast, no Docker)
cd backend && /bin/zsh -lc 'dotnet test tests/PaymentPlatform.UnitTests'

# Check Docker before integration tests
docker info >/dev/null 2>&1 && echo "ok" || echo "start Docker first"

# Run only Refund tests during development
cd backend && /bin/zsh -lc 'dotnet test --filter "FullyQualifiedName~RefundPayment"'

# Full suite
cd backend && /bin/zsh -lc 'dotnet test'
```
