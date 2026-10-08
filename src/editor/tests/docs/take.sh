#!/bin/sh
# Takes the documentation's screenshots (docs/images/*.png, light and dark) from the image running over the showcase model
# (tests/fixtures/models/showcase), so a UI change is one command away from new pictures:
#
#   npm run docs:screenshots            # from src/editor; builds nothing, uses MAQUETTISTE_IMAGE
#   npm run docs:screenshots -- -g Tags # only the shots whose title matches
#
# Copies the model to tmp/screenshots (gitignored) with the schemas and the example packs, starts the image on it, waits for
# /api/health, runs the Playwright project docs (tests/docs/screenshots.spec.ts), then removes the container. The pictures are
# committed, so the README on GitHub shows them too.
#
# Environment: MAQUETTISTE_IMAGE (default mattjcowan/maquettiste:dev), MAQUETTISTE_PORT (default 8097), SHOTS_KEEP=1 leaves
# the editor running afterwards (stop it with: docker compose -f docker/compose.yaml --project-directory tmp/screenshots down -v).
set -eu
repo="$(cd "$(dirname "$0")/../../../.." && pwd)"
fixture="$repo/tests/fixtures/models/showcase"
dir="$repo/tmp/screenshots"
MAQUETTISTE_IMAGE="${MAQUETTISTE_IMAGE:-mattjcowan/maquettiste:dev}"
MAQUETTISTE_PORT="${MAQUETTISTE_PORT:-8097}"
COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-mq-screenshots}"
MAQUETTISTE_EDITOR_TOKEN="${MAQUETTISTE_EDITOR_TOKEN:-$(node -e 'console.log(require("crypto").randomBytes(24).toString("hex"))')}"
MAQUETTISTE_UID="${MAQUETTISTE_UID:-$(id -u)}"
MAQUETTISTE_GID="${MAQUETTISTE_GID:-$(id -g)}"
export MAQUETTISTE_IMAGE MAQUETTISTE_PORT MAQUETTISTE_EDITOR_TOKEN COMPOSE_PROJECT_NAME MAQUETTISTE_UID MAQUETTISTE_GID
compose() { docker compose -f "$repo/docker/compose.yaml" --project-directory "$dir" "$@"; }

if [ -d "$dir" ]; then
  compose down -v >/dev/null 2>&1 || true
  docker run --rm --user 0 -v "$dir:/p" --entrypoint sh "$MAQUETTISTE_IMAGE" -c 'rm -rf /p/* /p/.[!.]*'
  rmdir "$dir"
fi
mkdir -p "$dir"
cp -r "$fixture/." "$dir/"
rm -f "$dir/README.md"
mkdir -p "$dir/.maquettiste/.schema" "$dir/.maquettiste/templates"
rm -rf "$dir/.maquettiste/.schema/v1" && cp -r "$repo/schemas/v1" "$dir/.maquettiste/.schema/v1"
for p in $(node -e 'const s=require(process.argv[1]); console.log(Object.keys(s.packs||{}).join(" "))' "$dir/.maquettiste/maquettiste.json"); do
  [ -d "$repo/packs/$p" ] && rm -rf "$dir/.maquettiste/templates/$p" && cp -r "$repo/packs/$p" "$dir/.maquettiste/templates/$p"
done
compose up -d --wait
i=0
until curl -fsS -H "Host: maquettiste.localhost:$MAQUETTISTE_PORT" -H "Authorization: Bearer $MAQUETTISTE_EDITOR_TOKEN" \
    "http://127.0.0.1:$MAQUETTISTE_PORT/api/health" 2>/dev/null | grep -q '"status":"ok"'; do
  i=$((i + 1))
  if [ $i -ge 300 ]; then compose logs --tail 60; echo "screenshots: /api/health never reported ok" >&2; exit 1; fi
  sleep 1
done
status=0
(cd "$repo/src/editor" && MAQUETTISTE_URL="http://maquettiste.localhost:$MAQUETTISTE_PORT" npx playwright test --project=docs "$@") || status=$?
[ "${SHOTS_KEEP:-}" = 1 ] || compose down -v >/dev/null 2>&1 || true
exit $status
