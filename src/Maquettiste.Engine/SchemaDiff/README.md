# SchemaDiff

**Owner:** W8 Formatters and schema diff. See docs/engineering/engine-design.md section 18.

Physical snapshots and the schema differ (engine-design.md section 14). Implemented. The public result records
(`SchemaDiffResult`, `TableChange`, …, in `SchemaDiffTypes.cs`) are unchanged from the scaffold.

- `SchemaDiffer` (`ISchemaDiffer`): `Capture` turns an `RDatabase` into a `PhysicalSnapshot` (tables, views, sequences sorted
  by key; columns by position; constraint lists by key). `Diff` captures the current database and compares it with the
  committed snapshot by key: same key and new name is `Renamed` (with any property changes), a new key is `Added`, a missing
  one `Dropped`, same name with changed properties `Altered`. Every snapshot property is compared; `PropertyChange` names are
  the snapshot's camelCase JSON names and values are plain CLR values (strings, numbers, bools, string lists, and literal
  defaults as `long`/`decimal`/`double`/`string`/`bool`/lists/key-sorted dictionaries; enums as kebab strings).
  Order: `Tables` = added (FK dependency order, key order on ties, cycles broken by smallest key), renamed, altered, dropped
  (reverse dependency order); columns = added and changed in current position order, then dropped; every other list = added,
  renamed, altered, dropped, each by key. Added and dropped tables carry no sub-changes (`Table` holds the new table).
  `Hash` = `H("mq-schema-diff-2", database id, name, dialect, from, to, the previous name and dialect, every table change with
  the table's old and new schema and comment, every change and property value as JSON)`.
  `ToRevision` = `FromRevision + 1` when non-empty; no previous snapshot means revision 0.
  A change of the database's `dialect` or `name` against the committed snapshot makes the diff non-empty (so `--check` reports
  MQ6018 and the revision moves) although `SchemaDiffResult` has no list to report it in; see the signature change requested.
  `Capture` throws `ArgumentException` for an empty or unknown `RDatabase.Dialect` instead of guessing `postgresql`
  (validation requires a dialect, so this is a broken invariant).
- `SnapshotStore` (`ISnapshotStore`): `<ModelRoot>/snapshots/<kebab database name>.json`, canonical JSON through
  `ICanonicalJson` (`snapshot.json` layout). Saves check the target and the temp file with `CheckEngineWrite(WriteTarget.Model, …)`
  (refused: `UnauthorizedAccessException`, nothing written), write a temp file in the folder and move it into place, and skip
  identical bytes. A missing file loads as `null`; an unreadable one throws `InvalidDataException` naming the path.

Deviations and notes:

- The resolved constraint and index types (`RPrimaryKey`, `RUnique`, `RForeignKey`, `RCheck`, `RIndex`) carry no key, so
  snapshot keys are derived from every property that tells two objects of a table apart, never from the name: `pk`;
  `uq:<column keys>`; `fk:<column keys>-><referenced table key>(<referenced column keys>)`;
  `ix:<column keys, " desc" when descending>` followed, only when not the default, by `;unique`, `;using=<method>`,
  `;include=<column keys>` and `;where=<16 hex of SHA-256(predicate)>`; `ck:<16 hex of SHA-256(expression)>`. Only otherwise
  identical definitions can collide; they get `#2`, `#3` ordered by their remaining properties (an FK's actions), then name,
  so two partial indexes on the same columns keep their own keys when one is renamed. Renaming a constraint or index is
  `Renamed`; changing a key-forming property (columns, an FK's target, an index's uniqueness, method, included columns or
  predicate, a check's expression) is a drop plus an add, which is also what most dialects need to apply it. `Altered` is left
  for a primary key's columns and an FK's `onDelete`/`onUpdate`. Renaming one of two fully identical indexes can still show as
  two renames (their names trade keys); applied in order, those renames produce the same schema. The lasting fix is a stable
  `Key` on the resolved types (requested).
  The primary key's `Clustered` is captured (`RUnique` has none, so a unique constraint's is never set).
- `TableChange` has no property-change list, so a table whose only change is its schema or comment is `Altered` with empty
  sub-lists; its `OldSchema` and `OldComment` (init properties) give the old values, and both values reach `Hash`.
- DDL coverage additions: the snapshot's declared `schemas` (compared only when both snapshots have them; `SchemaDiffResult.Schemas`,
  hashed only when non-empty), a column's `defaultName` (`ColumnChange.OldDefaultName` gives the old one), a foreign key's
  `deferrable` (compared as enums, so the unchanged-table check allocates no strings), and a view's `comment`;
  `ObjectChange.OldSchema` for views and sequences. All are additive: a model without them keeps its diff hashes, and only the declared `schemas` are new in the next snapshot saved.
