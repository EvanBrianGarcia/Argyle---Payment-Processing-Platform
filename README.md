# Argyle — Payment Processing Platform

A simplified payment processing platform built for the Argyle senior engineer
take-home. This repository contains **Phases 1 and 2**: the vertical slice
that proves the spine works end-to-end (Phase 1) and the full payment state
machine with capture, refund, list, and per-transition audit events (Phase
2). The async settlement worker, the React dashboard, and the full
OpenTelemetry wiring land in later phases (see [What's next](#whats-next)).

The exercise brief lives at [`Payment-Platform-Exercise.md`](Payment-Platform-Exercise.md).
The implementation plan lives under [`.claude/plans/`](.claude/plans).

---

## What's built right now

| Capability                                                       | Phase |
| ---------------------------------------------------------------- | :---: |
| `POST /v1/payments` (create, persisted, returns `pay_…` ULID)    |   1   |
| `GET /v1/payments/{id}` (own-merchant only, cross-tenant 404)    |   1   |
| `Idempotency-Key` header — replay returns identical body         |   1   |
| Per-operation idempotency scoping (`create` / `capture` / `refund`) |   2   |
| Dev bearer auth (SHA-256 hash of seeded merchant key)            |   1   |
| Structured JSON logs with `request_id` + `merchant_id` + `trace_id` |   1+2 |
| `X-Request-Id` response header AND `request_id` in error body    |   1+2 |
| `card_token` redaction (never logged)                            |   1   |
| `GET /health/live`                                               |   1   |
| Postgres 16 + EF Core 10 migrations, seeded with two merchants   |   1+2 |
| Integration tests over real Postgres via Testcontainers          |   1+2 |
| `Payment` aggregate with state machine + invalid-transition guards |  2   |
| `POST /v1/payments/{id}/capture` (Authorized → Captured)         |   2   |
| `POST /v1/payments/{id}/refund` (Captured/Settled → Refunded)    |   2   |
| `GET /v1/payments?status=&limit=&cursor=` cursor pagination       |   2   |
| `payment_events` audit table populated on every transition       |   2   |
| Optimistic concurrency via `payments.version`                    |   2   |
| Lifecycle event timeline returned by `GET /v1/payments/{id}`     |   2   |
| Async settlement worker (RabbitMQ + outbox)                      |   3   |
| OpenTelemetry traces + Prometheus metrics                        |   4   |
| React dashboard                                                  |   5   |

Phases 1 and 2 prioritize operational depth on a small surface, not breadth.

---

## Quick start

### Prerequisites

- Docker Desktop (tested on 4.76 / Engine 29.x; older engines have a TLS handshake quirk with Testcontainers)
- .NET 10 SDK (only required to run `dotnet test` outside Docker)
- `curl`, `jq`, and `uuidgen` for the acceptance walkthrough

### Bring the stack up

```bash
git clone <this repo>
cd "Argyle - Payment Processing Platform"
docker compose up -d
docker compose logs -f api          # first boot runs EF Core Migrate and seeds merchants
```

The API listens on `http://localhost:5000` (mapped from the container's
`:8080`). Postgres listens on `localhost:5432`. The container runs migrations
in `Development` mode at startup, so the first boot creates the schema and
inserts the two seed merchants.

To shut down and wipe state:

```bash
docker compose down -v               # -v removes the postgres-data volume
```

---

## Acceptance walkthrough

The Phase 1 plan ships with a seven-step acceptance script. Run it end-to-end
against the live stack:

```bash
# 1. Create a payment as merchant "acme"
RESP=$(curl -s -X POST http://localhost:5000/v1/payments \
  -H "Authorization: Bearer dev-key-mrc-acme" \
  -H "Idempotency-Key: $(uuidgen)" \
  -H "Content-Type: application/json" \
  -d '{"amount_minor":12500,"currency":"USD","card_token":"tok_stub_visa","customer_reference":"order-1"}')
echo "$RESP" | jq .
PAYMENT_ID=$(echo "$RESP" | jq -r .id)

# 2. Fetch it back
curl -s http://localhost:5000/v1/payments/$PAYMENT_ID \
  -H "Authorization: Bearer dev-key-mrc-acme" | jq .

# 3. Idempotent replay — same key + same body → byte-identical response, no second DB row
KEY=$(uuidgen)
RESP1=$(curl -s -X POST http://localhost:5000/v1/payments \
  -H "Authorization: Bearer dev-key-mrc-acme" \
  -H "Idempotency-Key: $KEY" \
  -H "Content-Type: application/json" \
  -d '{"amount_minor":5000,"currency":"USD","card_token":"tok_stub_visa"}')
RESP2=$(curl -s -X POST http://localhost:5000/v1/payments \
  -H "Authorization: Bearer dev-key-mrc-acme" \
  -H "Idempotency-Key: $KEY" \
  -H "Content-Type: application/json" \
  -d '{"amount_minor":5000,"currency":"USD","card_token":"tok_stub_visa"}')
diff <(echo "$RESP1") <(echo "$RESP2")     # no output → bodies match

# 4. Cross-merchant isolation — should be 404, never 403, never the payment body
curl -i http://localhost:5000/v1/payments/$PAYMENT_ID \
  -H "Authorization: Bearer dev-key-mrc-pied"

# 5. No auth — 401 with error envelope
curl -i http://localhost:5000/v1/payments/$PAYMENT_ID

# 6. Liveness
curl http://localhost:5000/health/live

# 7. Tests
cd backend && dotnet test
```

Inspect the API logs while running steps 1–6 — every line is a single JSON
object, every request emits `request_id`, every authenticated request also
carries `merchant_id`. Re-issuing a request with `-H "X-Request-Id: my-trace-id"`
will pin that id end-to-end.

---

## Phase 2 endpoints

Phase 2 adds three endpoints on top of the Phase 1 spine. All three reuse the
same `Idempotency-Key` header contract and return the same `PaymentResponse`
shape — now extended with `events[]` (the per-transition audit timeline) and
`updated_at`. A live capture of these steps lives at
[`docs/acceptance/phase-2-walkthrough.md`](docs/acceptance/phase-2-walkthrough.md).

The Phase 2 acceptance walk assumes a payment is already in `Authorized`
(Phase 3's worker performs that transition; for now we drive it via psql,
matching the shape the worker will use):

```bash
# Seed a Pending payment
RESP=$(curl -s -X POST http://localhost:5000/v1/payments \
  -H "Authorization: Bearer dev-key-mrc-acme" \
  -H "Idempotency-Key: $(uuidgen)" \
  -H "Content-Type: application/json" \
  -d '{"amount_minor":12500,"currency":"USD","card_token":"tok_stub_visa"}')
PID=$(echo "$RESP" | jq -r .id)

# Phase 3 stand-in: drive Pending → Authorized
docker compose exec postgres psql -U postgres payments -c \
  "UPDATE payments SET status='Authorized', version=version+1 WHERE id='$PID';
   INSERT INTO payment_events (id, payment_id, from_status, to_status, actor, reason, payload, at)
   VALUES ('evt_' || lower(replace(gen_random_uuid()::text, '-', '')),
           '$PID', 'Pending', 'Authorized', 'system', 'auth_ok', '{}', now());"

# Capture (Authorized → Captured)
curl -s -X POST http://localhost:5000/v1/payments/$PID/capture \
  -H "Authorization: Bearer dev-key-mrc-acme" \
  -H "Idempotency-Key: $(uuidgen)" \
  -H "Content-Type: application/json" -d '{}' | jq .
# → 200, status: "Captured", events array has 3 entries

# Refund (Captured → Refunded)
curl -s -X POST http://localhost:5000/v1/payments/$PID/refund \
  -H "Authorization: Bearer dev-key-mrc-acme" \
  -H "Idempotency-Key: $(uuidgen)" \
  -H "Content-Type: application/json" \
  -d '{"reason":"customer_request"}' | jq .
# → 200, status: "Refunded", events array has 4 entries

# Attempting to capture a Captured payment → 409 invalid_state_transition
curl -i -X POST http://localhost:5000/v1/payments/$PID/capture \
  -H "Authorization: Bearer dev-key-mrc-acme" \
  -H "Idempotency-Key: $(uuidgen)" \
  -H "Content-Type: application/json" -d '{}'

# List with status filter and cursor pagination
curl -s "http://localhost:5000/v1/payments?status=Refunded&limit=10" \
  -H "Authorization: Bearer dev-key-mrc-acme" | jq .
# → { data: [...], next_cursor: null }
```

Replays are byte-identical: the same `Idempotency-Key` + same body returns
the cached `PaymentResponse` without re-running the transition. A different
body with the same key returns `409 idempotency_key_conflict`. A different
key against a payment already past the legal transition returns `409
invalid_state_transition`.

---

## Payment state machine

Phase 2's `Payment` aggregate enforces every transition. Illegal transitions
throw `InvalidTransitionException` and surface as `409 invalid_state_transition`.

```
                         POST /v1/payments
                                │
                                ▼
                          ┌─────────┐
                          │ Pending │
                          └────┬────┘
                  Authorize    │    Fail
            (Phase 3 worker)   ├────────────┐
                               ▼            ▼
                       ┌────────────┐   ┌────────┐
                       │ Authorized │   │ Failed │
                       └────┬───────┘   └────────┘
                            │     ▲
            POST /capture   │     │  Fail
                            ▼     │
                       ┌──────────┴─┐
                       │  Captured  │
                       └────┬───────┘
              Settle        │     POST /refund
        (Phase 3 worker)    ├────────────┐
                            ▼            │
                       ┌─────────┐       │
                       │ Settled │       │
                       └────┬────┘       │
                            │            │
              POST /refund  │            │
                            ▼            ▼
                       ┌──────────────────┐
                       │     Refunded     │
                       └──────────────────┘
```

`Failed` and `Refunded` are terminal — no transitions leave them.

Every transition writes a row to `payment_events`:

| column        | meaning                                                |
| ------------- | ------------------------------------------------------ |
| `from_status` | The status before the transition. `null` only on the initial create event. |
| `to_status`   | The status after the transition.                       |
| `actor`       | Who drove it: `api` (the user-facing endpoint), `system` (the Phase 3 worker), or `worker` (background settlement). |
| `reason`      | Domain-level reason code: `created`, `auth_ok`, `captured`, `settled`, `refunded`, `failed`. |
| `payload`     | Optional structured context — e.g. `{"reason":"customer_request"}` on refunds. |
| `at`          | Timestamp from `IClock.UtcNow`.                        |

`GET /v1/payments/{id}` returns the timeline ordered by `at` ascending.

---

## Architecture

### High level

Vertical-slice architecture inside a layered project graph. Each feature
(`CreatePayment`, `GetPayment`) is one folder containing its command, handler,
validator, and contract — instead of spreading those files across
`Controllers/`, `Services/`, and `Repositories/`. Cross-cutting concerns
(auth, logging, idempotency, EF Core) live in their own layers and are wired
in via DI.

```
HTTP request
   │
   ▼
CorrelationIdMiddleware     ← creates / propagates X-Request-Id
   │
   ▼
ExceptionHandlingMiddleware ← translates app exceptions to ErrorEnvelope
   │
   ▼
DevBearerAuthMiddleware     ← SHA-256 hash → merchants.api_key_hash
   │                          binds CurrentMerchant, skips /health
   ▼
Minimal API endpoint
   │
   ▼
MediatR command / query     ← FluentValidation runs before the handler
   │
   ▼
Handler                     ← uses IIdempotencyStore + IPaymentsDbContext
   │
   ▼
EF Core (PaymentsDbContext) ← single Postgres connection, snake_case mapping
```

### Project layout

```
backend/
├── src/
│   ├── PaymentPlatform.Api               # Hosting, middleware, endpoints, DI composition
│   ├── PaymentPlatform.Application       # MediatR features, validators, abstractions, common exceptions
│   ├── PaymentPlatform.Domain            # Payment + Merchant aggregates, value objects
│   ├── PaymentPlatform.Infrastructure    # EF Core DbContext + configurations + migration, idempotency store, SystemClock
│   └── PaymentPlatform.Contracts         # Public DTOs (request / response shapes, error envelope)
└── tests/
    ├── PaymentPlatform.UnitTests         # 124 tests — domain, state machine, validators, handlers
    └── PaymentPlatform.IntegrationTests  # 56 tests — Testcontainers Postgres + WebApplicationFactory
```

Reference direction (no cycles):
`Api → Application + Infrastructure + Contracts`,
`Application → Domain + Contracts`,
`Infrastructure → Application + Domain`,
`Contracts` and `Domain` reference nothing.

### Data model (Phase 1 subset)

- `merchants(id, name, api_key_hash, created_at)` — seeded with two rows.
- `payments(id, merchant_id, status, amount_minor, currency, customer_reference, card_token, metadata, created_at, updated_at, version)` — `status` walks the state machine (see [State machine](#payment-state-machine)); `version` backs optimistic concurrency on capture / refund.
- `payment_events(id, payment_id, from_status, to_status, actor, reason, payload, at)` — append-only audit log, populated on every transition (including the initial `null → Pending` event on create). `payload` is JSONB.
- `idempotency_keys(merchant_id, operation, key, request_hash, response_json, created_at)` — composite PK `(merchant_id, operation, key)`. Replay returns the cached `response_json`. The `operation` column is what makes per-operation key reuse safe.

The `(merchant_id, created_at DESC, id DESC)` index on `payments` powers
the Phase 2 list endpoint's cursor pagination.

### Idempotency

Two layers:

1. **Application-level cache:** `IIdempotencyStore` looks up `(merchant_id, operation, key)`
   before the handler runs. A hit returns the cached response body verbatim, with the
   endpoint re-applying the original status code (e.g. `201 Created` for create,
   `200 OK` for capture and refund).
2. **Database-level guard:** unique constraint on `(merchant_id, operation, key)`. If two
   concurrent requests race past the lookup, the loser catches Npgsql
   `DbUpdateException` (SQLSTATE `23505`) and falls into the replay branch.

Phase 2 added the `operation` column to that primary key — so the same
`Idempotency-Key` string can safely be reused across `create_payment`,
`capture_payment`, and `refund_payment` without collision. The rationale and
migration shape live in [`docs/adr/0007-idempotency-keys-per-operation.md`](docs/adr/0007-idempotency-keys-per-operation.md).

The body is hashed on insert. A future request with the same key but a
different body fails loudly as `409 idempotency_key_conflict`.

### Logging

- Serilog with `CompactJsonFormatter` to stdout — single-line JSON per event.
- `request_id` enriched via `LogContext.PushProperty` inside `CorrelationIdMiddleware`.
- `merchant_id` enriched once the auth middleware resolves a merchant.
- `card_token` is never passed to a logger and never appears in error
  envelopes. The integration test `LoggingTests.CardToken_IsNeverLogged`
  asserts this against the full captured log stream.
- `trace_id` is populated in error responses (`Activity.Current?.TraceId`).
  Wiring it onto every log line requires `Serilog.Enrichers.Span`, which
  Phase 4's OpenTelemetry work pulls in.

### Auth

A dev-only bearer scheme. The middleware SHA-256-hashes the incoming token
and looks up the matching `merchants.api_key_hash`. There are two seeded keys
(see [Dev keys](#dev-keys)). The seam — `ICurrentMerchant` resolved from DI —
is the same one an OAuth2 / OIDC implementation would plug into. Health
endpoints bypass auth.

---

## Dev keys

The migration seeds two merchants:

| Merchant id | Name        | Bearer token         |
| ----------- | ----------- | -------------------- |
| `mrc_acme`  | Acme Corp   | `dev-key-mrc-acme`   |
| `mrc_pied`  | Pied Piper  | `dev-key-mrc-pied`   |

Tokens are stored as SHA-256 hashes; rotating them means changing the seed
value and running a new migration. These keys are intentionally not secret —
they're dev affordances for the take-home.

---

## Running tests

```bash
cd backend
dotnet test                    # 180 tests: 124 unit + 56 integration
dotnet test --filter "FullyQualifiedName~UnitTests"          # unit only — no Docker required
dotnet test --filter "FullyQualifiedName~IntegrationTests"   # needs Docker running
```

Integration tests use Testcontainers to spin up a fresh `postgres:16-alpine`
container per test run, then `WebApplicationFactory<Program>` to host the
real API in-process against it. The fixture sets
`POSTGRES_HOST_AUTH_METHOD=trust` so the container accepts host-bound
connections without password handshake.

---

## Tradeoffs

Phase-1-relevant decisions. The full list across all phases lives in
[`.claude/plans/payment-platform.plan.md` §14](.claude/plans/payment-platform.plan.md).

| Decision               | Chose                          | Gave up                                | Why                                                                                                                |
| ---------------------- | ------------------------------ | -------------------------------------- | ------------------------------------------------------------------------------------------------------------------ |
| Database               | PostgreSQL                     | Mongo / Dynamo                         | Payments need ACID and unique constraints. JSONB still gives us flexibility where we want it.                       |
| Architecture           | Vertical slice + MediatR       | Classic layered (Controllers/Services) | Higher cohesion, less paging through folders to follow one feature, easier to retire a slice cleanly.               |
| Idempotency storage    | Postgres table                 | Redis                                  | DB is the source of truth. Adding Redis as a cache is a Phase 4+ optimization, not a correctness requirement.       |
| Auth                   | Hashed dev bearer              | OAuth2                                 | OAuth2 wiring is mechanical noise for an exercise. The seam (`ICurrentMerchant`) is identical.                       |
| ID format              | ULID                           | UUIDv4 / sequential int                | Lexicographically sortable, monotonic-ish, debugger-friendly, multi-region-safe by default.                          |
| Migration strategy     | `Database.Migrate()` on Dev startup | Migration-job container                | Phase 1 is a single API; a Job container is overkill. Phase 4 splits this into a dedicated migration step.           |
| Integration test infra | Real Postgres via Testcontainers | Mocked `DbContext`                   | Mocks lie about migrations, constraints, value conversions, and concurrency. Real containers catch real bugs.        |

---

## Assumptions

Phase-1-relevant assumptions. The full list across all phases lives in
[`.claude/plans/payment-platform.plan.md` §15](.claude/plans/payment-platform.plan.md).

1. **Scale.** ~500 merchants, ~5M payments/month, 200 req/s peak, p99 < 300ms.
2. **Single region for the exercise.** No code assumes single-region — IDs are
   globally unique, no in-process caches survive restart, idempotency is
   scoped per merchant. A second region is a deploy concern, not a code
   concern.
3. **No real card data.** `card_token` is an opaque stub. PCI vault is out of
   scope; the redaction discipline is what's being demonstrated.
4. **Currency.** Stored as integer minor units (cents). Single currency per
   payment. Mixed-currency rollups are a Phase 5+ concern.
5. **Time.** All timestamps UTC. `IClock` is injected (`SystemClock`) so tests
   can substitute a fixed clock.
6. **Tenancy.** Single-tenant deployment serving many merchants. Cross-tenant
   isolation is enforced in handler code via `merchant_id` filtering and
   verified by `GetPaymentTests.Returns404_WhenPaymentBelongsToOtherMerchant`.

---

## Production considerations

What would change before this is something to bet revenue on. The first item
is the most important.

- **Multi-region active-passive within the US.** Primary serves all traffic;
  standby runs warm with replicated Postgres and an idle RabbitMQ broker.
  Idempotency is what makes failover safe — merchant retries during the cut
  either hit the replicated cached response or process fresh. Either way: no
  double-charges.
- **Migrations as a separate step.** `Database.Migrate()` at API startup is a
  Phase 1 affordance. Production runs migrations as a pre-deploy job, gated
  by an approval workflow.
- **Real auth.** OAuth2 client-credentials for merchants, OIDC for dashboard
  users, mTLS service-to-service. The `ICurrentMerchant` seam is the
  insertion point.
- **Secrets out of env vars.** Vault / Key Vault / Secrets Manager with
  per-region replicas. The `ConnectionStrings__Payments` env var pattern is
  fine for dev, not for prod.
- **Real observability backends.** OTLP → Tempo + Mimir + Loki (or the
  cloud-native equivalents). Trace ids must carry a region tag for
  post-failover analysis.
- **Rate limiting** moves from per-instance to Redis-backed sliding window,
  per region.
- **Card data** lands in a separate tokenization vault in a different trust
  zone. The payment service never sees PAN.
- **Audit completeness.** Phase 2 adds a `payment_events` table tracking
  every state transition with actor + reason. That's the audit trail
  payments needs in production.

---

## What's next

Phases planned in [`.claude/plans/payment-platform.plan.md` §13](.claude/plans/payment-platform.plan.md):

- **Phase 3** — Async settlement worker via RabbitMQ, transactional outbox,
  configurable failure modes on the stub processor, DLQ. Replaces the Phase 2
  walkthrough's psql-driven `Pending → Authorized` step with a real worker.
- **Phase 4** — OpenTelemetry across API + worker, `/metrics`, `/health/ready`,
  log redaction enricher, ADRs.
- **Phase 5** — React + Vite dashboard with list view, status filter, cursor
  pagination, detail view with event timeline.
- **Phase 6** — Polish, scripted demo, finalized docs.

---

## Troubleshooting

| Symptom                                                                                                  | Cause / fix                                                                                                                                                                                                                                |
| -------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `docker compose up` fails with `port is already allocated` on `5432`                                    | A local Postgres (Homebrew, Postgres.app, another macOS Postgres install) owns the host port. Override the host-side mapping for one boot: `POSTGRES_HOST_PORT=15432 docker compose up -d`. The API connects to Postgres through the internal Docker network, so it is unaffected. Integration tests are also unaffected — Testcontainers always uses a random port. |
| `Bind for 0.0.0.0:5000 failed: port is already allocated`                                                | On macOS, this is almost always the AirPlay Receiver (System Settings → General → AirDrop & Handoff → AirPlay Receiver: off) or another local server. Override the host-side mapping: `API_HOST_PORT=5050 docker compose up -d` (then use `http://localhost:5050` for the walkthrough). |
| `Cannot connect to the Docker daemon`                                                                    | Docker Desktop isn't running. Open it; wait for the whale icon to stop animating; retry. Integration tests need this too.                                                                                                              |
| `dotnet test` integration tests fail with `28P01 password authentication failed for user "postgres"`     | A stray host Postgres is listening on `localhost:5432` and the API host config is falling through to it. Stop the host Postgres, or run only the unit tests (`--filter "FullyQualifiedName~UnitTests"`).                                |
| Integration tests fail with `Testcontainers ... failed to pull image`                                    | Docker is up but can't reach Docker Hub. Pre-pull manually: `docker pull postgres:16-alpine`.                                                                                                                                            |
| `401 Bearer token is not recognized` even though the key looks right                                     | The Postgres volume from a previous run is missing the seed data. Run `docker compose down -v` to drop the volume, then `docker compose up -d` to re-seed.                                                                              |
| Logs aren't JSON                                                                                          | You're looking at the .NET host bootstrap output. The Serilog JSON pipeline starts after `WebApplication.CreateBuilder`. Once you see the `"Now listening on"` line, every subsequent line is JSON.                                       |
| `MSB3277` warnings about `Microsoft.EntityFrameworkCore.Relational` versions                              | The pin in `Infrastructure.csproj` should silence this. If it returns after a NuGet bump, re-pin Relational to the major used by `Npgsql.EntityFrameworkCore.PostgreSQL`.                                                                |
