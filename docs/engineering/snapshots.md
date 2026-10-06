# Model snapshots

Phase A of `next.md` item 4 ("Model service"), engine and server side, built 2026-10-05. A snapshot is a named, immutable
copy of the whole model at a point in time: taken, listed, renamed and published, deleted, opened read-only ("as of"),
compared with another snapshot or with the working model, restored into the working model, exported and imported. The
editor's side (the picker, the as-of mode, the compare view) followed the same day; section 10 describes it. Erratum E45
records the SPEC change.

Every operation is an explicit action of a person or an agent (the API, the CLI, the MCP tools). Nothing takes, compares or
restores a snapshot in the background, so each may read the whole model; each streams, so none copies the whole model
into memory beyond what the store already holds.

## 1. Owner's decisions

- Snapshots are zip archives under `.maquettiste/model-snapshots/`, one per snapshot. Not `.maquettiste/snapshots/`, which
  holds the physical schema snapshots the migration diff reads.
- The packs (`.maquettiste/templates/`) are left out by default; `includePacks` holds them, so generating from the snapshot
  can reproduce exactly.
- Compare is part of the first build.
- Restore takes a safety snapshot first; undo is restoring that one, never the editor's undo stack.
- The product writes nothing outside `.maquettiste/`, the engine stays free of git, and every write goes through the
  engine-write guard.

## 2. The document store seam

`IModelDocumentStore` (`src/Maquettiste.Engine/Loading/ModelDocumentStore.cs`) is the interface under `ModelStore` and
`ModelLoader` that the model-service proposal (section 1) asks for, kept to what the loader and the store use: `Stat`,
`FolderExists`, `List`, `ReadAsync` (model-relative paths), `ApplyAsync` (all-or-nothing writes and deletes), plus
`IsReadOnly` and `UsesIndexCache`. Two providers:

| Provider | Reads | Writes | Index cache |
| --- | --- | --- | --- |
| `FileDocumentStore` | the model folder | `AtomicFileSet`, through `CheckEngineWrite(Model)` | yes |
| `Snapshots.ZipDocumentStore` | a snapshot archive, entry by entry, never extracted | refused (MQ6029) | no |

The file provider is a pure refactor: the loader's stat, enumeration and reads, and the store's settings read, stale-file
check, batch preview and every `AtomicFileSet` call (saves, batches, settings, translations, default locale, format,
branding icon) now go through it, with the same behaviour; every engine test and golden is unchanged. `EngineServices.Documents`
is built on each access from `EnginePaths` unless set, so a services copy with another guard writes through that guard. The
partial methods that read the live folder directly for a write-only purpose (extension files, pack lists, sidecar moves) stay
as they were; on a snapshot their writes are refused by the guard. The http and database providers of the proposal are not
built.

`AtomicFileSet` lost a quadratic step (each delete searched every write); a restore of tens of thousands of files needed it.

## 3. The archive

```
branding/...            the project icon files (settings.branding.icon names one)
extensions/...          extension schemas and script rules
maquettiste.json
model/...               every element document, locale shard and sidecar (hidden and staged files left out)
templates/...           the packs, only with includePacks
snapshot-index.json     one row per document: path, SHA-256, and the id, kind and name of an element document
snapshot.json           the metadata, always the last entry
```

- **Paths** are model-relative (relative to `.maquettiste/`). A path is safe or it is not held: `/` separators, no empty,
  `.`, `..` or hidden segment, no colon, backslash or control character, at most 1,024 characters
  (`SnapshotLayout.IsSafePath`). `branding/` is held because `maquettiste.json` names its icon there; caches, manifests,
  outputs, `.schema/` and the schema snapshots are not model content and are never held. A description sidecar outside
  `model/` (the loader allows one anywhere under the model root) is not held either.
