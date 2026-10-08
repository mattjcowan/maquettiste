# Start from a database

Some teams think in tables first. This guide designs part of Spoke & Chain's database (docks and bikes), watches the DDL
the `sql-ddl` pack writes for it as you go, and then makes entities from the tables.

In Maquettiste the database side stands on its own. A database holds only what you make in it: tables, views, queries and
the rest. It knows no entities. An entity says on its own **Storage** tab which table it reads and writes; [Bind entities
to tables](bind-entities.md) covers that side.

You need a project made by `maquettiste init`, which installs the `sql-ddl` starter pack. The live DDL preview uses it.

## 1. Create the database

1. On the rail, pick **Databases**.
2. Click **+** in the explorer header and choose **New database**.
3. Fill in the dialog:
    - **Name**: `main`.
    - **Schemas**: `fleet`. This field is optional and takes a comma-separated list; the first one is the default schema.
    - **Dialect**: `postgresql`.
4. Click **Create**.

The new database is empty. The explorer says so under it, and the Database screen shows it.

## 2. Create the docks table

1. With **main** selected, click **+** in the explorer header and choose **New table…** (the Database screen's **New**
   menu has it too).
2. **Name**: `docks`. **Schema**: `fleet`. Leave **Start with an id column (int64, primary key)** ticked.
3. Click **Create**. The table opens in its table editor.
4. On the **Columns** tab, click **Add column**. It adds a nullable string column named `column_1`.
5. Rename it `name`, and untick its null cell so it is required. Arrow keys move, Enter or F2 edits, Space toggles Null.
6. Add two more columns: `address` (`string`, length 200) and `capacity` (`int32`, not null).

Every saved cell is one change of the table's file, and Undo takes it back.

## 3. Create the bikes table

1. Create a second table, `bikes`, in `fleet`, again with the id column.
2. Add `serial_number` (`string`, length 40, not null) and `purchased_on` (`date`).
3. Add `dock_id` (`int64`). It will hold the dock a bike is parked at; leave it nullable, since a bike can be out on a
   rental.
4. On the **Unique constraints** tab, click **Add unique constraint**. In its columns, tick `serial_number` and untick
   any other column (a key always keeps at least one). The new constraint is named after the table and its first
   column; type over the name to change it.

## 4. Add the foreign key on the diagram

The Database screen shows the table diagram of one database. Open it from the database row (**Show in Database screen**
on a table's menu also works).

1. Hover the `dock_id` row of the **bikes** card. A dot shows at its right edge.
2. Drag the dot onto the header of the **docks** card. The **New foreign key** dialog opens with `dock_id` paired with
   the primary key of `docks`.
3. Check the name (it follows the project's naming convention, `fk_{table}_{columns}` by default). Set **On delete** to
   **Set null**, so removing a dock leaves its bikes without one.
4. Click **Create**.

The key appears as a line between the two tables, with crow's feet at its ends. The same dialog opens from **Add foreign
key** on the table editor's **Foreign keys** tab.

!!! note
    The referenced columns must be the referenced table's primary key or one of its unique keys, as the database itself
    requires. A column whose type differs from the one it references is pointed out but not refused.

## 5. Watch the DDL

1. On the Database screen, open the DDL preview: click the slim strip at the right edge, or press Alt+Shift+D.
2. Pick the **bikes** table on the diagram or in the list. The preview renders its `CREATE TABLE` at once, and its header
   names the pack and the unit that wrote it.
3. Change something, such as the length of `serial_number`. The preview follows.
4. With nothing picked, click **Preview the whole database** to see the full script.

![The Databases screen: the table list with rentals.rentals selected, its column grid, and the DDL preview showing its CREATE TABLE statement](../images/database-light.png#only-light)
![The Databases screen: the table list with rentals.rentals selected, its column grid, and the DDL preview showing its CREATE TABLE statement](../images/database-dark.png#only-dark)

The table editor has the same text for one table on its **DDL** tab. The **dialect selector** on the Database screen shows
how another dialect would write it.

A preview writes nothing. Generation writes the real scripts; see [Generate code](generate-code.md).

## 6. Read the table's problems

Validation checks the tables as you edit them. A foreign key column whose type differs from the column it references is
MQ4005, for example. The Problems panel lists it at once, and its row opens the Database screen on that table with the
column picked. A row's problems also show under the row in the table editor.

## 7. Create entities from the tables

Now give the tables entities, so the services and the rest of the model can work with docks and bikes.

1. Create a domain `Fleet` in the Domain model explorer (**+**, then **New domain**).
2. Click **+** again and choose **New entities from tables…** (a domain's menu has it too).
3. Pick the database **main**. The dialog lists its tables and views that no entity is bound to.
4. Tick **docks** and **bikes**, and choose **Fleet** as the domain the new entities go to.
5. Preview, then apply.

Each table becomes an entity named after it, singular and in Pascal case: `Dock` and `Bike`. Each gets one attribute per
column, its key from the primary key, and a binding to its table. The foreign key between the two picked tables becomes a
many-to-one relationship that names it. The whole thing is one batch and one undo step.

## 8. Look at the result

1. Open **Bike** and pick its **Storage** tab. The binding card shows **Source**, the `bikes` table it reads, and
   the **Field map** with one row per attribute and the column it reads.
2. In the explorer, the **Bike** row has a **Storage** folder listing its binding, `main › bikes`.
3. Open the relationship between **Dock** and **Bike**, and pick its **Storage** tab. **Foreign key** names the key you
   drew.

From here on, the tables and the entities are separate things that a binding joins. Renaming a column, adding one or
removing one is a database change; the binding's field map says what reads it.

## From a terminal

The same operation, with a dry run first:

```sh
maquettiste model materialize entities --database main --package Fleet docks bikes --dry-run
maquettiste model materialize entities --database main --package Fleet docks bikes
```

## Next

- [Bind entities to tables](bind-entities.md): the other direction, from entities to tables, and the field map in detail.
- [The explorers and screens](../user-guide.md#the-explorers-and-screens), under **Databases**, describes the Database
  screen, the table editor and every database object (views, sequences, routines, queries).
- [New entities from tables](../user-guide.md#new-entities-from-tables) and
  [Materialize, both ways](../user-guide.md#materialize-both-ways) are the reference for step 7.
- [sql-ddl](../packs/sql-ddl.md) lists what the DDL covers per dialect, and how migrations are written.
