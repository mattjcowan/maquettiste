# Planning

**Owner:** W6 Planner and orchestration. See docs/engineering/engine-design.md section 18.

The pack loader, unit planner (stage 4), dependency hasher, unit state store and change detector (stage 5) (engine-design.md
sections 8 and 11). Implemented; no stubs left.

| File | Implements |
| --- | --- |
| `PackLoader.cs` | `IPackLoader`: local packs under `<ModelRoot>/templates/<name>/pack.json` |
| `EngineRange.cs` | the `engine` range of a `pack.json` |
| `UnitPlanner.cs` | `IUnitPlanner` (`UnitPlanner`) and the `where` filters (`UnitFilter`) |
| `DependencyHasher.cs` | `IDependencyHasher` |
| `UnitStateStore.cs` | `IUnitStateStore`: `CacheDirectory/units/<pack>.v1.bin` |
| `ChangeDetector.cs` | `IChangeDetector` |
| `CanonicalForm.cs` | sorted-key compact JSON for hashing settings sections, units and parameters |
| `EngineFiles.cs` | guarded atomic writes for W6's stores (unit state, plans, jobs), through `IOutputPathPolicy.CheckEngineWrite` |
| `PackFiles.cs` | pack-relative path confinement and file hashes |

Tests: `tests/Maquettiste.Engine.Tests/Planning/`; fixture packs in `tests/fixtures/packs/`.

## Pack loader

- Discovery: every folder of `<ModelRoot>/templates/` (dot folders skipped) that holds a `pack.json`, in ordinal name order;
  `packs.<name>.enabled: false` leaves it out (also when it is requested by name). A requested name without a folder is MQ6001.
  `LoadedPack.Order` is the position among the loaded packs; `RelativePath` is repo-relative (`.maquettiste/templates/<name>`);
  `RootPath` is the absolute folder.
- `pack.json`: strict JSON (MQ6001 with line and column), `pack.json` schema (each failure re-coded MQ6001 with its pointer and
  position), `name` equal to the folder (MQ6001), engine range (MQ6002 when not satisfied, MQ6001 when unparseable), unique unit ids,
  template and companion template paths relative, inside the pack and existing (MQ6001), and no `where` filter other than
  `database` on a `for: model` unit (MQ6001; such a unit has no element to filter).
- Scripts: `scripts` lists pack-relative paths (inside the pack, existing, listed once); empty means every `*.js` in the pack,
  recursively, dot files and folders skipped, ordinal by pack-relative path. `ScriptSource.Path` is repo-relative
  (`.maquettiste/templates/<pack>/helpers.js`), so the sandbox reports pack script errors as MQ6016 (it recognizes rule scripts by
  path). `ScriptsHash = H("mq-scripts-1", count, path₁, hash₁, …)`.
- Parameters: the manifest's defaults overlaid by `packs.<name>.parameters`, in an ordinal sorted map.
- Type maps: `types/*.json` (top level of `types/`), each a JSON object of strings (`$schema` ignored); the target is the file
  name without `.json`. A non-string value or bad JSON is MQ6001.
- A pack with any error is left out of `PackSet.Packs`; the orchestrator stops the run on any pack error, so a broken pack is never
  treated as removed (which would orphan its whole manifest).
- Shared packs by git reference are phase 4.

## Unit planner

- `for`: `model` (one unit, no element), `each package|entity|relation|enum|value object|table` (`table` covers every database,
  databases by name, tables in resolver order), `select <name>` (JavaScript selector through a pool of size 1 created only when a
  pack needs scripts; each id goes through `ResolvedModel.Find`, unknown ids are MQ6017 at `/units/<i>/for`). Script failures
  (`ScriptErrorException`, `ScriptLimitException`) become their MQ6016/MQ6007 diagnostics and the affected units are not planned.
- `generation["*"].skip` or `generation[<pack>].skip` on a conceptual element drops its units (either one set is enough). For a
  table (`each table`, or a selector that returns tables) the hints of the table's own file count: the designed or imported table,
  or the overlay of a synthesized table (found by its section 7.3 key). A synthesized table does not inherit the skip of the entity
  or relation it comes from: dropping a table's DDL because its entity's classes are skipped would silently break the schema
  (foreign keys of other tables still point at it); skip the table through an overlay instead.
- `where` (every set filter must match; lists match any value): tags and stereotypes of the element; categories by id or name
  with descendants (through the category tree); packages by id or qualified name with sub-packages (a package unit matches on the
  package itself and its ancestors); `database` (a table in it; an entity or relation with a mapping there, i.e. in
  `Mappings`; any other element or a model unit when the database exists); `abstract` (entities by `IsAbstract`, anything else
  counts as not abstract); `script` (a JavaScript filter, seeded with the unit key).
- Keys: `<pack>/<unitId>` or `<pack>/<unitId>:<elementId>`; units ordered by pack order then key ordinal, a duplicate key kept once.
- `StaticHash = H("mq-unit-1", EngineVersion.Value, pack name, pack version, canonical PackUnit JSON, ScriptsHash, canonical
  effective parameters, PackSettings.Output, formatter, template hashes, unit key)`. Formatter: the named formatter's settings, `"none"`,
  `"unknown:<name>"`, or, for a unit that names none, every configured formatter (which one applies depends on output paths).
