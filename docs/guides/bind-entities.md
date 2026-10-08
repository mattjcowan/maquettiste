# Bind entities to tables

An entity and a table are separate things. A **binding** joins them: it says which table, view or query the entity reads,
which column each attribute reads, and where the entity writes and how it deletes. An entity with no binding is **domain
only**, a deliberate choice that is not a warning. A database never makes a table for an entity on its own.

This guide takes Spoke & Chain's `Member` and `Rental` entities from [A first model](first-model.md), and the `main`
database from [Start from a database](start-from-a-database.md). It binds `Member` to a new table made from the entity,
binds `Rental` to a table a teammate already designed, and then fills the gaps both ways.

## 1. Open the Storage tab

1. On the rail, pick **Domain model** and open **Member**.
2. Pick the **Storage** tab.

It reads **Domain only: not stored in any database.** All the storage work for an entity happens here, on the entity
side. The database side shows tables and never the entities stored in them.

## 2. Create a table for Member

1. On the Storage tab, click **Create a table for this entity…**.
2. Pick the database **main**. When the database has several schemas, the dialog also asks for one.
3. Read the plan: the table it writes, and the binding it adds to the entity.
4. Apply it. It is one batch and one undo step.

The entity now has a table of its own, `members`, shaped by the project's naming conventions (Settings › Conventions),
with every column mapped. From now on the table is yours: change it in the table editor like any other.

![The Rental entity's Storage tab: its binding to the rentals.rentals table in main, with the field map pairing each attribute with its column](../images/entity-storage-light.png#only-light)
![The Rental entity's Storage tab: its binding to the rentals.rentals table in main, with the field map pairing each attribute with its column](../images/entity-storage-dark.png#only-dark)

The Storage tab now shows a **binding card** for `main`:

- **Source**: the table, view or query the entity reads, grouped as Tables, Views and Queries.
- **Constants**: columns with a fixed value. Every read filters on them and every insert sets them.
- **Field map**: one row per attribute the binding can map, with its **Column** and a **Status** (mapped, not mapped).
- **Columns of members**: every column of the source and what accounts for it.
- **Write** and **Delete**: where the entity writes (the source table by default) and how it deletes (**By key** by
  default).
- **SQL**: the binding's five statements (select, select by key, insert, update, delete) for the database's dialect, or
  another one.

To do this for many entities at once, select them (or a whole domain) in the explorer and use **Create tables…** from the
row menu.

## 3. Auto-map Rental to an existing table

Say a teammate already made a `rentals` table in `main`, with the columns `id`, `member_id`, `started_at`,
`returned_at` and `deposit_cents`. `Rental` should read and write it.

1. Open **Rental** and pick **Storage**, or right-click **Rental** in the explorer.
2. Click **Auto-map to existing tables…** and pick **main**.
3. The review list matches each entity to a table by name through the naming conventions (the plural form is tried
   first), then its attributes to the table's columns by name. Each entity shows as a **match**, a **partial match** (with
   the attributes no column matched) or **no table by that name**.
4. For a miss, pick the table or view by hand. Untick a row to leave it out.
5. Apply. Each included entity gets a binding, in one batch and one undo step.

Names match ignoring case and underscores, so `startedAt` reads `started_at`. The key of a to-one relationship is in
the field map too: `MemberRentals` gives `Rental` a `memberId` row, which maps to `member_id`.

## 4. Account for every column

Look at **Columns of rentals**. `deposit_cents` has no attribute, so it is **unaccounted**, shown in the warning color. The
Problems panel says the same (MQ4047): a binding accounts for every column, so nothing a table holds goes unnoticed.

You have two choices:

- **Ignore**: the entity does not use the column. **Account for** also offers *filled by the database* (a default or a
  trigger fills it, and it is never written) and *computed*.
- **Add attributes for the 1 unmapped column**: the entity gets an attribute for the column, named and typed from it and
  mapped to it. Its key, constants, write and delete stay as they are.

Pick the second. `Rental` now has a `depositCents` attribute.

## 5. Add columns for new attributes

The other direction works the same way. Suppose the cooperative wants a note on each rental.

1. On **Attributes**, add `note` (`string`, length 500).
2. Back on **Storage**, the field map shows `note` as **not mapped**.
3. Click **Add columns for the unmapped attributes** above the field map. The `rentals` table gets a column per unmapped
   attribute, named and typed as the conventions would, each mapped to its attribute.

For a single attribute, its **Column** picker offers **New column in rentals**. A column name the table already has is
left for you to map. A to-one navigation's column gets no foreign key; add it in the table editor.

!!! note
    Adding columns needs a table file as the source. A view or a query has its own columns; map them by hand.

## 6. Map by hand when names differ

When a column has a name of its own, pick it in the attribute's **Column** cell. **Map by name…** lists the attributes
without a field whose name matches a free column; untick the ones you do not want, then **Map**.

## 7. Name the relationship's foreign key

1. Open the relationship **MemberRentals** and pick its **Storage** tab.
2. Both ends are bound in `main`, so **Foreign key** lists the keys that can realize it: the keys of `rentals` that
   reference `members`.
3. Pick one. If none is listed, add the foreign key on the `rentals` table first (see
   [Start from a database](start-from-a-database.md#4-add-the-foreign-key-on-the-diagram)).

While only one end is bound, the section says "Bind both ends to a table to name the foreign key".

## 8. Check the SQL

Open the **SQL** section of the `Rental` binding. Each statement is the text a repository runs: parameters named after the
fields, constants as literals, and the columns the database fills left out of inserts and updates. Switch the dialect to
see another database's form. Generation uses the same statements (the `csharp-dapper` pack builds a bound entity's
repository from them).

## Undoing and removing

Every gesture on the Storage tab is one save of the entity and one undo step. The trash button on a binding card removes
the binding; the table stays. **Remove bindings…** on entities or a domain removes their bindings to one database, or to
all of them, and the entities become domain only there.

## From a terminal

The editor's actions have command-line twins, each with a dry run:

```sh
maquettiste model materialize tables --database main Member --dry-run
maquettiste model materialize attributes --database main Rental        # every unmapped column
maquettiste model materialize columns --database main Rental           # a column per unmapped attribute
```

## See also

- [Storage: binding an entity](../user-guide.md#storage-binding-an-entity), with
  [The Storage tab](../user-guide.md#the-storage-tab),
  [Create a table for this entity](../user-guide.md#create-a-table-for-this-entity),
  [Auto-map to existing tables](../user-guide.md#auto-map-to-existing-tables) and
  [Relationship storage](../user-guide.md#relationship-storage).
- [Binding entities to tables, views and queries](../user-guide.md#binding-entities-to-tables-views-and-queries): the
  file shape, several entities over one table, and a read-only entity over a query.
- [Materialize, both ways](../user-guide.md#materialize-both-ways) and
  [The rules for bindings](../user-guide.md#the-rules-for-bindings) (MQ4044 to MQ4055).
