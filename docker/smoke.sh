#!/bin/sh
# Smoke test of an editor image (phase2-design.md section 3.9): checks its OCI title and version labels (the version is the release
# of /opt/maquettiste/engine.version); starts it with no network over a copy of the billing fixture, waits
# for /api/health "ok" (which proves the first functions build restored offline from the image's feed), checks the host volume's
# ownership, then runs curl checks inside the container: the release in /api/health, the project with the workspace the container was
# given (MAQUETTISTE_WORKSPACE), the index, a save with If-Match, a 409 on the stale hash,
# a plan job and an apply job through to completion; then the CLI in the image (--version, and generate --check started as root
# over a host-owned copy of what the editor wrote with a root-owned .maquettiste/.cache: the run as the host user cannot write
# it, the run started as root repairs it and runs as the host user); the editor again as the host user (started as root with
# MAQUETTISTE_UID and MAQUETTISTE_GID, over a model folder only the host user can write): the model files it writes come out
# owned by the host user; the editor started as root with no variables over that folder with root-owned and 1654-owned strays
# in the model and an output root: it runs as the host user, repairs them, and saves 0644 files; the editor over a root-owned
# model folder in a host-owned repository: it runs as the repository's owner and claims the folder; a command started as root
# with no variables runs as the owner of its mounted working directory; last, rootless Podman simulated: started as root with no
# variables over a model folder and repository owned by root, the editor stays root and its saves succeed. Cleans up after
# itself.
#
# usage: docker/smoke.sh [image]      (run from the repository root; default image mattjcowan/maquettiste:dev)
set -eu
image="${1:-mattjcowan/maquettiste:dev}"
name="mq-smoke-$$"
work=$(mktemp -d)
work2=$(mktemp -d)
work3=$(mktemp -d)
volume="$name-data"

cleanup() {
  docker rm -f "$name" >/dev/null 2>&1 || true
  docker volume rm "$volume" >/dev/null 2>&1 || true
  # Files the container wrote belong to UID 1654 or root: remove the contents as root inside a container first.
  for dir in "$work" "$work2" "$work3"; do
    docker run --rm --user 0:0 -v "$dir:/w" --entrypoint sh "$image" -c 'rm -rf /w/* /w/.[!.]*' >/dev/null 2>&1 || true
  done
  rm -rf "$work" "$work2" "$work3" 2>/dev/null || true
}
# Runs a shell command as root in a throwaway container over a folder (mounted at /w), bypassing the entrypoint.
as_root() { docker run --rm --network none --user 0:0 -v "$1:/w" --entrypoint sh "$image" -c "$2"; }
trap cleanup EXIT
fail() { echo "smoke: FAIL: $*" >&2; docker logs "$name" 2>&1 | tail -40 >&2 || true; exit 1; }
pass() { echo "smoke: ok: $*"; }

label() { docker image inspect -f "{{ index .Config.Labels \"org.opencontainers.image.$1\" }}" "$image"; }
engine_version=$(docker run --rm --network none --entrypoint cat "$image" /opt/maquettiste/engine.version)
release=$(echo "$engine_version" | sed 's/[-.]b[0-9a-f]\{7,\}$//')
[ "$(label title)" = Maquettiste ] || fail "the image's title label is '$(label title)', not Maquettiste"
[ "$(label version)" = "$release" ] || fail "the image's version label is '$(label version)', not the release $release of $engine_version"
pass "labels: title $(label title), version $(label version), revision '$(label revision)'"

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
  -v "$work:/repo" -e MAQUETTISTE_WORKSPACE=smoke "$image" >/dev/null

# Every request goes to the site from inside the container: loopback, Host maquettiste.localhost, so local trust applies.
api() { docker exec "$name" curl -sS -H 'Host: maquettiste.localhost:8080' "$@"; }
jqc() { docker exec -i "$name" jq "$@"; }

wait_health() {
  i=0
  until api -f http://127.0.0.1:8080/api/health 2>/dev/null | grep -q '"status":"ok"'; do
    i=$((i + 1))
    [ "$i" -le 180 ] || fail "/api/health did not report ok within 180 s"
    if docker logs "$name" 2>&1 | grep -q 'the deploy failed'; then fail "the first-boot deploy failed"; fi
    sleep 1
  done
}
wait_health
pass "/api/health is ok: $(api http://127.0.0.1:8080/api/health)"

