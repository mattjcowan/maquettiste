#!/bin/sh
# /usr/local/bin/maquettiste in the image: the framework-dependent CLI in /opt/maquettiste/cli on the image's .NET runtime.
#
#   docker run --rm --user "$(id -u):$(id -g)" -v "$PWD:/repo" -w /repo mattjcowan/maquettiste:<tag> maquettiste generate
#
# It must work under any --user: a UID without a passwd entry gets HOME=/ and cannot write /data (the image's app-owned volume),
# so the defaults that point at folders this UID cannot write fall back to a per-run folder under /tmp. Running an app through the
# dotnet muxer writes nothing to HOME (no SDK first-run step); HOME and DOTNET_CLI_HOME are still pointed at a writable folder.
set -eu
writable() { mkdir -p "$1" 2>/dev/null && [ -w "$1" ]; }
tmp="${TMPDIR:-/tmp}"
if [ -z "${HOME:-}" ] || ! [ -w "$HOME" ]; then export HOME="$tmp"; fi
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$HOME}" DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
if [ -n "${MAQUETTISTE_CACHE_DIR:-}" ] && ! writable "$MAQUETTISTE_CACHE_DIR"; then
  export MAQUETTISTE_CACHE_DIR="$tmp/maquettiste-cache"
fi
exec dotnet /opt/maquettiste/cli/Maquettiste.Cli.dll "$@"
