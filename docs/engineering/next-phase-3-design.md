# Next capabilities, phase 3: design

**Status (2026-11-03): approved by the owner, with the recommended answer to each question in section 4. Nothing in
this file is built yet.** It designs one roadmap item of phase 3, item 3.8 of `next.md`: one expression language for
checks, validations, derived values and guards. It keeps to the roadmap's decisions of 2026-10-30 for that item (a
closed, typed core lowered to SQL and JavaScript, three-valued null semantics, a typed tree of ids with a textual form,
named constraints, no emitters for other languages, the evaluation mode set by the use site, PostgreSQL and JavaScript
first) and decides only what they leave open. `next-phase-1-design.md` and `next-phase-2-design.md` stay binding for
what they built; `phase-3-design.md` is the earlier design of processes, not this phase. `docs/api/openapi.yaml` and
`schemas/v1/` remain the source of truth for their contracts. File, type, member, rule, verb and tool names here are
binding.

## 1. Scope

| Scope item | What this design builds | Section |
| --- | --- | --- |
| 3.8 | A small, closed, typed expression core stored as a tree of ids, with a text form; lowered to PostgreSQL and JavaScript; used by table and column checks, entity checks, derived values, process guards, operation guards and declared rule conditions; named `constraint` elements; a differential corpus | 3.1 |

## 2. Conventions

### 2.1 Tasks and verification

The section ends with its tasks, H1 to H14, with H9 left unused; H15 to H19 are free. A task names the files it owns,
the tests it adds and a Verify line. The Verify legend, the pinned counts and the MCP test classes are those of section
2.1 of `next-phase-1-design.md`. A file path without a project prefix is under `src/Maquettiste.Engine/`. The milestone
ends with the gate of phase 2: the full build, every .NET suite, the pack tests against PostgreSQL and the editor suite.

### 2.2 Rule ids

The section takes MQ3101 to MQ3120, a new family MQ31xx for expressions. No id of the family is in
`Diagnostics/RuleCatalog.cs` or in any design file today (checked on 2026-11-03), and phase 2's blocks stop at MQ3040
in the MQ30xx family. `list_validation_rules` with `family: MQ31xx` lists them.

## 3. Milestone H: one expression language

### 3.1 One expression language for checks, validations, derived values and guards

**Why every team**

Every team states rules about values: a due date is not before the issue date, a quantity is positive, a code is three
capital letters, a payment never exceeds what is owed. Today each rule is written again wherever it must hold: SQL text
for the database, JavaScript for process simulation, and by hand in the API code a pack generates. Each copy treats a
missing value its own way, so the database accepts a row the API refused, or a guard passes in simulation and fails in
the running system. One language, written once in the model and stored by id, gives every place the same answer. The
engine turns it into the CHECK and into JavaScript, packs render it in their own language from the same typed tree, and
renaming an attribute or a column never breaks it. A team that writes no expression sees no change.

**Today**

- Table and column checks are SQL text per dialect (`schemas/v1/common.json:876-899`, `Model/Physical.cs:675-689`). A
  check with no text for the database's dialect is dropped without a word (`Resolution/DatabaseRun.cs:265-271`), and so
  is a stereotype's (`Resolution/DatabaseRun.Members.cs:77-81`). Column names inside the text do not follow a rename.
  `RCheck.Expression` is the dialect's text (`Resolution/ResolvedPhysical.cs:391-409`) and the schema differ compares
  that text (`SchemaDiff/SchemaDiffer.cs:535-540`). Computed columns are SQL text too
  (`schemas/v1/common.json:675-681`).
- An attribute's `validation` is `min`, `max`, `pattern`, `allowedValues` and `rules`, the ids of JavaScript rules
  (`schemas/v1/common.json:348-376`, `Model/Conceptual.cs:208-224`). The pattern is any regular expression the engine
  accepts (MQ3013, `Validation/AttributeRules.cs:157-166`). The C# example pack renders `min` and `max` into command
  validation (`packs/csharp-dapper/dispatch-behaviours.scriban:32-36`). Nothing relates two attributes.
- `derived.expression` is a free string "interpreted by templates" (`schemas/v1/common.json:378-392`,
  `Model/Conceptual.cs:186-194`), copied as text into the resolved model (`Resolution/ResolveRun.Conceptual.cs:1004`).
  No shipped pack reads it.
- Process guards and actions are JavaScript strings (`schemas/v1/process.json:433-467`, `Model/Processes.cs:272-305`),
  compiled in the script sandbox as `(context, event) => (expression)` (`Processes/ProcessExpressions.cs:115-136`), with
  MQ9501 for syntax (`:142-158`) and MQ9502 and MQ9503 for errors and limits (`:240-256`). A guard holds only when it
  returns `true`; anything else is MQ9504 and counts as false (`Processes/StatechartInterpreter.cs:486-509`). Inside the
  expression JavaScript's rules apply: `null >= 0` is true, `null + 5` is 5, a missing context member is `undefined`.
  The fixtures and the reference app hold three guard expressions and five action expressions.
- The C# example pack translates a documented subset of guard and action JavaScript to C#
  (`packs/csharp-dapper/helpers.js:54-59`), leaves out every operator where the two languages disagree, ordering on a
  value that may be null among them (`:215-224`), and writes a stub for the rest
  (`docs/engineering/phase-3-design.md:640-643`). That subset is the only portable form guards have today.
- The query kind has an expression tree, but it is a SQL syntax tree: `call` passes any identifier through
  (`schemas/v1/query.json:460-470`, MQ4034), `sql` is opaque text (`:495-499`), and predicates are a separate tree
  (`:537-605`, `Model/Queries.cs:268-345`). Function names map per dialect, and `length` becomes `LEN` on `sqlserver`,
  which ignores trailing spaces (`Rendering/QuerySql.cs:137-150`). Its column references are ids or keys marked
  `[ElementRef(Keyed = true)]` (`Model/Queries.cs:270-271`), which is how the index sees them.
- Operations carry no guard until this item (`next-phase-2-design.md:3717`); their errors already point at an input
  member (`Model/Operations.cs:196-204`).
- Declared rules have a closed, two-valued set of conditions (`Model/DeclaredRules.cs:61-86`,
  `Validation/DeclaredRuleEvaluator.cs:154-174`); phase 2 left an `expression` condition, "judged at a guard site", to
  this item (`next-phase-2-design.md:8159-8160`).
- Calculated values (phase 2, section 5.5) stay JavaScript (`next-phase-2-design.md:7961`): they compute facts about
  the model, such as a name or a topic, not about records, so this item does not touch them.

**Decisions taken here**

1. **One core with its own tree.** The query tree cannot be typed (`call`, `sql`) nor turned into JavaScript, so the
   core is a new, closed tree, and the query tree becomes one of its targets. The query tree keeps values and
   predicates apart and has no node for a predicate used as a value, so the lowering adds an internal `pred` node.
   Reason: the settled decision of 3.8 makes the query tree a target, so a later dialect and projections (3.12) reuse
   its writer. Lowering straight to PostgreSQL text would need a second writer for the same SQL, and the gap is one
   node.
2. **Stored as a tree of ids; text is for people.** Paths, enum members, reference-type codes and constraints are ids,
   so renames follow with no rewrite. The text form is printed from current names on every read. Reason: an id is
   what every other reference in the model is.
3. **Write surfaces accept text and store the tree.** `save_element`, `create_element`, `apply_batch`, the CLI and the
   editor accept `{ "text": "issuedOn <= dueOn" }` wherever a tree goes, parse it in the site's scope and save the tree.
   Text that does not parse refuses the save with MQ3101 at its pointer. Reason: agents and people write text; storing
   it would let a rename break the expression silently.
4. **Hand-edited text is read, and warned.** A file edited outside the engine may hold a `text` node. It is parsed
   and typed like a tree, and MQ3108 warns that renames will not follow; the batch operation `text-to-trees` converts
   it. Reason: files are edited by hand in repositories and must not fail to load.
