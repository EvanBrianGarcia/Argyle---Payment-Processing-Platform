# Plan: Payment Platform — Phase 3 (Async Settlement Workflow)

**Source plan**: `.claude/plans/payment-platform.plan.md` (master plan §7 Async Workflow, §8 `payment_outbox`, §9 queue metrics, §13 Phase 3 deliverable)
**Entry note**: `.claude/plans/payment-platform-phase-3-entry-resume.md` (cost guardrails, skim-only Phase 2 patterns, skills load list)
**Goal**: Move settlement off the request path. Prove the outbox pattern works end-to-end — a captured payment reliably reaches `Settled` via RabbitMQ with retry + DLQ, and the worker is idempotent under duplicate delivery.
**Complexity**: Large (~10 hours of focused work, 1 new project, 1 new aggregate-adjacent table, 1 schema migration, 1 new RabbitMQ-backed integration fixture, ~30 new files)

## 1. Summary

Phase 3 turns the synchronous `Authorized → Captured` step from Phase 2 into the entry point for an asynchronous settlement workflow. When a capture succeeds, the same `SaveChangesAsync` that updates `payments` and appends a `payment_events` row also inserts a `payment_outbox` row carrying a `SettlePayment` message. A new `BackgroundService` polls undispatched outbox rows and publishes them to RabbitMQ via MassTransit. A new `PaymentPlatform.Worker` host consumes those messages, loads the payment under a `FOR UPDATE` row lock, calls a stub `IPaymentProcessor`, transitions `Captured → Settled`, and appends the closing `PaymentEvent` — all idempotent by construction so duplicate delivery is safe.

The outbox is what closes the race window between "DB commit succeeded" and "queue publish succeeded" — a dual-write hazard that direct publishing has and that the master plan explicitly calls out (§14 Tradeoffs).

Phase 3 deliberately does NOT add: webhook delivery, reconciliation against a real processor, the OTLP exporter wiring, the Prometheus `/metrics` endpoint beyond what already exists, or the frontend. Those land in Phases 4–5. `/health/ready` gets the RabbitMQ check now (because we're standing the broker up anyway), but the full observability surface stays Phase 4 work.

## 2. What "done" looks like (the acceptance walkthrough)

Against `docker compose up` (with the new `rabbitmq:3-management` service and worker container):

1. `POST /v1/payments` returns 201 in `Pending` (Phase 1/2 behavior, still passing).
2. `POST /v1/payments/{id}/capture` on an `Authorized` payment returns 200 in `Captured` (Phase 2 behavior). A new row appears in `payment_outbox` with `dispatched_at IS NULL` and a `SettlePayment` payload referencing the payment id.
3. Within ~2 seconds, the outbox dispatcher polls the row, publishes a message to the `settlement` exchange, and marks `dispatched_at`. The dispatcher log line carries the payment id and the trace id from the originating capture request.
4. The worker consumes the message, locks the payment row, calls the stub `IPaymentProcessor` (configured to succeed by default), transitions `Captured → Settled`, appends an `Authorized→Captured`-style event to `payment_events`, commits, acks.
5. `GET /v1/payments/{id}` returns the payment in `Settled` with 5 events in the audit timeline: `null→Pending`, `Pending→Authorized`, `Authorized→Captured`, `Captured→Settled`, and the `actor` field is `worker` on the final transition.
6. Pull all logs by the originating `trace_id` — you see the full story: capture span (API), dispatcher publish line, MassTransit publish span, MassTransit consume span (worker), processor call, state update, commit.
7. Re-deliver the same message (simulate via `docker compose exec` rerun or RabbitMQ admin): worker acks immediately, does NOT append a second event row, payment stays `Settled`. This is the idempotent-consumer contract from ADR-0010 in action.
8. Configure the stub processor to fail-twice-then-succeed via the worker's config. Capture a fresh payment. Observe 2 retry attempts in the worker log (1s, 4s backoff per MassTransit policy) before the third attempt succeeds. The payment lands in `Settled`. The audit trail still shows one settlement event, not three.
9. Configure the stub processor to always-fail-permanent. Capture a fresh payment. After 5 attempts (1s/4s/16s/64s/256s — actually shortened in test config), the message lands in `settlement.dlq`. The payment stays in `Captured` (no `Failed` transition — permanent processor failure is a human-investigation case, not an auto-state-transition). Queue depth on `settlement.dlq` is visible in logs.
10. `GET /v1/health/ready` returns 200 when RabbitMQ + Postgres are up; returns 503 when RabbitMQ is down. `GET /v1/health/live` returns 200 unconditionally (unchanged from Phase 1).
11. `dotnet test` runs and passes. Phase 1+2's tests still green; Phase 3 adds ~25 new tests (outbox unit, dispatcher integration, worker happy/retry/DLQ, end-to-end, health check).

## 3. Skills and TDD discipline

### Skills to leverage

| Skill | Where |
|---|---|
| `ecc:tdd-workflow` | Every task. Write the failing test first, run it red, write the minimum code to green, refactor. Each task below is structured Red → Green → Refactor. |
| `dotnet-skills:csharp-coding-standards` | All C# we touch — records for messages, immutability, sealed types, explicit access modifiers. The MassTransit `IConsumer<T>` implementation is a sealed class, the message contract is a `record`. |
| `dotnet-skills:efcore-patterns` | The outbox write happens **inside the same `SaveChangesAsync`** as the capture (this is the core invariant the pattern exists to enforce). The settlement consumer uses `AsTracking()` on its row-locked load. The dispatcher uses `ExecuteUpdateAsync` for the `dispatched_at` flip. |
| `dotnet-skills:testcontainers` | New `RabbitMqFixture` mirrors the existing `PostgresFixture` shape — `IAsyncLifetime`, container per test class collection, reset between tests via queue purge. Combined fixture for end-to-end tests that need both. |
| `docs-lookup` (Context7) | MassTransit v8 retry policy / DLQ / consumer config syntax. The skill catalog has no MassTransit-specific entry; pull official MassTransit docs on demand. |
| `ecc:database-migrations` | Reviewing the `payment_outbox` migration — partial index syntax for `(created_at) WHERE dispatched_at IS NULL`, reversible `Down()`. |
| `ecc:architecture-decision-records` | The format for ADRs 0008/0009/0010 in Task 0. |
| `ecc:api-design` | Confirming the `SettlePayment` message contract is stable (consumer-facing schema) and the `/health/ready` response body is consistent with the existing error envelope. |
| `ecc:code-review` | Run after each feature task completes (after green tests). |
| `csharp-reviewer` agent | Auto-fired by `code-review` skill for any C# changes. |

Skills NOT used in Phase 3 but referenced for later phases:
- `dotnet-skills:opentelementry-dotnet-instrumentation` (Phase 4 owns full OTLP wiring; Phase 3 only propagates the existing `trace_id` through the message envelope and Serilog `LogContext`).
- `dotnet-skills:aspire-*` (we are not on Aspire; the worker is a plain `Microsoft.Extensions.Hosting` `IHost`).
- `dotnet-skills:akka-*` (wrong stack).
- `ecc:e2e-testing` (Phase 5 frontend).

### TDD ordering (universal rule for Phase 3)

For every task below, the order is:

1. **RED** — Write the failing test(s) first. Confirm they fail with the expected runtime error, not just a compile error (after you stub the type signatures).
2. **GREEN** — Write the minimum production code to make those tests pass.
3. **REFACTOR** — Clean up names, extract helpers if duplication appeared, re-run tests.
4. **REVIEW** — Invoke `ecc:code-review` (which spawns `csharp-reviewer`) on the diff. Address CRITICAL and HIGH findings before moving on.
5. **VALIDATE** — Run the task-specific validation step at the end of the task description.

Each task's tests are listed BEFORE the implementation work for that task. Worker / dispatcher tasks have the highest risk of flake — every async assertion uses `await Task.Delay`-free synchronization (Testcontainers `WaitForLog`, MassTransit `ITestHarness`, or DB-state polling with a bounded retry helper). If a test depends on a fixed sleep, that's a bug in this plan — flag it.

## 4. Architectural decisions to record (Task 0)

Three open decisions resolve up front. **ADRs live under `docs/adr/`** alongside Phase 2's 0005/0006/0007. Keep each to ~30 lines.

### ADR-0008: Outbox pattern for cross-process delivery of settlement jobs

**Choice.** When the capture handler transitions a payment to `Captured`, it inserts a row into `payment_outbox` in the **same `SaveChangesAsync`** as the `payments` update and the `payment_events` append. A separate `OutboxDispatcher` (a hosted `BackgroundService` running inside the API) polls undispatched rows, publishes them via MassTransit, and flips `dispatched_at`.

**Why not direct publish from the handler.** Direct publish has a race window: if the DB commit succeeds and the publish fails (broker hiccup, process crash between the two calls), the payment is `Captured` but no settlement job ever runs. The outbox closes the window by making the message a row in the same transaction as the state change. The dispatcher's job is idempotent ("publish whatever is undispatched"), so a crash mid-publish just means the next poll re-publishes — and the consumer is idempotent against that (ADR-0010).

**Why poll, not `LISTEN/NOTIFY`.** Polling is simpler, scales to multi-instance API hosts via `SELECT ... FOR UPDATE SKIP LOCKED`, and adds at most ~2s of latency at the configured poll interval (acceptable for settlement). `LISTEN/NOTIFY` adds complexity (a long-lived DB connection per host, fallback to poll on connection drop) for a latency win we don't need at exercise scale. Master plan §7 already specifies poll.

**Consequence.** `payment_outbox` is an internal implementation detail of the API — no external observer reads it. The dispatcher is the only thing that mutates `dispatched_at`. We can add a multi-instance dispatcher later by adding `SKIP LOCKED` to the query; for Phase 3 the single-host case is sufficient.

### ADR-0009: MassTransit over raw RabbitMQ.Client for the publisher and consumer

**Choice.** Use MassTransit v8 with the RabbitMQ transport. The API registers the publisher via `AddMassTransit(...).AddRabbitMqHost(...)`. The Worker host registers a consumer with `AddConsumer<SettlePaymentConsumer>` and a per-receive-endpoint retry policy.

**Why not raw `RabbitMQ.Client`.** Raw `IConnection`/`IModel` works fine, but we'd hand-write the consumer dispatch loop, the retry/DLQ topology, the trace propagation, and the test harness. MassTransit gives us all four out of the box with idiomatic .NET patterns we'd reproduce badly. The cost is one mid-weight dependency; the benefit is ~300 lines of infrastructure code we don't write.

**Why MassTransit over Wolverine, NServiceBus, EasyNetQ.** MassTransit has the deepest .NET community footprint, first-party OTel instrumentation (Phase 4 will use this), and a built-in `ITestHarness` that drives MassTransit-aware integration tests without standing up a real broker (we'll still use Testcontainers RabbitMQ for end-to-end coverage). NServiceBus is licensed; Wolverine is newer and less battle-tested; EasyNetQ is thinner and we'd lose the harness.

**Consequence.** Message contracts live in a shared assembly (`PaymentPlatform.Messaging`, new). Retry policy and DLQ are configured at the consumer's receive endpoint, not on the publisher side. The dispatcher publishes via `IPublishEndpoint` (MassTransit's exchange-routing primitive), not `ISendEndpoint` — settlement is conceptually a domain event, not a directed command.

### ADR-0010: Settlement consumer is idempotent by row-lock + state check, not by message dedupe

**Choice.** The consumer loads the payment row with `SELECT ... FOR UPDATE`, checks the current state, and short-circuits if already `Settled`. No message-id dedupe table. The retry policy retries transient failures; the state check makes duplicate delivery safe.

**Why not an `inbox` table keyed on `MessageId`.** An inbox table is the textbook dedupe mechanism, but it doubles the write cost (one row per delivery attempt) and the state check on the payment is already authoritative — if the payment is `Settled`, the message has already been processed regardless of which delivery attempt succeeded. The row lock prevents the concurrent-delivery hazard. We document the inbox option as future work for high-throughput tenants.

**Why permanent failures don't auto-transition to `Failed`.** Master plan §5 lets `Captured → Failed` exist as an aggregate transition, but auto-transitioning on processor 4xx confuses two concerns (we don't know if the processor is wrong or our request is wrong without human review). DLQ + alert is the right surface. Permanent-failure messages sit in `settlement.dlq` until a human inspects them and re-publishes or marks the payment as `Failed` manually via a future admin tool.

