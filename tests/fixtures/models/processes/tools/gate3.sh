#!/bin/sh
# The gate 3 check (SPEC Section 21, phase-3-design.md section 8), step by step, the same way locally and in
# .github/workflows/gate3.yml. Run from anywhere; every step prints its wall time and the first failure stops the run.
#
#   tests/fixtures/models/processes/tools/gate3.sh              # all steps, then down
#   tests/fixtures/models/processes/tools/gate3.sh <step>...    # prepare walk validate verify generate build test roundtrip bench down
#
# prepare    copy this fixture to tmp/gate3 (model, isolation files, projects and committed companions; no generated output),
#            add the schemas and the example packs its settings name, start the image over it, wait for GET /api/health
# walk       Playwright project live, src/editor/tests/e2e/gate3.spec.ts: open PurchaseApproval, simulate a path in the
#            simulation panel and record it as the scenario RecordedInTheEditor, record one SalesOrderLifecycle path the same
#            way, then plan every pack and apply
# validate   (criterion 1) stop the editor; maquettiste validate: no error and no warning (MQ9005 infos allowed); format --check
# verify     (criteria 2, 3) maquettiste process verify: every scenario passes, the fixture's 14 and the walk's recorded ones
# generate   (criteria 4, 6) generate --check is clean after the editor's apply; generate --force at --jobs 1 and --jobs N
#            into two copies gives byte-identical trees, equal to the editor's output; each process exported twice is
#            byte-identical
# build      (criterion 4) dotnet build src/Processes.slnx -c Release -warnaserror (generated files plus the committed companions)
# test       (criterion 5) dotnet test: one generated test per scenario, all passing, the recorded ones included
# roundtrip  (criterion 7) each process exported to XState and imported --into itself in a scratch copy: byte-identical process
#            file and an identical second export
# bench      (criterion 8) Maquettiste.Bench time-processes: every section 4.5 budget met (no MISS). The budgets assume the
#            machine the gate is signed off on (8 cores or more, native disk) and are binding locally; on a GitHub-hosted
#            runner (RUNNER_ENVIRONMENT=github-hosted, 4 vCPUs) they are advisory, as in bench.yml: a MISS becomes a ::warning::
#            and the step passes. GATE3_BENCH_ADVISORY=1 makes them advisory anywhere, GATE3_BENCH_ADVISORY=0 binding anywhere.
# down       remove the editor and its volume
#
# Environment: MAQUETTISTE_IMAGE (default mattjcowan/maquettiste:dev), MAQUETTISTE_PORT (default 8098),
# MAQUETTISTE_EDITOR_TOKEN (default: made once and kept in tmp/gate3.token), GATE3_DIR (default <repo>/tmp/gate3),
# COMPOSE_PROJECT_NAME (default pr-gate3), GATE3_BENCH_ARGS (extra time-processes options), GATE3_BENCH_ADVISORY (see bench),
# MAQUETTISTE_CLI (default: dotnet <repo>/src/Maquettiste.Cli/bin/Release/net10.0/Maquettiste.Cli.dll, built when missing).
# Needs docker, node 20+, the .NET 10 SDK; walk needs `npm ci` and `npx playwright install chromium` in src/editor.
set -eu
here="$(cd "$(dirname "$0")" && pwd)"
fixture="$(cd "$here/.." && pwd)"
repo="$(cd "$fixture/../../../.." && pwd)"
dir="${GATE3_DIR:-$repo/tmp/gate3}"
MAQUETTISTE_IMAGE="${MAQUETTISTE_IMAGE:-mattjcowan/maquettiste:dev}"
MAQUETTISTE_PORT="${MAQUETTISTE_PORT:-8098}"
COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-pr-gate3}"
if [ -z "${MAQUETTISTE_EDITOR_TOKEN:-}" ]; then
  tokenfile="$(dirname "$dir")/$(basename "$dir").token"
  mkdir -p "$(dirname "$dir")"
  [ -s "$tokenfile" ] || node -e 'console.log(require("crypto").randomBytes(24).toString("hex"))' > "$tokenfile"
  MAQUETTISTE_EDITOR_TOKEN="$(cat "$tokenfile")"
fi
MAQUETTISTE_UID="${MAQUETTISTE_UID:-$(id -u)}"
MAQUETTISTE_GID="${MAQUETTISTE_GID:-$(id -g)}"
export MAQUETTISTE_IMAGE MAQUETTISTE_PORT MAQUETTISTE_EDITOR_TOKEN COMPOSE_PROJECT_NAME MAQUETTISTE_UID MAQUETTISTE_GID
url="http://127.0.0.1:$MAQUETTISTE_PORT"
compose() { docker compose -f "$repo/docker/compose.yaml" --project-directory "$dir" "$@"; }

