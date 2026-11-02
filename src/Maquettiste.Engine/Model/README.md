# Model

**Owner:** Scaffold. See docs/engineering/engine-design.md section 18.

Model records (engine-design.md sections 2.1 to 2.6): elements and sub-elements, `ProjectSettings`, `PackManifest`, `ExtensionSchema`, `ManifestFile`, `PhysicalSnapshot`, the union converters (`TypeRef`, `Description`, `MaxCardinality`), `ElementRefAttribute`, `KindInfo`, `BuiltinTypes`, `EngineJson`, the document records and `ModelSnapshot` with its indexes (`ModelIndexer`).

- Implements: `ModelSnapshot.Create` (complete).
- Consumes: `Hashing` (`HashBuilder` for kind-set hashes), `Pipeline.ScriptSource`.

Record shape and schema shape must stay in step: `tests/Maquettiste.Engine.Tests/Json/SchemaConsistencyTests.cs` fails the build otherwise. A change here goes through engine-design.md first.

## Performance notes (WP, round 2)

- `ModelIndexer.Build` walks documents in parallel (a document's walk reads only the element and the stereotype keys) and merges
  the walks in path order, so the first registration of an id wins exactly as in a sequential walk. Reference groups are built in
  one pass (order within a group as `GroupBy` gives it). The snapshot's id, document and reference maps are plain dictionaries,
  written only while the snapshot is built (concurrent reads are safe); freezing a few hundred thousand ids on every load cost more
  than it saved. `ReferencesTo` and `ReferencesFrom` return the stored arrays wrapped as `ImmutableArray<ReferenceInfo>`
  (`ImmutableCollectionsMarshal.AsImmutableArray`: no copy): patched snapshots share those arrays, so a caller must not be able to
  write one through a downcast to `ReferenceInfo[]` (round 3). `ReferrersHash` reads the arrays directly.
- `ModelSnapshot.CreateAfter(previous, ...)` (internal; the loader uses it) patches the previous snapshot's indexes when at most 256
  documents were added, removed or replaced (same `ElementDocument` instance = unchanged), none of them a stereotype, tag vocabulary
  or category tree, and the previous index had no id registered twice: unchanged documents keep their walks, the removed ones'
  ids and references are taken out, the added ones' put in (a reference group is rebuilt in path order), the touched kinds' sorted
  lists and id-set hashes are recomputed, and an id that would now be taken twice falls back to the full build. The public `Create`
  is unchanged. `ModelIndexerPatchTests` checks the patched indexes against a full build, map by map and in order, over chains of
  seeded random edits (content changes, moved attributes, re-pointed ends, stereotype changes, renames, additions, removals, id
  collisions).
- Round 3: the index build's document walk runs on the caller's parallelism and observes its token. The internal
  `ModelSnapshot.CreateAfter(..., parallelism, ct)` and `ModelIndexer.Build(documents, previous, parallelism, ct)` take them; the
  loader passes `EngineOptions.EffectiveParallelism` and the load's token, and the store's change planner the same with the save's
  token (so `--jobs 1` walks one document at a time). The public `ModelSnapshot.Create` keeps its signature and walks on
  `min(cores, 8)` threads, as before. `ModelSnapshotIndexTests` checks the index is the same at any parallelism and that a
  cancelled build throws.

## Performance notes (WB, one-shot CLI)

- `ModelIndexer.MetadataCache` (per-type property metadata of the document walk) is a `ConcurrentDictionary`: lookups take no lock.
  It is read once per object walked by the eight parallel walks of a full build, and the lock was contended in a fresh process.
  A type two walks miss at once may be computed twice; the first stored array wins and the result is a pure function of the
  type, so indexes are unchanged.

## Stereotype members and defaults (next-phase-2-design.md section 3.1)

- `StereotypeDefaults.cs` builds each element's holder (`StereotypeHolder`: its own and excluded keys, the package
  whose chain gives package defaults, the schema entry whose defaults reach it, by `SchemaOf`) from the documents alone,
  before the index, so the index's property walk can use it. `ModelSnapshot.HolderOf` caches holders per element id, and
  `EffectiveStereotypes(Element)` builds the layered set (schema, packages outermost first, own; each layer's implied
  copies first; a key listed twice keeps its last place). The key-list overload stays for sub-elements and process
  nodes. With no default list in the model the set is exactly phase 1's, and the internal `PropertyKeys` is null so
  property schemas apply as before.
- `ContributedMembers.cs` is the kind-free mechanism (`KeyOf`, `TryParse`, `Inherit`, `CheckLinks`); `TableMembers.cs`
  makes tables its holders: member sets keyed `<tableKey>.<memberId>`, the facet table of redefinitions, and MQ2027,
  MQ2028 and MQ4078 for one table. A member set is a pure function of the snapshot, the table and `skip`, cached per
  snapshot and (table key, skip); a candidate table that is not the snapshot's instance is never cached.
- `Element.ExcludeStereotypes` is virtual so that `Migration` can hide it with `[JsonIgnore]`: System.Text.Json drops an
  ignored override from a record's contract but not an ignored `new` property, and `migration.json` has no such key.
- `MemberForeignKey` has an internal `Name`, outside the JSON contract, that only a member set fills from a table's
  redefinition.
- `ModelIndexer` treats a `MemberForeignKey` and a primary key with an id as `key` sub-elements, and
  `redefines[].member` and `excludes` as references to stereotype members. While either model declares a default, a
  patch builds the index in full when a package, database or mapping changes, when an entity, relation, process,
  reference type or table is added or removed or changes its package, and when a table changes its schema, entity,
  relation, attribute, origin or database: each can move the holder of a kept document.

## Binding provenance and drift (next-phase-2-design.md section 3.3)

- `EntityBinding.Provenance` (`BindingProvenance`) maps column ids, or keyed ids of inherited columns, to
  `"<name part>:<type part>"`. It is a record, not a link: no member is an `[ElementRef]`, so the index walks the
  record and finds no reference in it, and a column delete never sees it.
- `ColumnShapeHash.cs` computes the two parts from a table file's own column entry (an inherited member's entry with
  the table's redefinition applied): the name part over the loose name (lowercase, ASCII letters and digits only), the
  type part over eight fixed lines. The line list is part of the format.
- `BindingDrift.cs` computes one binding's drift from files only, for the resolver, the drift report and re-sync:
  `ColumnsOf` (own entries in file order, then inherited members), `Find` (the resolver's column lookup), and `Of`
  (renamed, retyped, added with the accounting's status, removed, and whether the provenance is stale). Without a
  provenance, or with a stale one, only the columns nothing accounts for are listed as added.
