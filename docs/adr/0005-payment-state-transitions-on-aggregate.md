# ADR-0005: Payment state transitions live on the `Payment` aggregate

**Status:** Accepted
**Date:** 2026-06-05
**Phase:** 2

## Context

Phase 2 introduces the payment lifecycle: `Pending → Authorized → Captured → Settled`, plus the `Failed` and `Refunded` terminal branches. Each transition is server-validated, server-recorded as a `payment_events` row, and protected by optimistic concurrency on `payments.version`.

We have to decide where the transition logic lives. The two candidates that were genuinely on the table:

1. **On the `Payment` aggregate** — methods like `Authorize(now)`, `Capture(now)`, `Refund(now, reason)` directly on the type, returning the resulting `PaymentEvent`.
2. **In a separate `IPaymentStateMachine` service** — the aggregate stays a passive data bag, the service holds the transition table and validates moves.

Phase 1 already established `Payment` as an encapsulated aggregate with private setters and a `Create` factory. The state column already has a DB-side `CHECK` constraint enumerating the legal states.

## Decision

State transitions are methods on the `Payment` aggregate.

Each transition method:
- Validates the current `Status` against the legal-source set for that transition.
- Throws `InvalidTransitionException` (a `DomainException` subtype) on illegal calls. The exception carries `from` and `to` so the API layer can render a precise error.
- Updates `Status` and the shadow `UpdatedAt` property on success.
- Returns the resulting `PaymentEvent` object. The handler appends it to `IPaymentsDbContext.PaymentEvents`.

The transition table (the canonical source — duplicated in §5 of the master plan):

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

The DB `CHECK` constraint on `status` is the second-line safety net. Application discipline is the first line.

## Consequences

- **The aggregate is the only place that knows the transition rules.** Handlers do not branch on status — they call, and they catch `InvalidTransitionException`.
- **Illegal transitions return HTTP 409.** The exception middleware adds an explicit `catch (InvalidTransitionException)` clause before the general `catch (DomainException)` (which maps to 422). Order matters in the middleware.
- **One test file owns the transition table.** `PaymentStateMachineTests` is table-driven across every `(from, to)` pair; legal pairs assert success, all others assert the exception. New states or transitions are one row of test data away.
- **`Phase 3`'s worker calls the same aggregate method.** When the settlement worker drives `Captured → Settled`, it loads the aggregate and calls `payment.Settle(now)`. No worker-side duplicate of the rules. The `Settled` transition is reachable from the aggregate in Phase 2 but is only exercised by a unit test until Phase 3 ships.

## Alternatives considered

- **Dedicated `IPaymentStateMachine` service.** Rejected. The transition logic is small (5 methods, one fixed table) and is a genuine aggregate invariant. Splitting it across two files would buy zero reuse — there is no second aggregate that shares these rules — and would weaken Phase 1's existing encapsulation. The service form makes sense when transitions are dynamic (configurable per-tenant, loaded from a DSL) or when multiple aggregates share a state model. Neither applies here.
- **A formal state-machine library (e.g. Stateless).** Rejected. The transition count is small enough that an explicit `switch` on the source status reads better than the library's fluent configuration, and it removes an external dependency that Phase 1 doesn't carry.