cli() {
  if [ -n "${MAQUETTISTE_CLI:-}" ]; then $MAQUETTISTE_CLI "$@"; return; fi
  dll="$repo/src/Maquettiste.Cli/bin/Release/net10.0/Maquettiste.Cli.dll"
  [ -f "$dll" ] || dotnet build -c Release "$repo/src/Maquettiste.Cli" -nologo -v q >/dev/null
  dotnet "$dll" "$@"
}

fail() { echo "gate3: $*" >&2; exit 1; }

# The packs the fixture's settings name that exist under packs/ (csharp-dapper, sql-ddl, process-docs when it is there).
packs() { node -e 'const s=require(process.argv[1]); console.log(Object.keys(s.packs||{}).join(" "))' "$fixture/.maquettiste/maquettiste.json"; }

# The editor runs as the caller (the image's user rule), so every file it wrote is already the caller's. A file owned by anyone
# else means the rule did not apply: name it, then hand it back so the later steps can still run.
reclaim() {
  [ -d "$dir" ] || return 0
  foreign="$(find "$dir" ! -user "$(id -u)" -print 2>/dev/null | head -5)"
  [ -z "$foreign" ] && return 0
  echo "gate3: warning: files not owned by $(id -u) (the image's user rule did not apply), for example:" >&2
  echo "$foreign" | sed 's/^/  /' >&2
  docker run --rm --user 0 -v "$dir:/p" --entrypoint sh "$MAQUETTISTE_IMAGE" -c "chown -R $(id -u):$(id -g) /p"
}

scenario_count() { find "$dir/.maquettiste/model/scenarios" -name '*.json' | wc -l | tr -d ' '; }

step_prepare() {
  if [ -d "$dir" ]; then
    compose down -v >/dev/null 2>&1 || true
    docker run --rm --user 0 -v "$dir:/p" --entrypoint sh "$MAQUETTISTE_IMAGE" -c 'rm -rf /p/* /p/.[!.]*'
    rmdir "$dir"
  fi
  rm -rf "$dir".j1 "$dir".jn "$dir".rt "$dir".cache*
  mkdir -p "$dir"
  # The fixture without anything generate writes (the .gitignore'd paths) and without tools/.
  tar -C "$fixture" -cf - --exclude=./tools --exclude=./db --exclude=./src/Processes.Data/Generated --exclude=./src/Processes.Tests/Generated \
    --exclude='./src/Processes.Data/Custom/Processes/*/Endpoints' --exclude='*/bin' --exclude='*/obj' --exclude=./.maquettiste/.cache \
    --exclude=./.maquettiste/manifest --exclude=./.maquettiste/snapshots . | tar -C "$dir" -xf -
  mkdir -p "$dir/.maquettiste/.schema" "$dir/.maquettiste/templates"
  cp -r "$repo/schemas/v1" "$dir/.maquettiste/.schema/v1"
  for p in $(packs); do
    if [ -d "$repo/packs/$p" ]; then cp -r "$repo/packs/$p" "$dir/.maquettiste/templates/$p"; else echo "gate3: warning: pack $p is not under packs/" >&2; fi
  done
  compose up -d --wait
  i=0
  until curl -fsS -H "Host: maquettiste.localhost:$MAQUETTISTE_PORT" -H "Authorization: Bearer $MAQUETTISTE_EDITOR_TOKEN" \
      "$url/api/health" 2>/dev/null | grep -q '"status":"ok"'; do
    i=$((i + 1))
    if [ $i -ge 300 ]; then compose logs --tail 60; fail "/api/health never reported ok"; fi
    sleep 1
  done
  echo "gate3: editor ready on $url over $dir ($(scenario_count) scenarios) after ${i}s of functions start-up"
}

step_walk() {
  (cd "$repo/src/editor" && MAQUETTISTE_GATE3=1 MAQUETTISTE_URL="http://maquettiste.localhost:$MAQUETTISTE_PORT" \
    npx playwright test --project=live tests/e2e/gate3.spec.ts)
}

