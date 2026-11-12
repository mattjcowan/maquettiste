# Hand-written SQL queries and named queries (WITH): design

**Status (2026-11-11): built, unreleased.** The owner answered all five questions as recommended and asked for both halves in one pull request, with an outside review, whose three extra cases for MQ4098 are in decision 6 and section 9. It designs roadmap row 5.5, "The query kind for hand-written SQL, and CTEs in the structured query" (`next.md:146`), with the binding work's "raw SQL as a read source", which `next.md:227-228` moved here. The original notes are `tmp/scope-next-capabilities.md:452-460` in the main checkout: "a second query form: SQL text per dialect with typed parameters, declared result columns and declared `writes`". [Processes linked to operations](processes-operations-design.md) gives the format. Every code fact was checked on `main` at ca779f8b. A path without a project prefix is under `src/Maquettiste.Engine/`. The examples are invented. Each new name is held to the roadmap's test: it stays for good, it helps any team whether they use it or not, and it stays optional.

## 1. In plain words

**The problem.** A team that works database-first writes a lot of SQL by hand. Today, when the query builder can't say something (a ranking, a running total, a tree walk, a feature only one database has), the team writes the whole data-access method by hand: the SQL string, the parameter types, the row type, the mapping. Then it keeps all of that in step with the tables, by hand.

**What this item gives them.**
- **Hand-written queries.** A query can be written as SQL text instead of built from parts. The team declares what the code needs: the parameters and their types, the columns each row has, and the tables the SQL reads and writes. Maquettiste does not read the SQL to find these. It checks what it can, and the code generation packs write the same typed method they write for a built query.
- **Commands too.** A hand-written query may change data (insert, update, delete). It declares the tables it writes. The generated method returns the number of rows changed, or the rows the statement returns.
- **Named queries (WITH) in built queries.** A built query can define named parts first and read them like tables, including a part that repeats over itself (a tree or a chain). So fewer queries need hand-written SQL at all.
- **An entity can read through hand-written SQL.** An entity's binding can already read from a query. It can now read from a hand-written one, so the entity's repository runs the team's own SQL.
- **Impact.** Every table a hand-written query reads or writes is a reference by id. Changing or deleting the table lists the query, like any other reference. Renaming the table warns that the SQL still uses the old name.

**What it does not do.** Maquettiste never runs the SQL and never connects to the team's database. It does not parse SQL. It reads only a few simple things from the text: where the parameters are, the first word, and which names appear (section 6, decision 6).

**A project that uses none of this** keeps its files, generated output, findings, keys and speed. Section 18 says how, and which tests pin it.

## 2. Questions for the owner

Five questions. Each has a recommendation. **Answered 2026-11-11: all five as recommended, both halves in one pull request.** The review added three cases to the light check of question 3 (decision 6, MQ4098): a command behind a leading `WITH`, a query that reads rows but declares no columns, and names compared ignoring case. It also asked that packs which run a statement use the rendered text (`query_sql`, `RQuery.Sql`); `query.sql_text` stays the team's text as written (section 10).

1. **May a hand-written query change data?** A query that inserts, updates or deletes, declares the tables it writes, and generates a method that returns the rows changed (or the rows it returns). **Recommendation: yes, in this item.** Commands are a large share of a database-first team's hand-written code. Without them, half the problem stays.
2. **Parameters in SQL text are written `:name`.** The same in every dialect; each pack turns it into its own style (`@name`, `:name` or `$1`). **Recommendation: `:name`.** `@name` clashes with real features of three dialects (decision 4).
3. **Light checks on the SQL text.** Besides parameters, the engine warns when a declared table's name never appears in the text (it catches a renamed table), and when the text starts with `INSERT`, `UPDATE`, `DELETE` or `MERGE` but declares no written table (MQ4098). These read names, not structure. **Recommendation: yes, as one warning a team can turn off.**
4. **Where the SQL text lives.** Inside the query's JSON file, per dialect, as views and routines keep their bodies today. A separate `.sql` file beside it would be nicer to edit by hand. **Recommendation: in the JSON file now. A separate file for views, routines and queries together later, if teams ask.**
5. **The names.** On a query: `sql`, `columns`, `reads`, `writes`; `with` for named queries, each with `recursive` and `union`. In the editor: "Built from parts" and "SQL text", "SQL text" and "Named queries (WITH)" tabs. Template members `query.form`, `query.sql_text`, `query.writes`, `query.with`. **Recommendation: as listed.**

## 3. What changes, by layer

| Layer | What changes |
| --- | --- |
| The model | A `query` file has one of two forms. **Built from parts**: as today, plus an optional `with` list of named queries, each optionally `recursive`. **SQL text**: `sql` (text per dialect), `columns` (declared result columns), `reads` and `writes` (ids), with the `parameters` both forms share |
| Checks | Four new rules, MQ4096 to MQ4099 (section 9). Eleven existing rules gain cases. The two forms are kept apart by the schema (MQ1002) |
| Rendering | `QuerySql` renders a hand-written text with its placeholders rewritten, and named queries per dialect. A binding over a built query with named queries puts the `WITH` first. Nothing else changes |
| Resolved model and contract | `RQuery` gains `Form`, `Texts`, `SqlText`, `Writes` and `With`; `From` may be null; a declared column is an `RQueryField` without an expression. Named queries add `RNamedQuery`, `RQuerySource.Named` and `RQueryExpression.NamedField`. `QueryView` gains the same, each left out when unset |
| Editor | The New query dialog asks "Built from parts" or "SQL text". A hand-written query has **SQL text**, **Parameters**, **Columns** and **Tables** tabs, and the **SQL** preview tab every query has. A built query gains a **Named queries (WITH)** tab, and its source pickers list named queries |
| Example packs | `csharp-dapper` generates a typed method for a hand-written query, and a `Task<int>` method for a command. Binding repositories over a hand-written query need no change. `sql-ddl`, `process-docs`, `seed-data`: no change |

**Each part is optional on its own.** Named queries need no hand-written query, and the other way round. A binding over a hand-written query is an ordinary binding over a query.

## 4. Scope

| | What | Section |
| --- | --- | --- |
| **This item** | The hand-written form (text per dialect, `:name` parameters, declared columns, `reads`, `writes`, commands); named queries in built queries, recursive included; a binding over either; checks; records, contract and template members; editor; mock; `csharp-dapper`; docs | 6 to 17 |
| **Later, if teams ask** | SQL text in a separate `.sql` file; a recursion limit (`maxRecursion`) and cycle detection for named queries; a "Convert to SQL text" action on a built query; one row or one value results (not a list); `table.written_by`; writes to views; checking declared columns against a live database (`db check`, 6.5) | 19 |
| **Not in this item** | Parsing SQL. Running SQL. Named queries inside nested queries or other named queries. Collections on a hand-written query. An `entity` on a hand-written query (it binds instead) |

## 5. What a team can rely on, and what is theirs

Teams will inventory this and some will build to match it. **Nothing below has a runtime meaning unless this table gives it one.** The same rows go into the user guide (section 17).