- **Determinism.** Entries in ordinal path order, then the index, then the metadata; every entry dated 1980-01-01 00:00
  and compressed with `Fastest`. The same documents and metadata give the same bytes (a test compares two repositories,
  and an import of another tool's zip of the same documents writes the same bytes again).
- **`snapshot.json`**: `kind` (`maquettiste-model-snapshot`), `format` (1), `name`, `description`, `author`, `createdUtc`
  (`yyyy-MM-ddTHH:mm:ssZ`), `origin` (`user` or `before-restore`), `published`, `includesPacks`, `modelFormat`, `engine`
  (the release), `modelHash` (over every document's path and hash, the packs aside: the model it was taken from),
  `packsHash` (with the packs), `files`, `elements`, `kinds` (count per kind). It is last, so a rename, a description or the
  published flag rewrites only the archive's tail (`ZipArchiveMode.Update` on the last entry: about 0.2 s on a 40,000-entry
  archive) and the id and documents do not change.
- **Ids** are the file name without `.zip`: the kebab-case name (at most 80 characters, `snapshot` when it has no letter or
  digit) and the UTC time, `<slug>-<yyyymmdd-hhmmss>`, with `-2`, `-3` on a collision; the pattern
  `^[a-z0-9]+(-[a-z0-9]+)*$` (at most 120 characters) is checked before any file is touched. A name that already ends with
  the time (`before-restore-20261005-120500`) is not stamped twice.
- **Writing** reads the documents from disk in chunks of 512, each chunk in parallel, and streams them into
  `.<id>.zip.tmp` beside the final name, then renames it; at most one chunk's bytes are held. Both names are checked with
  `CheckEngineWrite(WriteTarget.Model)`. No new write target was needed: the folder is under the model root.

## 4. Open as of

`SnapshotLibrary.OpenAsync(id)` builds a `ModelStore` over `ZipDocumentStore` with `EngineServices.CreateReadOnly`: the same
options as the live model (so repo paths read the same), every writer given `ReadOnlyPathPolicy`, which refuses every
engine write with MQ6029, and no index cache. The store loads once (the archive is closed after the load and reopened only
for a later read such as the settings), never rescans, and refuses saves, creates, deletes, batches and settings saves up
front with MQ6029; any other write is refused by the guard. A `GenerationService` over it serves the database view, the
resolved model and the template preview; the preview renders with the working model's packs (a snapshot's own packs serve
restore and a later "generate from a snapshot"). The two most recently opened snapshots stay loaded.

**API addressing.** The smallest change that lets the editor show a snapshot read-only: the reads the editor needs take
`?snapshot=<id>` and answer from the snapshot (header `X-Maquettiste-Snapshot: <id>`; 404 when no snapshot has the id):
`GET /api/model/index`, `/api/model/elements`, `/api/model/elements/{id}`, `/api/model/kinds`, `/api/model/resolved`,
`/api/model/references/{id}`, `/api/diagrams/{id}`, `/api/databases/{id}/view`, `/tables`, `/tables/{key}`,
`/api/project/settings`, and `POST /api/model/elements/read`, `/api/validate`, `/api/templates/preview`. The sign-in gate
refuses `?snapshot=` anywhere else: 409 `snapshot-read-only` for a write, 400 `snapshot-unsupported` for another read, so no
request can silently act on the working model while the editor thinks it shows a snapshot. The editor then needs only to
add the parameter to its reads and switch its write affordances off.

## 5. Compare

`CompareAsync(from, to, offset, limit)`, each side a snapshot id or `working`.

- A snapshot's side is its index entry: no document is decompressed. The working side is the store's documents and their
  hashes after a stat rescan; only documents that are not elements, or did not load, are read.
- Elements are matched by id (a duplicate id: the ordinally first path wins, as the loader decides) and compared by hash:
  `added`, `removed`, `changed`, with `previousName` for a rename and `previousPath` for a moved file. Other documents
  (settings, locale shards, extensions, sidecars, branding, and the packs when both sides hold them) are matched by path.
- The answer: totals, counts per kind, one page of elements ordered by kind, name and id (`next` is the next offset;
  default 500, at most 5,000), and up to 1,000 other documents (`filesTruncated`).
