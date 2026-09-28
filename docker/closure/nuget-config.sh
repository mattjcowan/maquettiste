#!/bin/sh
# Writes the app user's NuGet.Config for the image (phase2-design.md section 6.1, PD22): two sources, the image's local feed and
# nuget.org, with package source mapping that sends every package of the engine's closure (exact ids) to the local feed and
# everything else to nuget.org. The functions' restore is then offline, while other sites on the same host still reach nuget.org.
#
# usage: nuget-config.sh <global packages folder of the closure restore> <feed path in the image>
set -eu
packages="$1"
feed="$2"
cat <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="maquettiste" value="$feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="maquettiste">
XML
# The global packages folder holds one lowercase folder per package id; mapping patterns are case-insensitive.
for dir in "$packages"/*/; do
  id=$(basename "$dir")
  printf '      <package pattern="%s" />\n' "$id"
done
cat <<XML
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
XML
