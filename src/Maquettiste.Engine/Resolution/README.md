# Resolution

**Owner:** W3 Resolver. See docs/engineering/engine-design.md section 18.

The resolver (stage 3), the resolved model types (`RList<T>`, `RElement`, `REntity`, `RTable`, …), conventions and the
`Dialects/<dialect>.json` type maps (embedded as `Maquettiste.Engine.Resolution.Dialects.<file>`).

## Implemented

- `ModelResolver.ResolveAsync` (complete; `NotImplementedException`s removed). One `ResolveRun` per call: the conceptual layer
  (`ResolveRun.Conceptual.cs`), then one `DatabaseRun` per database (`DatabaseRun*.cs`), then dependency keys are frozen. Runs on
  the thread pool, observes the token between elements and databases, reports one `ProgressUpdate` per conceptual element and
  per database. No static mutable state; ordinal comparison everywhere; every list sorted as section 7.1 states.
- Conceptual: packages (qualified names), scalar types, enums, value objects (with stereotype virtual attributes), entities
  (flattened attributes per section 7.2, key from the hierarchy root, alternate keys of every level), relations (ends, cardinality),
  navigations (on the entity opposite the end that names them; `REntity.Navigations` also lists inherited navigations, nearest
  declaration wins), promoted entities and their two many-to-one relations `<relationId>.<endId>` (also listed in each end
  entity's `Relations`).
- Promoted entity key (S6 "an entity has identity and a key"; the design does not say which): with `AllowDuplicates` or an
  ordered end, a synthesized attribute `id` (int64, strategy `database-identity`, id `<relationId>/id`) mapped to the junction's
  surrogate `id` column; otherwise one synthesized attribute per end key attribute, named `{role}{Key}` camel-cased (`memberId`),
  typed like the end's key attribute, strategy `application`, id `<relationId>/<endId>/<keyAttrId>`, mapped to the foreign key
  column `<endId>.<keyAttrId>`. These attributes come first in `Attributes`/`OwnAttributes`, are `Required` and `Immutable`, and
  appear in `REntityMapping.Columns` (their columns' `Attribute`/`AttributePath` point at them).
- Physical, per database: scope by `Database.Packages` and `Mapping.Ignore`; entity tables with naming conventions (project ←
  database overrides, section 2.4 patterns, plural tables: `{entity}` goes through the inflector as a whole, so whole-name
  `inflection.plurals` entries apply, and an element's explicit `pluralName` wins for entity, child and lookup tables); TPH (root table, derived columns nullable, `discriminator` string(64),
  values from `Mapping.DiscriminatorValue` else the entity name), TPT (own attributes, PK also FK to the base table, cascade),
  TPC (concrete tables with every inherited attribute and inherited foreign keys, no table for abstract entities); key identity
  (`database-identity`) and sequences (overlay-named, else synthesized `<entityId>.sequence@<databaseId>` named by
  `sequenceName`); enum storage `int` / `string` / `lookup` (lookup table `id`, `code`, `name`, rows, unique code, FK);
  value objects `embedded` (recursive, `valueObjectColumn`, `AttributeMapping.Prefix`), `table` (child table keyed by the owner
  key), `json`; collections as child tables (owner FK + `position`) or `json`; derived attributes stored only when mapped
  (an `AttributeMapping` or an overlay column) or marked `derived.stored: true`;
  relations per SPEC Section 7's default table (one-to-one FK + unique in the dependent table, one-to-many FK on the many side,
  many-to-many junction with composite key, relations with attributes to a junction or a promoted entity by
  `relationsWithAttributes`, n-ary junction, surrogate `id` and `position` for duplicates and ordered ends); `Mapping.Shape`,
  `ForeignKeyEnd`, `ForeignKey`, `JunctionTable` + `Ends`, `PromotedName`, `Table` + `AttributeMapping.Column` bindings;
  table overlays (renames, schema, comment, column overrides including native type, nullability, defaults, generation, extra
  columns, PK name, uniques, foreign keys, checks, indexes) merged over entity, child, junction, promoted and lookup tables;
  designed and imported tables, views (body for the dialect, else `*`) and sequences passed through; constraint names from the
  patterns; entity mappings (column mappings across TPT tables and child tables) and join paths per navigation; MQ4001 for every
  identifier over `MaxIdentifierLength` (dialect default: pg 63, sqlserver 128, mysql 64, oracle 128, sqlite none), honouring
  `validation.rules`.
- Dialect maps for PostgreSQL, SQL Server, MySQL/MariaDB (`mysql`), SQLite and Oracle; `typeMaps.<dialect>` overrides single
  entries; a custom type's `nativeTypes.<dialect>` wins over both for the columns that store it (applied once, in
  `AddColumn`, so entity, child and junction tables and copied key columns agree; 2026-10-01); `Column.NativeType` wins over
  all; missing facets take `defaultStringLength`, `decimalPrecision`/`decimalScale`, `datetimePrecision`.
