#!/usr/bin/env bash
# End-to-end smoke test against a REAL OpenFGA server on PostgreSQL, the way the API will use it:
# stored role tuples + contextual tuples for the loaded appointment row, and a conditional delegation.
#
#   OPENFGA_DATASTORE_URI=postgres://user:pass@host:5432/openfga ./authorization/smoke.sh
#
# Needs the openfga and fga binaries on PATH (or OPENFGA_BIN / FGA_BIN).
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
OPENFGA="${OPENFGA_BIN:-openfga}"; FGA="${FGA_BIN:-fga}"
: "${OPENFGA_DATASTORE_URI:?set OPENFGA_DATASTORE_URI to an empty PostgreSQL database}"
PORT="${OPENFGA_HTTP_PORT:-18080}"

"$OPENFGA" migrate --datastore-engine postgres --datastore-uri "$OPENFGA_DATASTORE_URI" >/dev/null
"$OPENFGA" run --datastore-engine postgres --datastore-uri "$OPENFGA_DATASTORE_URI" \
    --http-addr "127.0.0.1:$PORT" --grpc-addr "127.0.0.1:$((PORT + 1))" --playground-enabled=false \
    --log-level warn >/tmp/openfga-smoke.log 2>&1 &
PID=$!; trap 'kill $PID 2>/dev/null || true' EXIT
for _ in $(seq 1 50); do curl -sf "http://127.0.0.1:$PORT/healthz" >/dev/null && break; sleep 0.2; done

export FGA_API_URL="http://127.0.0.1:$PORT"
STORE=$("$FGA" store create --name spms-smoke --model "$HERE/model.fga" | python3 -c 'import json,sys;print(json.load(sys.stdin)["store"]["id"])')
export FGA_STORE_ID="$STORE"

# Stored tuples: what the outbox worker writes from staff_role_assignment / delegated_authority.
"$FGA" tuple write tenant:t1 tenant property:a1 >/dev/null
"$FGA" tuple write user:desk front_desk property:a1 >/dev/null
"$FGA" tuple write user:g1 owner guest:g1 >/dev/null
"$FGA" tuple write user:assistant delegate_book guest:g1 --condition-name active_delegation \
    --condition-context '{"grant_expires_at":"2099-01-01T00:00:00Z","allowed_property_ids":["a1"]}' >/dev/null

pass=0; fail=0
expect() {  # expect <true|false> <user> <relation> <object> [--contextual-tuple ...] [--context ...]
    local want="$1"; shift
    local got
    got=$("$FGA" query check "$@" | python3 -c 'import json,sys;print(str(json.load(sys.stdin)["allowed"]).lower())')
    if [[ "$got" == "$want" ]]; then pass=$((pass + 1)); else fail=$((fail + 1)); echo "FAIL: check $* -> $got (wanted $want)"; fi
}

CTX=(--contextual-tuple "property:a1 property appointment:ap1"
     --contextual-tuple "guest:g1 guest appointment:ap1"
     --contextual-tuple "user:prov assigned_provider appointment:ap1")

expect true  user:desk      can_read                appointment:ap1 "${CTX[@]}"
expect true  user:prov      can_read_intake_summary appointment:ap1 "${CTX[@]}"
expect false user:prov2     can_read                appointment:ap1 "${CTX[@]}"
expect false user:desk      can_read                appointment:ap1          # no contextual tuples, no access
expect true  user:assistant can_read                appointment:ap1 "${CTX[@]}" \
       --context '{"current_time":"2026-09-25T10:00:00Z","property_id":"a1"}'
expect false user:assistant can_cancel              appointment:ap1 "${CTX[@]}" \
       --context '{"current_time":"2026-09-25T10:00:00Z","property_id":"a1"}'

echo "OpenFGA smoke: $pass passed, $fail failed"
[[ $fail -eq 0 ]]
