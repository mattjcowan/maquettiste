# Schema changes

When a change to the model reshapes a database (a renamed column, a narrower type, a dropped table), the rows already
stored have to follow it. A migration cannot guess what was meant: a column that disappears while another with a new
name appears may be a rename or a drop and an add, and a shorter text column may cut values. Maquettiste keeps what was
meant next to the model, as **migration facts**, so the migration that applies a change knows it.

## Migration facts

A migration fact is one small file under `.maquettiste/model/databases/<database>/migrations/open/` (kind `migration`).
A recorded fact has no name: its file is named by its id, so the folder lists facts in the order they were recorded.
A fact names the object it changes by its physical key and by the names it had at the time. Those are history, not
references: deleting or renaming an element never edits a fact. Deleting a database deletes its facts.

Each fact says what happened (`fact`) and carries the intent a migration needs:

| Fact | What it records | What it needs before a migration can apply it |
| --- | --- | --- |
| `rename` | A table, view, sequence, column or database schema took a new name (`from` and `to`) | Nothing |
| `rekey` | Physical keys moved, as materialize moves them, or a SQL object's id (`keys`) | Nothing; it writes no DDL |
| `change-type` | A column's type changed (`from` and `to`) | A `conversion`; a `dataCheck` too when the change narrows |
| `drop` | A table, column or sequence was dropped | An `acknowledge` text: why the data may go |
| `backfill` | A column was made not null | A `value` for the empty rows, or a `dataCheck` |
| `raw` | Statements written by hand (`sql`), run `before` or `after` the schema statements | A `skipWhen` guard |

`adopt`, `baseline`, `value-map` and `state-map` are written by hand and need nothing more.

A fact that lacks its intent is **pending**: MQ4101 names what is missing, and generation stops until the fact is
completed or held. Previews, path listings and the resolved model keep working meanwhile. A **held** fact
(`held: true`) waits: migrations skip it until it is `released`.

## How writes record facts

Facts are recorded by the write that makes the change, in the same atomic save: an element saved, created or deleted in
the editor, from the command line, through MCP or the HTTP API, a batch (schema renames, materialize and retag
included), and a settings save that changes `conventions`, `databases`, `typeMaps` or `inflection`, which can rename
the objects of every database. A default-locale change records the renames a moved plural name causes. The answer
lists what was recorded (`facts`: the id, the fact type, its state, the intent it still misses, a one-line summary and
its file); a pending fact's MQ4101 is among the answer's diagnostics, and the save itself goes through.

The comparison is between the database as the model shaped it before the write and after it, so a column's type that
comes from an attribute, a scalar type, a mapping, the conventions or a type map is seen wherever it comes from. Objects
are matched by key, never by name: deleting a column and adding another with its name records a `drop`, and a
materialize, which moves keys, records one `rekey` and no drop. Adding objects, and changing indexes, constraints,
views, routines, defaults or comments, records nothing.

