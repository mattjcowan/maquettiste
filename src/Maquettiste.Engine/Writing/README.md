# Writing

**Owner:** W7 Writer. See docs/engineering/engine-design.md section 18.

The path policy, the writer (stage 8), the manifest, the run journal, the run lock and unified diffs (engine-design.md section 12).
Everything here is implemented; there are no stubs left.

| File | Implements |
| --- | --- |
| `OutputPathPolicy.cs`, `FileSystemPaths.cs` | `IOutputPathPolicy` (§12.1) |
| `OutputWriter.cs`, `WriteRun.cs`, `AtomicFile.cs`, `ManifestHashes.cs` | `IOutputWriter` (§12.3, §12.4) |
| `ManifestStore.cs`, `ManifestSetData.cs` | `IManifestStore` and the data behind `Pipeline.ManifestSet` (§12.2) |
| `RunJournal.cs` | `IRunJournal` (§12.4) |
| `RunLock.cs` | `IRunLock` (§12.4, host-contracts 28) |
| `DiffGenerator.cs` | `IDiffGenerator` |

Tests: `tests/Maquettiste.Engine.Tests/Writing/`.

## Path policy

- `Check(path)`: relative, `/` separators, non-empty segments, no `.`/`..`, no drive, UNC or leading `/`, no `<>:"|?*` or control
  characters, no segment ending in `.` or space, no reserved device name (`CON`, `con.txt`, `LPT1`…), no `.git` or `.maquettiste`
  segment (any case); under an `outputs.allow` root (longest wins; an allow path that fails these rules, such as `../x`, is
  ignored; `""` or `.` means the whole repo); no `outputs.deny` match; then the real path of the root must stay in the real repo
  root and the real path of the target (every existing ancestor and the target itself, links followed up to 40 hops) must stay in
  the root's real path. Refusals carry `MQ6004`. The real paths of existing folders are cached per policy instance (one run).
- Deny globs: `*` and `?` stay within a segment, `**` spans segments (`**/` also matches zero folders), a leading `/` is ignored, a
  trailing `/` means the folder and everything under it. A path is denied when the glob matches it or any of its ancestor folders.
  Matching is case-insensitive (denying more is the safe side). Globs match the whole repo-relative path, not a file name at any
  depth: write `**/*.pem`, not `*.pem`.
- `CheckEngineWrite`: `Model` under `ModelRoot`, `Cache` under `CacheDirectory` or `JournalDirectory`, `Setup` exactly
  `.gitignore`, `.git/hooks/post-checkout` and `.git/hooks/post-merge`; the path must be absolute and strictly inside its folder,
  and links are resolved on both sides (a link cycle or an unreadable link is an `MQ6004` refusal, never an exception). `Output` maps the path to repo-relative and runs `Check`. A policy built with
  `null` settings (`EngineServices.EnginePaths`) refuses every `Check`.
- `MQ6005` (duplicate and case-colliding paths) needs the whole run, so the writer reports it, not the policy. The first claimant
  to reach the writer is written and later ones are refused, so when two units of one pack collide, which one wins follows
  arrival order (see Deviations).

## Writer

- The intake loop reads processed units in order; for each file it runs the policy, the root selection (`Check` mode forces
  committed roots), the plan's path list (`WriteContext.PlannedPaths`), the `MQ6005` claim table (skipped units' outputs claim
  their paths first) and the regions-on-built-root rule (`MQ6015`, unless the unit already carries it), then queues the file on a
  bounded channel (256 files) drained by `min(EngineOptions.MaxDegreeOfParallelism, 8)` tasks.
- Decision per file is §12.3. Owned files are `once` files and companions (or an `o:` manifest hash); their manifest hash gets the
  `o:` prefix if the post-processor did not add it; a kept owned file keeps its old `o:` hash (or `o:` + the disk hash when it was
  untracked). Hand edits: `fail` → `Conflict` + `MQ6009` error, `overwrite` → `HandEdited` and written + `MQ6009` warning,
  `skip` → `HandEdited`, not written, old entry kept + `MQ6009` warning. The `r:` comparison hashes the disk file's skeleton
  (`ManifestHashes.Skeleton`: a line with `maquettiste:keep` followed by a space or tab opens a region, the next line with
  `maquettiste:end-keep` closes it, the body lines between are dropped, an unclosed region runs to the end of the file).