step_validate() {
  compose down -v >/dev/null 2>&1 || true
  reclaim
  out="$(cli --repo "$dir" validate --format json)" || { echo "$out"; fail "validate reported errors"; }
  echo "$out" | node -e '
    let t = ""; process.stdin.on("data", (d) => (t += d)).on("end", () => {
      const r = JSON.parse(t); const ds = r.diagnostics || [];
      const bad = ds.filter((d) => d.severity === "error" || d.severity === "warning");
      const infos = ds.filter((d) => d.severity === "info");
      for (const d of bad) console.error(`${d.rule} ${d.severity}: ${d.message}`);
      if (infos.some((d) => d.rule !== "MQ9005")) { for (const d of infos) console.error(`${d.rule} info: ${d.message}`); process.exit(1); }
      console.log(`gate3: validate: ${bad.length} errors or warnings, ${infos.length} MQ9005 infos`);
      process.exit(bad.length ? 1 : 0);
    });'
  cli --repo "$dir" format --check
}

step_verify() {
  expected="$(scenario_count)"
  [ "$expected" -ge 16 ] || fail "expected the fixture's 14 scenarios and the walk's 2 recorded ones, found $expected"
  for p in purchase-approval sales-order-lifecycle; do
    test -f "$dir/.maquettiste/model/scenarios/$p/recorded-in-the-editor.json" || fail "the walk did not record a scenario of $p"
  done
  cli --repo "$dir" process verify | tee "$dir.verify.txt"
  grep -q "^$expected scenarios, $expected passed, 0 failed" "$dir.verify.txt" || fail "process verify did not pass all $expected scenarios"
  grep -q "pass  PurchaseApproval/RecordedInTheEditor" "$dir.verify.txt" || fail "RecordedInTheEditor of PurchaseApproval was not verified"
  grep -q "pass  SalesOrderLifecycle/RecordedInTheEditor" "$dir.verify.txt" || fail "RecordedInTheEditor of SalesOrderLifecycle was not verified"
  rm -f "$dir.verify.txt"
}

# The files generation writes (everything but the model folder and build output), for comparing trees.
tree_list() { (cd "$1" && find . -path ./.maquettiste -prune -o -path '*/bin' -prune -o -path '*/obj' -prune -o -type f -print | sort); }

