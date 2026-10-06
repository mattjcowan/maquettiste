# Snapshots

**Owner:** the model-service round (phase A, 2026-10-05). See docs/engineering/snapshots.md.

Named, immutable copies of the whole model as deterministic zip archives under `.maquettiste/model-snapshots/`, and the
read-only document store that serves one without extracting it.

| File | What it does |
| --- | --- |
| `SnapshotLibrary.cs` (+ `.Compare.cs`, `.Restore.cs`) | The public service: list, create, update (rewrites only `snapshot.json`, the last entry), delete, open read-only (a `ModelStore` over the archive; the two most recent stay loaded), compare (index against index, or against the store's hashes), per-element field diff, restore (run lock, safety snapshot, one `ReplaceDocumentsAsync`), export (`OpenRead`) and import (copy, check, rewrite, load once, store). |
| `SnapshotArchive.cs` | The layout (`SnapshotLayout`: content paths, safe paths, entry time and compression), the index rows (path, SHA-256, element id, kind and name from a top-level scan), the metadata (`snapshot.json`) and the deterministic writer. |
| `ZipDocumentStore.cs` | `IModelDocumentStore` over an archive: stat and list from the entry table, reads under a lock from an archive opened on demand (`Close` after a load), every write refused; `ReadOnlyPathPolicy`, the guard of a snapshot opened read-only (MQ6029). |
| `SnapshotRecords.cs` | The public records the API, the CLI and the MCP tools serialize. |

## Behavior worth knowing

- Same documents and metadata, same bytes: entries in ordinal order, then `snapshot-index.json`, then `snapshot.json`, every
  entry dated 1980-01-01 and compressed with `Fastest`.
- Creating reads 512 documents at a time in parallel and streams them into `.<id>.zip.tmp`, renamed into place; both names pass
  `CheckEngineWrite(Model)`. The working model is read from disk, not from the store, so no load is needed.
- An opened snapshot uses the live model's options, so its documents carry the same repo paths; it never rescans, and the
  loader neither reads nor writes the index cache for it.
- Import never trusts the archive: names are checked before anything is read, sizes and the expansion ratio are bounded,
  decompression stops at each entry's declared size, and the stored archive is the engine's own rewrite.