| Capability | Written in | The model states | Reads it | Left to the team |
| --- | --- | --- | --- | --- |
| Two forms of query | `query` file | Built from parts (a tree the engine renders), or SQL text (the team's text, rendered as written) | Checks (C), records (R), templates (T), editor (E) | Which form to use |
| SQL text per dialect | `sql` | The text for a dialect is that dialect's entry, else `"*"`. The database's dialect must have one | C (MQ4029), R `texts`, T `query.sql_text`, `query_sql`, E SQL text tab | Correct SQL. Statements the database accepts |
| Parameters | `parameters` (exists) | Each is a typed value the caller passes, written `:name` in the text | C (MQ3001, MQ4018, MQ4024, MQ4040, MQ4043, MQ4096), R, T, E | How a list parameter is passed (expanded or as an array) |
| Result columns | `columns` | Each row has these columns, by name, with these types; a column may be null unless `nullable` is false. No columns: the statement returns no rows the code reads | C (MQ3001, MQ4018, MQ4040), R `select`, T `query.select`, E Columns tab | That the SQL returns exactly these. Nothing checks it against a database |
| What the SQL reads | `reads` | The SQL reads or calls these tables, views, routines, sequences and database types | C (MQ4021, MQ4098), impact, deletes, R `uses`, T `query.uses`, E Tables tab | Completeness: the engine can't see what the text reads |
| What the SQL writes | `writes` | The SQL inserts into, updates or deletes from these tables | C (MQ4021, MQ4044, MQ4098), impact, deletes, R `writes`, T `query.writes`, E Tables tab | Transactions, locking, order between commands |
| Rows changed | worked out | A hand-written query with no columns returns the number of rows changed | T (`query.select` empty), `csharp-dapper` | What a count of -1 or 0 means for the caller |
| Named queries | `with` | Each is a named select the query, its nested queries and later named queries may read as a source | C (MQ3001, MQ4021, MQ4022, MQ4023, MQ4099), R `with`, T `query.with`, E Named queries tab | Performance (the database decides how to run them) |
| Recursive named queries | `with[].recursive` | The anchor rows, joined by `union` with the rows the repeating part reads from the rows so far, until it adds none | C (MQ4099), R, T, E | Depth limits and cycles (decision 13) |
| Entity over SQL text | binding `source` (exists) | The entity reads its rows through the hand-written query | C (MQ4044, MQ4045, MQ4097), T `binding_sql` | Writes: the binding writes a table or nothing, as for any query source |

## 6. Decisions

Each gives its confidence: **high**, **medium** or **low**.

### The form

1. **One `query` kind, two forms; no new kind.** Confidence: high.
   - A hand-written query is a `query` file in `model/databases/<db>/queries/<name>.json`, like a built one. It has `sql` where a built one has `from` and `select`.
   - **Why one kind:** a binding already reads "a table, view or query" (`Model/Bindings.cs:22-27`), `each query` units already cover every query, MQ4040 already compares the class names of a database's queries (`Resolution/DatabaseRun.Queries.cs:66-74`), and the explorer, references, impact and deletes already know the kind. A new kind would repeat all of that and make packs loop twice. The original notes call it "a second query form".
   - **The schema keeps the forms apart** (MQ1002), with `if` `sql` is present `then` the SQL text form `else` the built form. A hand-written query has none of `entity`, `with`, `from`, `joins`, `select`, `where`, `groupBy`, `having`, `orderBy`, `distinct`, `paging` or `collections` (an empty list or `false` is allowed, since the canonical writer drops it). A built query has `from` and `select`, and none of `columns`, `reads` or `writes` (a file with `sql` is the SQL text form). **Changed during the build:** the first schema used a `oneOf`, whose failure says only "Expected 1 matching subschema but found 0" at the file's root. With `if`/`then`/`else` a built query without `select` says `required: Required properties ["select"] are not present`, as before this item, and a stray key is reported at its own pointer (`/where`, `/joins`). An older engine refuses a file with `sql`, which is what a team upgrading together wants.
2. **No `entity` on a hand-written query.** Confidence: high. The editor already treats a query's `entity` as an older form (`src/editor/src/editors/database/QueryEditor.tsx:1-5`; `docs/user-guide.md:1076-1080`): a query's rows have their own shape, and an entity reads through a query by a binding. So declared columns have names and types, not attributes. An entity that wants the rows binds to the query (decision 15).

### The text

3. **A text per dialect, in `common.json`'s dialect map.** Confidence: high. `sql` is `{ "<dialect>" | "*": text }` (`schemas/v1/common.json:489-498`), as a view's body, a routine's body and an `sql` expression are. The text for a dialect is its own entry, else `"*"` (the rule of `QuerySql.SqlFor`, `Rendering/QuerySql.cs:147-150`). The database's dialect must have one (MQ4029). Most teams write one entry. More entries serve a pack that renders for several dialects (`query_sql query "sqlserver"`) and a team moving between databases.
4. **Parameters are written `:name` in every dialect.** Confidence: high for `:name`; medium-high for the exact lexical rule.
   - **Why not `@name`,** though it is the renderer's default output (`QuerySql.cs:17-21`): `@` starts PostgreSQL operators (`@>`, `<@`, `@@`), SQL Server's own variables (`DECLARE @total`, `@@ROWCOUNT`) and MySQL's user variables (`@rank := @rank + 1`). The engine could not tell them from parameters without parsing. `$1` clashes with PostgreSQL's dollar quotes. `:name` is the SQL standard's form for host parameters, native to Oracle and SQLite, and its one clash (PostgreSQL's `::` cast) is a fixed two-character token.
   - **The rule.** A placeholder is `:` followed by a name `[A-Za-z_][A-Za-z0-9_]*`, outside comments, string literals and quoted identifiers, where the `:` is not part of `::` and the character before it is not a letter, digit, `_`, `]`, `)` or a quote. So `x = :id`, `(:id)` and `IN :ids` are placeholders; `a::text`, `arr[1:n]`, `label:` and `:=` are not. `arr[a : b]` (a space after the colon) is not one, since no name follows the `:` directly; `arr[a :b]` is, and MQ4024 then names it.
   - **Characters,** per UTF-16 code unit, as the editor's copy reads them: a letter is Unicode category L (`Ⅷ`, a letter number, is none), a digit is category Nd, and white space is .NET's `char.IsWhiteSpace` plus U+FEFF (a byte order mark at the start of a text is white space).
   - **The scanner is per dialect,** since the dialect is known (the text's own, or the one rendered for a `"*"` text). It skips: `--` comments and `/* */` comments in all five (nested in PostgreSQL and SQL Server); `'…'` with `''` in all five; double-quoted identifiers in all five; PostgreSQL `E'…'` with backslash escapes and `$tag$…$tag$`; SQL Server `[…]` with `]]` and `N'…'`; MySQL `#` comments, backslash escapes in strings and `` `…` ``; SQLite `[…]` and `` `…` ``; Oracle `q'x…x'` (with `[]`, `{}`, `()` and `<>` pairs) and `N'…'`. It lives in a new `Rendering/SqlText.cs`, with test vectors shared with the editor's copy (section 21).
   - **How packs get their style:** through the options they already pass. `QuerySqlOptions.Placeholder` `@` (the default) writes `@name`, `:` leaves `:name`, `$` writes `$1`, `$2`… numbered by first appearance, one number per name; `Parameters` lists the names in that order (`QuerySql.cs:392-407`). `Lists` does not apply: the text says how a list is used (`IN :ids` for a library that expands lists, `= ANY(:ids)` for an array).
5. **The rendered text is the text as written,** with only the white space at both ends removed, and placeholders rewritten. Nothing else changes: comments, case, line breaks and a final `;` stay. Confidence: high. `RQuery.Sql` holds this rendering for the database's dialect with `@name`, as for a built query (`Resolution/ResolvedQueries.cs:61-66`).
   - **White space** is what .NET's `char.IsWhiteSpace` calls white space (Unicode `White_Space`, U+0085 included) plus U+FEFF (a byte order mark), the same set the scanner skips. The editor's copy trims the same set.
   - **Where the text is wrapped,** in a binding's derived table (`FROM (<text>) q`, decision 15), the wrapped copy also loses its **final `;`**: the last token outside comments, string literals and quoted identifiers, when it is a `;` outside parentheses. That `;` goes together with the white space and comments after it (`SELECT …; -- note` wraps as `SELECT …`), then white space at the end is removed again. A `;` anywhere else is never touched, and only one is removed. The scanner finds it (`SqlTextScan.FinalSemicolon`, with shared vectors).
   - **Changed during the build (2026-11-11).** The first version removed one final `;` from every rendering. It now stays, because only the team knows whether its statement needs one: SQL Server requires a `;` after a `MERGE` (error 10713), an Oracle block ends with `END;`, and Oracle refuses a `;` after a plain statement. A pack that joins statements adds its own separator.
6. **What the engine reads from the text, exactly.** Confidence: medium-high. Declared, not parsed: the scanner reports four things and nothing else.
   - The placeholders and their positions (MQ4024, MQ4096).
   - The first word outside comments (MQ4097), and the **statement's verb** (MQ4098): the first word, or, when the first word is `WITH`, the first of `SELECT`, `INSERT`, `UPDATE`, `DELETE`, `MERGE`, `REPLACE`, `VALUES` or `TABLE` found outside parentheses after it. So `WITH moved AS (…) UPDATE …` is a command, and `WITH r AS (…) SELECT …` reads rows.
   - The names that appear, as views' bodies are read today for their dependencies (`Resolution/DatabaseRun.Objects.cs:292-363`): unquoted identifiers, and quoted ones without their quotes, **compared with table and view names ignoring case**, as that reading does (MQ4098). `Invoices` and `invoices` name the same table.
   - Whether `ORDER BY` appears outside parentheses, and whether `OFFSET`, `FETCH` or `TOP` does (MQ4097).
   - **Never:** which tables or columns the SQL reads, the statement's structure, its result columns, or whether it is valid. The declarations say those, and the database judges the SQL.

### Columns, tables and commands

7. **Declared columns.** Confidence: high.
   - `columns`: a list of `{ name, type, nullable, description }`. `name` is required and is the name the SQL returns (an identifier, as a select field's; an alias in the SQL gives any other). `type` is required: a built-in type or a database type of the same database (`common.json#/$defs/dbTypeRef`, as a parameter's). `nullable` defaults to `true`, as a view column's (`schemas/v1/view.json:108-129`); a team sets `false` where the SQL guarantees a value. `description` is optional text.
   - No columns means the statement returns no rows the code reads (a command, or a call).
   - **Why `nullable` defaults to true:** a missing null would make generated code fail at runtime on a null, while an extra one only costs a null check. The engine cannot infer it.
8. **`reads` and `writes`.** Confidence: high for the shape; medium for `reads` taking routines, sequences and database types.
   - `reads`: ids of the tables (a table file id, a table key, or an entity id for its table there, as `from.source` takes, `schemas/v1/query.json:223-226`), views, routines, sequences and database types of the query's database that the SQL reads or calls. `writes`: ids of tables only. Both are lists of keyed references (`[ElementRef(Keyed = true)]`), unique, file order kept. A table may be in both.
   - **What they enable:** impact (each is a reference, so a change lists the query; section 12), deletes (decision 16), the name check (MQ4098), templates (`query.uses`, `query.writes`) and the rule that a binding reads only a query that writes nothing (MQ4044). **Not:** any ordering between queries, any rights, or any change to DDL. Queries stay out of the schema snapshot (`docs/engineering/engine-design.md:1006-1007`).
   - `RQuery.Uses` (`ResolvedQueries.cs:68-72`) holds `reads` resolved, so templates and the database view read a hand-written query's uses where they read a built one's.
9. **A hand-written query may write (commands).** Confidence: high, if the owner agrees (question 1).
   - A query with `writes` changes data. With `columns`, it returns those rows (`RETURNING`, `OUTPUT`). Without, it returns the number of rows changed.
   - **Why now:** inserts, updates, upserts and deletes are much of what a database-first team writes by hand, and the generated method, its parameters and its transaction are the same work as a read.
   - A built query stays read-only.

### Named queries

10. **A built query may have `with`: named queries, each a select with a name.** Confidence: high for the shape.
    - Each entry: `name` (an identifier), `from`, `joins`, `select` (required, each field with a `name`), `where`, `groupBy`, `having`, `distinct`, and `recursive` (decision 12). No `orderBy`, `paging` or `collections`: an order inside a named query means nothing, and SQL Server refuses one.
    - **Its columns** are its select fields' names, with their inferred types, as a view's columns.
    - `with` is only on the top query. Nested queries and other named queries don't have their own.
11. **Where a named query is read.** Confidence: high.
    - A `source` (in `from` or a join) may be a named query's **name**. Ids are upper-case ULIDs that start with a digit, so a name never reads as an id, and a keyed reference ignores a segment that isn't an id (`Model/ElementRefAttribute.cs:20-27`). No new key.
    - It may be read by the top query, its nested queries (collections, `exists`), and the named queries listed after it. Reading one listed later, or itself outside `recursive`, is MQ4099.
    - Its columns are referenced by name, `alias.name`, as a view's columns are.
    - A named query's name may not be a table's or view's name that the query reads (ignoring case), since the SQL would then read the named query instead (MQ4099).
    - **Renaming a field** of a named query renames the column references to it in the same edit (the editor, as the view editor does for a view column, `ViewEditor.tsx:1-4`). A hand edit that misses one gets MQ4023 at the reference.
12. **A recursive named query** has `recursive`: `{ union, from, joins, select, where }`. Confidence: medium-high.
    - The named query's own select is the **anchor**. `recursive` is the part that repeats: it reads the named query's own name once, and its rows are added with `union` (`all`, the default, or `distinct`) until it adds none.
    - Its select has the same number of fields as the anchor, with the same names in the same order. Each field's type equals the anchor's (built-in keyword; unknown on either side passes). A column's nullable is the anchor's or the repeating part's.
    - It reads its own name exactly once, in its `from` or a join, not in a nested query, and **not on the null-padded side of an outer join**: the right side of a left join, the left side of a right join (every source before it), either side of a full join. `FROM chain LEFT JOIN categories` and `FROM categories RIGHT JOIN chain` are allowed; `FROM categories LEFT JOIN chain` and `FROM chain RIGHT JOIN categories` are not, since PostgreSQL refuses a recursive reference there. It calls no aggregate function. These hold in all five dialects. SQL Server also refuses any outer join (on either side) and any nested query in the repeating part, and SQL Server and Oracle take only `union: "all"`. All are MQ4099.
    - The schema leaves out `groupBy`, `having` and `distinct` from the repeating part, since no dialect takes them there.
13. **No recursion limit or cycle check in this item.** Confidence: medium. SQL Server stops a recursion after 100 levels by default and MySQL after 1,000; PostgreSQL, SQLite and Oracle run until no row is added. A team adds a depth column and a `where depth < n` to stop a cycle. The user guide says so. A `maxRecursion` key, and the cycle clauses some dialects have, wait for a team to ask: each is a new permanent key.

### Rendering per dialect

14. **Named queries render as a `WITH` clause before the statement, with a column list always.** Confidence: high.
    - `WITH RECURSIVE` on PostgreSQL, MySQL and SQLite when any named query is recursive; `WITH` otherwise, and always on SQL Server and Oracle, which have no `RECURSIVE` keyword.
    - Each entry is `<name> (<col>, …) AS (`, its clauses on lines of their own indented four spaces (as `exists`, `QuerySql.cs:484-496`), then `UNION ALL` or `UNION` and the repeating part, then `)`. The column list is valid in all five, and Oracle requires it for a recursive one, so it is always written.
    - A collection's statement is a statement of its own (`QuerySql.cs:245-255`), so it repeats the `WITH` entries it reads, directly or through other named queries, in list order. csharp-dapper already passes every query parameter to a collection's statement (`packs/csharp-dapper/query.scriban:357-359`).
    - Parameters are numbered in text order, so a `$n` in a named query comes first.
    - A query without `with` renders byte for byte as today.

### Bindings

15. **A binding reads a hand-written query as it reads any query.** Confidence: high.
    - No new binding key. A binding's `source` names the hand-written query; its fields map the declared columns by name; `BindingSql` wraps the rendered text as a derived table, `FROM (<text>) q` (`Rendering/BindingSql.cs:137-143`), without its final `;` and the comments after it (decision 5), with the query's parameters first (`packs/csharp-dapper/_binding.scriban:91-97`). The source's columns are the query's select fields (`Resolution/DatabaseRun.Bindings.cs:373`), which for a hand-written query are its declared columns.
    - **A query that writes, or declares no columns, can't be a binding's source** (MQ4044 gains the case). Writing stays the binding's `write` table, as for any query source.
    - **SQL Server can't nest every text.** A derived table there may not start with `WITH`, and may not end with `ORDER BY` unless `OFFSET`, `FETCH` or `TOP` goes with it. MQ4097 warns, from decision 6's first word and top-level order. The other dialects take both.
16. **A binding over a built query with named queries puts the `WITH` first:** `WITH … SELECT <fields> FROM (<query without its WITH>) q`. Confidence: high. It is valid in all five, and SQL Server needs it. `QuerySql` gains an internal method that returns the `WITH` clause and the statement apart.

### References, deletes and keys

17. **Deletes and renames.** Confidence: medium-high.
    - `reads` and `writes` entries are references like any other: a plain delete of a table they name is refused, with the query among the referrers.
    - **`remove-references` and `delete-dependents` both remove the entry and keep the query.** A built query is deleted whole when a required reference goes (`Loading/ChangePlanner.Cascade.cs:559-564`), but a hand-written query's text is the team's own work, and the engine can't tell whether the text still needs the table. The delete plan adds a warning: "Query `TopCustomers`'s SQL text may still name table `invoices`" (`DeletePlanWarning`, `DeletePlan.cs:80-87`).
    - A table rename changes no SQL text. MQ4098 then warns that the text no longer names the table.
18. **Dependency keys.** Confidence: high.
    - A hand-written query keys on its file and its referrers, its database, the type maps and the conventions (for its parameters' and columns' native types), each database type it names, and the **element** key of each `reads` and `writes` entry. Not their full dependencies: its output does not change when a column of a table it reads changes. That is what declared columns mean.
    - **What the element key covers.** A designed table keeps its columns in its own file, so a column change there is a change to the table's element, and the query's unit renders again (the safe direction). Only a table whose columns come from entities and relations behaves as first written: a column change renders no query unit again, and impact still lists the query among the table's referrers, which tells the team to look.
    - A built query with named queries keys as today, on everything each named query reads.
19. **A minor release; a team upgrades together.** Confidence: high, as for 4.1 and 4.6. `formatVersion` stays 1.

## 7. What each dialect gets

| | PostgreSQL | SQL Server | MySQL | SQLite | Oracle |
| --- | --- | --- | --- | --- | --- |
| Text scanner skips | `--`, nested `/* */`, `'…'`, `E'…'`, `$tag$…$tag$`, `"…"`, `::` | `--`, nested `/* */`, `'…'`, `N'…'`, `[…]`, `"…"` | `--`, `#`, `/* */`, `'…'` and `"…"` with backslashes, `` `…` `` | `--`, `/* */`, `'…'`, `"…"`, `[…]`, `` `…` `` | `--`, `/* */`, `'…'`, `N'…'`, `q'x…x'`, `"…"` |
| `WITH` keyword | `WITH RECURSIVE` when one is recursive | `WITH` | `WITH RECURSIVE` when one is recursive | `WITH RECURSIVE` when one is recursive | `WITH` |
| `union: "distinct"` in a repeating part | yes | no (MQ4099) | yes | yes | no (MQ4099) |
| Outer join or nested query in a repeating part | not on its own name (MQ4099) | none at all (MQ4099) | not on its own name | not on its own name | not on its own name |
| Default recursion limit | none | 100 levels | 1,000 levels | none | none |
| Binding over a hand-written query | derived table | derived table; MQ4097 for a leading `WITH` or an unbounded `ORDER BY` | derived table | derived table | derived table |
| Binding over a built query with `with` | `WITH` first | `WITH` first | `WITH` first | `WITH` first | `WITH` first |
| Oldest server that runs named queries | all supported | 2005 | 8.0 | 3.8.3 | 11g R2 |

No version check is added. MQ4090 (a feature newer than the database's `version`) was designed in `next-phase-2-design.md:194` but never built, and stays reserved for it. The user guide names MySQL 8.0.

## 8. Schema and files

| File | Key | Type | Notes |
| --- | --- | --- | --- |
| `query.json` | top level | | `required` becomes `kind`, `id`, `name`, `database`; `if`/`then`/`else` on `sql` for the two forms (decision 1) |
| `query.json` | `with` (after `parameters`) | list of `namedQuery`, default empty | Built form only |
| `query.json` | `sql` (after `collections`) | `common.json#/$defs/dialectMap`, at least one entry | Hand-written form only, required there |
| `query.json` | `columns` | list of `{ name, type, nullable, description }`, default empty | `name`: identifier, required. `type`: `dbTypeRef`, required. `nullable`: default `true` |
| `query.json` | `reads`, `writes` | lists of strings, unique, default empty | Keyed references. `writes` names tables only (MQ4021) |
| `query.json` `$defs` | `namedQuery` | `{ name, from, joins, select, where, groupBy, having, distinct, recursive }` | `name`, `from`, `select` required; `select` at least one |
| `query.json` `$defs` | `recursivePart` | `{ union, from, joins, select, where }` | `union`: `all` (default) or `distinct`; `from` and `select` required |
| `query.json` | `from.source`, `join.source` | unchanged type | The description adds "or the name of a named query of `with`" |

No key gains a schema default that existing files hold, so no file changes. Also updated: the sample app's schema copies, `docs/api/openapi.yaml` by hand (section 10), and the editor's types generated from it.

**A hand-written query** (PostgreSQL):

```json
{ "kind": "query", "id": "…Q2", "name": "TopCustomersByRegion", "database": "…DB",
  "description": "The best customers of each region by revenue between two dates.",
  "parameters": [
    { "name": "fromDate", "type": "date" }, { "name": "toDate", "type": "date" },
    { "name": "perRegion", "type": "int32", "default": 5 } ],
  "sql": { "postgresql": "SELECT region, customer_id, revenue, place\nFROM (\n  SELECT c.region, c.id AS customer_id, SUM(i.total) AS revenue,\n         RANK() OVER (PARTITION BY c.region ORDER BY SUM(i.total) DESC) AS place\n  FROM billing.invoices i\n  JOIN billing.customers c ON c.id = i.customer_id\n  WHERE i.issued_on BETWEEN :fromDate AND :toDate\n  GROUP BY c.region, c.id\n) ranked\nWHERE place <= :perRegion" },
  "columns": [
    { "name": "region", "type": "string" },
    { "name": "customer_id", "type": "uuid", "nullable": false },
    { "name": "revenue", "type": "decimal" },
    { "name": "place", "type": "int64", "nullable": false } ],
  "reads": [ "<id of invoices>", "<id of customers>" ] }
```

**A command:**

```json
{ "kind": "query", "id": "…Q3", "name": "MarkOverdueInvoices", "database": "…DB",
  "parameters": [ { "name": "asOf", "type": "date" } ],
  "sql": { "postgresql": "UPDATE billing.invoices SET status = 'overdue'\nWHERE status = 'issued' AND due_on < :asOf" },
  "writes": [ "<id of invoices>" ] }
```

**A built query with a recursive named query** (the ancestors of a category):

```json
{ "kind": "query", "id": "…Q1", "name": "CategoryAncestors", "database": "…DB",
  "parameters": [ { "name": "categoryId", "type": "int64" } ],
  "with": [ {
    "name": "chain",
    "from": { "source": "<id of categories>", "alias": "c" },
    "select": [
      { "name": "id", "expression": { "column": "c.id" } },
      { "name": "parentId", "expression": { "column": "c.parent_id" } },
      { "name": "depth", "expression": { "value": 0 } } ],
    "where": { "op": "eq", "left": { "column": "c.id" }, "right": { "param": "categoryId" } },
    "recursive": {
      "from": { "source": "chain", "alias": "ch" },
      "joins": [ { "source": "<id of categories>", "alias": "p",
                   "on": { "op": "eq", "left": { "column": "p.id" }, "right": { "column": "ch.parentId" } } } ],
      "select": [
        { "name": "id", "expression": { "column": "p.id" } },
        { "name": "parentId", "expression": { "column": "p.parent_id" } },
        { "name": "depth", "expression": { "op": "+", "args": [ { "column": "ch.depth" }, { "value": 1 } ] } } ] } } ],
  "from": { "source": "chain", "alias": "a" },
  "joins": [ { "source": "<id of categories>", "alias": "c",
               "on": { "op": "eq", "left": { "column": "c.id" }, "right": { "column": "a.id" } } } ],
  "select": [
    { "name": "id", "expression": { "column": "a.id" } },
    { "name": "name", "expression": { "column": "c.name" } },
    { "name": "depth", "expression": { "column": "a.depth" } } ],
  "orderBy": [ { "expression": { "column": "a.depth" } } ] }
```

It renders for `postgresql` as:

```sql
WITH RECURSIVE chain (id, parentId, depth) AS (
    SELECT c.id AS id, c.parent_id AS parentId, 0 AS depth
    FROM shop.categories c
    WHERE c.id = @categoryId
    UNION ALL
    SELECT p.id AS id, p.parent_id AS parentId, (ch.depth + 1) AS depth
    FROM chain ch
    INNER JOIN shop.categories p ON p.id = ch.parentId
)
SELECT a.id AS id, c.name AS name, a.depth AS depth
FROM chain a
INNER JOIN shop.categories c ON c.id = a.id
ORDER BY a.depth ASC
```

On `sqlserver` and `oracle` the first line is `WITH chain (id, parentId, depth) AS (`.

## 9. Rules

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ4096 | W | A parameter of a hand-written query appears in none of its texts as `:name` (or not in one of them: the message names the dialects). Reported at `/parameters/<i>` | None (the editor offers Remove) |
| MQ4097 | W | A binding reads a hand-written query whose text SQL Server can't nest as a derived table: it starts with `WITH`, or has an `ORDER BY` outside parentheses with no `OFFSET`, `FETCH` or `TOP`. Only on SQL Server databases. Reported on the binding, at `/bindings/<i>/source` | None |
| MQ4098 | W | A hand-written query's text and its declarations disagree, by name and verb only (decision 6), in one of three ways. (1) A table or view in `reads` or `writes` whose name appears nowhere in the text, compared ignoring case (renamed, or declared by mistake). (2) A statement whose verb is `INSERT`, `UPDATE`, `DELETE`, `MERGE`, `REPLACE` or `TRUNCATE`, directly or after a leading `WITH`, with no `writes`. (3) A statement whose verb is `SELECT`, `VALUES` or `TABLE`, directly or after a leading `WITH`, with no `columns`: it would be generated as a command returning a count. Reported at `/reads/<i>`, `/writes/<i>` or `/sql/<dialect>`. One rule, so `validation.rules` can turn all three off | None |
| MQ4099 | E | A named query does not fit. It is misplaced: it reads a named query listed after it, reads itself outside `recursive`, or has the name of a table or view the query reads (ignoring case). Or its repeating part (recursive) does not fit: another number of fields, other names or another order than the anchor; a field type that differs from the anchor's; its own name read other than once, in a nested query, or on the null-padded side of an outer join (the right side of a left join, the left side of a right join, either side of a full join); an aggregate call; on SQL Server, any outer join or nested query; `union: "distinct"` on SQL Server or Oracle. (The first draft had these in a separate MQ4100; the id falls in MQ41xx, which is "Migrations", so the two are one rule.) | None |

**Free ids, checked.** MQ40xx is "Databases and mappings" (`Diagnostics/RuleCatalog.cs:510`), where the query rules live (MQ4021 to MQ4043, `RuleCatalog.cs:181-203`). MQ4096 to MQ4099 are the only free ids left in it: MQ4071 to MQ4075 are reserved for phase 1 follow-ups (`next-phase-1-design.md:94`), MQ4080, MQ4081, MQ4086, MQ4091 to MQ4093 and MQ4095 for phase 2 (`next-phase-2-design.md:262-264`), and MQ4090 and MQ4094 are designed there. MQ41xx is "Migrations". A search of `src`, `docs`, `schemas`, `tests`, `packs`, `skills`, `samples` and the main checkout's `tmp` on 2026-11-11 found no use of MQ4096 to MQ4099. So the rules are few on purpose, and existing rules take the cases that fit them.

**Existing rules that gain a case.** No finding changes for a model that uses none of this.
- **MQ1002** (schema): a file that mixes the two forms, or has neither.
- **MQ3001**: two columns, or two named queries, with one name ignoring case.
- **MQ4018**: a column's type that is neither built in nor a database type of the database.
- **MQ4021**: a `reads` entry that is not a table, view, routine, sequence or database type of the database; a `writes` entry that is not a table of it; a source that names no table, view or named query.
- **MQ4022**, **MQ4023**: aliases and columns of named-query sources, as for views.
- **MQ4024**: a placeholder that names no parameter. The message gives the line and column in the text: "`:fromDat` (line 7, column 31 of the postgresql text) names no parameter".
- **MQ4029**: a hand-written query with no text for its database's dialect and no `"*"` text.
- **MQ4040**: columns whose names collide once Pascal-cased, or start with `mq_`.
- **MQ4043**: a parameter's default, as today.
- **MQ4044**: a binding's source that is a hand-written query that writes, or declares no columns.
- **MQ2001**, **MQ2002**, **MQ2020**, **MQ2022**: `reads` and `writes` are references like any other; they need nothing beyond the `[ElementRef]` attributes.

**Where they run.** In the resolver, as every query rule does today, because they need the database's resolved tables (`Resolution/DatabaseRun.Queries.cs:9-15`; `ModelStore` adds resolver findings to every validate path). Hand-written queries go in a new `DatabaseRun.SqlQueries.cs`, named queries in `DatabaseRun.NamedQueries.cs`, MQ4097 beside MQ4044 in `DatabaseRun.Bindings.cs` (`:310`). Resolution runs on the whole snapshot (`ModelStore.ResolverFindings.cs`), so no validator peers are added. A finding of MQ4096, MQ4097 or MQ4098 leaves the query rendered; MQ4099 and the new MQ4021, MQ4024 and MQ4029 cases leave it without SQL, as today's rule set decides (`DatabaseRun.Queries.cs:151-158`).

The mock's rule list and checks follow (section 14).

## 10. Resolved model, template context and contract

```csharp
// RQuery (Resolution/ResolvedQueries.cs) gains:
public string Form { get; internal set; } = "structured";   // "structured" or "sql"
public IReadOnlyDictionary<string, string> Texts { get; internal set; } = Empty; // the file's sql: dialect or "*" to text; empty when built
public string? SqlText { get; internal set; }               // the text for the database's dialect as written (:name); null when built
public IReadOnlyList<RTable> Writes { get; internal set; } = [];
public IReadOnlyList<RNamedQuery> With { get; internal set; } = [];
// From becomes RQuerySource? : null for a hand-written query.
// Select: a hand-written query's declared columns, each an RQueryField whose Expression is null.
// Uses: a hand-written query's reads, resolved, in file order.
// Sql: the rendering for the database's dialect with @name placeholders (decision 5), as for a built query.

public sealed class RNamedQuery                 // a named query of a built query's with
{
    public string Name { get; internal set; } = "";
    public RQuery Query { get; internal set; } = null!;      // the anchor; Parent is the top query
    public RQuery? Recursive { get; internal set; }          // the repeating part; Parent is the top query
    public string Union { get; internal set; } = "all";      // "all" or "distinct"; "all" when not recursive
    public IReadOnlyList<RQueryField> Columns { get; internal set; } = []; // the anchor's names; types and nullable merged
    public int Index { get; internal set; }
}
// RQuerySource gains Named (RNamedQuery?): the named query it reads, or null.
// RQueryExpression gains NamedField (RQueryField?): for a column of a named query, as ViewColumn is for a view.
// RQueryField gains Description (string?): a declared column's; null for a select field.
```

**Which text a pack uses.** A pack that runs the statement uses the rendered text (`query_sql query`, `RQuery.Sql`), which has its placeholders in the pack's style. `query.sql_text` is the team's text as written, with `:name`, for documentation and display.

**Template members:** `query.form`, `query.texts`, `query.sql_text`, `query.writes`, `query.with` (`name`, `query`, `recursive`, `union`, `columns`, `index`), `source.named`, `expression.named_field`, `field.description`. `query.with` was checked to parse in Scriban 7.5.0, the version the engine uses (`Directory.Packages.props:9`), although `with` is a Scriban keyword. `get_template_context` lists the members, since its lists come from reflection. The helpers `query_sql`, `query_collection_sql` and `query_sql_parameters` (`Rendering/BuiltinHelpers.cs:188-190`) take both forms unchanged.

**Records** (`Editor/QueryViews.cs`, the contract's `QueryRecord` and `DatabaseView.Queries`). Each new field is left out of the JSON when unset, so answers for a project without them don't change:

| Record | Change | Meaning |
| --- | --- | --- |
| `QueryView` | `form` (new) | `"sql"` for a hand-written query; left out for a built one |
| `QueryView` | `texts` (new) | The file's `sql` map; left out when built |
| `QueryView` | `writes` (new) | Ids (keys, for a synthesized table) of the tables written; left out when empty |
| `QueryView` | `with` (new) | List of `namedQueryView`; left out when empty |
| `QueryView` | `from` | Now optional: left out for a hand-written query |
| `QueryView` | `uses` | A hand-written query's `reads`, resolved |
| `namedQueryView` (new) | `name`, `query`, `columns`, `recursive`, `union` | The anchor as the file writes it; its columns as `queryFieldView`; the repeating part as written, and `union`, left out when not recursive |
| `QuerySourceView` | `namedQuery` (new) | The named query's name, when the source is one; left out otherwise |
| `QueryFieldView` | `expression` | Now optional: left out for a declared column |
| `QueryFieldView` | `description` (new) | A declared column's; left out when none |

**Regenerating the contract.** After the record changes, the one command of the dev skill (`.claude/skills/maquettiste-dev/SKILL.md:43-48`) writes `schemas/v1/resolved-model.json`, `docs/resolved-model.md` and their copies in `samples/reference-app/.maquettiste/.schema/v1/`. `openapi.yaml` changes by hand (the records above, the query schema), and the Functions contract tests check it against real answers.

## 11. Rendering

- `QuerySql.Render` (`Rendering/QuerySql.cs:51-57`): a hand-written query renders its text (decision 5). No text for the dialect gives an empty statement and MQ4029, as an `sql` expression does today (`:530-538`). `RenderCollection` is never called for one (it has no collections).
- `QuerySql.Top` writes the `WITH` clause first (decision 14), so placeholders are numbered in text order. A query without `with` takes the same path as today.
- `RenderCollection` writes the `WITH` entries the collection reads.
- `BindingSql.Select` (`Rendering/BindingSql.cs:128-167`) puts a built query's `WITH` before its derived table (decision 16). A hand-written source's body is its rendering without the final `;` and the comments after it (`QuerySql.Wrapped`, decision 5).
- `Rendering/SqlText.cs` (new, internal): the per-dialect scanner of decision 4 and its four answers (decision 6), ordinal and culture-invariant like the rest of rendering.

## 12. Tools and API

- **No new tool.** `preview_query_sql` and `GET /api/model/queries/{id}/sql` answer both forms; a hand-written query's preview has no collections. Previews stay one query at a time.
- `get_schema query` shows both forms and their texts. `get_resolved_model`, `get_template_context` and `model export --resolved` show the new members.
- `apply_batch`, `save_element` and `patch_element` take qualified names in `reads` and `writes` (`@table:billing.invoices`), as in `from.source`.
- `get_references` lists `reads` and `writes` entries. **Impact** (`BatchImpact.cs`) lists a hand-written query among the referrers of a table it reads or writes; its pack units render again only when the query's own file, or a name it prints, changes (decision 18).
- **Deletes:** decision 17, with the new delete-plan warning.
- **Index:** no new row or member. The format stays `maquettiste-index/e14` (`Editor/ModelReads.cs:23`).
- **Tool texts:** `Agent/tools.json` (`preview_query_sql`: a hand-written query's text is its own, `:name` rewritten; `lists` does not apply), `ModelTools.cs` (`save_element`: the two forms), the modeling skill's queries row (`skills/maquettiste-modeling/SKILL.md:28`, which says "never SQL text"), and its `preview_query_sql` line (`:115`). The conventions resource and `AssistantPrompt.cs` embed the skill, so they need no code change.

## 13. Editor

**Creating.** The New query dialog (`src/editor/src/explorer/databaseCreate.ts`) asks **Written as**: **Built from parts** (today's dialog, which asks for the table it reads first) or **SQL text** (name only; it opens on the SQL text tab with a `SELECT` starter for the database's dialect).

**A hand-written query** shows these tabs: General, **SQL text**, **Parameters**, **Columns**, **Tables**, **SQL**, JSON, Code generation, References. The built-query tabs (Sources, Select, Filter, Group and order, Collections, Named queries) are not shown.
- **SQL text**: the shared `DialectBodies` editor (`editors/database/DialectBodies.tsx:1-3`), one code editor per dialect the text holds, with Add dialect, as views and routines have. It marks each `:name` placeholder, and underlines one that names no parameter, with the MQ4024 text as its hover. The marks come from the editor's copy of the scanner, so they show as you type, with no call to the server.
- **Parameters**: the existing `ParametersSection` (`QueryParts.tsx:1007`). A parameter the text never uses shows MQ4096 in its row.
- **Columns**: a grid like the view editor's Columns tab: **Name**, **Type** (built-in and database types), **Can be null** (checked by default), **Description**. Renaming a column renames it in the bindings that read the query by that name, in the same batch, as for a view column.
- **Tables**: two lists, **Reads** and **Writes**, each with an **Add** picker of the database's tables and views (Writes: tables only), from the index. **Find in SQL** (a text button) lists the tables and views whose names appear in the text, unticked, for the user to add. It reads the index and the open document only. MQ4098 shows on the row it is about.
- **SQL**: the existing preview tab (`QueryEditor.tsx:189`), unchanged: what packs receive, for any dialect and placeholder style. So **SQL** means the same thing for both forms.

**A built query** gains a **Named queries (WITH)** tab, after Sources.
- One card per named query: **Name**; the anchor's Sources, Select and Filter, using the same `SourcesSection`, `SelectSection` and `FilterSection` (`QueryParts.tsx:258`, `:434`, `:740`), which already take any `Subquery` body; and a **Repeats** switch. Switched on, a second panel, **Repeating part**, holds its own Sources, Select and Filter, and a **Union** choice (All rows, Distinct rows). Its select starts with the anchor's field names.
- Every source picker in the query lists **Named queries** as a group above Tables and Views. Column pickers on a named-query source list its column names.
- Renaming a named query, or one of its fields, rewrites the references to it in the same save.
- MQ4099 show on the card and row they are about.

**Storage tab.** The binding's source picker lists hand-written queries with the others. When the picked query writes or has no columns, one line under the picker says "This query changes data; an entity can only read through a query that returns rows" (MQ4044's case).

**Words.** Plain labels: **Written as**, **Built from parts**, **SQL text**, **Columns**, **Can be null**, **Tables**, **Reads**, **Writes**, **Find in SQL**, **Named queries (WITH)**, **Repeats**, **Repeating part**, **Union**. Tooltips on every icon button. No vendor names.

## 14. Mock backend

The mock mirrors the engine (`src/editor/src/mocks/model/queries.ts`, 1,526 lines, resolves queries and reports their rules; `querySql.ts`, 695 lines, renders them).
- `model/sqlText.ts` (new, shared by the mock and the SQL text editor): the scanner, tested against the same vectors as the engine's (`tests/fixtures/sql-text/scanner.json`).
- `queries.ts`: both forms, named queries and recursive parts, and the rules of section 9. `querySql.ts`: the hand-written rendering and the `WITH` clause per dialect. The binding source checks: MQ4044's case and MQ4097.
- `refs.ts` and `cascade.ts`: `reads` and `writes` as references, and the delete warning.
- `mocks/recorded/validation-rules.json`: regenerated with the four rules.
- `tests/unit/mock-queries.test.ts` gains cases for each rule and rendering.

## 15. Packs

| Pack | Change |
| --- | --- |
| `csharp-dapper` | `query.scriban`: a hand-written query with columns takes today's path unchanged. Its rows are the declared columns, and `query_sql query` gives its text with `@name`. A declared column's `expression` is null, and `query.from` is null for a hand-written query. **Relaxed member access does not make `f.expression.table_column` null:** reading a member of a null value is an error (MQ6006), so a template branches on `query.form` (or tests `f.expression` for null) before it reads into an expression or `query.from`. The shipped `query.scriban` guards each such read, and its record member takes the column's own type. A team with its own copy of an older `csharp-dapper` must update it: the old copy fails with MQ6006 on the first hand-written query. **New:** a query with no columns gets `Task<int> ExecuteAsync(…)`, which runs the statement and returns the rows changed. The interface says so. A short comment names `query.form` and `query.select`. `_binding.scriban`: no change |
| `sql-ddl` | No change. A query creates nothing in the database |
| `process-docs`, `seed-data`, `samples/typescript-pack` | No change. None reads queries |

**Built-query output stays byte for byte.** The new branch in `query.scriban` is taken only when `query.select` is empty, which a built query can't be (`select` has at least one field). Named queries change only the SQL text, and only for queries that have them.

## 16. Hand-edited files and `format`

- **Checked like any file.** Schema errors (MQ1002) and rule findings carry the JSON pointer of the key (`/sql/postgresql`, `/columns/2/type`, `/reads/0`, `/with/0/recursive/select/1`), which positions map to a line and column of the file. A finding inside the text (a placeholder) also gives the line and column within the text in its message (MQ4024).
- **`format`** writes keys in the schema's order and drops values equal to a default (`nullable: true`, `union: "all"`, empty lists), as for every file (`Json/CanonicalJson.cs:15-16`). It never changes a text: the string is stored as given, escaped as JSON requires. `format --check` reports a non-canonical file (MQ1003), as today.
- **Hand-written SQL is long in a JSON string.** Decision on question 4: it stays there in this item, as views and routines do.

## 17. Docs to update

- **User guide** (`docs/user-guide.md`):
  - "Queries" (`:1000-1007`): a query is built from parts *or* written as SQL text; the "never SQL text" sentence changes.
  - New subsection **"Queries written as SQL text"**: the file, `:name` and the exact placeholder rule, declared columns and what `nullable` means, `reads` and `writes`, commands, what the engine reads from the text and what it never reads, and section 5's rows for these keys.
  - New subsection **"Named queries (WITH)"**: shape, where they are read, recursion, section 7's per-dialect table, recursion limits and how to stop a cycle.
  - The rules table (`:1180`) gains MQ4096 to MQ4099 and the extended cases. "A read-only entity over a query" (`:1467`) adds hand-written queries and MQ4097; the binding rules table (`:1659`) gains MQ4044's case.
  - The editor's query section (`:1208-1250`) describes the dialog choice and the new tabs.
- **Engineering:** `engine-design.md`'s Queries section (`:927`) gets a dated line pointing here; `next.md` row 5.5 gets its status, and the "Raw SQL as a read source" line (`:227-228`) says it is built through hand-written queries.
- `docs/releases.md` under "## Unreleased", the modeling skill (section 12), `docs/resolved-model.md` (generated), and the owner's plan checklist doc.

## 18. A project that uses none of this

- **Files:** nothing rewrites a file. No key a file holds today gains a default. Saves, `format`, `format --check` and snapshot import behave as today.
- **Findings:** the same. Every new rule and case needs `sql`, `with`, or a binding over a hand-written query. **Pinned by** the existing query rule tests (`tests/Maquettiste.Engine.Tests/Resolution/QueryRulesTests.cs`) and a test that validates the billing, bindings and showcase fixtures before and after.
- **Output:** the same bytes for every pack. **Pinned by** the pack goldens (`tests/Maquettiste.Packs.Tests/GoldenTests.cs`), which pass untouched, and the new fixture living in its own folder.
- **Dependency keys:** the same. A built query without `with` keys as today.
- **Tool answers:** the same; new record fields are left out when unset. Answers that change for every project, as in earlier items: `list_validation_rules` (four rules), `get_schema query`, `tools/list` texts, the conventions resource, `get_template_context`.
- **Speed:** the same. The scanner runs only on hand-written texts. The bench and its baseline (`bench/baseline.json`) pin it.
- **Once, as on any upgrade:** every unit renders again with the same bytes, since the engine version is part of each unit's key; the `.schema` copies refresh (MQ1008 until then).

## 19. What comes next

Each waits on purpose and adds an optional key later without changing a file.

| Item | Why it waits |
| --- | --- |
| SQL text in a separate `.sql` file beside the query (and for views and routines) | Question 4. Needs sidecar loading, watching, renames and snapshots for three kinds at once |
| `maxRecursion` and cycle detection on a recursive named query | Decision 13. Each dialect says it differently; no team has asked |
| **Convert to SQL text** on a built query | Starts a hand-written query from the built one's SQL. Useful, not requested |
| One row, or one value, as a query's result | A new result shape for both forms |
| `table.written_by` (the queries that write a table) | Templates can filter `database.queries` by `writes` today |
| Writes to views | Few teams write through views |
| Columns checked against a live database | `db check` (6.5) |

## 20. The three questions

**How does the UI change?**
- The New query dialog asks one more thing: built from parts, or SQL text.
- A hand-written query has its own small set of tabs: write the SQL, list the parameters, list the columns, list the tables it reads and writes. The SQL tab every query has shows what packs receive.
- A built query gets one more tab, Named queries (WITH), made of the same Sources, Select and Filter panels it already has. Source pickers gain a Named queries group.
- The entity's Storage tab can pick a hand-written query, and says plainly when a query can't be read through.
- New warnings show in Problems and on the row they are about.

**Will it be intuitive?** Mostly yes.
- The SQL text tab is the same editor views and routines already use. Placeholders light up as you type, and a misspelled one is underlined at once, before any save.
- Columns and Tables are plain grids with pickers. Find in SQL fills Tables for you from the text, and you choose what to keep.
- **The riskiest part is the recursive named query.** Recursion is hard for many people. To help: the card is called "Repeats", the second panel "Repeating part", its select starts with the anchor's names, and every MQ4099 finding says in words what the repeating part may not do. The user guide's walk-through is the category tree above. The browser pass checks that a first-time user can build it.
- The second risk is the word "SQL" on two tabs. The writing tab is "SQL text"; "SQL" stays the preview, as on every query today.

**How does it help modeling and code generation?**
- **Code generation:** a database-first team stops hand-writing data-access methods. Each hand-written query becomes a typed method, a typed parameter list and a typed row, generated the same way as a built query. Commands get typed methods too. An entity's repository can run the team's own SQL through a binding. Recursive named queries cover trees, chains and running totals without leaving the builder. The packs stay examples: any team's pack reads the same members (`query.form`, `query.sql_text`, `query.select`, `query.uses`, `query.writes`, `query.with`) to generate repositories, API endpoints, documentation or test fixtures.
- **Modeling:** every hand-written statement becomes part of the model, with its parameters, result shape and tables stated. Impact analysis answers "what does changing this table reach?" for hand-written SQL as it does for everything else. A renamed table warns on every query whose text still uses the old name. Deleting a table never deletes a team's SQL.

## 21. Tests and fixtures

- **Scanner** (`SqlTextTests`, new): one vector file `tests/fixtures/sql-text/scanner.json` (text, dialect, expected placeholders with offsets, first word, names, top-level order) read by the engine's tests and the editor's unit tests, so both scanners agree. Cases: every quote and comment form per dialect, `::`, `:=`, slices, labels, `$tag$`, `q'[…]'`, nested comments, MySQL backslashes, and the statement's verb behind a leading `WITH` (`WITH x AS (…) UPDATE`, `WITH RECURSIVE x (a) AS (…) SELECT`, a `WITH` entry whose body itself holds `INSERT … RETURNING` inside parentheses); a leading byte order mark, U+0085 and a letter number (`Ⅷ`); and the final `;` (`finalSemicolon`: after comments, inside strings, quoted names, comments and parentheses, before a word).
- **Hand-written queries** (`SqlQueryTests`, new): rendering in each placeholder style, `$n` numbering with repeats, trimming (the final `;` kept, and left out only where a binding wraps the text), `"*"` fallback, MQ4029; each rule and extended case, failing and passing; dependency keys (a column change on a designed read table renders the unit again; on a table whose columns come from entities it renders nothing again; a rename renders a unit that prints the name); delete plans for both resolutions and the warning.
- **Named queries** (`NamedQueryTests`, new): rendering in all five dialects (keyword, column list, union), a collection reading one, an `exists` reading one, a named query reading an earlier one, a binding over a built query with `with` (hoisted), MQ4099 cases per dialect, type inference and merged nullability.
- **Contract:** `ResolvedContractTests` after the regenerate command; `ResolvedCoverageTests` and `ResolvedNullContractTests` cover the new members.
- **Packs:** a new fixture `tests/fixtures/models/sql-queries` (a category tree table; `CategoryAncestors`; `TopCustomersByRegion`; `MarkOverdueInvoices`; an entity bound to a hand-written query) with goldens `golden/csharp-dapper/sql-queries` and `golden/sql-ddl/sql-queries` (the second equal to the fixture without queries). `CompileTests` builds the generated code and runs the recursive query, a hand-written query, the command and the binding repository on SQLite in process, as it runs `LocalInvoicesByCustomerQuery` today (`tests/Maquettiste.Packs.Tests/CompileTests.cs:2302`); `ServerTests` runs them on PostgreSQL and SQL Server when the test containers are set. Existing goldens pass untouched.
- **Editor:** unit tests for the scanner (shared vectors), the column rename, Find in SQL, and named-query renames. Browser tests: create a hand-written query, mistype a placeholder and see the mark, declare columns and tables, bind an entity to it; build `CategoryAncestors` and see its SQL for PostgreSQL and SQL Server.
- **Acceptance**, on the new fixture: all three queries generate typed methods that compile and run; renaming `customers` gives MQ4098 on `TopCustomersByRegion`; `WITH … UPDATE` with no `writes` and a `SELECT` with no `columns` each give MQ4098; a declared `Invoices` matches `invoices` in the text; deleting `invoices` with `remove-references` keeps both hand-written queries and warns; `union: "distinct"` on a SQL Server copy gives MQ4099.

## 22. Tasks, build plan and estimate

### Size

With tests, without pack expected files: **about 8,300 lines (7,200 to 9,600).** Bigger than 4.6 (about 5,000): it has two halves, a scanner for five dialects, and two editor areas.

| Group | Lines |
| --- | --- |
| Contract: schemas, `openapi.yaml`, rule texts | 350 |
| Model records, references, deletes, with tests | 400 |
| Scanner for five dialects, shared vectors, tests | 650 |
| Hand-written queries: resolution, rendering, rules, bindings, with tests | 1,250 |
| Named queries: resolution, rendering per dialect, collections, binding `WITH` first, rules, with tests | 1,600 |
| Records, contract, template members, tool texts, with tests | 450 |
| `csharp-dapper` command method, new fixture, goldens, compile and database tests | 550 |
| Editor: dialog, SQL text, Columns, Tables, marks, Named queries, pickers, Storage line; mock; tests | 2,600 |
| Docs | 450 |

### How it gets built

Lean, as the owner asked: one pull request, one review at high effort, one browser pass. Each agent is capped at about 100 steps or 200k context. One kind of agent builds the engine and the pack; another builds the editor, the mock and the docs, and runs the browser pass. At most three agents run at once, in worktrees fast-forwarded to the branch `sql-queries`, merged by patch. **File owners in wave 2:** the hand-written agent owns `QueryViews.cs`, `ResolvedRecords.cs`, the contract regenerate command and the tool texts; the named-query agent writes `DatabaseRun.NamedQueries.cs`, `RNamedQuery` and its rendering, and hands the record wiring to the first through a short note in its final report.

| Wave | Agent | Steps | Touches | Starts from |
| --- | --- | --- | --- | --- |
| 1 | **Contract first** (schemas, all `openapi.yaml` changes, rule texts), then model records, references, the delete warning, the scanner and its shared vectors | About 80 | `schemas/`, `docs/api/`, `Model/Queries.cs`, `Diagnostics/`, `Loading/ChangePlanner.Cascade.cs`, `ModelStore.DeletePlan.cs`, `Rendering/SqlText.cs`, `tests/fixtures/sql-text/` | `main` (ca779f8b or later) |
| 1 | Editor part 1: dialog choice, SQL text tab with marks, Columns, Tables with Find in SQL, the TypeScript scanner | About 80 | `src/editor/src/editors/database/`, `explorer/databaseCreate.ts`, `model/sqlText.ts` | The contract commit |
| 2 | Hand-written queries: resolution, rendering, rules, binding cases, records, contract regenerate, tool texts | About 90 | `DatabaseRun.SqlQueries.cs`, `DatabaseRun.Bindings.cs`, `QuerySql.cs`, `QueryViews.cs`, `ResolvedRecords.cs`, `Agent/`, `skills/` | Wave 1's engine agent |
| 2 | Named queries: resolution, rendering per dialect, collections, binding `WITH` first, MQ4099 | About 100 | `DatabaseRun.NamedQueries.cs`, `ResolvedQueries.cs`, `QuerySql.cs` (the `WITH` writer only), `BindingSql.cs` | Wave 1's engine agent |
| 2 | Editor part 2: Named queries tab, source pickers, Storage line; mock for both forms and the rules | About 90 | `editors/database/QueryParts.tsx`, `editors/storage/`, `mocks/model/` | Editor part 1, in the first free slot |
| 3 | `csharp-dapper` command method, the new fixture, goldens, compile and server tests | About 60 | `packs/csharp-dapper/query.scriban`, `tests/fixtures/`, `tests/Maquettiste.Packs.Tests/` | Both wave 2 engine agents |
| 3 | Review of the whole branch | About 60 | Read only | Every builder's finish |
| 3 | Docs; browser pass | About 40 each | `docs/`; the running editor | Beside the review |
| 4 | Fixers by area; generated files; one push; the pull request; CI. A "Version" commit only if the owner wants a release | About 30 each | The findings | The review |

A design gap found while building stops the work, and the owner is asked. **If the owner prefers a smaller pull request,** the named-query half (its wave 2 agent, its editor part, its tests) can wait for a later one: nothing in the hand-written half depends on it.

### Estimate

**Calibration,** as in the 4.6 design: about 155 output tokens, 425 new input tokens per line built; cached re-reads about 70M before size plus about 21M per thousand lines.

| | Wall time | Output | New input | Cached re-reads |
| --- | --- | --- | --- | --- |
| Wave 1 | About 0.9 hour | 0.3M | 0.8M | 55M |
| Wave 2 | About 1.6 hours | 0.6M | 1.6M | 110M |
| Wave 3 (builders) | About 0.8 hour | 0.2M | 0.55M | 40M |
| Wave 4 | About 0.7 hour | 0.15M | 0.45M | 35M |
| **In all, the builders** | **About 4.0 hours (3.5 to 4.8)** | **About 1.25M** | **About 3.4M** | **About 240M (215M to 270M)** |
| The review | Within wave 3 | About 0.06M | About 0.2M | About 16M |
| **Everything** | | **About 1.3M** | **About 3.6M** | **About 256M** |

Cutting the named-query half saves about a third: roughly 1.4 hours, 0.4M output and 80M cached re-reads.
