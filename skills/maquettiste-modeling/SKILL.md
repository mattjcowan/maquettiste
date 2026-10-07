---
name: maquettiste-modeling
description: Editing a Maquettiste model (the JSON under .maquettiste/) in a repository that uses Maquettiste, validating it and generating code. Use when asked to add or change entities, relations, enums, types, reference data, seeds, translations, databases, mappings, diagrams, processes, actors, scenarios or templates.
---

# Modeling with Maquettiste

A model is one file per element under `.maquettiste/model/<kind>/`, canonical JSON (two-space indent, LF, sorted
by each schema's key order, defaults omitted). Every element has a ULID `id`; every reference between elements is
an id, never a name. The schemas are in `.maquettiste/.schema/v1/` and each file's `$schema` points at them.

## Kinds and folders

| Folder | Element | Notes |
| --- | --- | --- |
| packages/ | domain (package) | groups elements; may nest through `parent` |
| entities/ | entity, with attributes, keys and `bindings` (one per database: how it reads and writes) | `stereotypes` add virtual attributes (audited, soft-delete, tenant-scoped) |
| relations/ | named relation with ends (roles, cardinality) and optional attributes | many-to-many or attributed relations become junction tables |
| enums/ | closed value set with codes, stored as an integer or a string | data that grows or needs a lookup table is a reference type |
| types/ | value objects and custom scalar types | reusable across attributes |
| reference-types/ | reference type: built-in `code` and `label`, plus your own fields | an attribute's `type` may name one (single, or `collection`); defaults and cells use the row's code; storage is template-defined unless `referenceData.strategies` declares one |
| seeds/<target>/ | rows of an entity, relation or reference type | one row per line with its own id; owned by its target and deleted with it |
| locales/<locale>/ | translation shards (`kind: "locale-shard"`) keyed by id | only display name, plural name, description, and a row's label and description |
| databases/<db>/ | database, tables (overlays), views, sequences | tables from entities store only overrides |
| databases/<db>/routines/ | routine (`kind: "routine"`): a function or procedure, `parameters` typed by a built-in type or a database type id, `returns`, a `body` per dialect | `dependsOn` orders it; routines are not overloaded |
| databases/<db>/types/ | database type (`kind: "database-type"`): a domain, composite, enum or range the database owns | a column uses it by naming its id or name in `nativeType` |
| databases/<db>/objects/ | SQL object (`kind: "sql-object"`): a trigger, grant, extension or anything else, statements per dialect | `phase` before or after the tables; `dependsOn` orders it |
| databases/<db>/queries/ | query (`kind: "query"`): `from`, `joins`, `select`, `where`, `groupBy`, `orderBy`, `paging`, `collections` as JSON trees over the database's tables and views, with an `entity` as the row shape or, without one, the select list | column refs are `alias.<column key>` (the key `get_database_view` lists: an attribute id, a designed column's id), names accepted; never SQL text but an `sql` expression per dialect |
| mappings/ | projection overrides of older models, and relation mappings (`foreignKey`, junctions) | new work binds entities in their own file instead |
| processes/ | process: a statechart (states, transitions, events, guards, actions, invokes, gates, context) | `use` is `lifecycle` (a `subject` entity, optionally a bound enum attribute) or `orchestration` |
| actors/ | actor: `type` person, role or external system | raises events, signs gates, completes human tasks; not in a domain |
| scenarios/<process>/ | scenario: a recorded run of one process, each step with what it expects | owned by its process and deleted with it |
| diagrams/ | canvas membership and positions only (a process's diagram holds its chart's places) | never own elements |
| vocabularies/ | tags, categories, stereotypes | |

Templates live in `.maquettiste/templates/<pack>/` (pack.json, *.scriban, helpers.js). Project settings, output
roots and conventions are in `.maquettiste/maquettiste.json`.

## Preferred: the MCP tools