- Routines, database types and SQL objects (`DatabaseRun.Objects.cs`, 2026-10-01, erratum E40): resolved per database before
  its tables into `RRoutine`, `RDatabaseType` and `RSqlObject` on `RDatabase.Routines`, `.Types`, `.Objects` and each `RSchema`;
  parameter, result and field types resolve to native types (a database type of the same database through its `NativeName`);
  a column whose file's `nativeType` names a database type (id, name or schema-qualified name) gets `RColumn.DbType` and the
  type's native name; `dependsOn` resolves to the database's objects once every one exists (engine-design.md section 7,
  "Routines, database types and SQL objects").
- Dependency keys (section 11, D38): `e:` of every contributing file, `s:conventions`, `s:typeMaps`, `s:inflection` when used, and
  `r:<id>` for every entity and relation an object derives from (lookup tables: `r:<enumId>`; designed tables: `r:<tableId>`).
  A foreign-key relation also lists what its resolved `RForeignKey`'s name and actions come from (the host table's designed or
  overlay file, the host entity, the principal's key, `s:conventions`, `s:inflection`), since `RForeignKey` is not tracked.
  List membership keys: `model.entities`/`relations` → `k:entity`, `k:relation`, `k:mapping`, `s:conventions` and relation mapping
  files, plus every mapping and database file when any relation can be promoted (by a mapping's `shape` or by conventions);
  an entity's `Relations` adds those keys when the entity has relations; database lists → every `k:` kind, the settings keys, every conceptual file and the union of their tables' keys.

## Choices and deviations (see the W3 report for the full list)

- `RForeignKey`, `RIndex`, `RUnique`, `RCheck`, `REntityMapping`, `RRelationMapping` are not `IResolvedObject`s in the scaffold,
  so they carry no `Dependencies`: their keys live on the owning `RTable` (and each `RColumn`, which shares the table's list) and
  on the owning `REntity` / `RRelation`.
- Synthesized column ids are `<tableKey>/<columnKey>`; navigation ids are `<relationId>.<fromEndId>.<toEndId>`; a child table's
  owner key columns keep the owner's column keys; lookup rows use the member value, else the 0-based ordinal.
- `RStereotype.Name` is the stereotype's display name, else its name, else its key.
- Sequence and constraint names use the column case (section 2.4 "`ColumnCase` for everything else"); child table names
  pluralize the `{entity}` part when tables are plural (`products_tags`); an n-ary junction's `{entity2}` joins every end after
  the first.
- A foreign key to a TPC abstract entity gets its columns but no constraint (its rows live in several tables).
- `RForeignKey.End` of a junction foreign key is the end it references; of a promoted table's key, the promotion relation's
  dependent end. `RNavigation.Joins` holds the declaring entity's path; `REntityMapping.Joins` holds each entity's own path
  (inherited navigations included). An entity without a table (TPC abstract) has no join path, and neither has a navigation
  towards a TPC abstract dependent (its rows live in several tables); the concrete entities' mappings hold their paths.
- `RRelationMapping.ForeignKey` is chosen after foreign keys resolve: the resolved key in the dependent's table, else another
  resolved key of the relation, else `null` (never a half-filled key).
- `DefaultExpression` is not translated into `DefaultSql`; templates read it from the attribute.
- Diagnostics the resolver adds (validation rules otherwise stay with W2): MQ4001 (identifier limits); MQ4008 for every foreign
  key that cannot be resolved (synthesized, designed or overlay: missing referenced table or columns, a referenced table without
  a primary key, a column count mismatch), which is left out of its table (a designed or overlay key may also be reported by W2's
  MQ4008 when that rule runs as a warning); MQ4011 when a relation's dependent end is bound to a designed table and either the
  principal is synthesized and no `Mapping.ForeignKey` is named, or the named foreign key is not in the database (both ends bound
  with none named stays W2's MQ4011); MQ4009 for a table overlay nothing consumes (a TPH-derived or TPC abstract entity, a bound
  entity, or a target without a synthesized table in the database), which is then not applied.
- A bound dependent's relation attributes get no columns (the designed table is complete as written).

Tests: `tests/Maquettiste.Engine.Tests/Resolution/` (golden billing model in `Golden/billing/resolved.txt`, rewritten with
`MAQUETTISTE_UPDATE_GOLDEN=1`).

## Index overrides from table files (integration fix, 2026-09-28)

An index a table file declares (an overlay or a designed table) overrides the index an attribute's `indexed` flag synthesizes on
the same resolved columns, instead of being added beside it. `IndexSpec.FromFile` marks file-declared specs, and
`DatabaseRun.Finish` drops a synthesized index whose column list (by column name, in order) matches a file index. Sort order,
method, name, include list and predicate therefore come from the file. The billing fixture's `invoices.issued_on` index is the
covered case: the attribute is `indexed` and the overlay adds the same column descending. Two file indexes on the same columns are
still both kept, so a deliberate second index with a different name works.
## Performance notes (WP, round 2)

The resolved model is unchanged: a dump of every resolved object with its members, dependencies and list membership keys over the
benchmark model is byte-identical before and after these changes (331,184 objects), and the golden and resolver tests pass.

- **Parallel conceptual phases.** Entity shells, entity attributes and keys, relations, navigations, the finishing pass (navigation
  lists, relation lists, derived entities, mappings) and the final dependency freeze run on the run's parallelism
  (`EngineOptions.MaxDegreeOfParallelism`). Each item writes only objects it creates or owns; what it registers (`Find`) or adds to
  shared lists is returned and committed afterwards in model order, so registrations (first id wins), list orders and progress
  counts are those of the sequential loops. Keys read across items (a relation's and its end entities' keys for a navigation) are
  frozen first. A failure rethrows the lowest index's exception, as a sequential loop would. Database runs stay sequential: a
  database run reads relation keys that earlier database runs added (`FinishRelationMappings`), so their order is part of the result.
- **Parallel pure steps of a database run:** freezing each table's keys, column mappings per entity, and join paths per entity are
  computed in parallel and recorded in the original order.
- **Dependency sets** (`DependencySet`): a set lives on its object (`RObject.PendingDependencies`) instead of a dictionary keyed by
  object identity, and is frozen into `Dependencies` at the end. Frozen lists are `FrozenKeys` (sorted, distinct, never changed);
  adding one to another set keeps it as a sorted run that is merged pairwise instead of re-sorted, a set that is exactly one run
  returns it (lists are shared, not copied), and `RList` shares a frozen membership list instead of copying it. Very many runs (a
  database's membership) are gathered in a hash set and sorted once. The result is the same ordinal, distinct list.
- **Memos:** enum kebab names per enum type (an immutable table built once per type); rendered name patterns per database run
  (keyed by the pattern and the values of the tokens it uses, length-prefixed); inherited navigations per entity (cleared whenever
  promotion adds a navigation); navigation join paths are collected on the navigation (`RNavigation.PendingJoins`, internal) rather
  than in an identity-keyed dictionary. `ModelResolver` reuses the previous run's `Inflector` when the inflection settings are the
  same object (an incremental run whose `maquettiste.json` did not change); an inflector is a pure, thread-safe function of its
  settings.
- `DependencyKeyCache` is thread-safe (a concurrent dictionary); which string instance wins a race does not matter.
- Warm resolve of the benchmark model: about 1.8 s before, about 0.7 s after (8 threads).

## Review fixes (WP, round 3)

- **Rendered-name memo key.** The key now length-prefixes the pattern as well as every token value (`DatabaseRun.MemoKey`), so a
  pattern with a literal such as `|5:X` can no longer collide with another pattern whose token value contains `|`.
- **Termination on invalid snapshots.** A pipelined run resolves beside validation (`Generation/README.md`), so the resolver must end
  on shapes validation rejects. Two expansions did not: an embedded value object that contains itself (MQ3015) was expanded to depth
  16 (4^16 embeddings for four self-typed members), and an inheritance cycle (MQ3002) made the placements' base links a cycle, so
  the descendant walk of TPH never ended. `Embed` now skips a value object already on its containment path and observes
  cancellation per call; `ComputePlacements` leaves out a base link that would close a cycle. Valid models have neither shape, so
  their resolved model is unchanged (golden and resolver tests). `SpeculativeResolutionTests` resolves every invalid validation
  fixture, the self-embedding and cyclic value objects, and an inheritance cycle under TPH, TPT and TPC, with a bound.
- **Inflector carried across runs, bounded.** `ModelResolver` reuses the previous `Inflector` only while its memo holds at most
  `MaxCarriedWords(documents)` = max(16,384, 8 × element files) results; past that (names a long-lived host no longer uses) the run
  starts a fresh one. The benchmark model memoizes about one word per file (25,922 for 26,267 files), so it is always reused there.

## Performance notes (WB, one-shot CLI)

- The objects whose dependency sets are frozen at the end are collected in a `ConcurrentBag` (per-thread lists) instead of a
  list behind a lock that every resolved object took once (about 330,000 on the benchmark, from the parallel phases). Freezing is
  per object and already ran in parallel, so the order of the collection does not matter; the resolved model is unchanged (the
  benchmark's cold output tree is byte-identical). Indicative only: an interleaved A/B of three rounds on a machine at a load average
  of 4 to 22 gave a cold resolve median of 1.20 s with it and 1.30 s without, a difference three samples at that load cannot
  establish; the change is kept because it removes a lock taken about 330,000 times, not for that figure. The database runs stay
  sequential; in a fresh process they are most of what a one-shot edit run still pays (`bench/README.md`, "One-shot CLI").
- The bag keeps its per-thread lists in thread statics of the threads that added to it, which would keep every resolved object
  reachable after the run until the bag's finalizer ran (a long-lived host would carry the previous resolved model through one more
  collection). `ResolveRun.Run` empties the bag when it returns or throws (`ConcurrentBag.Clear`: 4 to 8 ms for 330,000 objects,
  median 5 ms over eleven runs of a micro-benchmark); `ResolverRetentionTests` checks that a dropped resolved model is reclaimed by the next
  collection, sequential and parallel.

## Reference data (RT step 3: reference-types-seeds-localization.md sections 1.4, 1.5 and 2.5)

- `ResolveRun.ReferenceData.cs` resolves reference types before value objects and entities (every type exists before any field or
  attribute resolves, so a self-referencing type works), and seeds, rows and reference usages after `FinishConceptual` (they read
  flattened attributes and ends). The engine models intent only: a storage choice is a project strategy key or null
  (template-defined, the default); no lookup table, CHECK, FK or native type is ever synthesized.
- Effective choice per (type, database): the type's entry for the database id, its `*` entry, `databases.<name>.referenceStorage`,
  `conventions.referenceStorage`, else none; `RStorageChoice.Source` is `type`, `database`, `project` or null.
- A single-valued reference attribute maps to one column of type `reference` (`RColumn.ReferenceType`, `CodeType`, `Strategy`; the
  code's length rides along and the native type is the code's); a collection maps to no column and no child table and is listed in
  `REntityMapping.TemplateDefined` / `RRelationMapping.TemplateDefined` (an attribute mapping with `ignore` leaves it out). Only
  direct attributes are listed: a reference collection inside an embedded value object is skipped silently (no column either).
- Dependency keys (deviation from the design's wording "a reference type's `e:` key plus the `e:` keys of its seeds"): the
  `RReferenceType` object carries its own element keys only; the seeds' keys sit on the objects that expose rows (`rows`
  membership, each `RRow` and `RSeedRow`, `RReferenceUsage`), so a unit that reads only a type's name or code facets does not
  re-render when a row changes. `RReferenceUsage` and `RStorageChoice` are resolved objects with their own keys for the same reason.
- `Find(rowId)` returns the `RRow` for a reference-type row (its `RSeedRow` twin is not registered) and the `RSeedRow` otherwise.
- `refs` maps are built lazily on first read (thread-safe) and capture only the reference types' code maps and the seed-row map;
  `json` refuses resolved objects, so `json row.values` never follows a row reference.
- Orders: `OrderedRows` is Kahn's algorithm over in-seed references with ties by file order (a cycle is broken at its first
  remaining row in file order); `SeedsInOrder` sorts by (depth over cross-seed references, back edges of a cycle ignored, name, id).

## One database at a time (explorer step 13, E5c and E5f)

`ResolveRun.RunOneDatabase` (`ModelResolver.ResolveDatabaseAsync`, internal) resolves the conceptual layer and one database's run
only, for the editor's table summaries and table detail (`Editor/DatabaseTables.cs`). A database run reads the conceptual layer and its
own files; what earlier database runs add (relation dependency keys in `FinishRelationMappings`) and what runs after the databases
(`FinishConceptual`, seeds, usages, the dependency freeze) shape no table, so they are skipped and dependency lists stay unset. The
tables are those of `Run` (tests compare every `TableView` on the fixture model and the bench's synthetic model). Generation always
uses the whole-model `Run`. Numbers: explorer-redesign.md section 4.5 (`bench time-tables`).

## Processes (P5a: phase-3-design.md sections 4.3 and 7.1)

- `ResolveRun.Processes.cs` resolves actors, then process shells (an invoke names another process), then each process, then
  scenarios, the actors' uses and `REntity.Lifecycle`, after the seeds (a lifecycle reads its subject's flattened attributes). The
  R-types are in `ResolvedProcesses.cs`; engine-design.md section 7.1a lists them.
- The chart is the interpreter's: `StatechartModel.Get(process, null)` gives the states in document order with their paths, depth,
  initial children and history defaults, and the transitions whose source resolves in priority order. Nothing about paths or order is
  re-derived here. `AfterMs`, `AfterTicks` (the exact span, below a millisecond too) and the `iso_duration_ms` helper use
  `StatechartModel.ParseDuration` (fixed spans).
- `RStep.Trace` is the engine interpreter's replay of the step's scenario (`ScenarioReplayer` over a `ProcessRuntime` of the resolved
  snapshot, pool size 1), computed lazily once per scenario and shared by its steps, so a run whose templates never read it pays
  nothing; its reads record the step's keys (the scenario's and the process's). A step's `Payload` names keys by event payload, then
  gate audit attribute, then context attribute.
- `ProcessText.Label` and `ProcessText.ShortDuration` copy the editor's `edgeLabel` and `shortDuration` (`canvas/statechart/
  chartModel.ts`): an unknown id reads `?`, the invoke triggers read `done: <invoke>` and `error: <invoke>`, the duration pattern
  accepts decimals in any unit (ASCII digits, as the script pattern). The editor test `statechart-label-parity.test.ts` compares the
  canvas labels with the golden `Golden/processes/resolved.txt` on the gate 3 fixture. A transition's `DisplayName` is its
  `displayName`, else its label.
- As built where section 4.3 left a choice: `IsAtomic` (and `AtomicStates`) is what a configuration reports as an active leaf
  (`ChartState.IsLeaf` without choice states): atomic and final states and containers without children, never history or choice
  states. `Depth` is the chart's (1 for a child of the root). `RegionIndex` is set on the children of a parallel state. `Initial` is
  set on compound states only (`null` on a parallel state). `BoundMember` is the bound enum's member with the state's name, set only
  on a lifecycle's bound root states. `Context` is the declared context attributes stably sorted by order, without stereotype virtual
  attributes (the interpreter's context holds only declared attributes). An action's `UsedBy` lists the transitions that run it, then
  the states whose entry or exit run it. `REvent.Transitions` holds event-triggered transitions only. `RActor.Events` lists the events
  that name the actor (an event without actors accepts any actor but is not listed on every actor).
- The audit record (`RGate.Audit`, `RAuditField`): `instance` string, `process`, `gate`, `transition` `id`, `sequence` int64, `signer`
  string, `actor` and `meaning` `id`, `reason` text, `at` datetimeoffset, `outcome` string with `Values` signed, completed, refused,
  discarded; then one field per audit attribute with its `Attribute`. `signer`, `actor`, `meaning` and `reason` are not `Required`:
  a discarded record carries none, even when the gate requires a reason on signatures.
- Scenario maps (`Start.Context`, `Payload`, `Expect.Context`) key by attribute name, `Assume` by guard name; an id that resolves to
  nothing keeps the id as its key. A scenario whose process does not resolve (MQ2001) is left out of `model.scenarios`.
- Dependency keys (deviation from phase-3-design.md 4.3's list, which puts `r:<process id>` on the process): a process's own
  object carries no `r:` key. The referrers of a process are its scenarios, so an `r:` key on the object would re-render every
  process unit and every sibling scenario unit whenever one scenario is edited. `r:<process id>` sits on `process.scenarios`
  (with `k:scenario`), the only member that other files decide. An actor's `processes`, `events` and `gates` carry `r:<actor id>`,
  so an actor's unit re-renders when a process starts naming it (a scenario step naming the actor also changes the key, which is
  a harmless over-approximation). Every process node lists the process file's `e:` key plus the subject's and the bound enum's
  (a bound state's member follows the enum), and the `e:` keys of the actors and invoked processes that node names. An entity that
  names a lifecycle adds `k:process`, so the process appearing or disappearing re-renders its units.
- Processes are resolved one after another (not on the parallel phases): each writes the shared actor uses in process order.

Tests: `tests/Maquettiste.Engine.Tests/Resolution/ProcessResolutionTests.cs` (golden `Golden/processes/resolved.txt` over the gate 3
fixture, ordering, keys, the interpreter's chart, the duration rules), `Planning/ProcessScopeTests.cs`,
`Rendering/ProcessRenderTests.cs`, `Integration/ProcessGenerationTests.cs` (incremental runs).
