# Upgrade tests

A release that changes the model has to upgrade the databases the previous release built. An **upgrade test** proves it
before you ship: build a database as the previous release left it, apply the migration to the current version, and run
your own test suite against the result. Maquettiste writes both scripts; your tests decide whether the upgraded database
still works. This guide tests billing 0.5.0, which adds a `phone` column to `customers`, against billing 0.4.0, on
PostgreSQL. Nothing here needs version control: the previous release is a model snapshot or a folder.

## A few words first

| Word | Meaning |
| --- | --- |
| Baseline | The previous release's model: a snapshot id, or a folder holding the project; `--against` only reads it. |
| Previous schema | The `schema.sql` and `seed.sql` the previous model generates: the database as that release built. |
| Migration | What `generate --against` writes from the baseline to the current model: `db/main/migrations/<key>.sql`. |
| Upgrade test | The previous schema, the migration, the current `seed.sql`, then your tests, on a scratch database. |

## 1. Keep the previous release

The test needs the model as it was when 0.4.0 shipped. Keep it in one of two ways.

**A snapshot per release.** When you cut the release, take a snapshot with the template packs:

```sh
maquettiste snapshot create "Billing 0.4.0" --packs --description "What 0.4.0 shipped"
# Took snapshot billing-0-4-0-20261103-063722 (58 documents).
```

The id is the name in kebab case followed by the time. `--packs` keeps the templates, so the previous schema is built
with the packs 0.4.0 used. Snapshots are files under `.maquettiste/model-snapshots/` and travel with the project. When
the build machine does not get that folder, move the one snapshot: `maquettiste snapshot export
billing-0-4-0-20261103-063722 --out billing-0.4.0.zip` on one side, `maquettiste snapshot import billing-0.4.0.zip` on
the other. An imported snapshot keeps its id.

**A folder.** Any folder that holds 0.4.0's copy of the project works: its root, or its `.maquettiste` folder for
`--against`. One way to get it is your CI's checkout step: check out the 0.4.0 release into a second folder, such as
`../billing-0.4.0` next to the working copy. A release archive unpacked into a folder works the same way.

The rest of the guide uses the folder `../billing-0.4.0`. Every `--against` takes the snapshot id instead.

## 2. Check that the facts tell the whole story

A change the stored rows must follow (a rename, a narrower type, a drop, a column made not null) needs a migration fact,
or the migration guesses: a renamed column with no `rename` fact upgrades as a drop and an add, and its values are lost.
Check every change since the baseline first:

```sh
maquettiste validate --against ../billing-0.4.0
# Compared with the baseline: folder /work/billing-0.4.0.
# Validation passed: 0 errors, 0 warnings, 0 infos.
```

