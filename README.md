# maquettiste
Visual designer for entities, relations, processes and databases with code generation capabilities

## Building

Requires the .NET SDK pinned in `global.json` (10.0.109).

```sh
dotnet build maquettiste.slnx   # warnings are errors
dotnet test maquettiste.slnx
```

`SPEC.md` is the specification; `docs/engineering/engine-design.md` is the phase 1 engine contract and
`docs/engineering/host-contracts.md` what the editor host requires of the engine.

## What is built

Phases 1 and 2 of `SPEC.md` Section 21 are complete (product version 0.3.0); phase 3 opens with
`docs/engineering/phase-3-brief.md`.

**Phase 1: engine and CLI.** The model as one JSON file per element under `.maquettiste/` (entities, relations, enums,
value objects, custom types, domains, databases and mappings, diagrams, reference types, seeds, locale shards), in one
canonical form; a loader, validator (every rule an MQ id in the rule catalog, with a test) and resolver; template packs in
a sandbox; an incremental planner that explains why each unit renders; a writer that never writes outside `outputs.allow`;
a manifest, hand-edit detection and `generate --check` for CI; the CLI (`init`, `validate`, `generate`, `format`, `l10n`,
`seed`, `pack new`, `mcp`) and an agent server with 39 tools (`docs/mcp.md`). Example packs: `sql-ddl` and `csharp-dapper`.

**Gate 1 (pass).** The synthetic benchmark (5,000 entities, 20,000 relations, seed 42, `--jobs 8`) writes 100,050 files;
against the Section 13 budgets: load, validate and resolve 1.7 s (3 s), plan 0.1 s (2 s), render 2.2 s (40 s), post-process
and write 4.5 s (15 s), cold total 6.7 s (60 s), incremental 1.4 s (2 s); output is byte-identical across runs and
platforms and `--check` catches drift, orphans and hand edits (`bench/README.md`, `bench/baseline.json`).

**Phase 2: editor.** The IDE-style shell (rail of explorers, inspector, bottom panel, collapsible panels and restored page
state, command palette, one density), the domain model canvas and element editors (entities with inheritance and
mappings, relationships, enums, value objects, custom types, domains with their own tags and categories), explicit
database mapping (a database holds only what is mapped to it) with the Database screen, reference data with storage
strategies and seed data grids, localization of the standard fields, the Generate screen with the pack editor (units,
parameters, templates with completion and live preview, outputs) and an explained plan, project branding, and realtime
saves with conflict handling. The user guide is `docs/user-guide.md`.

**Gate 2 (pass).** The reference application (`samples/reference-app`: 200 entities in 12 domains, 439 relations, 2
reference types) is modeled through the editor's API, edited in a browser walk, generated and applied, and its data layer
compiles; `tools/gate2.sh` checks 209 tables, 1 view and 3 sequences applied to a live database, and `generate --check`
keeps the committed output current.

Known misses carried forward, none a gate item: a few editor timings on the 5,000-entity scale dataset
(`docs/engineering/explorer-redesign.md` §4.5) and the listed "Left" items of the three design documents.
