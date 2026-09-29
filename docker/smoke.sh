#!/bin/sh
# Smoke test of an editor image (phase2-design.md section 3.9): starts it with no network over a copy of the billing fixture, waits
# for /api/health "ok" (which proves the first functions build restored offline from the image's feed), checks the host volume's
# ownership, then runs curl checks inside the container: the project, the index, a save with If-Match, a 409 on the stale hash,
# a plan job and an apply job through to completion; then the CLI in the image (--version, and generate --check as the host user
# over a copy of what the editor wrote). Cleans up after itself.
#
# usage: docker/smoke.sh [image]      (run from the repository root; default image mattjcowan/maquettiste:dev)
set -eu
image="${1:-mattjcowan/maquettiste:dev}"
name="mq-smoke-$$"
work=$(mktemp -d)
volume="$name-data"

cleanup() {
  docker rm -f "$name" >/dev/null 2>&1 || true
  docker volume rm "$volume" >/dev/null 2>&1 || true
  # Files the container wrote belong to UID 1654: remove them as that user first.
  docker run --rm -v "$work:/w" --entrypoint sh "$image" -c 'rm -rf /w/* /w/.[!.]*' >/dev/null 2>&1 || true
  rm -rf "$work" 2>/dev/null || true
}
trap cleanup EXIT
fail() { echo "smoke: FAIL: $*" >&2; docker logs "$name" 2>&1 | tail -40 >&2 || true; exit 1; }
pass() { echo "smoke: ok: $*"; }

cp -r tests/fixtures/models/billing/. "$work/"
mkdir -p "$work/.maquettiste/templates"
cp -r packs/sql-ddl packs/csharp-dapper "$work/.maquettiste/templates/"
if command -v setfacl >/dev/null 2>&1; then
  # The default ACL copies the folder's own base bits onto everything the container creates, so open them first (mktemp
  # makes 0700 folders), and give the host user a default entry so files the container writes stay editable from here.
  chmod -R u+rwX,go+rX "$work"
  setfacl -R -m u:1654:rwX -m d:u:1654:rwX -m "u:$(id -u):rwX" -m "d:u:$(id -u):rwX" "$work"
else
  chmod -R a+rwX "$work"
fi

docker volume create "$volume" >/dev/null
docker run -d --name "$name" --network none -v "$volume:/data" -v "$work/.maquettiste:/data/sites/maquettiste.localhost/data" \
  -v "$work:/repo" "$image" >/dev/null

# Every request goes to the site from inside the container: loopback, Host maquettiste.localhost, so local trust applies.
api() { docker exec "$name" curl -sS -H 'Host: maquettiste.localhost:8080' "$@"; }
jqc() { docker exec -i "$name" jq "$@"; }

i=0
until api -f http://127.0.0.1:8080/api/health 2>/dev/null | grep -q '"status":"ok"'; do
  i=$((i + 1))
  [ "$i" -le 180 ] || fail "/api/health did not report ok within 180 s"
  if docker logs "$name" 2>&1 | grep -q 'the deploy failed'; then fail "the first-boot deploy failed"; fi
  sleep 1
done
pass "/api/health is ok: $(api http://127.0.0.1:8080/api/health)"

owners=$(docker exec "$name" stat -c '%U' /data/sites /data/sites/maquettiste.localhost | tr '\n' ' ')
[ "$owners" = "app app " ] || fail "/data/sites and the site folder must be owned by app, not: $owners"
pass "the new host volume's site folders are owned by app"

