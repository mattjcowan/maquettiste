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
