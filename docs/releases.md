# Releases

What each version added, newest first. Every version is a tag on the repository, an image
(`mattjcowan/maquettiste:<version>`) and the `Maquettiste.Cli` and `Maquettiste.Engine` packages of the same number. The
[getting started](getting-started.md) page and the [update steps](getting-started.md#updating) say how to move to a new
one.

## 0.12.0 (unreleased)

- **Stereotypes that contribute structure.** A stereotype that applies to tables can carry `columns`, a `primaryKey`,
  `uniques`, `foreignKeys`, `checks` and `indexes`, and every table that carries it inherits them, designed, imported or
  synthesized. An inherited member's id is `<tableKey>.<memberId>`, which seeds, keys, indexes, binding fields and
  queries use like any id. Constraint names come from the naming conventions per table. A member foreign key's target
  is a fixed table or a table each carrier names in a stereotype parameter. A synthesized table that stores a
  stereotype's attributes skips its members, so `audited` keeps serving entities through its attributes. sql-ddl renders
  inherited members as it renders own ones, with no template change. The
  [user guide](user-guide.md#stereotypes-that-contribute-structure) has the details.
- **Default stereotypes.** A database schema entry and a package can list `defaultStereotypes`, applied to every
  element they hold in one line: `{ "name": "billing", "defaultStereotypes": ["audited"] }` gives `audited` to every
  table of schema billing. A default skips the kinds it does not apply to. An element drops a default with
  `excludeStereotypes`. Group, requires and conflict rules judge defaults with the element's own stereotypes, and
  templates read where each stereotype came from in `stereotypes[].source` (`own`, `implied`, `schema-default`,
  `package-default`); `has_stereotype` and `where: { stereotypes: [...] }` see defaults.
- **Exceptions per table.** `excludes` drops inherited members by id, and `redefines` changes a few facets of one
  member (nullable, defaults, comment, collation; a key's columns or name; an index's predicate, method or storage) and
  keeps its id, so the table goes on following the stereotype for everything else.
- **Fold into a stereotype.** The batch operation `fold-into-stereotype` (MCP `apply_batch`, `maquettiste model
  apply`) makes tables that already hold a stereotype's columns, keys and indexes inherit them instead: the own copies
  leave the table files, every reference moves to the keyed ids, differences become `redefines` (with `redefine`) and
  missing members `excludes` (with `excludeMissing`). The write records one `rekey` per database, which now also lists
  the index, unique, foreign-key and check ids it moved, and the next migration holds no statement for it. The [schema
  changes guide](guides/schema-changes.md#fold-columns-into-a-stereotype) explains it.
- Migration facts follow members: renaming a stereotype's column records one complete `rename` for each table that
  carries it and changes no table file; removing one records a `drop` per table. `materialize-tables` keeps inherited
  members inherited and moves the overlay's `redefines`, `excludes` and `excludeStereotypes` into the designed table.
- MCP `get_database_view` and `get_resolved_model` give every inherited column, key, foreign key and index a
  `memberOrigin` (null for own members), and a synthesized table without an overlay now lists the stereotypes defaults
  give it. `get_references` on a stereotype member lists every table and file that names a copy of it, and deleting a
  stereotype lists its member links in the delete plan.
- New rules check members (MQ2024, MQ2025 for a key, index or check naming a column the stereotype lacks, MQ2026 for a
  member foreign key's target), `redefines` (MQ2027, and the warning MQ4078 for a facet equal to the inherited one),
  `excludes` (MQ2028), `excludeStereotypes` (MQ2029, and the warning MQ2030 when it excludes nothing), default lists
  (MQ2031), two primary keys on one table (MQ4076), a refused fold (MQ4077) and a NOT NULL inherited column that rows
  written through an entity cannot fill (MQ4079). MQ2013 now names the schema or package that gives a clashing default.
  MQ4003 also reports a duplicate column name in a synthesized table, MQ4008 suggests the keyed id where a bare member
  id is written, MQ2011 refuses a bare member id under `of: "$element"`, and MQ4106 takes the constraint entries of a
  `rekey`.
- Objects that belong to a package now depend on every package of its chain, so a package that gains a default list
  renders its members' units again. Unit state was already invalidated by the engine version; no output changes.
- **Columns typed by reference types, enums and scalar types.** A table column's `typeRef` names the type whose values
  it holds, beside its `type`, which stays the physical keyword: `"type": "string", "typeRef": { "ref": "<id>" }`.
  Designed, imported, extra and stereotype columns take it. A facet the column leaves out comes from the type, and a
  column typed by a reference type gets what an attribute of that type gets: sql-ddl points it at the bound table or
  the lookup table, or gives it a CHECK (on PostgreSQL a `native` type is a CHECK here), unless the table declares that
  foreign key itself. csharp-dapper reads such a column into a query row as the enum, or as the code's type with a
  summary naming the reference type. `reference_type_usage` lists the columns beside the attributes, templates read
  `column.type_ref`, and `materialize-tables` and `materialize-columns` keep the logical type on the columns they
  write. Typing a column by a type stored as a lookup table, a CHECK, a native type or a bound table records a
  `change-type` that asks how values that are not codes convert. The [reference data
  guide](guides/reference-data-and-seeds.md#11-type-a-column-of-your-own-table) walks through it.
- **Sensitive columns.** A column's `sensitive` (`pii` or `secret`) is said once, on the column or on the attribute a
  binding maps to it, and both sides show it: `attribute.sensitive` and `column.sensitive` now give the borrowed value
  when there is no own one. A column's own `secret` also checks its literal default for credentials (MQ3017).
- New rules check where `typeRef` may sit (MQ4082), that the column can hold the type's values (MQ4083), that a
  declared foreign key from a typed column points at the type's bound table (MQ4084), that the two sides state one
  sensitivity (MQ4085, a warning), and that seed cells, defaults and binding constants written into a typed column are
  its codes (MQ7014).
- **Bindings that record their provenance and drift.** A binding to a designed or imported table now records its
  `provenance`: two short hashes per column, one of the name and one of the type, never a copy of the table and never a
  date. Every materialize operation writes it, and so do the new batch operations `map-by-name` (unmapped attributes to
  free columns of the same name, ignoring case and separators) and `resync-binding`. When the table changes,
  `maquettiste binding drift` and the MCP tool `get_binding_drift` list per binding the columns added, renamed, retyped
  and removed since, from files only; `--check` exits 2 on drift. MQ4087 (a mapped column renamed or retyped) and MQ4088
  (provenance that does not describe the source) join MQ4047, which is unchanged, and all three take the severity
  `validation.rules` gives them. `maquettiste binding resync` (or `resync-binding` through `apply_batch` and
  `preview_materialize`) catches up in one batch: an attribute whose name followed its column takes the new name and
  keeps its id (`--keep-names` keeps every name), a retyped column retypes its attribute unless its type still fits,
  added columns get attributes, and removed columns lose their fields; a refused operation is MQ4089.
  `materialize-entities` and `materialize-attributes` type an attribute by its column's `typeRef`, and a foreign key
  into a reference type's bound table makes no relation. An attribute with no description of its own shows its bound
  column's comment, named by `descriptionColumn` in the resolved model, so when billing's `notes.body` column carries a
  comment, `InvoiceNote.body` is documented with it in generated code. The [bind entities
  guide](guides/bind-entities.md#7-keep-a-binding-in-step-with-its-table) walks through it.
- **Seeds by natural key.** A table seed can list a foreign key of its table in `columns`, in place of the key's own
  columns, and give in each cell the referenced row's natural key: `"ada@example.com"`, or `["ACME", "EU"]` for a key of
  several columns. The engine fills the foreign key's columns with the referenced row's values, so no seed file has to
  repeat a surrogate id. Where the database fills the referenced value, sql-ddl writes a lookup on PostgreSQL,
  `(SELECT id FROM sales.customers WHERE email = 'ada@example.com')`, and inserts rows that look up rows of their own
  table by level; other dialects get `NULL` with a TODO comment (MQ4056), and seed-data lists such cells under
  `unresolved`. Templates read `seed_column.foreign_key` and `data_row.refs`. A natural-key cell is data, not a link:
  deleting the referenced row is not refused, and validation reports the cell (MQ7111).
- **Stable ids.** A `uuid` column the database does not fill can say `"stableId": true`, on a table file or a
  stereotype. Every row the table receives (table seed rows, a bound reference type's rows, entity rows through a
  binding) then gets a UUID version 5 derived from the table's id and the row key, which PostgreSQL's
  `uuid_generate_v5` and most UUID libraries compute the same way, so ids survive rebuilds and agree across
  environments. Templates read `column.stable_id`, and the new helper `stable_id <table> <key>` gives the id of a key;
  `stable_id` is now a reserved helper name (MQ6013). Changing `stableId` or a seed's row key changes the ids of rows
  already in databases.
- **Rows computed by a script.** A seed can take its rows from `rowsFrom: { "script": "seeds/<name>.js" }`, a file
  under `extensions/seeds/` that calls `maquettiste.rows((model, seed) => [...])` once and reads the model as a script
  rule does. Row ids are derived from the seed and the row key, so they are the same on every run. The script runs only
  when something reads its rows, its result is kept until the script, the seed or something it read changes, and a unit
  that read the rows renders again exactly then. The extension file tools write rows scripts (kind `seed-rows`) and load
  them without running them.
- **Seed preview.** `maquettiste seed preview <seed>` and the MCP and assistant tool `preview_seed_rows` (73 MCP tools
  now) give one seed's rows, as written or scripted, or with `--database` (`database`) as that database receives them:
  stable ids, the values natural keys find, and each foreign-key entry's ref. Only the scripts of that seed and of the
  seeds it names run. `seed export` and `export_seed_csv` head a foreign-key column with the foreign key's name and
  write a composite key as JSON array text, and export a scripted seed's rows; `seed import` and `import_seed_csv`
  refuse a scripted seed. The [reference data
  guide](guides/reference-data-and-seeds.md#12-name-a-referenced-row-by-its-natural-key) walks through all three.
- New rules MQ7124 (a stable id on a column that is not a `uuid` or that the database fills), MQ7125 (a row key that
  cannot derive the stable id), MQ7126 (a misplaced foreign-key entry), MQ7127 (a rows script that fails) and MQ7128 (a
  scripted row without a row key). MQ7107, MQ7108, MQ7109, MQ7111, MQ7116, MQ5005 and MQ4056 cover foreign-key entries,
  stable id columns and rows scripts too.
- **Rename, retire and merge codes.** Four batch operations change reference data that databases store:
  `rename-code` gives a row a new code, `retire-code` removes a row and moves its uses to another code,
  `merge-reference-types` moves every use of one type to another (mapping codes with `codes`, moving the rows the
  target lacks with their ids and translations) and `rename-reference-type` renames a type. Each goes through MCP
  `apply_batch`, `maquettiste model apply` and the editor's batch endpoint, and a dry run lists what it rewrites
  (`usage`: uses, rows moved and removed, translations removed) and the facts it would record. A merged type's seeds
  whose rows all matched or mapped go with the type. The [reference data
  guide](guides/reference-data-and-seeds.md#15-rename-retire-or-merge-codes) walks through all four on billing's
  invented `PaymentTerm`.
- **Former codes.** A refactor keeps the old code on the type, `formerCodes: [{ "code": "net30", "replacedBy": "<row
  id>" }]`, so a seed import skips a row that names it, `names-to-ids` turns it into the replacing row's id,
  `get_references` on a row lists the former codes that point at it, and templates read
  `reference_type.former_codes`. csharp-dapper writes an `[Obsolete]` field per former code that holds the replacing
  row. A code never comes back: renaming to a live or former code is refused.
- **The value map is recorded and applied.** Each refactor records one `value-map` fact per database that stores the
  type's codes, naming the columns and the template-defined collections that hold them (`collections`, new), the
  merged-away type (`fromReferenceType`, new) and the type's names and plural names (`pluralName`, new in a fact's
  states). On PostgreSQL sql-ddl's migration maps the stored codes before its schema statements: it renames the lookup
  table or enum type of a renamed type, makes the target's rows exist, renames enum values in place where it can,
  maps each column and collection between a drop and a re-add of its foreign key or CHECK, and adds the ledger row; the
  step can run twice. A mapped value an enum type lacks stops the migration with the codes named, since PostgreSQL
  cannot use an enum value in the transaction that adds it. Other dialects get a TODO line. A column whose reference
  type changes without a value map gets its foreign key or CHECK rebuilt (and an enum column its new enum type) on
  PostgreSQL instead of a TODO line. The [schema changes guide](guides/schema-changes.md#codes-that-change) has the
  step per storage strategy. The ledger is unchanged.
- New rules check former codes: a live, repeated or ill-fitting one (MQ7017), a `replacedBy` that is not a row of the
  type (MQ7018), and a default, allowed value or seed cell that names one (MQ7019); MQ7020 refuses a refactor that
  cannot apply. MQ4113 refuses an open value map that maps away a live code, MQ4114 warns when a plain save changes a
  kept row's code or a type's name, and `validate --against` reports such a change that no value map explains
  (MQ4112). MQ4106 and MQ4103 cover value maps.
- **Operations, first cut.** A new kind, `operation`, models one command over the model, in a file under
  `model/operations/` (in a folder per subject): its subject entity (one record or the whole collection, or none),
  whether it reads or changes data, its input members, its outcomes (each completing now or later, and returning
  members, records of an entity or a list of either), its errors (a stable `code`, the name when it has none, and the
  input an error points at), the process events it fires and what it requires. Rules MQ3023 to MQ3028, MQ3030 and MQ3031
  check them. The editor lists operations under their domain and opens them with General and JSON tabs. Templates get
  the scope `each operation` (`type_of` maps a member), and csharp-dapper's new `operation-contracts` unit writes each
  operation's request, result, error codes and handler interface. MCP `get_model_index` rows carry an operation's
  `subject` and `effect`, and `get_resolved_model` has the scope `operations`. Errors shared from stereotypes, file
  members, paging, the HTTP endpoint template and an operation screen with grids come later.
- **Custom kinds.** A project can declare kinds of its own, one file each in `extensions/kinds/<type>.json` with a
  display name, a plural name and a JSON Schema per content key; its elements are `kind: "custom"` files under
  `model/custom/<type>/` that hold a `content` object the schema checks (MQ1017, MQ1019, MQ3033). An `x-member` array
  makes each item a member with an id the engine mints, which references (`x-ref` kind `member`, with `of: "$element"`
  for the same element) and translations (`content/<name>` for an `x-translatable` key) can name. Batches name them
  `@custom:<type>.[Pkg.]<name>` and `@member:<type>.[Pkg.]<name>.<member>`. Templates get the scope
  `each custom:<type>` (the variable `custom`) and the helper `custom_named`, and a changed custom kind file renders its
  elements' units again. MCP `get_schema` takes `custom:<type>`, the index, element and kind tools and the CLI's
  `model export` and `model stats` filter by `custom` or `custom:<type>`, and the extension file tools handle custom
  kind files. An operation's `requires` can name a custom element. The editor shows custom elements as JSON for now.
  The [user guide](user-guide.md#custom-kinds-extensionskindsjson) has the details.
- **Fixes from the milestone D review.** The 37 notes of [issue #2](https://github.com/mattjcowan/maquettiste/issues/2)
  are fixed before release. Those a user can see:
  - On PostgreSQL, a value map that retypes an enum column first drops the views the model knows about, and those the
    migration removes, and creates the model's views again at the end of the migration. A view outside the model needs a
    `raw` fact before and after the step (see the sql-ddl pack's README).
  - A value-map step upserts only the lookup rows it needs, after the rows their fields name, and creates the lookup
    tables a new lookup table references before it, so a row that names a code, or a table, new in this generation no
    longer fails. Each table and row is written once per migration.
  - `list_migrations` and write answers summarize a merge whose codes all match as a merge, and a change of a type's
    plural name alone as a rename.
  - `rename-reference-type` plans only the seeds in the type's own seed folder, so other operations in the batch can
    change the rest.
  - Re-syncing a binding after a column was dropped and added again under the same name moves the attribute to the new
    column instead of adding a second one, also when the re-sync names only some columns.
  - An attribute that shows a bound column's sensitivity or comment is no longer planned again when another entity bound
    to the same table changes.
  - Seed validation follows a lookup through another lookup, as generation does (MQ4056, MQ7125). Outside PostgreSQL,
    the seed script's TODO for such a lookup stays one comment.
  - The editor API's CSV export of a seed whose rows script fails answers 422 with MQ7127 instead of 500. The engine's
    `ScriptErrorException` is now public, so a host can catch that failure from `ModelStore.ExportSeedCsvAsync`.
  - `fold-into-stereotype` refuses with MQ4077, naming the cause, when its result cannot be checked or when another
    operation in the batch writes a document it rewrites.
  - Findings about a default stereotype's parameters name the schema or package instead of its id, and MQ2011 suggests a
    keyed id only when the table inherits the member.
  - Changing an enum or reference type re-checks tables that get its typed column through an implied stereotype, and
    changing a schema's defaults re-checks the relations whose overlay puts their table in that schema.

## 0.11.0 (unreleased)

- **Table seeds follow their foreign keys.** `model.seeds_in_order` puts a table seed after the seeds that hold the
  rows its foreign keys name, in every environment it loads in. A table whose rows name rows of the same table orders
  its seeds, then each seed's rows, so a named row comes first and a seed's rows stay together. sql-ddl follows that
  order, so a seed script may list its blocks, or a self-referencing seed its rows, in a new order. MQ7111 now asks
  for the named row in every environment the row loads in.
- **Reference types stored in a table.** A reference type can name, per database, the table file that holds its rows:
  `bindings` map its fields to columns, and constant columns tell types that share a table apart. Its rows are then the
  table's rows: foreign keys into the table may name them, table seeds may not repeat their keys (MQ7112), and
  `database.seed_tables` lists them first. New rules MQ7117 to MQ7120 check the table, the field map, the key and the
  NOT NULL columns. sql-ddl stores such a type in its table, with no lookup table, CHECK or enum type: `seed.sql`
  writes its rows with the table's other rows, then points every column holding its codes at the table's code column
  with a foreign key. Templates read a column's binding as `column.reference_binding`, and the editor's Storage tab
  gains "Stored in a table".
- **Migration facts.** A change to a database's shape that its stored rows must follow (a rename, a type change, a
  drop, a column made not null) is now recorded as a migration fact: a small file of the new kind `migration` under
  `model/databases/<database>/migrations/open/`, written by the save that makes the change, in the editor, the command
  line, MCP or the HTTP API. Save, create, delete and batch answers list what was recorded (`facts`). A fact that lacks
  what a migration needs (a conversion, a data check, a reason to drop) is pending, MQ4101, and stops generation until
  it is completed or held. `migrations.record: off` in `maquettiste.json` records nothing. The [schema changes
  guide](guides/schema-changes.md) explains each fact and the widening table that decides when a type change needs one.
- **Checking against a baseline.** `maquettiste validate --against <snapshot id or folder>` checks that facts explain
  every change since another copy of the model (a model snapshot, or a folder such as the main line checked out in CI):
  an unexplained change is MQ4102. `maquettiste generate --against` diffs the migrations from that baseline for one run
  instead of the committed physical snapshot, which it leaves alone. MCP `validate` and `plan` take `against`;
  `POST /api/validate?against=` and `POST /api/generate/plan` take a snapshot id.
- **Listing and folding facts.** `maquettiste migration list` and the `list_migrations` tool (also the assistant's)
  list the open facts in the order they apply. `maquettiste migration fold` and the `fold_migrations` tool remove stale
  facts (MQ4103: a change made and undone) and join consecutive ones; fold facts this way, never delete their files by
  hand.
- New rules MQ4101 to MQ4111 (the new "Migrations" family) check facts: missing intent, unexplained or stale changes,
  `after` links in a cycle, to another database or to nothing, fields that do not fit the fact, two facts on one object
  with no order, SQL text without the database's dialect, a baseline with errors, and a write that recorded nothing for
  elements with errors. MQ4056 also reports migration facts outside PostgreSQL.
- **sql-ddl migrations apply facts.** On PostgreSQL a fact's step replaces the TODO a migration wrote before: a rename,
  a type change with its data check and conversion, a backfill before `SET NOT NULL`, a drop, a raw step behind its
  guard; a held fact waits for its release, and on every dialect a held drop or rename writes a note instead of the
  drop or rename. Migrations are now named `<NNNN>-<key>.sql` (`<key>.sql` with `--against`), the key hashing their
  content, and open with their from state, to state and facts, so the migrations of copies that diverged get distinct
  names and chain by state. Migrations named `0001.sql`, … earlier keep their names.
- **Earlier migrations stay yours.** A migration (any `once` file) of a pack that still exists stays tracked while it is
  on disk, even when no run produces it, so the pack no longer renders placeholders for earlier revisions and
  `generate --check` never reports them.
- `get_project`'s workspace (the branch) is now read by the MCP server and the editor's assistant once, when they start:
  restart the server after switching branches. The engine itself reads no version control.
- **The migration ledger, version 1.** On PostgreSQL, the sql-ddl scripts record the migration facts they reach
  in three bookkeeping tables, in the schema `migrations.ledgerSchema` names (default `migration_ledger`), inside the
  script's own transaction. `maquettiste migration ledger --grant-read <role>` and the `get_ledger_ddl` tool print the
  DDL and the read grant for a team's own migrator. [The migration ledger](ledger.md) has the contract, frozen at
  version 1: a change to it is version 2, with a conversion in its DDL. New rule MQ4067 (a warning): the ledger schema
  is also a schema of the database's modeled objects.
- New built-in helper name: `ledger_ddl`. A pack helper of that name is now MQ6013; rename it.
- **Range columns.** A column can be a `range` or `multirange` with a `subtype` (PostgreSQL's built-in range types);
  sql-ddl migrates between them with the cast PostgreSQL has, and csharp-dapper holds them as text. Elsewhere they are
  stored as text.
- **Index nulls.** An index column can sort its nulls `first` or `last` (PostgreSQL).
- **SQL object environments and run-once.** A SQL object can list the `environments` it runs in: sql-ddl writes it only
  to `objects.<env>.sql`, which runs on every deploy, so its statements must be safe to run again. `apply: once` runs a
  SQL object one time per database behind a migration ledger guard that records it (PostgreSQL). New rules MQ4069 (a
  dependency that does not run in every environment its dependent runs in) and MQ4070 (environments with phase
  `before`). A `rekey` migration fact can list a SQL object whose id changed.
- **Name rules.** Two index or constraint names that collide where the dialect keeps one namespace for them are now
  MQ4068 (error): two indexes on the same columns that both take the conventional name, an index named like a key or a
  table on PostgreSQL, a foreign key name used twice in a MySQL schema. Names compare without case after cutting to the
  identifier limit. The over-long name check (MQ4001) on conventional names now counts UTF-8 bytes on PostgreSQL and
  Oracle, as it already did on given names; a name with letters outside ASCII may now be reported.
- **Column defaults.** A column's literal default is checked against its resolved length (the conventions'
  `defaultStringLength` when the column has none). A `json` column's default must be a string of JSON text: an object,
  array, number or boolean, which the DDL could not write, is now MQ3019; use a string or `defaultSql`. A column with a
  `nativeType` is checked for the kind of value only.
- **Batches by name.** A batch (MCP `apply_batch`, the new `maquettiste model apply <batch.json>`, the editor's
  assistant) can name what it touches instead of carrying ids: `@kind:path` with the names as written in the files
  (`@table:billing.notes`, `@entity:Billing.Invoice`, `@column:billing.notes.body`), and placeholders declared with
  `"as": "$name"` for elements the same batch creates. The engine mints every missing id, a sub-element's included, and
  the answer lists the names it resolved and the ids it minted (`ids`). Files stay id-based. `apply_batch` takes
  `dryRun`, and `model apply` takes `--dry-run`: nothing is written, and the answer has a unified diff per file and the
  diagnostics the change would add and remove. New rules MQ1012 to MQ1015 (a name that finds nothing, a name that finds
  several, a misused identity, a malformed or misplaced name); an update that drops a sub-element and adds a same-named
  one without its id is refused (MQ1014: keep its id). See [batches by name](mcp.md#batches-by-name).
- The assistant's proposals resolve names the same way and no longer retry to fill in missing ids; each proposed file
  carries its diff (`diff` on `AssistProposalFile`). `BatchResult` gains `ids`, and `BatchOperation` gains `as`.
- **Typed links in custom properties.** A property schema (an extension file, a stereotype's `parameters`, a pack's
  `parameterSchema`) can type a value as the id of an element or sub-element with `x-ref`: `kind`, `of` (`$element` for
  a member of the holder, or an element whose seed rows it names), `sameDatabase` and `optional`. Validation checks the
  values (MQ2010: a name where the id belongs; MQ2011: a target out of scope; MQ2009: a malformed `x-ref`), deletes and
  saves treat them as references (cleared by `remove-references` when optional), and `get_references` rows carry
  `optional` and `schema`. The batch operation `names-to-ids` turns the values still written as names into ids, for the
  whole model or the `elementIds` given.
- **Stereotype rules and parameters.** A stereotype can declare a `group` (one per element), `requires`, `conflicts`
  and `implies` (one level), and typed `parameters` for the custom properties of the elements that carry it. Implied
  stereotypes act as listed ones, and templates see them with `implied_by`. New rules MQ2013 to MQ2019; MCP
  `get_schema` returns `stereotypeParameters`.
- **Packages on every kind, and layers.** Tables, views, sequences, routines, database types, SQL objects, queries,
  databases, mappings, stereotypes, reference types, seeds, actors and scenarios can join a package (`package`), which
  groups them without changing any database. A package's `dependsOn` lists the packages its elements may reference;
  any other reference from a checked package is MQ2020, and a `dependsOn` that names its own package or forms a cycle
  is MQ2021. A model without `dependsOn` behaves as before. The seeds of one target merge in a fixed order, and two
  seeds of one table that key their rows differently are MQ7121.
- **Translatable custom properties.** `"x-translatable": true` on a top-level string property of an extension file or
  of stereotype parameters makes its text translatable per locale, as the field `properties/<name>` in the translation
  screens, `get_translations`, `set_translations`, `localization_status`, the `l10n` verbs and their exports; templates
  read it with `translate(entity, "properties/<name>")`. New rules MQ7212 (a misplaced keyword) and MQ7213 (a keyword
  on kinds that have no translations).
- **Tags into a property.** The batch operation `tags-to-property` moves the tags that start with a prefix
  (`status:draft`) into a custom property (`status`), converted by the schema that declares it, and removes them from
  the vocabulary.
- **Rationale, decision and status.** Every element, attribute, enum member, column, database schema and category can
  carry `rationale` (Markdown), `decision` (a URL or a decision record reference) and `status` (`proposed`,
  `approved`, `deprecated`, `retired`). A reference to a retired element is MQ2022, a warning, so retiring something
  lists its last users. Templates read the three fields on entities, tables, databases and most other resolved
  elements; editing them records no migration fact.
- The editor's forms do not show the new keys yet: edit them on the inspector's **JSON** tab. The new guide [Typed
  properties and layers](guides/typed-properties-and-layers.md) walks through all of them, and the modeling skill
  (`maquettiste init --skill` refreshes it) teaches agents to use them.
- **Seeds by name in templates.** `seed_named "currencies"` finds a seed by its name (`Billing.currencies` when its
  file states a package), so a template can write one constant per row of one seed without looping over every seed;
  a name two seeds share fails the unit (MQ6006). Renaming a seed into or out of the name renders the unit again.
  `seed_row <seed> <key>` finds one row by the code of a reference type's seed, the row key of a table seed or the key
  of an entity's seed. Templates read a seed's longest name as `qualified_name`. New built-in helper names: a pack
  helper named `seed_named` or `seed_row` is now MQ6013; rename it.
- **Seed columns and cells by name.** A seed written through `create_element`, `save_element`, `patch_element`,
  `apply_batch`, `model apply` or the editor's API may give its `columns` and `key` by name and a row's cells as a
  `values` object keyed by its columns; the file still stores ids and one cell per column. A name that finds nothing or
  several is MQ1002 and nothing is written. See [seeds by name](mcp.md#seeds-by-name).
- **Closed block comments.** A `block` unit can set `blockCommentEnd`, the text that ends each delimiter line, so a
  managed block fits a file whose comments must be closed: `<!--` and `-->` in an XML or HTML file. A delimiter line is
  found whatever its comment and closing text, and other closing text is rewritten on the next run. See [managed
  blocks](user-guide.md#managed-blocks-a-few-generated-lines-in-a-file-you-own).
- **Copy units.** A unit in the new mode `copy` names no template: its `sources` (glob patterns over the pack's files,
  matched case-sensitively) are copied under its `output` folder, each after the pattern's fixed folders. `tokens` maps
  literal text to a pack parameter, so a skeleton stays code that builds: `"Skeleton.Api": "rootNamespace"` writes the
  project's namespace into every text file and path. Text files change only where a token occurs (line endings and
  every other byte stay, no formatter runs); other files, such as images, are copied byte for byte. The copies are
  managed like overwrite files: a changed, added or removed source renders the unit again, and a hand edit is MQ6009. A
  pattern that matches no pack file is MQ6030 and a token whose parameter is undeclared or not a string is MQ6031;
  either skips the unit while the pack's other units render. The preview lists every copied file and marks binary ones
  (`binary: true`, no text), a binary file's diff reads `Binary file <path> differs`, and the editor's file list marks
  the files a copy unit copies as `copy-source`; they are never parsed, loaded as pack scripts or read as type maps. See
  [copy units](user-guide.md#copy-units-a-folder-of-files-copied-as-they-are).
- **Template helpers that find and order.** `table_named "billing.invoices"` finds a table by name in every database (or
  the one you pass), `column_named` one of its columns; renaming a table into or out of the name renders the unit again,
  and a name two tables share fails the unit (MQ6006). `array_index_of` and `array_find` search a list by an item's
  member, `toposort` orders a list by `[before, after]` pairs (stable; a cycle releases the earliest item left), and
  `json` takes member paths, `json table ["name", "columns.name"]`, so a table or entity can be written as JSON.
- **Two scopes.** `each database` runs a unit once per database, without a selector script; `each attribute` once per
  attribute declared in a model file (never an inherited or added copy). Their `where` filters as the user guide says.
  The example packs' database units (sql-ddl's schema, migration, seed and environment scripts, seed-data's data) now
  use `each database`, and their scripts no longer register a `databases` selector; unit keys and outputs are unchanged.
- **Definitions on `model`.** `model.stereotypes`, `model.tag_vocabularies` and `model.category_trees` list the
  stereotypes (with the attributes they add and their default properties), the tag vocabularies and the category trees.
  A category now also has `parent`, `children`, `order` and `description`.
- New built-in names: the helpers `array_index_of`, `array_find`, `table_named`, `column_named` and `toposort`, and the
  template variables `database` and `attribute`. A pack helper with one of these names is now MQ6013; rename it.
- **Absent values are null.** A value a model file leaves out reads as null in templates, pack scripts and
  `get_resolved_model`, never as an empty text: the display and plural names of tables, columns and other database
  objects without them, the `body` of a view, routine or SQL object without text for the database's dialect, the
  `returns.native_type` of a routine that returns a table, the `native_name` of a database type the dialect cannot
  create, the `sql` of a query that cannot be written for the dialect (and of a nested query), and the `description` of
  a gate's audit field. A template that compares one of these with `""` should compare with `null`; sql-ddl now does.
  Values that are worked out keep their rule (an element's display name falls back to its name). `display_name`,
  `plural_name` and `description_of` give a database object's own text. csharp-dapper's audit records now describe an
  audit attribute without a description as `Audit attribute <name>.` instead of leaving the summary empty.
- **Plain helpers.** `maquettiste.helper(name, fn, { plain: true })` gives a pack helper frozen plain copies of its
  arguments instead of read-only views, which are faster to read in a loop. A copy reaches two hops: a table argument
  has its columns and its database, and what they hold is `{ id, kind }`. Everything copied counts as read. A copy has
  no `hasStereotype`, `hasTag` or `find`, and the model itself cannot be passed; any other option stops the script from
  loading (MQ6016).
- **Scoped generation.** `maquettiste generate --element <id>` and `--unit <pack>/<unit>` (both repeatable; values of
  one option OR, the two options AND) run only those units: their files are written, their outputs that are no longer
  produced are deleted, and every other file and unit record stays as the last run left it, so the next full run
  writes the rest. `--unit sql-ddl/schema --element <database id>` regenerates one database's schema script. A limited
  run never moves the schema snapshots, so a selected unit that reads the schema difference (`sql-ddl/migration`)
  fails with MQ6033; migrations render only in a full run. New rules MQ6032 (a unit filter that names a pack the run
  lacks, a unit no run pack has, or a unit id several run packs have) and MQ6034 (a warning: the filters select
  nothing). MCP `plan` and `generate` take `units` and `elements`, and so does `POST /api/generate/plan` in its body;
  a limited plan's apply writes only its own units' files. See [regenerate one element or one
  unit](user-guide.md#regenerate-one-element-or-one-unit).
- **Short answers for agents.** Three MCP tools answer a summary first, so an agent's first calls stay small on a large
  model. `get_project` names the packs with their unit counts, counts the pack diagnostics and names the extensions;
  `sections` (`settings`, `packs`, `extensions`) adds the typed settings, the pack manifests and diagnostics, or the
  extension schemas, which the answer no longer carries by default (`packs` and `extensions` now hold the summaries and
  the names), and the always-empty `git` member is gone.
  `list_validation_rules` with no argument answers the rule families with their counts; `family`, `ids` or `query`
  answer full entries in pages. `tag_usage` answers each tag with its use count and a few element names; `tag` answers
  the ids of the elements that hold one tag, in pages. The editor's HTTP routes answer as before. The assistant's
  `get_project`, `list_validation_rules` and `tag_usage` answer the same summaries. See [reading a large
  model](mcp.md#reading-a-large-model).
- **Patches.** The new MCP tool `patch_element` changes part of one element with an RFC 6902 JSON Patch and the hash
  the caller read, instead of the whole document: the same conflict check, validation and migration facts as
  `save_element`, and the engine mints the id of a new sub-element sent without one. A patch that cannot be applied (an
  unknown operation, a path that does not resolve, a failed `test`, a change to `/id` or `/kind`) is the new rule
  MQ1016, and nothing is written.
- **Generate in one call.** The new MCP tool `generate` renders and writes in one run, as `maquettiste generate` does,
  and answers a summary per pack: counts by kind, the first 50 paths, and at most 100 diagnostics, errors first. No
  plan is stored. `plan`, `get_plan_diff` and `apply_plan` remain for a review before writing. The MCP server now has
  71 tools.
- **Script limits without the clock.** `limits.scriptTimeoutMs: 0` in `maquettiste.json` turns the scripts' wall clock
  off, so script rules and pack scripts give the same results on a busy machine as on a quiet one; the statement limit
  and the memory limit (`limits.scriptMemoryBytes`, every byte a call allocates) then bound each call. Use it in CI. A
  regular expression match keeps its 250 ms bound, and previews and path listings keep a deadline of 8 seconds. `limits.scriptStatements` is now at most 2,000,000,000.
- **A scope per rule file.** Each file in `extensions/rules/` now runs in its own function scope, and the globals are
  frozen before the rule files run. A rule can no longer call a function of another rule file, and a rule file that sets
  a global or adds to a built-in prototype fails to load with MQ5002: copy a shared function into each file that needs
  it. Pack scripts are unchanged. New rule MQ5005 (a warning): a rule file declares `let` or `var` at its top level,
  state a rule must not keep between calls; `validate` and the Extensions tab show it.
- **Whole-model rules.** `maquettiste.rule({ id, severity, scope: "model", check(model, report) })` checks the model
  once per full validation instead of once per element, such as "every reference type is used"; each `report` names the
  `element` its finding is about. Element saves do not run it. `list_extension_files` marks such rules `wholeModel`.
- **Faster script rules.** A rule's `model` gains `withStereotype(key)`, `withTag(tag)`, `withProperty(key)` and
  `byQualifiedName(name)` (the qualified names batches take), backed by indexes built once per model, and
  `model.all(kind)` no longer scans every file. The editor, the MCP server and `generate --watch` keep each rule's
  findings per element in memory and run a rule again only on the elements whose inputs changed.

## 0.10.1 (2026-10-08)

- **Table seeds.** A seed can fill a table directly, by column, besides entities, relations and reference types. Entity
  seeds of an entity bound to a table now write their rows too.
- **Where and how a seed applies.** Environments (a seed for every environment, or only some, such as `demo`), insert
  once (the default) or keep the rows as the model has them, and optionally delete the rows the model no longer holds.
  sql-ddl writes `seed.sql` for every environment and `seed.<env>.sql` for each one.
- **Rows in a CSV file.** A seed can keep its rows in a CSV file beside it.
- **Seed data files.** The new seed-data pack writes the rows as data files (CSV with a manifest, or JSON), and
  csharp-dapper can add a loader that applies them (off by default).
- The documentation site, with guides and screenshots of every main screen.
- Fixes: the Processes explorer's search finds processes and actors; the field map labels where a mapping comes from;
  the primary color fields show their whole value.

## 0.10.0 (2026-10-07)

- **Storage both ways.** From an entity's Storage tab, create the attributes for a table's unmapped columns, or the
  columns for an entity's unmapped attributes, in one step. The field map labels where each mapping comes from.
- **Project properties.** Key and value pairs on Settings, General, which templates read as `project.properties.<key>`.
  The csharp-dapper pack takes its namespace from `baseNamespace` when its own parameter is empty.
- **Tags across the model.** Settings, Tags lists the tags in use that are not declared, declares them in one click,
  renames a tag everywhere and removes tags from every element that has them. Tags have a color picker.
- The browser tab shows the project's name, and editors on different ports of one machine keep their own sign-in.

## 0.9.0 (2026-10-06)

- PostgreSQL DDL features as typed fields, edited in the editor: temporal keys, exclusion constraints, partitioning,
  storage parameters.
- A foreign key can set only some of its columns on delete.

## 0.8.0 (2026-10-05)

- Model snapshots: take one, open the model as of it, compare two, restore, export and import.
- The inspector edits only the picked column.

## 0.7.0 to 0.7.2 (2026-10-03 to 2026-10-05)

- The assistant: a sidebar that reads the model and proposes changes, which you review before they are saved.
- Template previews are bounded to what is on screen; relation ends are indexed.
- Quick edits survive while a table is being stored.

## 0.6.0 and 0.6.1 (2026-10-03)

- Databases are designed on their own; entities bind to tables, views and queries, and materialize either way.
- A plan lists every file with what Apply will do to it, and offers force and the hand-edit choice.
- Every element has a property bag. The modeling skill refreshes without erasing a team's conventions.
- The DDL covers every dialect, and entities stay out of the generated DDL unless mapped.

## 0.5.0 to 0.5.5 (2026-09-30 to 2026-10-02)

- Processes: lifecycles and orchestrations as statecharts, drawn, laid out and simulated in the editor, with actors and
  scenarios. The engine interprets, verifies, imports and exports them, and the packs generate code and documentation.
- Queries over tables that fill entities; routines, database types and SQL objects; tables, views and sequences created
  from the editor.
- Extensions edited in the editor; pack renames everywhere; delete with dependents.
- The agent server registered in `.mcp.json` with no script in the repository.
- Generation writes every root, and ignore files as managed blocks.

## 0.1.0 to 0.4.0 (2026-09-29)

- The engine, the command line, the example packs and the editor: domains, entities, relations, enums, databases,
  reference data with seeds, localization, and the agent server.
- Generation visible and configurable in the editor; explicit database mapping; entity seeds edited in the editor;
  branding.
