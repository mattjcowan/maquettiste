# Template packs

**Owner:** W10 Packs. See docs/engineering/engine-design.md sections 8, 9 and 18.

This folder holds the example packs (two from phase 1, `process-docs` from phase 3, `seed-data` from 2026-10-07), and this file is the guide to writing a pack
of your own.

| Pack | Default root | Generates |
| --- | --- | --- |
| [`sql-ddl`](sql-ddl/README.md) | `db` | Table DDL for PostgreSQL, SQL Server and SQLite, one schema script per database, `once` migrations from the schema diff, a `regions` seed script, and (with `processTables`) instance, history and audit tables per process |
| [`csharp-dapper`](csharp-dapper/README.md) | `src/Generated` | Entity `pair` files, enums, value objects, a Dapper repository per mapped entity, one registration file per package, `types/csharp.json`; per process the states, definition and contracts, handler, service, machine and store pairs, `regions` endpoints, a typed dispatcher with pipeline behaviours, a generated statechart interpreter, one xunit test per scenario and, with `gitignorePath`, a managed block in an ignore file |
| [`process-docs`](process-docs/README.md) | `docs` | Markdown pages: one per process (a state diagram in diagram text, states, transitions, gates and their audit record, events, actors), one per actor, a walk-through per scenario, and an index |
| [`seed-data`](seed-data/README.md) | `data` | Per database, the rows every seed gives it in database terms and foreign key order, for a loader: a manifest and one CSV per table, or one JSON file |

`sql-ddl` and `csharp-dapper` are embedded into the CLI: `maquettiste init --pack <name>` copies one into
`.maquettiste/templates/<name>/`, and `maquettiste pack new <name> --from <pack>` starts a new pack from one; the bench generates
them over a synthetic model of 5,000 entities. A project uses `process-docs` or `seed-data` by copying its folder into
`.maquettiste/templates/` (each README shows the settings). The tests of all four are in `tests/Maquettiste.Packs.Tests/` (golden output under
`tests/fixtures/golden/`; `MAQUETTISTE_UPDATE_GOLDEN=1` rewrites it).

## Writing a pack

### 1. The folder and `pack.json`

A pack is a folder `.maquettiste/templates/<name>/` whose `pack.json` has the same `name`. Start with
`maquettiste pack new <name>` (or `--from sql-ddl`/`--from csharp-dapper`).

```json
{
  "name": "my-pack",
  "version": "1.0.0",
  "engine": ">=1.0 <2.0",
  "parameters": { "namespace": "App.Model" },
  "units": [
    {
      "id": "entity",
      "template": "entity.scriban",
      "for": "each entity",
      "where": { "stereotypes": ["aggregate-root"] },
      "output": "{{ entity.package.name }}/{{ pascal entity.name }}.g.cs"
    }
  ]
}
```

- **Parameters** are defaults; a project overrides them in `maquettiste.json` under `packs.<name>.parameters`, and templates read
  them as `pack.params.<name>` (keys keep their spelling). Values the whole project shares (a base namespace, a company name) are
  the project's `properties` (Settings › General), which every pack's templates read as `project.properties.<key>`.
- **`for`** is `model` (one unit), `each package|entity|attribute|relation|enum|value object|database|table|reference
  type|seed|locale|process|actor|scenario` (one unit per element; `table` covers every database), or `select <name>`
  with a JavaScript selector. `each database` runs once per database (element `database`), as the database units of
  `sql-ddl` and `seed-data` do; a selector that returns `model.databases` does the same. `each attribute` runs once
  per attribute declared in a model file, at its entity, relation, value object or reference type (element
  `attribute`): never an inherited copy, a stereotype's added attribute or a promoted relation's copy.
