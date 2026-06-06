# Phase 3 Entry Resume Note — Async Settlement Workflow

**Last session ended:** 2026-06-06, right after committing Phase 2 Task 9 (`eb9de8b`). Phase 2 is acceptance-complete (10 commits, 124 unit + 56 integration tests green). Working tree is clean except `.claude/`.

**Branch:** `main`
**Latest commits (newest first):**
- `eb9de8b` docs: Phase 2 walkthrough capture and README refresh
- `740db77` test: cross-cutting state-machine lifecycle integration test
- `8517efa` feat: write initial payment event on create
- `29c922d` feat: list payments endpoint with cursor pagination
- `72120a6` feat: refund payment endpoint and extract idempotency executor

Confirm with `git log --oneline -10`.

## What to do this session

**Single deliverable: write `.claude/plans/payment-platform-phase-3.plan.md` mirroring `payment-platform-phase-2.plan.md`. Do NOT start implementation.**

Last session ended at $64.45 because we tried to plan AND get partial pattern grounding in the same conversation. This session: just plan. Fresh cache, no execution.

## Source material (read these first, in this order)

1. `.claude/plans/payment-platform.plan.md` §7 (Async Workflow), §8 (`payment_outbox` row), §9 (queue metrics), §13 (what was deferred to Phase 3 from Phase 2).
2. `.claude/plans/payment-platform-phase-2.plan.md` — mirror its overall shape: numbered intro sections (Why, In/Out scope, NuGet packages, Migrations, Risks, Acceptance, Validation runbook), then a Tasks section where every task carries a Red / Green / Refactor / Review / Validate block.
3. Skim — do NOT deep-read — these Phase 2 patterns the Phase 3 work must reuse:
   - `backend/src/PaymentPlatform.Application/Common/IdempotencyExecutor.cs` — pattern for "one SaveChangesAsync wraps everything"
   - `backend/src/PaymentPlatform.Application/Features/CapturePayment/CapturePaymentCommandHandler.cs` — pattern that the outbox write needs to slot into, in the same transaction as `_db.PaymentEvents.Add(evt)`
   - `backend/tests/PaymentPlatform.IntegrationTests/Fixtures/PostgresFixture.cs` — the `TRUNCATE TABLE` reset list needs `payment_outbox` added (and a RabbitMQ fixture will need to be written from scratch)
   - `backend/src/PaymentPlatform.Infrastructure/Persistence/Migrations/20260606005248_PaymentEventsAndOperationKey.cs` — migration timestamp + naming cadence to mirror

## Skills to load (only the ones you need, at the time you need them)

| Skill | Load when | Why |
|---|---|---|
| `dotnet-skills:efcore-patterns` | Outlining the outbox write + dispatcher task | The outbox write MUST be inside the same `SaveChangesAsync` as the capture, with no separate transaction. The skill covers EF Core transactionality rules. |
| `dotnet-skills:testcontainers` | Outlining the worker integration test task | RabbitMQ Testcontainers usage. The skill is .NET-specific and shows the xUnit fixture pattern that mirrors PostgresFixture. |
| `docs-lookup` agent (via Context7) | If you're unsure about MassTransit consumer / retry / DLQ syntax | No MassTransit-specific skill exists. Context7 has current MassTransit v8 docs. |

**Do NOT load** while just planning:
- `ecc:tdd-workflow` — planning is not implementation; load it at execution time
- `dotnet-skills:csharp-coding-standards` — covered by `~/.claude/rules/ecc/csharp/`
- `dotnet-skills:aspire-*`, `dotnet-skills:akka-*` — wrong stack

## Phase 3 scope to plan (from master plan §7)

A correct Phase 3 plan covers at minimum these tasks (ordering and exact split is yours to make):

