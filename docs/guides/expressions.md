# Write a check once

An invoice must never be due before the day it is issued. Without one language, that rule is written three times: SQL
for the table, code for the API and JavaScript for a process guard. Each copy treats a missing due date its own way, so
the database accepts a row the API refused, or a guard passes in simulation and fails in the running system. This guide
writes the rule once, as `issuedOn <= dueOn` on the billing sample's `Invoice`, follows it into the table's DDL and the
generated C#, uses the same text in a guard, then renames the attribute and the column to see the rule follow.

You need the billing sample: `sh docker/dev-billing.sh` serves a copy of it in `tmp/billing`, with the sql-ddl and
csharp-dapper packs, and the commands below run in that folder. The [user guide](../user-guide.md#expressions) describes
the whole language.

## 1. Bind Invoice and add a due date

`Invoice` is laid out by the database's convention. A check reaches a table only through a binding, so give it one.

1. Open **Invoice**, pick the **Storage** tab and click **Create a table for this entity…**. Pick **main** and apply:
   Invoice now reads and writes the designed table `billing.invoices`.
2. On **Attributes**, add `dueOn`, of type `date`, not required: an invoice without a due date is allowed.
3. Back on **Storage**, in the field map, click **Add columns for the unmapped attributes**. The table gets a
   `due_on date NULL` column, mapped to `dueOn`.

From a terminal, steps 1 and 3 are `maquettiste model materialize tables --database main Invoice` and
`maquettiste model materialize columns --database main Invoice`.

## 2. Write the check as text

Until the inspectors have a text box for expressions, select Invoice, open the inspector's **JSON** tab and add the
entity's `checks`:

```json
"checks": [
  {
    "name": "dueAfterIssue",
    "attribute": "01J92P0V0SNXS6PZ42VDK42HP9",
    "condition": { "text": "issuedOn <= dueOn" },
    "message": "An invoice is due on or after the day it is issued."
  }
]
```

`attribute` is the id of `issuedOn`, the attribute a failure points at, and `message` is what a failure reports. Save.
The save reads the text with Invoice's attribute names, gives the check an id and stores the condition as a tree of
ids:

```json
"condition": {
  "op": "le",
  "args": [{ "path": ["01J92P0V0SNXS6PZ42VDK42HP9"] }, { "path": ["<the id of dueOn>"] }]
}
```

Text that does not parse, such as `issuedOn <=`, refuses the save with MQ3101 and the position of the problem. An agent
does the same with `patch_element` or `save_element`, sending `{ "text": "issuedOn <= dueOn" }` where the condition
goes.

Problems now shows the warning MQ3115 on the check: Invoice's binding writes a table that has a column for both
attributes, but it does not list the check, so no database enforces it yet. Step 4 lists it.

## 3. Preview it

```sh
maquettiste expression preview Invoice --at /checks/0
# text: issuedOn <= dueOn
# type: bool
# nullable: true
# javascript: (row) => mq.passes(mq.le(mq.date(mq.at(row, "issuedOn")), mq.date(mq.at(row, "dueOn"))))
# postgresql: issued_on <= due_on
```

`nullable: true` says the answer can be unknown, since `dueOn` may be null. The JavaScript is what the engine and packs
that write JavaScript run, `mq` being the expression runtime; the PostgreSQL text reads the columns the binding maps.
Give it a row:

```sh
maquettiste expression preview Invoice --at /checks/0 --values '{ "issuedOn": "2026-01-31", "dueOn": null }'
# ... answer: true
maquettiste expression preview Invoice --at /checks/0 --values '{ "issuedOn": "2026-01-31", "dueOn": "2026-01-30" }'
# ... answer: false
```

With `dueOn` null the comparison is unknown, and a check passes on unknown, exactly as a CHECK does. Whether `dueOn`
must be given is its attribute's `required`, not the check's concern. An agent calls `preview_expression` with the same
arguments; it reads Invoice and what the expression names, never the whole model.

## 4. List the check in the binding

On the inspector's **JSON** tab, add the check's id to the binding's `checks`:

```json
"bindings": [
  {
    "id": "<the binding's id>",
    "database": "01J92P0V1QRN2181XM2ZWE02W4",
    "source": "<the invoices table's id>",
    "fields": [ ... ],
    "checks": ["<the id of dueAfterIssue>"]
  }
]
```

Save. MQ3115 is gone. A binding lists its checks by hand on purpose: a database holds only what is mapped to it.

## 5. Generate

Run `maquettiste generate`, or generate from the editor. sql-ddl writes the check into
`db/main/billing/tables/invoices.sql`, with no template change, named by the naming conventions from the check's name:

```sql
CREATE TABLE billing.invoices (
    id uuid NOT NULL,
    number varchar(32) NOT NULL,
    issued_on date NOT NULL,
    ...
    due_on date NULL,
    ...
    CONSTRAINT ck_invoices_due_after_issue CHECK (issued_on <= due_on)
);
```

csharp-dapper gives `src/Generated/Billing/Invoice.g.cs` a `Validate()` method:

```csharp
public virtual IReadOnlyList<(string Pointer, string Code, string Message)> Validate()
{
    var errors = new List<(string Pointer, string Code, string Message)>();
    if ((DueOn is null ? (bool?)null : (IssuedOn <= DueOn.Value)) == false)
        errors.Add(("/issuedOn", "dueAfterIssue", "An invoice is due on or after the day it is issued."));
    return errors;
}
```

An API built on it reports a failure at `/issuedOn`, with the code `dueAfterIssue`. `issuedOn` is required, so only a
null `dueOn` makes the answer unknown, and unknown passes here as in the table: the database and the API give the same
answer for every row. The C# comes from `expr_cs` in the pack's `helpers.js`, an example renderer to copy into your own
pack and adapt; the engine itself writes only PostgreSQL and JavaScript.

On PostgreSQL, an invoice with no due date goes in, and one due before the day it is issued is refused:

```sql
INSERT INTO billing.invoices (id, number, issued_on, due_on, status, created_at, customer_id)
VALUES (gen_random_uuid(), 'INV-2', '2026-01-31', '2026-01-30', 'D', now(), '<a customer id>');
-- ERROR:  new row for relation "invoices" violates check constraint "ck_invoices_due_after_issue"
```

## 6. The same text in a guard

A process guard takes the same language. In a process whose context holds the dates `issuedOn` and `dueOn`, a guard
on the transition that approves an invoice:

```json
"guards": [ { "name": "datesInOrder", "condition": { "text": "issuedOn <= dueOn" } } ]
```

A guard holds only when the answer is true. In the simulation panel, start with `issuedOn` set to 2026-01-31 and no
`dueOn`, then raise the approving event: the transition is refused, and **Last step** shows the guard false, from an
expression. The text that let the row into the table stops the transition, because each site reads the answer its own
way: a check passes on unknown, a guard does not. Where that difference matters, say what you mean:
`dueOn is not null and issuedOn <= dueOn` fails as a check too.

A guard the process already holds as JavaScript stays JavaScript until you rewrite it; the user guide's [Guards in the
expression language](../user-guide.md#guards-in-the-expression-language) gives the steps.

## 7. Rename, and see the rule follow

1. On **Attributes**, rename `dueOn` to `dueDate`.
2. Preview the check again: it prints `issuedOn <= dueDate`. Invoice's file did not change where the check is: it holds
   ids, and the text is printed from current names.
3. Generate. `Invoice.g.cs` follows the new name (`DueDate`), and `invoices.sql` does not change: the column is still
   `due_on`.

Now rename the column too, in the table editor: `due_on` to `due_date`. The CHECK in `invoices.sql` reads
`issued_on <= due_date`, and the next migration renames the column and leaves the check alone:

```sql
ALTER TABLE billing.invoices RENAME COLUMN due_on TO due_date;
```

PostgreSQL renames the column inside the check itself. The schema snapshot knows the check by its shape, the check
written with column keys in place of names, so a renamed column is not a new check. A change to what the check means,
`<` in place of `<=`, drops the old check and adds the new one.

## What to know before you rely on it

- **Times are UTC.** `date_of`, `date_part` and `add_months` of a `datetimeoffset` work on its UTC date and time, and a
  `datetime` compared with a `datetimeoffset` reads as UTC, so "today" at a guard is the UTC date. The database
  session's time zone never enters.
- **Three known limits.** The two targets agree everywhere except in three cases:
    - A pattern with a repetition inside a repetition, such as `^(a+)+$`, can take a very long time to fail in
      JavaScript, where PostgreSQL stays fast. The engine stops such a match after 250 ms, and the C# example pack
      matches with .NET's linear-time mode, so generated C# never takes that long. Avoid a repeated group inside a
      repeated group.
    - A `double` that overflows is an error in PostgreSQL and infinity in JavaScript.
    - A JSON value that is `null` as a whole (not inside a document) reads as SQL null in JavaScript, so `json_type`
      answers null there and `'null'` in PostgreSQL.
- **One row.** A check and a derived value read one row: no navigation to another entity, and no `now()`. Guards may
  read both.
- **PostgreSQL first.** On a database of another dialect, a check written in the language is left out of the DDL with
  the warning MQ4056; keep SQL text in the check's `expression` there. A check on a `float` column stays SQL text too.