5. **The mode belongs to the site.** Four kinds of site: a check site (table, column and entity checks), where unknown
   passes; a guard site (process and operation guards), where unknown fails; a value site (derived values), which
   yields the value; and a rule condition, judged as a guard. One expression answers as each site requires. Reason:
   scope 13, item 7.
6. **`required` stays separate.** Whether a value must be given is `required` on the attribute, the input member or
   the column's nullability, never folded into an expression. Reason: scope 13, item 7.
7. **Three-valued logic, decided once.** Kleene logic as in SQL, missing and null are one value, no function throws,
   and division by zero is null. Reason: a CHECK and a generated validation must never disagree on the same input.
8. **Numbers are exact.** Integers and decimals are exact in both targets: `numeric` in PostgreSQL, scaled integers
   in the JavaScript runtime, which they enter as exact text. `double` values compare and compute as IEEE doubles in
   both. `float` is not in the core: it is MQ3103 at every site, since every site can lower to both targets, and a
   check on a `float` column stays SQL text. Reason: money must not drift through a binary float. A `real` value is
   rounded to single precision only when PostgreSQL stores it, and PostgreSQL then compares it as a `float8`, so
   `1.1::real > 1.1` is true there and false for the 1.1 a JavaScript caller holds. `Math.fround` on every float path
   would fix that, but it must reach every input, literal and result in both targets; refusing `float` needs no rule
   in either target and no corpus rows, and `real` columns are rare.
9. **Portability is decided in the core.** Dates move by functions, never `+`; mixing a date and a timestamp needs
   `date_of`; offset timestamps compute in UTC, and a `datetime` compared with a `datetimeoffset` reads as UTC;
   `length` counts code points; strings compare by code point; fixed-length strings read without their trailing
   spaces; `trim` removes spaces only; `in` lists hold literals and no null; regular expressions use the portable
   subset. Reason: each rule removes a case where the two targets would answer differently (the language section
   lists them).
10. **One row at check and value sites.** Paths through relations are allowed at guard sites only, and only through
    to-one navigations. Reason: a CHECK reads one row, and a derived value must be computable where the row is.
11. **`now` at guard sites only.** `now` is a `datetimeoffset`. Check sites, value sites and rule conditions reject
    it. Reason: a CHECK must not depend on the clock, a derived value must be the same wherever it is computed, and
    validation must give the same answer twice.
12. **Two targets: PostgreSQL and JavaScript.** A tree check on a database of another dialect reports MQ4056 and the
    DDL leaves it out. Reason: scope 16, item 1; the corpus proves agreement for PostgreSQL only.
13. **One JavaScript runtime, one evaluator.** The engine ships one runtime file; lowered JavaScript calls it. The
    engine evaluates guards and rule conditions by running lowered JavaScript in its sandbox, and packs that write
    JavaScript copy the same runtime. Reason: the code the corpus tests is the code that runs everywhere.
14. **No other emitters.** Packs render other languages from the typed tree; the C# example pack gains an example
    renderer to copy. Reason: response 4.4; the product has no official target language.
15. **Raw text stays as the escape hatch.** Every site keeps its raw form (a check's SQL per dialect, a guard's or a
    derived value's string) beside the tree, exactly one per site; a process guard may hold neither and stays a stub,
    as today. Reason: dialect-only checks and unusual guards exist, and stub guards are legal now.
16. **Entity checks reach a table only through a binding that lists them.** A binding's `checks` names the entity
    checks its write table enforces, lowered through its field map. MQ3115 warns when a write binding could enforce a
    check that no write binding lists. Reason: a database holds only what is mapped to it, and the mapping lives on the
    entity side; the warning keeps the list from drifting silently, and its severity is the project's to set.
17. **Named constraints.** A `constraint` element holds one condition over typed parameters; any site cites it with
    `satisfies`, binding each parameter to an expression, and lowering inlines it. Reason: one rule written once, used
    by tables, entities and guards, and the target of requirements (3.10) when they land.
18. **Process guards gain `condition`; actions stay JavaScript.** Reason: guards are conditions; actions are effects
    and become operations in phase 4 (`Model/Processes.cs:290`).
19. **Existing JavaScript guards stay JavaScript until a person rewrites them.** Nothing converts them; the guide shows
    how to rewrite one by hand with `preview_expression`. Reason: a rewrite changes behaviour for null values and for
    JavaScript's loose rules, so a person decides each one; the fixtures and the reference app hold three guards, too
    few to justify a converter.
20. **Operation guards name the error they raise.** Reason: phase 2 gave errors an input pointer; a guard that fails
    reports through it.
21. **Rule conditions read members.** A declared rule's `expression` reads the selected object's members with phase 2's
    path syntax. Reason: rules judge the resolved model, whose members are names of the API, not user names.
22. **A CHECK keeps its identity across a column rename.** A tree check's snapshot carries a hash of its lowered SQL
    with column keys in place of column names, and the differ treats equal hashes as unchanged. Reason: PostgreSQL
    renames columns inside checks itself; dropping and re-adding the check would rescan the table for nothing. The
    hash is of the lowered check, not the stored tree, so a change to a cited constraint, a binding's field map or an
    enum's stored value still changes the check.
23. **A project without expressions pays nothing.** No runtime is loaded, nothing is typed or lowered, no new key is
    written to any file, record or snapshot, and JavaScript lowering happens only when a template or tool reads it.
24. **Previews read one element.** `preview_expression` reads the element, the elements its paths name and, for SQL,
    the one binding or table it lowers through. Reason: models are large; only generation reads all of them.

#### Schema and files

`common.json` gains `$defs/expr` (an expression node), `$defs/exprWhen` (a case branch) and `$defs/exprArgument` (a
`satisfies` argument, `{ parameter, value }`). A node has exactly one of these keys:

| Key | Holds | Notes |
| --- | --- | --- |
| `value` (with `type`) | a literal | Strings, integers and booleans need no `type`. `type` is `decimal`, `double`, `date`, `time`, `datetime`, `datetimeoffset` or `uuid`, with the value as text so that digits are kept, as the query tree does |
| `null` | `true` | The null literal |
| `path` | a list of ids, at least one | To-one navigation ids (relation end ids), then one attribute, column, input member, context or payload attribute, or constraint parameter id; `attributeId.memberId` for a value object member; keyed ids for inherited and contributed members |
| `ref` | an id | An enum member or a reference-type code |
| `member` | a path text | Declared rule conditions only: phase 2's path syntax (`properties.retentionDays`) |
| `now` | `true` | Guard sites only; a `datetimeoffset` |
| `op` with `args` | an operator and its operands | The operator table below |
| `call` with `args` | a function and its arguments | The function table below; a name outside it is MQ3101 |
| `case` with `else` | a list of `exprWhen`, at least one | `else` optional |
| `satisfies` with `with` | a constraint id and a list of `{ parameter, value }`, one per parameter | Boolean; `value` is a node |
| `text` | a string | Accepted on writes and converted; in a file it is MQ3108 |

`Model/Expressions.cs` (new) holds `Expr`, `ExprWhen` and `ExprArgument`, records shaped like `QueryExpression`.
`Path`, `Ref`, `Satisfies` and an argument's `Parameter` are marked `[ElementRef(Keyed = true)]`; `with` is a list
shaped like a binding's `fields`, so the walker indexes each parameter id as it indexes a field's attribute
(`Model/ModelIndexer.cs:947-953`) and walks each value as a record (`:969-981`); a map would have its keys indexed
but the nodes it holds never walked (`:937-941`). The index, delete refusal, layering (MQ2020) and
`get_references` see every id with no further code.

The sites gain these keys. Each site holds its raw form or its tree, never both and never neither (MQ3107); a process
guard may hold neither, since a guard without `expression` is a stub today (`Model/Processes.cs:272-284`), so for
guards MQ3107 reports only both:

| File | Key | Change |
| --- | --- | --- |
| `common.json#/$defs/checkConstraint` | `condition` (`expr`) | `expression` leaves `required`; table, column and stereotype checks |
| `common.json#/$defs/derived` | `value` (`expr`) | `expression` leaves `required` |
| `common.json#/$defs/entityCheck` (new) | `id`, `name`, `displayName`, `attribute`, `condition`, `message`, `description` | `attribute` is the attribute a failure points at; `message` is a translatable text |
| `entity.json` | `checks` (after `alternateKeys`), `bindings[].checks` (an id list) | Default `[]` |
| `process.json#/$defs/guard` | `condition` (`expr`) | After `expression` |
| `operation.json` | `guards`, after `errors`: `id`, `name`, `displayName`, `condition`, `error`, `description` | `error` is an operation error id, own or inherited |
| `declared-rules.json` | `expression` in a condition (`expr`) | Beside `all`, `any`, `not`, `has`, `equals`, `count`, `references`, `referencedBy` |
| `constraint.json` (new kind) | `parameters`, `condition` | Below |
| `snapshot.json` | `shape` on a check | The hash of its lowered SQL with column keys in place of names, tree checks only |
| `batch.json` | operation `text-to-trees` | |

A `constraint` lives in `model/constraints/<id>.json` (`KindInfo.ConstraintsFolder`). Its `x-order`: `$schema`,
`kind`, `id`, `name`, `displayName`, `package`, `parameters`, `condition`, `message`, `description`, `rationale`,
`decision`, `status`, `stereotypes`, `tags`, `category`, `properties`, `generation`, `source`. A parameter is an
`ElementBase` sub-element with `type` (`common.json#/$defs/typeRef`), so its type is an attribute's type. Its
condition's paths name its own parameters only. An example, the done-when of scope 3.8 written on `Invoice`:

```json
"checks": [
  {
    "id": "01JCK0DUEAFTERISSUE00000000",
    "name": "dueAfterIssue",
    "attribute": "<issuedOn id>",
    "condition": { "op": "le", "args": [{ "path": ["<issuedOn id>"] }, { "path": ["<dueOn id>"] }] }
  }
],
"bindings": [
  { "id": "…", "database": "<main>", "source": "<invoices table>", "checks": ["01JCK0DUEAFTERISSUE00000000"] }
]
```

`CheckConstraint.Expression` becomes an empty map by default, so code that reads it compiles unchanged and a tree
check is simply skipped until H7 lowers it.

#### The language

**Operators.** Every operator keeps SQL's semantics, and the JavaScript runtime implements those semantics.

| Text | Stored `op` | Operands and result | PostgreSQL |
| --- | --- | --- | --- |
| `=` `<>` (`!=`) `<` `<=` `>` `>=` | `eq` `ne` `lt` `le` `gt` `ge` | Two of one type → bool; ordering only on numbers, strings, dates, times and timestamps | The same; strings add `COLLATE "C"`, for `=` and `<>` too |
| `x between a and b` | `between` | As `a <= x and x <= b` | `BETWEEN`; strings add `COLLATE "C"` |
| `x in (…)`, `x not in (…)` | `in`, `notIn` | Literals and refs only, no null (MQ3106) | `IN`, `NOT IN`; strings add `COLLATE "C"` |
| `is null`, `is not null` | `isNull`, `isNotNull` | Any → bool, never null | The same |
| `is distinct from`, `is not distinct from` | `distinct`, `notDistinct` | Two of one type → bool, never null | The same; strings add `COLLATE "C"` |
| `and` `or` `not` `implies` | `and` `or` `not` `implies` | Bools → bool, Kleene logic | `implies` is `(NOT a OR b)` |
| `+` `-` `*` `/`, unary `-` | `add` `sub` `mul` `div` `neg` | Numbers → number; int with decimal is decimal | Integers as `numeric`, so nothing overflows; `/` below |
| `\|\|` | `concat` | Strings → string | `\|\|` |
| `coalesce(a, b, …)` | `coalesce` | Values of one type → that type | `COALESCE` |
| `case when … then … else … end` | `case` | Conditions bool, branches of one type | `CASE` |

`COLLATE "C"` goes on every string comparison, `=`, `<>` and `in` included: a column's `collation` may be
nondeterministic, and then `=` ignores case or accents where JavaScript does not.

`/` always yields a decimal of 16 fractional digits and is null when the divisor is zero:
`round((a + 0.000000000000000000) / NULLIF(b, 0), 16)` in PostgreSQL. PostgreSQL rounds twice there, and the runtime
repeats both roundings, each half away from zero:

1. The division rounds at the scale PostgreSQL's `select_div_scale` (`numeric.c`) picks. Each operand has a weight
   `w`, the place of its first non-zero group of four digits (0 for 1 to 9999, 1 for 10,000 to 99,999,999, −1 for
   0.0001 to 0.9999, and 0 for zero), and `f`, that group's value (0 for zero). The quotient's weight is
   `q = w(a) − w(b)`, less 1 when `f(a) <= f(b)`. The scale is the largest of `16 − 4q`, the dividend's scale (at
   least 18, from the added zero) and the divisor's scale, at most 1000. So `1 / 3` divides at 20 digits and
   `1 / 1000000` at 24.
