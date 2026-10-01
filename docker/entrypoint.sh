#!/bin/sh
# The Maquettiste image's entrypoint (phase2-design.md section 6.2). Runs as app (UID 1654), or as the user picked below:
#   1. checks the host volume is writable and defaults MAQUETTISTE_LOCAL_PEERS to the container's gateway;
#   2. decides whether the bundled site zip must be deployed (a new volume, or an image newer than the last deploy);
#   3. seeds a deploy key into the host's apikeys.json while the host is stopped (host 0.2.0 has no bootstrap key; PD20);
#   4. starts static-site-hosting, forwarding TERM and INT;
#   5. deploys the zip through the host's own API when needed;
#   6. waits for the host and exits with its status.
# Given a command (docker run <image> maquettiste ...), it runs that command instead.
#
# Started as root (compose's user: "0:0", or docker run --user 0:0), with or without a command, it picks the user to run as:
#   - MAQUETTISTE_UID (and MAQUETTISTE_GID) when set and not empty; MAQUETTISTE_UID=0 means stay root;
#   - otherwise the owner of the bind-mounted folder: the model folder for the editor, else the working directory (docker run
#     -w /repo) for a command. Owned by anyone but root (Docker on Linux, Docker Desktop's file sharing), it runs as that owner,
#     so the files it writes are the owner's. Owned by root, it stays root under a rootless runtime (rootless Podman, whose root
#     in the container is the host user outside it); under Docker the editor takes the owner of the repository mount (/repo)
#     instead when that is not root (the model folder was created by Docker itself or by an earlier run as root), and otherwise
#     stays root and says how to set MAQUETTISTE_UID;
#   - with nothing mounted, the image's app user (1654).
# Before it drops to a user other than root it hands /data (bind mounts under it excepted) and /home/app to that user, then
# repairs the files Maquettiste reads and writes in the bind mounts (the model folder, the output roots its settings allow and,
# for a command, the files init writes at the project root): whatever an earlier run as root or as 1654 left owned by another
# user becomes that user's (chown, symbolic links not followed, never the mount point itself), and it logs how many. Then it
# re-runs itself as that user with setpriv.
set -eu
# Files the editor and the CLI write are 0644 and folders 0755 whatever umask the runtime passes; only the deploy key is 0600.
umask 022

log() { printf 'maquettiste: %s\n' "$*"; }
# Lines a command's caller must not read as its output (maquettiste mcp speaks its protocol on stdout) go to stderr.
note() { printf 'maquettiste: %s\n' "$*" >&2; }

model_dir=/data/sites/maquettiste.localhost/data
repo_dir="${MAQUETTISTE_REPO_ROOT:-/repo}"

