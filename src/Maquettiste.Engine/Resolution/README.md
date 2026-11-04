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
  identifier over `MaxIdentifierLength` (dialect default: pg 63, sqlserver 128, mysql 64, oracle 128, sqlite none),
  measured as `DialectInfo.IdentifierLength` does (UTF-8 bytes on pg and oracle, characters elsewhere), and MQ4068 for
  index and constraint names that collide in a namespace of `DialectInfo.NameSpaces` (`CheckNames`), honouring
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
- Definitions (`ResolveRun.cs` `ResolveDefinitions`, `ResolvedVocabularies.cs`, next-phase-1-design.md section 5.5):
  `model.stereotypes` (one `RStereotypeDefinition` per key, by key, with its virtual attributes owned by the definition),
  `model.tag_vocabularies` and `model.category_trees` (the global one first, then by package qualified name and id), each
  depending on its own file, with membership keys `k:stereotype`, `k:tag-vocabulary` and `k:category-tree`. A tree lists
  the same `RCategory` instances annotated objects carry; the run completes their `Parent`, `Children` (by order, none
  counting as 0, then file order), `Order`, `Description`, `Rationale`, `Decision` and `Status`.
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
- Diagnostics the resolver adds (validation rules otherwise stay with W2): MQ4001 (identifier limits); MQ4068 (name
  namespaces); MQ4008 for every foreign
  key that cannot be resolved (synthesized, designed or overlay: missing referenced table or columns, a referenced table without
  a primary key, a column count mismatch), which is left out of its table (a designed or overlay key may also be reported by W2's
  MQ4008 when that rule runs as a warning); MQ4011 when a relation's dependent end is bound to a designed table and either the
  principal is synthesized and no `Mapping.ForeignKey` is named, or the named foreign key is not in the database (both ends bound
  with none named stays W2's MQ4011); MQ4009 for a table overlay nothing consumes (a TPH-derived or TPC abstract entity, a bound
  entity, or a target without a synthesized table in the database), which is then not applied.
- A bound dependent's relation attributes get no columns (the designed table is complete as written).
- Queries (`DatabaseRun.Queries.cs`, 2026-10-02, erratum E41): resolved per database after `FinishDatabaseObjects` into `RQuery`
  on `RDatabase.Queries` (by name, id), with `RQuerySource`, `RQueryField`, `RQueryParameter`, `RQueryOrder`, `RQueryPaging`,
  `RQueryCollection` (and its `RQueryKey` correlation keys) and the trees as `RQueryExpression` and `RQueryPredicate`, each node
  with an inferred type; a nested query (collection, `exists`) is an `RQuery` with `Parent`. Sources resolve by table key, table
  or view file id, or entity id; columns by key, attribute id or (loosely, when unique) name. The resolver reports the query
  rules MQ4021 to MQ4043, MQ4018 for a parameter type and MQ3001 for duplicate parameter or field names, with the JSON pointer of
  the node, since only it knows the columns; `RQuery.Sql` is `QuerySql.Render` for the database's dialect, empty on errors.
  Dependency keys: the file and its referrers, the database, the type maps and conventions, every table's and view's it reads,
  the routines and database types it uses, the result and collection entities; the database lists add `k:query`
  (engine-design.md section 7, "Queries").

- Bindings (`DatabaseRun.Bindings.cs`, `ResolvedBindings.cs`, 2026-10-02, erratum E43): an entity's binding to the database
  is collected before placement, so the entity is never projected there (a mapping element for it is MQ4054 and ignored); a
  binding that writes or reads a designed table gives the entity a `ViaBinding` placement on that table for relations only (no
  entity mapping, no `RTable.Entity`, no attribute columns). After the queries each binding resolves into `REntityBinding` on
  `REntity.Bindings` and on the `BoundBy` of the tables, views and queries it uses, and reports MQ4044 to MQ4053 with the
  pointer of its entry; the objects it names list the entity's file among their dependency keys (engine-design.md section 7,
  "Bindings and materialize"). After MQ4047, `ReportDrift` reads a binding's provenance through `Model/BindingDrift.cs`
  (next-phase-2-design.md section 3.3): MQ4088 at `/bindings/<i>/provenance` when the source is not a table file or none
  of the recorded ids is one of its columns, else one MQ4087 at `/bindings/<i>` naming every mapped column renamed or
  retyped since it was recorded. All three take the severity `validation.rules` gives them.
