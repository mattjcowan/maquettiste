# Releases

What each version added, newest first. Every version is a tag on the repository, an image
(`mattjcowan/maquettiste:<version>`) and the `Maquettiste.Cli` and `Maquettiste.Engine` packages of the same number. The
[getting started](getting-started.md) page and the [update steps](getting-started.md#updating) say how to move to a new
one.

## 0.11.0 (unreleased)

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
- **The migration ledger (draft, version 1).** On PostgreSQL, the sql-ddl scripts record the migration facts they reach
  in three bookkeeping tables, in the schema `migrations.ledgerSchema` names (default `migration_ledger`), inside the
  script's own transaction. `maquettiste migration ledger --grant-read <role>` and the `get_ledger_ddl` tool print the
  DDL and the read grant for a team's own migrator. [The migration ledger](ledger.md) has the contract, which may still
  gain columns, never lose or change one, until it is frozen. New rule MQ4067 (a warning): the ledger schema is also a
  schema of the database's modeled objects.
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
