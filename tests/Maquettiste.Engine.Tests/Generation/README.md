# Generation tests

W6's orchestration tests run the real loader, validator, resolver, pack loader, planner, change detector, post-processor, writer,
manifest, journal and run lock over temporary repos, with `FakeRenderer` in place of the Scriban renderer (being built
concurrently). `FakeRenderer` honors the `IRenderer` and `RenderContext` contracts: it streams lazily in input order, records
reads through an `IReadRecorder` as the tracking context does (resolved objects' `Dependencies`, `RList` membership keys,
`t:` template keys) and computes input hashes with `RenderContext.Hasher`. Fixture packs live in `tests/fixtures/packs/`.

The list of end-to-end tests the integration stage should add with the real renderer is in
`src/Maquettiste.Engine/Generation/README.md` ("Integration stage").

`SchemaSnapshotTests.The_snapshot_serialized_during_the_run_is_what_a_capture_after_it_saves` (WA) runs the same model changes
through a fixture with the engine's snapshot store (the snapshot is serialized on the thread pool while the run renders) and one
whose store is wrapped so the run takes the plain path (a capture and `SaveAsync` after the apply), and requires identical snapshot
bytes at every revision. `The_store_holds_the_written_snapshot_only_while_runs_diff_it` checks that an apply leaves the store
holding the parse of the snapshot it wrote, that the next run reuses that object, and that a run whose packs no longer diff drops
it. The store and differ paths themselves (the held parse, a missing file and `Retain` dropping entries, a failed or cancelled
held parse, cancellation of the prepare, capture and compare) are in `SchemaDiff/SnapshotReuseTests.cs`.
`RunRecordTests` covers the last-run record's file format (round trip, chunked outputs, damage and truncation, a record from
another engine build) and the unit state store leaving an unchanged state file alone; the record's behavior with the real
renderer is in `Integration/LastRunTests.cs` (a replay equals a full run; every kind of change falls back, including a referenced
sidecar created or edited, an extension or rule script changed and a partial added; templates, manifests and schema
snapshots are compared by content, so a touch is answered and a same-length, same-time-stamp rewrite is not; an intact record
holding a path the file system refuses falls back instead of failing the run; hand edits, schema snapshots and long-lived hosts
write no record).

