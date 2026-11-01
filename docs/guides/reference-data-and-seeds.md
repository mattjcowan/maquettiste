# Reference data and seeds

Spoke & Chain rents city bikes, cargo bikes and electric bikes, and the list grows. That list is **reference data**: a
**reference type** whose rows are managed as data, each with a code and a label, and usable as the type of an attribute.
An enum, by contrast, is a closed set that belongs to the code.

**Seeds** are the rows an element starts with. A reference type's rows are a seed; so are an entity's initial rows and a
table's own rows. This guide makes a `BikeType` reference type, fills it, uses it on `Bike`, then adds an entity seed and a
table seed with environments and apply modes, moves rows to a CSV file, translates the labels and keeps the type's rows in
a table of its own.

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
  writes the type's rows into the bound table with the table's seed rows, and points each column holding the type's codes
  at the table's code column with a foreign key.
- The `seed-data` pack writes the rows as files for a loader instead, including a bound reference type's rows in its
  table (first, source `list`).

## See also

- [The explorers and screens](../user-guide.md#the-explorers-and-screens), under **Reference data** and **Seed data**,
  describes every tab, key and setting.
- [Translating the model in the editor](../user-guide.md#translating-the-model-in-the-editor).
- [Translations, seed CSV and reference data over the API](../user-guide.md#translations-seed-csv-and-reference-data-over-the-api).
- [sql-ddl: Reference data](../packs/sql-ddl.md#reference-data) and [seed-data](../packs/seed-data.md).
