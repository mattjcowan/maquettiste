# Reference data and seeds

Spoke & Chain rents city bikes, cargo bikes and electric bikes, and the list grows. That list is **reference data**: a
**reference type** whose rows are managed as data, each with a code and a label, and usable as the type of an attribute.
An enum, by contrast, is a closed set that belongs to the code.

**Seeds** are the rows an element starts with. A reference type's rows are a seed; so are an entity's initial rows and a
table's own rows. This guide makes a `BikeType` reference type, fills it, uses it on `Bike`, then adds an entity seed and a
table seed with environments and apply modes, moves rows to a CSV file, translates the labels, keeps the type's rows in
a table of its own and types a column of another table by it. Sections 12 to 14 link table seeds by natural key, give
rows stable ids and compute rows with a script, and section 15 renames, retires and merges codes once databases store
them.

## 1. Create the BikeType reference type

1. On the rail, pick **Reference data**.
2. Click **+** in the explorer header and choose **New reference type**.
3. Fill in the dialog:
    - **Name**: `BikeType` (letters, digits and underscores, not starting with a digit).
    - **Display name**: `Bike type`.
    - **Stored as**: how the packs store the codes in a database. `check` is preselected when the project declares it
      (`maquettiste init` declares `lookup-table`, `check` and `native`). The chosen strategy's description shows under
      the list.
4. Click **Create**.

The type opens on the Reference data screen, with five tabs: **General**, **Fields**, **Rows**, **Used by** and
**Storage**. Its rows live in a seed named after it, created with it.

## 2. Add a field

Every reference type has the built-in fields `code`, `label` and `description`. Add your own on **Fields**:

1. Pick **Fields** (Ctrl+2).
2. In the attribute grid, add `electric` (`bool`) and `maxLoadKg` (`int32`).

## 3. Enter the rows

1. Pick **Rows**.
2. Click **Add row** (or press Ctrl+Enter), and fill in:

| code | label | electric | maxLoadKg |
| --- | --- | --- | --- |
| `city` | City bike | false | 120 |
| `cargo` | Cargo bike | false | 200 |
| `ecargo` | Electric cargo bike | true | 220 |
| `kids` | Kids' bike | false | 50 |

The grid is a spreadsheet: arrows move, Enter or F2 edits, Tab commits and moves right, Ctrl+D duplicates a row (with an
empty code), Alt+Up and Alt+Down move rows. Ctrl+C copies a range as tab-separated text, and Ctrl+V pastes such text,
adding rows past the end. Shift+Enter opens the **row editor**, one row in full beside the grid.

