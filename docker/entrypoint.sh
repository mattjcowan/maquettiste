#!/bin/sh
# The Maquettiste image's entrypoint (phase2-design.md section 6.2). Runs as app (UID 1654):
#   1. checks the host volume is writable and defaults MAQUETTISTE_LOCAL_PEERS to the container's gateway;
#   2. decides whether the bundled site zip must be deployed (a new volume, or an image newer than the last deploy);
#   3. seeds a deploy key into the host's apikeys.json while the host is stopped (host 0.2.0 has no bootstrap key; PD20);
#   4. starts static-site-hosting, forwarding TERM and INT;
#   5. deploys the zip through the host's own API when needed;
#   6. waits for the host and exits with its status.
# Given a command (docker run <image> maquettiste ...), it runs that command instead.
set -eu

# A command runs instead of the editor host: docker run --rm <image> maquettiste generate (docker/maquettiste.sh). With no
# arguments (compose, docker run <image>) the image starts the editor as before.
if [ "$#" -gt 0 ]; then exec "$@"; fi

site=maquettiste.localhost
opt=/opt/maquettiste
state=/data/maquettiste
sites=/data/sites
site_dir="$sites/$site"
port=8080

log() { printf 'maquettiste: %s\n' "$*"; }

# ---------------------------------------------------------------- 1. volume and local peers
mkdir -p "$state/cache" "$site_dir" 2>/dev/null || true
for dir in "$sites" "$site_dir" "$state"; do
  if [ ! -w "$dir" ]; then
    log "$dir is not writable by uid $(id -u); the host volume was created by an older image. Recreate it with" \
        "'docker compose ... down -v' (this keeps the model, which is on the bind mount, and drops the host's users and keys)"
    exit 1
  fi
done

if [ -z "${MAQUETTISTE_LOCAL_PEERS:-}" ]; then
  # The default route's gateway, little-endian hex in /proc/net/route (010011AC is 172.17.0.1). A browser's request through a
  # 127.0.0.1 port binding reaches the container from this address.
  hex=$(awk '$2 == "00000000" { print $3; exit }' /proc/net/route 2>/dev/null || true)
  if [ -n "$hex" ]; then
    MAQUETTISTE_LOCAL_PEERS=$(printf '%d.%d.%d.%d' "0x$(echo "$hex" | cut -c7-8)" "0x$(echo "$hex" | cut -c5-6)" \
      "0x$(echo "$hex" | cut -c3-4)" "0x$(echo "$hex" | cut -c1-2)")
    export MAQUETTISTE_LOCAL_PEERS
    log "local peers: $MAQUETTISTE_LOCAL_PEERS (the container's gateway)"
  fi
fi

log "WARNING: never delete or rename the site $site in the host's UI or API. Its data folder is the bind-mounted .maquettiste/," \
    "and host 0.2.0 deletes a site folder recursively, model included (recovery: git checkout -- .maquettiste)."

