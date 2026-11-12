---
name: maquettiste-modeling
description: Editing a Maquettiste model (the JSON under .maquettiste/) in a repository that uses Maquettiste, validating it and generating code. Use when asked to add or change entities, relations, enums, types, reference data, seeds, translations, databases, mappings, diagrams, processes, actors, scenarios, operations or templates.
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
| locales/<locale>/ | translation shards (`kind: "locale-shard"`) keyed by id | only display name, plural name, description, a row's label and description, the text of a custom property marked `x-translatable` (field `properties/<name>`) and the `x-translatable` content text of a custom element or member (field `content/<name>`) |
| databases/<db>/ | database, tables (overlays), views, sequences | tables from entities store only overrides |
| databases/<db>/routines/ | routine (`kind: "routine"`): a function or procedure, `parameters` typed by a built-in type or a database type id, `returns`, a `body` per dialect | `dependsOn` orders it; routines are not overloaded |
| databases/<db>/types/ | database type (`kind: "database-type"`): a domain, composite, enum or range the database owns | a column uses it by naming its id or name in `nativeType` |
| databases/<db>/objects/ | SQL object (`kind: "sql-object"`): a trigger, grant, extension or anything else, statements per dialect | `phase` before or after the tables; `dependsOn` orders it |
| databases/<db>/queries/ | query (`kind: "query"`), in one of two forms. Built from parts: `from`, `joins`, `select`, `where`, `groupBy`, `orderBy`, `paging`, `collections` as JSON trees over the database's tables and views, with an `entity` as the row shape or, without one, the select list; `with` lists named queries (WITH), each `name`, `from`, `joins`, `select`, `where`, `groupBy`, `having`, `distinct` and an optional `recursive` part (`union`, `from`, `joins`, `select`, `where`), which the query's `from`, joins and nested queries and later named queries read by name as a source, their columns by `alias.<field name>`. Written as SQL text: `sql` (text per dialect or `"*"`, parameters written `:name`), `columns` (name, type, `nullable` default true; none for a command, which returns the rows changed), `reads` and `writes` (ids of what the text reads and the tables it writes) | built: column refs are `alias.<column key>` (the key `get_database_view` lists: an attribute id, a designed column's id), names accepted, an `sql` expression per dialect where a tree cannot say it; SQL text: the engine never parses it, so declare columns, reads and writes; never mix the two forms |
| databases/<db>/migrations/open/ | migration fact (`kind: "migration"`): what a change of the database's shape means for its stored rows (`rename`, `change-type`, `drop`, `backfill`, `raw`, ...) | recorded by the saves themselves; complete a pending one, never delete one by hand |
| mappings/ | projection overrides of older models, and relation mappings (`foreignKey`, junctions) | new work binds entities in their own file instead |
| processes/ | process: a statechart (states, transitions, events, guards, actions, invokes, gates, context) | `use` is `lifecycle` (`subjects`: entries `{ id, entity, boundAttribute? }`; the one-subject `subject`/`boundAttribute` form is deprecated, MQ9209) or `orchestration` |
| actors/ | actor: `type` person, role, group, organization, external system or system (shown as Automatic, at most one); `memberOf`; `relative` | raises events, signs gates, completes human tasks; may sit in a domain through `package` |
| scenarios/<process>/ | scenario: a recorded run of one process, each step with what it expects | owned by its process and deleted with it |
| operations/<subject>/ | operation: one command over the model (`subject`, `level`, `effect`, `input`, `outcomes`, `errors`, `guards`, `redefines`, `excludes`, `fires`, `requires`) | in `operations/` itself without a subject; the subject is not owning, so deleting the entity is refused while an operation names it |
| constraints/ | named constraint (`kind: "constraint"`): one condition in the expression language over typed `parameters`, with a `message` | cited by any check or guard with `satisfies`; see "Expressions" |
| custom/<type>/ | custom element (`kind: "custom"`): an element of a kind the project declares in `extensions/kinds/<type>.json`, its fields under `content` | see Custom kinds below |
| diagrams/ | canvas membership and positions only (a process's diagram holds its chart's places) | never own elements |
| vocabularies/ | tags, categories, stereotypes, verbs (`verbs.json`, kind `verb-vocabulary`: `hidden` built-in keys, own `verbs` of `{ id, key, displayName?, description? }`) | one verb file per project (MQ1009) |

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
   `get_project`, `list_validation_rules` and `tag_usage` answer a short summary first; ask for more only when you need
   it: `get_project` with `sections` (`settings`, `packs`, `extensions`), `list_validation_rules` with `family`
   (`MQ41xx`), `ids` or `query` for full entries, `tag_usage` with `tag` for the ids of the elements that hold one tag.
   Pass a page's `next` as `cursor` until it is null.
2. Read: `get_schema` for the kind before building a document (its `extensions` and `stereotypeParameters` constrain
   `properties`); `get_element` returns the document (`json`) and its `hash`. Read an element's `rationale` and
   `status` before you edit it: the rationale says why it is shaped this way, and a `deprecated` or `retired` element is
   not one to build on.
   Reading many elements: `get_model_kinds` (`by: "package"`) says what there is, `get_elements` returns documents in pages
   (`fields` keeps only the members you need), `get_resolved_model` returns what generation sees (resolved attributes, tables,
   processes) as flat records; pass each page's `next` as `cursor` until it is null, and never loop `get_element` over thousands of ids.
   What each record and field means, and when it is null: `.maquettiste/.schema/v1/resolved-model.md` (the JSON Schema beside it,
   or `get_schema` `resolved-model:<scope>`). Each page's `contract` is the version it follows; the copy matches the release.
3. Change: `save_element` (the whole edited document plus `expectedHash` = the hash you read), `create_element`,
   `delete_element`, or `apply_batch` for several changes that must land together (all or nothing).
   - To change part of a large element, `patch_element` takes its `id` (an id, never a name), an RFC 6902 `patch` over
     the document `get_element` returned and the same `expectedHash`, so you do not send the whole document back.
     Put a `test` before a `replace` to check you change what you read: `[{"op":"test","path":"/columns/3/name",
     "value":"body"},{"op":"replace","path":"/columns/3/name","value":"content"}]` renames the column.
     It saves like `save_element` (conflict check, validation, `facts`); a new column added without an id gets one,
     listed in `ids`. A path that does not resolve or a failed `test` is MQ1016 and nothing is written.
   - A script that refreshes elements re-runs one batch of `{"op":"upsert","id":"@kind:path","element":{...}}`: it creates
     the element when the name is new and replaces it otherwise, with no `expectedHash`. `id` must be a name the element
     will have; it never moves an element, and does not write seeds. An element that exists must send its parts' ids.
   - In `apply_batch`, write names instead of ids: every id field and reference takes a qualified name, `@kind:path`
     with the name as stored (`@database:main`, `@table:billing.notes`, `@column:billing.notes.body`,
     `@entity:Billing.Invoice`, `@attribute:Invoice.total`), and `"as": "$pm"` on a create names the new element for
     the whole batch, so other operations write `"$pm"` where its id goes. Send no ids for new elements and
     sub-elements: the engine mints them and the answer's `ids` lists every name and minted id. An ambiguous name
     (MQ1013) lists the candidates; use a longer form or the id. Run it with `dryRun: true` first: nothing is written,
     and the answer holds a diff per file and the diagnostics the change adds or removes.
   - Read the dry run's `impact` before you apply: `impact.referrers` lists the elements that point at what the batch
     changes or deletes (fix or include them), `impact.databases` the databases it can reach, and `impact.packs` per
     pack the units whose generated files would be written again (`inputs`, `deleted`, or `added` for new elements).
     Tell the user what it reaches, then apply. It names files, not their new bytes: after applying, `plan` with
     `elements` shows those.
   - Keep the ids of sub-elements you re-send: a sub-element without an id in an update is new, and dropping one and
     adding one with the same name without its id is refused (MQ1014).
   - Custom property values still written as names where an `x-ref` types an id (MQ2010): `apply_batch`
     `{"op":"names-to-ids"}` (optionally `"elementIds":[...]`) converts them all; never look the ids up one by one.
     Tags that carry a value (`status:draft`) move into a declared custom property with
     `{"op":"tags-to-property","prefix":"status:","name":"status"}`. Run both with `dryRun: true` first.
   - `conflict`: the file changed since you read it; nothing was written. Merge your change into `current` and retry with
     its `hash`. Never retry blindly with the new hash and your old document.
   - `invalid`: nothing was written; fix what `diagnostics` name (rule id, JSON pointer).
   - `referenced` (delete): the `referrers` list who points at it; fix them first, or pass
     `resolution: "remove-references"` when the references are optional, or `resolution: "delete-dependents"` to also delete
     what cannot exist without the element (call `delete_element` with `dryRun: true` first and read the plan).
   - Renaming an attribute or element is a plain save of the new `name`: references are ids, so nothing else changes
     (an element rename also moves its file).
   - A save that renames, retypes or drops a table, column or sequence, or makes a column not null, records migration
     facts in the same write and lists them in `facts`. A pending one (MQ4101 in the answer's diagnostics; the save went
     through) lacks its intent: read it with `get_element` and complete it with `save_element` (a `conversion`, a
     `dataCheck` for a narrowing, an `acknowledge` for a drop, a `value` for a backfill), or set `held: true`. Never
     delete a fact file by hand: `fold_migrations` (CLI `maquettiste migration fold`) removes stale facts and joins
     consecutive ones; `list_migrations` lists them in applied order.
4. Check: a successful save already returns `diagnostics`; `validate` (optionally scoped by `elementIds`) checks the model.
   With `against` (a model snapshot id, or a folder holding another copy of the project) it also checks that migration
   facts explain every change since that baseline (MQ4102); `plan` takes the same `against` to diff migrations from it.
   Before a larger change, `create_snapshot` first; when done, `compare_snapshots` with `from` that snapshot's id,
   `fieldChanges: true` and `format: "markdown"` gives the review by package (each member's field changes, the
   migration facts per database). Summarize it to the user by package, not by file.
   For a query, `preview_query_sql` returns the SQL it renders (for its database's dialect or another) with the diagnostics
   (MQ4021 to MQ4043, MQ4096 to MQ4098 for a query written as SQL text, MQ4099 for named queries) that point at the node to fix; a
   SQL text comes back as written (a final `;` kept), its `:name` placeholders in the asked style. For an entity's binding, `preview_binding_sql` returns its select,
   insert, update and delete statements; `validate` reports MQ4044 to MQ4054 for bindings.
   Custom property schemas (`extensions/<name>.json`) and script rules (`extensions/rules/<name>.js`, findings `x/<id>`, run by `validate`) are files: `list_extension_files`, `read_extension_file`, `write_extension_file` (with `expectedHash`, `new` to create; a rule's syntax error comes back at once), `move_extension_file`, `delete_extension_file`.
   A check across the model (every reference type used, no two tables alike) is a whole-model rule: `scope: "model"`,
   `check(model, report)`, each `report` naming its `element` id; it runs once per whole-model `validate`, never on a
   save. Read with `model.withStereotype`, `withTag`, `withProperty` or `byQualifiedName` rather than scanning
   `model.all`. For CI, set `limits.scriptTimeoutMs` to 0 so findings do not depend on the machine's load.
   A check of what generation sees (inherited attributes, calculated values) is a resolved rule: `scope: "resolved"`,
   `kinds` from the resolved kinds (`entity`, `attribute`, `table`, ...), `check(element, model, report)` per resolved
   object; it runs in `validate` and before generation plans, never on a save or a preview. A value several packs or
   rules need (a public name, an `apiName`) is a calculated value, defined once in `extensions/calculated/<name>.js`
   with `maquettiste.calculated({ name, kinds, schema, description, value(element, model) })` and nothing else;
   `write_extension_file` reports a bad declaration at once (MQ5002). Templates read `element.calculated.<name>` (test
   with `has_calculated element "<name>"`), scripts `e.calculated.<name>`. `get_calculated` with `ids` evaluates the
   values of those objects only, `get_resolved_model` pages carry `calculated`; `validate` reports a value that throws
   (MQ5010), misses its schema (MQ5011), is not declared (MQ5012) or loops (MQ5013). Add one only when asked.
   Prefer a declaration to a script for a simple convention ("every audited table has an updated_at column"): a file
   `extensions/rules/<name>.json` (`get_schema` `declared-rules`) whose rules each hold `id`, `severity`, `select` (`custom:<type>` as a kind picks one custom kind)
   (`kind`, with `stereotype`, `tag`, `property`, `package`), one `require` condition (`all`, `any`, `not`, `has`,
   `equals`, `count`, `references`, `referencedBy`, the last two with `excludeRetired` to skip retired links) and a `message` with `{path}` placeholders. It sees resolved objects,
   inherited members included; `write_extension_file` reports MQ5014 to MQ5016 at once, `validate` its `x/<id>` findings.
   When one element is a real exception to a rule, add a `suppressions` entry to that element (`rule`, optional `member`,
   and a `reason`, which is required: say why this element is the exception) instead of turning the rule off; never
   suppress to make `validate` pass without a reason the user gave. `validate` lists suppressed findings apart and warns
   MQ5020 for an entry that matches nothing.
5. Reference data and translations: `reference_type_usage` lists the attributes that use a reference type and its storage
   per database, and in `columns` the table columns whose `typeRef` names it;
   `create_seed` gives a reference type that has none its empty seed; `export_seed_csv` / `import_seed_csv` (a dry run unless `apply` is true, then `expectedHash`) move rows as
   CSV; `localization_status`, `get_translations` and `set_translations` (with the `shardHash` values you read as
   `expected`) read and write translations, which never go in element files. From a terminal, `maquettiste l10n status`,
   `l10n export|import <locale>`, `l10n prune`, `l10n set-default <locale>` and `maquettiste seed new|export|import` do
   the same; import, prune, set-default and seed import only preview until given `--apply`.
6. Generate: `generate` renders and writes in one call and answers a summary per pack (counts by kind, the first 50
   paths, at most 100 diagnostics, errors first); no plan is stored. When someone should review the files first, use
   `plan` (stores a plan, touches nothing), `get_plan` / `get_plan_diff` for the files that matter, then `apply_plan`
   with the plan id. `stale` means the inputs changed since the plan: plan again. To regenerate part of the model,
   `generate` and `plan` take `units` (`<pack>/<unit>`) and `elements` (ids, never names); every other file stays as it
   is. Migrations render only in a full run (a selected `sql-ddl/migration` is MQ6033): for one database's schema,
   pass `units: ["sql-ddl/schema"]` with the database id. `list_packs`, `get_settings` and `save_settings` (with
   `expectedHash`) cover the packs and `maquettiste.json`.
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
updates); one without an expression is a named stub that the generated code asks the team to implement. A guard may
hold a `condition` in the expression language instead (see "Expressions"), never both. A lifecycle's root-level states
must equal its bound enum's members, in order (MQ9203).
- Lifecycles are written as `subjects` (one lifecycle on several entities, several lifecycles on one entity); never create the
  deprecated one-subject form, and never convert a file unasked (`use-subjects` converts, MQ9209). A subject's `boundAttribute`
  may be an enum, a reference type or a plain string or integer. A state may carry a `code` (string or integer, unique in the
  lifecycle, none on history or choice states); under a binding the codes must form a map (MQ9206), fit the attribute (MQ9207),
  and cover every state (MQ9210 warns). `sync-codes` (`id`, `entity`, `attribute`, optional `stateCodes` by state id) proposes
  codes from the attribute's type and binds it; dry run first. Several instances of one lifecycle per object: model the object
  as a detail entity and make it the subject.
- A subject entry may hold `stereotype` (a key, never an id) instead of `entity`, exactly one of the two: it covers every entity
  carrying the stereotype and its derived entities (MQ9211 if it does not apply to entities). An entry naming the entity or a base
  wins over a stereotype entry. `boundAttribute` and `owner` there are the stereotype's own attributes. `sync-enum` and `sync-codes`
  take `stereotype` for `entity`. Never write one unasked.
- Verbs: `verb` (a key) on an event, on a transition with no event (MQ9705 with one) and on an operation (optional, not tied to
  `effect`). 27 built-ins in five groups (moves, signatures, tasks, objects, links; the list with meanings is in `get_schema` `common`
  `builtInVerb`); add own ones in `verbs.json`. An undeclared verb is MQ9701; an own key equal to a built-in is MQ9702. Hide built-ins with
  `hidden`. `rename-verb` (`verb`, `to`) and `remove-verb` (`verb`) follow every use; a plain key change in use is MQ9707. Verbs do nothing yet.
- A guard `condition` may read `subject.<attribute>` and to-one navigations (not the bound attribute; a stereotype's attributes are readable; MQ3102);
  a missing value reads as default, then null, and only true passes. A scenario's `objects` (`id`, `name`, `entity`, `values`
  by attribute id, `links` of `{ end, object }`) and `subject` (required with objects) give it something to read; `simulate_process`
  and `record_scenario` take both. `csharp-dapper` leaves such a guard as a partial method.
- Object effects: an action's `sets` is `[{ id, attribute, value }]`, `attribute` an id every subject holds
  (own, inherited, base or stereotype), `value` an expression tree (`{ "currentActor": true }`, `{ "now": true }`); not derived or state-bound attributes
  (MQ9802), not twice per action (MQ9803), none without subjects or on a member, collection or end (MQ9801), none on a library action (MQ9020). Sets run
  on exit, move, entry actions in that order; values read the object as it was when the action started; guards that pick the move see the old values.
  `current_actor()` (signer, else participant identity, else name; takes the other side's type, else string) and `in_state(subject.end, Lifecycle.State, ...)`
  (one lifecycle per call, MQ3116; to-one path; no SQL) work at process and operation guards and sets only (MQ3104). A scenario object's `states` (state
  ids; else the bound value; else the initial state, noted in the trace) feed `in_state` (MQ9305 on a clash); a step's `expect.values` is
  attribute name to value, exactly what changed (MQ9307). Traces add `sets` per action, `valuesChanged` and `notes`. `csharp-dapper` keeps a sets-only
  action as the team's stub and adds a `Record<Action>` hook; guards using the new names are left to a person. Never add sets unasked.
- Rights (lifecycles only, MQ9901): `rightsDefault` (`allow` when absent, or `deny`) and `rights`, each entry an inline right
  `{ id, name, access: allow|deny, actors?, except?, verbs (at least one), ends?, condition?, description?, states? }` or a use
  `{ id, right, states? }` of a named right (kind `right`, `model/rights/`, same keys without `states`). Empty `actors` means everyone; `except` never grants;
  `states` are this lifecycle's states (MQ9902), empty means all; `ends` only beside connect, disconnect, read, modify, freeze, thaw (MQ9903). `condition` reads
  `subject.x`, `current_actor()`, `in_state`, `now()`, not the context or payload; a named right's condition is typed at each use (MQ3102) and stays text with no use (MQ3108).
  Each answering lifecycle: its denies, then allows, then default; any no wins. Event actors, task actors and gate signers count as allows of their own lifecycle. Checks run
  in order: lists (`actor`), denies (`right`), defaults (`default`), no move, guards, gate checks; the trace has `refusal` and `refusedBy`. A scenario step may be `{ verb, values?, end?, object? }`
  and a refused step may expect `refusal` and `refusedBy` (MQ9308; MQ9305 when a step does not fit; `freeze` and `thaw` steps not yet). Warnings MQ9904 (allow where the default allows), MQ9905, MQ9906.
  `get_rights` (`process`, `states`, `actors`, `verbs`, `entity`) answers allowed, denied or depends per state, actor and verb. Never add rights unasked.
  Freezing: an accepted `freeze` event or step freezes the subject (or a link, with `end` and `object`), `thaw` thaws it; `frozen: true` on a scenario object or link starts it frozen.
  While frozen, `modify`, `connect`, `disconnect`, `delete` are refused before any right (`refusal: "frozen"`, no `refusedBy`); moves, `revise` and the rest are not. A right's `whileFrozen: true` holds only while frozen.
  MQ9309 warns when a step is refused as frozen after an earlier freeze; `verify_scenarios` gives it in `warnings`. Naming `freeze` is the opt-in; never add it unasked.
  Attributes: a right's `attributes` (ids every subject holds, only with `read` and `modify`, never with `ends`, MQ9903) speak of those attributes, each checked on its own; unknown attributes fail closed. A `read` step may name `attributes` (MQ9305 beside other verbs).
- Trigger points (lifecycles only, MQ8101): `triggerPoints`, each `{ id, name, type: check|action|replace, verbs (at least one), timing?, severity?, states?, targets?, transitions?, entities?, attributes?, ends?, when?, condition?, sets?, outside?, description? }`.
  Names are unique among the lifecycle's trigger points and its process's guards (MQ3001). A check has a `condition` (only true passes) or none (the team's code), `severity` `error` (default) or `warning`, and
  `description` is its message. An action needs `timing` (`before`, `with-change`, `after-commit`) and has `sets` or none; `before` fills values and cannot refuse; an after-commit action sets nothing (MQ8105);
  `outside: true` only on team code, warned on a with-change action (MQ8106). A replace (`delete`, `disconnect`, `copy`, `revise` only, MQ8103; one per verb and entity, MQ8104) takes the place of the built-in effect.
  Places narrow it: `states` (a move's source), `targets` (states a move enters), `transitions` (for `sign`, their gates), `entities`, `attributes` (only `read`, `modify`), `ends` (link verbs); a key that does not fit its verbs
  is MQ8102; a move verb no event names is MQ8107. `when`, `condition` and set values read `subject.x`, `old.x` (values before the verb; only here, MQ3104), `current_actor()`, `now()`, `in_state`; never the context.
  Order: frozen, rights, guards, signatures, then before actions, checks, the effect or replace, with-change actions, after-commit actions. A check that refuses a move leaves it out like a false guard (refusal `check`, `refusedBy`
  the trigger point); an automatic move it refuses is untaken and the step before stays accepted; a refused completing signature is uncounted and audited `refused`. An event that names `delete` still does its move; a replace never runs for it.
  What a trigger point sets is part of the verb: rights and freezing judge the actor's own step. In a scenario, `assume` maps a team-code check (no `condition`) to `pass`, `warning` or `error` (MQ9305 otherwise); a step may
  expect `checkWarnings` and `triggerPoints` (MQ9310); a `create` step may give `values`. An action written as team code fills nothing in a replay, so give a check the values it reads on the step. `get_trigger_sequence`
  (`process`, `verb`, `state`, `entity`, `attributes`, `end`, `transition`) lists what runs, in order. `record_scenario` writes `triggerPoints` only with `triggerPoints: true`. Never add trigger points unasked.
- Named trigger points (kind `trigger-point`, `model/trigger-points/<name>.json`): for a type with no lifecycle, or one rule shared by many types. `subjects` (entity and stereotype ids, at least one) say what it covers, `exceptEntities`
  what it skips; the other keys are an inline point's, except `states`, `targets` and `transitions` (MQ8102; read a state with `in_state` in `when`). `noBypass: true` keeps a check or action running when generated code skips optional points.
  What it reads and sets must be held under one id by every covered entity. Named points run before a lifecycle's points at each moment (farthest base first, entity-brought before stereotype-brought, then by name). On a move, signature or task verb
  one runs only inside the lifecycle that owns it, never in an orchestration. `get_trigger_sequence` takes `entity` alone for a type with no lifecycle. Simulation never bypasses. Never add one unasked.

- Revisions (an entity's optional `revisions` block; without it `revise` stays only a word that rights and trigger points name): `levels` (one to three, most significant first; each `{ name?, format: letters|numbers, or values: [...], first?, skip?, width?, startEmpty? }`, MQ8301), `separator` (`.` by default; `""` only between a letters and a numbers level, MQ8303) and `boundAttribute` (a single-valued string or text attribute, MQ8305; without it the team's system holds the revision). Written `A`, `A.1`, `B`; skipped letters never appear; `width` pads numbers; a list ends at its last value. A derived entity inherits the block: `levels`, `separator` and `boundAttribute` by nearest statement, the three lists by adding and replacing per lifecycle, attribute or end.
  The type says what a revise carries over, in `lifecycles` (`{ lifecycle, onRevise: restart|keep|<state id> }`), `attributes` (`{ attribute, onRevise: copy|reset|clear }`) and `links` (`{ end, onRevise: stay|move|duplicate }`, `duplicate` where the type's own end has max 1 is MQ8308); what a list does not name takes the fallback (lifecycles start over, values copied, links not said); an entry that can't stand is MQ8312. Never put this on a lifecycle, state or attribute.
  Two shapes of revising on a move: a transition's `nextRevision` (1 to 3, a level; only lifecycles, MQ8306) on a move whose verb is `revise` makes a new revision to work on (thawed, the type's lists apply), and on any other move (a release) labels the next revision in place with states, values, freezing and links unchanged. "May be revised in this state" is a right on `revise` with `states`. A scenario object may say `revision`; a verb step that names `revise` may give `revision` (a level or a chosen name later in the sequence); any step may give `expect.revision` (MQ9312); a revise the sequence can't give is refused with `refusal: "revision"`. Levels are named by number, so reordering them changes what level 2 means; a sequence in use should stay as it is. Never add a `revisions` block unasked: adding one makes existing revise steps move revisions, and `verify_scenarios` names the steps whose outcome changes.

- Approvals (richer sign-off): a gate with `stages` is an approval. The stage holds `signatures`, each with `name`, `signers`, `required`, `requiredActors`, `allowRepeatSigner`, `when` and `notBy`. On such a gate, `required`, `signers`, `requiredActors` and `allowRepeatSigner` go on each signature, never on the gate (MQ8201), and `allowSignerAcrossSignatures` lets one person answer more than once (MQ8201 on a gate without stages). `when` is a condition (`subject.cost > 10000`; reads the object, context, `in_state`, `now()`; not `current_actor()`): false means not needed, true or unknown means needed; judged when the object enters the move's source state and again after a rejection; not on a gate policy's signature (MQ8202). `notBy` is a list of `{ "verb": ... }` or `{ "transition": ... }`: whoever did one since the object entered the source state (the entering move included) may not give the signature; verbs must be ones the lifecycle's runs see (moves, signatures, `complete`, verbs its events name); a gate policy may name verbs only (MQ8202). A step on the gated event takes `response`: `sign` (default), `reject` (a veto: clears the approval, moves nothing), `ignore` (abstain: finishes the person, count unchanged) or `withdraw` (takes back their latest signature); `signature` names the one it answers, else the engine picks. A move on rejection is an ordinary transition whose verb is `reject`. A step may expect `signatures` (id to count; MQ9311). New refusals `gate-excluded` and `gate-not-signed`. A gate without stages takes `sign` only and keeps its meaning. Use an approval only when the team asks for named signatures, conditions or a veto; never convert a plain gate unasked.
- Approval rounds and notices (part 2, each optional): a stage's `round` (1 by default; stages listed in round order, MQ8203) runs it after the lower rounds complete, and its `when` skips it when false (not on a gate policy's stage, MQ8202). On a signature, `delegateTo` (actors) lets a signer hand it to someone who meets one of them; `remindAfter`, `escalateAfter` (positive ISO 8601 durations from the round's start, MQ8204) and `escalateTo` (actors; goes with `escalateAfter`) say when the running system reminds the people who may still answer and tells the `escalateTo` actors, once per round start; `reauthenticate: true` asks generated code to check the person's identity again for every response (simulation takes it as done). A step's `response: delegate` with `delegateTo` (a participant) hands the signature over; that participant then answers with `onBehalfOf` (the delegator), and the answer counts as the delegator's actor; a delegation can't be passed on. A time step lists the notices that fell due; a step may expect them as `notices: [{ signature, kind: remind|escalate }]` (a set; MQ9311). Notices never move the object; a move on a timer stays an `after` transition. Never add these unasked.
- Tools: `simulate_process` runs inputs through the engine's interpreter and keeps no state (send the whole input list each
  time; `enabled` says what can happen next); `record_scenario` saves a run as a scenario with every step's expectations
  filled from the replay (`dryRun` to preview); `verify_scenarios` replays scenarios; `export_process` and `import_process`
  (a dry run unless `apply`) move a process to and from an XState machine config; `sync_enum_from_process` (a dry run unless
  `apply`) makes a lifecycle's bound enum follow its states (`entity` picks the subject) and never removes a member still in
  use. `apply_batch` has the process operations `sync-enum`, `sync-codes`, `use-subjects`, `set-lifecycle` (binds an entity and a process on both sides), `set-initial`, `rename-verb`, `remove-verb` and
  `refresh-scenario`.
- Actors: `memberOf` (ids of roles, groups or organizations, no cycles) makes a list that names a holder accept its members
  at any depth. `relative` takes one form: `{ owner: true }`, `{ path: [ids] }` (a field or to-one ends of the subject), or
  `{ membersOf, within: [ids] }` (a role within a context). Such an actor has no `memberOf` or members, and a role held with
  no context does not count within one. A subject entry's optional `owner` names the single-valued attribute or to-one end
  that holds the owner. A scenario's `participants` (`name`, `identity`, `actor`, `holds: [{ id, actor, within }]`, `object`)
  and a step's `participant` let the engine work out the actor the step acts as (`step.acts_as`). MQ9601 to MQ9606, MQ9301
  and MQ9305 check them.
- Scenarios are the process's tests: `validate` replays every scenario (MQ93xx), and the packs turn each scenario into a
  generated test. When a chart change breaks a scenario on purpose, refresh its expectations (`refresh-scenario`) rather
  than editing `expect` by hand; when it breaks by accident, fix the chart.
- A scenario's `payload`, `start.context` and `expect.context` maps are keyed by attribute id; `assume` gives the result of a
  guard without an expression.
- From a terminal: `maquettiste process simulate|record|verify|export|import|sync-enum|sync-codes` (record, import, sync-enum and sync-codes only
  preview until `--apply`; `verify` exits 1 on a failing scenario).
- The engine creates no table and runs no process in the application: where instances, history and audit records live is
  the project's mapping or a pack parameter (`processTables` in sql-ddl). Never add entities or databases for them unasked.

## Operations

An operation is one command over the model, in one file: an optional `subject` entity, `level` (`record`, the default,
takes the subject record's key implicitly; `collection` acts on the whole set), a required `effect` (`read` or
`change`), `input` members, one or more `outcomes`, `errors`, `fires` and `requires`. No operation is implied per
entity; model only the commands asked for.

- An input member is exactly one of a typed value (`type`, as an attribute takes it), a record key (`keyOf`, an entity
  id; with `collection`, a list of keys) or a file (`file: true`, with `mediaTypes` and `length` as a byte cap)
  (MQ3024). A record key takes no `default`, `length`, `precision`, `scale` or `validation`; a file no `default`,
  `precision`, `scale` or `validation`; only a file takes `mediaTypes`.
- An outcome `completes` `now` (the default) or `later` (accepted and waiting). Its optional `returns` is exactly one of
  `members` (an inline shape; no `default`), `entity` (records of that entity) or `file` (a download, with
  `mediaTypes`), with `list: true` for a list of members or records; a list may set `paging` (`page` or `cursor`) with
  `maxPageSize`, always together, and declares no paging input members (MQ3025). `defaultOutcome` names one of the operation's own
  outcomes; absent, the first.
- An error has a `name`, an optional `code` (the stable text clients see; absent, the name) and an optional `input`, one
  of the operation's own input members (MQ3027). Names and codes are unique per operation, own and inherited (MQ3026).
- Errors many operations share go on a stereotype that applies to operations (MQ3029), in its `errors` (no `input`).
  Every carrier inherits them as `<operation id>.<error id>`; the operation's `excludes` drops one by the stereotype
  error's id, and a `redefines` entry (`member`, then only `input`, `displayName` or `description`) adjusts one
  (MQ2027, MQ2028). Never copy a shared error as an own one; change its code on the stereotype.
- `fires` lists process event ids, of any process: when the operation's work is done, the team's system sends them (on which
  outcome, in what order and transaction is the team's). A `read` operation that fires one is MQ3030; an operation on another
  subject than the process governs is MQ3032; a required payload attribute with no input member of its name and type is MQ3044.
  `requires` lists ids of
  any element or sub-element that stand for what the operation requires (an actor, a permission code's row); the engine
  gives it no meaning, and it may not name the operation, its own parts, a diagram or a migration fact (MQ3031).
- A process action may call one operation: `"call": { "operation": <id>, "inputs": [{ id, member: <input member id>, value }] }`,
  beside `expression` and `sets`; each value is typed like a set's value against the member (MQ3103), and a required member
  left out is MQ9026; file and collection members, record keys of an entity with a composite key or none, and members of a
  type expressions lack (binary, float, a value object) can't be given (MQ9025); none in a library (MQ9020). A
  service step (an invoke of type `service`) may name its `operation` (MQ9025 on other types). Both say what the team's
  system runs; the simulator runs nothing for a call and records it in the trace's `call`. Never add calls unasked.
- In `apply_batch`, `@operation:Invoice.send` finds `send` on Invoice, and `@operation-member:Invoice.send.recipient`,
  `@outcome:Invoice.send.waiting` and `@error:Invoice.send.missing_recipient` its parts.
- Templates read operations through `each operation` (the variable `operation`; `type_of` maps a member), and
  `get_resolved_model` has the scope `operations`. csharp-dapper's `operation-contracts` unit writes each operation's
  request, result, error codes and handler interface, and `operation-endpoints` one HTTP route per operation when the
  pack parameter `endpointsFolder` is set.

## Expressions

Checks, derived values and guards are written in one small language that reads like SQL (`issuedOn <= dueOn`). Files
hold it as a tree of ids, so a rename never breaks it; the text is printed from current names.

- Write text, never trees: wherever a tree goes, send `{ "text": "issuedOn <= dueOn" }` to `save_element`,
  `create_element`, `patch_element` or `apply_batch`; the engine reads it with the site's names and stores the tree.
  Text that does not parse refuses the write (MQ3101, with the position). Never write a tree's ids by hand. A rule
  file keeps a declared rule's `expression` text as written: its paths are member names by design, so no tree is
  needed.
- Sites: a table or column check's `condition` (beside the SQL text of `expression`), the entity's `checks` (`name`,
  `attribute` a failure points at, `condition`, `message`), an attribute's `derived.value`, a process guard's
  `condition` (beside the JavaScript `expression`), an operation's `guards` (`condition`, the `error` it reports) and a
  declared rule's `expression` (`member` paths). A rule used in several places is a `constraint` element, cited as
  `satisfies PositiveAmount(amount: total.amount)`. A site holds its raw form or its tree, never both (MQ3107).
- Names are the site's: attribute or column names, `total.amount` for a value object member, `InvoiceStatus.Paid` for
  an enum member; at process guards also `event.amount` and `now()`; at operation guards also `subject.status`, to-one
  navigations from the subject (`subject.customer.customerSince`) and `now()`. Checks and derived values read one row.
  Dates move with `add_days` and `add_months`, a date meets a timestamp through `date_of`, times are UTC, and patterns
  write `[0-9]`, never `\d`.
- Nulls: a check passes when the answer is unknown, as a CHECK does; a guard or a rule condition holds only on true.
  Whether a value must be given is the attribute's `required`: never write it into the expression.
- An entity check reaches a table only when a binding lists its id in the binding's `checks`; then sql-ddl writes the
  CHECK. MQ3115 warns on a check that a binding could enforce and none lists; ask before listing it.
- Before saving, `preview_expression` (`element`, an id, then `at` for a stored expression or `text` with `site`:
  `check`, `value`, `guard` or a site's pointer; `values` by member name adds the answer) shows the printed text, the
  type, the JavaScript and the PostgreSQL text. It reads one element and writes nothing (CLI
  `maquettiste expression preview`). `get_resolved_model` gives each entity check, derived value, guard and constraint
  condition as `{ text, type, nullable, tree }`; read a table check's text with `preview_expression`.
- Text left in a file by hand is MQ3108: `apply_batch` `{"op":"text-to-trees"}` (optionally `"elementIds":[...]`)
  converts it; run it with `dryRun: true` first.
- Never convert a process's JavaScript guards unasked. When asked: preview the text at the guard's pointer
  (`/guards/0/condition`), compare its answers with the JavaScript's in `simulate_process`, mind null values, `!` on a
  boolean that may be null and loose `==` (JavaScript reads them otherwise), then save `condition` and remove
  `expression` in one write and run `verify_scenarios`.

## Custom kinds

A project may declare its own kinds for facts no built-in kind holds (settings, messages, feature flags): one file per
kind, `.maquettiste/extensions/kinds/<type>.json` (`type` kebab-case and equal to the file name, `displayName`,
`pluralName`, `description`, `properties` as JSON Schemas per content key, `required`), written with
`write_extension_file`. Declare one only when asked; never invent a custom kind for something a built-in kind models.

- An element of the kind is `{ "kind": "custom", "name": "early_invoicing", "type": "feature-flag", "package": ...,
  "content": { "enabled": false } }`, in `model/custom/<type>/`. Names are unique per type and package. Content that
  fails the schema is MQ3033; a `type` with no kind file is MQ1019; a bad kind file MQ1017. `get_schema` with
  `custom:<type>` returns the element schema with the kind's content.
- An array marked `"x-member": true` (items are objects, no `id` declared) makes each item a member: a sub-element whose
  id the engine mints on save, named by its item's `name`. `"x-ref": { "kind": "member" }` names a member of any custom
  element, `"of": "$element"` one of the same element; `"x-ref": { "kind": "custom:<type>" }` names a custom element.
- Names carry the type: `@custom:feature-flag.early_invoicing` (or `@custom:feature-flag.Billing.early_invoicing`) and
  `@member:message.invoice_sent.subject`; the plain name is not a form. `apply_batch` returns minted member ids under
  `ids.minted` with their longest form.
- An `x-translatable` top-level string of the content or of member items is translated as `content/<name>`; a member's
  text is translated on the member's id (`set_translations`).
- Templates: a unit `"for": "each custom:<type>"` (variable `custom`: `type`, `content`, `members`), and from any unit
  `custom_named "<type>" "<name>"` or `lookup` of an id; `referenced_by <element or id> ["<field>"]` lists what links
  to one. `get_template_context` lists the content keys under
  `custom.content`. `get_model_index` and `get_elements` filter by kind `custom` or `custom:<type>`.
- An operation's `requires` may name a custom element; deleting it is then refused (`referenced`) until the link goes.

## Fallback: editing the files

Without the server (or for bulk mechanical edits the tools do not cover), edit the files directly:

1. Change the JSON files (keep them canonical; the next save by the tool rewrites them anyway); references are ids. To
   add elements, write a batch file instead and run `maquettiste model apply <batch.json>` (`--dry-run` first): it takes
   the `apply_batch` shapes, names and placeholders, and mints every id.
2. `maquettiste validate` (exit 1 on errors; `--format sarif` for annotations). Fix dangling ids first.
3. `maquettiste generate --dry-run --diff` to see what would change, then `maquettiste generate` (`--element <id>` and
   `--unit <pack>/<unit>` limit it to some units and leave every other file as it is).
4. `maquettiste generate --check` is what CI runs: it renders every output root and fails on stale, missing, orphaned or
   hand-edited output (guard only what the team commits, or run `generate` and compare the working tree).

If the editor or the MCP server is running on the repository, it picks up file changes (the editor live through its
watcher, the server on its next call). Prefer small, reviewable changes: one element per file makes the diff the review.

## Conventions worth knowing

- Names are PascalCase for entities and camelCase for attributes; table and column names come from the project's
  conventions (plural snake_case by default), overridable per element.
- `displayName`, `pluralName` and `description` feed generated UI and documentation; keep them meaningful.
- Relation ends: an end writes exactly one of `entity` (one entity), `types` (a list of `{ "entity": id }` or `{ "stereotype": key }`
  entries; a stereotype covers the entities that carry it) or `anyEntity: true`. A list of one entity is MQ3042: write `entity`. Both
  ends of a two-ended relation may have several types or be any entity; an n-ary relation's ends name one entity each, and a composition's
  part end can't be `anyEntity` (MQ3041); an entry another entry covers is MQ3043. `role`, `navigation`, `min`, `max` and `onDelete` are
  one per end. A path through such an end reads what every type holds under one id; `in_state` through it is unknown for an object of a
  type the lifecycle doesn't govern. Seeds and query fields through it are refused (MQ7105, MQ4116). Never add a shared base entity or one
  relation per type just to avoid this, unless asked.
- Storing a relation with an end of several types is optional: without a relation mapping in a database nothing is stored there (MQ4115,
  info), and entities and tables may stay separate models. To describe the team's existing tables, the mapping's `ends` entries say per end
  how the table that holds the link points at it, in exactly one way: `foreignKey` (one key; on an end with several types, a key to a table
  that holds every type's rows), `foreignKeys` (a key in each type's table), `types` with `{ "entity", "foreignKey" }` items (a key per type),
  or `idColumn` with an optional `typeColumn` (the linked object's type code); `referencesColumn`, on the entry or per type, names the unique
  column the id refers to when it isn't the table's one-column primary key. An end with no entry is the end whose rows hold the link;
  `junctionTable` names a link table, and a link table per type needs `foreignKeys` at one end and `types` at the other. Ids and keys are
  table columns, never the entity's key attributes. A type code is the entity's binding's `discriminatorValue` in that database, else its
  mapping's, else its name; two types with one code are refused. An any-entity end takes a type and an id, an id alone, a key to a shared
  table or a link table, never a key or table per type. Any mapping with a designed `junctionTable` may write `constants` (`{ "column", "value" }`:
  fixed values that pick this relation's rows in a link table several relations share) and `attributes[].column` (a relation attribute's
  column). An entity binding maps a held end's id column as `<endId>`, its type column as `<endId>.type` and a key column per type as
  `<endId>.<entityId>`. MQ4117 refuses what doesn't fit together, MQ4118 (info) lists what the database can't enforce with the check or
  unique to copy into the team's table, MQ4120 warns about a placed type no pointer reaches, and `validate --against` warns when a stored
  type code changed since the baseline (MQ4119): set `discriminatorValue` to the old code to keep stored links. The engine never adds a
  column, key or check for this. A covered type with no table in the database is left out with no finding, a read-only binding's entity
  (write `none`, or a view or query source) included; `ignore` on the mapping stores nothing there. A link table for each pair of types
  and one column pointer spread over several types' tables are refused (MQ4117). The editor's relation form offers several types at
  either end or both.
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
  "tables":[...],"package":...}`; to give a bound entity attributes for its table's unmapped columns,
  `{"op":"materialize-attributes","database":...,"entities":[...]}` (add `"columns":[...]` for some of one entity's); to give its
  table columns for its unmapped attributes, `{"op":"materialize-columns","database":...,"entities":[...]}` (`"attributes":[...]`
  for some). From a terminal: `maquettiste model materialize tables|entities|attributes|columns ... --dry-run`. Which
  tables to materialize, and when, is the user's decision.
- Bindings record their `provenance` (two short hashes per column of a table file); the engine writes it, so never write
  or copy it by hand. When a bound table changes, `get_binding_drift` lists per binding the columns added, renamed,
  retyped and removed since (MQ4087 is a mapped column renamed or retyped, MQ4088 provenance that does not describe the
  source). Catch up with `preview_materialize` and then `apply_batch`
  `{"op":"resync-binding","database":...,"entities":[...]}`: attributes that followed their column are renamed (ids
  kept; add `"keepNames":true` to keep every name), retyped columns retype their attribute, added columns get
  attributes, removed columns lose their fields; `"columns":[...]` limits it to some of one entity's.
  `{"op":"map-by-name","database":...,"entities":[...]}` maps unmapped attributes to free columns of the same name. From
  a terminal: `maquettiste binding drift|map|resync`. An attribute without a description shows its bound column's
  comment, so do not copy a comment into a description.
- Seeds: a seed's `target` is an entity, a relation, a reference type, or a table file (a table seed: rows the database needs
  without an entity, columns by column id, cells as column values, `key` a unique constraint id). `environments` (empty: every
  environment), `apply` (`once` default, or `converge`) and `delete` say how rows are applied; `rowsFrom: {"file": "<name>.csv"}`
  keeps large rows in a CSV beside the seed (send rows as usual; the engine writes the CSV).
- Natural keys and stable ids: a table seed may list a foreign key of its table in `columns` (by id, or by name when you
  write it), instead of the key's own columns, and give the referenced row's natural key in the cell
  (`"ada@example.com"`, or `["ACME", "EU"]` for a composite key); never copy a referenced row's id or UUID into a cell.
  A `uuid` column the database does not fill may say `"stableId": true`: the engine derives its value from the table and
  the row key (UUID version 5), so leave it out of seeds (MQ7108) and key such a table's seeds by a unique constraint,
  not by that column (MQ7125). Changing `stableId` or a seed's `key` changes the ids of rows already in databases: ask
  first.
- Scripted rows: a seed may take its rows from `rowsFrom: {"script": "seeds/<name>.js"}`, a file under
  `.maquettiste/extensions/seeds/` written with `write_extension_file`, which calls
  `maquettiste.rows((model, seed) => [{ <column name>: value }])` once and reads the model like a script rule. Such a
  seed has no `rows` of its own and takes no CSV import. Check the result with `preview_seed_rows` (one seed, written
  or scripted; `database` gives the values that database receives) rather than reading generated output, and fix
  MQ7127 (the script failed) and MQ7128 (a key no column has, or no row key) at the script.
- Seeds by name: when you write a seed, `columns` and `key` may hold names instead of ids (a table's column and unique
  constraint names, an entity's attribute names and to-one end roles, a relation's end roles and attributes, a reference
  type's attribute names, where `code`, `label` and `description` stay keywords), and a row may give
  `"values": {"<column>": ...}` keyed by the `columns` entries as you wrote them. The engine stores ids and positional
  cells; a name that finds nothing or several is MQ1002 and nothing is written. In a batch, `@column:...` names and
  placeholders resolve first, so a seed can name the columns of a table created earlier in the same batch. Templates
  find a seed with `seed_named "currencies"` and a row by its key with `seed_row`.
- Tags: `tag_usage` counts each tag's uses in a vocabulary's scope (and the undeclared ones in use); to remove tags from the
  vocabulary and every use, or rename one, use `apply_batch` `{"op":"retag","tags":[...],"name":<new key>,"package":<domain>}`
  rather than editing every element.
- Typed links: a custom property whose schema has `x-ref` (`{"kind":"attribute","of":"$element"}`, `{"kind":"entity"}`)
  holds ids, never names, exactly like a reference field; `get_references` lists such values with their `schema`, and
  deletes clear or refuse them by the schema's `optional`. MQ2010 is a name where the id belongs, MQ2011 a target out
  of the `of` or `sameDatabase` scope. Beside elements, attributes and states, a schema can apply to `guard`, `action`,
  `invoke`, `gate`, `check` and `constraint`; properties on a check never change its SQL.
- Shared process parts: a process with `use: library` holds `guards`, `actions` and `gates` once and has no states, events or
  context (MQ9020). Any process names them by id in `transition.guard`, `transition.actions`, `state.entry` and `state.exit`, and
  names a gate with `transition.gatePolicy` in place of an inline `gate` (MQ9021). A library's guard is an `expression` that reads
  the using process's `context` and `event`; it has no `condition`. Nothing runs a library (MQ9022).
- Stereotype rules: a stereotype's `group` (one per element), `requires`, `conflicts` and `implies` (one level) bind
  every element that carries it, implied stereotypes included (MQ2013 to MQ2017); its `parameters` type the
  `properties` of its carriers, with `defaultProperties` as defaults (MQ2018). Read the stereotype before adding or
  removing one on an element.
- Stereotype members: a stereotype that applies to tables may carry `columns`, `primaryKey`, `uniques`, `foreignKeys`,
  `checks` and `indexes`, which every table that carries it inherits, also through a database schema's or a package's
  `defaultStereotypes`. Write an inherited member as `<tableKey>.<memberId>` wherever its id goes (seeds, keys,
  indexes, binding fields, queries): the table file's id, or the synthesized key (`<entityId>@<databaseId>`) for a
  table without one; `get_database_view` shows each keyed id with its `memberOrigin`. Change a member on the
  stereotype, or for one table with `redefines` (facets only; the id stays) and `excludes` (member ids); a table drops
  a default with `excludeStereotypes`. Never copy an inherited member into a table as an own column. To turn own copies
  into inherited members, use `apply_batch` `{"op":"fold-into-stereotype","id":<stereotype>,"tables":[...]}` with
  `dryRun` first; it records one `rekey` and the database does not change.
- Layers: a package's `dependsOn` lists the packages its elements may reference (with their sub-packages; a parent's
  list applies to its subtree). A reference to any other package is MQ2020: when you add one, check the layers first,
  and never add a `dependsOn` entry just to silence it, since the direction of the layers is the user's decision.
  `package` on a table, seed, stereotype or other kind is membership only and never changes a database.
- Status: `status` is `proposed`, `approved`, `deprecated` or `retired`, with `rationale` (Markdown) and `decision` (a
  URL or a record such as `ADR-0042`) beside it. A reference to a retired element is MQ2022: repoint it rather than
  adding new ones. Write `rationale` when you make a choice the user will want explained.
- Reference data (units, countries, statuses that grow) is a reference type with its rows in a seed; an entity's
  starting rows are a seed of that entity. Deleting an element deletes its seeds and translations in the same save.
- The engine creates no table or column for reference data on its own: the packs decide the physical form, from the
  storage strategy the project declares. When a table file already holds a reference type's rows, the type's optional
  `bindings` say so per database (`database`, `table`, `fields` mapping `code`, `label`, `description` or a field id to a
  column, `constants` for a table several types share); its rows then count as the table's rows in validation and seed data. Stereotypes and their meaning are the project's own, not built in.
- A table column that holds an enum's values, a reference type's codes or a scalar type's values says so with
  `typeRef: {"ref": "<type id>"}` beside its `type`, which stays the physical keyword; facets it leaves out come from
  the type, and packs give it the same key or CHECK as an attribute of that type. A declared foreign key from such a
  column points at the code column of the type's bound table (MQ4084). A column holding personal data or a secret states
  `sensitive` (`pii` or `secret`); say it once, on the column or on the attribute a binding maps to it (two different
  values are MQ4085, a warning).
- Codes that databases store change through a refactor, never by editing a seed cell or a type's name: `apply_batch`
  with `{"op":"rename-code","id":<type>,"code":"net30","to":"net-30"}`, `retire-code` (the same keys; `to` a live code),
  `merge-reference-types` (`id` the type that goes, `target`, optional `codes`) or `rename-reference-type` (`id`,
  `name`), with `dryRun` first to read the item's `usage` and the facts. Each keeps the old code in the type's
  `formerCodes` and records a `value-map` fact per database, so each environment's migration moves the stored values.
  Write the replacing row's code, never a former code (MQ7019), and never bring a code back (MQ7020, MQ4113). A plain
  edit of a kept row's code warns MQ4114: undo it and use the refactor. Retiring or merging rows is the user's decision.
- Tag, stereotype or typed field: a tag is a label for grouping and search that no generation depends on
  (`domain:billing`); a stereotype is a named, reusable meaning that changes what is generated or validated (`audited`,
  `tenant-scoped`, a `high-churn` table storage profile); the values a feature needs are typed fields (a table's or
  index's `storage`, a foreign key's `onDeleteColumns`), never strings packed into a tag (`storage:fillfactor=90` would
  be parsed and never validated). A database feature the model has a field for (temporal keys, exclusion constraints,
  partitioning, storage parameters, view security options, routine volatility and settings, an index column's `nulls`,
  range columns) goes in that field; what it has none for (grants, extensions, policies, triggers) goes in a SQL object
  with `dependsOn`, which a migration runs again when it creates what it depends on again.
- A range column is `type: "range"` or `"multirange"` with a `subtype` (`int32`, `int64`, `decimal`, `date`,
  `datetime`, `datetimeoffset`), not `string` with `nativeType: "tstzrange"`; values are its text form (`"[1,10)"`), and
  only columns take it, never attributes. An index column's `nulls` (`first` or `last`, PostgreSQL) changes the index
  only when it differs from the direction's default.
- Name indexes that share columns: two indexes on the same columns (a btree and a gin) both get the conventional
  `ix_<table>_<columns>`, and that is MQ4068, as is an index or key named like another index, key or table where the
  dialect keeps one namespace for them (a PostgreSQL schema holds index and key names with its tables). Names compare
  without case after cutting to the identifier limit (63 bytes on PostgreSQL), so two long names that start alike
  collide too.
- The migration ledger is bookkeeping, not part of the model: on PostgreSQL the sql-ddl scripts record the migration
  facts they reach in three tables (`contract`, `applied_fact`, `applied_object`) in the schema
  `migrations.ledgerSchema` names (default `migration_ledger`). Never model those tables, and keep the ledger schema
  apart from every modeled schema (MQ4067). For a team's own migrator, `get_ledger_ddl` (`grantRead` for the read grant)
  or `maquettiste migration ledger --grant-read <role>` prints the DDL; the engine never reads the ledger.
- A SQL object that belongs to some environments only (a dev view, test data) lists them in `environments`; sql-ddl then
  runs it only in `objects.<env>.sql` on every deploy, so its statements must be safe to run again (`CREATE OR REPLACE`,
  `IF NOT EXISTS`). A view, routine or SQL object may depend only on a SQL object that runs in every environment it runs
  in (MQ4069). `apply: "once"` runs a SQL object one time per database behind a ledger guard (PostgreSQL only; no
  transaction control or `CONCURRENTLY` in it), and a change to it never runs again. Switching an existing object from
  `converge` to `once` runs it one more time everywhere (the ledger has no row for it yet; the migration says so).
- Localization settings are `localization` in `maquettiste.json` (`defaultLocale`, `locales`, `fallbacks`, `require`);
  default texts stay in the element files. Locale tags are BCP 47 with a hyphen and canonical case (`zh-CN`,
  `zh-Hant-TW`, `fr`), never `zh_CN` (MQ7201).
- `name` and `branding` (`icon`, `colors.light`, `colors.dark`) in `maquettiste.json` only brand the editor; they never
  change generated output.

## This repository's conventions

A repository keeps its own modeling conventions in `.claude/skills/maquettiste-modeling/CONVENTIONS.md`, whichever
copy of this skill you are reading (the same skill can sit in `.agents/skills/` or `.kiro/skills/` too). When that file
exists, read it after this one and follow it; where the two
disagree, it wins. Over MCP, the `maquettiste://conventions` resource and the `modeling-conventions` prompt end with it.
Add project rules there, never here: `maquettiste init --skill` rewrites this file and never touches `CONVENTIONS.md`.
