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
| `data` | `overwrite` (file blocks) | each database (`select databases`) | with `format` `csv`: `<database>/manifest.json` and one `<schema>.<table>.csv` per table that receives rows; with `json`: `<database>/seed-data.json`. Nothing for a database no seed reaches |

A CSV's header names the columns any row of the table gives; a cell a row does not give is empty. Text is quoted when it holds a
comma, a quote or a line break, or is empty; numbers and booleans are written as they are; lists and objects as JSON text.

The manifest (and the JSON file) holds:

- `database`, `dialect`, and `seedHash`: the hash of every row and setting below, so a loader can skip a database it has loaded.
- `tables`, in foreign key order, each with `table`, `schema`, `key` (the columns that find a row again on the next run: the
  seed's row key, else the primary key), `columns`, `types` (each column's built-in type keyword: `string`, `int32`, `decimal`, `uuid`, `date`...), `file` (CSV) or `rows` (JSON: `id` and `values` by column name), and `seeds`:
  per seed, its `seed` name, `source` (`table`, `entity` or `relation`), `environments` (empty: every environment), `apply`
  (`once`: insert the rows whose key is missing; `converge`: also update changed rows), `delete` (with converge: remove the
  table's rows the seed does not hold), the `columns` it gives, and its rows as `firstRow` and `rowCount`.

## Parameters

| Parameter | Default | Meaning |
| --- | --- | --- |
| `format` | `"csv"` | `csv`: a manifest and one CSV per table; `json`: one `seed-data.json` per database. |