![The Reference data screen: the Bike type reference type on its Rows tab, five rows with code, label, wheel size and whether the bike is electric](../images/reference-data-light.png#only-light)
![The Reference data screen: the Bike type reference type on its Rows tab, five rows with code, label, wheel size and whether the bike is electric](../images/reference-data-dark.png#only-dark)

## 4. Use the type on an attribute

1. Open the entity **Bike** (Domain model).
2. Add an attribute `bikeType`. In its **Type** cell, pick **BikeType** under **Reference data**. The list shows how each
   type is stored, for example `BikeType · check`.
3. Tick **Req**.

**Used by** on the reference type now lists `Bike.bikeType`. A cell, a default or a seed value of this attribute holds
the row's **code**.

## 5. Seed an entity

Docks exist before the first rental, so give `Dock` some starting rows.

1. Open **Dock** and pick its **Seed data** tab. With no seed yet, it offers **New seed**; nothing is created until you
   click it.
2. Click **New seed**. The grid lists every column the entity has: its attributes, then one per to-one relationship end.
3. Add a few rows: `Riverside`, `Station Square`, `Market Hall`, each with its address and capacity.

An end cell names a row of the other entity's seed: Enter opens a picker over those rows. Each seed row has its own id,
which other seeds use to point at it.

An entity's seed reaches the table its binding writes: the field map gives the columns, and the binding's constants are
added to every row.

## 6. Seed a table directly

Some rows belong to the database alone, with no entity in between: settings the application reads at start, say. Make
a table `app_settings` in `main` with an id column, a `key` column (`string`, not null) and a `value` column, and a unique
constraint on `key`.

1. Open the table in its table editor and pick its **Data** tab. It is the same grid over the table's columns, less those
   the database fills.
2. Click **New seed**, and add rows such as `late_fee_per_hour` = `2.50`.

## 7. Choose how the rows are applied

Under every seed grid, each seed has a line of settings:

- **Environments**: a comma-separated list such as `dev, test`. Empty means every environment. Set `dev, test` on the
  dock seed, since production docks are entered by staff.
- **Apply**:
    - **Insert once (leave existing rows)**, the default: a row whose key is missing is inserted, and a row that is
      there is left alone, so edits made in a database survive.
    - **Keep as the model has them (update)**: the rows are also updated to the model's values on the next run. In the
      seed file this is `"apply": "converge"`.
- **Delete rows the seed does not hold** (only with Keep as the model has them): the table then holds exactly the seed's
  rows.
- **Row key** (a table seed): the primary key, or a unique constraint, that finds a row again on the next run. For
  `app_settings`, pick the unique constraint on `key`, and set Apply to Keep as the model has them so a changed value
  reaches every database.

Validation checks a table seed's cells against their columns (type, length, NOT NULL), its row keys, and its foreign keys
against the referenced table's seeds.

## 8. Keep large seeds in a CSV file

Tick **Rows in a CSV file** on a seed's line. Its rows move to `<seed>.csv` beside the seed file: a header of `@id` and
the column ids, then one row per line. That suits seeds of thousands of rows and editing in a spreadsheet. The grid,
validation and generation read it the same way. Untick it to bring the rows back into the seed file.

Every seed grid also has **Import CSV** (pick or paste a file, preview what it adds, changes and removes, then apply it
as one change) and **Export CSV**.

## 9. Translate the labels

Labels and descriptions can be translated. While the project declares one locale, nothing of this shows.

1. Open **Settings › Locales**. Click **Declare locales** if the project has none yet.
2. Add the locale `fr` with **Add locale**, then **Save**.
3. In the top bar, the **Content** switcher picks the language the explorer and the Reference data screen show. Pick `fr`.
4. On **BikeType › Rows**, the grid gains a `label (fr)` column. Type `Vélo cargo` beside `cargo`. An empty cell shows the
   fallback label in italics.

A translation is saved to the locale, apart from the seed. A missing one falls back to the default text and is never a
warning. The **General** and **Fields** tabs have **Translations** sections for the type's own names and its fields'.

For a translator, export the texts as a file and import them back:

```sh
maquettiste l10n export fr --out bike-types.fr.xlf
maquettiste l10n import fr bike-types.fr.xlf            # preview
maquettiste l10n import fr bike-types.fr.xlf --apply
```

## 10. Keep the type's rows in a table of your own

By default the packs decide how `BikeType` is stored. When the database already has a table for it, say it. Make a table
`bike_types` in `main` with a `code` column (`string`, length 20, not null, the primary key), a `name` column (`string`,
not null) and a `sort_order` column.

1. On **BikeType › Storage**, under **Stored in a table**, pick `bike_types` for `main`. Code and Label map to `code` and
   `name` (the column named like each field); map any other field the same way, or leave it **Not stored**.
2. **Strategy in use** for `main` now reads **Table bike_types**: the table wins over the strategy in that database.

The engine now knows the rows are in `bike_types`:

- A foreign key into `bike_types` from a table seed may name `cargo`, and it orders after the type's rows.
- Rows the table needs besides the type's own (a demo-only `tandem`, say) go in a table seed of `bike_types` with
  `environments` `dev, test`. Validation refuses a row there that repeats a code of the type in an environment where both
  are loaded.
- When several types share one table, give each a **constant column** (a `kind` column holding `bike-type`, say) and make
  the table's key the code column with it. Validation refuses two types whose rows would share a key in the table.
- When a column the binding fills is itself a foreign key (that `kind` column into a `kinds` table with seeds, say), the
  value must be a seeded row there, and the type's rows load after it.

Without a binding nothing changes: it is optional, and a database without one keeps its strategy.

## 11. Type a column of your own table

A table you design yourself can hold codes too. Say `main` has a table `bike_events` with a `bike_type` column
(`string`, length 20, not null) that holds the code of a `BikeType` row. Say so on the column, beside its type:

```json
{ "id": "01KAB0000000000000000000E3", "name": "bike_type", "type": "string",
  "typeRef": { "ref": "01KAB0000000000000000000B1" }, "length": 20, "nullable": false }
```

`typeRef` names a reference type, an enum or a scalar type by its id. The editor's Columns grid has no picker for it
yet; set it in the column's JSON form. `type` stays the column's own: the database type does not change.

Three statements, each made once, decide how the database keeps `bike_type` honest:

- The binding of section 10, on the reference type, says where the type's rows live in `main` (or there is none, and the
  type's strategy decides).
- `typeRef`, on the column, says whose codes the column holds.
- How the database checks it is worked out from the two. `sql-ddl` gives the column what it gives an attribute of type
  `BikeType`: a foreign key into the bound table, a foreign key into the lookup table, or a CHECK. On PostgreSQL a type
  stored as `native` gets a CHECK here, since an enum type would replace the column's own type.

When `bike_events` itself declares a foreign key from `bike_type`, that key is the check, and the pack adds none of its
own. Validation then wants the key to point at the bound table's code column in that database (MQ4084), and refuses it
when the type has no table there.

What else follows from the one statement:

- A facet the column leaves out comes from the type: drop `length` and the column takes the code's length. A length or
  type that cannot hold the codes is refused (MQ4083).
- The column's literal default, the cells of table seeds of `bike_events` and the constants a binding gives the column
  must be codes of `BikeType` (MQ7014). A column typed by an enum holds the members' stored values: their values in an
  integer column, their codes in a text column.
- **Used by** lists the column with the attributes, and so does `reference_type_usage`.
- Templates read `column.type_ref`. A query row that reads the column keeps the code's type in `csharp-dapper`, and the
  row's summary names `BikeType`; a column typed by an enum reads as the enum.
- A column may state `sensitive` (`pii` or `secret`) the same way. Say it once, on the column or on the attribute a
  binding maps to it, and both show it. Two different values are a warning (MQ4085).

## 12. Name a referenced row by its natural key

A seed row that points at a row of another table used to repeat that row's key value, which is often a surrogate id
nobody knows. A table seed can name the row by its natural key instead: the value people already use, such as an email
or an invoice number.

Take the billing tables `customers` (an `id`, a `name` and an `email` with the unique constraint `uq_customers_email`)
and `invoices` (an `id`, a `number` unique as `uq_invoices_number`, an `issued_on` date and a `customer_id` whose
foreign key `fk_invoices_customer` references `customers`). The customers seed is keyed by `uq_customers_email`. The
invoices seed lists the foreign key in `columns`, where it would have listed `customer_id`, and gives each invoice's
customer by email:

```json
{ "kind": "seed", "name": "invoices", "target": "<invoices id>", "key": "<uq_invoices_number id>",
  "columns": ["<number id>", "<issued_on id>", "<fk_invoices_customer id>"],
  "rows": [
    { "id": "01JCA4M5R1S2T3V4W5X6Y7Z8A0", "values": ["INV-1", "2026-01-05", "ada@example.com"] },
    { "id": "01JCA4M5R1S2T3V4W5X6Y7Z8A1", "values": ["INV-2", "2026-01-06", "grace@example.com"] }
  ] }
```

- The cell holds the referenced row's key as the referenced table's seeds write it. That table's row key is the one
  its seeds name (here `uq_customers_email`), else the key of a reference type bound to it (section 10), else its
  primary key. A key of several columns is an array in key order: `["ACME", "EU"]`.
- The foreign key's own columns are not listed beside it (MQ7126). An agent or the API may give the foreign key by name
  (`"fk_invoices_customer"`); an unnamed foreign key is given by id. The editor's Data grid has no picker for such a
  column yet: edit `columns` in the seed's JSON form.
- When the rows reach the database, the engine fills `customer_id` with the value the customer row has there. A key no
  row holds is MQ7111 at the cell, and so is a key the referenced rows do not hold in every environment the row is
  loaded in. A cell of the wrong shape (a single value for a composite key, say) is MQ7109.
- When the database fills the referenced value itself (an identity column, a sequence, a default), the value is not
  known before the insert. `sql-ddl` then writes a lookup on PostgreSQL,
  `(SELECT id FROM sales.customers WHERE email = 'ada@example.com')`, and inserts rows that look up rows of their own
  table one level at a time. Other dialects get `NULL` with a TODO comment and the warning MQ4056. The `seed-data` pack
  leaves the cell empty and lists it under `unresolved` for a loader to fill.
- A natural-key cell is data, not a model link, as a code cell is. Deleting the customer row is not refused; validation
  then reports the invoice cell that names it (MQ7111).
- In a CSV file the column is headed by the foreign key's name (its id when it has none), and a composite key is JSON
  array text such as `["ACME","EU"]`.

## 13. Give rows stable ids

A table whose key is a `uuid` needs a value for every seeded row. Typing UUIDs into seed files, and keeping them in step
across tables and databases, is what section 12 avoids. Mark the column instead:

```json
{ "id": "<id>", "name": "id", "type": "uuid", "nullable": false, "stableId": true }
```

Every row the database receives into that table then gets a derived value in that column: table seed rows, the rows of a
reference type bound to the table, and entity rows that reach it through a binding, unless the binding writes the column
itself (a field or a constant). A seed gives no cell for the column (MQ7108), and the column cannot be one the database
fills (MQ7124: it must be a `uuid` without identity, sequence, generation or computation). The editor's Columns grid has
no checkbox for it yet: set `stableId` in the column's JSON form. A stereotype's column takes it too.

The value is a UUID version 5 (RFC 9562, name-based, SHA-1), so any tool can compute it again:

- The namespace is the table's id: the 16 bytes of its ULID, read as a UUID, as `row_uuid` writes an id.
- The name is the row key's values in database terms, each as text, joined by the character U+001F, in UTF-8. A string
  is itself; an integer has invariant decimal digits with a leading `-` when negative; a decimal has no exponent, no
  trailing fractional zeros and no `.` when it is whole (`0`, never `-0`); a float or a double is its shortest
  round-trip text; a boolean is `true` or `false`; a UUID is lowercase with hyphens; a date, time or timestamp is
  written as the cell writes it.

A worked example. The table with id `01JCA4M2A1B2C3D4E5F6G7H8J9` has the namespace
`0193144a-0941-5898-3691-c579a078a249`. Its row keyed `ada@example.com` gets `713ef598-46bf-59bd-aecd-d0be95b552e3`, and
a row keyed `["ACME", "EU"]` gets `e2aa38bc-b70c-52f3-ac0e-c50c98d04b1e` (the name is `ACME`, U+001F, `EU`). PostgreSQL
computes the same with the `uuid-ossp` extension:

```sql
select uuid_generate_v5('0193144a-0941-5898-3691-c579a078a249', 'ada@example.com');
```

What follows from deriving the id:

- The row key may not hold the stable id column itself, nor a value the database fills, directly or through a
  foreign-key entry of section 12 (MQ7125): the id would depend on itself, or on a value the model never knows. Key such
  a table's seeds by a unique constraint (`uq_customers_email`).
- A key part that a foreign-key entry fills is the referenced row's value, which may itself be a stable id. So an
  invoice keyed by its customer's email and a number gets the same id in every database.
- A key that is retired and comes back gets its old id again, and no other row can ever get it. Nothing keeps a list of
  retired keys.
- The ids change when the inputs change. Two tables never share ids, and a table file deleted and created again gets new
  ones. Changing `stableId` or a seed's row key changes the ids of rows already in databases: a seed set to **Keep as
  the model has them** updates those rows on its next run, and an **Insert once** seed leaves them as they are.
- Templates read the flag as `column.stable_id`. A pack that emits constants for seeded rows computes an id with the
  helper `stable_id <table> <key>` (a value, or a list in key order, in database terms).

## 14. Compute rows with a script

Some rows mirror the model: one row per entity of a kind, say. A seed can compute them with a script instead of listing
them. The script is a file under `.maquettiste/extensions/seeds/`, beside the script rules, and the seed names it in
place of its rows:

```json
{ "kind": "seed", "name": "AuditedRecords", "target": "<AuditedRecordType id>", "columns": ["code", "label"],
  "rowsFrom": { "script": "seeds/audited-records.js" } }
```

`extensions/seeds/audited-records.js` gives the reference type one row per entity that carries the stereotype `audited`:

```js
maquettiste.rows((model, seed) => model.withStereotype("audited")
  .filter(d => d.kind === "entity")
  .map(d => ({ code: d.name.toLowerCase(), label: d.displayName ?? d.name })));
```

- The script calls `maquettiste.rows(fn)` exactly once. `fn(model, seed)` gets the model documents through the view
  script rules use (`get`, `all`, `referencesTo`, `withStereotype`, `withTag`, `withProperty`, `byQualifiedName`) and a
  frozen copy of the seed, which tells seeds that share one script apart. It returns an array of plain objects keyed by
  the seed's column names: `code`, `label` and `description` for a reference type, an attribute's name or an end's role
  for an entity, a column's name for a table, and a foreign key's name for a foreign-key entry.
- Each row's id is derived as a stable id is (section 13), with the seed's id as namespace and the row's key in seed
  terms as name, and written as a ULID. So a preview, a CSV export and a template see the same ids on every run. A
  target with no row key takes no scripted rows (MQ7128): a relation, an entity without a key, a table without a primary
  key or named key, or a reference type's seed that does not list `code`. A key the columns do not name, or a row that
  leaves a key part empty, is MQ7128 too, and that row is left out.
- The script reads the model, never the generated output or a pack, and keeps nothing between calls. A top-level `let`
  or `var` is the warning MQ5005, as in a rule file. It runs under the sandbox limits of `limits` in `maquettiste.json`.
- It runs only when something reads its rows: `maquettiste seed preview`, the MCP tool `preview_seed_rows`, a template
  that reads the seed's rows, `maquettiste validate`, and a save of the seed or its target. Loading the model, editing
  the script and previews of units that do not read the rows never run it. Its rows are kept until the script, the seed
  or something the script read changes, and then a unit that read them renders again: adding `audited` to `Product`
  renders the unit that lists the type's rows, and no other.
- A script that does not load, throws or returns something other than an array of row objects is MQ7127 at the seed's
  `rowsFrom.script`, with the script's line. The units that read its rows fail with it; the others render.
- Scripted rows are not in the model's index. A custom property, an end cell or a translation cannot name one, and a
  seed with a script has no rows of its own (MQ7116, which also reports a script that is not there). A default or an
  allowed value that names a scripted code is checked by `maquettiste validate`, not by a save.
- `seed export` writes the scripted rows as CSV with their ids. `seed import` refuses a seed with a script; to freeze
  the rows, export them and import them into a seed without one.

Preview the rows of one seed, as written or scripted, or as a database receives them:

```sh
maquettiste seed preview AuditedRecords
maquettiste seed preview invoices --database main   # stable ids, foreign-key values
```

The editor has no screen for rows scripts yet: write the file with any editor or the MCP tool `write_extension_file`,
and set `rowsFrom.script` in the seed's JSON form.

## 15. Rename, retire or merge codes

Codes outlive the code that first used them: one gets misspelled, one is replaced by another, and two parts of a model
grow the same list twice. Databases store codes, so each of these changes moves data. Four batch operations make the
change in the model and record the database step once, for every environment to apply. This section uses a billing
model: the reference type `PaymentTerm` (codes `net30`, `net60` and `due`) types `Invoice.paymentTerm`, whose column
`invoices.payment_term` in the database `main` stores the codes, and `CreditTerm` (codes `net30` and `n90`) types
`Customer.creditTerm`.

| Operation | Input | What it does |
| --- | --- | --- |
| `rename-code` | `id` (the type), `code`, `to` | The row keeps its id and takes the new code; every default, `allowedValues` entry and seed cell that names the code follows |
| `retire-code` | `id`, `code`, `to` (a live code) | Removes the row and its translations; its uses move to `to`, and so do the `x-ref` values and former codes that named the row |
| `merge-reference-types` | `id` (the type that goes), `target`, `codes` (optional) | Every use of the type moves to the target; `codes` maps codes, a code the target has maps to its row, the other rows move with their ids, values and translations |
| `rename-reference-type` | `id`, `name` | The type's name, file and seed folder change; databases rename what is named after it |

Run one through `maquettiste model apply` or the MCP tool `apply_batch`, a dry run first:

```sh
cat > rename.json <<'EOF'
{ "operations": [ { "op": "rename-code", "id": "@reference-type:PaymentTerm", "code": "net30", "to": "net-30" } ] }
EOF
maquettiste model apply rename.json --dry-run
# rename-code PaymentTerm: 2 uses, 0 rows moved, 0 rows removed, 0 translations removed
maquettiste model apply rename.json
```

The dry run writes nothing. It prints what the refactor rewrites (here the default of `paymentTerm` and a cell of an
invoice seed), the files, and the facts the write would record; `apply_batch` answers the same `usage` on the item of
the type. After the write, `PaymentTerm` keeps the old code:

```json
"formerCodes": [ { "code": "net30", "replacedBy": "<the net-30 row's id>" } ]
```

- **Former codes** stay known. A seed import skips a row whose `@code` is a former code and lists it (MQ7019),
  `names-to-ids` turns a former code into the id of its replacing row, and csharp-dapper writes an obsolete field per
  former code that holds the replacing row, so code that names the old constant still compiles (after retiring `due`,
  `PaymentTerms.Due` reads the `net60` row; `net-30` keeps the field name `Net30`, so the rename needs none). A default,
  an `allowedValues` entry or a seed cell that names a former code is MQ7019: write the replacing row's code. A former
  code is never a live code of its type (MQ7017), and `replacedBy` names a row of the type (MQ7018). Remove an entry by
  hand once no database stores the code.
- **A code never comes back.** Renaming `net-30` back to `net30` is refused (MQ7020): in an environment that applies the
  first map late, the second would move the new row's data. A code an open value map maps away cannot be live again
  (MQ4113).
- **The database step.** The write records one `value-map` fact in each database that stores the codes, naming the
  columns and collections that hold them: `main` gets `net30 -> net-30` for `invoices.payment_term`. On PostgreSQL
  sql-ddl's next migration maps the stored codes before its schema statements, rebuilds the column's foreign key or
  CHECK, renames a native enum value in place where it can, and adds its ledger row; the
  [schema changes guide](schema-changes.md#codes-that-change) has the step per storage strategy.
- **Retire** `due` into `net60`: the `due` row and its labels go, every invoice seed cell `due` reads `net60`, and
  `PaymentTerm` lists `due` replaced by the `net60` row. To retire a code with no replacement, delete the row instead;
  that is refused while the model uses it.
- **Merge** `CreditTerm` into `PaymentTerm` with `"codes": { "n90": "net60" }`: `Customer.creditTerm`, and any column
  typed by `CreditTerm`, are typed by `PaymentTerm`; `net30` is a code of both, so it needs no entry; `n90` becomes a
  former code of `PaymentTerm` replaced by its `net60` row. A row with a code the target lacks moves into the target
  with its id, and a seed of `CreditTerm` named as a seed of `PaymentTerm` joins it. `CreditTerm` and its seeds left
  without rows are deleted. A type with a binding or rows computed by a script cannot be merged.
- **Rename** `PaymentTerm` to `PaymentCondition`: its file and seed folder move, and the value map records the two
  names in every database without a binding of the type, so sql-ddl renames `payment_terms` to `payment_conditions`, or
  the enum type `payment_term_t` to `payment_condition_t`.
- A batch refactors a type once (MQ7020 for a second operation on it), and a refactor never touches rows a script
  computes. MQ7020 gives the reason for every refusal, and nothing is written.

Editing a code or a type's name by hand records no value map: the save warns (MQ4114) and points at the refactor, and
`maquettiste validate --against <baseline>` reports each such change since the baseline that no value map explains
(MQ4112). The editor has no screen for the refactors yet: use the batch, and the type's JSON form shows `formerCodes`.

## Several seeds of one target

A reference type, an entity or a table can have several seeds: base rows in one domain, extra rows in another. A seed
states its own domain in the `package` of its file. The seeds of one target merge in one order: by the qualified name
of their package (`Billing.Catalog` for a sub-domain), a seed in no package first, then by seed name, then by id.
Without packages that is the name order.

- The first row of a code or a key wins. A later row that repeats it is refused on the later seed: MQ7001 for a code of
  a reference type, MQ7102 for an entity's key, MQ7112 for a table's row key. When the two seeds are in different
  packages, the message names both.
- Every seed of one table must find its rows by the same **Row key**: the same unique constraint, or the primary key for
  all of them. The first seed in that order decides the table's row key, and a later seed that names another key, or a
  key beside a seed that names none, is MQ7121, at its row key or, when it names none, at its target.

A seed in a domain that declares layers (`dependsOn`, see
[Packages and layering](../user-guide.md#packages-and-layering)) may target only what that domain may reference.

## Where the rows go

- The `sql-ddl` pack's `seed.sql` reconciles the reference data (by the type's storage strategy) and writes the seed
  rows, as inserts that skip existing keys or as upserts for Keep as the model has them. A seed with environments goes to
  `seed.<environment>.sql`, for each environment the pack's `environments` parameter names.
- For a type bound to a table (section 10), `sql-ddl` creates no lookup table, CHECK or enum type in that database: it
  writes the type's rows into the bound table with the table's seed rows, and points each column holding the type's
  codes at the table's code column with a foreign key. That includes a column of your own table typed by the type
  (section 11), unless the table declares that key itself.
- The `seed-data` pack writes the rows as files for a loader instead, including a bound reference type's rows in its
  table (first, source `list`). A cell whose value the database fills (section 12) is left empty and listed under
  `unresolved`.
- Both write stable ids (section 13) and the values natural keys find as plain values; only a value the database fills
  needs a lookup.

## Seeds by name

A template can find one seed by its name rather than loop over every seed. The table seed of section 6 is named
`app_settings` after its table. A unit that renders once for the whole model can write one constant per setting:

```scriban
{{~ settings = seed_named "app_settings" ~}}
{{~ if settings; for r in settings.rows ~}}
public const string {{ pascal r.values.key }} = "{{ r.values.value }}";
{{~ end; end ~}}
```

- `seed_named` takes the seed's name, or its domain's path and its name when the seed's file states a package
  (`Rentals.app_settings`). It gives nothing when no seed has the name, and fails the unit when two seeds share it:
  give one of them a domain and use the longer name, or rename one. Renaming a seed into or out of the name renders the
  unit again.
- `seed_row` finds one row by its key: the code of a reference type's row (`seed_row "BikeType" "cargo"`), the row key
  of a table seed (`seed_row "app_settings" "late_fee_per_hour"`, by the unique constraint on `key`) or the key of an
  entity's row. A key of several columns is a list in key order, such as `["city", 2026]`.

An agent or a script that writes a seed through the API may also give its columns, its row key and its cells by name:
`"columns": ["key", "value"]` for `app_settings`, `"key"` holding the unique constraint's name, and a row as
`{ "values": { "key": "late_fee_per_hour", "value": "2.50" } }`. The engine stores ids and one cell per column, as the
editor does. A name the target does not have, or has twice, is refused (MQ1002) and nothing is written. The order and
the rules are in [Seeds by name](../mcp.md#seeds-by-name).

## See also

- [The explorers and screens](../user-guide.md#the-explorers-and-screens), under **Reference data** and **Seed data**,
  describes every tab, key and setting.
- [Translating the model in the editor](../user-guide.md#translating-the-model-in-the-editor).
- [Translations, seed CSV and reference data over the API](../user-guide.md#translations-seed-csv-and-reference-data-over-the-api).
- [sql-ddl: Reference data](../packs/sql-ddl.md#reference-data) and [seed-data](../packs/seed-data.md).