step_generate() {
  # Committed roots (db/, src/Processes.Data/Custom): the editor's apply equals the CLI's output.
  cli --repo "$dir" --cache-dir "$dir.cache" generate --check
  # Determinism: every root generated afresh at --jobs 1 and --jobs N into two copies, then compared with each other and with
  # the editor's output. Companions are not regenerated (they are owned), so the copies keep the committed ones.
  n="$(nproc 2>/dev/null || echo 4)"
  for j in 1 n; do
    copy="$dir.j$j"
    rm -rf "$copy" "$copy.cache"
    tar -C "$dir" -cf - --exclude='*/bin' --exclude='*/obj' . | (mkdir -p "$copy" && tar -C "$copy" -xf -)
    rm -rf "$copy/db" "$copy/src/Processes.Data/Generated" "$copy/src/Processes.Tests/Generated" "$copy"/src/Processes.Data/Custom/Processes/*/Endpoints \
      "$copy/.maquettiste/manifest" "$copy/.maquettiste/snapshots" "$copy/.maquettiste/.cache"
    jobs=$([ "$j" = 1 ] && echo 1 || echo "$n")
    cli --repo "$copy" --cache-dir "$copy.cache" --jobs "$jobs" generate --force --quiet
  done
  tree_list "$dir.j1" > "$dir.files1"
  tree_list "$dir.jn" > "$dir.filesn"
  diff "$dir.files1" "$dir.filesn" || fail "--jobs 1 and --jobs $n wrote different file sets"
  (cd "$dir.j1" && while read -r f; do cmp -s "$f" "$dir.jn/$f" || { echo "$f"; exit 1; }; done < "$dir.files1") || fail "a file differs between --jobs 1 and --jobs $n"
  # The editor's output: the same files (its tree may hold the fixture's other files too).
  (cd "$dir.j1" && while read -r f; do cmp -s "$f" "$dir/$f" || { echo "$f"; exit 1; }; done < "$dir.files1") || fail "the editor's output differs from the CLI's"
  echo "gate3: $(wc -l < "$dir.files1" | tr -d ' ') files byte-identical at --jobs 1, --jobs $n and in the editor's apply"
  rm -rf "$dir.j1" "$dir.jn" "$dir.j1.cache" "$dir.jn.cache" "$dir.files1" "$dir.filesn"
  for p in PurchaseApproval SalesOrderLifecycle; do
    cli --repo "$dir" --cache-dir "$dir.cache" process export "$p" --out "$dir.$p.1.json"
    cli --repo "$dir" --cache-dir "$dir.cache" process export "$p" --out "$dir.$p.2.json"
    cmp "$dir.$p.1.json" "$dir.$p.2.json" || fail "two exports of $p differ"
    rm -f "$dir.$p.1.json" "$dir.$p.2.json"
  done
  echo "gate3: each process exported twice gives identical bytes"
}

step_build() {
  dotnet build "$dir/src/Processes.slnx" -c Release -warnaserror -nologo -v q
}

step_test() {
  expected="$(scenario_count)"
  tests="$(find "$dir/src/Processes.Tests/Generated" -name '*Tests.cs' | wc -l | tr -d ' ')"
  [ "$tests" = "$expected" ] || fail "$expected scenarios but $tests generated test files"
  dotnet test "$dir/src/Processes.slnx" -c Release --no-build -nologo -v q --logger "console;verbosity=normal" | tee "$dir.test.txt"
  for ns in Purchasing Sales; do
    grep -q "Passed Processes.Data.$ns.Tests.RecordedInTheEditorTests.RecordedInTheEditor" "$dir.test.txt" ||
      fail "the test of the scenario recorded in the walk ($ns) did not pass"
  done
  grep -Eq "^ *Total tests: +$expected\$" "$dir.test.txt" && grep -Eq "^ *Passed: +$expected\$" "$dir.test.txt" ||
    fail "not all $expected scenario tests passed"
  echo "gate3: $expected generated scenario tests passed"
  rm -f "$dir.test.txt"
}

step_roundtrip() {
  rt="$dir.rt"
  rm -rf "$rt" "$rt.cache"
  mkdir -p "$rt" && cp -a "$dir/.maquettiste" "$rt/"
  rm -rf "$rt/.maquettiste/.cache"
  for p in PurchaseApproval SalesOrderLifecycle; do
    file="$(grep -l "\"name\": \"$p\"" "$rt"/.maquettiste/model/processes/*.json)"
    cp "$file" "$rt.before.json"
    cli --repo "$rt" --cache-dir "$rt.cache" process export "$p" --out "$rt.$p.1.json"
    cli --repo "$rt" --cache-dir "$rt.cache" process import "$rt.$p.1.json" --into "$p" --apply
    cmp "$rt.before.json" "$file" || fail "the import of $p into itself changed its process file"
    cli --repo "$rt" --cache-dir "$rt.cache" process export "$p" --out "$rt.$p.2.json"
    cmp "$rt.$p.1.json" "$rt.$p.2.json" || fail "the second export of $p differs from the first"
    rm -f "$rt.before.json" "$rt.$p.1.json" "$rt.$p.2.json"
  done
  rm -rf "$rt" "$rt.cache"
  echo "gate3: both processes round-trip through XState byte for byte"
}

step_bench() {
  # shellcheck disable=SC2086
  dotnet run -c Release --project "$repo/bench/Maquettiste.Bench" -- time-processes ${GATE3_BENCH_ARGS:-} | tee "$dir.bench.txt"
  grep -q -e " pass" -e "MISS" "$dir.bench.txt" || fail "the bench reported no budget"
  if grep -q "MISS" "$dir.bench.txt"; then
    if [ "${GATE3_BENCH_ADVISORY:-}" = "1" ] || { [ -z "${GATE3_BENCH_ADVISORY:-}" ] && [ "${RUNNER_ENVIRONMENT:-}" = "github-hosted" ]; }; then
      grep "MISS" "$dir.bench.txt" | while IFS= read -r line; do echo "::warning title=gate3 bench (advisory on this runner)::$line"; done
      echo "gate3: a section 4.5 budget was missed; the budgets are advisory here (a shared runner), so the step passes"
    else
      fail "a section 4.5 budget was missed"
    fi
  fi
  rm -f "$dir.bench.txt"
}

step_down() {
  if [ -d "$dir" ]; then compose down -v >/dev/null 2>&1 || true; fi
  echo "gate3: stopped the editor"
}

run() {
  start=$(date +%s%N)
  echo "== gate3 $1"
  "step_$1"
  echo "$1 $start $(date +%s%N)" | awk '{ printf "== gate3 %s: ok in %.1f s\n", $1, ($3 - $2) / 1e9 }'
}

if [ $# -eq 0 ]; then
  trap 'step_down >/dev/null 2>&1 || true' EXIT
  set -- prepare walk validate verify generate build test roundtrip bench
fi
for s in "$@"; do
  case "$s" in
    prepare | walk | validate | verify | generate | build | test | roundtrip | bench | down) run "$s" ;;
    *) echo "gate3: unknown step '$s' (prepare walk validate verify generate build test roundtrip bench down)" >&2; exit 2 ;;
  esac
done