if [ "$(id -u)" = 0 ]; then
  is_mount() { awk -v p="$1" '$5 == p { found = 1 } END { exit !found }' /proc/self/mountinfo; }
  # A rootless runtime maps the container's root to the host user, so the first line of /proc/self/uid_map maps 0 to a
  # non-zero host uid ("0 1000 1"). Docker and rootful Podman map 0 to 0 ("0 0 4294967295"). The container=podman variable
  # is set by rootful and rootless Podman alike, so it does not tell them apart.
  host_root=$(awk '$1 == 0 { print $2; exit }' /proc/self/uid_map 2>/dev/null || true)
  if [ -n "$host_root" ] && [ "$host_root" != 0 ]; then rootless=1; else rootless=0; fi
  probe=
  project=
  if is_mount "$model_dir"; then probe="$model_dir"
  elif [ "$#" -gt 0 ]; then
    # The nearest mount point at or above the working directory (docker run -v "$PWD:/repo" -w /repo).
    dir="$PWD"
    while [ "$dir" != / ]; do
      if [ -z "$project" ] && [ -f "$dir/.maquettiste/maquettiste.json" ]; then project="$dir"; fi
      if is_mount "$dir"; then probe="$dir"; break; fi
      dir=$(dirname "$dir")
    done
    # The project a command works on: the nearest folder holding .maquettiste/maquettiste.json, as the CLI finds it.
    [ -n "$project" ] || project="$PWD"
  fi
  uid="${MAQUETTISTE_UID:-}"
  gid="${MAQUETTISTE_GID:-}"
  claim=
  hint=
  if [ -n "$uid" ]; then
    from="MAQUETTISTE_UID"
    if [ -z "$gid" ]; then
      if [ "$uid" = 0 ]; then gid=0; elif [ -n "$probe" ]; then gid=$(stat -c '%g' "$probe"); else gid="$uid"; fi
    fi
  elif [ -n "$probe" ]; then
    from="the owner of $probe"
    uid=$(stat -c '%u' "$probe")
    [ -n "$gid" ] || gid=$(stat -c '%g' "$probe")
    if [ "$uid" = 0 ] && [ "$rootless" = 0 ]; then
      # Docker, not a rootless runtime: a root-owned model folder was made by Docker (init did not run) or left by an earlier
      # run as root, and running as root would write root-owned files the host user cannot change.
      if [ "$#" = 0 ] && is_mount "$repo_dir" && [ "$(stat -c '%u' "$repo_dir")" != 0 ]; then
        from="the owner of $repo_dir ($probe is owned by root)"
        uid=$(stat -c '%u' "$repo_dir")
        [ -n "${MAQUETTISTE_GID:-}" ] || gid=$(stat -c '%g' "$repo_dir")
        claim="$probe"
      else
        hint=1
      fi
    fi
  else
    from="the image's app user (nothing mounted to take the owner from)"
    uid=1654
    [ -n "$gid" ] || gid=1654
  fi
  case "$uid$gid" in
    *[!0-9]*) log "MAQUETTISTE_UID and MAQUETTISTE_GID must be numbers (got '$uid' and '$gid')"; exit 1 ;;
  esac
  export HOME=/home/app

  # Repair: every file or folder under a path that is not owned by uid:gid becomes theirs. A mount point keeps its owner (only
  # what is inside it changes) unless it is the one to claim; find -P and chown -h never follow a symbolic link, and -xdev
  # never leaves the mounted file system.
  repair_tree() {
    [ -e "$1" ] || [ -L "$1" ] || return 0
    depth=0
    if is_mount "$1" && [ "$1" != "$claim" ]; then depth=1; fi
    found=$(find -P "$1" -xdev -mindepth "$depth" \( ! -user "$uid" -o ! -group "$gid" \) -printf . \
      -exec chown -h "$uid:$gid" {} + 2>/dev/null | wc -c)
    if [ "$found" -gt 0 ]; then
      left=$(find -P "$1" -xdev -mindepth "$depth" \( ! -user "$uid" -o ! -group "$gid" \) -printf . 2>/dev/null | wc -c)
      if [ "$found" -gt "$left" ]; then note "repaired $((found - left)) files owned by another user under $1"; fi
      if [ "$left" -gt 0 ]; then note "could not repair $left files owned by another user under $1; change their owner on the host"; fi
    fi
  }
  # The output roots of a project's settings (outputs.allow), each under the project root; paths that do not exist, or that
  # resolve outside the root, are skipped.
  repair_outputs() {
    [ -f "$2" ] || return 0
    root=$(realpath -e "$1" 2>/dev/null) || return 0
    jq -r '.outputs.allow[]? | if type == "object" then .path elif type == "string" then . else empty end | strings' "$2" \
      2>/dev/null | while IFS= read -r rel; do
        case "$rel" in ""|/*) continue ;; esac
        path=$(realpath -e "$root/$rel" 2>/dev/null) || continue
        case "$path" in "$root"|"$root"/*) repair_tree "$path" ;; esac
      done || true
  }
  repair() {
    if [ "$#" = 0 ]; then
      [ -z "$probe" ] || repair_tree "$probe"
      if is_mount "$repo_dir"; then repair_outputs "$repo_dir" "$model_dir/maquettiste.json"; fi
    elif [ -n "$probe" ]; then
      repair_tree "$project/.maquettiste"
      repair_outputs "$project" "$project/.maquettiste/maquettiste.json"
      for file in .gitignore .mcp.json .claude/skills/maquettiste-modeling; do repair_tree "$project/$file"; done
    fi
  }

  if [ "$uid" = 0 ]; then
    if [ "$from" = MAQUETTISTE_UID ]; then why="MAQUETTISTE_UID=0"
    elif [ "$rootless" = 1 ]; then why="$from is root, and root in this container is the host user (a rootless runtime such as rootless Podman)"
    else why="$from is root"; fi
    if [ -n "$hint" ] && [ "$#" = 0 ]; then
      note "staying root: $why, so what the editor writes there is root's; set MAQUETTISTE_UID=\$(id -u) and" \
        "MAQUETTISTE_GID=\$(id -g) to run as yourself"
    elif [ -n "$hint" ]; then
      note "staying root: $why, so what this command writes there is root's; pass -e MAQUETTISTE_UID=\$(id -u)" \
        "-e MAQUETTISTE_GID=\$(id -g) to run as yourself"
    elif [ "$#" = 0 ]; then
      log "staying root: $why"
    fi
    # Under a rootless runtime root is the host user, so files another container uid left (1654) are repaired to root.
    if [ "$rootless" = 1 ] && [ "$from" != MAQUETTISTE_UID ]; then repair "$@"; fi
  else
    if [ "$gid" = 0 ] && [ -n "${MAQUETTISTE_GID:-}" ]; then
      log "MAQUETTISTE_GID=0: the editor does not run with the root group; use the host user's group id (id -g)"
      exit 1
    fi
    # The mount points under /data (the bind-mounted model) keep their owner: their files belong to the host.
    set_owner() {
      set --
      for mount in $(awk '$5 ~ "^/data/" { print $5 }' /proc/self/mountinfo); do
        set -- "$@" -path "$mount" -prune -o
      done
      find /data /home/app "$@" \( ! -user "$uid" -o ! -group "$gid" \) -exec chown -h "$uid:$gid" {} +
    }
    # A command (docker run --user 0:0 <image> maquettiste ...) runs as that user too, so nothing it writes is owned by root;
    # the volume is handed over only for the editor or when MAQUETTISTE_UID asks for it.
    if [ "$#" = 0 ] || [ -n "${MAQUETTISTE_UID:-}" ]; then set_owner; fi
    repair "$@"
    [ "$#" -gt 0 ] || log "running as $uid:$gid: $from"
    export USER=app LOGNAME=app
    exec setpriv --reuid="$uid" --regid="$gid" --clear-groups -- "$0" "$@"
  fi
fi

# A command runs instead of the editor host: docker run --rm <image> maquettiste generate (docker/maquettiste.sh). With no
# arguments (compose, docker run <image>) the image starts the editor as before.
if [ "$#" -gt 0 ]; then exec "$@"; fi

site=maquettiste.localhost
opt=/opt/maquettiste
state=/data/maquettiste
sites=/data/sites
site_dir="$sites/$site"
port=8080

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
