# Releases

What each version added, newest first. Every version is a tag on the repository, an image
(`mattjcowan/maquettiste:<version>`) and the `Maquettiste.Cli` and `Maquettiste.Engine` packages of the same number. The
[getting started](getting-started.md) page and the [update steps](getting-started.md#updating) say how to move to a new
one.

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
