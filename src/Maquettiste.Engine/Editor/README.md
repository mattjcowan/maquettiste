# Editor

The engine additions the editor's functions call (phase2-design.md section 3.8, explorer-redesign.md section 4.1).

- E1 `GenerationService.GetDatabaseViewAsync` → `DatabaseViews` (the resolved tables of one database; null on a model with errors).
- E2 `GenerationService.GetPacksAsync` → `PackList`. E3 settings documents → `SettingsDocuments`.
- E5 index rows: `ElementSummary` gains `DisplayName`, `Database`, `Entity`, `MemberCount` and `Ends` (`Model/ModelIndexer.Summarize`),
  left out of the JSON when null.
- E5b `ModelReads.ReadElementsAsync(this ModelStore, ids)`: up to 200 documents from one snapshot, each once, plus the missing ids.
- E5c `DatabaseTables.GetAsync(databaseId)`: the table list without columns, partial on a model with errors.
- E5e `ModelReads.IndexTag(summaries)`: the index ETag. E5d (summaries on `model.changed`) lives in the functions host.

## Deviations

- E5c reads `ModelStore.Current` (loading when null) instead of `GetSnapshotAsync`: the stat rescan of 26,000 files costs about
  200 ms per call, the index is served the same way, and the host's watcher keeps the store current. Callers that write files behind
  the store's back call `RescanAsync` first (the tests do).
- E5c resolves a model with errors (the resolver ends on invalid snapshots, Resolution/README.md); if the resolver still throws on a
  model with errors, the answer is no tables with `partial: true` and the validation diagnostics. On a valid model the exception
  propagates.
- `DatabaseTables` keeps one entry: the last snapshot and its shared task. The shared work runs without the caller's token so a
  cancelled request does not fail the others waiting on it; each caller stops waiting on its own token. A faulted or cancelled entry
  is recomputed on the next call.

## Performance (bench model: 26,267 rows, databases of 10,005, 1,683 and 327 tables)

- E5c: the first call for a snapshot validates and resolves side by side, 2.4 s with the JIT warm (validation about 0.3 s, the
  resolve dominates). Every database of that snapshot then answers in under 1 ms. 10,005 table summaries are 230 KB gzipped.
- E5e: `IndexTag` over 26,267 rows takes about 20 ms (serializing the index about 100 ms), so the 304 path skips most of the work.
- E5 adds 2.6 MB to the 8.2 MB index JSON (2.4 MB gzipped in all), mostly the relation ends.