- Writes: `<dir>/.<name>.mq-<runId>-<n>.tmp`, then `File.Move(overwrite: true)`; a failure deletes the temporary file and
  leaves the target as it was. Identical bytes are never written. The journal `write` line is appended after the rename.
- A pack closes when `UnitCountByPack[pack]` processed units have arrived (the count is of `ProcessedUnit`s the writer will
  receive; a larger count, for example one that includes skipped units, just closes the pack at the end of the stream), or at
  the end of the stream. The run's packs are the keys of `UnitCountByPack`, the packs of skipped and arriving units, and with
  `AllPacks` every pack that has a manifest. Closing a pack: find orphans (entries of the pack produced by no unit this run;
  entries of skipped, failed and partly-refused units are kept; an entry an earlier pack produced this run is dropped without
  touching the file; an orphan outside the selected roots is kept; an orphan the policy or the plan now refuses is kept with
  `MQ6004`), then in apply mode save both manifest buckets (a bucket whose content is unchanged is not rewritten) with the
  orphans still listed, save unit states, and append the journal's `pack` line.
- Orphans are deferred to the end of the stream, because a later pack may still produce the path (a file moving from an earlier
  pack to a later one is then neither deleted nor rewritten, and dry run and apply agree). Once every pack is closed, an orphan
  that some pack produced, or that on a case-insensitive disk is the same file as a path produced under other case, is dropped
  without touching the file; the others are deleted or kept per §12.3. In apply mode emptied folders are pruned, and each pack
  that lost an entry saves its manifests again and appends a second `pack` line. A run stopped before this step leaves those
  orphans listed in the manifest; the next run handles them.
- Case-only renames (`db/Invoice.sql` becoming `db/invoice.sql`): when a produced path has no exact entry but the disk resolves
  it to the file of an entry of the same pack spelled with other case (checked by listing the folder, so only on a
  case-insensitive disk), that entry is `M` for the decision, so the file is not an untracked file in the way. In apply mode,
  unless the file is owned or kept by a conflict or skip, the file name is respelled in two renames (to a temporary name, then
  to the target) before any write; folder segments keep their on-disk case. On a case-sensitive disk the two names are two
  files, handled as an add and an orphan.
- Unit states (apply only): skipped units keep their `Previous`; a rendered unit gets a new `UnitState` (sorted read keys, outputs
  with the manifest hash, length and mtime after the write) unless it failed, had a conflict or skipped hand edit, or had a file
  refused or outside the selected roots, in which case its previous state (if any) is kept, so it renders again next run.
- Cancellation is observed between files: queued files are dropped, a file being written is finished and journaled, no pack is
  closed after the cancellation, and `OperationCanceledException` is thrown. An I/O failure in any file stops the run the same way
  and is rethrown. Either way the journal has no `end` line, so the next run resumes.
- `WriteSummary.Changes` lists every non-`Unchanged` decision, orphans included, sorted by path; `Written` and `Deleted` count disk
  operations only (both 0 in dry run and check). Diagnostics are sorted by (path, rule, message).

## Manifest, journal, lock, diff

- Committed manifests at `<ModelRoot>/manifest/<pack>.json`; built ones at `<JournalDirectory>/manifest/<pack>.json` (by default
  `.maquettiste/.cache/manifest/`, as in §12.2; a custom `JournalDirectory` moves them, since they are `Cache` writes). The head
  (`$schema`, `pack`) comes from `ICanonicalJson.Write`; entries follow, one per line, sorted by path in ordinal UTF-8 (code point)
  order. Strings are escaped as the canonical writer does (supplementary characters become escaped surrogate pairs). A manifest
  that is not valid JSON (git conflict markers) is salvaged line by line; the first occurrence of a path wins.
