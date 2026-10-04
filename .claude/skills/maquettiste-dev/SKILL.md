---
name: maquettiste-dev
description: Working on the Maquettiste repository itself (engine, CLI, packs, bench, editor, functions, Docker). Use for any change in this repo.
---

# Maquettiste development

The spec is SPEC.md; the binding designs are docs/engineering/engine-design.md (phase 1) and
docs/engineering/phase2-design.md (phase 2), with docs/api/openapi.yaml as the editor API contract. When they
disagree with the spec, the design wins and docs/engineering/spec-errata.md records why.

## Layout

- src/Maquettiste.Engine: the NuGet engine (model, loader, validator, resolver, sandbox, renderer, planner, writer,
  post-processing, schema diff). Each folder's README.md records its owner's deviations and performance notes.
- src/Maquettiste.Cli: the dotnet tool. src/Maquettiste.Functions: the editor site's C# functions for
  static-site-hosting 0.4.0 (27 files, under the host's 50-file and 4 MB limits). src/editor: the React SPA.
- packs/: example template packs (sql-ddl, csharp-dapper). bench/: synthetic generator and harness. docker/:
  image, compose, smoke test. tests/: xunit projects plus tests/fixtures (the billing model is the shared fixture).

## Build and test

- `dotnet build -c Release` (warnings are errors) and `dotnet test -c Release --no-build`. The packs' database
  tests skip without MAQUETTISTE_TEST_POSTGRES_CONTAINER / MAQUETTISTE_TEST_SQLSERVER_CONTAINER.
- Editor: `cd src/editor && npm ci && npm test && npm run build && CI=1 npm run e2e` (Playwright, chromium).
- Bench (gate 1): `dotnet run -c Release --project bench/Maquettiste.Bench -- --jobs 8 --no-example-packs`.
- Image: `docker build -f docker/Dockerfile -t mattjcowan/maquettiste:dev .` then `sh docker/smoke.sh`;
  `sh docker/dev-billing.sh` runs the billing sample on 127.0.0.1:${MAQUETTISTE_PORT:-8080}.
- Idle MSBuild worker nodes from parallel builds can block tools that wait for a quiet machine:
  `pkill -f 'MSBuild[.]dll.*nodemode:1'` when no build is running.

## Rules that bit before

- Determinism is byte-exact: ordinal comparisons, invariant culture, no timestamps or Guid.NewGuid in anything that
  reaches output or hashes; the integration and pack golden tests prove it, run them after engine changes.
- Every disk write goes through IOutputPathPolicy. Public engine signatures change only through the design docs.
- Paths reported by file watchers on macOS come through /private/var; resolve links (WatchPaths, ModelWatcher).
- Container-written files on a Linux bind mount need ACLs: open base bits, then setfacl for 1654 and the host user.
- The modeling skill (skills/maquettiste-modeling/SKILL.md) is engine-owned in customer repos: `init --skill` adds a header and
  refreshes only untouched copies. At each release, after tagging: `git fetch --tags && sh skills/shipped-skills.sh` regenerates
  `src/Maquettiste.Cli/Commands/ShippedSkills.cs` (the hash of the skill at every v* tag); commit it with the next change. Copies
  written with the header carry their own hash, so a missing row never turns an untouched copy into an "edited" one.
- Commits: the owner runs name-sorted scripts under tmp/ (gitignored); write `tmp/NN-<slug>.sh` and never commit
  or push without being asked. No Co-Authored-By or signature lines.