2. `round(…, 16)` rounds that quotient again.

So `2 / 10673`, which divides at 20 digits and whose digits after the point run `0001873887379368 49995…`, is
0.00018738873793685000 after the first rounding and 0.0001873887379369 after the second, where one rounding of the exact
quotient gives 0.0001873887379368; the corpus holds that row at the `…4999…` boundary. For the first step the runtime
keeps each decimal's scale as PostgreSQL does: the column's declared scale, else the digits of the text it crosses as; a
literal's digits; the larger for `+` and `-`; the sum for `*`; `n` for `round`; 16 for `/`; and a value's own through
`coalesce` and `case`.

Concatenation stays in the core, beyond the scope's list, because derived values need it and the query tree already
has it.

**Functions.** The table is closed. Adding a function needs its signature, its null rule, both lowerings and corpus
rows in one change.

| Function | Signature | Null rule | PostgreSQL | JavaScript runtime |
| --- | --- | --- | --- | --- |
| `trim(s)` | string → string | null in, null out | `btrim(s, ' ')` | Removes U+0020 at both ends, nothing else |
| `length(s)` | string → int | null in, null out | `char_length(s)` | Counts code points |
| `matches(s, p)` | string, pattern literal → bool | null when `s` is | `s ~ 'p'` | `new RegExp(p, "su").test(s)` |
| `nonnull_count(a, …)` | 1 to 16 values → int | Never null | `num_nonnulls(a, …)` | Counts values that are not null |
| `add_days(d, n)` | date, datetime or datetimeoffset, int → the same | null in, null out | `d + n`; `d + make_interval(days => n)`; offset: `d + make_interval(secs => n * 86400)` | Calendar days; offset: n × 86,400 seconds |
| `add_months(d, n)` | as `add_days` | null in, null out | `(d + make_interval(months => n))::date`; offset computed `AT TIME ZONE 'UTC'` | Clamped to the month's last day, as PostgreSQL |
| `date_part(part, d)` | `year`, `month`, `day` or `isodow` literal and a date or timestamp, or `hour` or `minute` literal and a timestamp → int; a time part of a date is MQ3103 | null in, null out | `extract(part from d)::int`; offset `AT TIME ZONE 'UTC'` first | The same fields in UTC; `isodow` is 1 (Monday) to 7 |
| `date_of(t)` | datetime or datetimeoffset → date | null in, null out | `t::date`; offset `(t AT TIME ZONE 'UTC')::date` | The UTC calendar date |
| `round(x, n)` | decimal, int literal 0 to 18 → decimal | null in, null out | `round(x, n)` | Half away from zero |
| `json_type(j)` | json → string | null in, null out; JSON `null` is `'null'` | `jsonb_typeof(j::jsonb)` | The same six names |
| `json_length(j)` | json → int | null unless an array | `CASE WHEN jsonb_typeof(j::jsonb) = 'array' THEN jsonb_array_length(j::jsonb) END` | Array length, else null |