- **Per element on demand**: `CompareElementAsync(from, to, id)` reads only that element's two documents and answers with
  both whole (`before`, `after`: what the editor's conflict view renders as a side-by-side diff) and the fields that
  differ as JSON pointers (`added`, `removed`, `changed` with both values): objects by key; arrays of objects with distinct
  string ids matched by id (a reorder alone is one `changed` on the array); other arrays by position when the lengths match,
  else whole; at most 500 (`fieldsTruncated`). Arrays are materialized once, so a seed of 100,000 rows diffs in linear time.

## 6. Restore

`RestoreAsync(id, includePacks)`: refused (`locked`) while a generation run holds the run lock, which the restore then holds
itself; a safety snapshot of the working model (`before-restore-<yyyymmdd-hhmmss>`, origin `before-restore`, holding the packs
when they are restored); the writes are the snapshot's documents whose hash differs from the safety snapshot's row, the
deletes the working documents the snapshot lacks (in `model/`, `extensions/`, `branding/`, `maquettiste.json`, and
`templates/` only when the packs are restored); then `ModelStore.ReplaceDocumentsAsync` applies them all or nothing through
the document store, reloads the whole model and publishes one change set, which the host turns into one `model.changed`.
Nothing is validated: a snapshot is restored as it was taken. The result names the safety snapshot and says, in `undo`, that
restoring it undoes the restore. The packs are restored only when the snapshot holds them and the caller asks.

## 7. Export and import

Export streams the archive as stored. Import (`ImportAsync(stream)`) copies the upload into the cache folder (512 MB at most),
then checks it: a zip; at most 500,000 entries; every entry a safe content path, `snapshot.json` or `snapshot-index.json`
(folder entries are ignored); no name twice, ignoring case; at most 64 MB per document and 4 GB in all; no entry over 1 MB that
expands more than 200 times; a readable `snapshot.json` of format 1 whose model format this release reads (MQ1007). It
rewrites the documents the engine's way (sorted, its own index, decompression bounded by each entry's declared size), loads
the new archive once as an opened snapshot would, and refuses it when a document does not parse (MQ1001), is not canonical
(MQ1003) or names an unsupported format (MQ1007). Then it is stored under a new id; the working model is never touched.
Every refusal is MQ1011 (or the document's rule) with the entry's path.

## 8. Surfaces

| | List | Take | Read, rename, publish | Delete | Compare | Restore | Export | Import |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| API | `GET /api/snapshots` | `POST /api/snapshots` | `GET`, `PATCH /api/snapshots/{id}` | `DELETE /api/snapshots/{id}` | `GET /api/snapshots/compare`, `/compare/element` | `POST /api/snapshots/{id}/restore` | `GET /api/snapshots/{id}/export` | `POST /api/snapshots/import` (`application/zip`) |
| Role | viewer | editor | viewer, editor | maintainer | viewer | maintainer | editor | maintainer |
| CLI | `snapshot list` | `snapshot create` | `snapshot show` | `snapshot delete` | `snapshot compare` | `snapshot restore` (`--apply`) | `snapshot export` | `snapshot import` |
| MCP | `list_snapshots` | `create_snapshot` | | | `compare_snapshots` | `restore_snapshot` | | |

The auth model has no owner role; restore, delete and import take `maintainer`, the role above editor that generation and
pack writes already need. The import body is the one body the sign-in gate takes that is not JSON. New problem codes:
`run-locked`, `snapshot-read-only`, `snapshot-unsupported`. New rules: MQ1011 (an archive cannot be imported) and MQ6029 (a
write against a snapshot opened read-only).

## 9. Timings

On the benchmark model (`bench/Maquettiste.Bench --keep --no-one-shot`: 5,000 entities, 20,000 relations, 26,268 documents,
a 15 MB archive), on a 24-core WSL2 machine, 2026-10-05. In one process over a loaded store (the engine's own cost):

| Step | Time |
| --- | --- |
| create (26,268 documents) | 1.01 s |
| create with the packs (26,336) | 0.96 s |
| list | 0.13 s |
| compare with the working model (100 changed, 10 removed) | 0.39 s |
| compare two snapshots | 0.26 s |
| one element's fields | 0.36 s |
| open as of (load from the archive), then cached | 1.53 s, 0.00 s |
| restore (safety snapshot, 110 documents written, reload, publish) | 1.11 s |
| import (copy, check, rewrite, load once) | 2.22 s |

Through the CLI, each a fresh process (start and JIT, and the model load where the step needs it): `snapshot create` 1.61 s,
`list` 0.20 s, `compare <id>` (with the working model) 1.10 s, `compare <id> <id>` 0.28 s, `restore <id>` (the preview) 1.04 s,
`restore <id> --apply` (50 entities changed) 3.08 s, `export` 0.09 s, `import` 3.54 s. Peak working set of the in-process run,
which holds the working model and one opened snapshot: 1.1 GB. The benchmark's own budgets are unchanged by the refactor
(load 0.59 s against 0.62 s on the same machine before it; the incremental line misses its 2 s budget on this machine before and
after alike, 4.72 s and 4.66 s).

## 10. The editor

Built 2026-10-05 (`src/editor/src/snapshots/`, `src/editor/src/api/snapshotScope.ts`). The owner's rules hold: one picker in
existing chrome (no header row added), dense, a tooltip on every icon button, text tab labels, and nothing heavy in the
background: the list is read when the picker opens, a comparison when it is asked for (in pages of 500, the next page on
**Load more**), an element's two documents when it is picked.

- **Picker** (`SnapshotPicker.tsx`): the project name is its trigger. A search (name, description, author, id), **Take
  snapshot…** (name, description, *Include the template packs* off by default), **Import snapshot…** (a `.zip`, sent as
  `application/zip` outside the JSON client), the working model, then the snapshots newest first with date and time,
  author, element count, description and badges (Published, Safety for `before-restore`, Packs). A row opens the snapshot
  as of; its menu has Open, Compare with working, Compare with…, Restore…, Export (.zip), Rename or describe…,
  Publish/Unpublish and Delete…. The list is not refreshed on a timer (no `snapshots.changed` event yet): it is read each
  time the picker opens.
- **Roles** (`snapshots/state.ts`, from `GET /api/session`): viewer opens and compares; editor also takes, renames, publishes
  and exports; maintainer also restores, deletes and imports. A lesser role sees the action disabled, its tooltip naming the
  role it needs (a disabled button takes no pointer events, so the reason sits on a wrapper).
- **As of** (`api/snapshotScope.ts`): one scope for the page, sourced from the URL's `?snapshot=<id>` (a reload or a link
  keeps it; Back and Forward move between the snapshot and the working model). The HTTP client adds the parameter to the
  fourteen reads of section 4, lets the scope-neutral requests through unchanged (session, project, presence, rule catalog,
  jobs, packs, assistant status, `/api/snapshots/**`), and refuses every other request before it leaves the page with the
  code the gate would answer (`snapshot-read-only`, `snapshot-unsupported`). The query client hashes every model key with
  the scope (`[{snapshot: id}, ...key]`; neutral keys are not hashed with it), so a snapshot's documents never share a cache
  entry with the working model's, and the index has its own loader and ETag per snapshot (never written to the browser's
  index cache). Switching remounts the shell. Leaving a snapshot drops its cached queries and marks the working model's
  stale, so the open views read it again (the index with its ETag); realtime model events are not applied while a snapshot
  is shown.
