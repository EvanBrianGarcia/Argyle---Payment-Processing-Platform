# ADR-0008: Outbox pattern for cross-process delivery of settlement jobs

**Status:** Accepted
**Date:** 2026-06-06
**Phase:** 3

## Context

Phase 2 finished the synchronous payment lifecycle: a capture moves a payment from `Authorized` to `Captured` in one HTTP request. Phase 3 needs to drive the next transition (`Captured → Settled`) asynchronously — settlement is the work that exercises a queue, retries, and the audit trail in the most representative way (master plan §7).

The naive way to publish the settlement job is direct from the capture handler: `_db.SaveChangesAsync()` to commit the state change, then `_bus.Publish(new SettlePayment(...))` to enqueue the work. This has a race window — if the DB commit succeeds and the publish fails (broker hiccup, process crash between the two calls), the payment is `Captured` but no settlement job ever runs. The reverse ordering loses messages in the other direction.

Master plan §14 names this dual-write hazard explicitly and commits us to the outbox pattern. This ADR records the implementation choices.

## Decision

When the capture handler transitions a payment to `Captured`, it inserts a row into a new `payment_outbox` table in the **same `SaveChangesAsync`** as the `payments` update and the `payment_events` append. The row carries a `SettlePayment` message envelope serialized to `jsonb`.

A separate `OutboxDispatcher`, a hosted `BackgroundService` running inside the API host, polls undispatched rows on a fixed interval (default 2s), publishes each via MassTransit, and flips `dispatched_at` to non-null.

Polling order is `created_at ASC, id ASC` for deterministic FIFO.

The dispatcher is **single-instance for Phase 3** — multi-instance support requires `SELECT ... FOR UPDATE SKIP LOCKED` on the poll query and is documented as future work. At exercise scale the single-host case is sufficient.

The dispatcher publishes **then** flips `dispatched_at` (never the reverse). A crash between publish and flip causes a duplicate publish on the next poll, which the idempotent consumer (ADR-0010) absorbs safely.

## Consequences

- **The dual-write race is closed.** Either both the state change and the outbox row commit, or neither does. The settlement job's existence is a property of the DB transaction, not of the broker's availability at the moment of capture.
- **Settlement latency is bounded by the poll interval plus broker delivery.** ~2s in the default configuration. This is acceptable for the exercise; production deployments can lower the interval or switch to `LISTEN/NOTIFY` if needed.
- **The dispatcher is the only writer of `dispatched_at`.** Application code never updates this column directly. The partial index `(created_at) WHERE dispatched_at IS NULL` keeps the poll query cheap even as the outbox grows.
- **The outbox is an internal implementation detail of the API host.** No external consumer reads from it. The Worker reads from RabbitMQ, not from Postgres.
- **Multi-region failover stays correct.** A standby region's dispatcher drains whatever outbox rows replicated over but didn't dispatch on the failed primary. No cross-region MQ coordination needed.

## Alternatives considered

- **Direct publish from the handler after `SaveChangesAsync`.** Rejected. The race window between commit and publish is exactly the problem the outbox pattern exists to solve.
- **`LISTEN/NOTIFY` instead of poll.** Rejected for Phase 3. Adds a long-lived DB connection per dispatcher instance and a fallback path on connection drop; the latency win (sub-second vs ~2s) doesn't matter for settlement at exercise scale. Documented as future work.
- **Outbox dispatcher in a separate process (not in the API host).** Rejected. Splitting the dispatcher into its own host adds operational surface (another container, another set of logs) for no correctness benefit. The dispatcher's scope-per-iteration pattern keeps it isolated from API request handling within the same host.
- **Combine `payment_outbox` with `payment_events`.** Rejected. The two tables serve different roles: events are an append-only domain audit log read by the same service that writes it, while outbox rows are queue messages destined for cross-process delivery and deleted (or archived) once dispatched. Conflating them muddles both responsibilities.
