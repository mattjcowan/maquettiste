#!/bin/sh
# The gate 2 check (SPEC Section 21, phase2-design.md section 7.2), step by step, the same way locally and in
# .github/workflows/gate2.yml. Run from anywhere; every step prints its wall time.
#
#   samples/reference-app/tools/gate2.sh              # all steps, then down
#   samples/reference-app/tools/gate2.sh <step>...    # prepare seed walk check build ddl down, in that order
#
# prepare  copy samples/reference-app to tmp/gate2 without its model, db/ and build output; start the image over it
#          (tools/empty-editor.sh) and wait for GET /api/health to report ok
# seed     tools/seed.mjs: the whole model through POST /api/model/batch, validate, compare with build-model.mjs
# walk     Playwright project live, src/editor/tests/e2e/gate2.spec.ts: open an entity, add an attribute in the grid, rename
#          a relation in the inspector, plan all packs, open a diff, apply, applyResult.outcome == succeeded
# check    stop the editor (its files are already the caller's; any other owner is named); the walk's edits are in the model and in both packs' output;
#          the editor's apply created src/ReferenceApp.Data/.gitignore holding the csharp-dapper block (createFile);
#          maquettiste validate; generate --check (every root equals the CLI's output); Generated/ equals what the CLI
#          generates into a second copy (diff -r)
# build    dotnet build tmp/gate2/src/ReferenceApp.Data -c Release -warnaserror
# ddl      tools/db-apply.sh on the copy: schema.sql + seed twice, migrations + seed, in two databases, each must hold
#          210 tables, 1 view, 3 sequences (host psql when PGHOST is set, as in CI; else a throwaway postgres:16 container)
# down     remove the editor, its volume and the db-apply container
#
# Environment: MAQUETTISTE_IMAGE (default mattjcowan/maquettiste:dev), MAQUETTISTE_PORT (default 8097),
# MAQUETTISTE_EDITOR_TOKEN (default: made once and kept in tmp/gate2.token), GATE2_DIR (default <repo>/tmp/gate2),
# MAQUETTISTE_CLI (default: dotnet <repo>/src/Maquettiste.Cli/bin/Release/net10.0/Maquettiste.Cli.dll, built when missing).
# Needs docker, node 20+, the .NET 10 SDK; walk needs `npm ci` and `npx playwright install chromium` in src/editor.
set -eu
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../../.." && pwd)"
sample="$repo/samples/reference-app"
dir="${GATE2_DIR:-$repo/tmp/gate2}"
gen="$dir/src/ReferenceApp.Data/Generated"
MAQUETTISTE_IMAGE="${MAQUETTISTE_IMAGE:-mattjcowan/maquettiste:dev}"
MAQUETTISTE_PORT="${MAQUETTISTE_PORT:-8097}"
COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-nw-gate2}"
NW_PG_CONTAINER="${NW_PG_CONTAINER:-nw-gate2-pg}"
if [ -z "${MAQUETTISTE_EDITOR_TOKEN:-}" ]; then
  tokenfile="$(dirname "$dir")/$(basename "$dir").token"
  mkdir -p "$(dirname "$dir")"
  [ -s "$tokenfile" ] || node -e 'console.log(require("crypto").randomBytes(24).toString("hex"))' > "$tokenfile"
  MAQUETTISTE_EDITOR_TOKEN="$(cat "$tokenfile")"
fi
export MAQUETTISTE_IMAGE MAQUETTISTE_PORT MAQUETTISTE_EDITOR_TOKEN COMPOSE_PROJECT_NAME NW_PG_CONTAINER
url="http://127.0.0.1:$MAQUETTISTE_PORT"
compose() { docker compose -f "$repo/docker/compose.yaml" --project-directory "$dir" "$@"; }

cli() {
  if [ -n "${MAQUETTISTE_CLI:-}" ]; then $MAQUETTISTE_CLI "$@"; return; fi
  dll="$repo/src/Maquettiste.Cli/bin/Release/net10.0/Maquettiste.Cli.dll"
  [ -f "$dll" ] || dotnet build -c Release "$repo/src/Maquettiste.Cli" -nologo -v q >/dev/null
  dotnet "$dll" "$@"
}

# The editor runs as the caller (the image's user rule, tools/empty-editor.sh), so every file it wrote is already the caller's.
# A file owned by anyone else means the rule did not apply: name it, then hand it back so the later steps can still run.
reclaim() {
  [ -d "$dir" ] || return 0
  foreign="$(find "$dir" ! -user "$(id -u)" -print 2>/dev/null | head -5)"
  [ -z "$foreign" ] && return 0
  echo "gate2: warning: files not owned by $(id -u) (the image's user rule did not apply), for example:" >&2
  echo "$foreign" | sed 's/^/  /' >&2
  docker run --rm --user 0 -v "$dir:/p" --entrypoint sh "$MAQUETTISTE_IMAGE" -c "chown -R $(id -u):$(id -g) /p"
}

