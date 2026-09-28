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
  `RPrimaryKey` and `RUnique` have no `Clustered`, so snapshots hold none.
- `TableChange` has no property-change list, so a table whose only change is its schema or comment is `Altered` with empty
  sub-lists (the old value is not reported, but both values reach `Hash`).
- Column reordering is not reported (no property holds a position).
- The snapshot file name uses a local kebab-case helper (`SnapshotStore.Kebab`, the section 9 word rule) because W3's
  `Casing.Words` is still a stub; switch to it once it lands.

Tests: `tests/Maquettiste.Engine.Tests/SchemaDiff/` (resolved objects built directly through internal setters).
