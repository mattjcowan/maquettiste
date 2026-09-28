# Loading

**Owner:** W1 Loader. See docs/engineering/engine-design.md sections 5, 15 and 18.

The model loader (stage 1), the index cache, the file-name policy, the change planner, the atomic file set and the batch parser. `ModelStore.cs` in the engine root is also W1's.

- Implements: `IModelLoader` (`ModelLoader`) and `ModelStore` (complete).
- Consumes: `ISchemaRegistry`, `ICanonicalJson`, `IModelValidator` (save checks), `IOutputPathPolicy.CheckEngineWrite` (`Model` for model files, `Cache` for the index cache).

## Files

| File | What it does |
| --- | --- |
| `ModelLoader.cs` | Full scan or partial (watcher) load: stat, read or reuse each file in parallel (`jobs` readers), parse, build `ModelSnapshot`, compute the `ChangeSet` against the previous snapshot. Keeps the files of its last load, so a rescan reads only files whose length or mtime changed. `LastStatistics` says what a load did (tests, bench). |
| `DocumentReader.cs` | UTF-8 check and strict parse (MQ1001; a duplicated property name or a byte sequence that is not UTF-8 is MQ1001 at its line and column) → schema (MQ1002; MQ1006 for malformed ids; MQ1007 for `formatVersion`) → deserialize → canonical check (MQ1003). Positions come from `JsonPositionLocator`. |
| `IndexCache.cs` | `CacheDirectory/index.v1.bin`: `MQIX`, format 1, engine version, schema-set hash, then per file: path, length, mtime ticks, SHA-256, flags (validated, canonical), bytes. |
| `ModelPaths.cs` | Model-relative, repo-relative and absolute paths; kebab-case file names with `-<id6>` only on a collision; conventional folders (tables, views and sequences in their database's actual folder). |
| `ChangePlanner.cs` | Plans creates, updates and deletes against a snapshot without writing: schema, ids, canonical bytes, paths, expected hashes, reference removal, candidate snapshot, delete reference checks. |
| `AtomicFileSet.cs` | Stages `.<name>.mq-<batchId>.tmp` files and `.bak` copies beside their targets, then renames; rolls back from the copies on a rename failure. A staged name that already exists (a leftover of a process that died midway; batch ids restart with the process) is overwritten. |
| `BatchParser.cs` | `batch.json` validation of untrusted batches, positioned diagnostics, no disk access. |

## Behavior worth knowing

- **Load.** Enumerates `maquettiste.json`, `model/**/*.json`, `extensions/*.json` and `extensions/rules/*.js`; hidden (dot) files and folders are skipped, so staged temp files never load. Description sidecars are read by reference (`"description": { "file" }`, relative to the element's folder, never outside the model root), not by enumerating `*.md`. A failing file is left out and its diagnostics go to `LoadDiagnostics`; that includes malformed content the reader does not anticipate (a backstop in `ModelLoader` turns any argument, format or invalid-operation exception from reading one file into MQ1001, or MQ5004 for an extension), so one bad file never makes the model unopenable. Duplicate element ids: the ordinally first path wins, the others are MQ1004 and left out; a duplicate sub-element id is MQ1004 on the later file, which still loads. MQ1005 (warning) covers both a file outside its conventional folder and a file name that the name policy would not produce. `maquettiste.json` missing means default settings; `formatVersion` other than 1 is MQ1007 and default settings are used. An invalid extension file is MQ5004 (all its failures); extensions are not checked for canonical form.
- **Index cache.** A file whose length and mtime match its record is taken from the cache without reading it; a record that passed schema validation under the same engine version and schema set also skips schema evaluation and the canonical check (the JSON is still deserialized into records). Records are content-addressed: a record whose bytes do not hash to its SHA-256 is dropped, and a header mismatch drops the whole file. Full loads rewrite the cache when anything differs; partial loads (watcher refreshes and the store's own saves) do not, so the next open re-reads only the files changed since. `VerifyHashes` re-reads and re-hashes everything.
- **Deviation from §5/§2.6:** sidecars are their own cache records rather than a "sidecar hash" field on the element's record, and `DependencyHash = H(Hash, S)` where `S` is null without sidecar references, else `H` over the sorted `(sidecar path, sidecar hash or "absent")` pairs of every sidecar the file references (sub-element descriptions included). `SidecarText` is the element's own (`/description`) sidecar only.
- **Saves** (`ModelStore`). One writer at a time. The plan checks each touched file on disk against its indexed hash; every file an update or delete names is checked, including when its expected hash already fails against the index, and a file changed behind the index is refreshed first (published as a `Disk` change set, also when the save then fails or throws) so a conflict is judged against the disk: a caller holding the disk version saves, any other hash gets `Conflict` with the disk version. Request bodies are parsed strictly (duplicated property names and bad UTF-8 are `Invalid` with MQ1001), and so is each element of a hand-built `ModelBatch`. Validation runs on the candidate model with `ValidationScope(changed ids, IncludeReferrers: true)`; only errors the change introduces (compared by rule, element id and pointer with the same scope before the change) make it `Invalid`, and every diagnostic is returned with the result. A rename keeps the file's folder and renames the file (`<kebab>.json`, suffix only on a collision); a table, view or sequence whose database changes follows it into that database's folder, with its sidecars; a sidecar never overwrites a file in the target folder (a file on disk, one the plan writes, or one another element references): it takes the element's collision suffix (`notes.md` → `notes-<id6>.md`) and `description.file` is rewritten to match, and if that name is taken too the save is `Invalid` (MQ1002 at `…/description/file`). A database rename moves its whole folder, hidden files included; staged leftovers (`.*.mq-*.tmp`/`.bak`) are deleted rather than moved. Unchanged bytes are not written. A write the path policy refuses is `Invalid` with MQ6004; an I/O failure while writing throws `IOException` after rolling everything back.
- **Deletes.** `Refuse`: `Referenced` with every reference to the element or its sub-elements from other files. `RemoveReferences`: removes the referencing property or list entry (a diagram member is removed whole); a referrer left schema-invalid by that (a required reference) makes the delete `Invalid` with MQ2001, and so does removing the last entry of a list whose empty value means something wider (`database.packages`, empty = every package, D6; a foreign key's `referencesColumns`, empty = the referenced primary key). A deleted element's sidecar goes too unless another element uses it; emptied folders under `model/` are removed.
- **Batches.** Validated as a whole before any write; each element may appear in one operation; expected hashes are checked against the files as they were before the batch; every id (top-level or sub-element) must be unique across the elements the batch writes as well as the rest of the model (MQ1004 on the later operation); deletes refuse while anything that survives the batch references the element (stereotype keys included). When a batch fails nothing is written: failed operations carry their outcome, the others report `Saved` with no hash and no changes.

## Performance notes (WP, round 2)

- `DocumentReader.Scan` builds JSON pointers only for the objects that hold an id or a sidecar reference (from the walk's path),
  and compares free-form key names as UTF-8 without allocating them. Same ids, pointers and order as before.
- The canonical check uses the parsed document (`Json/README.md`, element-based canonical check).
- A full load writes the index cache on a second task while the snapshot is assembled, and awaits it before returning, so the load
  still ends with the cache on disk (the tests that read it right after a load are unchanged).
- A document whose bytes are unchanged and that has no sidecar is reused before its dependency hash is recomputed (the hash is a
  function of the bytes alone then). `Compare` looks the previous documents up in the previous snapshot instead of building a
  dictionary of them. The loader's own maps are plain dictionaries (not frozen: they are rebuilt on every rescan).
- The snapshot is built after the previous one (`ModelSnapshot.CreateAfter`), so a rescan that changed a few files patches the
  indexes instead of rebuilding them (`Model/README.md`). On the benchmark an incremental rescan went from about 0.65 s to about
  0.25 s.
- Round 3: `ModelLoader` passes its parallelism and token to the snapshot's index build. `ChangePlanner` takes the store's
  parallelism (constructor) and the save's token (`Plan(changes, ct)`), and builds the candidate snapshot after the current one
  (`ModelSnapshot.CreateAfter(_snapshot, ...)`), so a save that changes a few elements patches the indexes instead of walking every
  document again; the result equals a full build (`ModelIndexerPatchTests`). The delete check's reference model (with deleted
  stereotypes kept) is still a full build.

## Performance notes (WB, one-shot CLI)

- **Index cache read.** The records are parsed in file order, then their SHA-256 checks run in parallel (the loader's parallelism)
  and the map is filled in file order, so a repeated path keeps its last valid record as before; the map is a plain dictionary
  (building a frozen one for 26,267 records cost more than its lookups save in a one-shot process). The schema-set hash in the header
  needs only the embedded schema files, which `SchemaRegistry` now reads without building JsonSchema.Net schemas (`Json/README.md`):
  a process whose files all come from trusted records builds no schema before validation. On the benchmark repo in a fresh
  ReadyToRun process, single instrumented runs (indicative, not medians): schema-set hash about 110 to 120 ms before and about 13 ms
  after (the 110 ms in `Json/README.md` is another single run of the same step), cache read about 97 ms before and about 45 ms after.
- **Dependency hashes on the readers.** An element without sidecar references has `DependencyHash = H(Hash, null)`, a function of its
  bytes; the parallel readers compute it (`FileEntry.OwnDependencyHash`) instead of the sequential assembly (about 60 ms in a cold
  process, one instrumented run). Documents with sidecars are hashed in the assembly as before; the hashes are the same.
- **File stamps for the last-run record.** `ModelLoader.LastFileStamps()` returns the files of the last load with the stat each was
  read at (settings, elements, extensions and rule scripts; existing referenced sidecars; referenced sidecars that were missing), and
  `EnumerateModelFiles()` the files a full load would look at; `ModelStore.LastFileStamps()` forwards to the store's loader. Both are
  computed on request, so hosts that never ask pay nothing (Generation/README.md, "Last-run record").

