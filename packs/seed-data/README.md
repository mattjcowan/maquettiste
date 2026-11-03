# seed-data

Seed data as files, for an application, a migration tool or a test fixture to load. Per database, every row the model seeds into
it: the database's table seeds, and the entity and relation seeds that reach one of its tables through a binding (the field map,
the binding's constants) or a mapping. Rows are in database terms (column names, an enum as its column stores it, a to-one end as
the referenced row's key) and tables come in foreign key order, referenced tables first. The pack never connects to a database:
loading the files is the loader's job (csharp-dapper's `seedLoader` generates one).

A project uses it by copying the pack into `.maquettiste/templates/seed-data/`, registering its output and allowing that root in
`maquettiste.json`:

```json
"outputs": { "allow": [ { "path": "data" } ] },
"packs": { "seed-data": { "output": "data", "parameters": { "format": "csv" } } }
```

## Output

| Unit | Mode | For | Writes |
| --- | --- | --- | --- |
| `data` | `overwrite` (file blocks) | `each database` | with `format` `csv`: `<database>/manifest.json` and one `<schema>.<table>.csv` per table that receives rows; with `json`: `<database>/seed-data.json`. Nothing for a database no seed reaches |

A CSV's header names the columns any row of the table gives; a cell a row does not give is empty. Text is quoted when it holds a
comma, a quote or a line break, or is empty; numbers and booleans are written as they are; lists and objects as JSON text.

The manifest (and the JSON file) holds:

- `database`, `dialect`, and `seedHash`: the hash of every row and setting below, so a loader can skip a database it has loaded.
- `tables`, in foreign key order, each with `table`, `schema`, `key` (the columns that find a row again on the next run: the
  seed's row key, else the primary key), `columns`, `types` (each column's built-in type keyword: `string`, `int32`, `decimal`, `uuid`, `date`...), `file` (CSV) or `rows` (JSON: `id` and `values` by column name), and `seeds`:
  per seed, its `seed` name, `source` (`table`, `entity`, `relation`, or `list` for a reference type bound to the table, whose rows come first), `environments` (empty: every environment), `apply`
  (`once`: insert the rows whose key is missing; `converge`: also update changed rows), `delete` (with converge: remove the
  table's rows the seed does not hold), the `columns` it gives, and its rows as `firstRow` and `rowCount`.
- `unresolved`, only when some cell needs it: the cells whose value the database fills in another row. A table seed may
  name a foreign key in its `columns` and give the referenced row's key (an email, an invoice number) in each row; the
  engine fills the key's columns with that row's values, and a `stableId` column with the UUID it derives from the table
  and the row key. Where the referenced value is one the database fills (an identity, a sequence, a default), the cell
  is left empty (`null` in JSON) and listed here with `table`, `schema`, the `row` id and its `index` in the table's
  rows, the `column`, `referencedTable`, `referencedSchema`, `referencedColumn`, `keyColumns` and the referenced row's
  `key`, so a loader looks the value up once the referenced rows are in: `SELECT id FROM sales.customers WHERE email =
  'ada@example.com'`.

## Parameters

| Parameter | Default | Meaning |
| --- | --- | --- |
| `format` | `"csv"` | `csv`: a manifest and one CSV per table; `json`: one `seed-data.json` per database. |

## Tests

`tests/billing-small/` is the pack's case: a database `main` with a `customers` table and a seed of two rows, and under
`expected/` the manifest and the CSV the pack writes for it. Copy the pack folder, `tests/` included, into a project's
`.maquettiste/templates/` and run `maquettiste pack test seed-data`: the case runs as a project of its own and its
output is compared byte for byte with `expected/`. `--update` rewrites `expected/` from the output; review the diff
before keeping it. In this repository `ShippedPackTests` in `tests/Maquettiste.Cli.Tests` runs it, and
`MAQUETTISTE_UPDATE_GOLDEN=1` updates it.
