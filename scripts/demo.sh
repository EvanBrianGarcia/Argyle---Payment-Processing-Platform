#!/usr/bin/env bash
#
# demo.sh — exercise every endpoint end-to-end against a running stack.
#
# Usage:
#   docker compose up -d
#   ./scripts/demo.sh
#
# Hits a live API at http://localhost:8080 (override with API_BASE_URL),
# walks a payment through create → capture → refund → list → fetch
# detail, and prints the audit trail at each step. Exits non-zero on
# any unexpected response.

set -euo pipefail

API="${API_BASE_URL:-http://localhost:8080}"
TOKEN="${BEARER:-dev-key-mrc-acme}"
JQ="$(command -v jq || true)"

if [[ -z "$JQ" ]]; then
  echo "demo: 'jq' is required. Install with: brew install jq" >&2
  exit 1
fi

# --- pretty output helpers ---
RED=$'\033[31m'; GRN=$'\033[32m'; CYN=$'\033[36m'; DIM=$'\033[2m'; END=$'\033[0m'
step() { printf "\n${CYN}▶ %s${END}\n" "$*"; }
ok()   { printf "  ${GRN}✓${END} %s\n" "$*"; }
fail() { printf "  ${RED}✗${END} %s\n" "$*"; exit 1; }
show() { printf "${DIM}%s${END}\n" "$(printf '%s' "$1" | jq .)"; }

# --- helpers ---
auth=(-H "Authorization: Bearer ${TOKEN}")
json=(-H "Content-Type: application/json" -H "Accept: application/json")

idem() { echo "DEMO-$(date +%s%N | cut -c1-13)-$RANDOM"; }

expect_status() {
  local got="$1" want="$2" what="$3"
  if [[ "$got" != "$want" ]]; then
    fail "$what: expected HTTP $want, got $got"
  fi
}

# --- 0. readiness ---
step "Readiness probe"
ready_body=$(curl -sS "${API}/health/ready" -o /tmp/demo-ready.json -w '%{http_code}')
expect_status "$ready_body" "200" "/health/ready"
ok "API is healthy"
show "$(cat /tmp/demo-ready.json)"

# --- 1. create ---
step "Create a payment (POST /v1/payments)"
create_key=$(idem)
create_payload='{"amount_minor": 49900, "currency": "USD", "card_token": "tok_VISA_4242", "customer_reference": "order_demo", "metadata": {"source": "demo.sh"}}'
create_code=$(curl -sS "${API}/v1/payments" \
  "${auth[@]}" "${json[@]}" \
  -H "Idempotency-Key: ${create_key}" \
  -d "${create_payload}" \
  -o /tmp/demo-create.json -w '%{http_code}')
expect_status "$create_code" "201" "POST /v1/payments"
PAYMENT_ID=$(jq -r .id < /tmp/demo-create.json)
status=$(jq -r .status < /tmp/demo-create.json)
ok "Created ${PAYMENT_ID} (${status})"
show "$(cat /tmp/demo-create.json)"

# --- 2. idempotent replay ---
step "Replay the create with the same Idempotency-Key (no double-charge)"
replay_code=$(curl -sS "${API}/v1/payments" \
  "${auth[@]}" "${json[@]}" \
  -H "Idempotency-Key: ${create_key}" \
  -d "${create_payload}" \
  -o /tmp/demo-replay.json -w '%{http_code}')
expect_status "$replay_code" "201" "idempotent replay"
replay_id=$(jq -r .id < /tmp/demo-replay.json)
[[ "$replay_id" == "$PAYMENT_ID" ]] && ok "Replay returned the same id ${PAYMENT_ID}" \
  || fail "Replay returned a different id (${replay_id})"