**Consequence.** Transient failures (network timeouts, processor 5xx, lock contention) → retry. Permanent failures (validation errors, processor 4xx, invalid-state errors raised by the aggregate) → ack-and-dead-letter via MassTransit's `Fault<T>` pipeline. The consumer surfaces this distinction by catching specific exception types and re-throwing transient ones; permanents are wrapped in `PermanentSettlementFailureException` (a marker type) which MassTransit's filter routes to DLQ without retry.

## 5. Out of scope (and where it lands)

| Out | Lands in |
|---|---|
| Webhook delivery to merchants | Future improvement (master plan §17) — same outbox + worker pattern with HTTP delivery and per-merchant retry policy |
| Reconciliation against the real processor | Future improvement (master plan §17) — daily batch comparing our state against a processor report |
| Real card-network adapter | Production hardening (master plan §16) — replaces the stub `IPaymentProcessor` |
| Multi-region active-passive (replicated outbox dispatcher per region) | Production hardening — design noted in master plan §13/§16; the regional-broker invariant is preserved by Phase 3's design (no cross-region MQ assumptions in code) |
| Full OpenTelemetry SDK + OTLP exporter wiring | Phase 4 — Phase 3 only propagates the existing `trace_id` through the message envelope and the worker's Serilog `LogContext` |
| Prometheus `/metrics` endpoint with the full queue-metrics list from master plan §9 | Phase 4 (Phase 3 logs queue activity but doesn't expose `mq_queue_depth` etc.) |
| Log redaction enricher | Phase 4 (no PAN ever reaches a log line in Phase 3 — we never see one; the stub processor returns a token, not a PAN) |
| Frontend dashboard | Phase 5 |
| Background TTL sweep for `idempotency_keys` and dispatched outbox rows older than 24h | Phase 4 polish or production hardening |
| Multi-instance dispatcher with `SELECT ... FOR UPDATE SKIP LOCKED` | Documented in ADR-0008; single-host dispatcher for Phase 3 is sufficient |

## 6. Solution-level additions

One new project (`PaymentPlatform.Worker`) and one new shared messaging assembly (`PaymentPlatform.Messaging`). Existing projects gain files; none get restructured.

```
backend/
├── src/
│   ├── PaymentPlatform.Messaging/              [NEW PROJECT]
│   │   ├── PaymentPlatform.Messaging.csproj
│   │   └── Settlement/
│   │       ├── SettlePayment.cs                [NEW] (record - message contract)
│   │       └── SettlementQueues.cs             [NEW] (queue/exchange name constants)
│   ├── PaymentPlatform.Domain/
│   │   └── Outbox/
│   │       ├── PaymentOutboxMessage.cs         [NEW] (aggregate-adjacent entity)
│   │       └── OutboxMessageType.cs            [NEW] (string-constant class)
│   ├── PaymentPlatform.Application/
│   │   ├── Abstractions/
│   │   │   ├── IPaymentsDbContext.cs           [EXTEND with DbSet<PaymentOutboxMessage>]
│   │   │   ├── IPaymentProcessor.cs            [NEW] (interface — stub adapter seam)
│   │   │   ├── IOutboxPublisher.cs             [NEW] (MassTransit IPublishEndpoint wrapper for testability)
│   │   │   └── ICorrelationContext.cs          [NEW] (read current trace_id/request_id)
│   │   ├── Common/
│   │   │   └── OutboxMessageFactory.cs         [NEW] (constructs SettlePayment message + outbox row from a Payment)
│   │   └── Features/
│   │       └── CapturePayment/
│   │           └── CapturePaymentCommandHandler.cs    [EXTEND: enqueue outbox row inside same SaveChangesAsync]
│   ├── PaymentPlatform.Infrastructure/
│   │   ├── Outbox/
│   │   │   └── OutboxPublisher.cs              [NEW] (thin wrapper over MassTransit IPublishEndpoint)
│   │   ├── Processing/
│   │   │   ├── StubPaymentProcessor.cs         [NEW] (configurable: always-succeed / fail-N-then-succeed / always-fail-permanent)
│   │   │   └── StubProcessorOptions.cs         [NEW] (POCO options)
│   │   ├── Persistence/
│   │   │   ├── Configurations/
│   │   │   │   └── PaymentOutboxMessageConfiguration.cs   [NEW]
│   │   │   └── Migrations/
│   │   │       └── <ts>_PaymentOutbox.cs       [GENERATED]
│   │   └── DependencyInjection/
│   │       └── MessagingServiceCollectionExtensions.cs   [NEW] (MassTransit publisher registration)
│   ├── PaymentPlatform.Api/
│   │   ├── HostedServices/
│   │   │   └── OutboxDispatcher.cs             [NEW] (BackgroundService — polls + publishes)
│   │   ├── HealthChecks/
│   │   │   └── RabbitMqHealthCheck.cs          [NEW]
│   │   ├── Endpoints/
│   │   │   └── HealthEndpoints.cs              [EXTEND: /health/ready now includes RabbitMQ]
│   │   ├── Configuration/
│   │   │   └── OutboxDispatcherOptions.cs      [NEW] (poll interval, batch size)
│   │   └── Program.cs                          [EXTEND: MassTransit publisher registration, OutboxDispatcher registration, RabbitMq health check registration]
│   └── PaymentPlatform.Worker/                 [NEW PROJECT]
│       ├── PaymentPlatform.Worker.csproj
│       ├── Program.cs                          [Host.CreateApplicationBuilder + MassTransit consumer + DbContext + IPaymentProcessor]
│       ├── Dockerfile
│       ├── appsettings.json
│       ├── Consumers/
│       │   ├── SettlePaymentConsumer.cs        [NEW] (IConsumer<SettlePayment>)
│       │   └── PermanentSettlementFailureException.cs    [NEW] (marker type for DLQ routing)
│       ├── Configuration/
│       │   └── WorkerOptions.cs                [NEW]
│       └── Correlation/
│           └── MassTransitCorrelationFilter.cs [NEW] (sets Serilog LogContext from message envelope)
└── tests/
    ├── PaymentPlatform.UnitTests/
    │   ├── Application/
    │   │   └── Outbox/
    │   │       └── OutboxMessageFactoryTests.cs           [NEW]
    │   └── Infrastructure/
    │       └── Processing/
    │           └── StubPaymentProcessorTests.cs           [NEW]
    └── PaymentPlatform.IntegrationTests/
        ├── Fixtures/
        │   ├── RabbitMqFixture.cs                          [NEW] (mirror PostgresFixture)
        │   ├── MessagingFixture.cs                         [NEW] (combined collection: Postgres + RabbitMq + Api + Worker)
        │   └── PostgresFixture.cs                          [EXTEND: TRUNCATE list adds payment_outbox]
        ├── OutboxWriteOnCaptureTests.cs                    [NEW]
        ├── OutboxDispatcherTests.cs                        [NEW]
        ├── SettlementConsumerTests.cs                      [NEW] (uses MassTransit ITestHarness — no real broker)
        ├── SettlementEndToEndTests.cs                      [NEW] (uses RabbitMqFixture — real broker)
        ├── SettlementRetryTests.cs                         [NEW]
        ├── SettlementDeadLetterTests.cs                    [NEW]
        └── HealthReadyTests.cs                             [NEW]
```

## 7. Database schema additions (one migration)

### `payment_outbox` (new table)

Matches master plan §8 exactly.

| Column | Type | Notes |
|---|---|---|
| `id` | `bigserial PK` | monotonic for poll-order stability |
| `aggregate_id` | `text NOT NULL` | `payment_id` — indexed for diagnostics |
| `message_type` | `text NOT NULL` | e.g. `SettlePayment` — leaves room for future message types |
| `payload` | `jsonb NOT NULL` | serialized message envelope (the same `SettlePayment` record body the consumer receives) |
| `correlation_id` | `text NOT NULL` | the originating capture request's `trace_id`, propagated through the message envelope so the consumer's spans inherit the parent |
| `created_at` | `timestamptz NOT NULL DEFAULT now()` | |
| `dispatched_at` | `timestamptz NULL` | null until the dispatcher publishes; non-null afterwards |

Indexes:
- `(created_at) WHERE dispatched_at IS NULL` — **partial index**. This is the index that keeps the dispatcher's poll cheap even as the outbox grows; without `WHERE`, the index would have one entry per row of history.
- `(aggregate_id)` — supports diagnostic queries like "what messages have we ever published for payment X".

No `UPDATE` of payload columns. `dispatched_at` is the only field that gets flipped, via `ExecuteUpdateAsync` for efficiency.

### `payments`, `payment_events`, `idempotency_keys` — no changes

The outbox row is additive. Phase 2's `IsConcurrencyToken()` on `payments.version` and the `payment_events` append-only contract carry over unchanged.

### Migration discipline

Generate via:
```bash
/bin/zsh -lc 'cd backend && dotnet ef migrations add PaymentOutbox \
  --project src/PaymentPlatform.Infrastructure --startup-project src/PaymentPlatform.Api'
```

Inspect the generated migration (per `ecc:database-migrations` checklist):
- `payment_outbox` table created with `bigserial` PK (npgsql translates this to `bigint GENERATED BY DEFAULT AS IDENTITY`).
- Partial index emits `CREATE INDEX ... ON payment_outbox (created_at) WHERE dispatched_at IS NULL`.
- `Down()` drops the table cleanly.

If `migrationBuilder` doesn't render the `WHERE` filter natively, supplement with `migrationBuilder.Sql("CREATE INDEX ix_payment_outbox_undispatched ON payment_outbox (created_at) WHERE dispatched_at IS NULL;")` and a matching `DROP INDEX` in `Down()`. The Phase 2 migration adds a precedent for hand-supplemented SQL inside a generated migration; mirror that style.

## 8. NuGet packages

| Project | Add | Why |
|---|---|---|
| `PaymentPlatform.Messaging` | none beyond the SDK | pure contracts — no MassTransit dependency, so the assembly is reusable by future consumers without dragging MT in |
| `PaymentPlatform.Application` | none | uses only the abstractions it owns |
| `PaymentPlatform.Infrastructure` | `MassTransit`, `MassTransit.RabbitMQ` | publisher registration lives here so the Application layer stays transport-agnostic |
| `PaymentPlatform.Api` | `AspNetCore.HealthChecks.Rabbitmq` (or hand-rolled `IHealthCheck` — pick whichever has the lighter dependency footprint after a Context7 lookup) | drives `/health/ready` RabbitMQ probe |
| `PaymentPlatform.Worker` | `MassTransit`, `MassTransit.RabbitMQ`, `Microsoft.Extensions.Hosting`, `Serilog.AspNetCore` (or `Serilog.Extensions.Hosting`), `Serilog.Enrichers.Span`, `Npgsql.EntityFrameworkCore.PostgreSQL` | the worker is a full hosted app — needs everything the API has minus ASP.NET |
| `PaymentPlatform.IntegrationTests` | `Testcontainers.RabbitMq`, `MassTransit.TestFramework` | RabbitMQ fixture + `ITestHarness` for in-memory consumer tests |

Honor the Phase 1 NuGet rule: pin every package to its latest stable version compatible with `net10.0`; if a package has no net10 build, fall back to the highest stable net9 build and note the gap. Do not pin to beta packages.

## 9. Task order (the build sequence)

Every task follows Red → Green → Refactor → Review → Validate. Tests are written and confirmed failing before the corresponding production code lands.

### Task 0 — Architectural decisions (45 min)

Write `docs/adr/0008-outbox-pattern-for-settlement.md`, `docs/adr/0009-masstransit-over-raw-rabbitmq.md`, `docs/adr/0010-settlement-consumer-idempotency.md`. Each follows the format: Context, Decision, Consequences, Alternatives considered. Keep them to ~30 lines.

**Validate:** All three files exist. Re-read each one cold — does the rationale survive a skeptical second read? Does ADR-0010 explicitly say "no inbox table, payment row state is authoritative"?

### Task 1 — Schema + EF wiring + RabbitMQ fixture skeleton (1.25 hr)

This task adds the table and the test fixture together because the migration is what the dispatcher tests will need to assert against. The RabbitMQ fixture skeleton lands here even though no consumer code uses it yet — having it available makes Task 5+ tests easier to write red-first.

**Red.** Add integration tests in `Persistence/MigrationSmokeTests.cs` (extend the existing file from Phase 2):
- Insert a `PaymentOutboxMessage` referencing a seeded payment, read it back, assert round-trip equality including `payload` jsonb and `correlation_id`.
- Insert two rows with the same `aggregate_id` — expect success (no unique constraint on `aggregate_id`; one payment can have multiple historical outbox messages).
- Insert a row, query `WHERE dispatched_at IS NULL`, assert it returns the row. Update via `ExecuteUpdateAsync` to set `dispatched_at`, re-query, assert it no longer returns.

Add `Fixtures/RabbitMqFixture.cs` skeleton (failing compile — no test references it yet, but its `IAsyncLifetime` shape should mirror `PostgresFixture`).

Run — all red.

**Green.** Implement:
- `PaymentOutboxMessage.cs` in `PaymentPlatform.Domain/Outbox/` — sealed class with private setters, factory `Create(aggregateId, messageType, payload, correlationId, createdAt)`. No EF awareness.
- `OutboxMessageType.cs` — string constants (`Settlement` = `"SettlePayment"`).
- `PaymentOutboxMessageConfiguration.cs` — table mapping, jsonb converter (reuse the dictionary value converter pattern Phase 2 uses for `payment_events.payload`).
- Extend `IPaymentsDbContext` and `PaymentsDbContext` with `DbSet<PaymentOutboxMessage> PaymentOutbox`.
- Generate the migration (per §7). Inspect and supplement the partial index with `migrationBuilder.Sql(...)` if EF doesn't emit it natively.
- `Fixtures/RabbitMqFixture.cs` — `RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").WithUsername("guest").WithPassword("guest").Build()`. Expose `ConnectionString`. Include a `PurgeAsync(queueName)` helper for between-test reset.
- Extend `Fixtures/PostgresFixture.cs` `ResetSql` to include `payment_outbox` in the `TRUNCATE TABLE` list.

**Refactor.** N/A (small surface).

**Review.** `ecc:code-review` on the diff. `ecc:database-migrations` skill checklist applied to the generated migration.

**Validate:**
- `dotnet build` clean.
- `MigrationSmokeTests` pass.
- `/bin/zsh -lc 'cd backend && dotnet test --filter FullyQualifiedName~MigrationSmokeTests'` green.
- Phase 1+2's full test suite still green (the new column is additive).

### Task 2 — Outbox write on Capture (1 hr)

Extend `CapturePaymentCommandHandler` to enqueue a `SettlePayment` outbox row in the same `SaveChangesAsync` as the payment update + event row.

**Red first.** Add tests in `OutboxWriteOnCaptureTests.cs`:
- Happy path: `POST /v1/payments/{id}/capture` on an `Authorized` payment returns 200, and a `payment_outbox` row exists with `aggregate_id = paymentId`, `message_type = "SettlePayment"`, `payload` decodes to a `SettlePayment` record carrying the payment id + merchant id + amount, `correlation_id` equals the request's `X-Request-Id` (or the generated trace id if none supplied), `dispatched_at IS NULL`.
- Replay (same idempotency key + body) returns the cached response and does NOT create a second outbox row.
- Capture failure on an invalid-state transition (`Pending → Capture`) returns 409 and creates NO outbox row.
- Concurrency conflict (two parallel captures, one loses with 409): the winner has an outbox row, the loser does not — the rollback drops the loser's outbox insert atomically with their payment update.

Also add a unit test in `OutboxMessageFactoryTests.cs`:
- `OutboxMessageFactory.ForSettlement(payment, correlationId, now)` produces a `PaymentOutboxMessage` with the right payload, message type, and correlation id. Null payment → throws.

Run — all red.

**Green.** Implement:
- `OutboxMessageFactory.cs` in `Application/Common/` — pure static factory. Serializes a `SettlePayment` to JSON (using the same `JsonSerializer` options the rest of the app uses) and stuffs it into the outbox row's `payload`.
- `ICorrelationContext.cs` in `Application/Abstractions/` — `string CorrelationId { get; }`. Implement in `Api/Middleware/` by reading from `Activity.Current?.TraceId` or the existing `CorrelationIdMiddleware`'s `HttpContext.Items` slot. Worker host implements via `MassTransitCorrelationFilter` (later task).
- Extend `CapturePaymentCommandHandler.CaptureAsync` (between `_db.PaymentEvents.Add(evt)` and the return): construct the outbox row via the factory and `_db.PaymentOutbox.Add(...)`. The existing `IdempotencyExecutor.RunAsync` already wraps everything in one `SaveChangesAsync` (because the executor's `SaveAsync` is what triggers persistence) — confirm by re-reading `IdempotencyExecutor.cs` lines 50–88; if the outbox insert is not part of the same `SaveChangesAsync`, the test will catch it.

**Refactor.** If the capture handler grows past ~120 lines, extract the outbox construction into a small private method `EnqueueSettlementOutbox(payment, ct)`. Don't preemptively extract.

**Review.** `ecc:code-review`.

**Validate:** `dotnet test --filter FullyQualifiedName~OutboxWriteOnCaptureTests` passes. Phase 2's `CapturePaymentTests` still pass (the new outbox write is invisible to existing assertions — they don't query `payment_outbox`). Commit as `feat: outbox row written in same transaction as capture`.

### Task 3 — MassTransit publisher in the API + new PaymentPlatform.Worker project (1.5 hr)

Stand up the messaging infrastructure on both sides of the queue. No outbox dispatcher yet (Task 4) and no consumer logic yet (Task 6) — this task is wiring, project structure, and the test that proves end-to-end publish-and-consume works at all.

**Red first.** Add a single test in `SettlementEndToEndTests.cs`:
- "Publishing a `SettlePayment` via the API's `IPublishEndpoint` reaches a no-op consumer registered in the Worker test host within 5 seconds." This test uses the new `MessagingFixture` (combined Postgres + RabbitMQ + Api + Worker) and asserts via a `TaskCompletionSource` the no-op consumer signals.

Compile fails (`PaymentPlatform.Worker` doesn't exist, `MessagingFixture` doesn't exist, `IOutboxPublisher` doesn't exist). Fix the compile by scaffolding empty types; the test then fails at runtime with "no consumer received the message".

**Green.** Implement:
- `PaymentPlatform.Messaging` project with `SettlePayment.cs`:
  ```csharp
  public sealed record SettlePayment(
      string MessageId,
      string PaymentId,
      string MerchantId,
      long AmountMinor,
      string Currency,
      string CorrelationId,
      int Attempt,
      DateTimeOffset EnqueuedAt);
  ```
  Match master plan §7 exactly.
- `SettlementQueues.cs` constants: `Exchange = "payments.settlement"`, `Queue = "settlement"`, `DeadLetterQueue = "settlement.dlq"`.
- `IOutboxPublisher.cs` in `Application/Abstractions/` — `Task PublishAsync(SettlePayment message, CancellationToken ct)`.
- `Infrastructure/Outbox/OutboxPublisher.cs` — thin wrapper over `IPublishEndpoint`. Sets the message's `MessageId` and `CorrelationId` headers from the message body for MassTransit-aware downstream observability.
- `MessagingServiceCollectionExtensions.AddPaymentMessagingPublisher(IConfiguration)` in `Infrastructure/DependencyInjection/`. Registers MassTransit with `UsingRabbitMq` pointing at `RabbitMq:Host` from config.
- Wire the publisher in `PaymentPlatform.Api/Program.cs` (one line: `services.AddPaymentMessagingPublisher(builder.Configuration);`).
- Create `PaymentPlatform.Worker` project:
  - `Program.cs`: `Host.CreateApplicationBuilder(args)`, add Serilog with the same JSON pipeline + span enricher Phase 2 uses for the API, add Npgsql DbContext (read-only for now), `AddMassTransit` with `AddConsumer<SettlePaymentConsumer>` (consumer is a no-op stub for this task — full logic in Task 6).
  - `appsettings.json` with `ConnectionStrings`, `RabbitMq` host/port/credentials, `Worker:ConcurrencyLimit = 16`.
  - `Dockerfile` mirroring the API's Dockerfile shape (multi-stage build, expose no ports — the worker is queue-driven).
- `Fixtures/RabbitMqFixture.cs` from Task 1 already exists; `Fixtures/MessagingFixture.cs` wires together `PostgresFixture` + `RabbitMqFixture` + a `WebApplicationFactory<Program>` for the API + a `HostBuilder` for the Worker, with all four reset between tests.
- Wire the integration tests' xUnit collection to use `MessagingFixture`.

**Refactor.** If the API's `Program.cs` becomes hard to scan after the MassTransit registration, extract into an `AddPaymentsApi` extension method.

**Review.** `ecc:code-review`. Run the docs-lookup agent against Context7 with query "MassTransit v8 publisher rabbitmq host configuration" before the green code lands — confirm the `UsingRabbitMq` syntax we use matches current MassTransit.

**Validate:** `dotnet test --filter FullyQualifiedName~SettlementEndToEndTests` passes the one wiring-test. `dotnet build` of the whole solution is clean. Commit as `feat: PaymentPlatform.Worker project + MassTransit publisher wiring`.

### Task 4 — Outbox dispatcher BackgroundService (1.5 hr)

The dispatcher polls undispatched outbox rows, publishes them via `IOutboxPublisher`, and flips `dispatched_at`. Lives inside the API host (per ADR-0008).

**Red first.** Add tests in `OutboxDispatcherTests.cs`:
- Single row: insert an undispatched outbox row directly, wait for the dispatcher to publish (assert via a captured `IPublishEndpoint` mock or — preferred — via the `MessagingFixture`'s test consumer signaling), assert `dispatched_at` is non-null after the publish.
- No rows: dispatcher polls and does nothing — no spurious log lines, no errors. Validated by asserting log buffer contains no "publishing" entries over a 3s window.
- Crash safety: insert a row, mock the publisher to throw on the first publish, assert `dispatched_at` stays null. Reset the mock to succeed, wait another poll interval, assert the row is published on the next attempt. (This proves the dispatcher does not flip `dispatched_at` until publish succeeds.)
- Ordering: insert three rows in known sequence, assert they are published in `created_at, id ASC` order.
- Correlation propagation: insert a row with a known `correlation_id`, assert the published `SettlePayment.CorrelationId` matches.

Run — all red.

**Green.** Implement:
- `OutboxDispatcherOptions.cs`: `PollInterval` (default `TimeSpan.FromSeconds(2)`), `BatchSize` (default 16).
- `OutboxDispatcher.cs` extending `BackgroundService`:
  ```
  while (!stoppingToken.IsCancellationRequested) {
      using var scope = _scopeFactory.CreateScope();
      var db = scope.ServiceProvider.GetRequiredService<IPaymentsDbContext>();
      var publisher = scope.ServiceProvider.GetRequiredService<IOutboxPublisher>();
      var batch = await db.PaymentOutbox
          .Where(o => o.DispatchedAt == null)
          .OrderBy(o => o.CreatedAt).ThenBy(o => o.Id)
          .Take(options.BatchSize)
          .ToListAsync(stoppingToken);
      foreach (var row in batch) {
          var message = OutboxMessageFactory.Deserialize(row);
          await publisher.PublishAsync(message, stoppingToken);
          await db.PaymentOutbox
              .Where(o => o.Id == row.Id)
              .ExecuteUpdateAsync(s => s.SetProperty(o => o.DispatchedAt, _clock.UtcNow), stoppingToken);
      }
      if (batch.Count == 0) await Task.Delay(options.PollInterval, stoppingToken);
  }
  ```
  Notes:
  - Per-message `ExecuteUpdateAsync` (not batch) — keeps the at-most-once-publish-per-flip invariant tight.
  - Publish-then-flip ordering means a crash between publish and flip causes a duplicate publish on the next poll. That's safe because the consumer is idempotent (ADR-0010).
  - No `try/catch` swallowing — a publish failure escapes the loop and the `BackgroundService` host restarts the iteration.
- Register `OutboxDispatcher` in `Program.cs` via `services.AddHostedService<OutboxDispatcher>()`. Bind `OutboxDispatcherOptions` from `appsettings.json` under `Outbox:Dispatcher`.

**Refactor.** If the inner loop body grows past ~40 lines, extract `DispatchBatchAsync(...)`. The publish-then-flip pair is the invariant to preserve under any refactor — keep it explicit.

**Review.** `ecc:code-review`. `dotnet-skills:efcore-patterns` checklist: confirm `ExecuteUpdateAsync` is used (not load-then-mutate-then-save), confirm the polling query uses `AsNoTracking()` implicitly via the default-NoTracking DbContext config from Phase 1.

**Validate:** `OutboxDispatcherTests` pass. Commit as `feat: outbox dispatcher BackgroundService`.

### Task 5 — Stub IPaymentProcessor with configurable failure modes (45 min)

Small task, large leverage — every consumer test depends on this.

**Red first.** Unit tests in `StubPaymentProcessorTests.cs`:
- `Mode = AlwaysSucceed`: returns `ProcessorResult.Success` on every call. Records call count.
- `Mode = FailNTimesThenSucceed, N = 2`: returns transient failure on calls 1 and 2, success on call 3. Resets on demand (test seam).
- `Mode = AlwaysFailPermanent`: returns permanent failure on every call, distinguishable from transient.
- Per-payment-id overrides: if the options include a per-id override map, that wins over the global mode (so one integration test can force one payment to fail while others succeed).

Run — all red.

**Green.** Implement:
- `IPaymentProcessor.cs` in `Application/Abstractions/`:
  ```csharp
  public interface IPaymentProcessor
  {
      Task<ProcessorResult> SettleAsync(SettlePayment message, CancellationToken ct);
  }
  public abstract record ProcessorResult
  {
      public sealed record Success(string ExternalReference) : ProcessorResult;
      public sealed record TransientFailure(string Reason) : ProcessorResult;
      public sealed record PermanentFailure(string Reason) : ProcessorResult;
  }
  ```
- `StubPaymentProcessor.cs` + `StubProcessorOptions.cs` in `Infrastructure/Processing/`. The options bind from `Worker:StubProcessor` config section. The stub uses a thread-safe `ConcurrentDictionary<string, int>` of call counts keyed by payment id.
- Register the stub in the Worker's `Program.cs` as a singleton (state spans messages — call counts persist across consumer invocations).

**Refactor.** N/A.

**Review.** `ecc:code-review`.

**Validate:** `dotnet test --filter FullyQualifiedName~StubPaymentProcessorTests` passes. Commit as `feat: stub IPaymentProcessor with configurable failure modes`.

### Task 6 — Settlement consumer (idempotent, FOR UPDATE, state-machine driven) (1.5 hr)

The heart of the worker.

**Red first.** Add tests in `SettlementConsumerTests.cs` using MassTransit `ITestHarness` (no real broker — fast feedback loop):
- Happy path: deliver a `SettlePayment` for a `Captured` payment; consumer transitions it to `Settled`, appends a `Captured → Settled` event with `actor = "worker"`, acks.
- Idempotent re-delivery: deliver the same message twice. After the second delivery, the payment is still `Settled`, exactly one settlement event exists (from the first delivery), the consumer logs an idempotent-skip line.
- State conflict: deliver a message for a payment that's already `Refunded`. Consumer acks without state change and logs the conflict — does NOT throw.
- Transient processor failure: stub returns `TransientFailure`; consumer throws (so MassTransit retries). Assert the thrown exception is NOT `PermanentSettlementFailureException`.
- Permanent processor failure: stub returns `PermanentFailure`; consumer throws `PermanentSettlementFailureException` (which the retry filter routes to DLQ without retry — proven in Task 7).
- Row lock: two concurrent deliveries against the same payment. The first wins (transitions); the second sees `Settled` (after waiting on the row lock) and acks idempotently. Exactly one new event row.

Run — all red.

**Green.** Implement `SettlePaymentConsumer.cs`:
```
public async Task Consume(ConsumeContext<SettlePayment> context) {
    using var _ = LogContext.PushProperty("payment_id", context.Message.PaymentId);
    using var __ = LogContext.PushProperty("trace_id", context.Message.CorrelationId);

    await using var tx = await _db.Database.BeginTransactionAsync(context.CancellationToken);
    // Row-lock via raw SQL — EF Core's tracked-load-then-FOR-UPDATE pattern.
    var payment = await _db.Payments
        .FromSqlRaw("SELECT * FROM payments WHERE id = {0} FOR UPDATE", context.Message.PaymentId)
        .AsTracking()
        .FirstOrDefaultAsync(context.CancellationToken);
    if (payment is null) throw new PermanentSettlementFailureException("payment_not_found");
    if (payment.Status == PaymentStatus.Settled) {
        _logger.LogInformation("Settlement skipped — payment already settled.");
        await tx.CommitAsync(context.CancellationToken); // release the lock cleanly
        return;
    }
    if (payment.Status != PaymentStatus.Captured) {
        _logger.LogWarning("Settlement skipped — payment in unexpected state {Status}.", payment.Status);
        await tx.CommitAsync(context.CancellationToken);
        return;
    }
    var result = await _processor.SettleAsync(context.Message, context.CancellationToken);
    switch (result) {
        case ProcessorResult.Success success:
            var evt = payment.Settle(_clock.UtcNow);
            _db.PaymentEvents.Add(evt);
            await _db.SaveChangesAsync(context.CancellationToken);
            await tx.CommitAsync(context.CancellationToken);
            return;
        case ProcessorResult.TransientFailure transient:
            throw new TransientSettlementException(transient.Reason);
        case ProcessorResult.PermanentFailure permanent:
            throw new PermanentSettlementFailureException(permanent.Reason);
    }
}
```
- `TransientSettlementException` is just `Exception` — MassTransit retries everything that isn't on the "no-retry" list.
- `PermanentSettlementFailureException` is on the retry policy's `Ignore<>` list (Task 7).
- Transaction commits on success and on idempotent-skip; rolls back implicitly on throw.

Wire the consumer in `Worker/Program.cs` `AddMassTransit` block: `cfg.AddConsumer<SettlePaymentConsumer>(); cfg.UsingRabbitMq((ctx, rmq) => { rmq.ReceiveEndpoint(SettlementQueues.Queue, e => { e.ConfigureConsumer<SettlePaymentConsumer>(ctx); /* retry policy comes in Task 7 */ }); });`.

**Refactor.** The switch on `result` is verbose; if a clean pattern-match expression-bodied lambda compresses it without losing the trace_id log scope, do it.

**Review.** `ecc:code-review`. `dotnet-skills:efcore-patterns` checklist for the row-lock pattern.

**Validate:** `dotnet test --filter FullyQualifiedName~SettlementConsumerTests` passes — `ITestHarness` makes these fast (<3s for the whole class). Commit as `feat: idempotent settlement consumer`.

### Task 7 — Retry policy + DLQ wiring + retry/DLQ integration tests (1.25 hr)

Configure MassTransit's retry policy and dead-letter topology, prove them under a real broker.

**Red first.** Add tests using the **real** `RabbitMqFixture` (not `ITestHarness` — we're testing the retry/DLQ behavior of the actual transport):
- `SettlementRetryTests`:
  - Stub configured to fail-twice-then-succeed for a specific payment id. Publish a `SettlePayment` for that id. Wait up to 30s. Assert payment lands in `Settled`. Assert log buffer shows 2 retry log lines from MassTransit. Assert exactly one new event row (the third attempt's success — the two failed attempts don't append events).
- `SettlementDeadLetterTests`:
  - Stub configured to always-fail-permanent for a specific payment id. Publish. Assert that within 10s the message lands in `settlement.dlq` (use `IBus.GetSendEndpoint` to peek the queue depth via RabbitMQ admin or by registering an in-test consumer on `settlement.dlq`). Assert the payment stays in `Captured`. Assert no `payment_events` row was written for this attempt.
- Use shortened retry intervals via `Worker:Retry` config (`100ms, 200ms, 400ms, 800ms, 1600ms`) so the tests finish in seconds, not minutes. Document the override in the test class setup.

Run — all red.

**Green.** Extend the consumer's receive endpoint config:
```
e.UseMessageRetry(r => {
    r.Exponential(5, baseInterval, maxInterval, increment);
    r.Ignore<PermanentSettlementFailureException>();
});
e.UseInMemoryOutbox(); // optional — see ADR note
e.DiscardSkippedMessages();
e.BindDeadLetterQueue(SettlementQueues.DeadLetterQueue);
```
Bind the intervals to `Worker:Retry` config so tests can shorten them. Default to master plan §7's policy (1s, 4s, 16s, 64s, 256s) when the override is absent.

**Refactor.** If the retry config grows beyond ~10 lines, extract `ConfigureSettlementRetry(IRetryConfigurator)` and `ConfigureSettlementDeadLetter(IReceiveEndpointConfigurator)`.

**Review.** `ecc:code-review`. Re-run `docs-lookup` against Context7 with "MassTransit v8 retry policy ignore exception type" and "MassTransit RabbitMQ dead letter queue binding" to confirm the API surface we use matches current MassTransit.

**Validate:** `dotnet test --filter FullyQualifiedName~Settlement` passes — both retry and DLQ tests green. The full Phase 3 test suite (Tasks 1–7) runs under 90s. Commit as `feat: settlement retry policy and DLQ wiring`.

### Task 8 — Observability: /health/ready RabbitMQ + correlation propagation (45 min)

`/health/ready` was a known gap in Phase 1/2 (master plan calls it out under Phase 4, but we're standing the broker up so we add the RabbitMQ check now). Correlation propagation through the worker is the other thing this task closes — without it, "pull all logs by trace_id" doesn't actually work.

**Red first.**
- `HealthReadyTests.cs`:
  - With RabbitMQ + Postgres up: `GET /v1/health/ready` returns 200 with body `{ status: "healthy", checks: [{ name: "postgres", ... }, { name: "rabbitmq", ... }] }` (mirror the response shape Phase 4 will own; for now make the body simple).
  - With RabbitMQ down (stop the container mid-test via `_rabbitFixture.StopAsync()`): returns 503 within 5s. Restart container, returns 200 again.
- Extend `SettlementEndToEndTests.cs` (the existing end-to-end test from Task 3) with a correlation-propagation assertion:
  - Capture a payment with a known `X-Request-Id` (e.g., `request-id-12345`). Wait for settlement.
  - Query the Serilog `InMemoryLogSink` for log lines from the worker. Assert at least one line has `trace_id == request-id-12345` in its properties.

Run — both red.

**Green.**
- `RabbitMqHealthCheck.cs` in `Api/HealthChecks/` — implements `IHealthCheck`; opens a short-lived `IConnection` and pings the broker. (Or use `AspNetCore.HealthChecks.Rabbitmq` if its dependency tree is light enough.)
- Extend `HealthEndpoints.MapHealthChecks(...)` to include the RabbitMQ check on `/health/ready`. `/health/live` stays unchanged.
- `MassTransitCorrelationFilter.cs` in `Worker/Correlation/` — implements `IFilter<ConsumeContext>` that reads `MessageId`/`CorrelationId` from the envelope and pushes `LogContext.PushProperty("trace_id", ...)` for the duration of the consume. Register in `Worker/Program.cs`'s `AddMassTransit` block via `cfg.AddConsumer<...>(c => c.UseFilter(new MassTransitCorrelationFilterSpecification()))` or the equivalent typed-receive-endpoint hook.

**Refactor.** If the filter has to be a class + a specification + a registration extension, accept the boilerplate — MassTransit's filter pipeline has a fixed shape.

**Review.** `ecc:code-review`.

**Validate:** Both new tests pass. `/v1/health/ready` returns the right shape when curled manually. Commit as `feat: /health/ready RabbitMQ check + worker correlation propagation`.

### Task 9 — End-to-end test + acceptance walkthrough + docker-compose + README + commits (1 hr)

The capstone task. Closes the loop on Phase 3 and stages the repo for Phase 4.

**Red.** Add `SettlementEndToEndTests.HappyPathFullLifecycle`:
- POST `/v1/payments` → 201 Pending.
- Drive `Pending → Authorized` via the test-only direct aggregate call (same way Phase 2's `StateMachineEndToEndTests` does).
- POST `.../capture` → 200 Captured.
- Wait (with a bounded retry helper, not a fixed sleep) for the payment to reach Settled — max 10s.
- GET `/v1/payments/{id}` → 200 Settled with 5 events: `null→Pending`, `Pending→Authorized`, `Authorized→Captured`, `Captured→Settled`. The Settle event has `actor = "worker"`.
- Assert the outbox row is now `dispatched_at IS NOT NULL`.

**Green.** No new production code expected — failure here indicates a regression in Tasks 1–8. If something breaks, fix the responsible task's tests + code, then return.

**docker-compose changes.** Edit `docker-compose.yml`:
- Add `rabbitmq` service: `rabbitmq:3-management-alpine`, ports `5672` + `15672`, healthcheck via `rabbitmq-diagnostics ping`.
- Add `worker` service: build from the same context as `api` but pointed at `src/PaymentPlatform.Worker/Dockerfile`, `depends_on: { postgres: healthy, rabbitmq: healthy }`, env vars for the connection strings.
- API gains `depends_on: { rabbitmq: healthy }`.

**README diff.**
- New "Phase 3 — async settlement" section with a swimlane diagram (ASCII) showing API → outbox → dispatcher → exchange → queue → consumer → DB.
- "Running the worker" subsection in the local-dev block (`docker compose up` now starts the worker too).
- Updated curl walkthrough — after `POST .../capture`, add a `GET /v1/payments/{id}` step that shows the `Settled` status and the 5-event timeline.
- Failure-mode block: how to force the stub processor to fail (env override on the worker container), how to inspect DLQ (`rabbitmqctl list_queues`), how to peek the outbox (`docker compose exec postgres psql ...`).

**Commits.** Mirror the Phase 2 commit cadence (~6 thematic commits):
1. `docs:` ADRs 0008/0009/0010 (Task 0).
2. `feat:` payment_outbox table + EF wiring + RabbitMqFixture (Task 1, with migration smoke test).
3. `feat:` outbox row written in same transaction as capture (Task 2, with tests).
4. `feat:` PaymentPlatform.Worker project + MassTransit publisher wiring (Task 3).
5. `feat:` outbox dispatcher BackgroundService (Task 4).
6. `feat:` stub IPaymentProcessor with configurable failure modes (Task 5).
7. `feat:` idempotent settlement consumer with retry policy and DLQ (Tasks 6 + 7).
8. `feat:` /health/ready RabbitMQ check + worker correlation propagation (Task 8).
9. `docs:` Phase 3 walkthrough capture, docker-compose updates, README refresh (Task 9).

Skip Claude attribution per global git settings.

**Validate:**
- `git log --oneline | head -10` reads cleanly.
- Working tree clean except `.claude/`.
- Walk §10 manually against `docker compose up`. Capture the curl session for next session's reference.

**Total estimated time: ~10 hours of focused work.** Master plan estimated 1.5 days; the extra ~2 hours absorb the RabbitMQ fixture, the worker project bootstrap, and the DLQ test.

## 10. Validation runbook (the "did Phase 3 work" curls)

```bash
# Bring up the stack — postgres + rabbitmq + api + worker
docker compose up -d
docker compose ps  # all four services healthy

# Seed: create + authorize (authorize step still test-only in Phase 3 since the auth processor lands later)
RESP=$(curl -s -X POST http://localhost:5000/v1/payments \
  -H "Authorization: Bearer dev-key-mrc-acme" \
  -H "Idempotency-Key: $(uuidgen)" \
  -H "Content-Type: application/json" \
  -d '{"amount_minor":12500,"currency":"USD","card_token":"tok_stub_visa"}')
PID=$(echo "$RESP" | jq -r .id)

# Same psql shim Phase 2 used to drive Pending → Authorized
docker compose exec postgres psql -U postgres payments -c \
  "UPDATE payments SET status='Authorized', version=version+1 WHERE id='$PID';"
docker compose exec postgres psql -U postgres payments -c \
  "INSERT INTO payment_events (id, payment_id, from_status, to_status, actor, reason, payload, at) \
   VALUES ('evt_' || gen_random_uuid(), '$PID', 'Pending', 'Authorized', 'system', 'auth_ok', '{}', now());"

# Step 1 — Capture (triggers the outbox row)
KEY=$(uuidgen)
curl -i -X POST "http://localhost:5000/v1/payments/$PID/capture" \
  -H "Authorization: Bearer dev-key-mrc-acme" \
  -H "Idempotency-Key: $KEY" \
  -H "Content-Type: application/json" \
  -d '{}'
# → 200 Captured

# Step 2 — Observe the outbox row exists and is undispatched
docker compose exec postgres psql -U postgres payments -c \
  "SELECT id, aggregate_id, message_type, dispatched_at FROM payment_outbox WHERE aggregate_id='$PID';"
# → 1 row with dispatched_at = NULL (within ~2s of capture)

# Step 3 — Wait ~3s, re-query: row should now show dispatched_at
sleep 3
docker compose exec postgres psql -U postgres payments -c \
  "SELECT id, aggregate_id, message_type, dispatched_at FROM payment_outbox WHERE aggregate_id='$PID';"
# → dispatched_at non-null

# Step 4 — Verify the payment is now Settled and the timeline has 4 transitions
curl -s "http://localhost:5000/v1/payments/$PID" \
  -H "Authorization: Bearer dev-key-mrc-acme" | jq '{status, events: [.events[] | {from_status, to_status, actor}]}'
# → status: "Settled", 5 events including Captured→Settled with actor="worker"

# Step 5 — Force a retry: set the stub processor to fail-twice-then-succeed for the next payment, then capture another
docker compose exec worker sh -c \
  "echo 'STUB_PROCESSOR_MODE=FailNThenSucceed' >> /app/runtime.env"  # or use a SIGHUP / file-based reload, depending on the chosen mechanism
# Re-run the capture flow on a fresh payment id — observe 2 retry lines in `docker compose logs -f worker`

# Step 6 — Force permanent failure and observe DLQ
docker compose exec worker sh -c "echo 'STUB_PROCESSOR_MODE=AlwaysFailPermanent' >> /app/runtime.env"
# Capture another payment. Observe in worker logs: 5 attempts, then DLQ.
docker compose exec rabbitmq rabbitmqctl list_queues name messages
# → settlement.dlq has 1 message; settlement has 0

# Step 7 — Health checks
curl -i http://localhost:5000/v1/health/live
# → 200
curl -i http://localhost:5000/v1/health/ready
# → 200 with rabbitmq + postgres both healthy
docker compose stop rabbitmq
curl -i http://localhost:5000/v1/health/ready
# → 503
docker compose start rabbitmq && sleep 5
curl -i http://localhost:5000/v1/health/ready
# → 200

# Step 8 — Full test suite
/bin/zsh -lc 'cd backend && dotnet test'
```

## 11. Risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Outbox row gets written outside the SaveChangesAsync that updates `payments` — silent dual-write hazard | Medium | Critical | The capture handler integration test asserts both rows land or neither lands by deliberately throwing inside the work delegate and asserting the outbox row was rolled back. `dotnet-skills:efcore-patterns` skill checklist applied on review. |
| MassTransit retries a permanent failure indefinitely because `Ignore<PermanentSettlementFailureException>()` isn't wired correctly | Medium | High | `SettlementDeadLetterTests` (Task 7) is the canonical proof — if the test passes, the wiring is right. The test must use the real RabbitMQ fixture, not `ITestHarness`, because the in-memory harness has its own retry semantics. |
| Trace propagation breaks at the MassTransit boundary because the consumer reads `Activity.Current.TraceId` instead of `context.Message.CorrelationId` | Medium | Medium | Task 8's correlation test asserts the worker's log lines have the originating request's `trace_id`. MassTransit also has built-in OTel filters; we explicitly use the message envelope as the source of truth so Phase 4's OTel wiring slots in cleanly. |
| Worker processes one message at a time and falls behind under capture spikes | Low at exercise scale | Medium | `Worker:ConcurrencyLimit = 16` on the receive endpoint. Concurrency is configured at MassTransit's prefetch level. Master plan §9 calls out `mq_queue_depth` and `mq_processing_lag_seconds` metrics — Phase 4 surfaces them. |
| `RabbitMqFixture` cold start adds 30–60s per test class collection | High | Low | The fixture is class-scoped, not per-test. Queue purge between tests is fast (~ms). End-to-end tests cluster into one collection so the cold start hits once per `dotnet test` invocation. |
| `ExecuteUpdateAsync` not available in the Npgsql provider for net10.0 (was added in EF Core 7) | Low | Low | Phase 2 already depends on it elsewhere — verify by grep. If unavailable, fall back to a tracked load + `dispatched_at = _clock.UtcNow` + `SaveChangesAsync` (slower but functionally identical). |
| Concurrent dispatcher instances re-publish the same row twice because we don't have `SKIP LOCKED` | Low (single-host in Phase 3) | Low (consumer is idempotent) | ADR-0008 documents the single-host constraint. Multi-instance dispatcher is future work; the consumer's idempotent contract means the worst case is wasted broker work, not a correctness bug. |
| Stub processor's per-id override map causes test bleed when tests share the worker host | Medium | Medium | `MessagingFixture` resets the stub between tests via a reset endpoint or by re-binding the singleton with fresh options per test. Document the reset mechanism in the fixture. |
| MassTransit version drift — v8 syntax differs from older docs | Medium | Low | Run `docs-lookup` against Context7 before writing the publisher/consumer config. Pin to a single major version in all three projects' `.csproj`. |
| The Worker's Dockerfile is wrong because the API's pattern doesn't translate directly (no ASP.NET, different SDK image) | Medium | Low | Build the Dockerfile early in Task 3 and `docker compose build worker` before any consumer logic lands. Catches base-image issues at the cheapest moment. |
| `FOR UPDATE` row lock blocks a concurrent API mutation (e.g., a manual refund attempt) for the duration of the processor call | Low | Medium | The processor stub is fast (<100ms). For real processors with 5s+ latency, ADR-0010's row-lock approach would need revisiting; documented in the ADR's Consequences section. |

## 12. Acceptance criteria

- [ ] All three ADRs (`0008`, `0009`, `0010`) committed under `docs/adr/`.
- [ ] `payment_outbox` table exists with the partial index on `(created_at) WHERE dispatched_at IS NULL`.
- [ ] `CapturePaymentCommandHandler` writes a `SettlePayment` outbox row in the same `SaveChangesAsync` as the payment + event update.
- [ ] `PaymentPlatform.Messaging` and `PaymentPlatform.Worker` projects exist, build clean, and ship in `docker compose up`.
- [ ] `OutboxDispatcher` polls and publishes within `Outbox:Dispatcher:PollInterval` (default 2s).
- [ ] `SettlePaymentConsumer` uses `FOR UPDATE` row lock, short-circuits on already-`Settled`, and is exhaustively unit-tested.
- [ ] Retry policy: 5 attempts, exponential. Permanent failures bypass retry and land in `settlement.dlq`.
- [ ] Stub `IPaymentProcessor` supports always-succeed, fail-N-then-succeed, always-fail-permanent — driven by config.
- [ ] Integration tests cover: end-to-end happy path, retry, DLQ, idempotent re-delivery, concurrent delivery row-lock.
- [ ] `/v1/health/ready` returns 200 when RabbitMQ + Postgres are up; 503 when RabbitMQ is down.
- [ ] Worker log lines include `trace_id` matching the originating capture request's correlation id.
- [ ] `docker-compose.yml` includes `rabbitmq:3-management-alpine` and `worker` services with proper `depends_on` health gates.
- [ ] `dotnet test` green: Phase 1+2's tests still passing + ~25 new tests.
- [ ] Manual curl walkthrough §10 produces the expected responses.
- [ ] README updated with the Phase 3 swimlane diagram, the worker run command, and the DLQ inspection runbook.
- [ ] Commits split thematically per Task 9; no Claude attribution.

## 13. What's intentionally NOT in Phase 3

| Out | Lands in |
|---|---|
| Webhook delivery to merchants | Future improvement (master plan §17) |
| Reconciliation against the real processor | Future improvement (master plan §17) |
| Real card-network adapter (replacing the stub) | Production hardening (master plan §16) |
| Multi-region active-passive dispatcher + replicated outbox | Production hardening (regional broker invariant preserved by Phase 3's design) |
| OpenTelemetry SDK + OTLP exporter wiring | Phase 4 |
| Prometheus `/metrics` endpoint with full queue metrics from master plan §9 | Phase 4 |
| Log redaction enricher (no PAN ever reaches a log in Phase 3) | Phase 4 |
| Frontend dashboard | Phase 5 |
| Background TTL sweep for old `idempotency_keys` and dispatched outbox rows | Phase 4 polish or production hardening |
| Multi-instance dispatcher with `SELECT ... FOR UPDATE SKIP LOCKED` | Documented in ADR-0008 as future work |
| Inbox table for message-id-based dedupe | Documented in ADR-0010 as future work for high-throughput tenants |
| Admin tool for DLQ inspect/requeue/discard | Documented in master plan §16 |

## 14. Notes for the implementer

- **Outbox write order is fixed.** Inside `CapturePaymentCommandHandler.CaptureAsync`: first `payment.Capture(now)`, then `_db.PaymentEvents.Add(evt)`, then `_db.PaymentOutbox.Add(OutboxMessageFactory.ForSettlement(payment, correlationId, now))`. The `IdempotencyExecutor`'s `SaveAsync` is what triggers the single `SaveChangesAsync`. Do NOT call `SaveChangesAsync` inside `CaptureAsync` — that breaks the transactional invariant.
- **The dispatcher does publish-then-flip, not flip-then-publish.** A crash between the two causes a duplicate publish, which the idempotent consumer absorbs. The reverse ordering (flip first, then publish) loses messages on crash — never do that.
- **The consumer's `FOR UPDATE` lock must be released by committing or rolling back the transaction.** EF Core's `TransactionScope`-style `await using` handles this. If the consumer throws, the transaction rolls back and the lock releases automatically — but the consumer must NOT do its own retry loop while holding the lock.
- **`ProcessorResult` is a sealed discriminated union.** The consumer's `switch` must be exhaustive — add a default case that throws `InvalidOperationException` so a future processor result type can't slip through silently.
- **The stub processor is a singleton with mutable call-count state.** This is fine for integration tests (state intentionally persists across messages within one test) but it requires `ConcurrentDictionary` for thread safety. `MessagingFixture` must reset the call counts between tests.
- **Worker logging mirrors the API's Serilog pipeline.** Same JSON formatter, same span enricher, same `request_id` / `trace_id` properties — so log queries by `trace_id` span both processes.
- **Don't accept a fixed `Task.Delay` in any test.** Use a bounded retry helper (`Eventually(action, timeout: 10s, polling: 100ms)`) instead. Sleep-based test timing is the leading source of CI flake in messaging tests.
- **MassTransit's `ITestHarness` vs the real fixture: use both.** `ITestHarness` for fast consumer unit-style tests (Task 6); the real `RabbitMqFixture` for retry, DLQ, and end-to-end tests (Tasks 3, 7, 9). The harness has different retry semantics than the real transport — testing both catches wiring bugs the harness alone misses.
- **Migration discipline.** Don't hand-edit the generated migration except to supplement the partial index `WHERE` clause via `migrationBuilder.Sql(...)`. If you regenerate (`dotnet ef migrations remove` then `add` again), remember to also re-add the supplemental SQL.
