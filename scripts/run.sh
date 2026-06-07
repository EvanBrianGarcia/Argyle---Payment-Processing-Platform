#!/usr/bin/env bash
#
# run.sh — bring up the entire stack and verify it's healthy end-to-end.
#
# Usage:
#   ./scripts/run.sh              # backend stack + demo, then print frontend command
#   ./scripts/run.sh --frontend   # also start the Vite dev server in the foreground
#   ./scripts/run.sh --no-demo    # skip the demo walk
#
# Idempotent — safe to re-run. Stops the stack with: docker compose down

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

RUN_DEMO=1
RUN_FRONTEND=0
for arg in "$@"; do
  case "$arg" in
    --no-demo)   RUN_DEMO=0 ;;
    --frontend)  RUN_FRONTEND=1 ;;
    -h|--help)
      sed -n '3,11p' "$0" | sed 's/^# \{0,1\}//'
      exit 0 ;;
    *)
      echo "run.sh: unknown flag '$arg'" >&2
      exit 2 ;;
  esac
done

GRN=$'\033[32m'; CYN=$'\033[36m'; YLW=$'\033[33m'; DIM=$'\033[2m'; END=$'\033[0m'
step() { printf "\n${CYN}▶ %s${END}\n" "$*"; }
ok()   { printf "  ${GRN}✓${END} %s\n" "$*"; }
warn() { printf "  ${YLW}!${END} %s\n" "$*"; }

# --- preflight ---
step "Preflight"
command -v docker >/dev/null || { echo "docker not found" >&2; exit 1; }
docker info >/dev/null 2>&1 || { echo "docker daemon not running" >&2; exit 1; }
ok "docker is up"
[[ $RUN_DEMO -eq 1 ]] && { command -v jq >/dev/null || warn "jq not found — demo step will fail. brew install jq"; }

# --- bring up the stack ---
step "docker compose up -d --build (postgres + rabbitmq + api + worker)"
docker compose up -d --build --wait 2>&1 | tail -10
ok "compose stack is healthy"

# --- wait for the API to answer ---
step "Waiting for API /health/ready"
API="${API_BASE_URL:-http://localhost:8080}"
for i in $(seq 1 60); do
  if curl -sf "${API}/health/ready" -o /dev/null; then
    ok "API ready after ${i}s"
    break
  fi
  [[ $i -eq 60 ]] && { echo "API did not become ready in 60s" >&2; docker compose logs --tail 30 api; exit 1; }
  sleep 1
done

# --- seed demo data (payments across every status, so filter chips have rows) ---
step "Seeding demo payments across every status"
./scripts/seed-demo-data.sh

# --- run the demo walk ---
if [[ $RUN_DEMO -eq 1 ]]; then
  step "Running endpoint demo walk"
  ./scripts/demo.sh
fi

# --- frontend ---
echo
printf "${GRN}✓ Backend stack is up at ${API}${END}\n"
printf "${DIM}  rabbitmq mgmt:  http://localhost:15672  (guest / guest)${END}\n"
printf "${DIM}  metrics (api):  ${API}/metrics${END}\n"
printf "${DIM}  metrics (wkr):  http://localhost:9090/metrics${END}\n"

if [[ $RUN_FRONTEND -eq 1 ]]; then
  step "Starting frontend dev server (foreground — Ctrl-C to stop)"
  cd frontend
  [[ -d node_modules ]] || pnpm install
  exec pnpm dev
else
  echo
  printf "${CYN}▶ Dashboard:${END} cd frontend && pnpm install && pnpm dev   ${DIM}# http://localhost:5173/payments${END}\n"
  printf "${CYN}▶ Tear down:${END} docker compose down ${DIM}(add -v to wipe postgres data)${END}\n"
fi