owners=$(docker exec "$name" stat -c '%U' /data/sites /data/sites/maquettiste.localhost | tr '\n' ' ')
[ "$owners" = "app app " ] || fail "/data/sites and the site folder must be owned by app, not: $owners"
pass "the new host volume's site folders are owned by app"

health=$(api -f http://127.0.0.1:8080/api/health)
[ "$(echo "$health" | jqc -r .productVersion)" = "$release" ] || fail "/api/health productVersion is not $release: $health"
[ "$(echo "$health" | jqc -r .build)" = "$engine_version" ] || fail "/api/health build is not $engine_version: $health"
pass "/api/health names the release $release, build $engine_version"

name_of=$(api -f http://127.0.0.1:8080/api/project | jqc -r .name)
[ "$name_of" = billing ] || fail "/api/project name is '$name_of'"
workspace=$(api -f http://127.0.0.1:8080/api/project | jqc -r .workspace)
[ "$workspace" = smoke ] || fail "/api/project workspace is '$workspace', not the MAQUETTISTE_WORKSPACE it was given"
pass "/api/project (workspace $workspace)"

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
# Started as root with a command and nothing mounted, the entrypoint drops to the image's app user; it refuses the root group,
# and MAQUETTISTE_UID=0 keeps root.
root_cmd=$(docker run --rm --network none --user 0:0 "$image" sh -c 'echo "$(id -u):$(id -g)"') || fail "a command started as root failed"
[ "$root_cmd" = "1654:1654" ] || fail "a command started as root ran as $root_cmd, not 1654:1654"
if docker run --rm --network none --user 0:0 -e MAQUETTISTE_UID=1000 -e MAQUETTISTE_GID=0 "$image" id >/dev/null 2>&1; then
  fail "MAQUETTISTE_GID=0 was accepted"
fi
root_kept=$(docker run --rm --network none --user 0:0 -e MAQUETTISTE_UID=0 "$image" id -u) || fail "MAQUETTISTE_UID=0 failed"
[ "$root_kept" = 0 ] || fail "MAQUETTISTE_UID=0 ran as $root_kept, not root"
pass "a command started as root runs as 1654:1654, MAQUETTISTE_GID=0 is refused, MAQUETTISTE_UID=0 stays root"
cli="$work/cli-copy"
mkdir "$cli" && (cd "$work" && tar cf - --exclude ./cli-copy --exclude ./.maquettiste/.cache .) | (cd "$cli" && tar xf - --no-same-owner --no-same-permissions)
chmod -R u+rwX "$cli"
# An earlier run as root left .maquettiste/.cache root's, closed to everyone else: a run as the host user cannot write its lock.
as_root "$cli" 'mkdir -p /w/.maquettiste/.cache/strays && echo stale > /w/.maquettiste/.cache/strays/stale &&
  chown -R 0:0 /w/.maquettiste/.cache && chmod 700 /w/.maquettiste/.cache'
if [ "$(id -u)" != 0 ]; then
  set +e
  out=$(docker run --rm --network none --user "$(id -u):$(id -g)" -v "$cli:/repo" -w /repo "$image" maquettiste generate --check --progress none 2>&1)
  code=$?
  set -e
  [ "$code" != 0 ] || fail "generate --check as --user $(id -u) wrote into a root-owned .maquettiste/.cache"
  echo "$out" | grep -q 'permission denied.*--user 0:0' || { echo "$out" | tail -5 >&2; fail "the refused run did not suggest --user 0:0"; }
  pass "generate --check as --user $(id -u):$(id -g) over a root-owned .cache exits $code with the --user 0:0 hint"
fi
set +e
out=$(docker run --rm --network none --user 0:0 -v "$cli:/repo" -w /repo "$image" maquettiste generate --check --progress none 2>&1)
code=$?
set -e
[ "$code" = 0 ] || { echo "$out" | tail -20 >&2; fail "maquettiste generate --check started as root over the editor's output exited $code"; }
echo "$out" | grep -q '^maquettiste: repaired [0-9]* files owned by another user under /repo/.maquettiste$' ||
  { echo "$out" | tail -5 >&2; fail "the command started as root did not report the repair"; }
foreign=$(find "$cli" ! -user "$(id -u)" -o ! -group "$(id -g)" | head -3)
[ -z "$foreign" ] || fail "the CLI started as root left files owned by another user: $foreign"
pass "maquettiste generate --check started as --user 0:0: $(echo "$out" | grep -o 'repaired [0-9]* files'), runs as $(id -u):$(id -g), $(echo "$out" | grep -o 'Outcome: [A-Za-z]*' | tail -1), exit 0"
# The editor as the host user (docs/user-guide.md, the Mac): the same volume, now owned by 1654, and a fresh model
# folder with no ACL that only the host user can write. The entrypoint starts as root, hands /data to MAQUETTISTE_UID and runs as it.
host_ids="$(id -u):$(id -g)"
if [ "$(id -u)" = 0 ]; then
  echo "smoke: skipped the MAQUETTISTE_UID run: this shell runs as root, and the editor never does"
else
  docker rm -f "$name" >/dev/null
  cp -r tests/fixtures/models/billing/. "$work2/"
  mkdir -p "$work2/.maquettiste/templates"
  cp -r packs/sql-ddl packs/csharp-dapper "$work2/.maquettiste/templates/"
  chmod -R go-rwx "$work2"
  docker run -d --name "$name" --network none --user 0:0 -e MAQUETTISTE_UID="$(id -u)" -e MAQUETTISTE_GID="$(id -g)" \
    -v "$volume:/data" -v "$work2/.maquettiste:/data/sites/maquettiste.localhost/data" -v "$work2:/repo" "$image" >/dev/null
  wait_health
  runs_as=$(docker exec "$name" stat -c '%u:%g' /proc/1)
  [ "$runs_as" = "$host_ids" ] || fail "with MAQUETTISTE_UID the entrypoint runs as $runs_as, not $host_ids"
  owners=$(docker exec "$name" stat -c '%u:%g' /data/sites /data/sites/maquettiste.localhost /data/maquettiste | sort -u)
  [ "$owners" = "$host_ids" ] || fail "with MAQUETTISTE_UID the host volume is owned by $owners, not $host_ids"
  pass "with MAQUETTISTE_UID=$(id -u) the editor runs as $host_ids and owns its volume"
  etag=$(api -f -D - -o /dev/null "http://127.0.0.1:8080/api/model/elements/$invoice" | tr -d '\r' | awk -F': ' 'tolower($1) == "etag" { print $2 }')
  body=$(api -f "http://127.0.0.1:8080/api/model/elements/$invoice" | jqc -c '.json | .attributes[5].name = "hostRemarks"')
  status=$(echo "$body" | docker exec -i "$name" curl -sS -o /dev/null -w '%{http_code}' -H 'Host: maquettiste.localhost:8080' -X PUT \
    -H 'Content-Type: application/json' -H "If-Match: $etag" --data-binary @- "http://127.0.0.1:8080/api/model/elements/$invoice")
  [ "$status" = 200 ] || fail "the save as the host user answered $status"
  model="$work2/.maquettiste/model/entities/invoice.json"
  grep -q '"hostRemarks"' "$model" || fail "the save as the host user did not reach the bind-mounted model"
  owner=$(stat -c '%u:%g' "$model")
  [ "$owner" = "$host_ids" ] || fail "the model file the editor wrote is owned by $owner, not $host_ids"
  foreign=$(find "$work2" ! -user "$(id -u)" | head -3)
  [ -z "$foreign" ] || fail "the editor run as MAQUETTISTE_UID left files owned by another user: $foreign"
  pass "a save as MAQUETTISTE_UID wrote the model file owned by $host_ids"
  # Strays an earlier run left (root's and 1654's, files and a closed folder) in the model and in an output root (db): started
  # as root with no variables, the editor runs as the owner of the model folder and makes them that owner's again.
  docker rm -f "$name" >/dev/null
  mkdir -p "$work2/.maquettiste/.cache"
  as_root "$work2" 'chown 0:0 /w/.maquettiste/model/entities/invoice.json && chown 1654:1654 /w/.maquettiste/model/entities/customer.json &&
    mkdir -p /w/.maquettiste/.cache/strays /w/db && echo stale > /w/.maquettiste/.cache/strays/stale && chmod 700 /w/.maquettiste/.cache/strays &&
    chown -R 0:0 /w/.maquettiste/.cache/strays && echo stale > /w/db/stray.sql && chown 0:0 /w/db /w/db/stray.sql'
  docker run -d --name "$name" --network none --user 0:0 \
    -v "$volume:/data" -v "$work2/.maquettiste:/data/sites/maquettiste.localhost/data" -v "$work2:/repo" "$image" >/dev/null
  wait_health
  runs_as=$(docker exec "$name" stat -c '%u:%g' /proc/1)
  [ "$runs_as" = "$host_ids" ] || fail "started as root over the host user's model folder with strays, the entrypoint runs as $runs_as"
  repaired=$(docker logs "$name" 2>&1 | grep '^maquettiste: repaired') || fail "the entrypoint did not log a repair"
  echo "$repaired" | grep -q '^maquettiste: repaired 4 files owned by another user under /data/sites/maquettiste.localhost/data$' ||
    fail "the model repair line is not the expected one: $repaired"
  echo "$repaired" | grep -q '^maquettiste: repaired 2 files owned by another user under /repo/db$' ||
    fail "the output root repair line is not the expected one: $repaired"
  foreign=$(find "$work2" ! -user "$(id -u)" -o ! -group "$(id -g)" | head -3)
  [ -z "$foreign" ] || fail "the editor left strays owned by another user: $foreign"
  pass "started as --user 0:0 the editor runs as $host_ids and repaired the strays: $(echo "$repaired" | tr '\n' ';')"
  etag=$(api -f -D - -o /dev/null "http://127.0.0.1:8080/api/model/elements/$invoice" | tr -d '\r' | awk -F': ' 'tolower($1) == "etag" { print $2 }')
  body=$(api -f "http://127.0.0.1:8080/api/model/elements/$invoice" | jqc -c '.json | .attributes[5].name = "repairedRemarks"')
  status=$(echo "$body" | docker exec -i "$name" curl -sS -o /dev/null -w '%{http_code}' -H 'Host: maquettiste.localhost:8080' -X PUT \
    -H 'Content-Type: application/json' -H "If-Match: $etag" --data-binary @- "http://127.0.0.1:8080/api/model/elements/$invoice")
  [ "$status" = 200 ] || fail "the save after the repair answered $status"
  grep -q '"repairedRemarks"' "$model" || fail "the save after the repair did not reach the bind-mounted model"
  mode=$(stat -c '%a %u:%g' "$model")
  [ "$mode" = "644 $host_ids" ] || fail "the saved model file is $mode, not 644 $host_ids"
  pass "a save after the repair wrote the model file 0644, owned by $host_ids"
  # A model folder root owns (Docker made it, or a run as root did) in a repository the host user owns: the editor takes the
  # repository's owner and claims the folder itself as well.
  docker rm -f "$name" >/dev/null
  as_root "$work2" 'chown 0:0 /w/.maquettiste /w/.maquettiste/maquettiste.json'
  docker run -d --name "$name" --network none --user 0:0 \
    -v "$volume:/data" -v "$work2/.maquettiste:/data/sites/maquettiste.localhost/data" -v "$work2:/repo" "$image" >/dev/null
  wait_health
  runs_as=$(docker exec "$name" stat -c '%u:%g' /proc/1)
  [ "$runs_as" = "$host_ids" ] || fail "over a root-owned model folder in a host-owned repository the entrypoint runs as $runs_as"
  docker logs "$name" 2>&1 | grep -q "running as $host_ids: the owner of /repo (/data/sites/maquettiste.localhost/data is owned by root)" ||
    fail "the entrypoint did not log that it took the repository's owner"
  docker logs "$name" 2>&1 | grep -q '^maquettiste: repaired 2 files owned by another user under /data/sites/maquettiste.localhost/data$' ||
    fail "the entrypoint did not repair the root-owned model folder: $(docker logs "$name" 2>&1 | grep repaired)"
  owner=$(stat -c '%u:%g' "$work2/.maquettiste")
  [ "$owner" = "$host_ids" ] || fail "the root-owned model folder is still owned by $owner"
  pass "over a root-owned model folder in a host-owned repository the editor runs as $host_ids and claims the folder"
  # A command started as root with no variables runs as the owner of the mounted working directory (the CLI form).
  cmd_ids=$(docker run --rm --network none --user 0:0 -v "$work2:/repo" -w /repo "$image" sh -c 'echo "$(id -u):$(id -g)"') ||
    fail "a command started as root over the host user's folder failed"
  [ "$cmd_ids" = "$host_ids" ] || fail "a command started as root over a folder owned by $host_ids ran as $cmd_ids"
  pass "a command started as root with no variables runs as the mount's owner, $host_ids"
fi

# Rootless Podman, simulated: its root is the host user and every other uid is a stranger to the bind mounts, which show as
# owned by root. Started as root with no variables over a root-owned model folder and repository, the editor stays root and
# can write them. Docker maps root to root, so here the entrypoint also says how to run as the host user instead.
docker rm -f "$name" >/dev/null
cp -r tests/fixtures/models/billing/. "$work3/"
mkdir -p "$work3/.maquettiste/templates"
cp -r packs/sql-ddl packs/csharp-dapper "$work3/.maquettiste/templates/"
docker run --rm --network none --user 0:0 -v "$work3:/w" --entrypoint chown "$image" -R 0:0 /w
docker run -d --name "$name" --network none --user 0:0 \
  -v "$volume:/data" -v "$work3/.maquettiste:/data/sites/maquettiste.localhost/data" -v "$work3:/repo" "$image" >/dev/null
wait_health
host_as=$(docker exec "$name" sh -c 'for p in /proc/[0-9]*; do if tr "\0" " " < "$p/cmdline" 2>/dev/null | grep -q StaticSiteHost.dll; then stat -c "%u:%g" "$p"; fi; done' | sort -u)
[ "$host_as" = "0:0" ] || fail "over a root-owned model folder the host process runs as '$host_as', not 0:0"
docker logs "$name" 2>&1 | grep -q 'staying root' || fail "the entrypoint did not log that it stays root"
docker logs "$name" 2>&1 | grep -q 'staying root.*MAQUETTISTE_UID=\$(id -u)' || fail "the entrypoint did not say how to set MAQUETTISTE_UID"
pass "over a root-owned model folder with no variables the host process runs as root"
etag=$(api -f -D - -o /dev/null "http://127.0.0.1:8080/api/model/elements/$invoice" | tr -d '\r' | awk -F': ' 'tolower($1) == "etag" { print $2 }')
body=$(api -f "http://127.0.0.1:8080/api/model/elements/$invoice" | jqc -c '.json | .attributes[5].name = "rootRemarks"')
status=$(echo "$body" | docker exec -i "$name" curl -sS -o /dev/null -w '%{http_code}' -H 'Host: maquettiste.localhost:8080' -X PUT \
  -H 'Content-Type: application/json' -H "If-Match: $etag" --data-binary @- "http://127.0.0.1:8080/api/model/elements/$invoice")
[ "$status" = 200 ] || fail "the save over the root-owned model folder answered $status"
# The copy is root's now (mktemp's 0700 keeps this shell out), so read the file and its owner through a container.
written=$(docker run --rm --network none --user 0:0 -v "$work3:/w" --entrypoint sh "$image" -c \
  'stat -c %u:%g /w/.maquettiste/model/entities/invoice.json; grep -c "\"rootRemarks\"" /w/.maquettiste/model/entities/invoice.json')
[ "$written" = "0:0
1" ] || fail "the save as root did not reach the bind-mounted model as root's file: $written"
pass "a save over the root-owned model folder answered 200 and wrote the model file as root"
echo "smoke: all checks passed"