- **`where`** filters by tags, stereotypes, categories, packages, `database` (tables in it, entities and relations
  mapped there; it also picks `mapping`), `abstract` and a JavaScript filter. On `each process`, `each actor` and `each
  scenario` it takes tags, stereotypes, categories and packages only (a scenario's package is its process's; an actor
  has none): `database` and `abstract` are refused when the pack loads (MQ6001). On `each database` it reads the
  database file and `database` matches the name; on `each attribute` it reads the attribute's own tags, stereotypes and
  category, its owner's package, and `database` keeps the attributes of entities and relations mapped there (a value
  object's or reference type's only when a database of that name exists). Both refuse `abstract`.
- **`output`** is a Scriban expression rendered with the unit's context, relative to `packs.<name>.output`. Never repeat that
  prefix: `init` sets it (`db` for sql-ddl, `src/Generated` for csharp-dapper), and a project may move it.

### 2. Choose the output mode

| Mode | Use it for |
| --- | --- |
| `overwrite` | Fully generated files (the default). |
| `once` | Scaffolds the team takes over: migrations, starting points. Written only when missing, never deleted. |
| `regions` | Generated files with hand-written islands between `maquettiste:keep id=<id>` and `maquettiste:end-keep` lines. Any root; the region bodies live only in the file, so keep it in a folder the team commits. |
| `pair` | A generated file plus a companion written once (`Invoice.g.cs` + `Invoice.cs`), the customization pattern for output the team does not commit. Needs `companion.template` and `companion.output`. |
| `block` | A few lines inside a file the team owns (an ignore file, a configuration file): the template renders the block's lines, kept between `<blockComment> maquettiste: begin <pack>/<unit>` and `... end ...` lines (`blockComment` defaults to `#`); the rest of the file is never touched. `createFile: true` creates a missing file; without it the unit writes nothing until the file exists (`target-missing`, MQ6028). Removing the unit removes the block. Never formatted. |

A unit without `output` writes only **file blocks**: `{{ capture content }}…{{ end }}{{ file path content }}`, one call per file,
for zero or many files per unit (one registration file per package, one migration per revision). Blocks take the unit's mode,
except under `pair`, where they are `overwrite`. Text outside file blocks in such a unit is warning MQ6011.