When the `maquettiste` MCP server is connected (in Claude Code its tools show up as `mcp__maquettiste__<tool>`; it is
`maquettiste mcp`, registered by `maquettiste init --mcp`, or by `maquettiste init --mcp --docker <image>` as a
`docker run` of the image; see docs/mcp.md), use its tools instead of editing the JSON
files: every write goes through the same validated, hash-checked path as the editor, so a save can never half-apply,
clobber a concurrent edit or leave a dangling id.

1. Find: `get_project`, then `get_model_index` (filter by `kind`, `package`, `tag`, `category`, `stereotype`, `query`)
   for ids; `get_references` for where an element is used; `get_database_view` for a database's tables and columns.
2. Read: `get_schema` for the kind before building a document (its `extensions` constrain `properties`); `get_element` returns the document (`json`) and its `hash`.
   Reading many elements: `get_model_kinds` (`by: "package"`) says what there is, `get_elements` returns documents in pages
   (`fields` keeps only the members you need), `get_resolved_model` returns what generation sees (resolved attributes, tables,
   processes) as flat records; pass each page's `next` as `cursor` until it is null, and never loop `get_element` over thousands of ids.
3. Change: `save_element` (the whole edited document plus `expectedHash` = the hash you read), `create_element`,
   `delete_element`, or `apply_batch` for several changes that must land together (all or nothing).
   - `conflict`: the file changed since you read it; nothing was written. Merge your change into `current` and retry with
     its `hash`. Never retry blindly with the new hash and your old document.
   - `invalid`: nothing was written; fix what `diagnostics` name (rule id, JSON pointer).
   - `referenced` (delete): the `referrers` list who points at it; fix them first, or pass
     `resolution: "remove-references"` when the references are optional, or `resolution: "delete-dependents"` to also delete
     what cannot exist without the element (call `delete_element` with `dryRun: true` first and read the plan).
   - Renaming an attribute or element is a plain save of the new `name`: references are ids, so nothing else changes
     (an element rename also moves its file).