- Output path expressions are not evaluated here: `PackUnit.Output` is rendered by the renderer (D19), so the planner stays cheap.

## Hashing, state, skip

- `DependencyHasher.CurrentHash`: `e:` → the owning document's `DependencyHash`; `k:<kind name>` → `KindSetHash`; `r:` →
  `ReferrersHash`; `s:conventions` (conventions and `databases`), `s:typeMaps`, `s:inflection` → `H(key, canonical JSON of the
  section)`; `t:<pack>/<path>` → content hash of the pack file (confined to the pack folder); `d:<databaseId>` → that
  database's `SchemaDiffResult.Hash`; anything else, or anything that no longer resolves → `"absent"`. Cached per run
  (thread-safe). `InputHash = H(StaticHash, key₁, hash₁, …)`; keys that are not sorted and distinct are sorted first.
- `UnitStateStore`: one binary file per pack (`MQUS`, format 2, engine version, a table of the distinct read keys, then the states
  sorted by key with their read keys as indexes into the table). A missing, truncated or foreign file loads empty (every unit of
  the pack renders; a format 1 file from an earlier build therefore costs one full render). Pack names that are not kebab keys are
  replaced by `x-<16 hex>` in the file name. Saving an empty list deletes the file. Written by the writer (W7) when a pack closes
  in apply mode. The store remembers, per pack, the bytes it last read or wrote with their decoded states and returns those
  states when a load reads byte-identical content (so a watch or editor host does not decode 100,000 states per run); any other
  content is decoded.
- `ChangeDetector`: design section 11 exactly; stat checks run in parallel (`MaxDegreeOfParallelism`), the result keeps plan
  order. With the engine's own hasher it compares input hashes through `DependencyHasher.InputHashEquals`, which hashes the same
  bytes as `InputHash` in a pooled buffer and compares the hex in place (no string per unit). An output must also still be listed in the manifest under the unit's own pack. `r:` outputs whose stat changed are
  re-hashed as skeletons (`ManifestHashes.Comparable`).

## Deviations and notes

- The static hash also folds in the pack version (`pack.json` `version`), which templates read as `pack.version` with no dependency
  key: without it a version bump left every unit skipped by an incremental run while `--force` re-rendered them. Section 11 of the
  design does not list it.
- The static hash also folds in the content hashes of the unit's template and companion template (the design lists the canonical
  unit JSON, which names them but not their content). Partials and anything else the renderer loads are covered by the `t:`
  keys the renderer records; the unit's own templates are covered whether or not the renderer records them.
- `where` on tables: tags, stereotypes and the category come from the table's own file (designed, imported, or the overlay of a
  synthesized table, found by the section 7.3 key) together with the entity or relation the table comes from; its package is the
  entity's or relation's. `RTable` exposes none of these, so the planner reads them from the snapshot. The design does not say.
- `where.database` on elements other than tables, entities and relations (and on `for: model`) only requires the database to exist;
  the design names tables, entities and relations only.
- A pack disabled in settings is skipped even when requested with `--pack`, silently. Because a run without a pack filter
  orphans the manifests of packs that are not loaded, disabling a pack deletes its (non-owned, unedited) outputs on the next full
  apply; a pack with load errors stops the run instead.

## Performance notes (WP, gate 1)

- Unit states: format 2 above (the synthetic 100,000-unit benchmark's state file went from 76 MB to 28 MB; its decoded read keys
  are shared strings), a direct little-endian encoder, and the decoded-states memo. Encoding stays deterministic: the same states
  in any order give the same bytes.
- Static hashes: the planner builds the length-prefixed bytes of a pack unit's shared fields once and appends only the unit key
  per element; the bytes hashed are exactly those of `HashBuilder.Of(...)` (`UnitPlannerTests` checks the formula).
- `DependencyHasher.CurrentHash` reads its cache before `GetOrAdd` and keeps one compute delegate (no delegate per call).

## Performance notes (WP, round 2)

- **Unit states keep their read keys as table indexes.** Decoded states carry `TableKeys` (the file's shared key table, the state's
  indexes, and the state's record bytes in the file with the owning state); `Encode` maps old indexes to new ones with one lookup
  per distinct key instead of hashing every key of every state, and copies a record byte for byte when every key keeps its index
  and the record belongs to that very state (a copy of a state, or a new state reusing its keys, is encoded afresh). A save
  remembers the states as a load of the written bytes would give them, so the next incremental run neither decodes the file nor
  re-encodes the states of the units it skipped. The bytes are exactly those of a fresh encoding (`UnitStateFastPathTests`).
- **Input hash over table-indexed keys** (`DependencyHasher.InputHashEquals`): each key's two hash fields (the key and its current
  hash) are encoded once per table index and copied; sortedness is checked on precomputed ordinal ranks (equal keys share a rank,
  so a repeated key goes to the general path). Same bytes hashed, same result.
- **Skip stage:** the stats of the outputs the loaded states record are taken in parallel beside resolution
  (`OutputStats`, started by the generation run after the run lock); the check uses them and stats only what they lack. Taking a
  stat a moment earlier within the same run is the same as a slightly faster check.
- **Unit planning:** units are sorted once (pack order, key ordinal, planned order as tie-break, so it is the stable order) and
  static hashes are computed for the kept units only, in parallel. Same units, order and hashes.