**Regular expressions.** `matches` takes a literal pattern in the portable subset of scope 14, item 4: bracket classes
and ranges, anchors, quantifiers (greedy or lazy), groups and alternation. It refuses back-references, lookaround,
POSIX classes and, decided here, the shorthands `\d`, `\w` and `\s`, whose meaning depends on the locale in
PostgreSQL; write `[0-9]`. The flags `s` and `u` give JavaScript PostgreSQL's meaning of `.` and of a code point.
`Expressions/PortableRegex.cs` parses the subset; a pattern outside it is MQ3105.

**Text form.** The text reads like SQL. Precedence, lowest first: `implies` (right to left), `or`, `and`, `not`,
comparisons and `is`, `between` and `in`, `||`, `+` and `-`, `*` and `/`, unary `-`. Literals: `12`, `12.50` (a
decimal), `'text'` (quotes doubled), `true`, `false`, `null`, `date '2026-01-31'`, `time '09:30'`,
`datetime '2026-01-31T09:30'`, `datetimeoffset '2026-01-31T09:30Z'`, `uuid '…'` and `double '1.5'`. Names resolve in
the site's scope: attribute names and `customer.creditLimit` through a to-one navigation; column names (double-quoted
where they are not identifiers) at a table; `event.amount` for a payload attribute and bare names for context
attributes in a process guard; bare input member names and `subject.total` in an operation guard. `Status.Paid` names
an enum member and `PaymentTerm.NET30` a reference-type code. `now()` is the clock, a `datetimeoffset`, and
`satisfies PositiveAmount(amount: total)` cites a constraint. The printer writes lower-case keywords, the fewest
parentheses and one space around operators, so printing a parsed text twice gives the same text.

#### Typing

The typer gives every node a type and whether it may be null; packs need both to render a typed language.

- **Types.** `bool`; `int` (from `int16`, `int32` and `int64`); `decimal`; `double` (from `double`); `string` (from
  `string` and `text`); `date`; `time` (compare only); `datetime`; `datetimeoffset`; `uuid` (from `uuid` and `ulid`,
  equality only); `json` (through functions only); `enum E` (an attribute or column typed by enum E); `code R` (typed
  by reference type R, scope 6.4 columns included). `float`, `binary`, `duration`, ranges and collections are not
  available (MQ3103; decision 8 says why `float` is not).
- **Integer width.** An `int` keeps its width, 16, 32 or 64, so packs can render `int` or `long`: a path has its
  type's width, a literal the smaller of 32 and 64 that holds it, `length`, `date_part`, `nonnull_count` and
  `json_length` 32, and arithmetic 64, since PostgreSQL computes it in `numeric`.
- **Paths.** A path's type is its last member's: a scalar type gives its base type, a value object must be followed
  by a member, a navigation must be followed by an attribute. A path may be null when any member on it is optional or
  nullable, or any navigation is optional.
- **Rules.** Operands of a comparison share a type; `int` widens to `decimal` and `double`; a `datetime` meets a
  `datetimeoffset` (`now()` among them) as UTC, `(d AT TIME ZONE 'UTC')` in PostgreSQL and the same reading in the
  runtime, so the session's time zone never enters. `decimal` with `double` is an error, so is a date with a timestamp
  (the message suggests `date_of`), `+` on a date (it suggests `add_days`) and `date_part` of `hour` or `minute` on a
  date, which PostgreSQL refuses. A `ref` must belong to the enum or reference type of the other operand. A check, guard
  or rule condition must be `bool`. A derived value must have its attribute's type, where an `int` result fits a
  `decimal` attribute. Each error is MQ3103 at the node's pointer.
- **Size.** An expression holds at most 512 nodes and nests at most 64 deep (MQ3113). Since the core has no loops,
  that bounds every evaluation.
- **Constraints.** A constraint is typed once, against its parameters. A `satisfies` must bind every parameter, to an
  expression of the parameter's type; constraints may cite constraints, without a cycle (MQ3109). A constraint that
  uses `now` can be cited only at guard sites.

#### Null semantics

Decided once for every site and both targets:

| Case | Answer |
| --- | --- |
| A path to a missing value, or a member missing from a JSON payload | null; missing and null are one value |
| Comparison, arithmetic, `\|\|` or a function with a null operand | null, except `nonnull_count`, `coalesce` and `json_length` as their rules say |
| `and` | false when either side is false, else null when either is null, else true |
| `or` | true when either side is true, else null when either is null, else false |
| `not`, `implies` | `not null` is null; `a implies b` is `not a or b`, so `null implies true` is true |
| `is null`, `is distinct from` | never null |
| `x in (…)` with `x` null | null; lists hold no null, so `not in` never meets SQL's null trap |
| `case` | a branch is taken only when its condition is true; no branch and no `else` is null |
| Division by zero | null, in both targets |
| The result at a check site | true or null passes, false fails, exactly as a CHECK |
| The result at a guard site or rule condition | only true passes |
| The result at a value site | the value, null included |
| JSON | JSON `null` inside a document is a value (`json_type` says `'null'`); a missing document is null |

So `issuedOn <= dueOn` with `dueOn` null passes as a check and fails as a guard. Whether `dueOn` must be given is its
attribute's `required`, never the expression's concern.

#### Lowering

**PostgreSQL.** `Expressions/ExprSql.cs` lowers a typed tree to resolved query nodes (`RQueryExpression`,
`RQueryPredicate`), the query tree as a target. `Rendering/QuerySql.cs` gains a row mode that writes column names
unqualified, as a CHECK needs, and `COLLATE "C"` after each string operand of a comparison or `in`. It also gains four
internal node kinds the query schema does not have: `match`, `distinct`, `typed` (a literal with its type) and
`pred`, a predicate where a value goes, written in parentheses: a comparison inside `coalesce`, `case` or `=`, a
derived `bool` value, and the guard's `COALESCE((…), false)`. The other way needs no node: a `bool` value used as a
condition, such as a `bool` column under `and`, lowers to the comparison `x = true`, which has the same three values.
`query.json` does not change. Columns come from the site: at a table, the table's resolved columns; for an entity
check, the listing binding's field map onto its write table; for a navigation (guard sites, on request only), a
scalar subquery through the relation's binding, `(SELECT credit_limit FROM customers WHERE id = customer_id)`. A
fixed-length string column (`fixedLength`, `schemas/v1/common.json:628-632`, or a `nativeType` of `char(n)`) reads as
`rtrim(col)`, so its padding never reaches a comparison, `length` or `matches`. The result is wrapped by the site: a
check as is, a guard in `COALESCE((…), false)`. Any other dialect returns no text and MQ4056.

**Enum members and reference-type codes.** A `ref` lowers to a literal in the form its other operand holds, which the
typer knows from the path's attribute or column:

- In PostgreSQL, and in JavaScript at a table site, the column's stored form, decided by its physical type. A column
  that holds enum E (projected from an E attribute, carrying `typeRef` E per scope 6.4, or mapped to an E attribute by
  the binding an entity check lowers through) holds a member's `value`, or its position from 0 when it has none, in an
  integer column, and its `code`, or its name when it has none, in a text column (`schemas/v1/enum.json:129-135`,
  `Resolution/DatabaseRun.Columns.cs:144-171`). A column typed by reference type R holds the code in R's code type:
  text, an integer or a uuid. So `Status.Paid` lowers to `2` in one table and `'PAID' COLLATE "C"` in another.
- In JavaScript at entity, value and guard sites, the form the API carries: a member's `code`, or its name when it has
  none, as the C# example pack writes it (`packs/csharp-dapper/enum.scriban:25`), and a reference type's code.

A table column with no `typeRef` and no attribute behind it has its plain type, so a `ref` cannot meet it (MQ3103).

