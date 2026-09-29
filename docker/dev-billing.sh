#!/bin/sh
# Starts the editor image on a copy of the billing fixture in tmp/billing (gitignored), with the sql-ddl and csharp-dapper packs
# installed (phase2-design.md section 6.4). Run from the repository root after building the image:
#
#   docker build -f docker/Dockerfile -t mattjcowan/maquettiste:dev .
#   docker/dev-billing.sh
#
# Stop with: docker compose -f docker/compose.yaml --project-directory tmp/billing down      (add -v to drop the host volume)
set -eu
cd "$(dirname "$0")/.."
MAQUETTISTE_IMAGE="${MAQUETTISTE_IMAGE:-mattjcowan/maquettiste:dev}"
export MAQUETTISTE_IMAGE
if [ -d tmp/billing ]; then
  # Stop the previous run first, then delete as root inside a container: an older run without MAQUETTISTE_UID left folders
  # and files owned by UID 1654 with mode 755, which the developer cannot remove with a plain rm.
  docker compose -f docker/compose.yaml --project-directory tmp/billing down >/dev/null 2>&1 || true
  docker run --rm --user 0 -v "$PWD/tmp:/t" --entrypoint rm "$MAQUETTISTE_IMAGE" -rf /t/billing
fi
mkdir -p tmp/billing && cp -r tests/fixtures/models/billing/. tmp/billing/
mkdir -p tmp/billing/.maquettiste/templates && cp -r packs/sql-ddl packs/csharp-dapper tmp/billing/.maquettiste/templates/
# The editor runs as the invoking user (docker/README.md "File ownership"), so what it writes stays editable here.
if [ "$(id -u)" != 0 ]; then
  MAQUETTISTE_UID="${MAQUETTISTE_UID:-$(id -u)}" MAQUETTISTE_GID="${MAQUETTISTE_GID:-$(id -g)}"
  export MAQUETTISTE_UID MAQUETTISTE_GID
fi
docker compose -f docker/compose.yaml --project-directory tmp/billing up -d
echo "dev-billing: starting; follow with: docker compose -f docker/compose.yaml --project-directory tmp/billing logs -f"
echo "dev-billing: then open http://maquettiste.localhost:${MAQUETTISTE_PORT:-8080}"
