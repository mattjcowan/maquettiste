---
name: maquettiste-modeling
description: Editing a Maquettiste model (the JSON under .maquettiste/) in a repository that uses Maquettiste, validating it and generating code. Use when asked to add or change entities, relations, enums, types, reference data, seeds, translations, databases, mappings, diagrams or templates.
---

# Modeling with Maquettiste

A model is one file per element under `.maquettiste/model/<kind>/`, canonical JSON (two-space indent, LF, sorted
by each schema's key order, defaults omitted). Every element has a ULID `id`; every reference between elements is
an id, never a name. The schemas are in `.maquettiste/.schema/v1/` and each file's `$schema` points at them.

## Kinds and folders

| Folder | Element | Notes |
| --- | --- | --- |
| packages/ | domain (package) | groups elements; may nest through `parent` |
| entities/ | entity, with attributes and keys | `stereotypes` add virtual attributes (audited, soft-delete, tenant-scoped) |
| relations/ | named relation with ends (roles, cardinality) and optional attributes | many-to-many or attributed relations become junction tables |
| enums/ | closed value set with codes, stored as an integer or a string | data that grows or needs a lookup table is a reference type |
| types/ | value objects and custom scalar types | reusable across attributes |
| reference-types/ | reference type: built-in `code` and `label`, plus your own fields | an attribute's `type` may name one (single, or `collection`); defaults and cells use the row's code; storage is template-defined unless `referenceData.strategies` declares one |
| seeds/<target>/ | rows of an entity, relation or reference type | one row per line with its own id; owned by its target and deleted with it |
| locales/<locale>/ | translation shards (`kind: "locale-shard"`) keyed by id | only display name, plural name, description, and a row's label and description |
| databases/<db>/ | database, tables (overlays), views, sequences | tables from entities store only overrides |
| mappings/ | entity-to-table bindings that conventions cannot express | |
| diagrams/ | canvas membership and positions only | never own elements |
| vocabularies/ | tags, categories, stereotypes | |

Templates live in `.maquettiste/templates/<pack>/` (pack.json, *.scriban, helpers.js). Project settings, output
roots and conventions are in `.maquettiste/maquettiste.json`.

## Preferred: the MCP tools

When the `maquettiste` MCP server is connected (in Claude Code its tools show up as `mcp__maquettiste__<tool>`; it is
`maquettiste mcp`, registered by `maquettiste init --mcp`, see docs/mcp.md), use its tools instead of editing the JSON
files: every write goes through the same validated, hash-checked path as the editor, so a save can never half-apply,
clobber a concurrent edit or leave a dangling id.

1. Find: `get_project`, then `get_model_index` (filter by `kind`, `package`, `tag`, `category`, `stereotype`, `query`)
   for ids; `get_references` for where an element is used; `get_database_view` for a database's tables and columns.
2. Read: `get_schema` for the kind before building a document (its `extensions` constrain `properties`); `get_element` returns the document (`json`) and its `hash`.
3. Change: `save_element` (the whole edited document plus `expectedHash` = the hash you read), `create_element`,
   `delete_element`, or `apply_batch` for several changes that must land together (all or nothing).
   - `conflict`: the file changed since you read it; nothing was written. Merge your change into `current` and retry with
     its `hash`. Never retry blindly with the new hash and your old document.
   - `invalid`: nothing was written; fix what `diagnostics` name (rule id, JSON pointer).
   - `referenced` (delete): the `referrers` list who points at it; fix them first, or pass
     `resolution: "remove-references"` when the references are optional.
   - Renaming an attribute or element is a plain save of the new `name`: references are ids, so nothing else changes
     (an element rename also moves its file).
4. Check: a successful save already returns `diagnostics`; `validate` (optionally scoped by `elementIds`) checks the model.
5. Reference data and translations: `reference_type_usage` lists the attributes that use a reference type and its storage
   per database; `export_seed_csv` / `import_seed_csv` (a dry run unless `apply` is true, then `expectedHash`) move rows as
   CSV; `localization_status`, `get_translations` and `set_translations` (with the `shardHash` values you read as
   `expected`) read and write translations, which never go in element files. From a terminal, `maquettiste l10n status`,
   `l10n export|import <locale>`, `l10n prune`, `l10n set-default <locale>` and `maquettiste seed export|import <seed>` do
   the same; import, prune, set-default and seed import only preview until given `--apply`.
6. Generate: `plan` (stores a plan, touches nothing), `get_plan` / `get_plan_diff` for the files that matter, then
   `apply_plan` with the plan id. `stale` means the inputs changed since the plan: plan again. `list_packs`,
   `get_settings` and `save_settings` (with `expectedHash`) cover the packs and `maquettiste.json`.
7. Explain and author templates: `get_plan` with `units` true gives each unit's pack, template, element, `reason` and
   `causes`; `explain_unit` answers why a unit renders or not (a plan unit by `planId` and `key`, or any `pack`, `unit`
   and `elementId`). Packs are files: `get_pack`, `list_pack_files`, `read_pack_file`, `write_pack_file` (with
   `expectedHash`, `new` to create), `move_pack_file`, `delete_pack_file`, `save_pack` (pack.json), `save_pack_settings`
   (`packs.<pack>` of the settings), `new_pack` (from `empty` or a starter), `get_template_context`, `preview_unit`
   (with `overlay` for unsaved text; writes nothing), `unit_paths` and `get_pack_outputs`.

Every model read rescans the model folder and settings reads and saves check the file on disk, so edits made outside the server are seen; the resource `maquettiste://conventions`
and the prompt `modeling-conventions` carry this text.

## Fallback: editing the files

Without the server (or for bulk mechanical edits the tools do not cover), edit the files directly:

1. Change the JSON files (keep them canonical; the next save by the tool rewrites them anyway). New elements need a new
   ULID `id` (uppercase, 26 characters); references are ids.
2. `maquettiste validate` (exit 1 on errors; `--format sarif` for annotations). Fix dangling ids first.
3. `maquettiste generate --dry-run --diff` to see what would change, then `maquettiste generate`.
4. `maquettiste generate --check` is what CI runs: it fails on stale, missing, orphaned or hand-edited output.

If the editor or the MCP server is running on the repository, it picks up file changes (the editor live through its
watcher, the server on its next call). Prefer small, reviewable changes: one element per file makes the diff the review.

## Conventions worth knowing

- Names are PascalCase for entities and camelCase for attributes; table and column names come from the project's
  conventions (plural snake_case by default), overridable per element.
- `displayName`, `pluralName` and `description` feed generated UI and documentation; keep them meaningful.
- Reference data (units, countries, statuses that grow) is a reference type with its rows in a seed; an entity's
  starting rows are a seed of that entity. Deleting an element deletes its seeds and translations in the same save.
- The engine creates no table or column for reference data on its own: the packs decide the physical form, from the
  storage strategy the project declares. Stereotypes and their meaning are the project's own, not built in.
- Localization settings are `localization` in `maquettiste.json` (`defaultLocale`, `locales`, `fallbacks`, `require`);
  default texts stay in the element files.