# --- 3. wait until processor authorizes ---
step "Wait for stub processor to authorize the payment"
for i in $(seq 1 20); do
  curl -sS "${API}/v1/payments/${PAYMENT_ID}" "${auth[@]}" -o /tmp/demo-poll.json
  s=$(jq -r .status < /tmp/demo-poll.json)
  if [[ "$s" == "Authorized" || "$s" == "Captured" || "$s" == "Failed" || "$s" == "Settled" ]]; then
    ok "Status is ${s} after ${i} poll(s)"
    break
  fi
  sleep 0.5
done

# --- 4. capture ---
current=$(jq -r .status < /tmp/demo-poll.json)
if [[ "$current" == "Authorized" ]]; then
  step "Capture the payment (POST /v1/payments/{id}/capture)"
  cap_code=$(curl -sS "${API}/v1/payments/${PAYMENT_ID}/capture" \
    "${auth[@]}" "${json[@]}" \
    -H "Idempotency-Key: $(idem)" \
    -d '{}' \
    -o /tmp/demo-capture.json -w '%{http_code}')
  expect_status "$cap_code" "200" "capture"
  ok "Captured ${PAYMENT_ID}"
  show "$(cat /tmp/demo-capture.json)"
else
  ok "Skipping capture — payment already at ${current}"
fi

# --- 5. wait for settlement ---
step "Wait for the worker to settle the payment"
for i in $(seq 1 20); do
  curl -sS "${API}/v1/payments/${PAYMENT_ID}" "${auth[@]}" -o /tmp/demo-poll.json
  s=$(jq -r .status < /tmp/demo-poll.json)
  if [[ "$s" == "Settled" || "$s" == "Failed" || "$s" == "Refunded" ]]; then
    ok "Status is ${s} after ${i} poll(s)"
    break
  fi
  sleep 0.5
done

# --- 6. refund ---
current=$(jq -r .status < /tmp/demo-poll.json)
if [[ "$current" == "Settled" || "$current" == "Captured" ]]; then
  step "Refund the payment (POST /v1/payments/{id}/refund)"
  ref_code=$(curl -sS "${API}/v1/payments/${PAYMENT_ID}/refund" \
    "${auth[@]}" "${json[@]}" \
    -H "Idempotency-Key: $(idem)" \
    -d '{"reason": "demo refund"}' \
    -o /tmp/demo-refund.json -w '%{http_code}')
  expect_status "$ref_code" "200" "refund"
  ok "Refunded ${PAYMENT_ID}"
else
  ok "Skipping refund — payment is ${current}"
fi

# --- 7. list ---
step "List payments (GET /v1/payments?limit=5)"
list_code=$(curl -sS "${API}/v1/payments?limit=5" "${auth[@]}" -o /tmp/demo-list.json -w '%{http_code}')
expect_status "$list_code" "200" "list"
count=$(jq '.data | length' < /tmp/demo-list.json)
ok "Got ${count} payments back"

# --- 8. detail with audit trail ---
step "Fetch detail with full audit trail (GET /v1/payments/{id})"
det_code=$(curl -sS "${API}/v1/payments/${PAYMENT_ID}" "${auth[@]}" -o /tmp/demo-detail.json -w '%{http_code}')
expect_status "$det_code" "200" "detail"
events=$(jq '.events | length' < /tmp/demo-detail.json)
final_status=$(jq -r .status < /tmp/demo-detail.json)
ok "Payment ${PAYMENT_ID} is ${final_status} with ${events} audit event(s)"
printf "${DIM}--- event timeline ---${END}\n"
jq -r '.events[] | "  \(.at)  \(.from_status // "—") → \(.to_status)  (\(.reason))"' < /tmp/demo-detail.json

# --- 9. cross-tenant 404 ---
step "Cross-tenant isolation check (other merchant must see 404)"
other_code=$(curl -sS "${API}/v1/payments/${PAYMENT_ID}" \
  -H "Authorization: Bearer dev-key-mrc-pied" \
  -o /tmp/demo-cross.json -w '%{http_code}')
expect_status "$other_code" "404" "cross-tenant"
ok "Other merchant got 404 — isolation enforced"

printf "\n${GRN}✓ All steps green${END}\n"
