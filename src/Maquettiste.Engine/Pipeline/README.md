# Pipeline

**Owner:** Scaffold. See docs/engineering/engine-design.md section 18.

Every stage interface and DTO of engine-design.md section 4 (`IModelLoader` … `IOutputWriter`, `IDependencyHasher`, `IUnitStateStore`, `IOutputPathPolicy`, `IManifestStore`, `IRunJournal`, `IRunLock`, `IDiffGenerator`, `IFormatterRunner`, `ISnapshotStore`, `ISchemaDiffer`) and `ManifestSet`.

- Implements: the records. `ManifestSet`'s members are stubs that W7 fills in (it may add internal members and constructors).
- Consumes: `Model`, `Resolution`, `SchemaDiff`, `Scripting` types.

Public signatures are binding; change them only through engine-design.md.

## Contract note (WP, round 3)

`IModelResolver.ResolveAsync` documents that a pipelined run resolves beside validation, so a resolver must terminate, and
observe cancellation, on any snapshot the loader produces (cycles and dangling references included); its result for a snapshot
with validation errors is discarded. Documentation only: no signature changed.
