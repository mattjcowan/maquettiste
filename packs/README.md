# Template packs

**Owner:** W10 Packs. See docs/engineering/engine-design.md sections 8, 9 and 18.

This folder holds the example packs (two from phase 1, `process-docs` from phase 3), and this file is the guide to writing a pack
of your own.

| Pack | Root | Generates |
| --- | --- | --- |
| [`sql-ddl`](sql-ddl/README.md) | committed (`db`) | Table DDL for PostgreSQL, SQL Server and SQLite, one schema script per database, `once` migrations from the schema diff, a `regions` seed script, and (with `processTables`) instance, history and audit tables per process |
| [`csharp-dapper`](csharp-dapper/README.md) | built (`src/Generated`) | Entity `pair` files, enums, value objects, a Dapper repository per mapped entity, one registration file per package, `types/csharp.json`; per process the states, definition and contracts, handler, service, machine and store pairs, `regions` endpoints, a typed dispatcher with pipeline behaviours, a generated statechart interpreter and one xunit test per scenario |
| [`process-docs`](process-docs/README.md) | committed (`docs`) | Markdown pages: one per process (a state diagram in diagram text, states, transitions, gates and their audit record, events, actors), one per actor, a walk-through per scenario, and an index |

`sql-ddl` and `csharp-dapper` are embedded into the CLI: `maquettiste init --pack <name>` copies one into
`.maquettiste/templates/<name>/`, and `maquettiste pack new <name> --from <pack>` starts a new pack from one; the bench generates
them over a synthetic model of 5,000 entities. A project uses `process-docs` by copying its folder into `.maquettiste/templates/`
(its README shows the settings). The tests of all three are in `tests/Maquettiste.Packs.Tests/` (golden output under
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
  them as `pack.params.<name>` (keys keep their spelling).
- **`for`** is `model` (one unit), `each package|entity|relation|enum|value object|table|reference type|seed|locale|process|actor|scenario`
  (one unit per element; `table` covers every database), or `select <name>` with a JavaScript selector. There is no `each database`: register a selector that returns
  `model.databases`, as `sql-ddl` does, and the unit's element is then `database`.
- **`where`** filters by tags, stereotypes, categories, packages, `database` (tables in it, entities and relations mapped there;
  it also picks `mapping`), `abstract` and a JavaScript filter. On `each process`, `each actor` and `each scenario` it takes tags,
  stereotypes, categories and packages only (a scenario's package is its process's; an actor has none): `database` and `abstract`
  are refused when the pack loads (MQ6001).
- **`output`** is a Scriban expression rendered with the unit's context, relative to `packs.<name>.output`. Never repeat that
  prefix: `init` sets it (`db` for sql-ddl, `src/Generated` for csharp-dapper), and a project may move it.

### 2. Choose the output mode

| Mode | Use it for |
| --- | --- |
| `overwrite` | Fully generated files (the default). |
| `once` | Scaffolds the team takes over: migrations, starting points. Written only when missing, never deleted. |
| `regions` | Generated files with hand-written islands between `maquettiste:keep id=<id>` and `maquettiste:end-keep` lines. Committed roots only. |
| `pair` | A generated file plus a companion written once (`Invoice.g.cs` + `Invoice.cs`), the customization pattern for built roots. Needs `companion.template` and `companion.output`. |

A unit without `output` writes only **file blocks**: `{{ capture content }}…{{ end }}{{ file path content }}`, one call per file,
for zero or many files per unit (one registration file per package, one migration per revision). Blocks take the unit's mode,
except under `pair`, where they are `overwrite`. Text outside file blocks in such a unit is warning MQ6011.

Decide committed versus built by who consumes the output: SQL that is applied or reviewed is committed (`db`, with a manifest in
`.maquettiste/manifest/`, checked by `generate --check`); code that is compiled is built (gitignored, manifest in the cache). The
root is decided by `outputs.allow` in `maquettiste.json`, not by the pack.

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

Helpers: `pascal camel snake kebab upper_snake`, `pluralize singularize`, `type_of <attribute|column> "<target>"` (a
`types/<target>.json` map or a SQL dialect; for a dialect, a value of a custom type that declares a native type for it takes that one), `sql_quote`, `sql_literal`, `indent`, `escape_xml`/`escape_json`/`escape_md`, `json`,
`has_stereotype`, `has_tag`, `in_category` (each takes an element, a database, schema, table, column, view or sequence), `lookup`, `banner "<comment prefix>"`, `file`, `state_path <state>` (a state's dotted path) and
`iso_duration_ms <text>` (an ISO 8601 duration in milliseconds, a month 30 days and a year 365 as the interpreter counts them; a
text that is not a duration fails the unit with MQ6006). Partials are templates included with
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
maquettiste.selector("databases", (model) => model.databases);
maquettiste.transform("summary", (entity) => ({ count: entity.attributes.length }));
```

Helpers are called like built-ins (`{{ join_path a b }}`) and also work in `output` expressions. Model objects reach scripts as
read-only proxies that record dependencies; they are much slower than template access, so walk large parts of the model in the
template and pass plain strings or numbers to scripts (see `dependency_spec` and `ddl_order` in `sql-ddl`). Scripts run under
`limits.scriptTimeoutMs` (2 s by default) per call.

### 7. Type maps

`types/<target>.json` maps built-in type keywords (and optionally enum, value object and scalar type names) to a language's
types, with `nullable` and `collection` patterns wrapping `{type}`. `type_of attribute "<target>"` applies it; editing the map
re-renders exactly the units that used it.

### 8. Test the pack

Generate a known model and compare the output byte for byte, as `tests/Maquettiste.Packs.Tests` does for the billing fixture;
run generation twice and `generate --check` to prove that a second run is clean; compile or execute the output where you can
(the tests build the C# with Dapper and run the repositories against SQLite built from the sql-ddl output). Run
`maquettiste validate` to see pack errors (MQ6xxx) with file, line and column.