- `ManifestSet` forwards to `ManifestSetData`. A journal overlay entry for a path no manifest holds has no root kind, so it is in
  neither `Entries(pack, true)` nor `Entries(pack, false)` (but `TryGet` finds it); the writer classifies it through the path
  policy and saves it into the right bucket when the pack closes, even when that bucket is outside the selected roots.
- Journal: the orchestrator (W6) calls `ReadUnfinishedAsync`, `BeginAsync` and `EndAsync`; the writer records files and packs.
  `BeginAsync` replaces an unfinished journal but carries its `write`/`delete` lines for packs without a `pack` line into the new
  one, so a resume that is interrupted again still resumes. The replacement is written to a temporary file in the journal
  folder, flushed to disk and renamed over the old journal, so a crash never leaves the carried lines only in memory.
  `EndAsync` writes `end` and deletes the file, except when `write`/`delete` lines remain for packs that got no `pack` line in
  this journal (packs of an earlier interrupted run that this run did not cover): then the journal is replaced, the same durable
  way, by `begin` and those lines, without `end`, so the next run still overlays them (host-contracts 33). Appends are
  serialized and never cancelled (a file already written must be recorded). A torn last line is ignored when reading.
- Run lock: `JournalDirectory/run.lock` opened with `FileShare.None` (an OS lock; the file is left in place); `wait` polls every
  100 ms and observes cancellation.
- Diff: GNU-style unified diff (`--- a/<path>`, `+++ b/<path>`, `\ No newline at end of file`); common prefix and suffix trimmed,
  Myers for the middle, and a middle needing more than 2,000 edits is shown as one replacement to bound memory.

## Performance notes (WP, gate 1)

- Closing a pack walks every manifest entry of the pack (100,000 in the synthetic benchmark). An entry kept by this run (a
  skipped, failed or partly refused unit, or one outside the selected roots) whose path the policy's lexical rules put under a
  root of the entry's own bucket (`OutputPathPolicy.LexicalRoot`: lexical checks, `outputs.allow`, `outputs.deny`, no disk) is
  kept without the symbolic link check: whatever that check would say, the bucket the entry is saved in is the same. Every other
  entry (orphan candidates, journal-only entries, a root kind that changed) still goes through the full `Check`. This took the
  incremental write stage from about 2 s to about 0.4 s: `Check` read links for every path.
- The pack's next manifest entries and unit states are collected in hash maps and sorted once when saved; the manifests and the
  unit states are then built and saved concurrently, both before the journal's `pack` line. The previous unit states are read
  only when a unit keeps its previous state.
- `LexicalError` walks segments over the span without splitting (no allocation per path).
- Manifests: entries that arrive sorted are not sorted again; `Utf8OrdinalComparer` compares UTF-16 units up to the first
  difference and falls back to runes only when a surrogate is there; printable ASCII other than `"` and `\` is written without a
  JSON writer (the relaxed encoder writes it unchanged); a loaded pack's entries are gathered in a hash map and turned into the
  sorted map in one step. The file bytes are unchanged (`ManifestStoreTests`).

## Deviations and notes

- `EndAsync` does not delete the journal while it still holds lines of packs this run did not complete (see above); §12.4 says
  the file is deleted after `end`. W6 needs nothing new: its next run finds the unfinished journal and overlays it as usual.
- Duplicate and case-colliding outputs (`MQ6005`, an error): the first claimant to arrive is written. Across packs arrival order
  is the pack order; within one pack it is the order units reach the writer, which §4.3 does not fix, so which of two colliding
  units of one pack is written can vary between runs. Making it order-independent would need either buffering every write of a
  pack until the pack closes (against the streaming design) or undoing a write already done; the run fails with `MQ6005` either
  way.

- `Pipeline/Support.cs` (scaffold-owned) was changed minimally so `ManifestSet` forwards to `Writing.ManifestSetData`
  (an internal constructor and an internal `Data` property); the public members are unchanged.
- W8 must compute `r:` hashes with the same skeleton rule as `ManifestHashes.Skeleton` (or call it), otherwise every regions file
  looks hand-edited.