A write records nothing when `migrations.record` is `off`, when it changes only migration facts, or when it changes
only descriptions, display names, tags or categories (and an element's generation hints or source). Nothing is recorded
for edits made outside Maquettiste (a text editor, a merge, another tool), for a snapshot restore (the model, facts
included, goes back as it was) or for `format`.

A column whose last open fact is a pending `change-type` keeps that one fact: changing its type again rewrites the
fact's `to`, so the fact always reads from the type the database has to the type the model shows. When the type goes
back to the fact's `from`, or to a type that widens it, the fact is left as it is: it is stale, and fold removes it.
Only a fact that is not held and has no conversion or data check follows its column. When the fact's file was edited
outside Maquettiste since the model read it (held or completed in a text editor), the write reads the file again
first: the edit stays, and the new change records a fact of its own after it.

A dry run (the assistant's proposals) lists the facts the change would record. When the change reaches every
database (a settings change, a type or stereotype every database uses, or any change in a project with one database),
the dry run lists none, its `facts` is null, and the save computes them: read the save's `facts`, and complete a
pending one before you generate. A pending fact never stops the save itself.

Objects whose elements have errors are left out of the comparison: MQ4110 (an information) says so, and no fact is
recorded for them.

## The widening table

A change of type needs no fact when it is a **widening**: every stored value converts exactly and reads back as an
equal value of the same kind. The reverse of a widening is a **narrowing**, which needs a conversion and a data check
(the rows that would not fit). Any other change needs a conversion. Only PostgreSQL has a widening table; on the other
dialects every change of type is a conversion.

Types are compared after folding case and spaces and reading PostgreSQL's aliases (`int4` is `integer`,
`character varying` is `varchar`, `timestamp with time zone` is `timestamptz`). `numeric(p)` reads as `numeric(p, 0)`
and a time type without a precision as precision 6.

| From | To | Widening when |
| --- | --- | --- |
| `varchar(n)` | `varchar(m)` | `m > n` |
| `varchar(n)` | `varchar` | always |
| `varchar(n)` | `text` | always |
| `char(n)` | `char(m)` | `m > n` |
| `smallint` | `integer` | always |
| `smallint` | `bigint` | always |
| `integer` | `bigint` | always |
| `numeric(p, s)` | `numeric(p', s')` | `s' >= s` and `p' - s' >= p - s` |
| `numeric(p, s)` | `numeric` | always |
| `timestamp(p)` | `timestamp(p')` | `p' > p` |
| `timestamptz(p)` | `timestamptz(p')` | `p' > p` |
| `time(p)` | `time(p')` | `p' > p` |
| `timetz(p)` | `timetz(p')` | `p' > p` |
| `interval(p)` | `interval(p')` | `p' > p` |
| an array of any of these | the array of its widening, same dimensions | as its element type |
| a not-null column | the column made nullable | always |

Not widenings: `real` to `double precision` (a stored 0.1 reads back as 0.10000000149011612), `timestamp` to or from
`timestamptz`, `char` to `varchar`, an integer type to `numeric` (the kind of value readers get changes), and every
pair not listed. Database types, ranges and domains are the same type only when their names are.

## Checking against a baseline

`maquettiste validate --against <baseline>` checks that the facts tell the whole story since a **baseline**: another
copy of the model, never a repository. The baseline is a model snapshot id (take one per deploy, or before an agent
starts), or a folder that holds another copy of the project, such as the main line checked out next to the working copy
in CI. The folder may be the project's root or its `.maquettiste` folder; it is read, never written.

The check compares each database the two models share (matched by id, not by name) as the model shapes it on each side.
It first reads the baseline under today's keys: the `rekey` facts the baseline does not have move its keys, so a table a
materialize stored as a file is one table, not a drop and a create. Then every change that the stored rows must follow
has to be explained by facts the baseline does not have:

- a rename, by renames that lead from the baseline's name to the model's (`a` to `b`, then `b` to `c`);
- a change of type that is not a widening, by `change-type` facts that lead from the baseline's type to the model's,
  widenings allowed before, between and after them;
- a dropped table, column or sequence, by a `drop`; a column made not null, by a `backfill`.

A change nothing explains is MQ4102, an error, on the column entry, the attribute of a derived column, the table or the
database file. A key that moved with no `rekey` fact reads as a drop and an add, so it needs a `drop`; when an object
with the dropped one's name was added, the message suggests a `rekey`. Pending and held facts explain their change
(MQ4101 names what a pending one still lacks). When the baseline has errors, the objects their elements own are left
out of the comparison, and so is what a baseline file that did not load holds: MQ4109, a warning, names them. The check
runs whatever `migrations.record` says, so a team that records nothing can still have every change accounted for, and
it reads no database, ledger or physical snapshot.

Generation takes the same baseline. `maquettiste generate --against <baseline>` diffs each database from the baseline
model for that one run instead of from its committed physical snapshot (`.maquettiste/snapshots/<db>.json`), read
under today's keys the same way, and never writes the committed snapshot; a database the baseline lacks diffs from
nothing. A team that keeps no committed snapshot still gets migrations: a snapshot per deploy, or the main line in a
folder, is the baseline. A plan made against a baseline stores the value (a folder as its full path), its apply opens
the baseline again, and a baseline that changed in between makes the plan stale. When the baseline has errors, the
objects their elements own are left out of both sides of the diff, and a database that does not resolve in the
baseline is not diffed: MQ4109 says so.

| Where | How |
| --- | --- |
| Command line | `maquettiste validate --against <snapshot id or folder>`: exit 1 on errors, 4 when it names nothing |
| Command line | `maquettiste generate --against <snapshot id or folder>`, with `--dry-run` or `--check` as usual; 4 when it names nothing |
| MCP | `validate` and `plan` with `against` (a snapshot id or a folder) |
| HTTP API | `POST /api/validate?against=<snapshot id>`; `POST /api/generate/plan` with `against` in its body (snapshot ids only) |

## Stale facts

A fact is **stale** when the model no longer shows what it changed: a column renamed and renamed back, a type narrowed
and widened back, a dropped column that is back, a column made not null and then nullable again, a materialize made and
undone (its two `rekey` facts move every key back). A stale fact is MQ4103, a warning, and `migration fold` removes it.
The facts on one object are judged together, in the order they apply: they are stale when, from the first one's starting
point to what the model shows now, nothing is left that needs a fact.

A pending fact is judged on its own, by every validation: once the model shows its starting type again (or a type that
widens it), it is stale and no longer pending, so it no longer stops generation. Facts that are not pending are judged
against a baseline: `validate --against` judges the facts its baseline lacks; plain `validate` judges them only for a
database that has a committed physical snapshot (`.maquettiste/snapshots/<database>.json`). Facts the baseline holds
are never stale.

## Listing and folding facts

`maquettiste migration list` prints the open facts in the order they apply, one line each: the database, the order, the
id, the type, the state (with the intent a pending fact still needs), `stale` when it is stale, and what it changes by
name. `--database` keeps one database; `--format json` prints the list as data. MCP `list_migrations` (also a tool of
the editor's assistant) pages the same list.

`maquettiste migration fold` tidies the facts a baseline does not have: the `--against` model's, else the facts the
committed physical snapshot lists as rendered into a migration, else only pending facts (a pending fact never reaches a
migration). Facts the baseline holds are never folded. Stale facts go first: fold deletes them. Then two facts on one
object join when nothing else on the same table lies between them, and both are held or neither is:

- renames: `a` to `b` and `b` to `c` become `a` to `c`, with `b` kept in `via`, so former names stay known;
- a type change that only widens joins its neighbour, which keeps its conversion and data check;
- two pending type changes join (after a merge of two copies), and the result still needs its intent;
- a rename then a drop join into the drop, which keeps the old names in its object's `formerNames`;
- `rekey` facts with nothing between them chain.

Two type changes that each carry a conversion stay as they are, and so does a join that would order the facts in a
cycle: fold reports both as refused. The joined fact keeps the last fact's id and file, so two copies that fold the same
facts write the same file; the links of other facts to the removed ones name the kept fact. `--dry-run` lists the files
it would change and writes nothing. Fold facts this way; never delete fact files by hand. MCP `fold_migrations` does the
same.

## Migrations from facts

The sql-ddl pack's `migration` unit writes one script per database whose schema changed since its baseline (the
committed physical snapshot, or the model `generate --against` names), and the facts the baseline lacks render with it,
in the order they apply. Pending facts never render. A held fact writes a note where its change would go and renders
nothing else until it is released: a held drop keeps the object, a held rename keeps the old name. On PostgreSQL:

- a fact's step takes the place of its change: a rename is a `RENAME`, a `change-type` runs its data check as a guard
  (the migration stops when the check's query finds a row) and then `ALTER COLUMN ... TYPE ... USING` its conversion, a
  `backfill` fills the empty rows before `SET NOT NULL`, and a `drop` is the drop the change needs;
- a `raw` fact runs its statements before or after the schema statements, unless its `skipWhen` query finds a row;
- a fact whose change an earlier migration did not make (a type change left as a TODO and completed later, a held fact
  released later) writes its own step in the first migration after that, guarded so that it does nothing where the
  database already shows the change;
- each fact applied adds its row to the [migration ledger](../ledger.md), in the migration's one transaction, so a
  migration that fails records nothing.

`adopt`, `baseline`, `value-map` and `state-map` facts write a TODO line for now. On the other dialects a migration
keeps the TODO comments it always wrote for such changes, records no ledger rows, and MQ4056 (a warning) says so. Only
a held drop or rename writes its note there (a held type change or backfill writes the change as before); once it is
released, the migration writes a TODO line for it, since those dialects have no fact steps.

Migrations are written once and are yours to review and edit; one written earlier stays yours while it is on disk, even
when no run produces it any more. A migration from the committed snapshot is named
`<database>/migrations/<NNNN>-<key>.sql`, where `NNNN` is the schema revision it reaches, so name order is apply order;
with `--against`, where nothing counts revisions, it is `<database>/migrations/<key>.sql`. The key is 12 hex digits that
hash where the migration starts, what it changes and the facts it renders, so two copies of a project that diverge from
one revision write different names. Migrations that earlier versions of the pack named `0001.sql`, `0002.sql`, … keep
their names.

Each migration opens with three comment lines:

```sql
-- From state: none
-- To state: 1ef0b1088dca
-- Facts: 01K9MGR0000000000000000001
```

The states hash the database's shape before and after the migration (`none` for a database built from nothing), and
`Facts` lists the facts it renders. A migration follows the one whose to state is its from state: that chain is how a
migrator orders the migrations of copies that diverged, where file names alone cannot. The pack's README has the
details of each step.