- **Writes off**: the fields below the top bar take `readOnly` (`components/ui/readOnly.tsx`: Input, Textarea and the code
  editors; search fields stay usable), undo and redo are disabled, the drafts manager refuses an edit with a notice, the
  dialogs that write (new element, new database object, foreign key, part delete, storage, type actions) do not open, and the
  assistant is off. Generate and Settings show "not available for a snapshot". Opening a snapshot first saves pending edits;
  an edit that cannot be saved (invalid or in conflict) keeps the editor on the working model until it is fixed.
- **Banner**: one line in the top bar, *Viewing snapshot <name> (<date>) — read-only*, with Compare with working, Restore… and
  Back to working; a snapshot id that does not exist says so.
- **Compare view** (`CompareView.tsx`): a full-height panel under the top bar. From and To are any snapshot or the working
  model. Elements tab: counts per kind (click to filter), a change filter (added, removed, changed, renamed), a name search
  (over the loaded rows), a virtualized list (`~ Bill (renamed from Invoice)`), and the totals. Other documents tab: paths
  with their change. Picking an element reads `/api/snapshots/compare/element` and shows the fields that differ, then both
  documents in the conflict dialog's Monaco diff, read-only.
- **Restore** (`SnapshotDialogs.tsx`): the confirmation says what happens (safety snapshot `before-restore-…` first, the working
  model replaced, the packs only when the snapshot holds them and the box is ticked, other windows see it, nothing validated);
  the result shows what was written and deleted and **Undo: restore before-restore-…**, which restores the safety snapshot
  (and shows its own undo). `run-locked` reads "A generation run is writing files right now. Nothing changed". From as-of
  mode a restore returns the editor to the working model.
- **Mock** (`src/mocks/snapshots.ts`): as-of reads are answered by a read-only mock backend built from the snapshot's documents,
  through the same handlers (`getResponse`), and any other request carrying `?snapshot=` is refused as the gate does; export
  writes the engine's layout (documents, `snapshot-index.json`, `snapshot.json`) and import reads it back under a new id;
  `?mock=role-viewer`, `role-editor` and `role-maintainer` sign in with that role (403 above it); `__mqMock.runLocked = true`
  makes a restore answer `run-locked`. The mock keeps nothing across a reload.
- **Request size** (measured on the image, 2026-10-05): static-site-hosting 0.4.0 bounds a request body at 536,870,912 bytes
  (512 MiB, the engine's own archive limit), not ASP.NET's default 30,000,000: a 52 MB archive imported in 0.7 s. A body over
  the bound used to surface as 503 `model-unavailable` (the host's reader throws an `IOException`); the import now refuses it
  as 413 with `tooLarge` and MQ1011, from `Content-Length` before reading or from the reader's 413, and the editor shows the
  message. The CLI has no such bound.

## 11. Left for later

- Generating from a snapshot (with its packs), and the http and database providers, the library and per-model roles (phases B
  and C).
- A realtime `snapshots.changed` event, so a second window's list refreshes without reopening the picker.
- In the compare view, a name search on the server (it filters the loaded pages today).
- Snapshots live in `.maquettiste/` and so in the repository unless the project ignores the folder; the product writes no
  ignore file, so that choice is the project's.
