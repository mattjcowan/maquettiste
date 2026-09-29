#!/bin/sh
# Starts the editor image on an empty reference-app project, ready for tools/seed.mjs (phase2-design.md section 7.2).
# The project gets a bare maquettiste.json (seed.mjs saves the real settings through the API), the schemas and the two packs,
# and nothing under model/. Run from anywhere; the project folder must be new or a previous run of this script.
#
#   samples/reference-app/tools/empty-editor.sh <project dir>        # then:
#   MAQUETTISTE_EDITOR_TOKEN=<token> node samples/reference-app/tools/seed.mjs --url http://127.0.0.1:<port> --compare <project dir>
#
# Environment: MAQUETTISTE_IMAGE (default mattjcowan/maquettiste:dev), MAQUETTISTE_PORT (default 8080),
# MAQUETTISTE_EDITOR_TOKEN (required: the container sees the host as a remote peer), COMPOSE_PROJECT_NAME (default nw-seed).
# Stop with: docker compose -f docker/compose.yaml --project-directory <project dir> down -v
set -eu
[ $# -eq 1 ] || { echo "usage: $0 <project dir>" >&2; exit 2; }
: "${MAQUETTISTE_EDITOR_TOKEN:?set MAQUETTISTE_EDITOR_TOKEN (for example \$(openssl rand -hex 24))}"
repo="$(cd "$(dirname "$0")/../../.." && pwd)"
mkdir -p "$1"
project="$(cd "$1" && pwd)"
MAQUETTISTE_IMAGE="${MAQUETTISTE_IMAGE:-mattjcowan/maquettiste:dev}"
COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-nw-seed}"
export MAQUETTISTE_IMAGE COMPOSE_PROJECT_NAME MAQUETTISTE_EDITOR_TOKEN

if [ -d "$project/.maquettiste" ]; then
  # Files the editor wrote belong to UID 1654; stop the previous run and delete them as root inside a container.
  docker compose -f "$repo/docker/compose.yaml" --project-directory "$project" down -v >/dev/null 2>&1 || true
  docker run --rm --user 0 -v "$project:/p" --entrypoint sh "$MAQUETTISTE_IMAGE" -c 'rm -rf /p/.maquettiste /p/db /p/src'
fi
mkdir -p "$project/.maquettiste/model" "$project/.maquettiste/templates" "$project/.maquettiste/.schema"
cp -r "$repo/schemas/v1" "$project/.maquettiste/.schema/v1"
cp -r "$repo/packs/sql-ddl" "$repo/packs/csharp-dapper" "$project/.maquettiste/templates/"
cat > "$project/.maquettiste/maquettiste.json" <<'EOF'
{
  "$schema": ".schema/v1/maquettiste.json",
  "formatVersion": 1,
  "name": "reference-app"
}
EOF
if [ "$(uname)" = Linux ]; then
  if command -v setfacl >/dev/null 2>&1; then
    chmod -R u+rwX,go+rX "$project"
    setfacl -R -m "u:1654:rwX,d:u:1654:rwX,u:$(id -u):rwX,d:u:$(id -u):rwX" "$project"
  else
    chmod -R a+rwX "$project"
  fi
fi
docker compose -f "$repo/docker/compose.yaml" --project-directory "$project" up -d --wait
echo "empty-editor: editor on http://127.0.0.1:${MAQUETTISTE_PORT:-8080} (Host maquettiste.localhost) over $project"
