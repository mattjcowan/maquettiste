# Status

The build record: what each phase delivered and what each gate measured.

## Building

Requires the .NET SDK pinned in `global.json` (10.0.109).

```sh
dotnet build maquettiste.slnx   # warnings are errors
dotnet test maquettiste.slnx
```

`SPEC.md` is the specification; `docs/engineering/engine-design.md` is the phase 1 engine contract and
`docs/engineering/host-contracts.md` what the editor host requires of the engine. `docs/engineering/next.md` lists the
work queued next, with the decisions each item waits on.

## What is built

Phases 1, 2 and 3 of `SPEC.md` Section 21 are complete (product version 0.10.1). Phase 3 answers
`docs/engineering/phase-3-brief.md` with `docs/engineering/phase-3-design.md`, whose section 9 records every round.

**Phase 1: engine and CLI.** The model as one JSON file per element under `.maquettiste/` (entities, relations, enums,
value objects, custom types, domains, databases and mappings, diagrams, reference types, seeds, locale shards), in one
canonical form; a loader, validator (every rule an MQ id in the rule catalog, with a test) and resolver; template packs in
a sandbox; an incremental planner that explains why each unit renders; a writer that never writes outside `outputs.allow`;
a manifest, hand-edit detection and `generate --check` for CI; the CLI (`init`, `validate`, `generate`, `format`, `l10n`,
`seed`, `pack new`, `mcp`, and later `model export` and `model stats`) and an agent server with 39 tools at the time, 51 today (`docs/mcp.md`), including the bulk reads an external system needs to pull a model of thousands of elements: documents in pages, the resolved model as flat records, and kind counts. Example packs: `sql-ddl` and `csharp-dapper`.

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

**Phase 3: processes, actors and scenarios.** Processes as statecharts (atomic, compound, parallel, final, history and
choice states; event, delayed, completion and eventless transitions; guards and actions as JavaScript expressions or
named stubs; service tasks, human tasks and sub-processes; gates with N of M signatures, required actors, meanings and an
audit record), used as the lifecycle of an entity bound to an enum attribute or as an orchestration; actors (people, roles,
external systems) and scenarios (recorded runs that the engine replays as tests) as element kinds; 49 MQ9xxx rules with
quick fixes in the Problems panel; one interpreter in the engine for validation, simulation and verification; XState import
and export as a projection that round-trips byte for byte; the Processes explorer, the process, actor and scenario
editors, the statechart canvas with nested layered layout and the simulation panel that records scenarios; the CLI's
`process simulate|record|verify|export|import|sync-enum` and six process tools in the agent server (46 tools in all at the time).
Example packs: `csharp-dapper` gains the process units (states, definition, contracts, handler, service, machine and store
pairs, endpoints with user-code regions, a typed dispatcher with pipeline behaviours, a generated interpreter and one test
per scenario), `sql-ddl` gains optional process tables, and the new `process-docs` pack writes Markdown pages; the
TypeScript sample pack mirrors the process units. The user guide's chapter is "Processes, actors and scenarios".

**Gate 3 (pass).** Over `tests/fixtures/models/processes` (a sales-order lifecycle and a purchase-approval orchestration,
both with parallel states and a gate), `tools/gate3.sh` on the merged phase 3 tree (24 cores): validate with no error and
no warning, every file canonical; 16 of 16 scenarios verified in the engine (the fixture's 14 and one recorded through the
editor's simulation panel for each process); `generate` with the three packs clean under `--check`, 110 files
byte-identical at `--jobs 1`, `--jobs 24` and in the editor's apply, and the generated solution builds with
`-warnaserror`; 16 of 16 generated scenario tests pass; each process exported to XState and imported over itself gives the
same bytes; the bench (5,000 entities, 1,000 processes, 5 of 400 states, 5,000 scenarios) meets every budget: macrostep
p95 0.006 ms (0.2 ms), guard p95 0.003 ms (0.05 ms), simulate of 200 inputs 12.7 ms (30 ms), rules of one 400-state process
2.9 ms (10 ms), whole-model rules 359 ms added (400 ms), replay of every scenario 2.74 s (3 s), export 5.8 ms and import
8.3 ms (50 ms).

**Model snapshots (2026-10-05; phase A of `next.md` item 4, engine and server).** Named, immutable copies of the whole model
as deterministic zip archives under `.maquettiste/model-snapshots/`, behind a document store interface under the model store
(the model folder and a read-only archive as its providers): take, list, rename and publish, delete, open read-only, compare,
restore after a safety snapshot, export and import, over the API, the CLI and the MCP tools. The design and its timings:
`docs/engineering/snapshots.md`; erratum E45. The editor side (the picker on the project name, read-only "as of" mode
with `?snapshot=` in the URL, the compare view, restore with its undo) is in that document's section 10.

Known misses carried forward, none a gate item: a few editor timings on the 5,000-entity scale dataset
(`docs/engineering/explorer-redesign.md` §4.5) and the listed "Left" items of the design documents, the phase 3 ones
gathered in `docs/engineering/phase-3-design.md`'s close-out paragraph.
