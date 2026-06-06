# ADR-0007: Idempotency keys are scoped per operation

**Status:** Accepted
**Date:** 2026-06-05
**Phase:** 2

## Context

Phase 1 wired idempotency for `POST /v1/payments` only. The `idempotency_keys` composite primary key is `(merchant_id, key)`. A row stores the cached response status + body for that exact request.

Phase 2 adds idempotency to `POST /v1/payments/{id}/capture` and `POST /v1/payments/{id}/refund`. The question is whether the key namespace is shared across all endpoints (one global key per merchant) or scoped per endpoint.

The concrete risk if we share the namespace: a merchant generates a UUID per logical operation in their own backend (a common pattern). They use the same UUID for "create the payment, then capture it" because to them it's all one logical "place this order" flow. With a shared namespace, the second call collides on the existing row — either we replay the wrong response, or we 409 a legitimate request as a phantom conflict.

Master plan §10 leaned toward per-endpoint without formalizing it. This ADR makes the choice explicit and the implementation matches.

## Decision

Extend the composite primary key on `idempotency_keys` from `(merchant_id, key)` to `(merchant_id, operation, key)`. `operation` is a short string the handler supplies when claiming the key.

Operation values defined in Phase 2 (`IdempotencyOperations` static class):

| Constant | Value |
|---|---|
| `CreatePayment` | `create_payment` |
| `CapturePayment` | `capture_payment` |
| `RefundPayment` | `refund_payment` |

`IIdempotencyStore.FindAsync` takes `(merchantId, operation, key)`. Handlers always pass their own operation constant; the API surface does not let callers spoof it.

The migration:
1. Adds `operation text NOT NULL DEFAULT 'create_payment'`. Backfills the default into any Phase 1 rows that exist.
2. Drops the old PK on `(merchant_id, key)`.
3. Creates the new PK on `(merchant_id, operation, key)`.
4. Drops the `DEFAULT 'create_payment'` so new rows are forced to specify the operation explicitly.

`Down()` reverses each step.

## Consequences

- **A single UUID can serve as the idempotency key for create + capture + refund in one merchant flow without false collisions.** This matches the natural way merchants build "one logical operation" wrappers.
- **The `idempotency_key_conflict` error code is unambiguous.** It now always means "same operation, same key, different body" — a real bug on the caller's side.
- **One uniqueness constraint per (merchant, operation).** Two concurrent captures for the same merchant with the same key still race correctly on the unique-violation path Phase 1 uses; the operation discriminator does not weaken that guarantee.
- **The cached response body is operation-specific.** A capture's cached response is never returned for a refund call, even with the same key.
- **Cross-operation replay attacks become structurally impossible.** Even if a merchant accidentally reuses a key from a capture call on a refund call, the refund handler claims a fresh row under `operation = refund_payment` — no information from the capture leaks.

## Alternatives considered

- **Shared `(merchant_id, key)` namespace.** Rejected for the reason above: legitimate merchant patterns produce false collisions.
- **Prefix the key string with the operation name (`capture:abc-123`) instead of adding a column.** Rejected. The discriminator becomes part of the stored key text rather than a separate field — harder to query, harder to audit, and a malformed prefix on the caller side could silently change the namespace. A dedicated column is one extra `text` column and one PK swap.
- **One physical table per operation (`payment_idempotency_keys`, `capture_idempotency_keys`, ...).** Rejected. Multiplies the schema for no benefit; the row shape is identical across operations. A discriminator column is the textbook fix.
