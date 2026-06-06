# Phase 2 acceptance walkthrough — 2026-06-06T04:49:31Z

Stack: `POSTGRES_HOST_PORT=15432 API_HOST_PORT=5050 docker compose up -d`.
API base URL: `http://localhost:5050` (host port override; AirPlay holds :5000 on macOS).
Database in compose is named `payments` (not `paymentplatform` as the plan's draft showed).

## Step 0 — Seed: create a Pending payment
```bash
POST /v1/payments → HTTP 201, id=pay_01KTDM7JKGZRE6HWXDHPSD55AG, status=Pending
events: [{"from_status":null,"to_status":"Pending","reason":"created","actor":"api"}]
```

## Step 1 — Drive Pending → Authorized in the DB (Phase 3's worker stand-in)
```bash
UPDATE 1
INSERT 0 1
```

## Step 2 — Capture (happy)
```bash
HTTP 200
status: Captured
events: [{"from_status":null,"to_status":"Pending","reason":"created","actor":"api"},{"from_status":"Pending","to_status":"Authorized","reason":"auth_ok","actor":"system"},{"from_status":"Authorized","to_status":"Captured","reason":"captured","actor":"api"}]
```

## Step 3 — Capture replay (same key, same body)
```bash
HTTP 200
bodies: byte-identical ✓
```

## Step 4 — Capture an already-captured payment (new key)
```bash
HTTP 409
error: {"code":"invalid_state_transition","message":"Payment cannot transition from Captured to Captured."}
```

## Step 5 — Refund
```bash
HTTP 200
status: Refunded
events: [{"from_status":null,"to_status":"Pending","reason":"created","actor":"api"},{"from_status":"Pending","to_status":"Authorized","reason":"auth_ok","actor":"system"},{"from_status":"Authorized","to_status":"Captured","reason":"captured","actor":"api"},{"from_status":"Captured","to_status":"Refunded","reason":"refunded","actor":"api"}]
```

## Step 6 — List Refunded payments
```bash
{"count":1,"next_cursor":null,"first_id":"pay_01KTDM7JKGZRE6HWXDHPSD55AG","first_status":"Refunded"}
```

## Step 7 — Error response carries X-Request-Id (polish fix #1)
```bash
X-Request-Id header: 01KTDM7KD3VT20YGTEQTM48RSG
error.request_id:    01KTDM7KD3VT20YGTEQTM48RSG
error.trace_id:      4bac8ca91a1d48b1e540d8276aa66c9d
request_id match: ✓
```

## Step 8 — Full test suite
```bash
(See test runs in the Phase 2 commit history; total 124 unit + 56 integration green as of commit 740db77.)
```

## Per-operation idempotency key reuse

An idempotency key is scoped to its (merchant, operation) pair, so the same
string can be reused across create/capture/refund on different payments without colliding:

```bash
create with key=$SHARED → HTTP 201, id=pay_01KTDM7KFKSTNK4RHNFWPVXYVX
capture with key=$SHARED → HTTP 200, status=Captured
(Same key string, two different operations, no collision — per ADR-0006.)
```