4. Check: a successful save already returns `diagnostics`; `validate` (optionally scoped by `elementIds`) checks the model.
   For a query, `preview_query_sql` returns the SQL it renders (for its database's dialect or another) with the diagnostics
   (MQ4021 to MQ4043) that point at the node to fix. For an entity's binding, `preview_binding_sql` returns its select,
   insert, update and delete statements; `validate` reports MQ4044 to MQ4054 for bindings.
   Custom property schemas (`extensions/<name>.json`) and script rules (`extensions/rules/<name>.js`, findings `x/<id>`, run by `validate`) are files: `list_extension_files`, `read_extension_file`, `write_extension_file` (with `expectedHash`, `new` to create; a rule's syntax error comes back at once), `move_extension_file`, `delete_extension_file`.
5. Reference data and translations: `reference_type_usage` lists the attributes that use a reference type and its storage
   per database; `create_seed` gives a reference type that has none its empty seed; `export_seed_csv` / `import_seed_csv` (a dry run unless `apply` is true, then `expectedHash`) move rows as
   CSV; `localization_status`, `get_translations` and `set_translations` (with the `shardHash` values you read as
   `expected`) read and write translations, which never go in element files. From a terminal, `maquettiste l10n status`,
   `l10n export|import <locale>`, `l10n prune`, `l10n set-default <locale>` and `maquettiste seed new|export|import` do
   the same; import, prune, set-default and seed import only preview until given `--apply`.
6. Generate: `plan` (stores a plan, touches nothing), `get_plan` / `get_plan_diff` for the files that matter, then
   `apply_plan` with the plan id. `stale` means the inputs changed since the plan: plan again. `list_packs`,
   `get_settings` and `save_settings` (with `expectedHash`) cover the packs and `maquettiste.json`.
7. Explain and author templates: `get_plan` with `units` true gives each unit's pack, template, element, `reason` and
   `causes`; `explain_unit` answers why a unit renders or not (a plan unit by `planId` and `key`, or any `pack`, `unit`
   and `elementId`). Packs are files: `get_pack`, `list_pack_files`, `read_pack_file`, `write_pack_file` (with
   `expectedHash`, `new` to create), `move_pack_file`, `delete_pack_file`, `save_pack` (pack.json), `save_pack_settings`
   (`packs.<pack>` of the settings), `new_pack` (from `empty` or a starter), `get_template_context`, `preview_unit`
   (with `overlay` for unsaved text; writes nothing; an element outside the unit's scope returns MQ6026 naming the kind
   the template expects, and no files), `unit_paths` and `get_pack_outputs`.
   A unit with `mode: "block"` keeps one delimited block of lines inside a file the team owns (an ignore file, a config
   file) and never touches the rest; `blockComment` sets the delimiter comment (default `#`) and `createFile: true` lets it
   create a missing file (otherwise it writes nothing and the plan says `target-missing`). Its path must be allowed: a file
   entry in `outputs.allow` (`{ "path": "src/App/.gitignore" }`) allows exactly that file.

8. Processes: see "Processes" below.

Every model read rescans the model folder and settings reads and saves check the file on disk, so edits made outside the server are seen; the resource `maquettiste://conventions`
and the prompt `modeling-conventions` carry this text.

## Processes

A process is a statechart in one file: states nest in `states` (types atomic, compound, parallel, final, history, choice),
`transitions` are in priority order, and events, guards, actions and invokes are declared in the process and referenced by
id. Guards and actions carry an optional JavaScript `expression` (a guard returns a boolean, an action an object of context
updates); one without an expression is a named stub that the generated code asks the team to implement. A lifecycle's
root-level states must equal its bound enum's members, in order (MQ9203).

- Tools: `simulate_process` runs inputs through the engine's interpreter and keeps no state (send the whole input list each
  time; `enabled` says what can happen next); `record_scenario` saves a run as a scenario with every step's expectations
  filled from the replay (`dryRun` to preview); `verify_scenarios` replays scenarios; `export_process` and `import_process`
  (a dry run unless `apply`) move a process to and from an XState machine config; `sync_enum_from_process` (a dry run unless
  `apply`) makes a lifecycle's bound enum follow its states and never removes a member still in use. `apply_batch` has the
  process operations `sync-enum`, `set-lifecycle` (binds an entity and a process on both sides), `set-initial` and
  `refresh-scenario`.
- Scenarios are the process's tests: `validate` replays every scenario (MQ93xx), and the packs turn each scenario into a
  generated test. When a chart change breaks a scenario on purpose, refresh its expectations (`refresh-scenario`) rather
  than editing `expect` by hand; when it breaks by accident, fix the chart.
- A scenario's `payload`, `start.context` and `expect.context` maps are keyed by attribute id; `assume` gives the result of a
  guard without an expression.
- From a terminal: `maquettiste process simulate|record|verify|export|import|sync-enum` (record, import and sync-enum only
  preview until `--apply`; `verify` exits 1 on a failing scenario).
- The engine creates no table and runs no process in the application: where instances, history and audit records live is
  the project's mapping or a pack parameter (`processTables` in sql-ddl). Never add entities or databases for them unasked.

## Fallback: editing the files

Without the server (or for bulk mechanical edits the tools do not cover), edit the files directly:

1. Change the JSON files (keep them canonical; the next save by the tool rewrites them anyway). New elements need a new
   ULID `id` (uppercase, 26 characters); references are ids.
2. `maquettiste validate` (exit 1 on errors; `--format sarif` for annotations). Fix dangling ids first.
3. `maquettiste generate --dry-run --diff` to see what would change, then `maquettiste generate`.
4. `maquettiste generate --check` is what CI runs: it renders every output root and fails on stale, missing, orphaned or
   hand-edited output (guard only what the team commits, or run `generate` and compare the working tree).

If the editor or the MCP server is running on the repository, it picks up file changes (the editor live through its
watcher, the server on its next call). Prefer small, reviewable changes: one element per file makes the diff the review.

## Conventions worth knowing

- Names are PascalCase for entities and camelCase for attributes; table and column names come from the project's
  conventions (plural snake_case by default), overridable per element.
- `displayName`, `pluralName` and `description` feed generated UI and documentation; keep them meaningful.
- A database holds only what is mapped to it: `byConvention` on the database file is `all`, `packages` (the domains
  listed in `packages`, with their sub-domains) or `none`; a mapping file adds one entity (or ignores one) either way. Write
  `byConvention` on every new database (`none` unless asked); a file without it keeps the older rule (every entity when
  `packages` is empty). MQ4012 (info) names an entity that lands in no database; MQ4013 (warning) a `packages` list that
  `all` or `none` does not use. Never add a database or a mapping just to silence MQ4012: which entities become tables,
  and how, is the user's decision.
- Bind entities explicitly. The database knows nothing about entities; an entity's `bindings` (one per database) say where it
  reads (`source`: a table, a view or a query of that database), its `constants` (a filter on every read and a value on every
  insert: how several entities share one table through an `entity_type` column), which column each attribute reads
  (`fields`), the columns no field maps (`columns`: `ignored`, `database` or `computed`; leaving one out is MQ4047), where it
  writes (`write`, `"none"` for a read-only entity) and how it deletes (`delete`: `key`, `soft` or `none`). A bound entity is
  never projected. To turn an entity the database projects into a table of its own, use materialize instead of relying on
  projection: `get_materialize_status`, `preview_materialize`, then `apply_batch` with `{"op":"materialize-tables",
  "database":...,"entities":[...]}`; to make entities from existing tables, `{"op":"materialize-entities","database":...,
  "tables":[...],"package":...}`. From a terminal: `maquettiste model materialize tables|entities ... --dry-run`. Which
  tables to materialize, and when, is the user's decision.
- Reference data (units, countries, statuses that grow) is a reference type with its rows in a seed; an entity's
  starting rows are a seed of that entity. Deleting an element deletes its seeds and translations in the same save.
- The engine creates no table or column for reference data on its own: the packs decide the physical form, from the
  storage strategy the project declares. Stereotypes and their meaning are the project's own, not built in.
- Tag, stereotype or typed field: a tag is a label for grouping and search that no generation depends on (`domain:billing`);
  a stereotype is a named, reusable meaning that changes what is generated or validated (`audited`, `tenant-scoped`, a
  `high-churn` table storage profile); the values a feature needs are typed fields (a table's or index's `storage`, a
  foreign key's `onDeleteColumns`), never strings packed into a tag (`storage:fillfactor=90` would be parsed and never
  validated). A database feature the model has a field for (temporal keys, exclusion constraints, partitioning, storage
  parameters, view security options, routine volatility and settings) goes in that field; what it has none for (grants,
  extensions, policies, triggers) goes in a SQL object with `dependsOn`, which a migration runs again when it creates what
  it depends on again.
- Localization settings are `localization` in `maquettiste.json` (`defaultLocale`, `locales`, `fallbacks`, `require`);
  default texts stay in the element files. Locale tags are BCP 47 with a hyphen and canonical case (`zh-CN`,
  `zh-Hant-TW`, `fr`), never `zh_CN` (MQ7201).
- `name` and `branding` (`icon`, `colors.light`, `colors.dark`) in `maquettiste.json` only brand the editor; they never
  change generated output.

## This repository's conventions

A repository keeps its own modeling conventions in `CONVENTIONS.md`, in the same folder as this file
(`.claude/skills/maquettiste-modeling/`). When that file exists, read it after this one and follow it; where the two
disagree, it wins. Over MCP, the `maquettiste://conventions` resource and the `modeling-conventions` prompt end with it.
Add project rules there, never here: `maquettiste init --skill` rewrites this file and never touches `CONVENTIONS.md`.