**JavaScript.** `Expressions/ExprJs.cs` lowers a typed tree to one arrow function,
`(row) => mq.le(mq.date(row.issuedOn), mq.date(row.dueOn))`, with values read by current name and wrapped by type
(`mq.dec`, `mq.date`, `mq.ts`, `mq.json`). A check site wraps the result in `mq.passes` (not false), a guard site in
`mq.holds` (exactly true). The runtime, `Expressions/expr-runtime.js`, is plain modern JavaScript with no dependency
and no locale or `Date` arithmetic; it holds integers and decimals as scaled `BigInt`s, compares strings by code point
and strips the trailing spaces of a fixed-length column's value, as `rtrim` does. Numbers cross into it as exact
text. Today the sandbox turns every JSON number into an `int64` or a `double` (`Scripting/ScriptValues.cs:381`), so an
integer beyond 2^53 or a decimal of 16 or more digits loses digits. For a tree expression the engine passes the row,
the context and the payload with each number as its JSON text, which `mq.dec` reads; JavaScript guards and actions
still receive numbers, and a value a JavaScript action computed is a double already. A pack's generated code must pass
numbers as text too.

**Where lowering runs.** A tree check's SQL is lowered during resolution, for the database that holds the table, into
`RCheck.Expression`, so the sql-ddl pack prints it unchanged. JavaScript is lowered on first read and cached on the
resolved object. Process guards compile into the process's prepared script, loaded after the runtime, which is
prepared once for the engine and shared by every sandbox that needs it, so their cache, limits and sessions are the
ones expressions have today. A process with a condition runs under statement limits only, so a replay never depends
on the machine's load. Declared rules that use `expression` share one sandbox pool per rule-file hash; a condition
that cannot answer is MQ5002 or MQ5003 for its rule, not a finding. Nothing else lowers.

#### Use sites

| Site | Paths may name | Mode | `now` | What stays |
| --- | --- | --- | --- | --- |
| Table and column check | The table's columns: own, inherited and contributed; on an overlay of an entity's table, also the entity's attributes and value object members, by attribute name | check | no | SQL text per dialect |
| Entity check | The entity's attributes, inherited ones and value object members | check | no | `validation` facets and JavaScript rules |
| Derived value | The entity's other attributes | value | no | `derived.expression` text |
| Process guard | Context attributes; payload attributes of every event whose transitions use the guard | guard | yes | `expression` JavaScript |
| Operation guard | Input members; the subject's attributes and to-one navigations, for a record-level operation | guard | yes | Nothing before; new |
| Rule condition | Members of the selected object | guard | no | The closed conditions |
| Constraint | Its parameters | the citing site's | the citing site's | New |

- **Table and column checks.** A check holds `expression` or `condition`. A column check's condition must read its
  `column` (MQ3114). Stereotype checks use the same definition, so contributed checks name contributed columns by
  key and are inherited by id as today. Resolution lowers the tree into `RCheck.Expression` and keeps the typed tree
  in `RCheck.Condition`; the snapshot records `shape`, the hash of the lowered check with column keys in place of
  column names, so a column rename keeps the check while a change to a cited constraint, a binding's field map or an
  enum's stored value changes it. The migration classifier already compares the resolved
  database before and after a write, so a changed tree check records its fact like a text check. An overlay of an
  entity's table names the entity's attributes and value object members by attribute name, as an entity check does,
  under the column keys the resolver gives their columns (`attributeId`, `attributeId.memberId`): the naming
  conventions decide the physical names only at resolution, so validation cannot know them, and a convention change
  then never touches the model. An override entry reads as its attribute; foreign key columns are not attributes, so
  only an entry that names one reads it. Resolution lowers the check through the resolved table's columns, and a path
  to an attribute the projection does not store is MQ4008 at the check, which is left out.
- **Entity checks (attribute and entity validations).** `checks` on the entity hold cross-attribute rules and
  single-attribute rules alike; `attribute` is where a failure points, `/issuedOn` in an API error. A binding's `checks`
  lowers each listed check through its field map into an `RCheck` on its write table, named by the naming convention
  from the check's name, with `MemberOrigin.Source` `binding`. A listed check whose paths the binding does not map to
  its write table, or maps to a fixed-length column, or a binding without a write table, is MQ3110: the check's
  JavaScript reads the API's value, which carries no padding, so a padded column could answer otherwise. MQ3115 warns
  on a check that no binding with a write table lists while one of them maps every attribute it reads, so a check added
  to the entity is not left out of the table silently; `validation.rules` sets its severity, `error` for a team that
  wants every such check enforced and `off` for one that checks in the API only. Inherited entity checks belong to
  derived entities by id. `min`, `max`, `pattern`, `allowedValues` and `rules` stay as they are.
- **Derived values.** `derived.value` is typed against its attribute and lowered to JavaScript on read; packs that
  compute in SQL lower it through a binding with `expression_sql`. `stored` keeps its meaning. A derived value never
  becomes a generated column in this design.
- **Process guards.** `condition` replaces the JavaScript of a guard a person rewrites; `expression` stays for the rest,
  and a guard with neither stays a stub. A payload path must exist on every event whose transitions use the guard
  (MQ3102). Simulation, scenario replay and `verify_scenarios` evaluate through the same interpreter path; `now` is the
  simulation's clock, so replays stay deterministic. MQ9504 cannot occur for a tree guard. When scope 4.1 lands,
  `subject` paths join the scope; when shared guards (phase 2, section 4.4) land, the typer checks a library guard
  against each using process's context, which that design left to this item.