- `IDatabaseResolver` (`ModelResolver.cs`) resolves the conceptual layer and some databases only: fact recording and the
  plans of the binding operations use it, and a resolver without it answers from a whole-model resolve.

Tests: `tests/Maquettiste.Engine.Tests/Resolution/` (golden billing model in `Golden/billing/resolved.txt`, rewritten with
`MAQUETTISTE_UPDATE_GOLDEN=1`; bindings in `BindingTests.cs`).

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
- Former codes (next-phase-2-design.md section 3.5): `RReferenceType.FormerCodes`, ordinal by code text, each an
  `RFormerCode` (`Kind` `former-code`, id `<type id>:<code>`) whose `ReplacedBy` is the row of the type its `replacedBy`
  names; an entry naming no row of the type is left out. It depends on the type's document and the row. A type with a
  scripted seed builds them with its rows, on first read. `RowOf` finds live codes only.
- A single-valued reference attribute maps to one column of type `reference` (`RColumn.ReferenceType`, `CodeType`, `Strategy`; the
  code's length rides along and the native type is the code's); a collection maps to no column and no child table and is listed in
  `REntityMapping.TemplateDefined` / `RRelationMapping.TemplateDefined` (an attribute mapping with `ignore` leaves it out). Only
  direct attributes are listed: a reference collection inside an embedded value object is skipped silently (no column either).
