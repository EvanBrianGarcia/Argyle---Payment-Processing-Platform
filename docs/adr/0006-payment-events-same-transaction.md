# ADR-0006: `payment_events` rows persist in the same transaction as the `payments` update

**Status:** Accepted
**Date:** 2026-06-05
**Phase:** 2

## Context

Every state transition on a `Payment` produces a `PaymentEvent` row — the append-only audit log the dashboard and runbook both read. Phase 3 will introduce an outbox table (`payment_outbox`) for cross-process queue messages, written transactionally alongside the `payments` mutation that triggered them.

We need to decide whether `payment_events` participates in the same transaction as the payment update, or whether it goes through the same outbox-deferred mechanism Phase 3 adopts for queue messages.

The two candidates:

1. **Atomic write in the same `SaveChangesAsync` call.** The handler does `_db.Payments.Update`, `_db.PaymentEvents.Add`, `_db.IdempotencyKeys.Add`, then one `SaveChangesAsync`. All three rows commit (or all roll back) together.
2. **Outbox-deferred.** The handler writes only the payment update + a row to the outbox. A background dispatcher later writes the event row.

## Decision

Atomic write in a single `SaveChangesAsync` call. `payment_events` is NOT routed through the outbox.

Each state-changing handler:
1. Loads the `Payment` with EF tracking enabled (so optimistic concurrency on `version` is wired).
2. Calls the aggregate method, which returns the `PaymentEvent`.
3. Adds the event to `_db.PaymentEvents`.
4. Adds the idempotency record to `_db.IdempotencyKeys`.
5. Calls `_db.SaveChangesAsync(ct)` once.

EF Core wraps the multi-table insert in a transaction by default. On `DbUpdateConcurrencyException` (someone else moved `version` first), the entire batch rolls back — no orphan event row, no claimed idempotency key.

## Consequences

- **Audit invariant is a database invariant.** A state change without its event row is impossible by construction, not by convention. The runbook never has to ask "did we forget to write the event?"
- **No second store to coordinate.** The handler is a single linear path. `payment_events` and `payments` share the same backup, the same replica, the same restore drill.
- **Phase 3 builds additively, not as a replacement.** When the outbox arrives for settlement queue messages, `payment_outbox` is its own table. State-change events still go to `payment_events` in the same transaction. The outbox table holds *queue messages*, not domain events. Two tables, two purposes.
- **At ~30 req/s steady-state and 200 req/s peak (§2 of master plan), the extra row write per transition is rounding error.** Postgres handles multi-row transactional inserts at far higher throughput than our peak. No performance argument carries.
- **The aggregate stays the producer of events.** The transition method returns the `PaymentEvent`, the handler persists it. The aggregate never reaches into a DbSet.

## Alternatives considered

- **Outbox-deferred event writes.** Rejected. The outbox pattern solves the "two-system commit" problem — writing to a DB and publishing to a queue, where one of the two can fail. Both rows here live in the same DB, so there's no two-system problem to solve. Adopting the pattern anyway adds operational complexity (dispatcher lag monitoring, outbox-age alarms) with no correctness or scale benefit.
- **Fire-and-forget event write after the transaction commits.** Rejected — fundamentally breaks the audit guarantee. If the event write fails, we have a state change with no record of why.
- **Database trigger on `payments.status` change.** Rejected. Triggers move logic to a place where the aggregate's actor/reason fields can't be supplied, and EF migration discipline against triggers is fragile. The handler already knows everything it needs.