name_of=$(api -f http://127.0.0.1:8080/api/project | jqc -r .name)
[ "$name_of" = billing ] || fail "/api/project name is '$name_of'"
pass "/api/project"

count=$(api -f http://127.0.0.1:8080/api/model/index | jqc 'length')
[ "$count" -gt 20 ] || fail "/api/model/index has $count elements"
pass "/api/model/index lists $count elements"

invoice=01J92P0V0FJ23CGSNKM7P1W5V7
etag=$(api -f -D - -o /dev/null "http://127.0.0.1:8080/api/model/elements/$invoice" | tr -d '\r' | awk -F': ' 'tolower($1) == "etag" { print $2 }')
[ -n "$etag" ] || fail "no ETag on the element"
body=$(api -f "http://127.0.0.1:8080/api/model/elements/$invoice" | jqc -c '.json | .attributes[5].name = "remarks"')
status=$(echo "$body" | docker exec -i "$name" curl -sS -o /dev/null -w '%{http_code}' -H 'Host: maquettiste.localhost:8080' -X PUT \
  -H 'Content-Type: application/json' -H "If-Match: $etag" --data-binary @- "http://127.0.0.1:8080/api/model/elements/$invoice")
[ "$status" = 200 ] || fail "the save answered $status"
grep -q '"remarks"' "$work/.maquettiste/model/entities/invoice.json" || fail "the save did not reach the bind-mounted model"
pass "a save with If-Match answered 200 and wrote the model file"
status=$(echo "$body" | docker exec -i "$name" curl -sS -o /dev/null -w '%{http_code}' -H 'Host: maquettiste.localhost:8080' -X PUT \
  -H 'Content-Type: application/json' -H "If-Match: $etag" --data-binary @- "http://127.0.0.1:8080/api/model/elements/$invoice")
[ "$status" = 409 ] || fail "the stale save answered $status, not 409"
pass "the same save with the old hash answered 409"

# A handler failure is the contract's problem, not the host's "500 text/plain": make the entities folder read-only and save.
etag=$(api -f -D - -o /dev/null "http://127.0.0.1:8080/api/model/elements/$invoice" | tr -d '\r' | awk -F': ' 'tolower($1) == "etag" { print $2 }')
changed=$(echo "$body" | jqc -c '.attributes[5].name = "remarksAgain"')
chmod a-w "$work/.maquettiste/model/entities"
answer=$(echo "$changed" | docker exec -i "$name" curl -sS -w '\n%{http_code} %{content_type}' -H 'Host: maquettiste.localhost:8080' -X PUT \
  -H 'Content-Type: application/json' -H "If-Match: $etag" --data-binary @- "http://127.0.0.1:8080/api/model/elements/$invoice")
chmod a+rwX "$work/.maquettiste/model/entities"
echo "$answer" | tail -1 | grep -q '^503 application/problem+json' || fail "a failed write answered: $(echo "$answer" | tail -1)"
echo "$answer" | head -1 | grep -q '"code":"model-unavailable"' || fail "a failed write is not model-unavailable: $(echo "$answer" | head -c 300)"
pass "a write the model folder refuses answered 503 model-unavailable as a problem"

# An element id that is not a ULID (the 300 KB presence payload of the review) is refused before it is stored.
status=$(docker exec "$name" curl -sS -o /dev/null -w '%{http_code}' -H 'Host: maquettiste.localhost:8080' -X PUT \
  -H 'Content-Type: application/json' --data '{"connectionId":"none","elementId":"not-a-ulid"}' http://127.0.0.1:8080/api/presence)
[ "$status" = 400 ] || fail "a presence report with a bad elementId answered $status, not 400"
pass "a presence report with a bad elementId answered 400"

wait_job() {
  j=0
  while :; do
    state=$(api -f "http://127.0.0.1:8080/api/jobs/$1" | jqc -r .state)
    case "$state" in succeeded|failed|cancelled) break ;; esac
    j=$((j + 1))
    [ "$j" -le 120 ] || fail "job $1 is still $state after 120 s"
    sleep 1
  done
}

job=$(api -f -X POST -H 'Content-Type: application/json' --data '{}' http://127.0.0.1:8080/api/generate/plan | jqc -r .id)
wait_job "$job"
outcome=$(api -f "http://127.0.0.1:8080/api/jobs/$job" | jqc -r .planResult.outcome)
[ "$outcome" = succeeded ] || fail "the plan job's outcome is $outcome"
plan=$(api -f "http://127.0.0.1:8080/api/jobs/$job" | jqc -r .planResult.plan.id)
changes=$(api -f "http://127.0.0.1:8080/api/generate/plan/$plan" | jqc '.changes | length')
pass "plan job $job succeeded: plan $plan with $changes changes"

job=$(api -f -X POST -H 'Content-Type: application/json' --data "{\"planId\":\"$plan\"}" http://127.0.0.1:8080/api/generate/apply | jqc -r .id)
wait_job "$job"
outcome=$(api -f "http://127.0.0.1:8080/api/jobs/$job" | jqc -r .applyResult.outcome)
[ "$outcome" = succeeded ] || fail "the apply job's outcome is $outcome"
written=$(api -f "http://127.0.0.1:8080/api/jobs/$job" | jqc -r '.applyResult.result.filesWritten')
rendered=$(api -f "http://127.0.0.1:8080/api/jobs/$job" | jqc -r '.applyResult.result.unitsRendered')
[ "${written:-0}" -gt 0 ] || fail "the apply job succeeded but reports $written files written ($rendered units rendered)"
# The container writes through the bind mount; give a slow mount a few seconds before deciding the host cannot see the file.
k=0
while [ ! -f "$work/db/main/billing/tables/customers.sql" ] && [ "$k" -lt 10 ]; do k=$((k + 1)); sleep 1; done
if [ ! -f "$work/db/main/billing/tables/customers.sql" ]; then
  echo "smoke: the apply wrote $written files ($rendered units), but the host does not see db/main/billing/tables/customers.sql" >&2
  echo "smoke: container view of /repo/db:" >&2; docker exec "$name" sh -c 'ls -laR /repo/db 2>&1 | head -30' >&2 || true
  echo "smoke: host view of $work/db:" >&2; ls -laR "$work/db" 2>&1 | head -30 >&2 || true
  echo "smoke: host user $(id) ; work dir: $(ls -ld "$work")" >&2
  fail "the apply did not write db/main/billing/tables/customers.sql where the host can see it"
fi
pass "apply job $job succeeded and wrote $written files into the repository ($rendered units rendered)"

# The CLI in the image (docker/maquettiste.sh), run as the host user the way docs/user-guide.md documents it, over a host-owned copy
# of the repository the editor just wrote: --check must find the editor's output byte for byte what the CLI generates (exit 0).
version=$(docker run --rm --network none "$image" maquettiste --version) || fail "maquettiste --version failed in the image"
pass "maquettiste --version in the image prints $version"
cli="$work/cli-copy"
mkdir "$cli" && (cd "$work" && tar cf - --exclude ./cli-copy --exclude ./.maquettiste/.cache .) | (cd "$cli" && tar xf - --no-same-owner --no-same-permissions)
chmod -R u+rwX "$cli"
set +e
out=$(docker run --rm --network none --user "$(id -u):$(id -g)" -v "$cli:/repo" -w /repo "$image" maquettiste generate --check --progress none 2>&1)
code=$?
set -e
[ "$code" = 0 ] || { echo "$out" | tail -20 >&2; fail "maquettiste generate --check over the editor's output exited $code"; }
foreign=$(find "$cli" ! -user "$(id -u)" | head -3)
[ -z "$foreign" ] || fail "the CLI run as --user $(id -u) left files owned by another user: $foreign"
pass "maquettiste generate --check as --user $(id -u):$(id -g): $(echo "$out" | grep -o 'Outcome: [A-Za-z]*' | tail -1), exit 0"
echo "smoke: all checks passed"