- A designed, imported, extra or inherited column whose `typeRef` names an enum, a reference type or a scalar type
  (next-phase-2-design.md section 3.2) keeps its own physical `type`; `DatabaseRun.TypeColumn` sets `RColumn.TypeRef`,
  fills the facets the column leaves out from the type (the code's length and an enum's longest stored code in a string
  column, a scalar's facets in a column of its base, whose native type then applies), and for a reference type sets
  `ReferenceType`, `CodeType` and `Strategy` as on a projected reference column. The table gains the type's keys
  (`s:referenceData` for a reference type).
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

`ResolveRun.RunOneDatabase` (`ModelResolver.ResolveDatabaseAsync`, internal) resolves the conceptual layer and one
database's run only, for the editor's table summaries and table detail (`Editor/DatabaseTables.cs`). A database run
reads the conceptual layer and its own files; what earlier database runs add (relation dependency keys in
`FinishRelationMappings`) and what runs after the databases (`FinishConceptual`, seeds, usages, the dependency freeze)
shape no table, so they are skipped and dependency lists stay unset. The reference type bindings into its tables are the
exception: a reference column's bound table is part of its shape and its physical snapshot, so `LinkReferenceBindings`
links them as `Run` does after every database (next-phase-2-design.md section 3.2). The tables are those of `Run` (tests
compare every `TableView` on the fixture model and the bench's synthetic model). Generation always uses the whole-model
`Run`. Numbers: explorer-redesign.md section 4.5 (`bench time-tables`).

`ResolveRun.RunDatabases` (`ModelResolver.ResolveDatabasesAsync`) resolves several databases after one conceptual run,
and with `freeze` keeps the dependency keys of what it resolved: the store records migration facts this way
(next-phase-1-design.md section 3.2), resolving only the databases a write can reshape and reading each one's footprint
(the element ids of its objects' `e:` keys) from the frozen keys. The keys are those of the partial run, a subset of
what `Run` would give. A partial run publishes no entity's or relation's mappings (`FinishConceptual` sets them), so
each `RDatabase` also lists its mappings' template-defined collections with their owner table (`TemplateCollections`,
internal, filled in both runs), which the value-map holders read (next-phase-2-design.md section 3.5).

Neither partial run binds a calculation host, so their objects read `RCalculated.Empty`: nothing on these paths (table
summaries, migration recording and stale facts, materialize, stereotype fold) renders a template or runs a script over
resolved objects, and every template and resolved rule that reads `calculated` runs on the whole-model `Run`.

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

## Large models (2026-10-05, generation-ui.md section 5.2 "Bounds")

- A binding's foreign-key field (`DatabaseRun.BindingBuilder.Target`) found its relation end by walking every relation of the
  model, once per field: a database with 2,741 bound tables in a model of 20,000 relations resolved in 15.6 s. The ends are now
  indexed once per resolution (`ResolveRun.RelationsWithEnd`, built on first use after the conceptual pass, in `RelationOrder`
  order, so the first match is the one the walk found): 0.8 s, and 1.4 s for the whole model (from 16.2 s).
- Seed rows of an entity or a relation convert their cells on first read (`RSeedRow.Values`); `RSeed.OrderedRows` and
  `ResolvedModel.SeedsInOrder` are ordered on first read (`SeedOrdering`, which holds only the seeds and the row index, never the
  run). A seed whose columns name no other row (no end, no reference type) keeps file order without reading its cells, which is
  what the Kahn pass gave it. A reference type's rows stay eager: its codes, `RowsByCode` and every usage need them. Generation
  output is unchanged (the golden and determinism tests); a 100,000-row seed that no template reads is never converted.

## Migration facts (next-phase-1-design.md section 3.2, task A8)

- `ResolveRun.Migrations.cs` runs after each database run (`Run` and `RunOneDatabase`): the database's open facts in
  applied order (`Migrations/FactKeys.Order`: a topological order over `after` that takes the ready fact with the
  smallest id; links to facts outside the database are ignored, and a cycle, MQ4104, is broken by the smallest unplaced
  id), their object keys moved through the `rekey` facts after them (`FactKeys.Moves`), then the projection: by current
  key, a table, view, sequence or column takes the `from` and `via` names of its renames as `FormerNames` (oldest first,
  distinct, without its current name) and the first fact that names it (as `object` or in `objects`) as `Since`; a
  column takes the conversion of its last `change-type`. Facts never change a capture. A value map's `Collections`
  (`RMigrationCollection`, next-phase-2-design.md section 3.5) move their owner table key as a table key into
  `CurrentTable`; `FromReferenceType` and the states' `PluralName` are read as recorded.
- Dependency keys: a fact carries its own `e:` key, its database's and those of the `rekey` facts after it (its keys
  today depend on them), plus its annotations' keys. `RDatabase.Migrations` and `ResolvedModel.Migrations` carry
  `k:migration` and every fact's `e:` key (one fact's `after` moves the others in the order; the model's list also
  `k:database`). A projected table adds the
  `e:` key of each fact projected onto it or its columns to the dependency list it shares with its columns (and to its
  column list's membership); a projected view or sequence adds its own facts' keys.
- `RMigration.Hash` (`Migrations/FactHash.cs`) is computed on first read, from the fact's document.

Tests: `Resolution/MigrationResolutionTests.cs`, `Editor/ResolvedRecordsTests.cs` (the `migrations` scope),
`Migrations/FactHashTests.cs`, the `migration` targets of `ResolvedCoverageTests`; the resolved goldens print every
table's, column's, view's and sequence's `former_names` and `since`.

## The null contract (next-phase-1-design.md section 5.5, task C24)

- A member backed by a schema property with a default carries the default; an absent value is `null`, never `""` or
  `false`. A physical object's `DisplayName` and `PluralName` are its file's or `null` (a conceptual object still falls
  back to its name and the inflector); `RView.Body`, `RRoutine.Body` and `RSqlObject.Body` are `null` without text for
  the dialect; `RRoutineReturns.NativeType` is `null` for a table result; `RDatabaseType.NativeName` is `null` where the
  dialect stores the type as nothing (a composite or range outside PostgreSQL); `RQuery.Sql` is `null` when the query
  cannot be rendered there (MQ4029) and on a nested query; `RAuditField.Description` is `null` for an attribute without
  one.
- Derived members (fallbacks, inferences) keep a value and document their rule: `ResolvedNullContractTests.Derived`
  lists them (a column's key, type, native type and identity, constraint names, a routine's language and volatility, the
  native type of a routine parameter, result column, composite field or query parameter, a query source's alias, a query
  field's name and nullability, an attribute's order, the display name of an enum member or process node, a scenario's
  start).
- The physical snapshot keeps `""` for a view without a body, so captured snapshots stay byte-identical.

Tests: `Resolution/ResolvedNullContractTests.cs` (every optional schema value without a default has a nullable member or
a `Derived` entry; no `""` in a member of any fixture model, or of a built model holding what no fixture has, unless its
schema default is `""` or `Derived` lists it).

## Sensitivity on both sides (next-phase-2-design.md section 3.2, D23)

A column's `Sensitive` is its entry's own (`ApplyColumnFile`, which also keeps it as the internal `OwnSensitive`), else
a projected column's attribute's own (`AddColumn`), else, after the database's bindings resolve, the strongest own value
of the attributes their fields map to it (`ShowBoundSensitivity`, for columns that store no attribute).
`ResolveRun.Shared.cs` runs after `FinishConceptual`: an entity attribute that states none shows the strongest
`OwnSensitive` of the columns its bindings read and write, and depends on what states those columns' entries
(`RTable.EntryKeys`: a designed or imported table's file, database, stereotypes and package chain, not the other
entities bound to it; a table without such a file, its whole dependency set). Attributes hold their own values during
the database runs, so each side reads only the other's own value. A value object member is shared by every embedding and
keeps its own. One-database runs skip the pass.

## Descriptions from bound column comments (next-phase-2-design.md section 3.3, D34)

The same pass gives an entity attribute with no description of its own the comment (`RColumn.Comment`, the text the
database gets) of the first column its bindings map it to that has one: bindings in database name order (the order of
`REntity.Bindings`), fields in file order, the read column before the write column. `RAttribute.DescriptionColumn`
names that column, and `AttributeRecord.DescriptionColumn` its key. The attribute depends on the tables of every column
its bindings map, commented or not, so a comment added later replans what reads it. A projected column's comment is set
in its database run from its attribute's own description, before the pass, so nothing cycles. Value object members and
relation keys take no fallback. The sensitivity and the description share one walk over the bindings.

## Stereotype members and defaults (next-phase-2-design.md section 3.1)

- A table's effective set comes from its holder (`TableBuild.Holder`, `TableBuild.Effective`), with its schema by
  `StereotypeDefaults.SchemaOf`, so validation and resolution judge the same set. A table without a file takes its
  entity's or relation's package and the stereotypes defaults give it (storage and annotations included); a child table
  without an overlay takes its owner table's holder. `RStereotype.Source` says where an entry came from (`own`,
  `implied`, `schema-default`, `package-default`).
