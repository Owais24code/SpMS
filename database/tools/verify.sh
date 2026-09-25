#!/usr/bin/env bash
# Builds the schema in a throwaway PostgreSQL 16 cluster and runs the SQL tests.
#
#   database/tools/verify.sh                 # temp cluster (needs PostgreSQL 16 binaries)
#   PGURL=postgres://... verify.sh           # an existing EMPTY database you may drop objects in
#
# Exits non-zero on the first failure. CI runs this against the postgres:16 service.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(dirname "$HERE")"

python3 "$HERE/generate.py" --check

if [[ -z "${PGURL:-}" ]]; then
    BIN="${PG_BIN:-/usr/lib/postgresql/16/bin}"
    DATA="$(mktemp -d)"; SOCK="$(mktemp -d)"
    trap '"$BIN/pg_ctl" -D "$DATA" -m immediate stop >/dev/null 2>&1 || true; rm -rf "$DATA" "$SOCK"' EXIT
    "$BIN/initdb" -D "$DATA" -U postgres -A trust >/dev/null
    "$BIN/pg_ctl" -D "$DATA" -o "-k $SOCK -c listen_addresses='' -c port=5499" -l "$DATA/log" start >/dev/null
    PGURL="postgresql://postgres@/postgres?host=$SOCK&port=5499"
    psql "$PGURL" -qc "CREATE DATABASE spms_verify" >/dev/null
    PGURL="postgresql://postgres@/spms_verify?host=$SOCK&port=5499"
fi

run() { psql "$PGURL" -v ON_ERROR_STOP=1 -q -X "$@"; }

echo "== roles"
run -f "$ROOT/schema/000_roles.sql"
run -c "GRANT CREATE ON DATABASE $(psql "$PGURL" -tAXc 'select current_database()') TO spms_owner"

echo "== schema"
for f in "$ROOT"/schema/[0-9]*_*.sql; do
    [[ "$(basename "$f")" == 000_roles.sql ]] && continue
    echo "   $(basename "$f")"
    run -c "SET ROLE spms_owner" -f "$f"
done

echo "== tests"
fail=0
for t in "$ROOT"/tests/*.sql; do
    if out=$(run -f "$t" 2>&1); then
        echo "   ok   $(basename "$t")"
    else
        echo "   FAIL $(basename "$t")"; echo "$out" | sed 's/^/        /'; fail=1
    fi
done
exit $fail