Where output may go is decided by `outputs.allow` in `maquettiste.json`, not by the pack: a folder, or a single file (a block
unit's ignore file, say). Which outputs the team commits is its choice; every pack has one manifest in `.maquettiste/manifest/`,
and `generate --check` renders every output.

### 3. Write templates against the resolved model

Templates are [Scriban](https://github.com/scriban/scriban). The context has `model`, `element` and its scope alias (`entity`,
`table`, `database`, …), `pack`, `mapping`/`mappings`, `schema_diff`, `hints`, `data` and `unit`. Member names are snake_case
(`table.primary_key.columns`, `column.native_type`). Useful parts of the resolved model:

- `entity.attributes` (flattened: base attributes, own attributes, stereotype attributes, sorted by `order`), `entity.key`,
  `entity.mappings[<database>]` with `table`, `columns` (attribute path to column) and `discriminator_column`.
- `database.tables` (by schema and name), each with `columns` (by position), `primary_key`, `uniques`, `foreign_keys`
  (`referenced_table`, `on_delete`), `checks`, `indexes`, and per column `native_type`, `nullable`, `identity`, `default`,
  `default_sql`, `attribute_path`. A table also carries the annotations of its own file (a designed or imported table, or a
  synthesized table's overlay) as an entity does: `display_name`, `plural_name`, `description`, `tags`, `category` (`id`, `name`,
  `path`), `stereotypes` (`key`, `name`, `icon`, `color`), `properties` (a map: the stereotypes' default properties merged under
  the file's own) and `generation`, plus `comment`. These are empty for a synthesized table without an overlay: the entity's own
  are on `table.entity`. A column carries the same annotations from its own entry in the table file (a designed or extra column,
  or a synthesized column's overlay entry; never its attribute's, which is `column.attribute`), with `comment`, `collation` and
  `sequence`. A child table names its attribute (`table.attribute`), the primary key has `clustered`, and uniques, foreign keys,
  checks and indexes keep their file `id` (null when synthesized).
- `database` itself carries the annotations of the database file, with `by_convention`, `packages` (`package`, `package_id`,
  `schema`), `quoting` and `max_identifier_length`; `database.schemas` (`name`, `is_default`, `is_declared`, `tables`, `views`,
  `sequences`) carry those of their entries in that file. `database.views` (`body`, `columns`, `comment`) and
  `database.sequences` (with `database`) carry the same annotations from their files; `each view` and `each sequence` run a unit
  once per view or sequence of every database (alias `view` or `sequence`), filtered by tags, stereotypes, categories and the
  database.
- `database.routines`, `database.types` and `database.objects` (also on each schema; added 2026-10-01) carry the annotations of
  their files too. A routine has `routine_kind` (`function` or `procedure`), `parameters` (`name`, `type`, `db_type`, `length`,
  `precision`, `scale`, `native_type`, `mode`, `default`), `returns` (null, a single value with the same type members, or `table`
  with columns), `language` (the dialect's own when the file names none), `body` and `has_body` for the database's dialect,
  `deterministic`, `security`, `depends_on` (the resolved objects) and `comment`. A database type has `type_kind` (`domain`,
  `composite`, `enum`, `range`), `base` and `base_native_type`, the facets, `check`, `members`, `fields`, `subtype` and
  `subtype_native_type`, `definition` (for the dialect, or null), `is_created`, `native_name` (what a column typed by it writes),
  `depends_on` and `comment`. A SQL object has `object_kind`, `phase` (`before` or `after`), `depends_on`, `body` and
  `has_body`. A column whose `nativeType` names a database type has `db_type`. `each routine`, `each database type` and `each sql
  object` run a unit once per object (alias `routine`, `database_type`, `sql_object`), filtered like views; `schema_diff` lists
  their changes in `routines`, `types` and `objects` (`kind`, `key`, `old_name`, `new_name`, `old_kind`, `new_kind`,
  `old_schema`, `changes`).
- `process` (`each process`, or `model.processes`): `use`, `subject`, `bound_attribute`, `bound_enum`, `context`, `events`,
  `guards`, `actions`, `states` (the tree), `all_states` (document order), `atomic_states`, `bound_states`, `transitions` (priority
  order), `gates`, `invokes`, `actors`, `scenarios`, `initial`. A state has `name`, `path` (`Fulfilment.Shipping.Packed`), `type`,
  `parent`, `children`, `initial`, `history`, `default_target`, `entry`, `exit`, `invoke`, `is_final`, `is_atomic`, `depth`,
  `bound_member`, `transitions_out`, `region_index`; a transition `source`, `targets`, `trigger`, `event`, `after`, `after_ms`
  (whole milliseconds), `after_ticks` (exact, in ticks of 100 ns), `invoke`, `guard`, `guard_missing` (it names a guard the
  process does not declare, which never holds), `actions`, `external`, `gate`, `label` (the canvas's edge label), `is_targetless`; a gate `required`,
  `signers`, `required_actors`, `allow_repeat_signer`, `reason_required`, `meanings`, `audit_attributes` and `audit` (the audit
  record's fields: `name`, `type`, `required`, `values`, `attribute`). `actor`: `type`, `processes`, `events`, `gates`.
  `scenario`: `process`, `start` (`context` by attribute name, `at`), `steps` (`index`, `input`, `event`, `after`, `after_ms`,
  `after_ticks`, `actor`, `signer`, `meaning`, `payload` (event payload and gate audit attributes) and `assume` by name, `expect`
  with `accepted`, `states`, `state_paths`, `context`, and `trace`: what the engine's interpreter did with the step when it
  replayed the scenario, read once per scenario on first use, with `accepted`, `refusal`, `audit` (the audit records' outcomes),
  `state_paths` and `final`, null when the replay stopped before the step) and `outcome`.
- `schema_diff.<database>` when the pack sets `"usesSchemaDiff": true`: `from_revision`, `to_revision`, `is_empty` and ordered
  `tables` (`kind` added/renamed/altered/dropped, `columns`, constraint and index changes).
- Definitions: `model.stereotypes` (one per key, by key: `key`, `name`, `applies_to`, the `attributes` it adds,
  `default_properties`, `icon`, `color`), `model.tag_vocabularies` (the global one first, then each domain's: `package`,
  `strict`, `definitions` with `key`, `description`, `color`) and `model.category_trees` (the global one first:
  `package`, `roots`, `categories` depth-first). A category, wherever a template meets it, has `parent`, `children`,
  `order` and `description`.
- A value the model file leaves out is null, never `""`: a database object's `display_name` without one, a view's,
  routine's or SQL object's `body` without text for the dialect, a database type's `native_name` the dialect cannot
  create. Compare with `null`, not `""`. Worked-out values keep their rule (an element's display name falls back to its
  name).

Helpers: `pascal camel snake kebab upper_snake`, `pluralize singularize`, `type_of <attribute|column> "<target>"` (a
`types/<target>.json` map or a SQL dialect; for a dialect, a value of a custom type that declares a native type for it takes that one), `sql_quote`, `sql_literal`, `indent`, `escape_xml`/`escape_json`/`escape_md`, `json`,
`has_stereotype`, `has_tag`, `in_category` (each takes an element, a database, schema, table, column, view or
sequence), `lookup`, `banner "<comment prefix>"`, `file`, `state_path <state>` (a state's dotted path),
`iso_duration_ms <text>` (an ISO 8601 duration in milliseconds, a month 30 days and a year 365 as the interpreter counts them; a
text that is not a duration fails the unit with MQ6006), `seed_named "<name>"` (the seed with that name, `currencies`,
or with its package path when its file states one, `Billing.currencies`; null when none has it, MQ6006 when two share
it; renaming a seed into or out of the name renders the unit again) and `seed_row <seed or name> <key>` (the row
whose key is the code of a reference type's seed, the key columns' value of a table seed or the key attributes' value
of an entity's seed, a list in key order for a key of several parts; null when none has it), `stable_id <table> <key>`
(the stable id the engine gives a row of a table with a `stableId` column: a UUID derived from the table id and the row
key, a value or a list in key order in database terms; a table without such a column or a key with a null part is
MQ6006), `table_named "<schema>.<name>" [<database or its name>]` (the resolved table, or null; without a schema part
a table with no schema or in the default schema; every database is searched unless one is given, and two matches are
MQ6006; renaming a table into or out of the name renders the unit again), `column_named <table> "<name>"`,
`array_index_of <list> [<member path>] <value>` (the first index whose item, or its member at a snake_case path such as
`"name"` or `"type.kind"`, equals the value, else -1; numbers compare by value and strings ordinally), `array_find
<list> <member path> <value>` (that item, else null), `toposort <items> <edges>` (the items ordered so that each
`[before, after]` pair's first end comes first, ends matching items by identity or value; ties keep input order and a
cycle releases the earliest item left) and `json <value> [<fields>]` (with fields, the named member paths such as
`["name", "columns.name"]` as an object, a path through a list applied per item; a path ending on a resolved object or
naming no member is MQ6006). These names, and the template variables `database` and `attribute`, are taken: a pack
helper registered under one of them is MQ6013. A function a template defines with `func` is not checked: it takes the
name in that template only, so the template keeps working when a built-in of the same name arrives, and every other
template still sees the built-in.
Partials are templates included with
`{{ include "_name.scriban" arg }}` (paths relative to the pack folder); a partial that only defines functions is a good home for
shared logic.

### 4. Keep output deterministic

Generated bytes must depend only on the model, the settings and the pack:

- No timestamps, random values or machine data: `date.now` and `math.random` fail (MQ6012); `banner` has no timestamp; the
  JavaScript clock is fixed and `Math.random` is seeded.
- Iterate the resolved model's ordered collections as given, or sort explicitly (`array.sort`, ordinal). Never depend on a
  dictionary's order.
- Write LF line endings (the engine normalizes CRLF anyway) and end files with one newline.
- Helpers must be pure: nothing may survive between calls (globals are frozen; closures are not).

### 5. Scriban pitfalls the example packs work around

- **Variables in functions.** An assignment inside `func` updates a global of the same name when one exists (a parameter is
  local). Prefix function locals with `$` (`$name = …`), and do the same in partials that set variables (`$t = $1`).
- **The loop budget.** `limits.templateLoopLimit` (1,000,000 by default) counts every iteration of the outermost loop and of
  everything nested in it, including elements copied by array functions. `list = list | array.add x` copies the whole list each
  time, so accumulating a large list is quadratic: append in place with `list[list.size] = x`, or build text with `+`.
- **Whitespace.** `{{-`/`-}}` trim all whitespace on that side, including indentation of the next line. Put a helper that
  returns indented lines on the same line as the text that follows it, and remember that Scriban indents a multi-line result by
  the column of its `{{`.
- **Regions.** A line containing `maquettiste:keep` is a region marker, so do not mention it in generated prose.

### 6. JavaScript helpers, selectors and transforms

Every `*.js` in the pack (or the `scripts` list) runs in a sandbox with no CLR, file or network access:

```js
maquettiste.helper("join_path", (...parts) => parts.filter((p) => p).join("/"));
maquettiste.selector("roots", (model) => model.entities.filter((e) => !e.base));
maquettiste.transform("summary", (entity) => ({ count: entity.attributes.length }));
```

Helpers are called like built-ins (`{{ join_path a b }}`) and also work in `output` expressions. Model objects reach scripts as
read-only proxies that record dependencies; they are much slower than template access, so walk large parts of the model in the
template and pass plain strings or numbers to scripts (see `dependency_spec` and `ddl_order` in `sql-ddl`). Scripts run under
`limits.scriptTimeoutMs` (2 s by default) per call.

A helper that must read many members can ask for plain copies instead:
`maquettiste.helper("columns_json", fn, { plain: true })` receives frozen plain objects and arrays, bounded by member
hops. A model object passed as an argument (or inside a list or map argument) has every member; the model objects its
members hold are copied the same way; theirs are only `{ id, kind }`. So a table argument has `table.columns[0].name`
and `table.database.name`, and `table.columns[0].attribute` is `{ id, kind }`. A copy has no `hasStereotype`, `hasTag`
or `find` (read `stereotypes` and `tags`), everything copied counts as read, and the model itself cannot be passed
(MQ6016).

An absent value is null in scripts as in templates, never `""`: a database object's `displayName` without one, a view's
`body` without text for the dialect.

### 7. Type maps

`types/<target>.json` maps built-in type keywords (and optionally enum, value object and scalar type names) to a language's
types, with `nullable` and `collection` patterns wrapping `{type}`. `type_of attribute "<target>"` applies it; editing the map
re-renders exactly the units that used it.

### 8. Library packs

A pack can use another pack: its templates include the other pack's partials and call its helpers. A pack that only
serves others has no `units` (a library); `maquettiste pack new naming --library` writes one with a partial and a helper.

```json
{ "name": "records", "version": "1.0.0", "engine": ">=1.0 <2.0",
  "uses": [ { "pack": "naming", "version": "^1.2.0" } ],
  "units": [ { "id": "record", "template": "record.scriban", "for": "each entity",
               "output": "records/{{ naming.class_name entity }}.cs" } ] }
```

- **`uses`** lists packs by folder name with a version range in the `engine` syntax (`^1.2.0`, `>=1.0 <2.0`). A used pack
  loads with the pack, enabled or not, and is never rendered or written for it. Packs run after the packs they use.
- **Cross-pack includes.** `{{ include "naming:partials/header.scriban" }}` renders the used pack's partial with the
  including unit's variables. An unprefixed include inside that partial resolves in `naming`. A used partial parses with
  the default delimiters, whatever the unit's.
- **Namespaces.** Each used pack with scripts is a read-only object named after the pack (`-` becomes `_`):
  `{{ naming.class_name entity }}` or `{{ entity | naming.class_name }}`. Its helpers run in that pack's own sandbox with
  its own parameters. A used pack also sees its own namespace, so its partials render in its own units.
- **Scripts.** `const naming = maquettiste.use("naming");` at the top of a script returns the frozen helpers object;
  calling one works inside a helper, not while scripts load, and not in selectors or script filters, which run while
  planning. Only registered helpers cross.
- **Project rule scripts.** `uses` in `maquettiste.json` (same entry shape) lets `extensions/rules/*.js` call
  `maquettiste.use("naming")`. Editing a library script or its parameters runs the cached rules again.
- **Rules.** MQ6042 (a missing, failed or repeated `uses` entry), MQ6043 (a version outside the range, or a range that
  does not parse), MQ6044 (a pack that uses itself or a cycle), MQ6045 (a namespace that collides with a builtin, a
  template keyword or variable, or a root helper) and MQ6046 (warning: a pack with no units that nothing uses).
- **Renames and deletes.** Renaming a pack rewrites every `uses` entry that names it and lists the files whose text
  still names it; deleting a used pack, or a partial another pack includes, is refused until those users change.

### 9. Test the pack

Generate a known model and compare the output byte for byte, as `tests/Maquettiste.Packs.Tests` does for the billing fixture;
run generation twice and `generate --check` to prove that a second run is clean; compile or execute the output where you can
(the tests build the C# with Dapper and run the repositories against SQLite built from the sql-ddl output). Run
`maquettiste validate` to see pack errors (MQ6xxx) with file, line and column.

## Exports

A unit can state names per element that other packs read, so a name has one definition. `exports` in a unit maps a
snake_case name to a template, rendered with the unit's variables as `output` is, or to `{ "each": "<list member>",
"value": "<template>" }`, rendered once per item of that member of the element (`attributes`, `columns`, an enum's
`members`) with the item as `item` and keyed by the item's id. Values are text: line endings LF, leading and trailing
whitespace trimmed.

```json
{ "id": "record", "template": "record.scriban", "for": "each entity",
  "output": "{{ entity.package.name }}/{{ exports.class_name }}.cs",
  "exports": {
    "class_name": "{{ pascal entity.name }}Record",
    "namespace": "{{ project.properties.baseNamespace }}.{{ entity.package.name }}",
    "members": { "each": "attributes", "value": "{{ pascal item.name }}" } } }
```

- The unit's own template, `output` and companion read its values as the variable `exports`, so a file's name and the
  name other packs read are one definition.
- `exported <element | id | model> "<pack>"` returns a pack's record for an element as a read-only map, or `null` when
  no unit of the pack plans the element. The record merges the exports of every unit of the pack that plans the
  element, with its scope, `where` filter and skip hints applied. `exported model "<pack>"` reads the `for: model` units.
- A template reads the exports of its own pack and of the packs in its `uses` closure. An export template reads only
  the packs its pack uses, never its own, and sees neither `data`, `schema_diff` nor `exports`.
- Exports are computed from the model, never read back from rendered files, so render order cannot change a byte.
  A unit that read a record renders again when that record changes, and only then: editing the exporting template's
  body renders only the exporting units.
- `maquettiste pack exports <pack> [--element <id>]...` and the MCP tool `get_pack_exports` show the records.
- Rules: MQ6047 (a template reads a pack its pack does not use, or an export template reads its own pack), MQ6048 (two
  units of one pack give one name different values for one element), MQ6049 (`each` names no list of items with
  ids, or two items share an id) and MQ6050 (a unit reads exports that failed to evaluate; it is not rendered).