- `DatabaseRun.Members.cs` adds inherited members in `FinishTables`, after a table's own columns and overlay extras and
  before positions and keys: columns as a designed column resolves, keys, indexes and checks as specs of the table's
  `TableBuild`, so naming conventions and the key rules apply unchanged. `skip` holds each stereotype whose virtual
  attributes the table stores as columns (decision 1). A member foreign key's target is its `referencesTable`, else the
  carrier's merged `properties[referencesParameter]`; a carrier without the value gets no key and no second finding.
  It also reports MQ4076, MQ4079, MQ4003 for synthesized tables, MQ2013 to MQ2015 for a table without a file (on its
  entity or relation at `/stereotypes`), and an overlay's links: `TableMembers.CheckLinks` and MQ4008 for an overlay key
  that names a member the table skips.
- Every resolved column, primary key, unique, foreign key, check and index carries `MemberOrigin`; own members share
  `RMemberOrigin.Own`. `RPrimaryKey.Id` is the key's id, keyed for an inherited one.
- A resolved object whose holder has a package adds the `e:` key of every package on the chain, own first, so a package
  that gains its first default list re-renders its members' units.
- A relation mapping's `foreignKey` names a file's key only: relations resolve before a table's members are added.

Tests: `Resolution/ResolverBehaviorTests.cs` (the stereotype member cases), `Model/StereotypeDefaultsTests.cs`,
`Resolution/DependencyKeyTests.cs`, `Integration/StereotypeMembersTests.cs` (the acceptance lines on billing).

