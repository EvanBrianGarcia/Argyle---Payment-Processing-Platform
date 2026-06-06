# ADR-0010: Settlement consumer is idempotent by row-lock + state check, not by message dedupe

**Status:** Accepted
**Date:** 2026-06-06
**Phase:** 3

## Context

The outbox dispatcher publishes-then-flips `dispatched_at` (ADR-0008). A crash between publish and flip causes a duplicate publish on the next poll. MassTransit's retry policy also re-delivers a message on transient consumer failure. RabbitMQ itself does not guarantee exactly-once delivery — at-least-once is the floor.

The consumer must be safe under duplicate delivery — receiving the same `SettlePayment` message twice must not double-settle, double-charge, or double-event the payment.

The textbook fix is an inbox table keyed on `MessageId`: the consumer checks for the message id, exits early if seen, otherwise processes and records the id. This is correct but costs a row write per delivery attempt (including the duplicates we're trying to absorb) and adds a second persistence concern alongside the `payments` and `payment_events` writes.

There's a simpler property in our domain: the **payment row's `Status` is authoritative**. If the payment is `Settled`, the work has already been done — regardless of which delivery attempt was the one that did it. The consumer can read the row state under a lock and decide based on that.

## Decision

The settlement consumer:

1. Opens a DB transaction.
2. Loads the payment row with `SELECT ... FOR UPDATE` (raw SQL via `FromSqlRaw`, tracked).
3. Checks `Status`:
   - If `Settled` → log "already settled, idempotent skip", commit (releases lock), ack.
   - If something other than `Captured` (e.g. `Refunded`) → log warning, commit, ack. This is the unexpected-state path; the message is no longer relevant.
   - If `Captured` → call `IPaymentProcessor.SettleAsync(...)`.
4. On `ProcessorResult.Success`: `payment.Settle(now)`, `_db.PaymentEvents.Add(evt)`, `SaveChangesAsync`, commit, ack.
5. On `ProcessorResult.TransientFailure`: throw `TransientSettlementException`. MassTransit retries per the configured policy. The transaction rolls back on throw, releasing the row lock cleanly.
6. On `ProcessorResult.PermanentFailure`: throw `PermanentSettlementFailureException`. The retry policy's `Ignore<PermanentSettlementFailureException>()` filter skips retry; the message routes to `settlement.dlq`.

No inbox table. No message-id dedupe. The payment's state under a row lock is the authoritative dedupe mechanism.

Permanent processor failures do **not** auto-transition the payment to `Failed`. They route to DLQ for human inspection.

## Consequences

- **One row write per successful settlement.** No inbox row, no extra index. The cost of duplicate delivery is one extra `SELECT FOR UPDATE` query, which is cheap.
- **The row lock is held for the duration of the processor call.** For the in-process stub (~ms) this is fine. A real processor with multi-second latency would block any concurrent mutation of the same payment (e.g. a manual refund attempt) for the call duration. Documented as a constraint to revisit when the real processor lands.
- **Permanent failures land in DLQ, not in the `Failed` state.** Auto-transitioning on a processor 4xx confuses two concerns: we don't know if our request was wrong (a bug we should fix) or if the processor's reasoning was wrong (a business decision a human needs to make). The DLQ + alert flow forces human inspection. The `Captured → Failed` transition still exists on the aggregate (Phase 2 wired it) for future callers like reconciliation jobs.
- **Concurrent delivery against the same payment is serialized by the row lock.** If MassTransit's prefetch delivers two copies to two consumer threads simultaneously, one acquires the lock, settles, commits; the other waits, sees `Settled`, idempotent-skips. The audit trail shows exactly one transition event.
- **`TransientSettlementException` vs `PermanentSettlementFailureException` is how MassTransit's retry policy distinguishes the two paths.** Adding a new permanent-failure case is one type and one entry in the `Ignore<>` list — no consumer-logic changes.

## Alternatives considered

- **Inbox table keyed on `MessageId`.** Rejected for Phase 3 on cost (one extra row per delivery attempt including duplicates) and on redundancy (the payment row's state already encodes whether the work has been done). Documented as the right answer for high-throughput tenants where duplicate-delivery rates are non-negligible.
- **`UPDATE payments SET status = 'Settled' WHERE id = ? AND status = 'Captured'` (compare-and-swap, no row lock).** Rejected. Skips the lock so the transaction commits faster, but doesn't serialize the processor call — two concurrent deliveries would both call the processor (one would then no-op the UPDATE). Calling the processor twice is what we're trying to avoid; the lock is what prevents it.
- **Optimistic concurrency on `payments.version` (Phase 2's idempotency-key pattern, no row lock).** Rejected. Optimistic concurrency throws on a write race after both deliveries have already called the processor. Pessimistic locking is the right model here.
- **Auto-transition `Captured → Failed` on permanent processor failure.** Rejected. See Consequences — the wrong default for an exercise that emphasizes operability. DLQ + human is the safer ladder.
