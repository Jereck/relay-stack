#!/usr/bin/env bash
# End-to-end Phase 0 walkthrough using only curl and jq.
#   export RELAYSTACK_API_KEY=rs_dev_...        (from the bootstrap command)
#   ./samples/curl/walkthrough.sh
# Defaults target `docker compose up`. When running the hosts with `dotnet run`, set
#   WEBHOOK_URL=http://localhost:8090/webhooks
# Any failed request stops the script and prints the HTTP status and the API's error.
set -euo pipefail

API="${API_URL:-http://localhost:8080}"
RECEIVER="${RECEIVER_URL:-http://localhost:8090}"               # reachable from your machine (control endpoints)
WEBHOOK_URL="${WEBHOOK_URL:-http://dev-receiver:8080/webhooks}" # reachable from the worker
DIR="$(cd "$(dirname "$0")/.." && pwd)"

step() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
fail() { printf '\n\033[31mStopped: %s\033[0m\n' "$*" >&2; exit 1; }

command -v jq >/dev/null || fail "jq is not installed (macOS: brew install jq)."
[[ -n "${RELAYSTACK_API_KEY:-}" ]] || fail "RELAYSTACK_API_KEY is not set in this terminal. Run the bootstrap command and export the rs_dev_... key it prints."
auth=(-H "Authorization: Bearer $RELAYSTACK_API_KEY")

# curl wrapper: prints the response body on success; on an HTTP error or connection failure, prints why and exits.
# Messages go to stderr so they are visible even when the output is captured with $(...).
call() {
  local url out code
  for url; do :; done # the URL is always the last argument
  out=$(curl -sS -w $'\n%{http_code}' "$@") || fail "could not connect to $url. Is it running?"
  code=${out##*$'\n'}
  out=${out%$'\n'*}
  if [[ "$code" -ge 400 ]]; then
    printf '\n\033[31mHTTP %s from %s\033[0m\n' "$code" "$url" >&2
    if ! printf '%s' "$out" | jq . >&2 2>/dev/null; then printf '%s\n' "$out" >&2; fi
    case "$code" in
      401) fail "the API key was not accepted. Check that RELAYSTACK_API_KEY in this terminal is the full rs_dev_... line." ;;
      *) fail "request failed (see the error above)." ;;
    esac
  fi
  printf '%s' "$out"
}

step "0. Checking the API and the development receiver are reachable"
call "$API/healthz" >/dev/null && echo "API:      $API"
call "$RECEIVER/received" >/dev/null && echo "Receiver: $RECEIVER"
echo "Webhooks will be sent to $WEBHOOK_URL"

step "1. Who am I? (the dashboard shows data for this organization and project only)"
call "${auth[@]}" "$API/v1/project" | jq .

step "2. Create a webhook destination (the signing secret is shown only once)"
if ! dest=$(call "${auth[@]}" -H 'Content-Type: application/json' -d "{\"name\":\"dev receiver\",\"url\":\"$WEBHOOK_URL\",\"max_attempts\":5}" \
  "$API/v1/destinations"); then
  [[ "$WEBHOOK_URL" == *dev-receiver* ]] && echo "Hint: dev-receiver only exists inside Docker Compose. If you run the hosts with dotnet run, set WEBHOOK_URL=http://localhost:8090/webhooks" >&2
  exit 1
fi
echo "$dest" | jq 'del(.signing_secret) + {signing_secret: "whsec_…(redacted)"}'
dest_id=$(echo "$dest" | jq -r .id)
secret=$(echo "$dest" | jq -r .signing_secret)
# Tell the development receiver which secret to verify signatures with.
call -H 'Content-Type: application/json' -d "{\"secret\":\"$secret\"}" "$RECEIVER/control/secrets" >/dev/null

step "3. Create an inbox with an explicit schema"
body=$(jq -n --arg d "$dest_id" --slurpfile s "$DIR/data/schema.timesheets.json" \
  '{name:"timesheets", destination_id:$d, schema:$s[0], retention_days:7}')