## Scripted rows (next-phase-2-design.md section 3.4, D43)

- A seed with `rowsFrom.script` gets `RSeed.RowsScript` and lazy `Rows`: the first read runs the script through
  `SeedScripts.Evaluate` and the engine's `SeedRowsCache` (held by `EngineServices`, shared by the resolver and the
  validator), never while resolving. Each row's id is `StableIds.RowId(seed id, row key in seed terms)`; the list and
  every row carry the seed's keys, the script's `x:` key and the keys the run read. A script that fails makes the read
  throw its MQ7127 (a `ScriptErrorException`), so the unit that read the rows fails with it; the resolved model's
  records list no rows instead (`RSeed.RowsOrNone`).
- A reference type with a scripted seed builds all its rows on first read, its stored seeds' rows too, so every row
  keeps its place among the type's rows; none of them is registered for `ResolvedModel.Find`, as scripted rows are in no
  index. Its usages' `Allowed` and `DefaultRow` are lazy too, and resolving reads only `RReferenceType.RowKeys`, the
  membership keys known before any run.
- Scripted rows are matched to columns by `SeedScripts.ColumnNames`, from the snapshot, so validation and the resolver
  agree: a foreign-key entry is named by the foreign key's name in its file, else its id, as CSV files head it.

## Expressions (next-phase-3-design.md section 3.1, task H7)

- `ResolvedExpressions.cs`: `RExpression` (a typed node: `Node`, `Op`, `Call`, `Value`, `Args`, `Target`, `Type`, `Nullable`,
  `Text`; the root's `JavaScript` is lowered on first read and cached), `REntityCheck`, `RConstraint`, `RConstraintParameter`, and
  `ExprResolution`, which types each site in the scope validation reads (`ExprSites`, silently: validation reports what does not
  type, and such a site resolves to nothing) and lowers through `ExprLowering` only.
- A table's, overlay's or stereotype's tree check is typed when its file is read (`DatabaseRun.TypeCheck`, so what it names joins
  the table's keys before they freeze) and lowered once the columns are final (`DatabaseRun.ResolveCheck`, from `FinishTables`), into
  `RCheck.Expression`; a stereotype's check reads its columns under their keyed ids. On another dialect it is left out with MQ4056.
  An overlay check on a column of a stereotype the synthesized table skips is MQ4008 at its condition (`ReportSkippedMemberUses`), and
  excluding a column a kept stereotype check reads is MQ2028 (`TableMembers.CheckLinks`), so neither leaves the DDL silently.
- A binding's `checks` are lowered through its field map (`AddBindingChecks`, as each binding resolves in `Publish`) and appended to
  its write table's checks with `MemberOrigin.Source` `binding`; `CheckIdentifiers` and `CheckNames` therefore run after `Publish`.
  The table depends on what each listed check depends on (`AddBinderKeys`, `REntityCheck.DependsOn`): the base that declares an
  inherited check, a value object a path reads, and what the condition names.
- A code the column it meets cannot store (a text code where the column stores integers) fails the lowering instead of converting:
  the check is left out with MQ3103 at a table, MQ3110 for a binding's check, the rules validation reports by lowering any check that
  holds a code (`ExpressionRules.CodeMisfits`). A seed holds a reference type's codes in its `code` column.
- Entity checks resolve with the entity's attributes (`ResolveRun.Conceptual.cs`, `ResolveExpressions`, on the parallel phase): the
  root base's first, an inherited one per derived entity (registered once, by its declaring entity); derived values (`RDerived.Value`)
  for the attributes an entity declares or inherits. Constraints resolve after reference types (`ResolveConstraints`); a
  `satisfies` is inlined in every `RExpression` tree, printing as the citation at its root.
- `RCheck.Shape` (internal) is the hash of the check lowered with column keys in place of names: the snapshot keys a tree check by it
  and the differ treats equal shapes as unchanged (decision 22). It is a hash of the lowered check rather than of the stored tree, so
  a cited constraint's condition, a binding's field map or an enum member's stored value changing still changes it.
- A model without expressions runs none of this: each entry point returns on the first test (`ExpressionCostTests`).
