# syntax=docker/dockerfile:1
#
# The Maquettiste editor image (phase2-design.md section 6): static-site-hosting 0.2.0 with the editor site zip, the engine package
# and its dependency closure in a local NuGet feed (offline first build), and an entrypoint that deploys the zip on first boot.
# Build from the repository root with BuildKit (the default builder since Docker 23; the legacy builder drops the /data ownership
# fix below, because /data is a declared volume of the base image):
#
#   docker build -f docker/Dockerfile -t mattjcowan/maquettiste:dev .

# ---------------------------------------------------------------- engine: the package, its closure and the NuGet config
# global.json pins SDK 10.0.109 with rollForward disabled. Microsoft publishes no image tag for that SDK (the 10.0.1xx tags stop at
# 10.0.103), so the stage installs it beside the image's SDK with dotnet-install.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS engine
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
 && bash /tmp/dotnet-install.sh --version 10.0.109 --install-dir /usr/share/dotnet --no-path \
 && rm /tmp/dotnet-install.sh
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY schemas/v1 schemas/v1
COPY src/Maquettiste.Engine src/Maquettiste.Engine
COPY docker/closure docker/closure
# PD24: one package version per engine build, <Directory.Build.props version> plus b<12 hex of the engine's inputs> (as a further
# prerelease segment, or as the prerelease label when the version has none), so an upgraded image
# never compiles the functions against an engine the persistent NuGet cache (/data/nuget) already holds under the same version.
RUN base=$(dotnet msbuild src/Maquettiste.Engine -getProperty:PackageVersion) \
 && sum=$(find src/Maquettiste.Engine schemas/v1 Directory.Build.props Directory.Packages.props -type f \
          -not -path '*/bin/*' -not -path '*/obj/*' | LC_ALL=C sort | xargs sha256sum | sha256sum | cut -c1-12) \
 && case "$base" in *-*) echo "$base.b$sum" ;; *) echo "$base-b$sum" ;; esac > /engine.version \
 && dotnet pack src/Maquettiste.Engine -c Release -o /feed -p:Version="$(cat /engine.version)" \
 && dotnet restore docker/closure/closure.csproj -p:EngineVersion="$(cat /engine.version)" \
      --packages /tmp/pkgs --source /feed --source https://api.nuget.org/v3/index.json \
 && find /tmp/pkgs -name '*.nupkg' -not -path '/tmp/pkgs/maquettiste.engine/*' -exec cp {} /feed/ \; \
 && sh docker/closure/nuget-config.sh /tmp/pkgs /opt/maquettiste/nuget > /feed.NuGet.Config \
 && echo "maquettiste: engine $(cat /engine.version), $(ls /feed/*.nupkg | wc -l) packages in the local feed"

# ---------------------------------------------------------------- cli: the maquettiste command line
# Framework-dependent and portable (no runtime identifier): the runtime image has the .NET 10 runtime, so one managed build serves
# linux/amd64 and linux/arm64 alike. Its own stage, so a CLI change does not touch the editor's layers, and the editor's `COPY . .`
# does not rebuild the CLI. Only what the CLI's project graph reads is copied (the bench is a library of `maquettiste bench`).
FROM engine AS cli
COPY bench/Maquettiste.Bench bench/Maquettiste.Bench
COPY bench/packs bench/packs
COPY packs/sql-ddl packs/sql-ddl
COPY packs/csharp-dapper packs/csharp-dapper
COPY skills/maquettiste-modeling skills/maquettiste-modeling
COPY src/Maquettiste.Cli src/Maquettiste.Cli
RUN dotnet publish src/Maquettiste.Cli -c Release -o /cli -p:UseAppHost=false \
 && rm -f /cli/*.pdb \
 && echo "maquettiste: cli $(du -sh /cli | cut -f1)"

# ---------------------------------------------------------------- editor: the site zip
# With the SPA (src/editor, workstream P2-E) the zip is its build plus _functions/ and _variables.json, packed by its pack-site.mjs;
# without it (a checkout of the functions alone) docker/pack-site.sh packs _functions/, _variables.json and a placeholder page, so
# the API can be tried before the SPA lands. Both stamp the image's engine version into the zip's Directives.cs.
FROM node:22-bookworm-slim AS editor
RUN apt-get update && apt-get install -y --no-install-recommends zip && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY . .
COPY --from=engine /engine.version /engine.version
RUN mkdir -p /out && if [ -f src/editor/package.json ] && [ -f src/editor/scripts/pack-site.mjs ]; then \
      cd src/editor && npm ci && npm run build \
      && node scripts/pack-site.mjs --out /out/site.zip --engine-version "$(cat /engine.version)"; \
    else \
      sh docker/pack-site.sh /out/site.zip "$(cat /engine.version)"; \
    fi

# ---------------------------------------------------------------- runtime
# The runtime-functions image of static-site-hosting 0.2.0 (Ubuntu 24.04, .NET SDK 10.0.401, git, curl and setpriv; runs as app,
# UID 1654; NUGET_PACKAGES=/data/nuget; VOLUME /data).
FROM mattjcowan/static-site-hosting:0.2.0
USER root
# jq edits the host's apikeys.json in the first-boot shim. The mkdir/chown makes the model mount's parents app-owned: Docker creates
# missing parents of a mount point as root, and without this the host cannot write .staging/, releases/ or site.json on a new
# volume (phase2-design.md section 6.1). safe.directory lets git read a bind-mounted repo owned by another UID.
RUN apt-get update && apt-get install -y --no-install-recommends jq && rm -rf /var/lib/apt/lists/* \
 && git config --system --add safe.directory '*' \
 && mkdir -p /data/sites/maquettiste.localhost/data /data/maquettiste/cache && chown -R app:app /data
COPY --from=engine /feed /opt/maquettiste/nuget
COPY --from=engine /engine.version /opt/maquettiste/engine.version
COPY --from=engine --chown=app:app /feed.NuGet.Config /home/app/.nuget/NuGet/NuGet.Config
COPY --from=editor /out/site.zip /opt/maquettiste/site.zip
COPY docker/entrypoint.sh /opt/maquettiste/entrypoint.sh
RUN sha256sum /opt/maquettiste/site.zip | cut -c1-64 > /opt/maquettiste/site.sha256 && chmod 755 /opt/maquettiste/entrypoint.sh
# The CLI, last so a CLI change leaves the layers above cached. `docker run <image> maquettiste ...` runs it instead of the editor
# (the entrypoint execs a given command); docker/maquettiste.sh makes it work under any --user.
COPY --from=cli /cli /opt/maquettiste/cli
COPY --chmod=755 docker/maquettiste.sh /usr/local/bin/maquettiste
# Server GC with dynamic adaptation and tiered PGO off: the settings the incremental generation budget was measured under (PD23).
ENV MAQUETTISTE_MODE=local MAQUETTISTE_REPO_ROOT=/repo MAQUETTISTE_CACHE_DIR=/data/maquettiste/cache \
    DOTNET_gcServer=1 DOTNET_GCDynamicAdaptationMode=0 DOTNET_TieredPGO=0
# Runs as app by default. Started as root with MAQUETTISTE_UID/MAQUETTISTE_GID (compose does), the entrypoint hands /data and
# /home/app to that user and drops to it with the base image's setpriv (util-linux), so a bind-mounted model is written as the host user.
USER app
ENTRYPOINT ["/opt/maquettiste/entrypoint.sh"]