1. **ADRs 0008–0010** — outbox-pattern choice, MassTransit-vs-raw-RabbitMQ choice, idempotent-consumer contract.
2. **`payment_outbox` table + EF migration** — columns per master plan §8, partial index on `(created_at) WHERE dispatched_at IS NULL`.
3. **Outbox write on Capture** — extend `CapturePaymentCommandHandler` to enqueue a `SettlePayment` row inside the same `SaveChangesAsync`. Add a unit test on the aggregate-or-handler seam and an integration assertion on the row landing.
4. **Outbox dispatcher** — a `BackgroundService` that polls for `dispatched_at IS NULL` rows, publishes to MassTransit, marks dispatched. Decision point: poll vs `LISTEN/NOTIFY` (master plan says poll — call it out).
5. **MassTransit + RabbitMQ wiring** — DI registration in the Api project (publisher) and a new `PaymentPlatform.Worker` project (consumer). Retry: 5 attempts, exponential 1s/4s/16s/64s/256s. DLQ: `settlement.dlq`.
6. **Settlement consumer** — loads payment with `FOR UPDATE` row lock, no-ops if already `Settled`, calls a stub `IPaymentProcessor`, transitions Captured → Settled, appends `PaymentEvent`, commits, acks. Idempotent by construction.
7. **Stub `IPaymentProcessor`** — configurable failure modes (always succeed / transient-fail-then-succeed / always-fail-permanent) driven by config so integration tests can exercise retry + DLQ.
8. **Integration tests** — Testcontainers RabbitMQ fixture; end-to-end: capture → outbox row → message published → worker consumes → payment settled → event row written. Plus a retry test and a DLQ test.
9. **Observability + health** — `/health/ready` should check RabbitMQ. Worker logs include `trace_id` and `payment_id` propagated from the capture's correlation id.
10. **Acceptance walkthrough + README diff + commit** — same shape as Phase 2 Task 9. Update `docker-compose.yml` to add the `rabbitmq:3-management` service and the worker container.

## Plan file shape (mirror Phase 2)

| Section | Phase 2 line range | Phase 3 equivalent |
|---|---|---|
| Header + branch context | top | same |
| 1. Why this phase exists | early | "Move settlement off the request path. Prove the outbox pattern." |
| 2. In scope | early | settlement workflow only — webhooks and reconciliation stay deferred (master plan §3 lists them as future workstreams) |
| 3. Out of scope (and where it lands) | early | Webhook delivery, reconciliation, real card-network adapter, multi-region, OpenTelemetry full wiring (Phase 4) |
| 4. NuGet packages | mid | `MassTransit`, `MassTransit.RabbitMQ`, `RabbitMQ.Client`, `Testcontainers.RabbitMq`, `Microsoft.Extensions.Hosting` for the Worker project, `MassTransit.TestFramework` |
| 5. Migrations | mid | one migration: `payment_outbox` table + partial index |
| 6. Project layout changes | mid | new `PaymentPlatform.Worker` project; `PaymentPlatform.IntegrationTests` adds RabbitMqFixture |
| 7. Tasks (R/G/R/Review/Validate each) | bulk | ~9 tasks per the scope above |
| 8. Risks | late | outbox dispatcher polling latency, transactionality gaps, RabbitMQ container slowness in tests, MassTransit retry-on-permanent-failure trap |
| 9. Acceptance criteria | late | checklist mirroring Phase 2's bottom-of-file checklist |
| 10. Validation runbook (curl + docker compose) | late | mirror Phase 2's §10 — capture a payment, watch the worker log the settlement, GET the payment, assert `Settled` status + 5-event timeline |
| 11. Total estimated time | bottom | ballpark — Phase 2 estimated 10 hours and took ~9 sessions; expect similar |

## Cost guardrails

- Last session hit $64.45 because of multiple skill loads + a docker walkthrough + a README diff in one conversation.
- Plan-only session estimate: **$15–25**. If you cross $30 mid-plan, stop and ship a partial plan with a "TODO" pointer rather than blowing the budget for completeness.
- Skip the `ecc:code-review` agent on the plan file itself — it's a markdown design doc, not production code.

## Useful commands

```bash
cd "/Users/evangarcia/Programing/Argyle - Payment Processing Platform"
git log --oneline -10
git status --short  # should show only `.claude/`

# Confirm Phase 2 test count baseline before planning anything that touches tests
cd backend && /bin/zsh -lc 'dotnet test --no-build 2>&1 | tail -4'

# Sanity-check the Phase 2 plan's section structure
wc -l .claude/plans/payment-platform-phase-2.plan.md
grep -n "^## " .claude/plans/payment-platform-phase-2.plan.md
```
