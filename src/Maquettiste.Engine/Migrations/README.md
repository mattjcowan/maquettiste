# Migrations

**Owner:** the next-capabilities phase 1 round (milestone A, 2026-10-31). See
docs/engineering/next-phase-1-design.md section 3.2.

Code data and helpers for migration facts (the `migration` kind, `Model/Migrations.cs`): what a change of a column's
type means for the rows already stored.

| File | What it does |
| --- | --- |
| `Widening.cs` | The widening table (`Widening.Rules`, `Widening.Classify`): one table per dialect, only PostgreSQL's has rows. Native types are parsed first (case and spaces folded, PostgreSQL aliases mapped, `numeric(p)` as `numeric(p, 0)`, a missing time precision as 6, array dimensions counted; a type with arguments that are not integers is the same only as the same text). A change is `Same`, `Widening`, `Narrowing` (its reverse widens) or `Conversion`. An internal overload classifies the two sides of a `change-type` fact: by their native types, else by keyword and facets rendered through the dialect's embedded type map (a side without a native type is rendered so when the other side has one, on PostgreSQL); a reference column whose strategy or bound table changed is a conversion (a narrowing when its type narrows). The rows are also the table in `docs/guides/schema-changes.md`; `WideningTests` keeps the two in step. |
| `FactKeys.cs` | The applied order of a database's facts (`Order`: topological over `after`, ties and cycles by the smallest id, links outside the set ignored), the keys their objects have today (`Moves`, through the `rekey` facts after each fact in that order) and the key maps of a `rekey` fact for `SnapshotRekey.Apply` (`KeyMaps`, and their reverse). The resolver, the classifier, `migration list` and fold share the order. |
| `FactHash.cs` | The fact content hash: SHA-256 over the canonical bytes without the annotations, `after`, `held` and `released` (the `$schema` line the writer always puts first is cut, so a file's place never reaches it). |
| `FactClassifier.cs` | The classifier core (task A9): two captures of one database, its open facts after the change, the dialect and the write's key maps in; the facts to create and the pending facts to update out. See the class remarks for the mapping, the key maps, the heads `after` takes (found once per write and table, by one walk of the links) and the order facts chain in. |
| `DatabaseReach.cs` | A database's reach: the element ids its resolved objects can depend on, from the snapshot alone (its own elements, the entities its convention takes and what names its tables, closed over references, the relations of reached entities and derived entities). The store chooses by it the databases a write can reshape when it holds no capture of a database yet; it is a superset of the footprint (`FactRecordingTests.Database_reach_covers_every_resolved_footprint`). |
| `FactDocuments.cs` | Where a new fact goes (its database's `migrations/open` folder, `<lowercase id>.json`), its canonical bytes and the one-line summary a write's answer and `migration list` carry (a value map names its type, codes and holders, or the type's two names; next-phase-2-design.md section 3.5). |
| `BaselineCheck.cs` | `validate --against` (task A12): the baseline's captures read under today's keys (`CapturesAsync`, `ToTodayKeys`: the `rekey` facts the baseline lacks move its keys first; `generate --against` reuses them), the classification from baseline to working and which needed facts none of the facts the baseline lacks explains (`Check`: renames and type changes composed in applied order, widenings allowed around them; a drop for a drop, a backfill for a column made not null), where MQ4102 goes (`Locate`), and the baseline files that did not load, which MQ4109 names (`NotLoaded`, `NotLoadedFor`); the reference codes and names changed since the baseline that no value map records in a database holding the type (MQ4112, `ValueMapFindings`). |
| `FactFolder.cs` | `migration fold` (task A14): one database's candidates (the facts the baseline lacks) lose their stale chains first (links to them name what they followed), then consecutive candidates on one object join into the last one (renames with `via`, a widening into its neighbour, two pending `change-type` facts, a rename into a drop's `formerNames`, adjacent rekeys composed); two conversions, and a join that would order facts in a cycle, are refused. It returns the facts to rewrite and delete; the store writes them in one batch. |
| `StaleFacts.cs` | Stale facts (MQ4103): a pending fact alone, for the validator (`PendingAsync`: the databases holding pending facts are resolved and captured), and chains of the facts a baseline lacks, for the store (`Chains`: per object and aspect, the first candidate's `from` against the model now; candidate rekeys that cancel; with a baseline model's codes, `BaselineCodes`, a value map whose type held none of its codes there and whose names did not change). |
| `CodeMap.cs` | A reference-type refactor's code map (next-phase-2-design.md section 3.5): the type, a merge's merged-away type, the names before and after, the codes mapped and whether the type is renamed. The batch's expansion hands one per operation to the write's recording. |
| `CodeHolders.cs` | Where a resolved database stores reference-type codes: per type, the columns whose `RColumn.ReferenceType` is the type and the template-defined collections typed by it with their owner table (`RDatabase.TemplateCollections`, internal, which a partial run fills too). |

Recording on store writes (task A10) is `ModelStore.Migrations.cs`: it chooses the databases by ids, takes the before
captures from the store's capture cache (one capture and footprint per database of the current snapshot), resolves the
chosen databases of the candidate (`ModelResolver.ResolveDatabasesAsync`, one conceptual run, dependency keys kept),
runs the classifier and adds the fact files to the write's atomic set. `ModelStore.ValidateAgainstAsync` (task A12) runs
the whole-model validation, captures each database both models hold on both sides (`CaptureDatabasesAsync`) and adds
MQ4102, MQ4103 and MQ4109; plain `ValidateAsync` adds MQ4103 for the complete facts of a database that has a committed
physical snapshot (judged against the facts its `facts` lists as rendered). `ModelStore.MigrationTools.cs` holds
`ListMigrationsAsync` (tasks A13: applied order, paged with an opaque cursor, the stale mark from MQ4103) and
`FoldMigrationsAsync` (A14: `FactFolder` per database, written through one batch). `generate --against` (A3) diffs
each database from the baseline's captures (`GenerationRun.BaselineDiffsAsync`).

## Behavior worth knowing

- Making a column nullable is a widening too, but nullability is not a type: callers treat it apart.
- `varchar` with no length to `text` is not in the table, so it is a conversion, exactly as scope 6.2 lists the rows.
- The classifier compares the two captures by key, as `SchemaDiffer` matches them, rather than running the differ: it
  needs the full column states of both sides, and the differ's constraint, index and dependency-order work would be
  thrown away.
- The write's `rekey` fact is the first fact the write records: every other fact names the keys it moved.
- A column whose last open fact is a pending `change-type` keeps that one fact: a type change rewrites its `to` (a
  widening of the narrowed type included), unless the new type is its `from` or a widening of it, which leaves the fact
  stale for fold.

## Stereotype members (next-phase-2-design.md section 3.1)

- Inherited members are resolved columns and keys of each carrying table, so the classifier records their facts as it
  records an own column's: a stereotype change that reshapes many tables records one fact per object it changes.
- `ModelStore.Migrations.cs` chooses more databases while defaults exist: a package whose `defaultStereotypes` changed
  chooses every database holding a physical element of its subtree, and a stereotype also chooses the databases whose
  schemas, or the packages that list it, give it as a default. `Carriers` reads a `defaultStereotypes` reference as its
  database or package and skips `excludeStereotypes`. `DatabaseReach` needs no walk of its own for defaults.
- A fold hands the classifier its constraint moves (`ClassifierInput.MemberRekeys`); the write's one `rekey` lists them
  after its table's column entries, by kind then `from`, each with `table` (`FactKeys.ConstraintKinds`: `index`,
  `unique`, `foreign-key`, `check`). `FactKeys.KeyMaps` and `RekeyMap` skip them, since captures key constraints by
  their columns.
- An open `rekey` whose column moves a write reverses is a key map too, so restoring the files from before a fold
  records the rekey back and no drop. Captures cannot tell that a constraint id came back, so the store reads the
  constraint ids of the tables an open rekey names from the documents (`ConstraintsBefore`, `ConstraintsAfter`) and
  records those entries back after the table's columns.
- `FactFolder` counts a constraint entry as its table and composes it like a column entry; `StaleFacts.RekeysCancel`
  follows constraint entries, so a rekey that moves only constraint ids is never stale alone; a rekey's summary counts
  them (`rekey 1 column, 1 index`).

## Reference-type refactors (next-phase-2-design.md section 3.5)

- A write with code maps (`RecordingRequest.CodeMaps`) chooses, besides what its documents choose, the databases whose
  footprint or reach holds the type whose codes move or an element that references it, and for a type rename every
  database without a binding of the type. Its before entries carry the databases' code holders (`CaptureEntry.Holders`):
  a cached capture without them is resolved again, from the snapshot's whole-model resolve when there is one.
- The classifier emits one `value-map` per code map and database: its objects and collections are the holders of the
  type before the write, moved through the write's key maps, named as the after capture names them, and only those still
  there; with none, only a type rename in a database without a binding of the type records one. It chains after every
  rename the write records and before its other facts, so a merge's change of strategy converts mapped codes.
- MQ4114 (`ModelStore.PlainCodeEdits`) goes on a change that alters a kept row's code or a type's names while no code map
  of the write covers the type; nothing is reported while `migrations.record` is `off`.
- `validate --against` reads the baseline's holders (`CaptureDatabasesAsync` with holders) for MQ4112, and passes the
  baseline's codes to the stale chains; `migration fold --against` passes them too, so it removes stale value maps.
  Without a baseline model (plain `validate`, fold against the committed snapshot) no value map is stale.