- DDL coverage review (2026-10-03): an index column is a column key or an expression (`SnapshotIndexColumn`; its key part is
  `expr:<16 hex of SHA-256(expression)>`, so a new expression is a drop plus an add) with an optional prefix `length` (in the
  `columns` property's text, `key(n)`); a unique constraint's `nullsNotDistinct`, a column's `identitySeed`,
  `identityIncrement` and `identityAlways`, and a view's `columns` (its column list), `withCheckOption` and `materialized` are
  compared properties. A view's `dependsOn` (the keys of the views it reads) is recorded but never compared: a migration reads
  it from the committed snapshot (`ObjectChange.OldDependsOn`, with `OldMaterialized`) to drop views before the views they read.
- A changed or dropped unique constraint or index carries its column keys and whether it was a key a foreign key can rely on
  (`ObjectChange.OldColumns`, `OldUnique`), from the committed snapshot, so a migration drops the foreign keys that rely on it
  before it drops the key and adds them back after.
- Column reordering is not reported (no property holds a position).
- The snapshot file name uses a local kebab-case helper (`SnapshotStore.Kebab`, the section 9 word rule) because W3's
  `Casing.Words` is still a stub; switch to it once it lands.

Tests: `tests/Maquettiste.Engine.Tests/SchemaDiff/` (resolved objects built directly through internal setters).

## Performance notes (WA, incremental runs with the example packs)

On the benchmark model the `sql-ddl` pack's `usesSchemaDiff` made every run capture, diff and (after an apply with a change)
serialize a 10,005-table snapshot, about 1.3 s of every incremental run, all of it on the run's critical path. Now:

- **One capture per run.** `SchemaDiffer.DiffAndCapture` (internal) returns the snapshot the diff captured; the snapshot saved after
  the apply is that capture with `Revision = ToRevision`, which is exactly what `Capture(database, ToRevision)` returns. `Diff`
  (the `ISchemaDiffer` member) is unchanged.
- **Serialized while the run renders.** `SnapshotStore.Prepare` (internal) produces the bytes `SaveAsync` writes, without I/O, and
  `WriteAsync` writes prepared bytes (engine-write guard on the target and the temp file, identical bytes skipped, temp file moved
  into place). `SaveAsync` is `WriteAsync(Prepare(...))`. The generation run prepares each non-empty diff's snapshot on the thread
  pool right after diffing (apply mode only) and writes it after a successful apply (`Generation/README.md`); the canonical
  serialization (`CanonicalJson`'s node path, about 0.7 s and a lot of allocation for 10,005 tables) no longer delays the end of
  the run. The bytes are the same (`SnapshotReuseTests`, `SchemaSnapshotTests`).
- **Parsed snapshots are reused.** The store keeps, per snapshot file, the last snapshot it parsed and the SHA-256 of the bytes it
  came from; `LoadAsync` still reads the file every time but parses it only when the bytes differ. The generation run prepares
  with `Prepare(snapshot, parse: true, ct)`: once the bytes exist, parsing them back starts as a separate thread-pool task
  (`PreparedSnapshot.Parsed`) that nothing in the run awaits; `WriteAsync` only hands it to the store for the file it wrote. So the
  parse is never on the run's critical path (the save waits for the bytes, not for the parse), and the next run's load of that
  file costs a read and a hash: about 20 ms instead of 100 to 200 ms. A held parse that failed or was cancelled (it checks the
  run's token before starting) is ignored and the file parsed instead. `SaveAsync` does not parse. A file changed by anything
  else (a checkout, a hand edit) is parsed again.
  The cost: a one-shot CLI process, or an apply that ends failed, stale or (before spec-errata E42) over part of the roots, parses a snapshot nobody loads
  (100 to 230 ms of one thread-pool thread beside the render; the graph is garbage once the task ends). Starting the parse only
  after the write avoided that, but was measured worse: the benchmark's same-store incremental run then followed a parse that
  had just allocated the whole snapshot graph, and its median went from 4.17 s to 5.11 s (six interleaved runs each, render and
  GC pauses up), so the parse stays beside the render.
- **Held snapshots are pruned.** A load that finds the file missing drops the file's entry, and `Retain(databaseNames)` (called at
  the end of every run's schema diff with the run's databases, and with none when no pack of the run uses the schema diff) drops
  every other file's, so a long-lived host (watch, server, functions) does not keep a renamed or removed database's parsed
  snapshot (tens of MB for 10,000 tables) until the store goes away. A run limited to packs that do not diff therefore drops them
  too, and the next diffing run parses its snapshot once.
- **Cancellation.** `DiffAndCapture`, `Compare` and `SnapshotCapture.Capture` take a token (internal parameters; `ISchemaDiffer`
  passes `CancellationToken.None`), set it on their `Parallel.For` and check it between capture and compare, and `Prepare` checks
  it before sorting, before serializing and before the parse starts (the canonical serializer and the JSON parse themselves
  cannot be interrupted, so a cancel that lands during one costs at most that step).
- **Unchanged tables are recognized cheaply.** `CompareTable` first compares the two tables pairwise in list order (names,
  schema, comment, and every column, key, constraint and index by key, name and the same property comparisons); only a table that
  differs, or whose lists are merely reordered, goes through the keyed maps. 10,004 of the benchmark's 10,005 tables are unchanged
  after an entity edit.
- **Parallel capture and compare.** With `EngineOptions.EffectiveParallelism` above 1 and at least 512 tables, tables are
  captured, and the unchanged ones found, in parallel; each table goes to its own slot and the lists are then sorted and walked in
  key order as before, so the snapshot, the diff and its hash do not depend on the parallelism.

Measured on the benchmark's main database (10,005 tables; probe runs on the 24-core machine, `--jobs 8`, incremental runs after
one entity edit): load 100 to 230 ms → 20 to 40 ms, capture plus compare about 300 ms → about 40 ms, the save at the end of the
run 650 to 1,500 ms → the write of prepared bytes (the end of the run now follows the last file write by about 0.2 s instead of
0.9 s).
