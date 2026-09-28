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
  # Stop the previous run first, then delete as root inside a container: folders and files the editor wrote are owned by
  # UID 1654 with mode 755, which the developer cannot remove with a plain rm.
  docker compose -f docker/compose.yaml --project-directory tmp/billing down >/dev/null 2>&1 || true
  docker run --rm --user 0 -v "$PWD/tmp:/t" --entrypoint rm "$MAQUETTISTE_IMAGE" -rf /t/billing
fi
mkdir -p tmp/billing && cp -r tests/fixtures/models/billing/. tmp/billing/
mkdir -p tmp/billing/.maquettiste/templates && cp -r packs/sql-ddl packs/csharp-dapper tmp/billing/.maquettiste/templates/
if [ "$(uname)" = Linux ]; then
  if command -v setfacl >/dev/null 2>&1; then
    # The default entries for the invoking user keep what the container creates editable and deletable by the developer.
    chmod -R u+rwX,go+rX tmp/billing   # the default ACL copies these base bits onto what the container creates
    setfacl -R -m "u:1654:rwX,d:u:1654:rwX,u:$(id -u):rwX,d:u:$(id -u):rwX" tmp/billing
  else
    echo "dev-billing: setfacl is missing (apt install acl); making tmp/billing world-writable instead" >&2
    chmod -R a+rwX tmp/billing
  fi
fi
docker compose -f docker/compose.yaml --project-directory tmp/billing up -d
echo "dev-billing: starting; follow with: docker compose -f docker/compose.yaml --project-directory tmp/billing logs -f"
echo "dev-billing: then open http://maquettiste.localhost:${MAQUETTISTE_PORT:-8080}"
