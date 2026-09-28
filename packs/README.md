# Template packs

**Owner:** W10 Packs. See docs/engineering/engine-design.md sections 8, 9 and 18.

This folder holds the two phase 1 example packs, and this file is the guide to writing a pack of your own.

| Pack | Root | Generates |
| --- | --- | --- |
| [`sql-ddl`](sql-ddl/README.md) | committed (`db`) | Table DDL for PostgreSQL, SQL Server and SQLite, one schema script per database, `once` migrations from the schema diff, a `regions` seed script |
| [`csharp-dapper`](csharp-dapper/README.md) | built (`src/Generated`) | Entity `pair` files, enums, value objects, a Dapper repository per mapped entity, one registration file per package, `types/csharp.json` |

Both are embedded into the CLI: `maquettiste init --pack <name>` copies one into `.maquettiste/templates/<name>/`, and
`maquettiste pack new <name> --from <pack>` starts a new pack from one. Their tests are in `tests/Maquettiste.Packs.Tests/`
(golden output under `tests/fixtures/golden/`; `MAQUETTISTE_UPDATE_GOLDEN=1` rewrites it), and the bench generates them over a
synthetic model of 5,000 entities.

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
- **`for`** is `model` (one unit), `each package|entity|relation|enum|value object|table` (one unit per element; `table` covers
  every database), or `select <name>` with a JavaScript selector. There is no `each database`: register a selector that returns
  `model.databases`, as `sql-ddl` does, and the unit's element is then `database`.
- **`where`** filters by tags, stereotypes, categories, packages, `database` (tables in it, entities and relations mapped there;
  it also picks `mapping`), `abstract` and a JavaScript filter.
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
  `default_sql`, `attribute_path`.
- `schema_diff.<database>` when the pack sets `"usesSchemaDiff": true`: `from_revision`, `to_revision`, `is_empty` and ordered
  `tables` (`kind` added/renamed/altered/dropped, `columns`, constraint and index changes).

Helpers: `pascal camel snake kebab upper_snake`, `pluralize singularize`, `type_of <attribute|column> "<target>"` (a
`types/<target>.json` map or a SQL dialect), `sql_quote`, `sql_literal`, `indent`, `escape_xml`/`escape_json`/`escape_md`, `json`,
`has_stereotype`, `has_tag`, `lookup`, `banner "<comment prefix>"` and `file`. Partials are templates included with
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
