#!/usr/bin/env bash
#
# watch-lifecycle.sh — capture one of the seeded Authorized payments
# and watch the full async settlement pipeline play out.
#
# Usage:
#   ./scripts/run.sh                    # bring up the stack + seed data
#   open http://localhost:5173/payments # open the dashboard
#   ./scripts/watch-lifecycle.sh        # pick an Authorized seed, capture it, poll until Settled
#
# What you'll see:
#   - API processes capture synchronously → status = Captured
#   - Outbox dispatcher (in-process BackgroundService) picks up the
#     queued message ~2s later, publishes to RabbitMQ
#   - Worker consumes, calls stub processor, writes Settled state
#   - With the detail page open the badge transitions live (the
#     detail query polls every 3s while status is in-flight)

set -euo pipefail

API="${API_BASE_URL:-http://localhost:8080}"
TOKEN="${BEARER:-dev-key-mrc-acme}"
DASHBOARD="${DASHBOARD_BASE_URL:-http://localhost:5173}"

command -v jq >/dev/null || { echo "jq is required: brew install jq" >&2; exit 1; }

CYN=$'\033[36m'; GRN=$'\033[32m'; YLW=$'\033[33m'; DIM=$'\033[2m'; END=$'\033[0m'
step() { printf "\n${CYN}▶ %s${END}\n" "$*"; }

# Find an Authorized seeded payment. Walks the two seeded candidates;
# a fresh ./scripts/run.sh always produces both.
PAYMENT_ID=""
for candidate in pay_demo_authorized_001 pay_demo_authorized_002; do
  status=$(curl -s "${API}/v1/payments/${candidate}" \
    -H "Authorization: Bearer ${TOKEN}" | jq -r '.status // empty' 2>/dev/null || true)
  if [[ "$status" == "Authorized" ]]; then
    PAYMENT_ID="$candidate"
    break
  fi
done

if [[ -z "$PAYMENT_ID" ]]; then
  printf "${YLW}No Authorized seeded payment available.${END}\n"
  echo "  Both pay_demo_authorized_001 and _002 have already been captured."
  echo "  Reset with: docker compose down -v && ./scripts/run.sh"
  exit 1
fi

step "Selected ${PAYMENT_ID} (currently Authorized)"
echo "  Dashboard URL — open this in your browser before continuing:"
echo ""
printf "    ${GRN}${DASHBOARD}/payments/${PAYMENT_ID}${END}\n"
echo ""

step "Starting capture in 5s — switch to the browser tab now"
for n in 5 4 3 2 1; do
  printf "  %s..." "$n"
  sleep 1
done
echo ""

step "POST /v1/payments/${PAYMENT_ID}/capture"
curl -sS -X POST "${API}/v1/payments/${PAYMENT_ID}/capture" \
  -H "Authorization: Bearer ${TOKEN}" \
  -H "Idempotency-Key: watch-lifecycle-$(date +%s)" \
  -H "Content-Type: application/json" -d '{}' >/dev/null
echo "  (status should flip to Captured almost immediately on the detail page)"

step "Polling until Settled (worker normally lands it within ~5s)"
final=""
for i in $(seq 1 30); do
  s=$(curl -s "${API}/v1/payments/${PAYMENT_ID}" \
    -H "Authorization: Bearer ${TOKEN}" | jq -r '.status')
  printf "  t=%2ds  status=%s\n" "$i" "$s"
  if [[ "$s" == "Settled" || "$s" == "Failed" ]]; then
    final="$s"
    break
  fi
  sleep 1
done

if [[ -z "$final" ]]; then
  printf "${YLW}Did not reach a terminal state in 30s.${END} Check worker logs:\n"
  echo "  docker compose logs worker | tail -50"
  exit 1
fi

step "Final event timeline"
curl -s "${API}/v1/payments/${PAYMENT_ID}" -H "Authorization: Bearer ${TOKEN}" | \
  jq -r '.events[] | "  \(.at)  \(.from_status // "null"):10 -> \(.to_status):10  (\(.reason))"' | \
  sed -E 's/:10/          /g'

echo ""
printf "${GRN}✓ ${PAYMENT_ID} reached ${final}.${END}  ${DIM}Full async settlement pipeline traversed.${END}\n"
