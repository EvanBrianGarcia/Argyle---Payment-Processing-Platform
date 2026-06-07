#!/usr/bin/env bash
#
# seed-demo-data.sh — insert a spread of payments at every status so the
# dashboard's filter chips actually have rows to show.
#
# Idempotent: skips if Acme already has ≥ 10 payments. Talks to Postgres
# through the running `payments-postgres` container, so the stack must
# already be up (./scripts/run.sh handles that).

set -euo pipefail

CONTAINER="${POSTGRES_CONTAINER:-payments-postgres}"
DB="${POSTGRES_DB:-payments}"
USER="${POSTGRES_USER:-postgres}"
MERCHANT="${SEED_MERCHANT_ID:-mrc_acme}"

if ! docker ps --format '{{.Names}}' | grep -q "^${CONTAINER}\$"; then
  echo "seed-demo-data: ${CONTAINER} is not running. Start the stack first (./scripts/run.sh)." >&2
  exit 1
fi

# Skip if already seeded (anything >= 10 for the target merchant counts as seeded)
existing=$(docker exec "$CONTAINER" psql -U "$USER" -d "$DB" -tAc \
  "SELECT count(*) FROM payments WHERE merchant_id = '$MERCHANT';")
if [[ "$existing" -ge 10 ]]; then
  echo "seed-demo-data: ${MERCHANT} already has ${existing} payments — skipping."
  exit 0
fi

echo "seed-demo-data: inserting demo payments for ${MERCHANT}…"

# All IDs are deterministic so reruns of the SQL itself stay idempotent
# via ON CONFLICT DO NOTHING. Times are spread across the last 24h so
# the "last 24 hours" header on the dashboard is accurate.
docker exec -i "$CONTAINER" psql -U "$USER" -d "$DB" <<SQL
BEGIN;

-- 2 x Pending
INSERT INTO payments (id, merchant_id, card_token, customer_reference, metadata, status, created_at, updated_at, version, amount_minor, currency) VALUES
('pay_demo_pending_001',    '${MERCHANT}', 'tok_VISA_4242', 'order_demo_pending_1',  '{"source":"seed"}', 'Pending',    now() - interval ' 1 hours', now() - interval ' 1 hours', 0,  12500, 'USD'),
('pay_demo_pending_002',    '${MERCHANT}', 'tok_VISA_4242', 'order_demo_pending_2',  '{"source":"seed"}', 'Pending',    now() - interval ' 2 hours', now() - interval ' 2 hours', 0,   8900, 'USD'),
-- 2 x Authorized
('pay_demo_authorized_001', '${MERCHANT}', 'tok_VISA_4242', 'order_demo_auth_1',     '{"source":"seed"}', 'Authorized', now() - interval ' 3 hours', now() - interval ' 3 hours', 1,  49900, 'USD'),
('pay_demo_authorized_002', '${MERCHANT}', 'tok_VISA_4242', 'order_demo_auth_2',     '{"source":"seed"}', 'Authorized', now() - interval ' 4 hours', now() - interval ' 4 hours', 1, 150000, 'USD'),
-- 2 x Captured
('pay_demo_captured_001',   '${MERCHANT}', 'tok_VISA_4242', 'order_demo_cap_1',      '{"source":"seed"}', 'Captured',   now() - interval ' 5 hours', now() - interval ' 5 hours', 2,  24900, 'USD'),
('pay_demo_captured_002',   '${MERCHANT}', 'tok_VISA_4242', 'order_demo_cap_2',      '{"source":"seed"}', 'Captured',   now() - interval ' 6 hours', now() - interval ' 6 hours', 2,  79900, 'USD'),
-- 3 x Settled
('pay_demo_settled_001',    '${MERCHANT}', 'tok_VISA_4242', 'order_demo_set_1',      '{"source":"seed"}', 'Settled',    now() - interval ' 8 hours', now() - interval ' 7 hours', 3, 124500, 'USD'),
('pay_demo_settled_002',    '${MERCHANT}', 'tok_VISA_4242', 'order_demo_set_2',      '{"source":"seed"}', 'Settled',    now() - interval '10 hours', now() - interval ' 9 hours', 3, 182000, 'USD'),
('pay_demo_settled_003',    '${MERCHANT}', 'tok_VISA_4242', 'order_demo_set_3',      '{"source":"seed"}', 'Settled',    now() - interval '13 hours', now() - interval '12 hours', 3,   4995, 'USD'),
-- 2 x Failed
('pay_demo_failed_001',     '${MERCHANT}', 'tok_VISA_4242', 'order_demo_fail_1',     '{"source":"seed"}', 'Failed',     now() - interval '15 hours', now() - interval '15 hours', 2,  39900, 'USD'),
('pay_demo_failed_002',     '${MERCHANT}', 'tok_VISA_4242', 'order_demo_fail_2',     '{"source":"seed"}', 'Failed',     now() - interval '18 hours', now() - interval '18 hours', 2,   9900, 'USD'),
-- 2 x Refunded
('pay_demo_refunded_001',   '${MERCHANT}', 'tok_VISA_4242', 'order_demo_ref_1',      '{"source":"seed"}', 'Refunded',   now() - interval '20 hours', now() - interval '19 hours', 4,  29900, 'USD'),
('pay_demo_refunded_002',   '${MERCHANT}', 'tok_VISA_4242', 'order_demo_ref_2',      '{"source":"seed"}', 'Refunded',   now() - interval '23 hours', now() - interval '22 hours', 4, 599000, 'USD')
ON CONFLICT (id) DO NOTHING;