step_prepare() {
  if [ -d "$dir" ]; then
    compose down -v >/dev/null 2>&1 || true
    docker run --rm --user 0 -v "$dir:/p" --entrypoint sh "$MAQUETTISTE_IMAGE" -c 'rm -rf /p/* /p/.[!.]*'
    rmdir "$dir"
  fi
  rm -rf "$dir.cli" "$dir.cli.cache"
  mkdir -p "$dir"
  # The sample with its isolation files, csproj and Custom/ code, but no model, no db/, no build output and no
  # src/ReferenceApp.Data/.gitignore: the editor writes all of those (the last one through the csharp-dapper block unit).
  tar -C "$sample" -cf - --exclude=./.maquettiste --exclude=./db --exclude=./tools/node_modules \
    --exclude=./src/ReferenceApp.Data/Generated --exclude=./src/ReferenceApp.Data/.gitignore \
    --exclude=./src/ReferenceApp.Data/bin --exclude=./src/ReferenceApp.Data/obj . |
    tar -C "$dir" -xf -
  "$here/empty-editor.sh" "$dir"
  i=0
  until curl -fsS -H "Host: maquettiste.localhost:$MAQUETTISTE_PORT" -H "Authorization: Bearer $MAQUETTISTE_EDITOR_TOKEN" \
      "$url/api/health" 2>/dev/null | grep -q '"status":"ok"'; do
    i=$((i + 1))
    if [ $i -ge 300 ]; then compose logs --tail 60; echo "gate2: /api/health never reported ok" >&2; exit 1; fi
    sleep 1
  done
  echo "gate2: editor ready on $url over $dir after ${i}s of functions start-up"
}

step_seed() {
  [ -d "$here/node_modules" ] || (cd "$here" && npm ci --no-audit --no-fund >/dev/null)
  node "$here/seed.mjs" --url "$url" --compare "$dir"
}

step_walk() {
  (cd "$repo/src/editor" && MAQUETTISTE_GATE2=1 MAQUETTISTE_URL="http://maquettiste.localhost:$MAQUETTISTE_PORT" \
    npx playwright test --project=live tests/e2e/gate2.spec.ts)
}

step_check() {
  compose down -v >/dev/null 2>&1 || true
  reclaim
  # The walk's two edits reached the model and both packs' output.
  model="$dir/.maquettiste/model"
  grep -q '"name": "receivingHours"' "$model/entities/warehouse.json" || { echo "gate2: attribute receivingHours is not in the model" >&2; exit 1; }
  test -f "$model/relations/delivery-route-departs-from-depot.json" || { echo "gate2: relation delivery route departs from depot is not in the model" >&2; exit 1; }
  grep -q receiving_hours "$dir/db/main/northwind/tables/warehouses.sql" || { echo "gate2: warehouses.sql lacks receiving_hours" >&2; exit 1; }
  grep -q ReceivingHours "$gen/Inventory/Warehouse.g.cs" || { echo "gate2: Warehouse.g.cs lacks ReceivingHours" >&2; exit 1; }
  # The csharp-dapper gitignore unit (mode block, createFile) created the file with just its block.
  ignore="$dir/src/ReferenceApp.Data/.gitignore"
  printf '# maquettiste: begin csharp-dapper/gitignore\n/Generated/\n# maquettiste: end csharp-dapper/gitignore\n' | cmp -s - "$ignore" ||
    { echo "gate2: $ignore does not hold exactly the csharp-dapper block" >&2; cat "$ignore" >&2 || true; exit 1; }
  cli --repo "$dir" validate
  # Every root (db/main, Generated/ and the .gitignore block): the editor's apply equals the CLI's output.
  cli --repo "$dir" generate --check
  # Generated/ once more, afresh: the CLI generates it into a second copy with a cold cache, compared byte for byte.
  ref="$dir.cli"
  rm -rf "$ref"
  cp -a "$dir" "$ref"
  rm -rf "$ref/src/ReferenceApp.Data/Generated"
  cli --repo "$ref" --cache-dir "$ref.cache" generate --force --quiet
  diff -r -q -x bin -x obj "$gen" "$ref/src/ReferenceApp.Data/Generated"
  cmp "$ignore" "$ref/src/ReferenceApp.Data/.gitignore"
  echo "gate2: Generated/ equals the CLI's output ($(find "$gen" -type f | wc -l) files)"
  rm -rf "$ref" "$ref.cache"
}

step_build() {
  dotnet build "$dir/src/ReferenceApp.Data" -c Release -warnaserror -nologo -v q
}

step_ddl() {
  # db-apply.sh uses the host psql when PGHOST is set (CI's service container), else a throwaway postgres:16 container.
  NW_EXPECT="210 tables, 1 views, 3 sequences" "$here/db-apply.sh" "$dir"
}

step_down() {
  if [ -d "$dir" ]; then compose down -v >/dev/null 2>&1 || true; fi
  docker rm -f "$NW_PG_CONTAINER" >/dev/null 2>&1 || true
  echo "gate2: stopped the editor and the database container"
}

run() {
  start=$(date +%s%N)
  echo "== gate2 $1"
  "step_$1"
  echo "$1 $start $(date +%s%N)" | awk '{ printf "== gate2 %s: ok in %.1f s\n", $1, ($3 - $2) / 1e9 }'
}

if [ $# -eq 0 ]; then
  trap 'step_down >/dev/null 2>&1 || true' EXIT
  set -- prepare seed walk check build ddl
fi
for s in "$@"; do
  case "$s" in
    prepare | seed | walk | check | build | ddl | down) run "$s" ;;
    *) echo "gate2: unknown step '$s' (prepare seed walk check build ddl down)" >&2; exit 2 ;;
  esac
done
