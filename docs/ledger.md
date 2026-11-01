# The migration ledger

**Status: Draft, version 1.** Version 1 is reviewed before it is frozen. Until the freeze, a change is additive only: a
new column that may be null or has a default, added by `ALTER TABLE ... ADD COLUMN IF NOT EXISTS` after its
`CREATE TABLE` in the same DDL. A column is never renamed, retyped or removed, so a database that holds the draft tables
converges when the DDL runs again. After the freeze, any change is version 2, and its DDL converts the tables.

The migration ledger is a small set of bookkeeping tables in each PostgreSQL database. It records which
[migration facts](user-guide.md#migration-facts) the database has reached: which renames, type changes and drops its
rows already follow. The scripts the sql-ddl pack generates write it, and so can a team's own migrator. Maquettiste
itself never reads it: `validate --against` compares two models, never a database.

## The tables

Three tables in one schema. The table names are fixed; only the schema is a setting, so every migrator finds the tables
the same way.

| Table | Holds | Columns |
| --- | --- | --- |
| `contract` | One row: the contract version the tables follow | `id` (always 1), `version` |
| `applied_fact` | One row per migration fact the database reached | `fact_id`, `content_hash`, `applied_at`, `applied_by` |
| `applied_object` | One row per object whose identity the writer owns | `element_id`, `kind`, `schema_name`, `name`, `content_hash`, `revert_text`, `applied_at` |

- `fact_id` is the fact's id in the model. `content_hash` is the fact content hash: SHA-256, in 64 lowercase hex digits,
  over the fact's document without its annotations and its `after`, `held` and `released` fields, so holding, releasing
  or reordering a fact never changes it. `migration list --format json` and MCP `list_migrations` show it as `hash`.
- `applied_at` is the database's transaction time, so no generated script holds a time. `applied_by` is free text that
  names the writer (`sql-ddl` for the pack's scripts), or null.
- `element_id` is a model id, `kind` the model kind (such as `sql-object`; readers ignore kinds they do not know),
  `schema_name` and `name` the object's current schema and name, `content_hash` the version of the object that was
  applied, and `revert_text` the SQL that undoes it, or null.

`schemas/v1/ledger.json` describes the same rows as a JSON document: its property names are the column names, in column
order, so a writer in any language maps them one to one.

This is the version 1 DDL for the default schema:

```sql
CREATE SCHEMA IF NOT EXISTS migration_ledger;
CREATE TABLE IF NOT EXISTS migration_ledger.contract (id smallint PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    version integer NOT NULL);
INSERT INTO migration_ledger.contract (id, version) VALUES (1, 1) ON CONFLICT (id) DO NOTHING;
DO $ledger$ BEGIN
    IF (SELECT version FROM migration_ledger.contract WHERE id = 1) > 1 THEN
        RAISE EXCEPTION 'ledger contract version is newer than 1; use a newer writer';
    END IF;
END $ledger$;
CREATE TABLE IF NOT EXISTS migration_ledger.applied_fact (fact_id text PRIMARY KEY, content_hash text NOT NULL,
    applied_at timestamptz NOT NULL DEFAULT now(), applied_by text);
CREATE TABLE IF NOT EXISTS migration_ledger.applied_object (element_id text PRIMARY KEY, kind text NOT NULL,
    schema_name text, name text NOT NULL, content_hash text NOT NULL, revert_text text,
    applied_at timestamptz NOT NULL DEFAULT now());
```

The ledger exists on PostgreSQL only in version 1. A fact in a database of another dialect is MQ4056 (a warning): its
migrations keep TODO comments and no ledger rows are written.

## The schema

`migrations.ledgerSchema` in `maquettiste.json` names the schema of the ledger tables in every PostgreSQL database of
the project. The default is `migration_ledger`. The name is a lower-case identifier (letters, digits and `_`, at most 63
characters):

```json
{ "migrations": { "ledgerSchema": "billing_ledger" } }
```

A name on PostgreSQL's reserved-word list, such as `order` or `user`, is quoted wherever the DDL, the grant and the
scripts write it (`"order".applied_fact`). Another writer must quote it the same way, or it writes to a table that does
not exist. Keep the ledger in a schema of its own: a ledger schema that is also a schema of the database's modeled
objects, declared or named by a table, view, sequence, routine, type or SQL object, is MQ4067 (a warning).

## Getting the DDL

```sh
maquettiste migration ledger --grant-read reporting
```

prints the DDL for the project's schema, then the read grant for the role `reporting`. `--database <name or id>` picks
one PostgreSQL database when the model has several, `--format json` prints
`{ contractVersion, database, dialect, schema, ddl, grant }`, and `--output <file>` writes the text under an allowed
output root instead. The command exits 1 when the model has no PostgreSQL database, when the database named is missing
or not PostgreSQL, or when several exist and none is named. Over MCP, and in the editor's assistant, `get_ledger_ddl`
answers the same (`database`, `grantRead`). In a template, `ledger_ddl <database>` writes the DDL, or nothing for a
database without a ledger.

## The rules

1. **Where.** The schema is `migrations.ledgerSchema`, quoted when it is a reserved word. The table names are fixed.
2. **Idempotent, inside your transaction.** The DDL creates only what is missing and holds no transaction control
   (`BEGIN`, `COMMIT`). A writer runs it inside its own transaction before it writes its rows, on every deploy if it
   likes.
3. **Versions.** A writer that finds a newer version stops: the DDL raises an error. A later version's DDL converts the
   tables and raises the version row in the same transaction.
4. **Reading.** The read grant gives a role the schema and `SELECT` on the three tables:

    ```sql
    GRANT USAGE ON SCHEMA migration_ledger TO reporting;
    GRANT SELECT ON migration_ledger.contract, migration_ledger.applied_fact, migration_ledger.applied_object
        TO reporting;
    ```

    Roles belong to environments, so Maquettiste prints the grant and never generates it.
5. **Fact rows.** A writer inserts one row per fact when the database reaches the fact's end state, in the transaction
   of the statements that reach it, with `ON CONFLICT (fact_id) DO NOTHING`. Rows are never updated or deleted. A
   migration reaches a held fact's end state when the fact is released and applied; a script that builds the database at
   the model's shape reaches it at once. A writer records only the facts whose end state it reached.
6. **Object rows.** Object rows are keyed by `element_id` and written only by the tool that owns the object's identity.
   It inserts the row when it applies the object and updates it when it applies a new version; an object that runs once
   keeps the row of the version that ran. A `rekey` fact moves the rows of the keys it lists. Readers ignore unknown
   kinds and read a missing row as unknown. A migrator with its own identity map writes fact rows only.
7. **Nobody else reads it.** `validate --against` and the rest of Maquettiste never read the ledger. A guard in a script
   reads it in the database, and a team's own migrator may read it.
8. **Reverts.** A revert kept in `revert_text` must succeed when its object is already gone. No writer fills
   `revert_text` yet.

## What the sql-ddl scripts write

- **`schema.sql`** builds the database at the model's shape, so it reaches every open fact except pending ones, `raw`
  facts (their `skipWhen` guard decides when a migrator runs them) and `adopt` facts. Held facts are included, since the
  model already shows their change. When it reaches at least one, the script is one transaction: `BEGIN;` after its
  header, the ledger DDL after the schemas, the DDL as usual, one fact row per fact in applied order, then `COMMIT;`. A
  failed run records nothing.
- **A migration** writes the ledger DDL right after its `BEGIN;` and follows each fact's step with its row. It records
  the facts that are complete or released; a held fact keeps its comment and gets no row until it is released.
- **Object rows** are written only for SQL objects that run once (`apply: once`), so a run-once object is not run
  again. Every script that holds one (`schema.sql`, a migration, `objects.<environment>.sql`, its object script) writes
  the ledger DDL first and runs the object inside a guard: a `DO` block that runs its statements and inserts its row
  (`element_id` the object's id, `kind` `sql-object`, its schema, name and the SHA-256 of its statements) unless a row
  with its id exists. A changed run-once object does not run again, so its row keeps the version that ran. An object
  that converged before has no row, so the first script that runs it as run-once runs it one more time. A `rekey` fact
  that lists a SQL object's id (`kind` `sql-object`) moves its row to the new id in the migration that applies it.
- A model without facts generates the same scripts as before: a script writes the ledger DDL only when it also writes a
  row.

```sql
INSERT INTO migration_ledger.applied_fact (fact_id, content_hash, applied_by)
VALUES ('01K9MGR0000000000000000001', '774d91e1690c8322a4cfa4e57f9613bd05be46b67bc6553608868c707e47466f', 'sql-ddl')
ON CONFLICT (fact_id) DO NOTHING;
```

A team that runs its own migrator writes fact rows the same way, after the statements that reach each fact, and no
object rows. The [sql-ddl pack](packs/sql-ddl.md) and the
[schema changes guide](guides/schema-changes.md) say more.