-- Event timelines. Every payment has a 'created' event; non-Pending also
-- have the authorize/capture/settle/fail/refund events that match.
INSERT INTO payment_events (id, payment_id, from_status, to_status, actor, reason, payload, at) VALUES

-- Pending
('evt_demo_pending_001_a', 'pay_demo_pending_001',    NULL,         'Pending',    'api',    'created',                      '{}', now() - interval ' 1 hours'),
('evt_demo_pending_002_a', 'pay_demo_pending_002',    NULL,         'Pending',    'api',    'created',                      '{}', now() - interval ' 2 hours'),

-- Authorized
('evt_demo_auth_001_a',    'pay_demo_authorized_001', NULL,         'Pending',    'api',    'created',                      '{}', now() - interval ' 3 hours'),
('evt_demo_auth_001_b',    'pay_demo_authorized_001', 'Pending',    'Authorized', 'system', 'Processor authorization succeeded', '{"auth_code":"773901"}', now() - interval ' 3 hours' + interval '4 seconds'),
('evt_demo_auth_002_a',    'pay_demo_authorized_002', NULL,         'Pending',    'api',    'created',                      '{}', now() - interval ' 4 hours'),
('evt_demo_auth_002_b',    'pay_demo_authorized_002', 'Pending',    'Authorized', 'system', 'Processor authorization succeeded', '{"auth_code":"118822"}', now() - interval ' 4 hours' + interval '3 seconds'),

-- Captured
('evt_demo_cap_001_a',     'pay_demo_captured_001',   NULL,         'Pending',    'api',    'created',                      '{}', now() - interval ' 5 hours'),
('evt_demo_cap_001_b',     'pay_demo_captured_001',   'Pending',    'Authorized', 'system', 'Processor authorization succeeded', '{"auth_code":"557721"}', now() - interval ' 5 hours' + interval '2 seconds'),
('evt_demo_cap_001_c',     'pay_demo_captured_001',   'Authorized', 'Captured',   'api',    'captured',                     '{}', now() - interval ' 5 hours' + interval '4 seconds'),
('evt_demo_cap_002_a',     'pay_demo_captured_002',   NULL,         'Pending',    'api',    'created',                      '{}', now() - interval ' 6 hours'),
('evt_demo_cap_002_b',     'pay_demo_captured_002',   'Pending',    'Authorized', 'system', 'Processor authorization succeeded', '{"auth_code":"909102"}', now() - interval ' 6 hours' + interval '3 seconds'),
('evt_demo_cap_002_c',     'pay_demo_captured_002',   'Authorized', 'Captured',   'api',    'captured',                     '{}', now() - interval ' 6 hours' + interval '5 seconds'),

-- Settled
('evt_demo_set_001_a',     'pay_demo_settled_001',    NULL,         'Pending',    'api',    'created',                      '{}', now() - interval ' 8 hours'),
('evt_demo_set_001_b',     'pay_demo_settled_001',    'Pending',    'Authorized', 'system', 'Processor authorization succeeded', '{"auth_code":"441290"}', now() - interval ' 8 hours' + interval '2 seconds'),
('evt_demo_set_001_c',     'pay_demo_settled_001',    'Authorized', 'Captured',   'api',    'captured',                     '{}', now() - interval ' 8 hours' + interval '4 seconds'),
('evt_demo_set_001_d',     'pay_demo_settled_001',    'Captured',   'Settled',    'worker', 'Settled by processor',         '{"batch":"BATCH_0421"}', now() - interval ' 7 hours'),
('evt_demo_set_002_a',     'pay_demo_settled_002',    NULL,         'Pending',    'api',    'created',                      '{}', now() - interval '10 hours'),
('evt_demo_set_002_b',     'pay_demo_settled_002',    'Pending',    'Authorized', 'system', 'Processor authorization succeeded', '{"auth_code":"720198"}', now() - interval '10 hours' + interval '2 seconds'),
('evt_demo_set_002_c',     'pay_demo_settled_002',    'Authorized', 'Captured',   'api',    'captured',                     '{}', now() - interval '10 hours' + interval '5 seconds'),
('evt_demo_set_002_d',     'pay_demo_settled_002',    'Captured',   'Settled',    'worker', 'Settled by processor',         '{"batch":"BATCH_0420"}', now() - interval ' 9 hours'),
('evt_demo_set_003_a',     'pay_demo_settled_003',    NULL,         'Pending',    'api',    'created',                      '{}', now() - interval '13 hours'),
('evt_demo_set_003_b',     'pay_demo_settled_003',    'Pending',    'Authorized', 'system', 'Processor authorization succeeded', '{"auth_code":"330187"}', now() - interval '13 hours' + interval '3 seconds'),
('evt_demo_set_003_c',     'pay_demo_settled_003',    'Authorized', 'Captured',   'api',    'captured',                     '{}', now() - interval '13 hours' + interval '6 seconds'),
('evt_demo_set_003_d',     'pay_demo_settled_003',    'Captured',   'Settled',    'worker', 'Settled by processor',         '{"batch":"BATCH_0419"}', now() - interval '12 hours'),