- **Operation guards.** Checked in order; the first that fails reports its `error` (MQ3111 when the error is not the
  operation's, or a guard reads the subject of an operation without one). The engine runs no operation, so it types
  and lowers them and packs render them.
- **Declared rule conditions.** `expression` reads `member` paths, typed from the resolved record of the selected kind
  and, for `properties.x`, from the property schemas. Objects and lists are not values; `count` and `has` stay for them.
- **Projections (scope 3.12), not part of this design.** A projection's computed field will be a value site whose paths
  may cross to-one navigations, lowered to SQL through the projection's source binding by the scalar subquery above,
  and read in templates as `field.value`. Query conditions in the core can follow through the same row writer.

**Converting a JavaScript guard by hand.** Nothing converts the guards models hold today (decision 19); the guide
gives one step. Write the condition as text with `preview_expression` against the process at the guard site, try it
with the values the process's scenarios use and compare its answers with the JavaScript guard's in
`simulate_process`; then save `condition` and remove `expression` in one write, and run `verify_scenarios`. Where a
value may be null, ordering and arithmetic differ: JavaScript reads null as 0, the core answers unknown. `!` on a
nullable boolean and loose `==` differ from JavaScript too (`!null` is true there and unknown here, and `==` converts
types where the core refuses mixed ones), so a hand conversion must check them.

#### Rules

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ3101 | E | An expression does not parse, or a node is malformed: an unknown operator or function, a wrong number of operands. | Fix the text or the node |
| MQ3102 | E | A path names what its site cannot read: an unknown id, a member of another entity or table, a navigation at a check or value site, a to-many navigation, a collection, a `member` outside a rule condition, a payload attribute some using event lacks, or a context attribute named `event`, under which a guard reads the payload. | Name a member of the site |
| MQ3103 | E | The types do not fit: operands of different types, ordering or arithmetic on a type without it, a date with a timestamp, `+` on a date, `date_part` of `hour` or `minute` on a date, a site result that is not bool, a derived value of another type, a type the core does not have (`float` among them), a `ref` against a value of no enum or reference type, or a code the column it is compared with cannot store. | Use `date_of`, `add_days` or a member of the right type; keep SQL text for `float` |
| MQ3104 | E | `now` at a check site, a value site or a rule condition, directly or through a constraint. | Move the rule to a guard |
| MQ3105 | E | A regular expression outside the portable subset. | Rewrite it in the subset |
| MQ3106 | E | An argument that must be a literal is not: a pattern, a `date_part` part, `round` digits, an `in` list item; or an `in` list holds null. | Write a literal |
| MQ3107 | E | A site holds both its raw form and a tree, or neither; a process guard is reported only for both, since a guard with neither is a stub. | Keep one |
| MQ3108 | W | A file holds an expression as text; renames will not follow it. | `text-to-trees` |
| MQ3109 | E | A `satisfies` names no constraint, misses or adds a parameter, binds one to another type, names a constraint whose condition is not a boolean that types, or constraints cite each other in a cycle. | Bind every parameter |
| MQ3110 | E | A binding lists a check that is neither its entity's nor a base's, or one that reads an attribute the binding does not map to its write table or maps to a fixed-length column or to a column that cannot store a code it names, or it has no write table. | Map the attribute to a varying column, or remove the check from the binding |
| MQ3111 | E | An operation guard's `error` is not an error of the operation, or it reads the subject of an operation without a record subject. | Name an own or inherited error |
| MQ3113 | E | An expression has more than 512 nodes or nests deeper than 64. | Split it into constraints |
| MQ3114 | W | A column check does not read its column, or an entity check does not read its `attribute`. | Point at a member it reads |
| MQ3115 | W | An entity check is listed by no binding with a write table, though one of them maps every attribute it reads, so no database enforces it. `validation.rules` sets its severity. | List it in the binding's `checks` |
| MQ4056 | W | Changed text: also a check written in the expression language on a database whose dialect is not PostgreSQL. | |

MQ3112 is left unused: nothing converts guards (decision 19, question 4). MQ3116 to MQ3120 are free. Expression rules
run where the element's other rules run: on a save, scoped and whole-model validation. A declared rule's expression is
checked with MQ5016's static pass.

#### Public API

```csharp
namespace Maquettiste.Engine.Model;
// Members are init properties; the comments list them.
public sealed record Expr { /* Value, Type, Null, Path, Ref, Member, Now, Op, Call, Args, Case, Else, Satisfies,
                               With (a list of ExprArgument), Text */ }
public sealed record ExprWhen { /* required When, required Then */ }
public sealed record ExprArgument { /* required Parameter (a keyed id), required Value */ }
public sealed record EntityCheck : ElementBase { /* Attribute, required Condition, Message */ }
public sealed record OperationGuard : ElementBase { /* required Condition, Error */ }
public sealed record Constraint : Element, IPackaged { /* Package, Parameters, required Condition, Message */ }
public sealed record ConstraintParameter : ElementBase { /* required Type (a TypeRef) */ }
// CheckConstraint.Condition, DerivedSpec.Value, ProcessGuard.Condition, Entity.Checks, EntityBinding.Checks,
// Operation.Guards and RuleCondition.Expression are added; ElementKind.Constraint and KindInfo.ConstraintsFolder.

namespace Maquettiste.Engine.Expressions;
public enum ExprSiteKind { Check, Value, Guard, RuleCondition }
public sealed record ExprType(string Kind, string? Of, int Bits = 0); // "decimal"; "enum" with the enum id; "int"
                                                                     // with Bits 16, 32 or 64
public sealed class ExprScope { /* built by the engine per site: names, ids, types, mode, whether now is allowed */ }
public sealed record TypedExpr(Expr Node, string Pointer, ExprType Type, bool Nullable, IReadOnlyList<TypedExpr> Args);
public static class ExprText
{
    public static ExprParse Parse(string text, ExprScope scope);   // the tree, or MQ3101 with a position
    public static string Print(Expr e, ExprScope scope);
}
public static class ExprTyper
{
    public static TypedExpr? Type(Expr e, ExprScope scope, ICollection<Diagnostic> problems);
}
public static class ExprLowering
{
    public static string? ToPostgreSql(TypedExpr e, ExprColumns columns, ExprSiteKind site,
        ICollection<Diagnostic> problems);
    public static string ToJavaScript(TypedExpr e, ExprSiteKind site);
}
public static class ExprRuntime { public static string Source { get; } }

namespace Maquettiste.Engine;
public sealed partial class ModelStore
{
    public Task<ExpressionPreview> PreviewExpressionAsync(ExpressionPreviewRequest request, CancellationToken ct);
}
```

Resolved: `RExpression` (`Node`, `Type`, `Nullable`, `Args`, `Target`, the `RAttribute`, `RColumn` or member a path
ends at, `Value` and `ValueText`, a literal's exact text, `Text`, and a lazy `JavaScript`); `RCheck.Condition`;
`RMemberOrigin.Source` value `binding`; `REntity.Checks` (`REntityCheck`: `Attribute`, `Condition`, `Message`,
`Bindings`); `RDerived.Value`; `RGuard.Condition`; `ROperation.Guards` (`ROperationGuard`: `Condition`, `Error`);
`ResolvedModel.Constraints` (`RConstraint`). API records in `openapi.yaml`: `ExpressionRecord` (`text`, `type`,
`nullable`, `tree`), `checks` on `EntityRecord`, `condition` on the check and guard records, `guards` on
`OperationRecord`, and `ConstraintRecord`.

#### CLI and MCP

- `maquettiste expression preview <element> --at <pointer> [--database <id>] [--values <json>]` and
  `maquettiste expression preview --text "<text>" --on <element> --site <site>` print the text, type, nullability,
  JavaScript, PostgreSQL text (or why there is none) and, with `--values`, the answer under the site's mode.
- MCP: `preview_expression` (new; the tool pins move by +1), with the same arguments as the preview verb, answering
  `{ preview: { text, type, nullable, javascript, sql, answer }, diagnostics }`. `text-to-trees` is an `apply_batch`
  operation, which `maquettiste model apply` also runs, so its dry run is `apply_batch` with `dryRun`. The texts of
  `save_element`, `create_element` and `apply_batch` say that `{ "text": … }` is accepted and stored as a tree.
  `docs/mcp.md` gains the row; the Functions host gains `POST /api/model/expressions/preview` (`previewExpression`).
- Editor follow-ups: a text box showing the printed form and the type in the inspectors of checks, guards and derived
  values. Until then the generic JSON form shows the tree and accepts a `text` node.

#### Templates

Packs read the typed tree: `entity.checks` (`name`, `attribute`, `message`, `condition`), `check.condition` on table
checks while `check.expression` stays the SQL, `attribute.derived.value`, `guard.condition`, `operation.guards`
(`condition`, `error`) and `model.constraints`. A condition exposes `node`, `op`, `call`, `args`, `value`, `value_text`
(`valueText` in scripts: a literal's exact text, since a script holds `value`'s 64-bit integer as a double), `target`,
`type` (with `bits` for an `int`), `nullable`, `text` and `javascript`. Two template functions:
`expression_sql(expr, binding)`, the PostgreSQL text through a binding, and `expression_runtime`, the runtime's text
for packs that write JavaScript. Units that read none of these keep their incremental keys.

- **sql-ddl** changes nothing: tree checks arrive as SQL in `check.expression`.
- **The C# example pack** gains `expr_cs(condition, spec)` in `helpers.js`, an example renderer from the typed tree to
  C# with lifted nulls and `decimal`. The entity unit gains `Validate()`, which returns one error per failed check with
  the JSON pointer of its `attribute` and the check's name as its code; the process units render `guard.condition`
  with it and keep `cs_expression` for JavaScript guards. The pack README calls the renderer a starting point to copy.

#### Docs

User guide: a section "Expressions" (the text form, the sites, the null table, the function table, the portable rules,
named constraints) with "The rules for expressions (MQ31xx)"; a step in "Binding entities to tables, views and queries"
for `checks` on a binding; a step in "Processes, actors and scenarios" for converting a guard by hand with
`preview_expression`, as "Converting a JavaScript guard by hand" above says, including the warning that `!` on a
nullable boolean and loose `==` differ from JavaScript; a line in "Operations" for guards.
`docs/guides/expressions.md` (new): write a check once, see it in the DDL and the API.
`docs/mcp.md`, `skills/maquettiste-modeling/SKILL.md`, the C# example pack's README, and `docs/releases.md` under the
next unreleased version.

**Not doing**

- Emitters for C#, TypeScript or any other language in the engine; packs render from the tree.
- Dialects other than PostgreSQL, until a team needs one (MQ4056).
- Actions as expressions: they become operations in phase 4 (scope 4.6).
- Generated columns from derived values or tree-form computed columns; `computed` stays SQL text.
- Checks on value objects or scalar types; write them on the entity with `amount.value`.
- Entity checks through legacy `mapping` files; bind the entity instead.
- Paths through to-many navigations, aggregates or subqueries; projections and queries come later.
- `lower`, `upper`, case-insensitive matching and collations, which depend on the locale.
- Rewriting `min`, `max`, `pattern` and `allowedValues` as trees.
- Calculations and decision tables, which the scope places after the core.
- `float` in the core (decision 8); its checks stay SQL text.
- Converting JavaScript guards for a team; a person rewrites each one (decision 19).
- A second evaluator in C#; the engine runs the lowered JavaScript.
- Time zones other than UTC.
- Running a project's own expressions against a live database outside the tests; `db check` is later (scope 6.5).

**Tests**

- `SchemaConsistencyTests` (existing);
  `CanonicalJsonTests.Absent_checks_guards_and_conditions_keep_files_byte_identical`; `ExprModelTests` (new): every
  node shape round-trips; `Expr` ids are index references.
- `ExprParserTests`, `ExprPrinterTests` (new): each operator, literal and precedence level; print of parse is stable;
  renaming an attribute changes the printed text and not the file.
- `ExprTyperTests` (new): each MQ3103 case, `float` and `hour` of a date among them; a `datetime` against `now()`;
  integer widths; nullability through optional members and navigations; constraints and their cycles; size limits.
  `PortableRegexTests` (new): the subset and each refused form.
- `ExprRuntimeTests` (new): Kleene tables; decimal exactness (`0.1 + 0.2 = 0.3`); division by zero; the division
  scale rule, `1 / 3`, `1 / 1000000` and `2 / 10673`; `add_months` on 31 January and 29 February; `trim` of a tab;
  `length` and ordering of text beyond the basic plane; the trailing spaces of a fixed-length value; regex dot and
  newline; an `int64` above 2^53 and a 20-digit decimal in a context reach `mq.dec` with every digit.
  `ExprJsLoweringTests`, `ExprSqlTests` (new): fixed lowered text per operator and function; `pred` and `x = true`;
  `COLLATE "C"` on `=` and `in`; `ref` to an integer and a text column.
- `ExpressionCorpusTests` (new, needs PostgreSQL): `tests/fixtures/expressions/corpus.json` holds the scope's shapes,
  value sets, optional value sets, ranges, formats, reserved values, not blank, lengths, exactly one of, both or
  neither, a state that needs a field, a state if and only if a field, the order of two columns, not itself,
  arithmetic with a tolerance (written with `between`), date windows, JSON shape, guards over an event and through a
  relation, and the portability cases above. It also holds an enum stored as an integer and as text and a
  reference-type code, each compared with a `ref` and in an `in` list; a fixed-length column with padded values; the
  division `2 / 10673` at the `…4999…` boundary; and a `datetime` against a `datetimeoffset`. Each case has typed
  columns, rows with nulls and expected answers per mode. The test sets the session's time zone away from UTC, creates
  each table, evaluates the lowered SQL per row in PostgreSQL and the lowered JavaScript in the sandbox, and requires
  both to equal the expected answers.
- `ExpressionRuleTests` (new): MQ3101 to MQ3115 but MQ3112, one test each, MQ3107 silent for a stub guard, and MQ4056
  for a tree check on a `sqlserver` database.
  `ExpressionWriteTests` (new): text is stored as a tree on every write surface; bad text refuses the save;
  `text-to-trees`; delete refusal and `delete-dependents` remove a check that names a deleted column.
- `ExpressionResolutionTests` (new): tree table, column, stereotype and binding checks resolve to SQL; a binding that
  lists nothing adds nothing; constraints inline. `SchemaDifferTests.A_column_rename_keeps_a_tree_check`.
- `ProcessConditionTests` (new): a tree guard with a null operand fails; decimals compare exactly; the cache re-keys
  on a rename.
- `OperationGuardTests`, `DeclaredRuleExpressionTests` (new).
- `ExpressionCostTests.A_model_without_expressions_loads_no_runtime_and_adds_no_keys` (new), and the golden tests
  unchanged for every fixture without expressions.
- `McpSurfaceTests` (+1 tool), `McpReadTests.Preview_expression_reads_one_element`, `ExpressionCommandTests` (CLI).
- **Acceptance (3.8):** `Integration/ExpressionAcceptanceTests.Issued_on_before_due_on_is_written_once` (new). Setup:
  an `E2ERepo` billing copy binds `Invoice` to its `invoices` table with a binding, adds an optional `dueOn` date, its
  `due_on` column and field, the check `dueAfterIssue` (`issuedOn <= dueOn`, `attribute` issuedOn) listed by the
  binding, and an invented lifecycle `InvoiceReview` whose context has `issuedOn` and `dueOn` and whose guard is the
  same text. Expected: sql-ddl's `invoices.sql` holds the CHECK `issued_on <= due_on`; the C# example pack's
  `Validate()` reports `/issuedOn`; with `dueOn` null, PostgreSQL accepts the row (needs PostgreSQL), the lowered check
  passes and `simulate_process` refuses the guarded transition; renaming `dueOn` to `dueDate` leaves the CHECK and the
  snapshot shape unchanged and changes the printed text.

**Tasks**

- **H1. Schemas and records.** Runs first, from `main`. Owns: `common.json`, `entity.json`,
  `process.json`, `operation.json`, `declared-rules.json`, `snapshot.json`, `batch.json`, `constraint.json` (new) and
  their copies; `Model/Expressions.cs` (new), `Model/Constraints.cs` (new), `Model/Conceptual.cs`, `Model/Physical.cs`,
  `Model/Bindings.cs`, `Model/Processes.cs`, `Model/Operations.cs`, `Model/DeclaredRules.cs`,
  `Model/PhysicalSnapshot.cs`, `Model/ElementKind.cs`, `Model/KindInfo.cs`, `Model/ModelIndexer.cs`,
  `Loading/NameIndex.cs` (name forms of entity checks, operation guards and constraint parameters). Tests:
  `SchemaConsistencyTests`, `CanonicalJsonTests`, `ExprModelTests`. Verify:
  `E "~Json.|~ExprModelTests|~BatchNamesTests"`; `P "~GoldenTests"`; `copies`.
- **H2. The core: text, tree and types.** Runs after H1. Owns: `Expressions/ExprScope.cs`, `ExprType.cs`,
  `ExprText.cs` (parser and printer), `ExprTyper.cs`, `ExprFunctions.cs` (the function and operator tables with both
  lowerings as data), `PortableRegex.cs`, all new. Tests: `ExprParserTests`, `ExprPrinterTests`, `ExprTyperTests`,
  `PortableRegexTests`. Verify: `E "~Expressions."`.
- **H3. The JavaScript runtime and lowering.** Runs after H2. Owns: `Expressions/expr-runtime.js`, `ExprJs.cs`,
  `ExprEvaluator.cs` (new), the embedded resource line in `Maquettiste.Engine.csproj`; `Scripting/ScriptValues.cs`
  (numbers as exact text for tree expressions) and the context and row serialization in
  `Processes/ProcessExpressions.cs` (`ToJson` and `Write`, `:262-326`), so decimals and `int64` values reach `mq.dec`
  as exact text. Tests: `ExprRuntimeTests`, `ExprJsLoweringTests`. Verify: `E "~Expressions.|~Scripting.|~Processes."`.
- **H4. The PostgreSQL lowering.** Runs after H2. Owns: `Expressions/ExprSql.cs` (new), `Rendering/QuerySql.cs` (row
  mode with `COLLATE "C"`, the four internal nodes), `Resolution/ResolvedQueries.cs` (their node names). Tests:
  `ExprSqlTests`. Verify: `E "~Expressions.|~QueryTests|~QueryRulesTests"`; `P "~GoldenTests"` (unchanged).
- **H5. The differential corpus.** Runs after H3 and H4. Owns: `tests/fixtures/expressions/corpus.json`,
  `tests/Maquettiste.Engine.Tests/Expressions/ExpressionCorpusTests.cs` (new). Tests: the corpus. Verify:
  `PG E "~ExpressionCorpusTests"`; without PostgreSQL the test reports itself skipped.
- **H6. Rules, references and writes.** Runs after H2. Owns: `Validation/ExpressionRules.cs` (new),
  `Validation/ModelValidator.cs` (the hook), `Validation/DeclaredRuleChecks.cs` (expressions in MQ5016's pass),
  `RuleCatalog.cs` (MQ3101 to MQ3115 but MQ3112, MQ4056's text), `CatalogTests.cs` (+14),
  `ModelStore.Expressions.cs` (new: text to tree on writes, `text-to-trees`), `ModelStore.cs` and
  `Loading/BatchParser.cs` (the `text-to-trees` operation), `DeletePlan.cs` (dependents). Tests: `ExpressionRuleTests`,
  `ExpressionWriteTests`. Verify:
  `E "~CatalogTests|~ExpressionRuleTests|~ExpressionWriteTests|~ValidationFixtureTests"`;
  `E "~ModelStoreDeleteTests|~DeleteCascadeTests|~ModelStoreBatchTests|~BatchPreviewTests"`; `record`.
- **H7. Checks, entity checks, derived values and constraints in the resolved model.** Runs after H3, H4 and H6.
  Owns: `Resolution/DatabaseRun.cs`, `DatabaseRun.Members.cs`, `DatabaseRun.Bindings.cs`, `ResolvedPhysical.cs`,
  `ResolvedConceptual.cs`, `ResolveRun.Conceptual.cs`, `ResolvedCore.cs`, `ResolvedExpressions.cs` (new),
  `ResolvedCoverageTests`. Tests: `ExpressionResolutionTests`, `ExpressionCostTests`. Verify:
  `E "~ExpressionResolutionTests|~ResolvedCoverage|~Migrations.|~ExpressionCostTests"`; `P "~GoldenTests"`.
- **H8. Process guards.** Runs after H3, H7 and H14 (for `ExpressionRecord`). Owns: `Processes/ProcessExpressions.cs`
  (but its value serialization, which H3 owns), `Processes/StatechartInterpreter.cs`, `Resolution/ResolvedProcesses.cs`
  (`RGuard.Condition`), the guard record in `Editor/ResolvedRecords.cs` and `openapi.yaml`. Tests:
  `ProcessConditionTests`. Verify:
  `E "~Processes.|~ProcessConditionTests|~ScenarioReplayTests|~InterpreterConformanceTests"`;
  `C "~ProcessCommandTests"`; `F "~ContractTests"`; `editor`.
- **H9** is left free: guard conversion is not built (decision 19).
- **H10. Operation guards and rule conditions.** Runs after H3, H7 and H14 (for `ExpressionRecord`). Owns:
  `Resolution/ResolveRun.Operations.cs`, `Resolution/ResolvedOperations.cs`, `Validation/OperationRules.cs` (MQ3111),
  `Validation/DeclaredRuleEvaluator.cs`, the operation record in `Editor/ResolvedRecords.cs` and `openapi.yaml`. Tests:
  `OperationGuardTests`, `DeclaredRuleExpressionTests`. Verify: `E "~Operations.|~OperationGuardTests|~DeclaredRule"`;
  `F "~ContractTests"`; `editor`.
- **H11. CLI and MCP.** Runs after H8 and H10. Owns: `Agent/AgentTools.cs`, `Agent/AgentTools.Handlers.cs`,
  `ModelStore.ExpressionPreview.cs` (new), `src/Maquettiste.Cli/Commands/ExpressionCommand.cs` (new), its registration,
  `src/Maquettiste.Functions/_functions/ExpressionEndpoints.cs` (new), `openapi.yaml` (`previewExpression`),
  `docs/mcp.md`. Tests: `McpSurfaceTests` (+1), `McpReadTests`, `ExpressionCommandTests`. Verify:
  `C "~McpSurfaceTests|~McpReadTests|~McpWriteTests|~ExpressionCommandTests"`; `F "~ContractTests"`; `editor`.
- **H12. Templates and the C# example renderer.** Runs after H8 and H10. Owns: `Rendering/BuiltinHelpers.cs` (the two
  template functions), `Rendering/TrackingTemplateContext.cs` (the new members), `packs/csharp-dapper/helpers.js`,
  `entity.scriban`, `_process.scriban`, `process-handlers.scriban`, the pack README, its pack test cases, and the pack
  copies in the reference app. Tests: the pack's cases, `TemplateCacheAndHelperTests` for the two functions. Verify:
  `P "~GoldenTests|~CompileTests|~OperationPackTests"`; `E "~Rendering.|~PackTestTests|~GoldenTests"`; `copies`.
- **H13. Docs and acceptance.** Runs after H5, H11, H12 and H14. Owns: the user-guide section and rule rows,
  `docs/guides/expressions.md` (new), `skills/maquettiste-modeling/SKILL.md`, `docs/releases.md`,
  `Integration/ExpressionAcceptanceTests.cs` (new). Tests: the acceptance test. Verify:
  `PG E "~ExpressionAcceptanceTests"`; `C "~SkillRefreshTests|~AgentSetupTests"`, where the skill hash changes on
  purpose.
- **H14. Snapshots, the differ and the editor records.** Runs after H7. Owns: `SchemaDiff/SnapshotCapture.cs` (the
  check's `shape`), `SchemaDiff/SchemaDiffer.cs` (equal shapes are unchanged), and the check, entity check, derived
  value and constraint records in `Editor/ResolvedRecords.cs` and `openapi.yaml` (`ExpressionRecord`, `checks` on
  `EntityRecord`, `condition` on the check record, `ConstraintRecord`). Tests: `SchemaDifferTests`. Verify:
  `E "~SchemaDiff|~Migrations.|~ExpressionCostTests"`; `F "~ContractTests"`; `editor`.

H15 to H19 are free.

## 4. Questions for the owner, answered

The owner accepted the recommended answer to each question on 2026-11-03; the sections above already follow them.

1. **Entity checks and tables.** This draft writes an entity check into a table only when the binding lists it
   (decision 16). The other choice is every check whose paths the binding maps, with an opt-out.

   Accepted: keep the explicit list, which keeps mapping explicit and which materializing a table from an entity
   can fill, plus the drift warning MQ3115, so a check the list leaves out is reported.
2. **Reading trees in pull requests.** Files hold the tree, so a reviewer of a model change sees JSON, not
   `issuedOn <= dueOn`. Storing the text beside the tree would read better but would make every rename rewrite every
   file that prints the name.

   Accepted: store the tree only, with the model diff (scope 7.3) printing the text, and have `get_element` return
   `text` beside each tree, so agents never read ULIDs; on a save that sends both, whichever differs from the file
   wins.
3. **Time zone.** `date_of`, `date_part` and month arithmetic on offset timestamps use UTC, and a `datetime` compared
   with a `datetimeoffset` reads as UTC, so "today" at a guard is the UTC date. A project time zone setting is
   possible later. Is UTC acceptable for the active team?

   Accepted: use UTC, and say so in the pre-release note; a project time zone later is one switch.
4. **Existing guards.** This draft converts nothing: guards stay JavaScript until a person rewrites them with the
   guide's step (decision 19). The other choice is a batch operation that converts the C# example pack's subset, with
   an info rule (MQ3112, left unused) pointing at each guard it would convert.

   Accepted: no MQ3112 and no converter.
5. **Limits the review of H1 to H6 found.** Three differences between the two lowerings remain after the review:
   a regular expression with nested repetition (`^(a+)+$`) can take exponential time in JavaScript, where PostgreSQL
   stays fast and the engine's sandbox stops a match after 250 ms; a `double` that overflows is an error in
   PostgreSQL and infinity in JavaScript; a JSON column holding the JSON value `null` is SQL null in JavaScript, so
   `json_type` answers null where PostgreSQL answers `'null'`. The other choices were refusing nested repetition
   (MQ3105, which also refuses dotted-name patterns such as `^([a-z]+\.)*[a-z]+$`) or a linear-time matcher in the
   runtime.

   Accepted (2026-11-03): keep the subset and state the three limits in the expressions guide (H13). The C# example
   renderer (H12) matches with .NET's linear-time mode, so generated C# never backtracks.