# ---------------------------------------------------------------- 2. does the image's zip need deploying?
needs_deploy=0
if [ "$(cat "$state/deployed.sha256" 2>/dev/null || true)" != "$(cat "$opt/site.sha256")" ]; then
  needs_deploy=1
  # Housekeeping: engine versions of older images in the persistent NuGet cache (PD24 already makes a new engine a new version).
  engine_version=$(cat "$opt/engine.version")
  if [ -d "${NUGET_PACKAGES:-/data/nuget}/maquettiste.engine" ]; then
    for cached in "${NUGET_PACKAGES:-/data/nuget}"/maquettiste.engine/*/; do
      [ -d "$cached" ] || continue
      if [ "$(basename "$cached")" != "$(echo "$engine_version" | tr 'A-Z' 'a-z')" ]; then
        rm -rf "$cached"
      fi
    done
  fi
fi

healthy() { curl -fs -o /dev/null --max-time 2 "http://127.0.0.1:$port/healthz"; }

wait_healthy() {
  i=0
  while ! healthy; do
    i=$((i + 1))
    if [ "$i" -gt 120 ]; then
      return 1
    fi
    sleep 0.5
  done
}

host_pid=
start_host() {
  cd /app
  dotnet /app/StaticSiteHost.dll &
  host_pid=$!
}

stop_host() {
  if [ -n "$host_pid" ]; then
    kill -TERM "$host_pid" 2>/dev/null || true
    wait "$host_pid" 2>/dev/null || true
    host_pid=
  fi
}

# ---------------------------------------------------------------- 3. deploy key (host 0.2.0 shim, PD20)
key_file="$state/deploy.key"
if [ "$needs_deploy" = 1 ] && [ ! -s "$key_file" ]; then
  if [ ! -f /data/config/users.json ]; then
    log "first boot: starting the host once so it creates its administrator"
    start_host
    if ! wait_healthy; then
      log "the host did not become healthy; see its log above"
      stop_host
      exit 1
    fi
    stop_host
  fi

  admin=$(jq -r '[.[] | select(.isBootstrap == true)][0].id // empty' /data/config/users.json)
  if [ -z "$admin" ]; then
    log "no bootstrap administrator in /data/config/users.json; cannot seed a deploy key"
    exit 1
  fi

  alnum() { LC_ALL=C tr -dc 'A-Za-z0-9' < /dev/urandom | head -c "$1"; }
  id=$(alnum 12)
  secret=$(alnum 43)
  hash=$(printf '%s' "$secret" | sha256sum | cut -c1-64)
  now=$(date -u +%Y-%m-%dT%H:%M:%S.0000000+00:00)
  keys=/data/config/apikeys.json
  [ -s "$keys" ] || echo '[]' > "$keys"
  jq --arg id "$id" --arg user "$admin" --arg hash "$hash" --arg display "sshost_${id}_••••••••" --arg now "$now" \
    '. + [{ id: $id, userId: $user, name: "maquettiste-entrypoint", secretHash: $hash, display: $display, createdUtc: $now,
            lastUsedUtc: null, expiresUtc: null, revoked: false }]' "$keys" > "$keys.tmp"
  mv "$keys.tmp" "$keys"
  umask 077
  printf 'sshost_%s_%s' "$id" "$secret" > "$key_file"
  chmod 600 "$key_file"
  umask 022
  log "seeded a deploy key for the host's administrator ($key_file)"
fi

# ---------------------------------------------------------------- 4. the host
trap 'stop_host; exit 143' TERM
trap 'stop_host; exit 130' INT
start_host

# ---------------------------------------------------------------- 5. first-boot or upgrade deploy
if [ "$needs_deploy" = 1 ]; then
  if wait_healthy; then
    log "deploying the editor site to $site (the first functions build restores from the local feed; up to a minute)"
    set -- -sS --max-time 900 -o "$state/deploy.response" -w '%{http_code}' \
      -H "X-Api-Key: $(cat "$key_file")" -H "Content-Type: application/zip" -H "X-Archive-Name: site.zip" \
      --data-binary "@$opt/site.zip"
    if [ -n "${SiteHosting__ManagementHosts__0:-}" ]; then
      set -- "$@" -H "Host: $SiteHosting__ManagementHosts__0"
    fi
    status=$(curl "$@" "http://127.0.0.1:$port/api/v1/sites/$site/deploy" || echo "000")
    if [ "$status" = 200 ]; then
      cp "$opt/site.sha256" "$state/deployed.sha256"
      log "deployed"
    else
      log "the deploy failed (HTTP $status); the next start retries. The host answered:"
      cat "$state/deploy.response" 2>/dev/null || true
      echo
    fi
  else
    log "the host did not become healthy; the deploy is retried at the next start"
  fi
fi

# ---------------------------------------------------------------- 6. wait for the host
set +e
wait "$host_pid"
status=$?
exit "$status"