-- Failed
('evt_demo_fail_001_a',    'pay_demo_failed_001',     NULL,         'Pending',    'api',    'created',                      '{}', now() - interval '15 hours'),
('evt_demo_fail_001_b',    'pay_demo_failed_001',     'Pending',    'Authorized', 'system', 'Processor authorization succeeded', '{"auth_code":"212501"}', now() - interval '15 hours' + interval '2 seconds'),
('evt_demo_fail_001_c',    'pay_demo_failed_001',     'Authorized', 'Failed',     'system', 'Processor declined capture: insufficient_funds', '{"processor_code":"card_declined","decline_reason":"insufficient_funds"}', now() - interval '15 hours' + interval '5 seconds'),
('evt_demo_fail_002_a',    'pay_demo_failed_002',     NULL,         'Pending',    'api',    'created',                      '{}', now() - interval '18 hours'),
('evt_demo_fail_002_b',    'pay_demo_failed_002',     'Pending',    'Authorized', 'system', 'Processor authorization succeeded', '{"auth_code":"480221"}', now() - interval '18 hours' + interval '3 seconds'),
('evt_demo_fail_002_c',    'pay_demo_failed_002',     'Authorized', 'Failed',     'system', 'Processor declined capture: do_not_honor', '{"processor_code":"card_declined","decline_reason":"do_not_honor"}', now() - interval '18 hours' + interval '6 seconds'),

-- Refunded
('evt_demo_ref_001_a',     'pay_demo_refunded_001',   NULL,         'Pending',    'api',    'created',                      '{}', now() - interval '20 hours'),
('evt_demo_ref_001_b',     'pay_demo_refunded_001',   'Pending',    'Authorized', 'system', 'Processor authorization succeeded', '{"auth_code":"610421"}', now() - interval '20 hours' + interval '2 seconds'),
('evt_demo_ref_001_c',     'pay_demo_refunded_001',   'Authorized', 'Captured',   'api',    'captured',                     '{}', now() - interval '20 hours' + interval '4 seconds'),
('evt_demo_ref_001_d',     'pay_demo_refunded_001',   'Captured',   'Settled',    'worker', 'Settled by processor',         '{"batch":"BATCH_0418"}', now() - interval '19 hours' - interval '20 minutes'),
('evt_demo_ref_001_e',     'pay_demo_refunded_001',   'Settled',    'Refunded',   'api',    'refunded',                     '{"reason":"customer request"}', now() - interval '19 hours'),
('evt_demo_ref_002_a',     'pay_demo_refunded_002',   NULL,         'Pending',    'api',    'created',                      '{}', now() - interval '23 hours'),
('evt_demo_ref_002_b',     'pay_demo_refunded_002',   'Pending',    'Authorized', 'system', 'Processor authorization succeeded', '{"auth_code":"883102"}', now() - interval '23 hours' + interval '3 seconds'),
('evt_demo_ref_002_c',     'pay_demo_refunded_002',   'Authorized', 'Captured',   'api',    'captured',                     '{}', now() - interval '23 hours' + interval '7 seconds'),
('evt_demo_ref_002_d',     'pay_demo_refunded_002',   'Captured',   'Settled',    'worker', 'Settled by processor',         '{"batch":"BATCH_0417"}', now() - interval '22 hours' - interval '15 minutes'),
('evt_demo_ref_002_e',     'pay_demo_refunded_002',   'Settled',    'Refunded',   'api',    'refunded',                     '{"reason":"duplicate charge"}', now() - interval '22 hours')

ON CONFLICT (id) DO NOTHING;

COMMIT;
SQL

count=$(docker exec "$CONTAINER" psql -U "$USER" -d "$DB" -tAc \
  "SELECT count(*) FROM payments WHERE merchant_id = '$MERCHANT';")
echo "seed-demo-data: done. ${MERCHANT} now has ${count} payment(s)."