inbox=$(call "${auth[@]}" -H 'Content-Type: application/json' -d "$body" "$API/v1/inboxes")
echo "$inbox" | jq '{id, name, upload_path, retention_days, columns: (.schema.columns | length)}'
inbox_id=$(echo "$inbox" | jq -r .id)

step "4. Upload a CSV with a few invalid rows (Idempotency-Key makes retries safe)"
file=$(call "${auth[@]}" -H 'Content-Type: text/csv' -H "Idempotency-Key: walkthrough-$(date +%s)" \
  --data-binary @"$DIR/data/timesheets_with_errors.csv" "$API/v1/inboxes/$inbox_id/files?filename=timesheets.csv")
echo "$file" | jq '{id, status, size_bytes, sha256}'
file_id=$(echo "$file" | jq -r .id)

step "5. Wait for processing"
status=""
for _ in $(seq 1 30); do
  status=$(call "${auth[@]}" "$API/v1/files/$file_id" | jq -r .status)
  [[ "$status" == "completed" || "$status" == "failed" ]] && break
  sleep 1
done
[[ "$status" == "completed" || "$status" == "failed" ]] || fail "the file is still '$status' after 30 s. Is the worker running? (docker compose logs worker)"
call "${auth[@]}" "$API/v1/files/$file_id" | jq '{status, total_rows, valid_rows, invalid_rows, error_code}'

step "6. Validation errors"
call "${auth[@]}" "$API/v1/files/$file_id/validation-errors" | jq -c '.data[]'

step "7. Events generated from the file"
call "${auth[@]}" "$API/v1/events?source_id=$file_id" | jq -c '.data[] | {id, type, record_count, body_bytes}'

step "8. Deliveries and attempts"
sleep 3
call "${auth[@]}" "$API/v1/deliveries" | jq -c '.data[] | {id, status, attempt_count, last_status_code}'

step "9. What the receiver saw (signature verified by the receiver)"
call "$RECEIVER/received" | jq -c '.[] | {webhook_id: .webhookId, type, recordCount, signatureValid, duplicate}'

step "10. Force failures, exhaust retries, then replay"
call -H 'Content-Type: application/json' -d '{"permanentStatus":503}' "$RECEIVER/control/mode" >/dev/null
file2=$(call "${auth[@]}" -H 'Content-Type: text/csv' --data-binary @"$DIR/data/timesheets_with_errors.csv" \
  "$API/v1/inboxes/$inbox_id/files?filename=retry-demo.csv" | jq -r .id)
echo "Uploaded $file2; waiting for deliveries to exhaust their retries (dev backoff is short)…"
failed=0
for _ in $(seq 1 90); do
  failed=$(call "${auth[@]}" "$API/v1/deliveries?status=failed" | jq '.data | length')
  [[ "$failed" -ge 2 ]] && break
  sleep 2
done
[[ "$failed" -ge 1 ]] || { call -H 'Content-Type: application/json' -d '{}' "$RECEIVER/control/mode" >/dev/null; fail "no delivery reached 'failed' within 3 minutes."; }
delivery=$(call "${auth[@]}" "$API/v1/deliveries?status=failed" | jq -r '.data[0].id')
call "${auth[@]}" "$API/v1/deliveries/$delivery/attempts" | jq -c '.data[] | {attempt_number, outcome, status_code}'
call -H 'Content-Type: application/json' -d '{}' "$RECEIVER/control/mode" >/dev/null
call "${auth[@]}" -X POST "$API/v1/deliveries/$delivery/replay" | jq '{id, replay_of, status}'

step "11. Metered usage and amplification ratios"
sleep 3
call "${auth[@]}" "$API/v1/usage" | jq '{totals, ratios}'

step "Done"
echo "Inbox $inbox_id now has two files. Open http://localhost:5173 and connect with the same API key to explore them."
