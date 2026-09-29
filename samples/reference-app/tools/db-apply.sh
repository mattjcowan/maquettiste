#!/bin/sh
# Applies the generated DDL of database main to PostgreSQL 16 (phase2-design.md section 7.2, step 5).
# Two databases: northwind_schema gets db/main/schema.sql, northwind_migrations gets every db/main/migrations/*.sql in
# order; both then get db/main/seed.sql (northwind_schema twice: the seed must be rerunnable). Every script runs with
# ON_ERROR_STOP=1. Afterwards both databases must hold exactly the tables, views and sequences that schema.sql creates,
# and, when NW_EXPECT is set, that count too (for example "209 tables, 1 views, 3 sequences"). Run from anywhere.
#
#   samples/reference-app/tools/db-apply.sh [project dir]    # default: samples/reference-app
#   samples/reference-app/tools/db-apply.sh --down           # remove the container
#
# Without PGHOST: a throwaway container NW_PG_CONTAINER (default nw-pg) of NW_PG_IMAGE (default postgres:16), reused when
# running; no host psql needed. With PGHOST set (CI's service container): the host psql and the usual PG* variables; the
# user must be allowed to create databases.
set -eu
name="${NW_PG_CONTAINER:-nw-pg}"
image="${NW_PG_IMAGE:-postgres:16}"
if [ "${1:-}" = "--down" ]; then docker rm -f "$name" >/dev/null; echo "removed $name"; exit 0; fi
dir="$(cd "${1:-$(dirname "$0")/..}" && pwd)"
db="$dir/db/main"
[ -f "$db/schema.sql" ] || { echo "no $db/schema.sql: run maquettiste generate first" >&2; exit 2; }

if [ -n "${PGHOST:-}" ]; then
  psql() { command psql -v ON_ERROR_STOP=1 -q "$@"; }
else
  if [ -z "$(docker ps -q -f "name=^${name}$")" ]; then
    docker rm -f "$name" >/dev/null 2>&1 || true
    docker run -d --name "$name" -e POSTGRES_PASSWORD=postgres "$image" >/dev/null
  fi
  i=0
  until docker exec "$name" psql -U postgres -tAc 'SELECT 1' >/dev/null 2>&1; do
    i=$((i + 1)); [ $i -lt 60 ] || { echo "PostgreSQL did not start" >&2; exit 1; }; sleep 1
  done
  psql() { docker exec -i "$name" psql -U postgres -v ON_ERROR_STOP=1 -q "$@"; }
fi
run() { echo "  ${2#"$dir"/} -> $1"; psql -d "$1" < "$2"; }
for d in northwind_schema northwind_migrations; do
  psql -d postgres -c "DROP DATABASE IF EXISTS $d" -c "CREATE DATABASE $d"
done

echo "schema.sql"
run northwind_schema "$db/schema.sql"
run northwind_schema "$db/seed.sql"
run northwind_schema "$db/seed.sql"
echo "migrations"
for m in "$db"/migrations/*.sql; do run northwind_migrations "$m"; done
run northwind_migrations "$db/seed.sql"

# Lookup tables of reference types are created by seed.sql (reconciled on every run), not by schema.sql.
want="$(( $(grep -cE '^CREATE TABLE' "$db/schema.sql") + $(grep -cE '^CREATE TABLE IF NOT EXISTS' "$db/seed.sql") )) tables, $(grep -cE '^CREATE (OR REPLACE )?VIEW' "$db/schema.sql") views, $(grep -cE '^CREATE SEQUENCE' "$db/schema.sql") sequences"
if [ -n "${NW_EXPECT:-}" ] && [ "$want" != "$NW_EXPECT" ]; then
  echo "schema.sql creates $want, expected $NW_EXPECT" >&2; exit 1
fi
count="SELECT (SELECT count(*) FROM information_schema.tables WHERE table_schema = 'northwind' AND table_type = 'BASE TABLE')
  || ' tables, ' || (SELECT count(*) FROM information_schema.views WHERE table_schema = 'northwind')
  || ' views, ' || (SELECT count(*) FROM information_schema.sequences WHERE sequence_schema = 'northwind') || ' sequences'"
for d in northwind_schema northwind_migrations; do
  got="$(psql -d "$d" -tAc "$count")"
  echo "$d: $got"
  [ "$got" = "$want" ] || { echo "$d holds $got, schema.sql creates $want" >&2; exit 1; }
done
