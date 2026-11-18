#!/usr/bin/env bash
# Smoke test of the packaged gateway (DESIGN D17, 7.1): exercises the running container, built from
# the Dockerfile and talking to the bank simulator, over HTTP. Start both first:
#   docker compose up -d --build
#   scripts/container-smoke-test.sh
# Uses the development merchant key, because compose runs the gateway in Development.
set -euo pipefail

GATEWAY_URL="${GATEWAY_URL:-http://localhost:5080}"
SIMULATOR_ADMIN_URL="${SIMULATOR_ADMIN_URL:-http://localhost:2525}"
API_KEY="${API_KEY:-sk_test_merchant_a}"

fail() { echo "FAIL: $*" >&2; exit 1; }

wait_for() {
  local url=$1
  for _ in $(seq 1 60); do
    curl -fs -o /dev/null "$url" && return 0
    sleep 1
  done
  fail "$url did not become available"
}

# post <card number>: prints "<status code> <body>"
post() {
  curl -s -w '\n%{http_code}' -X POST "$GATEWAY_URL/api/payments" \
    -H "Content-Type: application/json" \
    -H "Authorization: Bearer $API_KEY" \
    -d "{\"cardNumber\":\"$1\",\"expiryMonth\":12,\"expiryYear\":2030,\"currency\":\"GBP\",\"amount\":1250,\"cvv\":\"123\"}"
}

# expect <response> <status code> <jq filter that must be true>
expect() {
  local response=$1 code=$2 filter=$3
  local body actual
  body=$(echo "$response" | sed '$d')
  actual=$(echo "$response" | tail -n 1)
  [[ "$actual" == "$code" ]] || fail "expected HTTP $code, got $actual: $body"
  echo "$body" | jq -e "$filter" > /dev/null || fail "body did not match $filter: $body"
  echo "$body"
}

echo "Waiting for the bank simulator and the gateway"
wait_for "$SIMULATOR_ADMIN_URL"
wait_for "$GATEWAY_URL/health/live"
wait_for "$GATEWAY_URL/health/ready"

echo "Authorized payment"
body=$(expect "$(post 2222405343248877)" 200 '.status == "Authorized" and .cardNumberLastFour == "8877"')
id=$(echo "$body" | jq -r '.id')

echo "Retrieve it"
expect "$(curl -s -w '\n%{http_code}' -H "Authorization: Bearer $API_KEY" "$GATEWAY_URL/api/payments/$id")" \
  200 ".id == \"$id\" and .status == \"Authorized\"" > /dev/null

echo "Declined payment"
expect "$(post 2222405343298112)" 200 '.status == "Declined"' > /dev/null

echo "Bank unavailable (card ending in 0)"
expect "$(post 2222405343218110)" 502 '.error | contains("not processed")' > /dev/null

echo "No API key"
expect "$(curl -s -w '\n%{http_code}' "$GATEWAY_URL/api/payments/$id")" 401 '.error' > /dev/null

echo "Container smoke test passed"
