# Retiring projection: tables made from entities

Status: design, 2026-11-26, revision 3; decisions D1 to D4 confirmed by the owner the same day. Being built. The owner asked for it on 2026-11-26, after the question "why would
we not retire the old projection path's overlay files?". Revision 2 followed the team's answers (section 10) and the
owner's ruling that table per type and lists in a child table are common ways to map entities onto tables and must stay
possible. Revision 3 folds in a Fable review of revision 2 (section 13). Nothing here changes the product until the
owner approves it.

## 1. The recommendation

One release (0.29.0), three pull requests:

1. **Bindings learn every shape projection makes** (sections 4 to 6): table per type (`baseForeignKey`), an attribute
   stored in a child table, as a list or as a single value (`childTables`), value objects nested in value objects,
   and relation attributes stored on a foreign key. Each with rules, resolved records, the SQL the engine renders for
   templates, `csharp-dapper` and the editor's Storage tab.
2. **"Create tables…" proposes every shape and writes what projection used to work out at resolve time** (section 7),
   then our own sample, fixtures and bench model move to designed tables and bindings with it (section 8).
3. **Projection is removed** (section 9), with the guards old files need.

At no point does a version lose a table shape that projection can make today.

## 2. Why

Storage is a warehouse: tables, columns and keys carry their own definition and never point at the model. The model
points down at them: an entity's binding names the table it reads and writes, a relation mapping names the foreign key
or junction table that stores a relation. Tags are notes anyone can add or remove. The resolved model may derive
conveniences from those downward pointers (`boundType` on a column, a foreign key's relation), since no storage file
holds them.

Projection works the other way. A database "by convention" lays out a table for each entity, a child table for each
list, a junction table for each many-to-many relation; an overlay is a table file that points back at an entity, an
attribute or a relation to adjust what projection makes; `sql-ddl` reads an attribute's default and a reference-type
list's storage from the model. All make storage follow the model.

Erratum E43 (2026-10-02) superseded projection for new work with bindings and materialize; the user guide calls it
"older projects"; new databases start empty. What E43 left open is that several shapes projection makes have no
binding form. This design closes that, then finishes the retirement.

Designing from the entity stays. An architect designs tables to fit what the entities need, and "Create tables…" (the
operation `materialize-tables`) proposes them from the entity: it writes ordinary table files and the binding that maps
the entity onto them, once, when asked. What goes is the engine rebuilding tables from entities every time the model
resolves.

## 3. The shapes, before and after

| Shape | Projection today | Binding today | After this design |
| --- | --- | --- | --- |
| One entity, one table | yes | yes | binding |
| One table for a hierarchy | yes (`tph`) | yes, 0.27.0 (`discriminatorColumn`) | binding |
| A table per concrete entity | yes (`tpc`) | yes: each entity binds its own table and maps every attribute | binding (a relation to the base is checked in PR 1) |
| A table per type | yes (`tpt`) | **no** | `baseForeignKey` (section 4) |
| A list in a table of its own | yes (`storage: table`) | **no** | `childTables` (section 5) |
| A single value object in a table of its own | yes (`storage: table`) | **no** | `childTables`, at most one row (section 5) |
| A value object in columns | yes, nested to any depth | one level (`attribute.member`) | field paths of any depth (section 6) |
| A value object or list as JSON | yes, also inside an embedded value object | top level only | field paths of any depth (section 6) |
| Enum as integer or text, column prefix, column names | yes | yes: the designed column says it | binding |
| Many-to-many through a junction table, with its own attributes | yes | the relation mapping names `junctionTable`, `ends` and `attributes[].column` | relation mapping, unchanged |
| A one-to-many relation with its own attributes | yes, columns on the foreign key's table | **no** | relation mapping `attributes[].column` on the foreign-key shape (section 6) |
| Stored derived attributes | yes | yes: a field maps them | binding |
| Overlay extras: columns, indexes, checks, stereotype members | yes | yes: they are the designed table's own | designed table |
| A relation stored as an entity (promoted) | yes | no | removed; modeled as an entity with two relations |

## 4. Table per type: `baseForeignKey`

A derived entity's binding reads its own table, joined to its base's table by a foreign key that the derived table
declares (its primary key referencing the base table's key). Storage already holds that foreign key; the binding
names it.

```json
"bindings": [{
  "id": "…", "database": "db-main",
  "source": "tbl-card-payment",
  "baseForeignKey": "fk-card-payment-payment",
  "fields": [ { "attribute": "attr-card-last4", "column": "col-last4" } ]
}]
```

- **`baseForeignKey`** (optional): a foreign key of the binding's write table (else its source) that references the
  base binding's write table. The base entity's binding to the same database maps the inherited attributes and to-one
  ends, its constants and its type column; the derived binding maps only the entity's own.
- **The key** of the derived binding is the foreign key's columns, in the order of the base's key fields; the derived
  binding maps no field for an inherited key attribute (MQ4046 is satisfied by the foreign key). A key the base's
  insert generates (identity, sequence, default) flows into the derived insert: the engine renders the base insert with
  the dialect's way to return it (`RETURNING`, `OUTPUT`, `LAST_INSERT_ID()`, a sequence read first on Oracle).
- **The engine renders the base table's statements per derived entity.** Inserting a card payment writes the payments
  row with the card payment's own type code (when the base table has a type column) and the base's constants, then the
  card payments row; an update writes each table whose fields changed; a delete removes the derived row first, then the
  base's, or relies on the foreign key's `onDelete: cascade`. A soft delete is the base's: it marks the base row and
  leaves the derived row.
- **Reads** join the derived table to the base's, up the chain for several levels (`C → B → A`), each level a binding
  with its own `baseForeignKey`.
- **Reading the base** returns every row of its table, as for one table per hierarchy, without joining the derived
  tables. Which subtype a row is: the type column, when the base table has one, else the engine renders an "exists"
  probe per derived binding.
- **With a type column on the base** (`discriminatorColumn`), the entities that use `baseForeignKey` count among the
  hierarchy's members for the codes a read keeps and for MQ4122 to MQ4126, as entities in the same table do today.
- **Relations with several types** that today ask whether a derived entity shares its base's rows (`inheritance: tpt`
  on a mapping) read that from `baseForeignKey` instead.
- **Rules (new ids):** the foreign key belongs to the derived binding's write table and references the base binding's
  write table; its columns are the derived table's whole primary key; the entity has a base bound to the same
  database; the base binding writes to a table when the derived binding writes (a base read from a view or query
  leaves the derived binding read-only); no field maps an inherited attribute or to-one end; the derived binding does
  not share its base's table; its `delete` agrees with the base's (both soft, or neither); a binding check that names
  attributes stored in both tables is refused, since a check is lowered into one table.
- **Mixed hierarchies** (some derived entities in the base's table, some in their own) are allowed: each binding says
  which it is.
- **Not general split.** One entity across several tables that are not a hierarchy ("split", on the roadmap) stays
  later. It will reuse the join and the ordered writes.

## 5. An attribute in a child table: `childTables`

An attribute stored in a table of its own: a list (`collection: true`), one row per item, or a single value object, at
most one row. Each row holds the owner's key, the value or the value object's members, and for a list optionally the
item's position.

```json
"bindings": [{
  "id": "…", "database": "db-main", "source": "tbl-party",
  "fields": [ … ],
  "childTables": [{
    "id": "…",
    "attribute": "attr-remit-to-addresses",
    "table": "tbl-party-remit-to-address",
    "ownerForeignKey": "fk-party-remit-to-address-party",
    "fields": [
      { "attribute": "attr-remit-to-addresses.attr-line1", "column": "col-line1" },
      { "attribute": "attr-remit-to-addresses.attr-city", "column": "col-city" }
    ],
    "orderColumn": "col-position"
  }]
}]
```

- **`childTables`** (optional, on an entity binding): one entry per attribute stored in a table of its own.
- **`attribute`**: an attribute of the entity, or an inherited one when the base's binding does not store it.
- **`table`**: the child table. **`ownerForeignKey`**: a foreign key of the child table that references the owning
  table (the write table, else the source), on its primary key or a unique key; with a unique key, the binding maps a
  field for each of that key's columns.
- **`fields`**: paths as an entity binding's fields write them (`attribute.member`, deeper for nested value objects,
  section 6). For a list of a scalar or a reference type, one field whose path is the attribute itself, naming the value
  column.
- **`orderColumn`** (optional, lists only): an integer column holding each item's position, from 0.
- **`constants`** (optional): fixed columns, as on an entity binding, so several attributes can share one child table.
- **`columns`**: the child table's other columns accounted for (`ignored`, `database`, `computed`), as on an entity
  binding; a child row's own generated id is `database`. A column nothing accounts for is the existing warning MQ4047.
- **Order around the owner.** An insert writes the owner first, then its child rows; a delete removes the child rows
  first (or relies on the foreign key's cascade); a soft delete of the owner leaves its child rows.
- **Writes** replace an attribute's rows: delete the owner's rows, then insert the items in order. The engine renders
  three statements per entry (select by owner, ordered; delete by owner; insert one row). A template that wants to diff
  instead has the same columns. A read-only binding (`write: none`) reads its child tables and writes none.
- **Copy.** A copy carries an attribute's child rows with the copied owner (its `onCopy` decides: `copy` copies the rows,
  `clear` and `reset` leave none). Child tables are not parts: `entity.parts` stays about composition.
- **Rules (new ids):** the attribute is the entity's (or inherited), listed once, and not also mapped as a field; the
  owner foreign key belongs to the child table and references the owning table; its columns are not mapped as fields;
  each field path names a member of the attribute's type, each once; the order column is an integer column, and only on
  a list. MQ4058 (an unmapped attribute) keeps skipping list attributes, as it does today; it newly counts a single value
  object that neither fields nor a child table store, as it already does for its members.

## 6. Smaller binding additions

- **Field paths of any depth.** A field's `attribute` may be `a.b.c`, for a value object nested in a value object,
  embedded in columns, or a JSON column holding a nested value object or list. Today paths stop at one level
  (`Materializer` and the field rules refuse three segments).
- **Relation attributes on a foreign key.** A relation mapping's `attributes[].column` (today for junction tables)
  also works on the foreign-key shape, naming columns of the table that holds the foreign key.
- **Entity seeds through bindings.** `sql-ddl` writes an entity's seed rows through its binding's fields (and a
  relation's through its relation mapping), as it writes them through `entity.mappings` today. Without it, entity seeds
  write nothing after the removal.

## 7. "Create tables…" proposes every shape

Today `materialize-tables` works out a table by building a model where the entity is mapped, running the resolver, and
copying the projected table. That resolver path goes, so pull request 2 moves the shape-proposing code out of
resolution into the materializer's own helper, with its own types, run only when someone asks for tables. It then:

- **A hierarchy** (refused today, MQ4055, abstract bases included): one table with a type column, a table per concrete
  entity, or a table per type with each derived table's `baseForeignKey`. A parameter of the operation
  (`hierarchy: "one-table" | "per-concrete" | "per-type"`), asked once per hierarchy in the dialog; default one table.
  An entity mapping's `discriminatorValue` is copied into the binding, so type codes do not change.
- **An attribute stored in a table** gets a child table (named by the existing `childTable` and `orderColumn` patterns)
  and its `childTables` entry; a list may go to a JSON column instead (`listStorage: "table" | "json"`, default table).
- **A many-to-many relation** between two entities it creates tables for gets a designed junction table and a relation
  mapping naming it; a one-to-many relation gets the relation mapping naming its foreign key (MQ4011 otherwise).
- **What `sql-ddl` reads from the model today moves into the table:** an attribute's default becomes the column's
  default; a list of a reference type that `sql-ddl` stores in a template-defined junction table becomes a designed
  table with a `childTables` entry.
- The naming settings it reads (`tableName`, `keyColumn`, `foreignKeyColumn`, `junctionTable`, `childTable`,
  `orderColumn`, `discriminatorColumn`) stay in project settings for it.

## 8. Our own models

- **The reference sample** (about 200 projected entities, 10 entity mappings, 9 overlays, 439 relations, so about 430
  relation mapping files to write): converted with "Create tables…" with the sample's own choices. Its one-table
  hierarchy stays one table; its table-per-type hierarchy stays table per type and proves `baseForeignKey`; a supplier's
  remit-to addresses (the child table `parties_remit_to_addresses`, `position` in its primary key) prove `childTables`;
  a contact's extra phone numbers stay a JSON column.
- **The proof:** generated DDL unchanged byte for byte; the snapshots re-captured after the conversion, then `plan`
  shows no migration; the generated `csharp-dapper` code builds and gate 2 passes; the generated C# changes from mapped
  to bound repositories.
- **The billing fixture** (one overlay, two mappings), the processes and schemas fixtures, the validation fixtures,
  the bench's synthetic model and the editor's mock backend (which re-implements projection): converted the same way,
  goldens regenerated.
- **`seed.mjs`** in the sample's tools stops writing overlays and entity mappings.

## 9. What goes, what stays, and the guards

**Goes:**

- `byConvention` (`all`, `packages`) and `packages` on a database. `"byConvention": "none"`, which the editor has
  written on every new database, still loads and means what every database then means; the formatter drops it.
- The entity half of a mapping element: `entity`, `table`, `schema`, `ignore`, `inheritance`, `discriminatorValue`,
  and the entity uses of `attributes` (`storage`, `prefix`, `ignore`).
- Overlays: a table file's `origin`, `entity`, `attribute` and `relation`. Every table is a designed table.
- The relation shape `promoted`.
- `sql-ddl` reading an attribute's default (`attribute.default_expression`) and a reference-type list's storage
  (`template_defined`) from the model; `_lifecycle` and `_reference` reading `entity.mappings`.
- In the resolved model: `entity.mappings` and its column map, `isPromoted`/`promotedFrom`, a table's `origin`,
  `entity`, `attribute`, `isJunction`, `isLookup`, and a link table column's `attribute`.
- The rules that only judge projection (MQ4007, MQ4012, MQ4013, MQ4014, MQ4054, MQ4079, the entity mapping options),
  their ids not reused.
- The Settings › Conventions panel "Older projects", and the overlay mode of the table screens.

**Stays:**

- Designed tables, views, queries, routines; entity bindings, with the additions above.
- Relation mappings: `relation`, `shape` (`foreign-key`, `junction`), `foreignKey`, `foreignKeyEnd`, `junctionTable`,
  `ends`, `constants`, `attributes[].column`, and the storage of relations with several types (0.22.0). MQ4004 stays for
  two mappings of one relation. In the resolved model `relation.mappings` stays, and a foreign key's `relation` and
  `end`, derived from the relation mapping.
- `materialize-entities`, `-attributes` and `-columns` (tables to entities), and `materialize-tables` as in section 7.
- Reading snapshot aliases, for any model materialized before 0.29.0 that has not taken a snapshot since.

**Guards for old files:**

- A file with a removed key does not load, with an error that names what replaces it, as MQ4127 did for queries.
- **A database file with no `byConvention` and no `packages`** laid out a table for every entity before 0.29.0. After
  the removal it would load with no tables of its own, and the next migration would drop them. It is an error at load
  ("this database laid out tables by convention before 0.29.0; add `\"byConvention\": \"none\"` once every entity is
  bound"), unless the database's entities are all bound.
- **Baselines and older snapshots** are model files with removed keys. The migration comparison reads them with the
  removed keys dropped, so a baseline taken before 0.29.0 still compares (today a file that does not load drops out,
  MQ4109).

## 10. The team's answers (2026-11-26)

Asked whether they still rely on projection: no. Every entity they have is bound; their mapping files are all relation
mappings naming a foreign key; no table is an overlay or names an entity; they use no inheritance, no list attributes
and no promoted relations today. One of their templates reads `entity.mappings` and `relation.mappings` in two branches
that produce nothing for their model; they will delete both in the upgrade.

The removal does not affect them, provided their database files load (section 9, guards; question 1 below). The shapes
in sections 4 to 6 are not built for them: they are common ways to map entities onto tables, which any team designing
entities or binding to an older database expects (the owner, 2026-11-26).

Questions for them, from the review:

1. Do your database files have `"byConvention": "none"`, or no `byConvention` at all?
2. Do any of your seeds target an entity or a relation (rather than a reference type or a table)? Their earlier answer
   says no; this confirms nothing else relies on entity seeds through mappings.

Their answers (2026-11-26): their database file has `"byConvention": "none"`, so it loads unchanged and the guard in
section 9 does not touch it; no seed targets an entity or a relation, so entity seeds through bindings (section 6) are
for other teams, not a fix for them.

## 11. Estimate

The review put revision 2's 9 to 10 days at 13 to 16; revision 3 adds child tables for single value objects, field paths
of any depth, relation attributes on a foreign key and entity seeds through bindings.

| Pull request | Work | Time (agent, wall clock) | Reviews |
| --- | --- | --- | --- |
| 1 | `baseForeignKey` (keys, generated keys and base statements in five dialects, type codes, several types), `childTables`, deep field paths, relation attributes on a foreign key; rules, resolved records and contract, copy, `csharp-dapper`, Storage tab sections, docs | 6 to 7 days | 2 Fable rounds |
| 2 | The materializer's own shape helper; hierarchies, child tables, junctions, relation mappings, defaults, reference-type lists; entity seeds through bindings in `sql-ddl`; convert sample (about 430 relation mappings), fixtures, bench, editor mocks; regenerate output | 4 to 5 days | 1 to 2 Fable rounds |
| 3 | Removal across engine, contract, packs, editor, docs, tests; the guards; Version 0.29.0 | 4 to 5 days | 2 Fable rounds |

About 14 to 17 days in all, then one release. Pull request 1 is the size of 0.27.0 and 0.28.0 together.

## 12. Decisions for the owner

All confirmed by the owner on 2026-11-26, as written below.

- **D1. Names.** On an entity binding: `baseForeignKey`; `childTables` with `attribute`, `table`, `ownerForeignKey`,
  `fields`, `orderColumn`, `constants`, `columns`. On `materialize-tables`: `hierarchy` and `listStorage`. Each is a
  permanent, optional addition. (`lists` was dropped: it is already a SQL option, `lists: expand|any`; `baseKey` read as
  "the base's key".)
- **D2. Relation mappings stay,** gaining `attributes[].column` on the foreign-key shape. Recommended: they point down
  and already serve bound entities.
- **D3. Promoted relations are removed,** not converted: no team or fixture uses them, and an entity with two relations
  says the same.
- **D4. A child table's rows are replaced on write.** Recommended: simple and the same in every dialect; a template can
  still diff.
- **D5. One release,** 0.29.0, after pull request 3 (the owner, 2026-11-26). A team with an older projected model then
  converts on 0.28.0 (plain entities) or by hand; the guard in section 9 stops a silent loss of tables.

## 13. The Fable review of revision 2 (2026-11-26)

What it found, and where revision 3 answers it:

- "Create tables…" runs the resolver's projection, so the removal must first move that code into the materializer:
  section 7, estimate.
- The editor writes `"byConvention": "none"` on new databases, and a database with no key laid out every entity: both
  handled in section 9's guards.
- Table per type: the key, generated keys, the base's type code and constants, mixed hierarchies with a type column,
  several types, base binding shapes, checks across tables: section 4.
- Child tables: order around the owner, unique keys, a child row's own id, field path syntax, MQ4058, copy: section 5.
- Shapes with no binding form (a single value object in a table, nested value objects, JSON inside embedded value
  objects, relation attributes on a foreign key, entity seeds): sections 5 and 6.
- Storage following the model in `sql-ddl` (attribute defaults, reference-type list tables): moved into the tables by
  section 7.
- Removal list: MQ4004, `attributes[].column` and `foreignKeyEnd` kept; type codes copied from mappings; baselines read
  tolerantly; snapshot aliases kept for reading: section 9.
- Names: `lists` clashes with a SQL option; `baseKey` reads as the base's key: D1.

Read differently: a foreign key's `relation` in the resolved model is derived from the relation mapping, which points
down; no storage file holds it, so it stays, like `boundType`.
