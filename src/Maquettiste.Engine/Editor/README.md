# Editor

The engine additions the editor's functions call (phase2-design.md section 3.8, explorer-redesign.md section 4.1).

- E1 `GenerationService.GetDatabaseViewAsync` → `DatabaseViews` (the resolved tables of one database; null on a model with errors).
- E2 `GenerationService.GetPacksAsync` → `PackList`. E3 settings documents → `SettingsDocuments`.
- E5 index rows: `ElementSummary` gains `DisplayName`, `Database`, `Entity`, `MemberCount` and `Ends` (`Model/ModelIndexer.Summarize`),
  left out of the JSON when null.
- E5b `ModelReads.ReadElementsAsync(this ModelStore, ids)`: up to 200 documents from one snapshot, each once, plus the missing ids.
- E5c `DatabaseTables.GetAsync(databaseId)`: the table list without columns, partial on a model with errors.
- E5f `DatabaseTables.GetTableAsync(databaseId, key)`: one `TableView` (`DatabaseViews.Table`), null when absent or left out.
- E5e `ModelReads.IndexTag(summaries)`: the index ETag. E5d (summaries on `model.changed`) lives in the functions host.
- Bulk reads (2026-10-01): `ModelPages` (index filters, pages by (kind, name, id) with an opaque cursor, documents trimmed to
  fields, kind counts) and `ResolvedRecords` with `GenerationService.GetResolvedAsync` (the resolved model as flat records per
  template scope; the last snapshot's validation and resolution are kept, so paging resolves once).

## Deviations

- The bulk reads start from `GetSnapshotAsync` (a stat rescan per page, so a page never misses a file edited behind the host),
  unlike E5c. `GetResolvedAsync` keeps the last snapshot's resolved graph (not projected records), so a page projects only its
  own records; the graph is released when the next snapshot is read. Sorting a page sorts the whole scope (n log n per page).
- E5c reads `ModelStore.Current` (loading when null) instead of `GetSnapshotAsync`: the stat rescan of 26,000 files costs about
  200 ms per call, the index is served the same way, and the host's watcher keeps the store current. Callers that write files behind
  the store's back call `RescanAsync` first (the tests do).
- E5c resolves a model with errors (the resolver ends on invalid snapshots, Resolution/README.md); if the resolver still throws on a
  model with errors, the answer is no tables with `partial: true` and the validation diagnostics. On a valid model the exception
  propagates.
- `DatabaseTables` keeps the last snapshot only: its validation task and one task per database asked for (step 13: each database is
  resolved on its own, `ModelResolver.ResolveDatabaseAsync`; a resolver without it falls back to one whole-model resolve per
  snapshot). The resolved objects are projected (summaries and `TableView`s, in parallel) and dropped, so the cache holds no
  resolved graph. The diagnostics are the validation's and that database's resolution diagnostics. The shared work runs without the caller's token so a
  cancelled request does not fail the others waiting on it; each caller stops waiting on its own token. A faulted or cancelled entry
  is recomputed on the next call.

## Performance (bench model: 26,267 rows, databases of 10,005, 1,683 and 327 tables)

- E5c before step 13: the first call for a snapshot validated and resolved the whole model, 2.4 s with the JIT warm. After step 13
  (`bench time-tables`, `write-model` defaults: databases of 4,831, 10,179 and 10,179 tables): 0.27 to 0.4 s and 0.46 to 0.95 s per
  database after an edit; a 700-entity model's databases of about 1,460 tables 59 to 127 ms. Repeated calls and E5f are lookups
  (under 1 ms). 10,005 table summaries are 230 KB gzipped. Details: explorer-redesign.md section 4.5.
- E5e: `IndexTag` over 26,267 rows takes about 20 ms (serializing the index about 100 ms), so the 304 path skips most of the work.
- E5 adds 2.6 MB to the 8.2 MB index JSON (2.4 MB gzipped in all), mostly the relation ends.