A change nothing explains is MQ4102, an error, and the command exits 1. Record or complete the fact before you go on.
[Checking against a baseline](schema-changes.md#checking-against-a-baseline) has the details. Adding a column, as 0.5.0
does, needs no fact.

## 3. Build the previous schema

**From a folder.** Generate the two scripts in that folder:

```sh
maquettiste --repo ../billing-0.4.0 generate --unit sql-ddl/schema --unit sql-ddl/seed
# A db/main/schema.sql
# A db/main/seed.sql
```

`--repo` points the command at the folder, and it uses the packs in that folder's `.maquettiste/templates/`. The unit
filter renders only the two scripts: the run writes no migration and leaves that folder's schema snapshots as they are.
It does write the two files, so use a folder you can throw away, such as the CI checkout.

**From a snapshot.** Restore it into a throwaway copy of the project, never into your working copy, then generate there:

```sh
cp -r . ../billing-previous
maquettiste --repo ../billing-previous snapshot restore billing-0-4-0-20261103-063722 --apply --packs
# Restored billing-0-4-0-20261103-063722: ... packs included.
maquettiste --repo ../billing-previous generate --unit sql-ddl/schema --unit sql-ddl/seed
```

`--packs` restores the templates the snapshot holds. The scripts land in `../billing-previous/db/main/`.

## 4. Write the migration

Back in the working copy, generate the sql-ddl pack against the same baseline:

```sh
maquettiste generate --against ../billing-0.4.0 --pack sql-ddl --force
# A db/main/migrations/f625d3fcf5fe.sql
```

The schema diff runs from the baseline's model for this run only: the committed physical snapshot
(`.maquettiste/snapshots/main.json`) is not written. Run the whole pack: the migration unit reads the schema diff, so a
run limited by `--unit` or `--element` refuses it (MQ6033). The file is named by a 12-digit key that hashes where the
migration starts, what it changes and the facts it renders, so the same baseline and model always give the same name.
It opens with the states it moves between and the facts it applies:

```sql
-- Migration f625d3fcf5fe of database main (PostgreSQL 16): from a baseline model.
-- From state: 3e1368353955
-- To state: 45e7d5f85f0e
-- Facts: none
BEGIN;
ALTER TABLE billing.customers ADD COLUMN phone varchar(40) NULL;
COMMIT;
```

A migration is written once and is yours: review it, edit it, and commit it with the release if you like. When the file
is already there, the run keeps it and prints `K` instead of `A`, and a run with nothing to render prints nothing;
`--force` renders every unit, so the run names the migration either way, which a script needs. When nothing changed in
the schema, there is no migration. A team that deploys the numbered migrations it
commits as it goes (`<NNNN>-<key>.sql`, name order is apply order) applies the ones 0.4.0 did not have instead; the
steps after this one are the same. [Migrations from facts](schema-changes.md#migrations-from-facts) says what each fact
writes.

## 5. Run the upgrade on PostgreSQL

Build a scratch database the way 0.4.0 built it, then upgrade it the way 0.5.0 will:

```sh
createdb billing_upgrade
psql -v ON_ERROR_STOP=1 -d billing_upgrade -f ../billing-0.4.0/db/main/schema.sql
psql -v ON_ERROR_STOP=1 -d billing_upgrade -f ../billing-0.4.0/db/main/seed.sql
psql -v ON_ERROR_STOP=1 -d billing_upgrade -f db/main/migrations/f625d3fcf5fe.sql
psql -v ON_ERROR_STOP=1 -d billing_upgrade -f db/main/seed.sql
```

- `ON_ERROR_STOP` makes `psql` exit with an error at the first failed statement, so the build fails with it.
- The migration runs in one transaction: when it fails, the database stays as 0.4.0 left it. Each fact it applies adds
  its row to the [migration ledger](../ledger.md) in the same transaction, so your tests can also check
  `migration_ledger.applied_fact`.
- The current `seed.sql` runs after the migration: reference rows reach their lookup tables through it, not through the
  migration, and it can run again. With environments, run `objects.<environment>.sql` after the migration and before
  the seed scripts, and `seed.<environment>.sql` after `seed.sql`, as a deploy would.

An upgrade of empty tables proves little about the steps that move data: a type change's conversion, a backfill's value,
a data check that stops the migration when a row would not fit. Load rows that look like yours between the previous
`seed.sql` and the migration, written for the 0.4.0 schema, such as a fixture your team keeps for each release.

## 6. Run your own tests

Point your test suite at `billing_upgrade` and run it as you would against a fresh database: the application's
integration tests, the queries your reports run, whatever your team trusts. Maquettiste does not run them; it only
gives them a database upgraded the way production will be. Drop the database when the run is over:

```sh
dropdb billing_upgrade
```

## 7. Put it in CI

One script runs the whole test. It takes the baseline folder (or a snapshot id, with the restore of step 3 instead of
the folder build) and stops at the first failure:

```sh
#!/bin/sh
set -eu
baseline=../billing-0.4.0   # the previous release, checked out or unpacked here
db=billing_upgrade

maquettiste validate --against "$baseline"
maquettiste --repo "$baseline" generate --unit sql-ddl/schema --unit sql-ddl/seed
migration=$(maquettiste generate --against "$baseline" --pack sql-ddl --force \
  | sed -n 's|^[AK] \(db/main/migrations/.*\.sql\)$|\1|p')

dropdb --if-exists "$db"
createdb "$db"
psql -v ON_ERROR_STOP=1 -d "$db" -f "$baseline/db/main/schema.sql"
psql -v ON_ERROR_STOP=1 -d "$db" -f "$baseline/db/main/seed.sql"
# Load the rows your team keeps for the previous release here.
if [ -n "$migration" ]; then
  psql -v ON_ERROR_STOP=1 -d "$db" -f "$migration"
fi
psql -v ON_ERROR_STOP=1 -d "$db" -f db/main/seed.sql

# Run your own test suite against "$db" here.
dropdb "$db"
```

Run it on every change to the release branch, so a change that breaks the upgrade fails the build the day it is made,
not on release day. When 0.5.0 ships, take its snapshot or keep its copy, and move the baseline forward.

## See also

- [Schema changes](schema-changes.md): migration facts, the widening table, checking against a baseline and the
  migrations the facts render.
- [Snapshots](../user-guide.md#snapshots) and [the command line](../user-guide.md#the-command-line) for every
  `snapshot`, `validate` and `generate` option.
- [The migration ledger](../ledger.md) and the [sql-ddl pack](../packs/sql-ddl.md).
