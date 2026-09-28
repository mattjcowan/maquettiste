#!/bin/sh
# Packs the editor site zip without the SPA: _functions/ (Directives.cs stamped with the engine version the image's local feed
# holds, PD24), _variables.json and a placeholder index.html. The Dockerfile uses it only when src/editor (workstream P2-E, whose
# scripts/pack-site.mjs packs the full site) is not in the build context. Timestamps are fixed so the same inputs give the same zip.
#
# usage: docker/pack-site.sh <out.zip> <engine version>     (run from the repository root)
set -eu
out="$1"
version="$2"
stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT
mkdir -p "$stage/_functions" "$(dirname "$out")"
cp src/Maquettiste.Functions/_functions/*.cs "$stage/_functions/"
sed -i "s|^#:package Maquettiste.Engine@.*\$|#:package Maquettiste.Engine@$version|" "$stage/_functions/Directives.cs"
grep -q "^#:package Maquettiste.Engine@$version\$" "$stage/_functions/Directives.cs"
cp src/Maquettiste.Functions/_variables.json "$stage/_variables.json"
cp docker/placeholder/index.html "$stage/index.html"
find "$stage" -exec touch -h -d '1980-01-02T00:00:00Z' {} +
rm -f "$out"
(cd "$stage" && LC_ALL=C find . -type f | LC_ALL=C sort | sed 's|^\./||' | TZ=UTC zip -X -q "$out" -@)
echo "maquettiste: packed $out (functions only, engine $version)"
