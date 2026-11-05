# Next capabilities, phase 2: design

**Status (2026-11-02): milestone D (section 3) is built, in 0.12.0; milestones E to G are not built.** Which of their
items come next waits on the owner's decision on the proposal in `next.md`. Phase 2 is the second phase of the roadmap
of 2026-10-30, which `next.md` lists with its item numbers ("scope 3.3" here is item 3.3 there): the model shrinks,
commands and a project's own concepts become elements, and requirements tell agents why before they edit. The
roadmap's later decisions of that day hold here, among them no dependency on version control, and PostgreSQL, the CLI
and MCP first. `next-phase-1-design.md` stays binding for what phase 1 built, and `engine-design.md`,
`phase2-design.md`, `phase-3-design.md` and `snapshots.md` stay binding for everything this file does not change;
`docs/api/openapi.yaml` and `schemas/v1/` remain the source of truth for their contracts. Public engine signatures
change only as written here. File, type, member, rule, verb and tool names here are binding. Where code and this file
disagree during the build, fix one of them in the same change.

## 1. Scope and decisions

| Scope item | What phase 2 builds | Section |
| --- | --- | --- |
| 3.3 | Stereotypes that contribute columns, keys, indexes and checks, inherited by id; redefinition by facet; schema and package default stereotypes; fold into stereotype | 3.1 |
| 6.4 | Columns typed by an enum, a reference type or a scalar type, next to their physical type; `sensitive` on columns | 3.2 |
| 3.5 | Bindings that record their provenance; a drift rule and re-sync; materialize types attributes by reference type | 3.3 |
| 6.6 | Seed foreign keys by natural key, stable derived ids, rows computed by a script | 3.4 |
| 6.7 | Rename, retire and merge reference-type codes and types, each recording a value map | 3.5 |
| 3.6 | The `operation` kind | 4.1 |
| 3.7 | The `custom` kind with project-declared types and members with ids | 4.2 |
| 3.10 | The `requirement` kind, `satisfies`, `verifies` and a coverage report | 4.3 |
| 4.2 | Process libraries of guards, actions and gate policies; properties and stereotypes on them | 4.4 |
| 5.8 | DDL as structured steps from the engine; sql-ddl moves onto them | 5.1 |
| 5.1 | Library packs: shared partials and helpers | 5.2 |
| 5.3 | Packs that read each other's exports | 5.3 |
| 8.2 | `maquettiste pack test` | 5.4 |
| 5.2 | Values calculated once by project scripts | 5.5 |
| 8.3 | Declarative rules and per-element suppressions | 5.6 |
| 7.2 | Impact before apply | 6.1 |
| 7.3 | A model diff between two models | 6.2 |
| 6.9 | Declared environments | 6.3 |

Milestones: D (section 3, shared structure and typed columns), E (section 4, operations and the project's own
concepts), F (section 5, packs, scripts and rules) and G (section 6, agents and environments). Section 7 gives the
order.

Decisions that hold across sections (each section lists its own under "Decisions taken here"):

1. **Every mechanism serves many teams.** Each section opens with a "Why every team" paragraph: the reason most teams
   that model with Maquettiste need the mechanism, in the product's own terms. A part that would serve one team only is
   cut and listed under "Not doing" in its section. The product reserves no project vocabulary; packs keep languages
   and conventions.
2. **Carried over from phase 1:** no version control (a baseline is a model snapshot or a folder); PostgreSQL first
   (other dialects report MQ4056); engine, CLI and MCP first, with the generic JSON form where an editor screen is
   missing and each deferred screen listed as a follow-up; no compatibility settings for installed bases that do not
   exist; acceptance tests copy the billing fixture into a temporary folder and leave the shared fixture alone.
3. **The ledger stays at version 1.** Fact rows hold a fact id and a content hash, whatever the fact's type, so new
   fact types (a value map in 3.5, the fold's `rekey` in 3.1) need no ledger change. Nothing in phase 2 changes the
   ledger DDL. A section that finds it needs a change says so, and the owner decides on version 2.
4. **Nothing in phase 2 reads a live database or the ledger.** Declared environments (6.3) are names and settings
   the model checks; reading an environment's database or its ledger, and `db plan --env`, come with the live planner
   (scope 6.1, phase 3).
5. **Previews never touch the whole model.** Dry runs, impact (6.1), calculated values (5.5) and scripted seed rows
   (3.4) are evaluated for the elements a preview shows or a change touches, planned through incremental keys. Only
   validate, plan and generate work on the whole model.
6. **Every link is an id.** Inherited members, operation members, custom members, requirement links, library members
   and pack exports are referenced by id. Name matching appears only in authoring actions (fold, map by name), which
   write ids.
7. **Words already taken.** "Fact" means a migration fact, "computed" a computed column, "properties" extension
   properties and "outputs" a pack's generated files. New names avoid them.
8. **Rule ids** come from the blocks in section 2.2. Ids left free in a block stay reserved for phase 2 follow-ups.
9. **Release notes** go under `## 0.12.0 (unreleased)` in `docs/releases.md`; the owner sets the version and date at
   release. A milestone may land as a pre-release when the owner chooses. Customers follow each change through its
   pull request into `main`, whose description uses billing and invented names only.

Decisions taken after the section reviews (2026-11-01), each recorded in its section:

10. **Renaming a stereotype's column** records one complete rename fact per carrying table, all in one write (3.1).
    Each fact names one physical object, so any migrator can apply facts one at a time, and the fact format stays as
    it is.
11. **Re-sync** renames an attribute only when its name followed the column's old name; `--keep-names` stops every
    rename, and the dry run lists the renames first (3.3).
12. **Typing an existing column by a reference type** records a type-change fact that asks how values that are not
    codes convert (`conversion`, MQ4101), and asks for a data check as well only when the column also narrows (3.2,
    decision 15). csharp-dapper keeps the code's plain type for such a column and names the reference type in the
    summary, as it does for attributes.
13. **Value maps come from the four refactors only** (3.5). A plain edit of a code or a type name warns (MQ4114), and
    `validate --against` reports it (MQ4112). The classifier keeps comparing physical shapes only.
14. **A stable id's namespace is the table id** (3.4). A shared namespace across table files waits until a team needs
    one.
15. **New names where the scope's words were taken:** an operation's `level`, `returns` and an error's `input` (4.1);
    custom kinds declared in `extensions/kinds/<type>.json` and a requirement's `citation` (4.2, 4.3). An operation
    keeps `requires`: no file holds it beside a stereotype's `requires`. The release notes list these names.
16. **Saving a process library** does not replay the scenarios of the processes that use it; the next whole-model
    validate or `verify_scenarios` does (4.4).
17. **Packs reserve** a `tests/` folder and the template names `exports` and `exported` (5.3, 5.4), with a release
    note and no setting. The repository's own golden tests stay in C# against the shared fixture; each shipped pack
    gains one small `pack test` case (5.4).
18. **Calculated values** (`element.calculated`), which project scripts compute once per element, are evaluated on
    every element of their kinds in validate, plan and generate, so a broken one is reported whatever reads it, and
    cached by element hash (5.5). Their scripts cannot use library packs in phase 2.
19. **Declared environments are opt-in** (6.3): while a project declares none, names stay free as today; once it
    declares one, every name used must be declared.
20. **Impact before apply lists the files of every unit the change touches** (6.1): a superset, found without
    rendering. Plan after apply gives the exact bytes.
21. **Order:** milestone D whole, then E, F and G (section 7).

## 2. Conventions, rules and surfaces

### 2.1 Tasks and verification

Each section ends with its tasks. Ids are unique across the design and each section owns a range: D1–D19 (3.1),
D20–D29 (3.2), D30–D39 (3.3), D40–D49 (3.4), D50–D59 (3.5), E1–E19 (4.1), E20–E39 (4.2), E40–E49 (4.3), E50–E59
(4.4), F1–F19 (5.1), F20–F29 (5.2), F30–F39 (5.3), F40–F49 (5.4), F50–F59 (5.5), F60–F69 (5.6), G1–G9 (6.1),
G10–G19 (6.2) and G20–G29 (6.3). Ids left free in a range are unused. A task names the files it owns, the tests it adds
and a Verify line. A file path without a project prefix is under `src/Maquettiste.Engine/`.

The Verify legend, the pinned counts and the MCP test classes are those of section 2.1 of `next-phase-1-design.md`.
Each milestone ends with a gate (section 7): the full build, every .NET suite, the pack tests against PostgreSQL and
the editor suite.

### 2.2 Rule id blocks

Each section takes its rule ids from its own blocks. None of these ids is in
`src/Maquettiste.Engine/Diagnostics/RuleCatalog.cs` today (checked on 2026-11-01).

| Section | Blocks |
| --- | --- |
| 3.1 | MQ2024–MQ2031, MQ4076–MQ4081 |
| 3.2 | MQ4082–MQ4086, MQ7014–MQ7016 |
| 3.3 | MQ4087–MQ4089 |
| 3.4 | MQ7124–MQ7130 |
| 3.5 | MQ7017–MQ7020, MQ4112–MQ4114 |
| 4.1 | MQ3023–MQ3032 |
| 4.2 | MQ1017–MQ1022, MQ3033–MQ3036 |
| 4.3 | MQ3037–MQ3040 |
| 4.4 | MQ9020–MQ9023 |
| 5.1 | MQ4090–MQ4093, MQ6040–MQ6041 |
| 5.2 | MQ6042–MQ6046 |
| 5.3 | MQ6047–MQ6050 |
| 5.4 | MQ6051–MQ6053 |
| 5.5 | MQ5010–MQ5013 |
| 5.6 | MQ5014–MQ5020 |
| 6.3 | MQ4094–MQ4095, MQ7131 |

### 2.3 Rules

New and changed rules, by id. None of the new ids is in `src/Maquettiste.Engine/Diagnostics/RuleCatalog.cs` today
(checked on 2026-11-01). The catalog grows by 80 rules, from 274 to 354: milestone D adds 32 (3.1: 12, 3.2: 5, 3.3: 3,
3.4: 5, 3.5: 7), E adds 20 (4.1: 9, 4.2: 4, 4.3: 4, 4.4: 3), F adds 26 (5.1: 3, 5.2: 5, 5.3: 4, 5.4: 3, 5.5: 4, 5.6: 7)
and G adds 2 (6.3). Twenty-three existing rules change text or behaviour. Every new id falls in a family that
`RuleCatalog.FamilyLabels` already names, so no label is added.

| Id | Severity | Rule | Section |
| --- | --- | --- | --- |
| MQ1017 | E | A custom kind file is invalid: its `type` differs from its file name, its `properties` do not compile as a JSON Schema, or its `required` names a property it does not declare. | 4.2 |
| MQ1018 | E | An `x-member` keyword is misplaced: not `true`, not on an array whose `items` is an object schema, on an item schema that declares `id`, or under an applicator the walk does not follow. | 4.2 |
| MQ1019 | E | A custom element's `type` names no custom kind file; its content is not checked. | 4.2 |
| MQ2009 | E | Changed text: "An x-ref keyword is malformed: an unknown kind or key, a `custom:<type>` whose type is not a key, of or sameDatabase where they do not fit, an of naming no element (under kind member, no custom element), not on a string, under an applicator the link walk does not follow, or beside a patternProperties pattern that needs backtracking (a lookaround, backreference, atomic group or conditional)." | 4.2 |
| MQ2011 | E | Changed text: "An x-ref value is out of scope: a member of another element, a row of another list, a member of a custom element other than the one of names, another database, a holder with no single database, or a stereotype default under of "$element" or sameDatabase." Changed behaviour: a bare member id of a column or key under `of: "$element"` on a table is out of scope; its keyed id is in scope. | 3.1, 4.2 |
| MQ2013 | E | Changed text: "An element carries two stereotypes of one group, own, implied or from a schema or package default." | 3.1 |
| MQ2020 | E | Changed behaviour: layering skips `satisfies` and `verifies` links, as it skips diagrams. | 4.3 |
| MQ2024 | E | A stereotype's members do not fit: members on a stereotype whose `appliesTo` leaves out `table`, a contributed column with `attribute` or `sequence`, or a contributed key, index or check with a `name`. | 3.1 |
| MQ2025 | E | A stereotype's key, index or check names a column the stereotype does not declare. | 3.1 |
| MQ2026 | E | A contributed foreign key's target is invalid: both or neither of `referencesTable` and `referencesParameter`; a `referencesParameter` that is not a required string parameter with `x-ref` kind `table` and `sameDatabase`; or `referencesColumns` beside `referencesParameter`. | 3.1 |
| MQ2027 | E | A `redefines` entry is invalid: it names a member the element does not inherit, names one twice, states no facet, or states a facet its member's kind does not take. | 3.1 |
| MQ2028 | E | An `excludes` entry names a member the element does not inherit or also redefines, or excludes a column that an inherited key, index or check the element keeps uses. | 3.1 |
| MQ2029 | E | `excludeStereotypes` names a stereotype the element lists in `stereotypes`, or one a listed stereotype implies; only defaults can be excluded. | 3.1 |
| MQ2030 | W | `excludeStereotypes` names a stereotype no schema or package default gives the element, so it excludes nothing. | 3.1 |
| MQ2031 | E | A `defaultStereotypes` list names two stereotypes of one group, or two that conflict. | 3.1 |
| MQ3017 | W | Changed behaviour: a column's own `secret` sensitivity also feeds the check on its literal default, as an attribute's does. | 3.2 |
| MQ3023 | W | `level` is `collection` on an operation without a `subject`; it is ignored. | 4.1 |
| MQ3024 | E | An operation member is not exactly one of a typed value (`type`), a record key (`keyOf`) or a file (`file`); `mediaTypes` sits on a member that is not a file; a record key or a file sets `default`, `precision`, `scale` or `validation`, or a record key sets `length`; or a member of a `returns` sets `default`. | 4.1 |
| MQ3025 | E | An outcome's `returns` is not exactly one of `members`, `entity` or `file`; `mediaTypes` sits beside no `file`; `list` sits beside `file`; `paging` is set without `list`; or `paging` and `maxPageSize` are not set together. | 4.1 |
| MQ3026 | E | Two errors of an operation, own and inherited counted, share a name or a code, or two stereotypes give it an error of one id. | 4.1 |
| MQ3027 | E | An error's `input`, or a redefinition's `input`, is not an input member of the operation that holds it; a stereotype's error has none. | 4.1 |
| MQ3028 | E | `defaultOutcome` names an outcome of another operation. | 4.1 |
| MQ3029 | E | A stereotype declares `errors` but does not apply to operations. | 4.1 |
| MQ3030 | W | A `read` operation fires a process event; an event changes state. | 4.1 |
| MQ3031 | E | `requires` names the operation itself, one of its own sub-elements, a diagram or a migration fact. | 4.1 |
| MQ3033 | E | A custom element's content fails its custom kind's schema: a required key missing, an undeclared key, a value of the wrong shape, or a member without an id. | 4.2 |
| MQ3037 | E | Two requirements share an external id. | 4.3 |
| MQ3038 | I | A requirement that is neither proposed nor retired is satisfied by nothing (runs only when `validation.rules` gives it a severity). | 4.3 |
| MQ3039 | I | A requirement that is neither proposed nor retired is verified by no scenario (runs only when `validation.rules` gives it a severity). | 4.3 |
| MQ3040 | E | A requirement import is refused: a row without an external id, an external id the file repeats or two requirements share, a new requirement without text, an unknown status, or a value of the wrong form. | 4.3 |
| MQ4003 | E | Changed behaviour: the resolver also reports a duplicate column name in a synthesized table, inherited columns included. | 3.1 |
| MQ4008 | E | Changed behaviour: inherited columns resolve by keyed id; a bare member id gets the keyed-id hint; an overlay naming a member its table skips is reported by the resolver. | 3.1 |
| MQ4056 | W | Changed text: also a seed cell that names a row whose referenced value the database fills, outside PostgreSQL (written as NULL with a TODO comment). | 3.4 |
| MQ4076 | E | A table gets two primary keys: an inherited one beside its own, its entity's key, or another stereotype's. | 3.1 |
| MQ4077 | E | A fold into a stereotype is refused: the stereotype does not apply to tables or has no members; a target is not a designed or imported table file, excludes the stereotype or is written by another operation of the batch; or a matched column differs in a type facet, in another facet without `redefine`, or carries annotations its member lacks, or the stereotype would add a member the table lacks without `excludeMissing`. | 3.1 |
| MQ4078 | W | A redefinition states a facet equal to the value the member inherits, so the table stops following the stereotype for that facet. | 3.1 |
| MQ4079 | W | A synthesized table inherits a NOT NULL column with no default, identity or generation, which rows written through its entity or relation cannot fill. | 3.1 |
| MQ4082 | E | A column's `typeRef` is misplaced: it names no enum, reference type or scalar type, it sits on an overlay entry of a projected column, or a reference type's binding fills the column with a field. | 3.2 |
| MQ4083 | E | A typed column's type or a facet it states cannot hold its type's values: a reference type's codes, an enum's stored values, or a scalar type's base and facets. | 3.2 |
| MQ4084 | E | A foreign key that includes a column typed by a reference type does not point where the type's rows are: the type has no binding in the column's database, or the key does not pair the column with the code column of the bound table. | 3.2 |
| MQ4085 | W | A column and an attribute it stores, or that a binding maps to it, state different sensitivities. | 3.2 |
| MQ4087 | W | A column a binding maps was renamed or retyped since its provenance was recorded, so the attribute may no longer follow it. | 3.3 |
| MQ4088 | W | A binding's provenance does not describe its source: the source is not a table file, or none of the columns it records belongs to the source. | 3.3 |
| MQ4089 | E | A map-by-name or resync-binding operation is refused: an entity with no binding to the database, a source it cannot read, columns for more than one entity or naming a column the source lacks, or nothing to map or re-sync. | 3.3 |
| MQ4090 | W | A table, column or view of a PostgreSQL database uses a feature that needs a later server version than the database's `version` names: a multirange column (14); NULLS NOT DISTINCT, a foreign key's onDeleteColumns, a view's securityInvoker (15); a temporal key (withoutOverlaps) or a foreign key's period (18). | 5.1 |
| MQ4094 | E | A SQL object names an environment that `environments` in `maquettiste.json` does not declare, once it declares at least one; with none declared, names are free and the rule does not run. | 6.3 |
| MQ4103 | W | Changed text: "An open fact the baseline lacks is stale: the model no longer shows its combined effect (a change made and undone, a pending change reverted or widened back), or a value map's codes were not codes of its type in the baseline model and its names did not change; migration fold removes it." | 3.5 |
| MQ4106 | E | Changed text: "A fact's fields do not fit its type: from equal to to, a conversion or data check with both forms or neither, released without held, a key or object listed twice, a values code listed twice or mapped to itself, an object kind the type does not take, or a value map that maps codes in no column or collection and changes no name, or whose to is also a from." Changed behaviour: a rekey entry may also be an index, unique, foreign-key or check, and every member entry names its table. | 3.1, 3.5 |
| MQ4112 | E | Since the baseline, a kept row's code changed, a former code was added, or a type's name or plural name changed, and no open value-map fact of a database holding the type records it. | 3.5 |
| MQ4113 | E | An open value-map fact maps away a code that is a live code of its reference type. | 3.5 |
| MQ4114 | W | A write changed a kept reference row's code, or a reference type's name or plural name, outside the refactor operations, so no value map was recorded. | 3.5 |
| MQ5002 | E | Changed text: "Validation rule or calculation script error." Also a calculation file that does not load and a resolved rule that throws. | 5.5 |
| MQ5003 | E | Changed text: "Validation rule or calculation script exceeded a sandbox limit." Also while a calculation file loads. | 5.5 |
| MQ5005 | W | Changed text: "A rule, rows or calculation script declares top-level let or var variables; it keeps no state between calls, so its results are never cached." 3.4 adds rows scripts, 5.5 calculation scripts. | 3.4, 5.5 |
| MQ5010 | E | A calculated value failed on an object: its function threw, broke a sandbox limit or returned a value that is not JSON. | 5.5 |
| MQ5011 | E | A calculated value does not fit its schema. | 5.5 |
| MQ5012 | E | A template, script or rule reads a calculated value that is not declared for the object's kind. | 5.5 |
| MQ5013 | E | Calculated values read each other in a cycle. | 5.5 |
| MQ5014 | E | A rule declaration file does not fit `declared-rules.json`. | 5.6 |
| MQ5015 | E | Two project rules share an id: a declared rule and another declared rule or a rule script. | 5.6 |
| MQ5016 | E | A declared rule names what does not exist or does not apply: a kind, a stereotype key, a package id, a path, a calculated value, a link kind, bounds or a message placeholder. | 5.6 |
| MQ5017 | W | A declared rule selects no object. | 5.6 |
| MQ5018 | E | A suppression names a rule that cannot be suppressed: MQ1xxx, MQ7012 or MQ5018 to MQ5020. | 5.6 |
| MQ5019 | W | A suppression names a rule nothing defines or a member the element does not hold, or repeats another entry. | 5.6 |
| MQ5020 | W | A suppression matched no finding in a whole-model validate. | 5.6 |
| MQ6040 | E | A template (`ddl_steps`) or a preview (`preview_ddl`) needs DDL steps for a database whose dialect has no step builder; only PostgreSQL has one in this version. The unit fails; the preview leaves the element out. | 5.1 |
| MQ6041 | W | A migration's DDL steps hold one that needs a decision the schema diff cannot make (a TODO line): a narrowing or a retype with no migration fact, a new NOT NULL column without a default, a change PostgreSQL cannot make in place (partitioning, a database type), a changed or dropped SQL object, or a fact whose step has no PostgreSQL text. | 5.1 |
| MQ6042 | E | A `uses` entry in `pack.json` or `maquettiste.json` names a pack that does not exist or did not load, or a list names one pack twice. | 5.2 |
| MQ6043 | E | A used pack's version does not satisfy the `uses` range, or the range does not parse. | 5.2 |
| MQ6044 | E | A pack's `uses` names the pack itself or forms a cycle. | 5.2 |
| MQ6045 | E | A used pack's helper namespace collides with a builtin, a template keyword or variable, or a root helper of a pack that sees it. | 5.2 |
| MQ6046 | W | A pack has no units, and no pack and no project `uses` lists it. | 5.2 |
| MQ6047 | E | A template reads the exports of a pack its pack does not see, or an export template reads its own pack's exports. | 5.3 |
| MQ6048 | E | Two units of one pack export one name for one element with different values. | 5.3 |
| MQ6049 | E | An export's `each` names a member that is not a list of items with ids, or two items share an id. | 5.3 |
| MQ6050 | E | A unit reads exports that failed to evaluate; it is not rendered. | 5.3 |
| MQ6051 | E | A pack test case's output differs from its expected tree: a file added, missing or different. | 5.4 |
| MQ6052 | E | A pack helper test fails: an assertion fails, the test throws or it hits a sandbox limit. | 5.4 |
| MQ6053 | E | A pack test is malformed: a case without a project or `expected/`, a case holding `templates/`, an invalid `case.json` or one naming a missing case, cases in a pack without units, a test file that does not load or registers anything but tests, or two tests with one name. | 5.4 |
| MQ7009 | E | Changed text: "A seed cell names a row that does not exist, or an end cell names a row its seed does not load in every environment the cell's row loads in." | 6.3 |
| MQ7014 | E | A value written into a column typed by an enum or a reference type is not one of its stored values or codes: a table seed cell, the column's literal default, or a binding constant. | 3.2 |
| MQ7017 | E | A former code is a live code of its type, is listed twice, or does not fit the type's code (type, length, pattern). | 3.5 |
| MQ7018 | E | A former code's `replacedBy` names a row that is not a row of its type. A missing row is MQ2001. | 3.5 |
| MQ7019 | E | A default, `allowedValues` entry or seed cell names a former code. It replaces MQ3019, MQ7009, MQ7011 and MQ7014 for that value. | 3.5 |
| MQ7020 | E | A reference-type refactor cannot apply (an unknown or scripted code, a code taken or former, mismatched types, a binding, a missing field, a second refactor of the type in the batch); the message gives the reason. | 3.5 |
| MQ7107 | E | Changed text: "A table seed targets a table that is not a table file of a database, or lists a column or foreign key the table does not have." | 3.4 |
| MQ7108 | E | Changed text: "A table seed lists an identity, computed, generated or stable id column, whose value the database or the engine gives." | 3.4 |
| MQ7109 | E | Changed text: "A table seed's cell does not fit its column (type, length, precision or scale, or NULL in a NOT NULL column without a default), or a foreign-key cell does not have the referenced row key's shape." | 3.4 |
| MQ7111 | E | Changed text: "A foreign key that a table seed or a reference type's binding fills, or a foreign-key cell's natural key, names no row of the referenced table's rows, or one they do not hold in every environment the row is loaded in." | 3.4 |
| MQ7116 | E | Changed text: "A seed's rows file or rows script is missing or cannot be read, or the seed has rows of its own beside it." | 3.4 |
| MQ7124 | E | `stableId` is set on a column that is not of type `uuid`, or whose value the database fills (identity, sequence, generated, computed). | 3.4 |
| MQ7125 | E | The row key of a table with a stable id column holds that column, or a value the database fills directly or through a foreign-key entry, so the stable id cannot be derived. | 3.4 |
| MQ7126 | E | A table seed's column entry names a foreign key that is not its target's, is listed beside one of that key's own columns, references a table that has no rows in the model, or the seed's row key holds part of the entry's columns. | 3.4 |
| MQ7127 | E | A seed's rows script fails: it does not load, registers anything but rows exactly once, throws, exceeds a sandbox limit, or returns something that is not an array of row objects. | 3.4 |
| MQ7128 | E | A scripted row has a key its seed's columns do not name, or the seed's target has no row key to derive row ids from. | 3.4 |
| MQ7131 | E | A seed names an environment that `environments` in `maquettiste.json` does not declare, once it declares at least one; with none declared, names are free and the rule does not run. | 6.3 |
| MQ7212 | E | Changed text: "x-translatable is not on a top-level string property of an extension file, of stereotype parameters, or of a custom kind's content or member items: nested deeper, on another type, not a boolean, or, in a custom kind, on a name that is not an identifier." | 4.2 |
| MQ9013 | W | Changed text: "A declared event, guard, action, invoke or library gate that nothing uses (an invoke is used by an invoke-done or invoke-error transition; a library member by any process); use it or remove it." | 4.4 |
| MQ9014 | E | Changed text: "A reference to a state, event, guard, action or invoke of another process, other than a library's guard or action; declare it in this process or move it to a library." | 4.4 |
| MQ9020 | E | A library process holds what only a running process has (states, an initial, transitions, context, events, a subject or a bound attribute, or an action that raises events), or a process that is not a library has no states or lists library gates. | 4.4 |
| MQ9021 | E | A transition sets both `gate` and `gatePolicy`, or its `gatePolicy` names a gate that no library holds. | 4.4 |
| MQ9022 | E | A process invoke or a scenario names a library process, which has no states to run. | 4.4 |
| MQ9103 | E | Changed text: "A gate or gate policy on a transition whose trigger is not event, or two gates on one source and event (a gate policy counts as a gate); move the gate to an event transition or keep one gate." | 4.4 |

Quoted texts are the new catalog strings; unquoted changed texts give what the old text gains. Rules that keep their
text while their reach grows to the new kinds (MQ1005, MQ2001's note, MQ2004, MQ2022, MQ3001) are listed in their
sections only.

MQ6013 keeps its text and reserves more names: `stable_id` (3.4), `operation` (4.1), `custom` and `custom_named`
(4.2), `requirement` (4.3), `ddl_steps` and `ddl_sql` (5.1), `exports` and `exported` (5.3) and `has_calculated` (5.5).
MQ6045 (5.2) checks a pack's helper namespace against the same names.

Ids left free in the blocks of section 2.2 stay reserved for phase 2 follow-ups: MQ4080 and MQ4081 (3.1); MQ4086,
MQ7015 and MQ7016 (3.2); MQ7129 and MQ7130 (3.4); MQ3032 (4.1); MQ1020 to MQ1022 and MQ3034 to MQ3036 (4.2); MQ9023
(4.4); MQ4091 to MQ4093 (5.1); MQ4095 (6.3).

### 2.4 Schema files

Every file created or changed under `schemas/v1/` gets a byte-identical copy under
`samples/reference-app/.maquettiste/.schema/v1/` in the same task, and `copies` checks it. `docs/api/openapi.yaml`
references the kind schemas, `batch.json` and `maquettiste.json`, so a task that changes one of them, or the contract
itself, runs `editor`, which regenerates the editor's types and mocks. The contract, or a schema it references,
changes in every section except 5.1 and 6.1; each section names what it touches.

Phase 2 adds three kind schemas (`operation.json`, `custom.json`, `requirement.json`), so there are 28, and three
document schemas (`custom-kind.json`, `pack-case.json`, `declared-rules.json`).

| File | Change | Section |
| --- | --- | --- |
| `operation.json` | New kind: `subject`, `level`, `effect`, `input`, `outcomes`, `defaultOutcome`, `errors`, `redefines`, `excludes`, `fires`, `requires`, `excludeStereotypes`; `satisfies` on the operation, its members, outcomes and errors; `suppressions` | 4.1, 4.3, 5.6 |
| `custom.json` | New kind: `type` and `content`; `satisfies`; `suppressions` | 4.2, 4.3, 5.6 |
| `requirement.json` | New kind: `externalId`, `text`, `citation`, `package`; `suppressions` | 4.3, 5.6 |
| `custom-kind.json` | New document schema for `extensions/kinds/<type>.json` | 4.2 |
| `pack-case.json` | New document schema for a pack test case's `tests/<case>/case.json`: `description`, `model`, `parameters` | 5.4 |
| `declared-rules.json` | New document schema for `extensions/rules/*.json` | 5.6 |
| `common.json` | `$defs` `tableColumn`, `primaryKey` (optional `id`), `uniqueConstraint`, `foreignKey`, `checkConstraint` and `tableIndex`, moved from `table.json`; `columnTypeRef`, `sensitivity`, and `tableColumn`'s `typeRef` and `sensitive`; `tableColumn`'s `stableId`; `operationError`; the `xRef` description (`custom:<type>`, `of` with `member`); `requirementIds`, and `satisfies` on the attribute and `tableColumn`; `suppressions` | 3.1, 3.2, 3.4, 4.1, 4.2, 4.3, 5.6 |
| `table.json` | Members by `$ref` to `common.json`; `excludeStereotypes`, `redefines`, `excludes`; `satisfies`; `suppressions` | 3.1, 4.3, 5.6 |
| `stereotype.json` | `columns`, `primaryKey` (`id` required), `uniques`, `foreignKeys`, `checks`, `indexes`, `excludeStereotypes`; `errors`; `appliesTo` gains `operation`, `operation-member`, `outcome`, `error`, `custom`, `requirement`, `guard`, `action`, `invoke` and `gate`; `satisfies`; `suppressions` | 3.1, 4.1, 4.2, 4.3, 4.4, 5.6 |
| `extension.json` | `appliesTo.kinds` gains the same ten kinds | 4.1, 4.2, 4.3, 4.4 |
| `database.json` | Schema entries gain `defaultStereotypes`; `excludeStereotypes`; `satisfies` (the database, not its schemas); `suppressions` | 3.1, 4.3, 5.6 |
| `package.json` | `defaultStereotypes`, `excludeStereotypes`; `satisfies`; `suppressions` | 3.1, 4.3, 5.6 |
| `entity.json` | Binding `provenance` after `delete`; `satisfies`; `suppressions` | 3.3, 4.3, 5.6 |
| `seed.json` | `columns` items and `key` also take keyed ids of inherited members; `columns` items also take foreign-key ids of a table target; `rowsFrom` is `{ file }` or `{ script }`; `satisfies`; `suppressions` | 3.1, 3.4, 4.3, 5.6 |
| `reference-type.json` | `formerCodes`; `satisfies`; `suppressions` | 3.5, 4.3, 5.6 |
| `migration.json` | The rekey item's `table` description covers constraint entries; `collections`, `fromReferenceType`, `migrationState.pluralName`, a `then` of its own for `value-map`; `suppressions` | 3.1, 3.5, 5.6 |
| `batch.json` | `fold-into-stereotype` (`redefine`, `excludeMissing`); `map-by-name` and `resync-binding` (`keepNames`, `columns`); `rename-code`, `retire-code`, `merge-reference-types` and `rename-reference-type` (`code`, `to`, `codes`) | 3.1, 3.3, 3.5 |
| `process.json` | `satisfies` on the process, states, transitions, events, guards, actions, invokes and gates; `use` `library`, top-level `gates`, `states` no longer required, transition `gatePolicy`; `stereotypes` and `properties` on guards, actions, invokes and gates; `suppressions` | 4.3, 4.4, 5.6 |
| `enum.json` | `satisfies` on the enum and its members; `suppressions` | 4.3, 5.6 |
| `scenario.json` | `verifies`; `suppressions` | 4.3, 5.6 |
| `value-object.json`, `scalar-type.json`, `relation.json`, `view.json`, `sequence.json`, `routine.json`, `database-type.json`, `sql-object.json`, `query.json`, `mapping.json`, `actor.json` | `satisfies`; `suppressions` | 4.3, 5.6 |
| `category-tree.json`, `diagram.json`, `tag-vocabulary.json` | `suppressions` | 5.6 |
| Every other kind schema with `stereotypes`, except `migration.json` | `excludeStereotypes` | 3.1 |
| `locale.json` | Entry `content` (`content/<name>` texts) and `src` `content/<name>` keys | 4.2 |
| `pack.json` | Unit `for` gains `each operation`, `each custom:<type>` and `each requirement`; `uses`, `units` no longer required; unit `exports` | 4.1, 4.2, 4.3, 5.2, 5.3 |
| `maquettiste.json` | Top-level `uses`; `environments` (optional; absent or empty, environment names stay free) | 5.2, 6.3 |
| `diagnostics.json` | `suppressed` | 5.6 |

New project files that are not schema files: rows scripts `extensions/seeds/*.js` (3.4), custom kind files
`extensions/kinds/<type>.json` (4.2), calculation files `extensions/calculated/*.js` (5.5), rule declaration files
`extensions/rules/*.json` (5.6) and a pack's `tests/` folder (5.4).

### 2.5 CLI verbs and flags

| Command | Change | Section |
| --- | --- | --- |
| `maquettiste binding drift [<entity>...] [--database <id\|name>] [--all] [--check] [--format text\|json]` | New group `binding`: the drift report; with `--check`, exit 2 when a listed binding drifts | 3.3 |
| `maquettiste binding map --database <id\|name> <entity...> [--dry-run] [--format text\|json]` | Maps by name and records provenance | 3.3 |
| `maquettiste binding resync --database <id\|name> <entity...> [--column <key\|name>]... [--keep-names] [--dry-run] [--format text\|json]` | Re-syncs in one batch; `--column` serves one entity only | 3.3 |
| `maquettiste seed preview <seed> [--database <name or id>] [--format text\|json]` | New: a seed's rows, written or scripted, in database terms with `--database`; exit 4 for an unknown seed or database | 3.4 |
| `maquettiste seed export` | A foreign-key entry is headed by the foreign key's name and a composite key is JSON array text; a scripted seed exports its scripted rows | 3.4 |
| `maquettiste seed import` | A seed with a rows script is refused (exit 4); a row whose `@code` is a former code is skipped and listed with MQ7019 | 3.4, 3.5 |
| `maquettiste model apply <batch.json> [--dry-run] [--format text\|json]` | Takes `fold-into-stereotype` and the four reference-type refactors, each printing its usage; table seed columns take foreign keys by name; `--dry-run` runs through `GenerationService.PreviewBatchAsync` and prints the impact after the diagnostics | 3.1, 3.4, 3.5, 6.1 |
| `maquettiste migration list` | A value map's summary names its type, codes and names | 3.5 |
| `maquettiste model export --resolved --scope operations` | New scope value: one `OperationRecord` per operation | 4.1 |
| `maquettiste model export\|stats --kind custom:<type>` | Filters custom elements by custom kind | 4.2 |
| `maquettiste format` | Also rewrites `extensions/kinds/*.json` in canonical form | 4.2 |
| `maquettiste requirement coverage [--package <name or id>] [--check] [--format text\|json]` | New group `requirement`; with `--check`, exit 2 when a gap exists | 4.3 |
| `maquettiste requirement import <file.csv\|file.json> [--package <name or id>] [--apply] [--format text\|json]` | CSV or JSON by extension; a preview, written only with `--apply`; exit 1 when refused | 4.3 |
| `maquettiste process simulate` | The trace names library guards and actions | 4.4 |
| `maquettiste pack new <name> [--library]` | `--library`: a pack with no units, a partial, a helpers file and a helper test; a usage error with a `--from` other than `empty`. New packs start with an example case under `tests/`; the embedded starters leave `tests/` out | 5.2, 5.4 |
| `maquettiste pack rename --apply` | Also rewrites `uses` entries that name the pack and lists files whose text names it | 5.2 |
| `maquettiste pack exports <pack> [--element <id>]... [--format text\|json]` | New: a pack's export records for at most 20 elements, or its model-scope record | 5.3 |
| `maquettiste pack test [<pack>...] [--case <name>]... [--update] [--format text\|json]` | New: runs pack test cases and helper tests; exit 1 on a failure, 4 on a usage error or an unknown pack | 5.4 |
| `maquettiste model calculated --element <id>... [--name <name>]... [--format text\|json]` | New: the calculated values of those elements only; exit 1 when a calculated value failed | 5.5 |
| `maquettiste validate` | Declared rule findings; text counts suppressed findings; `json` and `sarif` carry them | 5.6 |
| `maquettiste model diff <a> [<b>] [--format text\|json\|markdown] [--element <id>]` | New: compares two models (each `working`, a snapshot id or a folder; `<b>` defaults to `working`), grouped by package, migration facts by database, with field changes per sub-element; exit 4 when a side resolves to nothing, is not a project or holds a newer model format | 6.2 |

`--all`, `--keep-names` (3.3), `--library` (5.2) and `--update` (5.4) join `CommandLine.FlagOptions`; `--case` (5.4)
joins `CommandLine.ValueOptions`. `--check`, `--apply`, `--dry-run`, `--database`, `--column`, `--package`,
`--element`, `--name`, `--from` and `--format` exist already. `markdown` is a new `--format` choice of `model diff`
only. The groups `binding` and `requirement` are new.

### 2.6 MCP tools

Net change to the pinned counts over phase 2: MCP tools +8 (+2 in milestone D, +2 in E, +4 in F, none in G) and
`AgentTools.All` +5 (+2 in D, +1 in E, +2 in F). When this was written the pins read 71 (the `McpSurfaceTests` Tools
array) and 27 (`AgentTools.All` in `McpSurfaceTests` and `AgentToolsTests`), so they end at 79 and 32. The tool count in
`README.md` and the two counts in `docs/mcp.md` move with them.

| Tool | Change | Section |
| --- | --- | --- |
| `get_binding_drift` | New catalog tool (MCP and the assistant): bindings with drift, in pages, from files only | 3.3 |
| `preview_seed_rows` | New catalog tool: one seed's rows with ids and keys, in database terms with `database`; pages of 100, at most 1000 | 3.4 |
| `get_requirement_coverage` | New catalog tool: totals and paged rows with gaps, `satisfiedBy` and `verifiedBy` | 4.3 |
| `import_requirements` | New: CSV or JSON import by external id; a preview unless `apply` | 4.3 |
| `preview_ddl` | New catalog tool: the create steps and PostgreSQL text of 1 to 100 picked tables, views, sequences, routines, database types and SQL objects | 5.1 |
| `get_pack_exports` | New: a pack's export records for at most 20 elements, or its model-scope record | 5.3 |
| `test_pack` | New: runs a pack's cases and helper tests; `update` rewrites expected trees | 5.4 |
| `get_calculated` | New catalog tool: the calculated values of 1 to 100 ids, with declarations, missing ids and failures; evaluates those only | 5.5 |
| `apply_batch` | `fold-into-stereotype`; `map-by-name` and `resync-binding` (`keepNames`); table seed columns take foreign keys by name, `rowsFrom.script`; the four reference-type refactors, items carry `usage`; describes custom elements, minted member ids, `@custom:` and `@member:` names, `satisfies`, `verifies` and `@requirement:` names; `dryRun` answers `impact` | 3.1, 3.3, 3.4, 3.5, 4.2, 4.3, 6.1 |
| `create_element`, `save_element` | Table seed columns take foreign keys by name, `rowsFrom.script`; describe the `custom` kind, `type`, `content` and member ids, `satisfies` and `verifies` | 3.4, 4.2, 4.3 |
| `patch_element` | Table seed columns take foreign keys by name, `rowsFrom.script`; describes custom elements and member ids | 3.4, 4.2 |
| `delete_element` | The plan lists `redefines`, `excludes` and default-list links of a stereotype's members | 3.1 |
| `get_references` | Every keyed use of a stereotype member; former codes whose `replacedBy` names a row; transitions across processes that use a library guard | 3.1, 3.5, 4.4 |
| `get_database_view` | `memberOrigin` on inherited members; default stereotypes on tables without an overlay; columns carry `typeRef` and `sensitive` | 3.1, 3.2 |
| `get_resolved_model` | `memberOrigin` and default stereotypes; columns' `typeRef` and `sensitive`; attribute `descriptionColumn`; `formerCodes`, and `pluralName` in migration states; scopes `operations`, `custom-elements` and `requirements`; libraries with `use: library` and gates' `usedBy`; pages carry `calculated` | 3.1, 3.2, 3.3, 3.5, 4.1, 4.2, 4.3, 4.4, 5.5 |
| `reference_type_usage` | Answers `columns` beside `usages` (also the assistant's tool) | 3.2 |
| `preview_materialize` | Ops `map-by-name` and `resync-binding`, with `keepNames` | 3.3 |
| `export_seed_csv` | Foreign-key entries headed by the foreign key's name, composite keys as JSON array text; scripted seeds export their scripted rows | 3.4 |
| `import_seed_csv` | Refused (`bad-request`) for a seed with a rows script; a row whose `@code` is a former code is skipped and listed with MQ7019 | 3.4, 3.5 |
| `list_extension_files`, `read_extension_file`, `write_extension_file`, `move_extension_file`, `delete_extension_file` | Accept `seeds/*.js` (kind `seed-rows`), `kinds/<type>.json` (`custom-kind`), `calculated/<name>.js` (`calculated`) and `rules/<name>.json` (`declared-rules`); each write checks its kind; a custom kind's write answers its elements' MQ3033 findings; deleting or moving a custom kind that has elements is refused; listings carry the declarations of calculated values and resolved rules | 3.4, 4.2, 5.5, 5.6 |
| `list_migrations` | Value-map summaries name the type, the codes and the names | 3.5 |
| `get_model_index` | Operation rows carry `subject` and `effect`; custom rows carry `type` and `memberCount`, with the kind filter `custom:<type>`; library rows carry `use: library` | 4.1, 4.2, 4.4 |
| `get_elements`, `get_model_kinds` | Kind filter `custom:<type>`; counts per custom kind | 4.2 |
| `get_template_context` | `each operation` and `each custom:<type>` units; `namespaces`; `exportsFrom`; `calculated.<name>` members | 4.1, 4.2, 5.2, 5.3, 5.5 |
| `get_schema` | Takes `custom:<type>` (`customKind`, `contentSchema`); returns `declared-rules` | 4.2, 5.6 |
| `get_project` | `customKinds`; the summary lists `environments`, the declared names (also the assistant's tool) | 4.2, 6.3 |
| `get_translations`, `set_translations`, `localization_status` | `content/<name>` fields and member nodes | 4.2 |
| `format_model` | Also rewrites `extensions/kinds/*.json` | 4.2 |
| `get_element` | Adds `requirements` (id, name, externalId, status, text) | 4.3 |
| `simulate_process`, `verify_scenarios` | Library guards, actions and gate policies run in the using process; `simulate_process` refuses a library | 4.4 |
| `record_scenario`, `export_process`, `sync_enum_from_process` | Refuse a library; `export_process` writes library guards and actions by name | 4.4 |
| `new_pack` | `library` (boolean); a new pack starts with an example case, a library with a helper test; a copied project pack leaves `tests/` out | 5.2, 5.4 |
| `list_packs` | Rows carry `uses` and `usedBy`; MQ6042 to MQ6046 among the load diagnostics | 5.2 |
| `get_pack` | `usedBy`; library partials list `include:<pack>:<path>` users; files under `tests/` have the role `test` | 5.2, 5.4 |
| `delete_pack` | Refused as `referenced` while a pack or `maquettiste.json` uses the pack | 5.2 |
| `delete_pack_file`, `move_pack_file` | Refused as `referenced` while another pack includes the file literally | 5.2 |
| `rename_pack` | Rewrites `uses`; the answer carries `usesUpdated` and `textReferences` | 5.2 |
| `get_plan` | Units carry `exports`; the plan carries `exports` | 5.3 |
| `explain_unit` | Causes of kind `exports` | 5.3 |
| `list_pack_files` | Files under `tests/` have the role `test` | 5.4 |
| `write_pack_file` | A file under `tests/` is not parsed as a template | 5.4 |
| `validate` | Failures of calculated values and resolved rule findings; declared rule findings and `suppressed`; MQ5020 on whole-model reports | 5.5, 5.6 |
| `compare_snapshots` | `from` and `to` also take folders (`maquettiste mcp` only; the assistant answers `bad-request`); answers grouped by package; `fieldChanges` and `format` (`json` or `markdown`); field changes carry `subId`, `subKind`, `subName`, `subPointer` | 6.2 |

### 2.7 Public engine API

An index; each section states the signatures.

| Section | Added or changed |
| --- | --- |
| 3.1 | `StereotypeSource`; `AppliedStereotype.Source`, `DefaultHolder`; `StereotypeHolder`; `ModelSnapshot.HolderOf` and the `EffectiveStereotypes(Element)` and `EffectiveStereotypes(StereotypeHolder)` overloads; `Element.ExcludeStereotypes`, `Package.DefaultStereotypes`, `DbSchema.DefaultStereotypes`; `PrimaryKey.Id`; `Stereotype.Columns`, `PrimaryKey`, `Uniques`, `ForeignKeys`, `Checks`, `Indexes`; `MemberForeignKey`; `Table.Redefines`, `Table.Excludes`, `TableRedefinition`; `RMemberOrigin` and `MemberOrigin` on `RColumn`, `RPrimaryKey`, `RUnique`, `RForeignKey`, `RCheck`, `RIndex`; `RPrimaryKey.Id`; `RStereotype.Source`; `MemberOriginView` on `ColumnView`, `KeyView`, `ForeignKeyView`, `IndexView`; `BatchOp.FoldIntoStereotype`, `BatchOperation.Redefine`, `ExcludeMissing` |
| 3.2 | `Column.TypeRef`, `Column.Sensitive`; `RColumn.TypeRef`, `RColumn.Sensitive`; `RColumn.ReferenceType` (with `CodeType`, `Strategy`, `ReferenceBinding`) also on typed designed, imported, extra and inherited columns; `RAttribute.Sensitive` (effective); `ColumnView` (`TypeRef`, `Sensitive`); `ReferenceTypeUsage.Columns`, `ReferenceTypeColumnUse` |
| 3.3 | `EntityBinding.Provenance`, `BindingProvenance`; `BatchOp.MapByName`, `BatchOp.ResyncBinding`, `BatchOperation.KeepNames`, `MaterializeRequest.KeepNames`; `ModelStore.IsBindingOperation`, `GetBindingDriftAsync`, `PlanMaterializeAsync` (changed); `BindingDriftQuery`, `BindingDriftPage`, `BindingDriftEntry`, `DriftColumn`, `DriftField`; `RAttribute.Description` (falls back to the column's comment), `RAttribute.DescriptionColumn`; `AttributeRecord.DescriptionColumn` |
| 3.4 | `Column.StableId`; `SeedRowsFile.Script` (`File` becomes nullable); `ModelSnapshot.SeedScripts` and the `seedScripts` parameter of `Create`; `StableIds`; `RColumn.StableId`, `RSeedColumn.ForeignKey`, `RSeed.RowsScript`, `RDataRow.Refs`, `RDataRef`; `ScriptRegistrationKind.Rows`, `IScriptSandbox.RunRows`; `ModelStore.PreviewSeedRowsAsync`, `SeedRowsPreview`, `SeedRowsPreviewRow`, `SeedRowsPreviewRef`; `ExtensionFileInfo.Kind` and `ExtensionFileContent.Kind` take `seed-rows` |
| 3.5 | `ReferenceType.FormerCodes`, `FormerCode`; `Migration.FromReferenceType`, `Migration.Collections`, `MigrationCollection`, `MigrationState.PluralName`; `BatchOp.RenameCode`, `RetireCode`, `MergeReferenceTypes`, `RenameReferenceType`; `BatchOperation.Code`, `To`, `Codes`; `SaveResult.Usage`, `RefactorUsage`; `ReferenceTypeRecord.FormerCodes`, `FormerCodeRecord`; `RReferenceType.FormerCodes`, `RFormerCode`; `RMigration.FromReferenceType`, `RMigration.Collections`, `RMigrationCollection`, `RMigrationState.PluralName` |
| 4.1 | `ElementKind.Operation`; `Operation`, `OperationLevel`, `OperationEffect`, `OperationMember`, `OperationOutcome`, `OperationCompletion`, `OperationReturns`, `OperationPaging`, `OperationError`, `OperationErrorRedefinition`; `Stereotype.Errors`; `ElementSummary.Effect`; `ResolvedModel.Operations`, `ROperation`, `ROperationMember`, `ROperationOutcome`, `ROperationReturns`, `ROperationError`; `REntity.Operations`, `REvent.FiredBy`, `RStereotypeDefinition.Errors`; `OperationRecord`, `OperationMemberRecord`, `OperationOutcomeRecord`, `OperationReturnsRecord`, `OperationErrorRecord` |
| 4.2 | `ElementKind.Custom`; `Custom`, `CustomKind`, `CustomKindDocument`, `ModelSnapshot.CustomKinds`; `ElementSummary.Type`; `KindInfo.CustomFolder`; `TranslationEntry.Content`, `LocalizationIndex.ContentFieldPrefix`; `RCustom`, `RMember`, `ResolvedModel.CustomElements`; `ExtensionFileInfo.Kind` and `ExtensionFileContent.Kind` take `custom-kind` |
| 4.3 | `ElementKind.Requirement`, `Requirement`; `ElementBase.Satisfies`; `Satisfies` on `ProcessState`, `ProcessTransition`, `ProcessEvent`, `ProcessGuard`, `ProcessAction`, `ProcessInvoke`, `ProcessGate`; `Scenario.Verifies`; `RRequirement`, `RAnnotated.Satisfies`, `RProcessNode.Satisfies`, `RScenario.Verifies`, `ResolvedModel.Requirements`; `RequirementCoverageQuery`, `RequirementCoverage`, `RequirementCoverageTotals`, `RequirementCoverageRow`, `CoverageLink`; `RequirementImportFormat`, `RequirementImportRequest`, `RequirementImportResult`, `RequirementImportRow`; `ModelStore.GetRequirementCoverageAsync`, `ImportRequirementsAsync` |
| 4.4 | `ProcessUse.Library`; `Process.States` (no longer required), `Process.Gates`; `ProcessTransition.GatePolicy`; `Stereotypes` and `Properties` on `ProcessGuard`, `ProcessAction`, `ProcessInvoke`, `ProcessGate`; `ProcessLibraries`; `StatechartModel.Get` and `ProcessExpressions.Get` take it; `StatechartModel.Libraries`; `ChartTransition.Gate` (inline, else the policy's); `RProcess.Libraries`; `RGate.Transition` (nullable), `RGate.UsedBy`; `RGuard.UsedBy` and `RAction.UsedBy` across processes; `ResolvedModel.ProcessLibraries` |
| 5.1 | `DdlBuilder`, `DdlResult`, `DdlStep`, `DdlVerb`, `DdlKind`, `DdlPhase`, `DdlOptions`, `DdlSql` (namespace `Maquettiste.Engine.Ddl`); `SchemaDiffResult.DatabaseId`; `GenerationService.GetDdlAsync`, `DdlPreviewResult`, `DdlPreview`, `DdlPreviewStep` |
| 5.2 | `PackManifest.Uses`, `PackUse`; `PackManifest.Units` (no longer required); `ProjectSettings.Uses`; `ModelSnapshot.ScriptLibraries`, `ScriptLibrarySource`; `LoadedPack.Uses`, `Sees`, `UsedBy` (`Order` becomes dependency order); `PackSet.Used`, `PackSet.Find`; the `IScriptSandboxFactory.CreatePool` overload with libraries; `ScriptLibrary`, `IScriptLibraryCalls`, `ScriptCallContext.Libraries`; `PackSummary.Uses`, `UsedBy`, `PackDocument.UsedBy`; `PackRenameResult.UsesUpdated`, `TextReferences`; `TemplateContextResult.Namespaces`, `TemplateNamespace`; the `GenerationService.CreatePackAsync` overload with `library` |
| 5.3 | `PackUnit.Exports`, `PackExport`; `UnitExports`; `UnitPlan.ExportOnly`; `UnitState.Exports` (unit state format 5), `RenderedUnit.Exports`; `PlanUnit.Exports`, `GenerationPlan.Exports`, `PlanExport`; `GenerationService.GetPackExportsAsync`, `PackExportsResult`, `ElementExports`; `TemplateContextResult.ExportsFrom`, `TemplateExportSource` |
| 5.4 | `GenerationService.TestPackAsync`, `PackTestOptions`; `PackTestReport`, `PackTestResult`, `PackCaseResult`, `PackCaseFile`, `PackHelperTestResult`; `PackTestOutcome`, `PackCaseFileChange`; `PackFileInfo.Role` takes `test` |
| 5.5 | `RAnnotated.Calculated`, `RCalculated`, `CalculatedValueException`; `ScriptRegistrationKind.CalculatedValue`, `ScriptRegistration.Resolved`, `ScriptRegistration.CalculatedValue`, `CalculatedValueDeclaration`; `IScriptSandbox.RunCalculation`, `RunResolvedRule`; `ModelSnapshot.CalculationScripts` and the `calculationScripts` parameter of `Create`; `GenerationService.GetCalculatedAsync`, `CalculatedValueQuery`, `CalculatedValueItem`, `CalculatedValueAnswer`; `ResolvedPage.Calculated`; `ExtensionFileInfo.Calculations`; `ExtensionFileInfo.Kind` takes `calculated`; `ExtensionRuleInfo.Resolved` |
| 5.6 | `Element.Suppressions`, `Suppression`; `DeclaredRuleFile`, `DeclaredRule`, `RuleSelector`, `RuleCondition` with `RuleEquality`, `RuleCount` and `RuleReferences`, `DeclaredRuleDocument`; `ModelSnapshot.DeclaredRules` and the `declaredRules` parameter of `Create`; `ValidationReport.Suppressed`, `SuppressedFinding`; the `SarifWriter.WriteAsync(Stream, ValidationReport, string, CancellationToken)` overload; `ExtensionFileInfo.Kind` takes `declared-rules`; `ExtensionRuleInfo.Resolved` also for declared rules |
| 6.1 | `BatchPreview.Impact`; `BatchImpact`, `ImpactReferrer`, `ImpactDatabase`, `ImpactPack`, `ImpactUnit`; `GenerationService.PreviewBatchAsync` |
| 6.2 | The `SnapshotLibrary.CompareAsync` and `CompareElementAsync` overloads with `SnapshotCompareOptions`; `SnapshotComparison.Packages`, `SnapshotPackageChanges`; `SnapshotElementChange.Package`, `Database`, `Label`, `Fields`, `FieldsTruncated`; `SnapshotFieldChange.SubId`, `SubKind`, `SubName`, `SubPointer`; `ModelDiffFormat` |
| 6.3 | `ProjectSettings.Environments`, `EnvironmentSettings`; `ResolvedModel.Environments`, `REnvironment`; `AgentProjectInfo.Environments` |

Members that several sections extend, in task order: `BatchOp` and `BatchOperation` (3.1, 3.3, 3.5; each adds
trailing optional parameters); `ColumnView` (3.1, 3.2); `RAttribute` (3.2, 3.3); `ElementKind` (`Operation`, `Custom`,
`Requirement`, appended in that order by E1, E20 and E40); `ElementSummary` (`Effect`, then `Type`); the last optional
parameters of `ModelSnapshot.Create` (`seedScripts` in 3.4, `calculationScripts` in 5.5, `declaredRules` in 5.6);
`IScriptSandbox` and `ScriptRegistrationKind` (3.4, 5.5); `ExtensionFileInfo.Kind` (`seed-rows`, `custom-kind`,
`calculated`, `declared-rules`); `ExtensionRuleInfo.Resolved` (5.5, widened in 5.6); `TemplateContextResult` (5.2, 5.3).

Template contract and dependency keys: helpers `stable_id` (3.4), `custom_named` (4.2), `ddl_steps` and `ddl_sql`
(5.1), `exported` (5.3) and `has_calculated` (5.5); variables `operation` (4.1), `custom` (4.2), `requirement` (4.3) and
`exports` (5.3); key families `x:<path under extensions>` (3.4), `c:<type>` (4.2), `px:<pack>/<id>` and `px:<pack>`
(5.3) and `cv:<kind>` (5.5), and the settings key `s:environments` (6.3).

## 3. Milestone D: shared structure and typed columns

### 3.1 Stereotypes that contribute structure

**Why every team**

Most schemas repeat a few small structures across many tables: audit timestamps, a row version, an owner column with
its foreign key, a deletion marker with its partial index. Copied by hand into each table, they drift: a type change
touches every file, one table forgets a column, and an agent editing a table cannot tell which columns follow a
convention. A stereotype already names such a convention. When it also carries the columns and keys, a team states the
structure once, applies it to a whole database schema or package in one line, writes per-table exceptions that say
only what differs, and adopts it later on existing tables without touching the database. The product reserves no
stereotype: the project writes it and chooses where it applies; the engine only resolves.

**Today**

- A stereotype holds `attributes`, `defaultProperties` and `storage` and nothing physical beyond storage
  (`Model/Vocabularies.cs:117`, `:126`, `:129-130`; `schemas/v1/stereotype.json`). Its attributes become virtual
  attributes of the entities that carry it (`Resolution/ResolveRun.Conceptual.cs:439-443`), and its storage reaches only
  a table that has a file (`Resolution/DatabaseRun.cs:397-399`). It cannot add columns, keys, indexes or checks.
- The effective set is computed from a key list alone: implied first, then own (`Model/ModelSnapshot.cs:282-313`).
  Nothing applies a stereotype by database schema or package. Conventions stop at project and database level
  (`Resolution/EffectiveConventions.cs:7-10`). A resolved table without a file has no annotations, stereotypes included
  (`Resolution/ResolveRun.Conceptual.cs:799-815`), and merged properties stop early when the file lists neither
  stereotypes nor properties (`:876`).
- A designed column's id is its key (`Resolution/DatabaseRun.cs:211-212`); a synthesized column's id is
  `<tableKey>/<key>` (`Resolution/DatabaseRun.Columns.cs:289-290`); a relation end's key column is keyed
  `<endId>.<keyAttrId>` (`Resolution/DatabaseRun.Relations.cs:164`). The index splits a keyed value on `@` and `.` and
  records each id segment as a reference (`Model/ModelIndexer.cs:859-867`). The `x-ref` description already allows "ids
  joined by '.', the keyed id of an inherited member" (`schemas/v1/common.json:85`).
- Validation finds a table's columns in its file only: constraint and index column lists
  (`Validation/PhysicalRules.cs:116`), seed columns (`Validation/TableSeedRules.cs:45`) and reference bindings
  (`Validation/ReferenceBindingRules.cs:28-29`). Bindings resolve a column by name, key or id among the resolved columns
  (`Resolution/DatabaseRun.Bindings.cs:300-304`).
- An update that drops a sub-element drops the seed columns that name it, by exact value
  (`Loading/ChangePlanner.cs:306-319`, `:450`).
- `RTable.Origin` already means `synthesized`, `designed` or `imported` (`Resolution/ResolvedPhysical.cs:176-177`); an
  attribute added by a stereotype carries `FromStereotype` (`Resolution/ResolvedConceptual.cs:184`,
  `Editor/ResolvedRecords.cs:124`).
- `materialize-tables` gives every resolved column an id of the designed table in its key map (the overlay entry's, an
  alias's or a new one, `Loading/Materializer.cs:94-103`) and copies every column, key, index and check of the
  projected table into the table file, with the overlay's annotations, `stereotypes` among them (`:179-291`, `:379`).
- The migration classifier moves keys only through a materialize's key maps and `rekey` facts
  (`ModelStore.Migrations.cs:296-298`), and it sees an undo only when a table key moves back
  (`Migrations/FactClassifier.cs:208-217`). The `rekey` item's `kind` takes every object kind
  (`schemas/v1/migration.json`, `$defs/objectKind`), but MQ4106 refuses a rekey entry that is not a table, column,
  sequence or SQL object (`Validation/MigrationRules.cs:25`, `:295-296`). The classifier records only table, column and
  sequence moves (`Migrations/FactClassifier.cs:226-248`), key readers skip the other kinds
  (`Migrations/FactKeys.cs:98-122`, `:215-237`), and `migration fold` reads any other entry's `to` as a table key
  (`Migrations/FactFolder.cs:184-190`) and composes only table, column, sequence and SQL object moves (`:265-329`).
  Capture keys of keys and indexes derive from column keys (`SchemaDiff/SnapshotCapture.cs:13-30`), and
  `SnapshotRekey.Apply` moves them with their columns (`SchemaDiff/SnapshotRekey.cs:30-96`).
- A packaged object does not depend on its package's file: an entity depends on its own file, its bases, its mappings
  and the databases (`Resolution/ResolveRun.Conceptual.cs:400-412`). A physical object depends on its database's
  file (`Resolution/DatabaseRun.cs:205`). The index patch builds in full only when a stereotype, tag vocabulary,
  category tree or reference data changes (`Model/ModelIndexer.cs:284-303`).
- Expanded batch operations plan against the model as loaded, not against earlier operations of the batch
  (`ModelStore.Schemas.cs:25-46`). A batch placeholder is `$name` (`schemas/v1/batch.json:19`), so a `$`-prefixed value
  in a stored id field would read as one.
- The resolver reports no duplicate column name; only the file-level MQ4003 does (`Validation/PhysicalRules.cs:90`).
- Check expressions and index predicates are SQL text that names columns by physical name (`Model/Physical.cs:548`,
  `:624`).

**Decisions taken here**

1. **Stereotypes, not a new kind** (scope 3.3 and 10). A stereotype that applies to tables may declare `columns`,
   `primaryKey`, `uniques`, `foreignKeys`, `indexes` and `checks`. These are its **members**. Every table whose
   effective set holds the stereotype inherits them in the resolved model, whatever its origin: designed, imported or
   synthesized (with or without an overlay). One exception, by identity and not by name: a synthesized table skips the
   members of a stereotype whose virtual attributes it already stores as columns (a column's attribute has
   `FromStereotype` set to it). That covers an entity table, a hierarchy stored in one table, a junction or promoted
   table and a child table alike, whatever the inheritance strategy. There the stereotype is realized by its
   attributes, so one stereotype serves entities through attributes and designed tables through members.
2. **Keyed ids.** An inherited member's id and key in a table is `<tableKey>.<memberId>`. The table key is the table
   file's id for a designed or imported table, and the synthesized key for a synthesized one (`<entityId>@<databaseId>`,
   `<entityId>.<attributeId>@<databaseId>`, `<relationId>@<databaseId>`). Ids stay globally unique, so seeds, indexes,
   bindings, the reverse index, delete refusal, captures and facts treat an inherited member like any other. A bare
   member id never names an inherited member of a table (MQ4008 says so and suggests the keyed form).
3. **Redefinition by facet.** A table's `redefines` entry names a member (`member`, the stereotype member's id) and
   states only the facets it changes. The entry has no id; the member keeps its keyed id, so adding or removing a
   redefinition changes no key, seed, index or binding. Type facets and a column's `name` are not redefinable: the
   stereotype's check expressions and index predicates name its columns in SQL text, which a per-table name would break.
   A table that needs another type or name excludes the member and declares its own column. A redefinition replaces a
   default; it cannot remove one.
4. **`excludes`** drops members by id. **`excludeStereotypes`** drops a stereotype that a schema or package default
   gives (and the stereotypes it implies), never an own one or one an own one implies (MQ2029).
5. **Defaults.** A database schema entry and a package may declare `defaultStereotypes`. The effective set is built in
   layers: the schema's defaults, then the package defaults from the outermost package to the element's own, then the
   own list. Within a layer, the stereotypes the layer's entries imply come first, then the entries, as phase 1 orders
   the own list. The default entries `excludeStereotypes` names leave, with the copies they imply. A key listed twice
   then keeps its last place: a later layer beats an earlier one, and within a layer an entry beats an implied copy.
   With no defaults the set is exactly phase 1's. A default skips, silently, an element whose kind it does not apply
   to, and so do the stereotypes it implies, so a schema default never makes MQ2004. Group, requires and conflict rules
   (MQ2013 to MQ2015) judge the whole set, so a clash is an error, never a hidden precedence. Defaults reach top-level
   elements only, never columns, attributes or process nodes. Schema defaults reach tables, views, sequences, routines,
   database types and SQL objects; package defaults reach every kind with a package (the package fallbacks of
   next-phase-1 4.3 included: a table overlay's entity or relation, a seed's target, a scenario's process). A migration
   fact has no package and takes none. Database-level and project-level defaults are not built: a team lists its
   schemas.
6. **Members name only their own stereotype's columns** (MQ2025). A contributed key, index or check sets no `name`
   (MQ2024): every carrier would share it. Names come from the naming conventions per table; a table may redefine one.
7. **A contributed foreign key's target** is either `referencesTable` (one fixed table) or `referencesParameter`, the
   name of a required stereotype parameter typed `{type: string, x-ref: {kind: table, sameDatabase: true}}` (MQ2026),
   so each carrier names a table file of its own database. Each carrier gives its own target in `properties`, checked
   by MQ2010, MQ2011 and MQ2018 as phase 1 built them. Not a `$param.<name>` string: `$` already starts a batch
   placeholder. A carrier without a value has MQ2018 and the resolver leaves the key out without a second finding.
8. **Order in a resolved table.** Own members first (a synthesized table's attribute columns and overlay extras), then
   inherited ones in effective-set order, each stereotype's in its file order. Inherited checks continue the own
   checks' ordinal. Inherited indexes count as declared indexes (they are never replaced by an `indexed` attribute's).
9. **Two primary keys are an error** (MQ4076): an inherited key beside the table's own, its entity's key, or another
   stereotype's. A partitioned table adds its partition column through a `columns` redefinition of the inherited key.
10. **Origin on every member.** Each resolved column, primary key, unique, foreign key, check and index carries
    `MemberOrigin`: `own`, `stereotype`, `schema-default` or `package-default`, the stereotype, its member id, and the
    facets the table redefined. Not `Origin`, which tables already use for another meaning.
11. **Facts.** Members are resolved columns and keys, so the phase 1 classifier records their facts unchanged: a
    stereotype change that reshapes many tables records one fact per object it changes. Renaming a stereotype's column
    records one complete `rename` fact **per carrying table**, all in one write, and changes no table file, which is the
    scope's acceptance line. This revises the scope's "one rename fact": a `rename` names one object, so that any
    migrator can apply each fact alone; the fact format and the ledger stay as phase 1 froze them.
12. **Fold into a stereotype** is the batch operation `fold-into-stereotype` (MCP `apply_batch`, CLI `model apply`). It
    matches own columns to the stereotype's by name and own keys, indexes and checks structurally, removes what matched,
    rewrites every reference to the inherited ids, and records one `rekey` fact per database. Phase 1 already defines
    `rekey` (`next-phase-1-design.md` 3.2); the fold is its second producer after materialize. The rekey also lists the
    constraint ids it moved (`index`, `unique`, `foreign-key`, `check`), each with its `table` as a column entry has, so
    a consumer that keys objects by element id follows them; capture readers ignore those entries, since capture keys
    follow columns. MQ4106 takes these kinds as phase 1 widened it for `sql-object`. Name matching appears only here,
    and the operation writes ids.
13. **Previews never touch the whole model.** Inherited members are computed per table from the snapshot. The fold
    resolves only the databases of its target tables, as every write's classifier does. A stereotype or default change
    that reaches every database lists no facts in a dry run, as phase 1 decided.
14. **No template change.** Inherited members are ordinary members of the resolved table, so sql-ddl renders them as
    today (D13 proves it). Section 5.1's DDL steps consume them the same way.
15. **Materialize keeps inheritance.** `materialize-tables` writes the designed table with the projection's shape and
    leaves inherited members inherited; their keys move with the table key. Materialize never turns a member into an
    own column.
16. **`Element.ExcludeStereotypes` is virtual** (found in D2). `Migration` overrides it with `[JsonIgnore]` and always
    answers null, because `migration.json` has no such key. System.Text.Json leaves an ignored override out of a
    record's contract, but not an ignored `new` property, and the schema and record keys must match.
17. **Default layers, exactly** (D2). A layer drops an excluded entry, or one whose kind does not fit, before it adds
    implied copies. A copy that another kept entry of the layer implies therefore stays. A copy whose own key is
    excluded, or whose kind does not fit, leaves too. The schema entry a default schema names is found by the database's
    `defaultSchema`, else the dialect's default name (`public`, `dbo`), as the resolver names it.
18. **Property schemas by effective keys** (D3). The walks keep `PropertySchemaSet` stateless: `Sites` takes the
    element's keys, `For(PropertyHost)` reads them from the host (`PropertyHost.EffectiveKeys`), and the internal
    `ModelSnapshot.PropertyKeys` gives them (null while the model declares no default, so phase 1's applicability holds
    exactly). The keys are the effective set's, then the own keys that name no stereotype; an excluded implied copy is
    never added back. Every caller passes them: the index, the validator's reference walk, extension checks,
    names-to-ids, localization, tags and the delete cascade.
19. **The index's full build, widened** (D3). While either model declares a default, a patch builds in full when a
    package, database or mapping is changed, added or removed, when an entity, relation, process, reference type or
    table is added or removed or changes its package, and when a table changes its schema, entity, relation,
    attribute, origin or database. Each can move the holder of a kept document (a child table follows its owner's
    overlay; a seed's target may be a table or a reference type).
20. **Holders of tables without a file** (D3). An entity's table takes its entity's package and `SchemaOf` by the
    entity; a relation's table its relation's package and the default schema's entry; a child table without an overlay
    takes its owner table's package and schema, which also covers the child of a relation's table, where routing by
    package would differ from the resolver. It does not take the owner's own `stereotypes` or `excludeStereotypes`: an
    owner's exclusion of a default does not reach its child tables, and a child table that needs one gets a file of
    its own. `TableBuild.Holder` and `TableBuild.Effective` keep them for D7.
21. **Provenance on attributes and pointers** (D3). `RAttribute.FromStereotype.Source` says where the owner got the
    stereotype. MQ3007 for a virtual attribute of a default points at `/stereotypes`, or the file root without a list;
    MQ5001 and MQ2018 do the same and their messages name the schema entry or package that gives the default.
22. **Membership packages shape databases while defaults exist** (D4). A write's classifier counts a change of a
    membership-only `package` as shaping while either model declares a default, and a package whose `parent` changes
    then chooses what its defaults reach, as a change of its `defaultStereotypes` does.
23. **The save scope sees the candidate only** (D4). `ConflictPeers` cannot tell that a list changed, so a package
    brings its reach while the model declares any default, a database the reach of its schema entries that declare one,
    and a stereotype the reach of every default list naming it or a stereotype implying it; the reach adds, for schemas,
    the entities and relations whose tables without a file would be there. A save that removes the model's last default
    list leaves the package's descendants' and fallback members to the next validate (its own members are its
    referrers).
24. **A redefined foreign key name has a home** (D6). `MemberForeignKey` has no `Name` in files, yet a table may
    redefine `name`, and an inherited member is the stereotype's member with the redefinition applied. The record gains
    an internal `Name` (outside the JSON contract and the public API) that only a member set fills. The redefinition's
    `storage` is written through `allOf` around `common.json#/$defs/storage`, so its empty default does not apply:
    absent means not redefined, and an empty map removes the inherited parameters.
25. **What `TableMembers.CheckLinks` reads** (D6). `TableMemberSet` also carries `Inheritable`: every member before
    `excludes` (after `skip`), by member id, column references keyed and no redefinition applied, which the facet fit,
    MQ4078 and the generic link check need. Its findings carry the table's id and pointer but no file path, since the
    validator and the resolver place them; their severity is the catalog's, else the rules table's until D7 adds MQ2027,
    MQ2028 and MQ4078. A table file's set is also cached by file id, a candidate table (not the snapshot's instance) is
    never cached, and a cached (table key, skip) entry answers only the inputs it was built from.
26. **Exclusion and default-list checks** (D5). An unknown key in `excludeStereotypes` is MQ2003 at its entry, as in
    a default list, and takes no MQ2030. MQ2031 judges a default list with the stereotypes its entries imply, as the
    layer applies them, at the later entry (for an implied copy, the entry that implies it). A finding about a default
    stays a message hint ("add `<key>` to its excludeStereotypes"); the catalog's quick fix names batch operations, and
    none adds an exclusion.
27. **Seeds name inherited columns by keyed id** (found in D10). `seed.json` took only plain ids in `columns` and
    `key`, so no seed could name an inherited column or unique. Both now also take the keyed id `<tableId>.<memberId>`
    (two ids joined by `.`); `Seed.Columns` and `Seed.Key` were already keyed references. D8 judges such a column
    against the table's member set.
28. **Member links on edit and delete** (D10). A save that removes a stereotype member drops the seed columns whose
    keyed value's member part is that member, and removes the `redefines` and `excludes` entries naming it from every
    table file the batch does not name. A plain delete of a stereotype changes nothing, so its carriers, member links
    and keyed uses refuse it; the delete plan names the inherited copy each keyed use holds. `remove-references` and
    `delete-dependents` remove a `redefines` entry whole and clear an `excludes` entry. `remove-references` refuses
    every other keyed use of an inherited copy, a binding field included, although a deleted column's binding field
    goes in both resolutions: deleting a stereotype never rewrites a binding silently. The delete plan lists the seed
    columns it drops under `removes`, with the sub-kind `column`.
29. **Findings of a table without a file** (D7). MQ2013 to MQ2015 for a table without a file reuse validation's judge
    of an effective set (`BuiltinRules.JudgeEffectiveSet`, now internal) and go on its entity or relation at
    `/stereotypes`, noting that an overlay holds the exclusion; a child table takes its owner's defaults and is judged
    with its owner. MQ4003, MQ4076 and MQ4079 about an inherited member of such a table go on its entity or relation
    without a pointer.
30. **Relation mappings name file keys only** (D7). A relation mapping's `foreignKey` cannot name an inherited foreign
    key: relations resolve before a table's members are added, as phase 1 resolves them before a table's own keys are
    finished.
31. **`$element` on an overlay** (D8). An `x-ref` value holds ids joined by `.`, so `of: "$element"` reaches the
    inherited members of designed and imported tables by `<tableId>.<memberId>`. An overlay's inherited members are
    keyed by the synthesized table key, which holds `@` and is no `x-ref` value (MQ2010), so on an overlay a bare member
    id is MQ2011 and its message says why. Under `sameDatabase`, a keyed id takes its table's database.
32. **Keyed ids in seed input are ids** (found in D8). The names-to-ids step of a seed body (`Loading/SeedInput.cs`)
    read a keyed id in `columns` or `key` as a bare name and refused the create (MQ1002). It now leaves an id or a keyed
    id as written, so decision 27's keyed ids reach the seed rules.
33. **What validation reads of a table's members** (D8). The seed, binding and foreign-key lookups read a designed or
    imported table file with its inherited columns, primary key (when it has none of its own), uniques and indexes
    appended, keyed (`MemberRules.WithMembers`). Inherited foreign keys are not added there, since a carrier's parameter
    can give their target, so MQ7111 through an inherited foreign key waits for a follow-up. A foreign key that
    references a synthesized table without an overlay takes any `<tableKey>.<memberId>` whose member is a stereotype's
    column; the resolver reports one the table does not inherit. A contributed foreign key's fixed `referencesTable`
    that names no table is MQ2001 on the stereotype, as a table file's is.
34. **The rekey back lists constraint entries too** (D17). Captures key constraints by their columns, so they cannot
    tell that a constraint id came back. `ClassifierInput` gains the internal `ConstraintsBefore` and
    `ConstraintsAfter`: the own and inherited (keyed) constraint ids of the table files that an open rekey's constraint
    entries name, which the store reads from the documents. An entry whose `to` leaves its table and whose `from` comes
    back is recorded back after the table's columns. Without it, `migration fold` would compose a fold and its undo
    into a rekey that still moves the constraint ids.
35. **Readers of constraint entries** (D17). `FactKeys.ConstraintKinds` names the four kinds. `StaleFacts` follows a
    constraint entry when it judges whether rekeys cancel, so a rekey that moves only constraint ids is never stale by
    itself. A rekey's summary counts them (`rekey 1 column, 1 index`, plural `indexes`). MQ4106's message for an
    unknown kind names them as ids, not keys.
36. **`memberOrigin` on the wire** (D9). Every column, key, foreign key and index view carries `memberOrigin`, null for
    own members. The contract leaves it out of `required`, as it does `dbTypeId`, since recordings made before lack it.
37. **Materialize keeps the holder** (D16). A designed table does not fall back to its entity's package, so the table
    `materialize-tables` writes states the package whose defaults reached the projected table: the overlay's own (which
    materialize used to drop), else the entity's when that package's chain gives tables a default. A carried
    redefinition's `columns` and `onDeleteColumns` follow the new column ids, and a redefinition of a member the table
    now excludes leaves with it (it would be MQ2028).
38. **Folds expand last** (D11). A batch expands its folds after its other expanded operations, so a fold knows every
    document the others write, later ones included, and refuses when one writes the stereotype or a target. A fold's key
    maps go to the recording of facts only, never to the committed snapshot's aliases, which stay materialize's.
39. **How the fold checks itself** (D11). `Loading/StereotypeFold.cs` matches on the target databases as resolved before
    the fold: own constraints by their resolved columns, a member's in dialect terms. Step 5 compares each folded member
    with the own one it replaces (columns on captures, constraints on the resolved objects, which carry ids) to choose
    the redefinitions, then confirms that every table of the target databases captures as before, moved through the
    key map, with column positions and derived constraint keys set aside. A redefinition never removes a value: an own
    column without a default, comment, collation or default name, or an own key without clustering, beside a member
    that states one, refuses the table.
40. **Own primary keys and mapped foreign keys** (D11). An own primary key matches the stereotype's whenever both exist
    (its capture key is `pk`), so different columns become a `columns` redefinition or a refusal. An own primary key
    with an id moves its references to the keyed id but takes no rekey entry: rekey kinds have no primary key, and
    capture keys follow columns. A foreign key that a relation mapping names stays own (decision 30).
41. **What the fold rewrites** (D11). Only a value that equals a moved id; migration facts are history and never
    change. A query's column text changes only where it names the column by key, aliased or bare
    (`QueryColumnRewrite.Replacements` with `byKeyOnly`); a text that names it by physical name still finds it.
    Materialize keeps rewriting every aliased text, as before.
42. **A dry run's facts** (D11). As phase 1 decided, a dry run lists no facts when a change reaches every database, and
    a model with one database is always that case; the dry-run test adds a second, empty database.

#### The contributed-members mechanism

One mechanism serves every holder that inherits members from its stereotypes: tables here, operations' errors in
section 4.1. A holder kind supplies its key, its member lists, its redefinition record and the function that applies a
redefinition's facets; the mechanism does the rest. It is internal to the engine; 4.1 uses it from the same assembly.
A public model record such as `TableRedefinition` implements the internal interface.

```csharp
namespace Maquettiste.Engine.Model;

/// <summary>One entry of a holder's redefines list.</summary>
internal interface IMemberRedefinition
{
    /// <summary>The id of the stereotype member the entry redefines.</summary>
    string Member { get; }
    /// <summary>The JSON names of the facets the entry states, in ordinal order (never "member").</summary>
    IReadOnlyList<string> Facets { get; }
}

/// <summary>A member a holder inherits from a stereotype of its effective set.</summary>
/// <param name="Key">ContributedMembers.KeyOf(holderKey, MemberId).</param>
/// <param name="MemberId">The stereotype member's id.</param>
/// <param name="Member">The stereotype's member with the holder's redefinition applied.</param>
/// <param name="From">The effective-set entry that contributes it (its Source: own, implied or which default).</param>
/// <param name="Redefined">The redefinition's facets, or empty.</param>
internal sealed record InheritedMember<TMember>(string Key, string MemberId, TMember Member, AppliedStereotype From,
    IReadOnlyList<string> Redefined) where TMember : class;

internal enum MemberLinkProblem { NotInherited, RedefinedTwice, NoFacet, ExcludedAndRedefined }

/// <param name="Pointer">The entry's pointer from the holder's root: /redefines/<n>/member or /excludes/<n>.</param>
internal sealed record MemberLinkFinding(MemberLinkProblem Problem, string Member, string Pointer);

internal static class ContributedMembers
{
    /// <summary>holderKey + "." + memberId.</summary>
    public static string KeyOf(string holderKey, string memberId);

    /// <summary>Splits at the last '.' when the rest is an id. Only splits: the caller confirms the holder key and that
    /// the member belongs to a stereotype (a relation end's key column has the same shape).</summary>
    public static bool TryParse(string key, out string holderKey, out string memberId);

    /// <summary>
    /// The inherited members, in effective-set order, then each stereotype's member order. A member without an id, a
    /// member listed in excludes, and every member of a stereotype in skip are left out; a member a redefinition names
    /// is passed through apply.
    /// </summary>
    public static IReadOnlyList<InheritedMember<TMember>> Inherit<TMember, TRedefinition>(
        string holderKey,
        IReadOnlyList<AppliedStereotype> effective,
        Func<Stereotype, IReadOnlyList<TMember>> membersOf,
        Func<TMember, string?> idOf,
        IReadOnlyList<TRedefinition> redefines,
        IReadOnlyList<string> excludes,
        IReadOnlySet<string> skip,
        Func<TMember, TRedefinition, TMember> apply)
        where TMember : class where TRedefinition : IMemberRedefinition;

    /// <summary>
    /// The kind-free link problems: an entry naming a member outside inheritable, a member redefined twice, an entry
    /// with no facet, a member both excluded and redefined (reported at its excludes entry). Facet fit and the members
    /// a kind keeps together are the holder kind's checks.
    /// </summary>
    public static IReadOnlyList<MemberLinkFinding> CheckLinks<TRedefinition>(IReadOnlySet<string> inheritable,
        IReadOnlyList<TRedefinition> redefines, IReadOnlyList<string> excludes)
        where TRedefinition : IMemberRedefinition;
}
```

`inheritable` is the set of member ids the holder inherits before `excludes` (after `skip`). The resolved counterpart
is the public `RMemberOrigin` (Public API below), which any inherited member carries. MQ2027 and MQ2028 are worded for
any holder, so a holder kind maps `CheckLinks` findings onto them and adds its own facet and dependency checks under the
same ids.

Tables use it through `Model/TableMembers.cs` (internal):

```csharp
namespace Maquettiste.Engine.Model;
internal sealed record TableMemberSet(string TableKey,
    IReadOnlyList<InheritedMember<Column>> Columns,           // Id = the keyed id; facets redefined
    IReadOnlyList<InheritedMember<PrimaryKey>> PrimaryKeys,   // more than one is MQ4076
    IReadOnlyList<InheritedMember<UniqueConstraint>> Uniques,
    IReadOnlyList<InheritedMember<MemberForeignKey>> ForeignKeys,
    IReadOnlyList<InheritedMember<TableIndex>> Indexes,
    IReadOnlyList<InheritedMember<CheckConstraint>> Checks)
{   public bool IsEmpty { get; }
    public bool HasColumn(string key); }                       // a keyed id of an inherited column
internal static class TableMembers
{   // A table file: its holder (ModelSnapshot.HolderOf) and its table key. For a designed or imported table this is
    // the exact set. For an overlay it is the candidate set (skip empty): which stereotypes decision 1 skips needs the
    // columns the table stores, which only resolution knows.
    public static TableMemberSet Of(ModelSnapshot model, Table file);
    // The resolver's form, for a table with or without a file; skip holds the stereotypes realized by attributes.
    public static TableMemberSet Of(ModelSnapshot model, string tableKey, IReadOnlyList<AppliedStereotype> effective,
        IReadOnlyList<TableRedefinition> redefines, IReadOnlyList<string> excludes, IReadOnlySet<string> skip);
    public static string? TableKeyOf(Table file);              // the file id, or the synthesized key of an overlay
    // MQ2027, MQ2028 and MQ4078 for one table: CheckLinks, the facet table below, the columns kept members use, and
    // facets equal to the inherited value. The validator (table files) and the resolver (overlays) both call it.
    public static IReadOnlyList<Diagnostic> CheckLinks(TableMemberSet set, Table file); }
```

Inside a member set every column reference is already keyed: a contributed index on column `C` lists
`<tableKey>.C`. A member set is a pure function of the snapshot, the table and `skip`, with no resolve, and is cached
per snapshot and (table key, skip).

#### Schema and files

Every file changed under `schemas/v1/` gets its byte-identical copy under
`samples/reference-app/.maquettiste/.schema/v1/` in the same task. `docs/api/openapi.yaml` references the kind schemas
and `batch.json`, so every task that changes one regenerates the editor's types and mocks (`editor`) and owns them.

- `common.json` gains `$defs` `tableColumn`, `primaryKey`, `uniqueConstraint`, `foreignKey`, `checkConstraint` and
  `tableIndex`, moved verbatim from `table.json`'s inline items, which now `$ref` them. `primaryKey` gains an optional
  `id` (first in its `x-order`); a primary key with an id is a `key` sub-element in the index, as uniques already are.
- `stereotype.json`: after `storage` in `x-order`, `columns` (items `tableColumn`), `primaryKey` (`$ref` `primaryKey`
  with `"required": ["id", "columns"]`), `uniques`, `foreignKeys`, `checks`, `indexes` (each `[]` by default).
  `foreignKeys` items are their own object: `x-order` `id`, `columns`, `referencesTable`, `referencesParameter`,
  `referencesColumns`, `period`, `onDelete`, `onDeleteColumns`, `onUpdate`, `deferrable`; required `id` and `columns`.
- `table.json`: `excludeStereotypes` after `stereotypes`; `redefines` and `excludes` after `indexes`. `redefines` items:
  `x-order` `member`, `name`, `nullable`, `default`, `defaultSql`, `defaultName`, `comment`, `collation`, `columns`,
  `clustered`, `nullsNotDistinct`, `onDelete`, `onDeleteColumns`, `onUpdate`, `deferrable`, `where`, `method`,
  `storage`; required `member`; `minProperties` 2; `additionalProperties` false; the array is `x-sort` `member`.
  `excludes` is an id list with `x-sort` `.` and `uniqueItems`.
- `database.json`: a schema entry gains `defaultStereotypes` (`keyList`, after `stereotypes`).
- `package.json`: `defaultStereotypes` after `dependsOn`.
- Every kind schema with a top-level `stereotypes` list except `migration.json` gains `excludeStereotypes` (`keyList`,
  after `stereotypes`). Every such kind has a package (or, for a package, a parent), so a default can reach it. A
  migration fact has no package and takes no defaults; leaving the key off keeps the fact content hash and the ledger
  contract untouched.
- `batch.json`: the operation `fold-into-stereotype` with `id` (the stereotype), `tables` or `schema`, `redefine`,
  `excludeMissing` and `expectedHashes` (Fold below). `id` is required for it, as for the other expanded operations
  that act on one element; the descriptions of `id`, `tables` and `schema` name the new use.
- `migration.json`: the rekey item's `table` description says it is given for a column, index, unique, foreign-key or
  check entry. Nothing structural changes.
- `seed.json` (D10, decision 27): `columns` items and `key` also take the keyed id `<tableId>.<memberId>` of an
  inherited column or unique.

Facets a redefinition may state, by member kind (MQ2027 refuses others):

| Member | Facets |
| --- | --- |
| Column | `nullable`, `default`, `defaultSql`, `defaultName`, `comment`, `collation` |
| Primary key | `columns`, `name`, `clustered` |
| Unique | `columns`, `name`, `nullsNotDistinct` |
| Foreign key | `name`, `onDelete`, `onDeleteColumns`, `onUpdate`, `deferrable` |
| Index | `name`, `where`, `method`, `storage` |
| Check | `name` |

Setting `default` or `defaultSql` replaces the inherited default of either form. A key's `columns` lists the whole new
list: own column ids and inherited keyed ids. An index's `storage` replaces the inherited map.

Billing terms. `audited` declares the two columns and an index (ids invented):

```json
{ "kind": "stereotype", "id": "01J92P0V25BX5TMQ8N9F6AJE61", "key": "audited", "name": "Audited",
  "appliesTo": ["entity", "table", "column"],
  "columns": [
    { "id": "01JC7AVD1TC0000000000000C1", "name": "created_at", "type": "datetimeoffset", "nullable": false,
      "defaultSql": { "*": "CURRENT_TIMESTAMP" } },
    { "id": "01JC7AVD1TC0000000000000C2", "name": "updated_at", "type": "datetimeoffset" } ],
  "indexes": [ { "id": "01JC7AVD1TC0000000000000X1", "columns": [ { "column": "01JC7AVD1TC0000000000000C1" } ] } ] }
```

One line on the schema entry of database `main` applies it to every table of schema `billing`:
`{ "id": "01J92P0V1RC04SKQ5353EAKHG2", "name": "billing", "defaultStereotypes": ["audited"] }`. The tables of
Customer, Invoice and Payment, whose entities carry `audited`, keep storing its attributes and inherit no member. An
overlay for Product's table states a nullable exception with
`"redefines": [{ "member": "01JC7AVD1TC0000000000000C1", "nullable": true }]`; its inherited column keeps the key
`01J92P0V0JR8BE8253SKT29ZG7@01J92P0V1QRN2181XM2ZWE02W4.01JC7AVD1TC0000000000000C1`. The `notes` table keeps its own
`created_at` with `"excludes": ["01JC7AVD1TC0000000000000C1", "01JC7AVD1TC0000000000000X1"]` (the index uses the
column, MQ2028) and inherits `updated_at` as `01K6BND0000000000000000001.01JC7AVD1TC0000000000000C2`.

A foreign key whose target each carrier names (an invented `child-row` stereotype):

```json
{ "kind": "stereotype", "id": "01JC7AVD1TC0000000000000S1", "key": "child-row", "name": "Child row",
  "appliesTo": ["table"],
  "parameters": { "properties": { "parentTable": { "type": "string",
    "x-ref": { "kind": "table", "sameDatabase": true } } }, "required": ["parentTable"] },
  "columns": [ { "id": "01JC7AVD1TC0000000000000C3", "name": "parent_id", "type": "uuid", "nullable": false } ],
  "foreignKeys": [ { "id": "01JC7AVD1TC0000000000000F1", "columns": ["01JC7AVD1TC0000000000000C3"],
    "referencesParameter": "parentTable", "onDelete": "cascade" } ] }
```

A designed table carrying it sets `"properties": { "parentTable": "01K6BND0000000000000000001" }`, and its inherited
key references `notes`' primary key.

#### Effective set with defaults

`ModelSnapshot.EffectiveStereotypes(Element)` builds the set from the element's holder; the key-list overload stays for
sub-elements and process nodes. `AppliedStereotype` gains `Source` (`Own`, `Implied`, `SchemaDefault`,
`PackageDefault`; a stereotype implied by a default has its default's source and `ImpliedBy` set) and `DefaultHolder`
(the schema entry id or package id whose list gives it). A default entry's `Entry` is -1; every reader that builds a
pointer from `Entry` points a default at `/stereotypes` (the file root when the file has no list).

The holder (`StereotypeHolder`, built by `Model/StereotypeDefaults.cs` from the documents before the index, so the
index's property walk can use it; `ModelSnapshot.HolderOf` caches it per element id):

- `Package`: the element's own (a package's is its parent), else the phase 1 fallback: a table overlay's entity's (a
  child overlay's owner entity's) or relation's, a seed's target's, a scenario's process's.
- `Schema`, for tables, views, sequences, routines, database types and SQL objects, by one rule,
  `StereotypeDefaults.SchemaOf`: the stated `schema`; else, for an entity table, the schema `DatabaseScope.SchemaFor`
  routes it to (`Resolution/DatabaseScope.cs:59-76`; an overload takes a parent lookup instead of a snapshot, so the
  holder needs no snapshot and the routing stays one walk); for a child table, its owner's; else the schema entry the
  database's `defaultSchema` names; an id that names no entry falls back to that entry too, as the resolver's
  `SchemaName` does (`Resolution/DatabaseRun.cs:392-393`). The resolver takes the holder of a table without a file from
  the same rule, by ids, so validation and resolution judge the same set; a test pins that both agree on every billing
  table.

Readers move to the element-aware set: the phase 1 4.2 reader table (`ResolveRun.StereotypesOf` and `FillAnnotations`,
`ValidationContext.StereotypesOf`, `ExtensionSet`, `UnitPlanner.Stereotypes`, `PropertySchemaSet.For` and `Sites`, the
classifier's carriers) plus `ModelStore.NamesToIds.cs:265` and `ResolveRun.ReferenceData.cs:69`. A shortcut on an
empty file list (`MergedProperties`, `ResolveRun.Conceptual.cs:876`) tests the effective set instead. Rows that mirror
files (`ModelIndexer.Summarize`, `ModelPages` and `model export --stereotype`) keep the file's list.
`UnitPlanner.Stereotypes` (`Planning/UnitPlanner.cs:750-761`) reads a table file's set today; it reads the resolved
table's stereotypes instead, so `where: { stereotypes: [audited] }` selects a synthesized table that a schema default
reaches.

Incremental keys:

- **Dependencies.** A resolved object whose holder has a package adds the `e:` key of every package on that package's
  chain, own first; a physical object already depends on its database's file, which holds the schema defaults. Without
  the chain, a package that gains its first default list would not re-render its members' units. The dependency keys of
  packaged objects change once (the engine version already invalidates unit state); no output changes.
  `RStereotype.Source` (`own`, `implied`, `schema-default`, `package-default`) tells templates where an entry came from.
- **The index.** The property walk applies a top-level host's schemas by its effective keys, defaults included. While
  the previous or the new model declares any default, the index patch builds in full when a package, database or
  mapping file changes, or an entity, relation or process whose package changed, since each can move the holder of a
  document the patch would keep (an overlay, a seed, a scenario), as the stereotype case does
  (`Model/ModelIndexer.cs:284-294`); decision 19 gives the exact rule. `PropertySchemaSet` keeps no snapshot: callers
  pass the keys (decision 18).

#### Resolution and the resolved model

`DatabaseRun` adds inherited members in `FinishTables`, after a table's own columns and overlay extras and before
positions and keys are computed (`Resolution/DatabaseRun.Finish.cs:15-35`), from `TableMembers.Of(model, table.Key,
effective, redefines, excludes, skip)`. The effective set comes from the table's holder: its file's own list,
exclusions and package (the fallback package for a table without a file), with its schema by `SchemaOf`. `skip` holds
each stereotype whose virtual attributes the table stores as columns (decision 1); the table still lists the
stereotype.

- A column becomes an `RColumn` exactly as a designed column does (`ApplyColumnFile`, facet defaults, native type), from
  the stereotype's column with the redefinition applied; `Id` and `Key` are the keyed id; `Attribute` is null; its
  annotations come from the member entry.
- Keys, indexes and checks become specs in the table's `TableBuild` and resolve as a file's do, so naming conventions,
  MQ4008 (a column that does not resolve), MQ4059, MQ4065, MQ4066 and MQ4068 apply unchanged. A contributed foreign
  key's target is `referencesTable`, else the table id in the carrier's merged `properties[referencesParameter]`.
- MQ4076 when a table gets a second primary key; the table's own (or its entity's) key stays.
- MQ4079 (a warning) when a synthesized table inherits a NOT NULL column with no default, identity or generation:
  rows written through its entity or relation (seeds, generated data access) have no value for it.
- For a synthesized table, MQ4003 when an inherited column's name equals another column's (an attribute column,
  another stereotype's); designed and imported tables get it from validation. MQ2013 to MQ2015 for a table without a
  file go on its entity or relation at `/stereotypes`, naming the defaults that clash.
- An overlay's links are judged here, where `skip` is known: `TableMembers.CheckLinks` gives MQ2027, MQ2028 and
  MQ4078 on the overlay, and an overlay key, index or check that names a member the table skips is MQ4008. They join
  `validate` through the resolver's findings, as MQ4008 for foreign keys does today.
- Placement of findings about an inherited member on one carrier: its `redefines` entry when it has one, else the
  contributing stereotype's own entry (`/stereotypes/<n>`), else `/stereotypes` (a default) or the file root; a table
  without a file reports on its entity or relation.

Resolved types gain `MemberOrigin` (non-null; own members share one instance with `Source` `own`). `RPrimaryKey` gains
`Id`. `RStereotypeDefinition` lists no members: a member resolves only on a carrying table, so `ResolvedCoverageTests`
excuses `columns`, `primaryKey`, `uniques`, `foreignKeys`, `checks` and `indexes` of `stereotype.json`, `redefines`
and `excludes` of `table.json`, and every `excludeStereotypes` and `defaultStereotypes`, with the reason "applied
through the effective set and inherited members (`MemberOrigin`, `RStereotype.Source`)".

#### Links: seeds, indexes, bindings, the reverse index and deletes

- **Index.** `redefines[].member` and `excludes` are `[ElementRef(IndexKinds = ["column", "key"])]`;
  `defaultStereotypes` and `excludeStereotypes` are `[StereotypeKeys]`. Keyed values naming inherited members already
  split into the table key and the member id (`Model/ModelIndexer.cs:859-867`), so `get_references` on a stereotype
  member lists every table, seed, binding, index or query that names an inherited copy. Package layering (MQ2020)
  treats such a keyed id like any reference: naming an inherited member references the stereotype's package, as
  listing the stereotype does.
- **Validation lookups** use the table's member set beside its own columns: constraint and index column lists
  (`PhysicalRules.cs:116`), seed columns and MQ7109 for a not-null inherited column without a default
  (`TableSeedRules.cs:45`, `:66`), reference bindings and MQ7120 (`ReferenceBindingRules.cs:28-29`), and MQ4003
  between own and inherited names. For an overlay the lookups take the candidate set, and the resolver reports what
  the table skips. A bare member id where a column is expected is MQ4008 with "write `<tableKey>.<memberId>`".
  `MemberRules` judges the links of designed and imported tables (MQ2027, MQ2028, MQ4078) through
  `TableMembers.CheckLinks`.
- **`x-ref` `of: "$element"`** on a table reaches its inherited columns and keys by keyed id; a bare member id of a
  column or key is out of scope (MQ2011). A stereotype's virtual attributes keep their bare ids on entities, as today.
  `sameDatabase` takes a keyed id's database from its table, not from the stereotype.
- **Bindings** need nothing: a field's `column` holds the keyed id, and the lookup finds it among the resolved columns
  (`DatabaseRun.Bindings.cs:300-304`). MQ4047 counts inherited columns like own ones.
- **Removing a member** from a stereotype (an update): the planner drops seed columns whose keyed value's member part
  (`ContributedMembers.TryParse`) is the removed id, and removes the `redefines` and `excludes` entries that name it;
  other keyed references (indexes, foreign keys, binding fields, queries) report MQ2001 as for any removed sub-element.
- **Deleting a stereotype**: its members are sub-element ids of the deleted element, so referrers refuse a plain delete.
  `remove-references` clears `stereotypes`, `defaultStereotypes` and `excludeStereotypes` entries, `redefines` and
  `excludes` entries and seed columns that name its members; it refuses where an index, key, binding field or query
  names an inherited key, which `delete-dependents` removes as it removes a deleted column's uses. The delete plan lists
  each.
- **Save scope.** `ModelValidator.ConflictPeers` (`Validation/ModelValidator.cs:417`) gains: a package whose default
  list changed brings the files of its members and its descendants' members; a database whose schema defaults changed
  brings the physical files of those schemas; a stereotype brings the files that carry it through a default.

#### Migration facts and the classifier

Members are captured like any column or key, so the classifier needs no new mapping for changes. What changes is which
databases a write chooses (`ModelStore.Migrations.cs:464-488`) and who counts as a carrier (`:492-518`):

- a package whose `defaultStereotypes` changed chooses every database holding a physical element whose package (own or
  fallback) is in its subtree, and the databases whose footprint or reach holds an element of that subtree;
- a stereotype also chooses every database one of whose schemas lists it, or a stereotype implying it, in
  `defaultStereotypes`, and what a change to each package that lists it chooses;
- `Carriers` treats a `defaultStereotypes` reference as its database or package, and skips `excludeStereotypes`.

Rekeys that move constraints, and undoing a fold:

- `ClassifierInput.MemberRekeys` carries the constraint moves of a fold in the same write; the write's one `rekey`
  lists them after its table's column entries, by kind then `from` (ordinal), each with `table`. MQ4106 takes `index`,
  `unique`, `foreign-key` and `check` entries and asks every member entry (`FactKeys.MemberKinds`) for its `table`.
  `migration fold` counts such an entry as its table and composes it like a column entry. `FactKeys.KeyMaps` and
  `RekeyMap` keep skipping them.
- An open `rekey` whose column moves a write reverses (the `to` key leaves its table and the `from` key comes back in
  the same table) is a key map too, as a table moving back is today. Restoring the files from before a fold (a model
  snapshot, say) then records the rekey back and no `drop`. Its constraint entries go back the same way, judged on the
  table files' constraint ids (decision 34).

Consequences in billing terms: adding a column to `audited` records nothing (added objects); removing one records a
pending `drop` per carrying table; renaming one records a complete `rename` per carrying table; a widening records
nothing; a narrowing records a pending `change-type` per table, and each follows its column as phase 1 built. An
`excludes` entry is a `drop` for that one table. A redefinition that makes a column not null is a `backfill`.

#### Materialize

`materialize-tables` writes the designed table with the projection's shape, members included, without copying them:

- An inherited column maps to `<tableId>.<memberId>` in the materialize's key map (`TableRekey`) instead of taking a new
  id, and the table file leaves inherited columns, keys, indexes and checks out. The overlay's `redefines`, `excludes`
  and `excludeStereotypes` move into the table with its annotations. The table states the package whose defaults
  reached the projection (decision 37), so its effective set stays the same.
- A stereotype the synthesized table skipped (decision 1) that reaches the designed table (the overlay listed it, or a
  default reaches the table) gets an `excludes` entry for each of its members, since the table now holds those
  attributes as own columns. The database sees nothing; a later fold adopts the members.
- `materialize-entities` and `materialize-attributes` treat an inherited column like an own one: an attribute per
  column, its binding field naming the keyed id. `materialize-columns` adds no column for an attribute whose column name
  an inherited column already has; it maps the attribute to that keyed id (an authoring action that writes an id).

#### Fold into a stereotype

```json
{ "op": "fold-into-stereotype", "id": "01J92P0V25BX5TMQ8N9F6AJE61",
  "schema": "01J92P0V1RC04SKQ5353EAKHG2", "redefine": true, "excludeMissing": true }
```

`id` names the stereotype (an id, a qualified name or a placeholder, as every id field of a batch); exactly one of
`tables` (table ids or names) and `schema` (a schema id: every designed or imported table file of its database whose
stated schema is it, or none stated when it is the default). `redefine` and `excludeMissing` default to false;
`expectedHashes` works as for materialize. The fold plans against the model as loaded, as every expanded operation
does; another operation of the batch that writes the stereotype or a target table refuses it.

Per target table:

1. **Carry.** When the stereotype is not in the table's effective set, its key joins the table's `stereotypes`. A
   table that excludes it (`excludeStereotypes`) is refused.
2. **Match columns by name.** Each member column finds the own column with the same `name` (ordinal); a member the
   table excludes is matched too, and a match removes it from `excludes`. No match: with `excludeMissing` the member
   joins (or stays in) `excludes`, else the table is refused ("would add column `updated_at`"). A target where nothing
   matches and the stereotype was not already effective is skipped.
3. **Match keys, indexes and checks structurally**, after mapping folded column ids to their keyed ids: same kind and
   the properties that form a capture key (`SchemaDiff/SnapshotCapture.cs:13-30`); a contributed foreign key's target
   is the carrier's parameter value. A match removes the own constraint and moves its id; no match is excluded or
   refused as in step 2. An own constraint that matches nothing stays own. An own primary key has no id, so its fold
   moves none.
4. **Rewrite references.** Every site the reverse index lists for a moved id (indexes, keys and partitioning of any
   table, other tables' foreign keys, seed `columns` and `key`, binding fields, constants, write and delete columns,
   reference bindings, `x-ref` sites) gets the keyed id. Query column texts (`alias.<id>`) go through
   `Loading/QueryColumnRewrite.cs`, moved out of `Materializer.RewriteReferences` (`Loading/Materializer.cs:469`) so
   both share it. Then the own entries leave the table file.
5. **Verify on captures.** The databases of the targets resolve on the candidate and are captured. Each target's capture
   must equal its before capture moved through the fold's key map, column positions aside. A difference in a type facet
   (type, length, precision, scale, native type, subtype, identity, generation, computed, sequence, a reference column's
   storage, and a stable id, compared on the resolved columns: 3.4, decision 22) refuses the table. A difference in a
   redefinable facet writes a `redefines` entry with the own value (the file's, or for a comment the text the `comments`
   convention gave it) when `redefine` is set, else refuses the table; the fold then captures once more to confirm. An
   own column or constraint whose entry carries annotations its member lacks (description, tags, category, stereotypes,
   properties, rationale, decision, status, display names) is refused too, so the fold never drops documentation: move
   it to the stereotype first.

Refusals are MQ4077 at the operation, one per refused table, and the batch writes nothing. The write hands the
classifier the fold's key maps (`TableRekey` with an unchanged table key, and the constraint moves as `MemberRekeys`),
so it records one `rekey` per database and nothing else. A dry run returns the files, the rekey it would record, and
the refusals. The next migration holds no DDL statement, only the rekey's fact row in the ledger; `schema.sql` may list
a folded column later in its table (the schema diff matches columns by key, so column order is no database step).
Folding a synthesized table is not built: its columns come from attributes; materialize it first.

#### Rules

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ2024 | E | A stereotype's members do not fit: members on a stereotype whose `appliesTo` leaves out `table`, a contributed column with `attribute` or `sequence`, or a contributed key, index or check with a `name`. | Fix the stereotype |
| MQ2025 | E | A stereotype's key, index or check names a column the stereotype does not declare. | Name one of its columns |
| MQ2026 | E | A contributed foreign key's target is invalid: both or neither of `referencesTable` and `referencesParameter`; a `referencesParameter` that is not a required string parameter with `x-ref` kind `table` and `sameDatabase`; or `referencesColumns` beside `referencesParameter`. | Fix the key |
| MQ2027 | E | A `redefines` entry is invalid: it names a member the element does not inherit, names one twice, states no facet, or states a facet its member's kind does not take. | Fix or remove the entry |
| MQ2028 | E | An `excludes` entry names a member the element does not inherit or also redefines, or excludes a column that an inherited key, index or check the element keeps uses. | Exclude that member too |
| MQ2029 | E | `excludeStereotypes` names a stereotype the element lists in `stereotypes`, or one a listed stereotype implies; only defaults can be excluded. | Remove it from one list |
| MQ2030 | W | `excludeStereotypes` names a stereotype no schema or package default gives the element, so it excludes nothing. | Remove the entry |
| MQ2031 | E | A `defaultStereotypes` list names two stereotypes of one group, or two that conflict. | Fix the list |
| MQ4076 | E | A table gets two primary keys: an inherited one beside its own, its entity's key, or another stereotype's. | Exclude one, or redefine its columns |
| MQ4077 | E | A fold into a stereotype is refused: the stereotype does not apply to tables or has no members; a target is not a designed or imported table file, excludes the stereotype or is written by another operation of the batch; or a matched column differs in a type facet, in another facet without `redefine`, or carries annotations its member lacks, or the stereotype would add a member the table lacks without `excludeMissing`. | Fix the target, or set the option |
| MQ4078 | W | A redefinition states a facet equal to the value the member inherits, so the table stops following the stereotype for that facet. | Remove the facet |
| MQ4079 | W | A synthesized table inherits a NOT NULL column with no default, identity or generation, which rows written through its entity or relation cannot fill. | Give the column a default, or exclude it |

Changed text: MQ2013 "An element carries two stereotypes of one group, own, implied or from a schema or package
default." A finding about a default stereotype names the schema or package that gives it, and its quick fix adds the
key to `excludeStereotypes`. Changed behaviour, same text: MQ2011 (a bare member id of a column or key under
`of: "$element"`), MQ4003 (the resolver reports it for synthesized tables), MQ4008 (keyed ids, the keyed-id hint, an
overlay naming a skipped member) and MQ4106 (constraint entries in a rekey). An unknown key in a default list is MQ2003
at its entry; a key listed twice fails the schema (`keyList` has `uniqueItems`). MQ2018 covers a carrier without a
`referencesParameter` value. Ids MQ4080 and MQ4081 stay reserved. The catalog grows by 12.

#### CLI and MCP

No tool or verb is added; pinned tool counts do not move.

- `apply_batch` (MCP; its description is in `src/Maquettiste.Cli/Mcp/ModelTools.cs:196-197`) and `model apply` (CLI)
  take `fold-into-stereotype`, with `dryRun` listing the files, the rekey and the refusals.
- `get_database_view` and `get_resolved_model` (scopes `databases`, `tables`; both described in `Agent/tools.json`)
  carry `memberOrigin` on inherited columns, keys, foreign keys and indexes (null for own members), so an agent reads
  the keyed ids it must write. A synthesized table without an overlay now carries the stereotypes defaults give it;
  both descriptions and `docs/mcp.md` stop saying it has none.
- `get_references` on a stereotype member lists every keyed use; `validate` reports the new rules; `delete_element`'s
  plan lists member links.

#### Templates and sql-ddl

An inherited member sits in `table.columns`, `table.primary_key`, `table.uniques`, `table.foreign_keys`,
`table.checks` and `table.indexes` like any other, with `member_origin` (`source`, `stereotype`, `member_id`,
`redefined`). `has_stereotype` on a table sees defaults. sql-ddl renders inherited members with no template change:
names come from the conventions per table, comments from the member entry's description, migrations from the schema
diff by key. D13's pack test holds that; if it finds a gap, the fix and its reason go to `packs/sql-ddl/README.md`.

#### Public API

```csharp
namespace Maquettiste.Engine.Model;
public enum StereotypeSource { Own, Implied, SchemaDefault, PackageDefault }
public sealed record AppliedStereotype(Stereotype Stereotype, int Entry, string? ImpliedBy)
{   public StereotypeSource Source { get; init; } = StereotypeSource.Own;
    public string? DefaultHolder { get; init; } }                 // schema entry id or package id
public sealed record StereotypeHolder(ElementKind Kind, IReadOnlyList<string> Own, IReadOnlyList<string> Excludes,
    string? Package, string? Schema);                             // Schema: a schema entry id
public sealed class ModelSnapshot
{   public StereotypeHolder HolderOf(Element element);
    public IReadOnlyList<AppliedStereotype> EffectiveStereotypes(Element element);
    public IReadOnlyList<AppliedStereotype> EffectiveStereotypes(StereotypeHolder holder); }  // key-list one stays
public abstract record Element
{   [StereotypeKeys] public virtual IReadOnlyList<string>? ExcludeStereotypes { get; init; } }
// Migration overrides ExcludeStereotypes with [JsonIgnore] (always null): a fact takes no defaults (decision 16).
public sealed record Package { [StereotypeKeys] public IReadOnlyList<string> DefaultStereotypes { get; init; } = []; }
public sealed record DbSchema { [StereotypeKeys] public IReadOnlyList<string> DefaultStereotypes { get; init; } = []; }
public sealed record PrimaryKey { public string? Id { get; init; } }
public sealed record Stereotype
{   public IReadOnlyList<Column> Columns { get; init; } = [];
    public PrimaryKey? PrimaryKey { get; init; }
    public IReadOnlyList<UniqueConstraint> Uniques { get; init; } = [];
    public IReadOnlyList<MemberForeignKey> ForeignKeys { get; init; } = [];
    public IReadOnlyList<CheckConstraint> Checks { get; init; } = [];
    public IReadOnlyList<TableIndex> Indexes { get; init; } = []; }
public sealed record MemberForeignKey   // ForeignKey's members without Name, plus the parameter target
{   public required string Id { get; init; }
    [ElementRef(Keyed = true)] public required IReadOnlyList<string> Columns { get; init; }
    [ElementRef(Keyed = true)] public string? ReferencesTable { get; init; }
    public string? ReferencesParameter { get; init; }
    [ElementRef(Keyed = true)] public IReadOnlyList<string> ReferencesColumns { get; init; } = [];
    public bool Period { get; init; }  public ReferentialAction OnDelete { get; init; } = ReferentialAction.NoAction;
    [ElementRef(Keyed = true)] public IReadOnlyList<string> OnDeleteColumns { get; init; } = [];
    public ReferentialAction OnUpdate { get; init; } = ReferentialAction.NoAction;
    public Deferrability Deferrable { get; init; } = Deferrability.NotDeferrable; }
public sealed record Table
{   public IReadOnlyList<TableRedefinition> Redefines { get; init; } = [];
    [ElementRef(IndexKinds = ["column", "key"])] public IReadOnlyList<string> Excludes { get; init; } = []; }
public sealed record TableRedefinition   // implements the internal IMemberRedefinition
{   [ElementRef(IndexKinds = ["column", "key"])] public required string Member { get; init; }
    public string? Name { get; init; }  public bool? Nullable { get; init; }  public JsonElement? Default { get; init; }
    public IReadOnlyDictionary<string, string>? DefaultSql { get; init; }  public string? DefaultName { get; init; }
    public string? Comment { get; init; }  public string? Collation { get; init; }
    [ElementRef(Keyed = true)] public IReadOnlyList<string>? Columns { get; init; }
    public bool? Clustered { get; init; }  public bool? NullsNotDistinct { get; init; }
    public ReferentialAction? OnDelete { get; init; }
    [ElementRef(Keyed = true)] public IReadOnlyList<string>? OnDeleteColumns { get; init; }
    public ReferentialAction? OnUpdate { get; init; }  public Deferrability? Deferrable { get; init; }
    public string? Where { get; init; }  public IndexMethod? Method { get; init; }
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>>? Storage { get; init; }
    [JsonIgnore] public IReadOnlyList<string> Facets { get; } }      // the non-null facets' JSON names, ordinal
// IMemberRedefinition, InheritedMember<T>, MemberLinkProblem, MemberLinkFinding and ContributedMembers are internal.

namespace Maquettiste.Engine.Resolution;
public sealed class RMemberOrigin
{   public string Source { get; internal set; }                 // own | stereotype | schema-default | package-default
    public RStereotype? Stereotype { get; internal set; }       // null for own
    public string? MemberId { get; internal set; }              // the stereotype member's id; null for own
    public IReadOnlyList<string> Redefined { get; internal set; } }   // facet names the table states, ordinal
// RColumn, RPrimaryKey, RUnique, RForeignKey, RCheck, RIndex gain: RMemberOrigin MemberOrigin { get; internal set; }
// RPrimaryKey gains: string? Id. RStereotype gains: string Source (own | implied | schema-default | package-default).

namespace Maquettiste.Engine;   // Editor/DatabaseViews.cs
public sealed record MemberOriginView(string Source, string? Stereotype, string? MemberId,
    IReadOnlyList<string> Redefined);
// ColumnView, KeyView, ForeignKeyView and IndexView gain the init property MemberOriginView? MemberOrigin (null: own).
// BatchOp gains FoldIntoStereotype ("fold-into-stereotype"); BatchOperation gains the trailing optional parameters
// bool? Redefine and bool? ExcludeMissing (Id, Tables, Schema and ExpectedHashes are reused).
```

All new members carry XML documentation. `ModelIndexer.IdOf` and `SubElementKind` add `MemberForeignKey` and an
id'd `PrimaryKey` as `key`. Internally, `MaterializeResult` gains `MemberRekeys` (the constraint moves a fold hands the
classifier) and `ClassifierInput` gains `MemberRekeys`, `ConstraintsBefore` and `ConstraintsAfter` (decision 34).

#### Editor follow-ups

The generic JSON form covers every new key in the meantime. Deferred screens:

- Table inspector: inherited members read-only, each with its source (stereotype, schema default or package default) as
  a link, a "Redefine here" action that writes a `redefines` entry with the chosen facet, and "Exclude here".
- Database schema and package inspectors: a `defaultStereotypes` picker; an element's "Exclude default" action.
- Stereotype editor: member lists, with the parameter picker for a foreign key's target.
- A fold dialog over the batch operation's dry run, showing refusals and the redefinitions it would write.

#### Docs

User guide: a new section "Stereotypes that contribute structure" after "Stereotype rules and parameters" (members,
keyed ids, defaults, `excludeStereotypes`, `redefines`, `excludes`, the facet table, materialize, facts).
`docs/guides/schema-changes.md`: "Fold columns into a stereotype", with the rekey, its constraint entries, and why the
migration has no DDL. `docs/mcp.md`: the `apply_batch` operation, `memberOrigin` and default stereotypes on database
views. `skills/maquettiste-modeling/SKILL.md` (embedded in the engine and the CLI; `ShippedSkills.cs` is regenerated
only at a release tag): inherited members are written as `<tableKey>.<memberId>`, changed on the stereotype or with
`redefines` and `excludes`, never copied as own columns. `docs/releases.md` under `## 0.12.0 (unreleased)`.
`docs/engineering/engine-design.md` sections 7.1 (column order) and 7.3 (keyed ids of inherited members); the engine
READMEs of `Model/`, `Resolution/` and `Migrations/`.

**Not doing**

- A separate mixin kind (scope 10).
- Redefinition by restating a whole member, type facets or a column's name in a redefinition, or removing an inherited
  default through one. Exclude the member and declare an own column.
- Members that name another stereotype's or the table's own columns, and contributed exclusion constraints,
  partitioning or partitions (partitioning stays per table; a key's `columns` redefinition covers partitioned tables).
- Checking a stereotype's SQL texts (check expressions, index predicates) against its columns; no SQL is parsed.
- A self-referencing contributed foreign key: a carrier names itself in the parameter.
- A column placement facet: inherited columns follow own columns.
- Per-table annotations (description, tags, properties) on inherited members: they come from the member entry.
- A link from a contributed column to a stereotype attribute. A synthesized table that stores the attributes skips the
  members (decision 1); a designed table takes the members, and its binding maps the attributes to them.
- Database-level and project-level default stereotypes.
- One rename fact for all carriers (decision 11); folding a synthesized table; an unfold action (restoring the files
  is the undo, and records the rekey back).
- Editor screens (follow-ups above).

**Tests**

- `ContributedMembersTests` (new): `Inherit_keys_members_by_holder_and_member_id`,
  `Excluded_and_skipped_members_are_left_out`, `A_redefinition_applies_only_its_facets_and_lists_them`,
  `Order_follows_the_effective_set_then_member_order`, `TryParse_splits_at_the_last_dot_only_before_an_id`,
  `CheckLinks_reports_each_problem_at_its_entry`.
- `TableMembersTests` (new): `A_designed_table_inherits_columns_and_keys_with_keyed_ids`,
  `Member_column_lists_become_keyed_ids`, `An_overlay_uses_the_synthesized_table_key`,
  `Member_sets_are_cached_per_snapshot_table_and_skip`, `CheckLinks_refuses_a_column_name_facet`.
- `StereotypeDefaultsTests` (new): `Package_defaults_run_outermost_first`, `A_seed_takes_its_targets_package`,
  `An_overlay_takes_the_schema_its_entity_is_routed_to`; after D3,
  `Holder_schema_matches_the_resolvers_schema_for_every_billing_table`.
- `ModelSnapshotTests`: `Schema_and_package_defaults_join_the_effective_set_in_layers`,
  `Exclude_stereotypes_removes_a_default_and_its_implied_ones`, `A_default_skips_kinds_it_does_not_apply_to`,
  `A_later_layer_keeps_the_last_place_of_a_key`, `Without_defaults_the_set_is_unchanged`,
  `A_primary_key_with_an_id_is_a_key_sub_element`, `Redefines_and_excludes_are_references_to_stereotype_members`,
  `Default_and_exclude_lists_are_references_to_stereotypes`.
- `SchemaConsistencyTests.Table_members_validate_alike_after_the_move_to_common`.
- `PropertySchemaSetTests.A_package_default_types_the_properties_of_its_members`.
- `ModelIndexerPatchTests.A_package_default_change_builds_the_index_in_full_and_matches_a_full_build`.
- `UnitPlannerTests.Where_stereotypes_matches_a_table_that_a_schema_default_reaches`.
- `ResolverBehaviorTests`: `A_schema_default_reaches_a_table_without_a_file`,
  `A_default_entry_points_at_the_stereotypes_list`, `Inherited_columns_follow_own_columns_with_their_member_origin`,
  `A_redefined_column_keeps_its_key_and_lists_its_facets`, `A_contributed_foreign_key_targets_the_carriers_parameter`,
  `A_carrier_without_the_parameter_gets_no_key_and_no_second_finding`, `A_second_primary_key_is_MQ4076`,
  `A_synthesized_table_skips_the_members_whose_attributes_it_stores`,
  `A_tph_root_table_skips_the_members_a_derived_entity_stores_through_attributes`,
  `An_inherited_column_named_like_another_stereotypes_attribute_column_is_MQ4003`,
  `An_overlay_link_to_a_skipped_member_is_MQ2027_from_the_resolver`,
  `A_not_null_member_without_a_default_on_a_synthesized_table_is_MQ4079`.
- `DependencyKeyTests.A_package_that_gains_a_default_list_re_renders_its_members`.
- `StereotypeRuleTests`: a failing and a passing case for MQ2029 to MQ2031, and
  `A_clash_with_a_default_names_its_holder_and_offers_an_exclusion`.
- `StereotypeMemberRuleTests` (new): a failing and a passing case for MQ2024 to MQ2028 and MQ4078 on a designed table,
  and `A_bare_member_id_in_an_index_is_MQ4008_with_the_keyed_form`.
- `TableSeedTests.A_seed_names_an_inherited_column_by_its_keyed_id`,
  `XrefRuleTests.Element_scope_takes_an_inherited_column_by_keyed_id_only`.
- `FactRecordingTests`: `Selection_covers_the_databases_a_package_default_reshapes` (D4),
  `Renaming_a_stereotype_column_records_one_rename_per_carrier_and_writes_no_table_file`,
  `Excluding_a_member_records_a_drop_for_that_table` (D7), `Folding_two_rekeys_keeps_their_constraint_entries` (D17).
- `FactClassifierTests`: `Member_rekeys_join_the_writes_one_rekey_and_move_no_capture_key`,
  `Undoing_a_fold_records_the_rekey_back_and_no_drop`.
- `MigrationRulesTests.A_rekey_takes_constraint_entries_that_name_their_table`.
- `ModelStoreTests.A_save_that_adds_a_package_default_judges_its_members` (`Store/ModelStoreTests.cs`),
  `ReferenceDataStoreTests.Removing_a_stereotype_column_drops_its_keyed_seed_columns_and_member_links`.
- `ModelStoreDeleteTests.Deleting_a_stereotype_lists_member_links_and_inherited_keys_as_referrers`,
  `DeleteCascadeTests.Remove_references_clears_default_and_exclude_lists`.
- `MaterializeTests`: `Materialize_tables_keeps_inherited_members_and_moves_their_keys`,
  `Materialize_tables_carries_the_overlays_redefines_and_excludes`,
  `Materialize_tables_excludes_the_members_a_stereotype_realizes_through_attributes`,
  `Materialize_tables_keeps_the_package_whose_default_reaches_the_projected_table` (decision 37),
  `Materialize_columns_maps_an_attribute_to_an_inherited_column_of_its_name`; the rest unchanged (the query rewrite
  moved).
- `DatabaseViewTests.Member_origin_is_null_for_own_members_and_set_for_inherited`,
  `DatabaseViewTests.A_table_without_a_file_shows_its_default_stereotypes`.
- `StereotypeFoldTests` (new): `Fold_removes_own_columns_and_rewrites_references_to_keyed_ids`,
  `Fold_records_one_rekey_per_database_with_constraint_moves`, `Fold_refuses_a_type_difference_with_MQ4077`,
  `Fold_writes_redefinitions_with_redefine`, `Fold_excludes_missing_members_with_exclude_missing`,
  `Fold_adopts_an_excluded_member_that_matches`, `Fold_skips_tables_that_match_nothing`,
  `Fold_refuses_a_synthesized_overlay`, `Fold_refuses_when_another_operation_writes_a_target`,
  `Fold_dry_run_writes_nothing_and_lists_the_rekey`, `Fold_is_byte_identical_with_the_sequential_id_generator`.
- `McpReadTests.Get_database_view_shows_inherited_members`, `McpWriteTests.Apply_batch_folds_into_a_stereotype`,
  `ModelApplyTests.Model_apply_folds_into_a_stereotype_with_a_dry_run`.
- `StereotypeMembersDdlTests` (Packs, new): `Inherited_members_render_like_own_members`,
  `Inherited_constraint_names_follow_each_table`, and, with PostgreSQL,
  `A_migration_from_a_widened_stereotype_column_applies_to_every_carrier`.
- `GoldenTests` (engine and packs) unchanged: billing declares no members and no defaults.

Acceptance tests copy the billing fixture into a temporary folder; the shared fixture does not change. All live in
`Integration/StereotypeMembersTests` (new). "AT1's copy" is: `audited` keeps its attributes and gains the `columns`
and index of the example above; schema `billing` gets `"defaultStereotypes": ["audited"]`; `notes` gets
`"excludes": ["01JC7AVD1TC0000000000000C1", "01JC7AVD1TC0000000000000X1"]`. The billing queries that select
`createdAt` keep working, since the attributes stay.

- **First line:** `Audited_columns_reach_every_table_of_schema_billing_from_one_line`. Setup: AT1's copy. Expected:
  all seven tables of schema billing have `created_at` and `updated_at`. customers, invoices and payments store them
  from their entities' attributes and inherit no member. products, invoice_lines and payment_invoice inherit both and
  the index, keyed `<table key>.<member id>` with `MemberOrigin.Source` `schema-default`; notes keeps its own
  `created_at` and inherits `updated_at`. The view `outstanding_invoices` and the sequence get nothing and no MQ2004;
  validate reports no error and no MQ4079.
- **Second line:** `Invoice_line_opts_out_in_one_line`. Setup: AT1's copy plus an overlay file
  (`01JC7AVD1TC0000000000000T1`: database main, origin `synthesized`, entity InvoiceLine) whose one other key is
  `"excludeStereotypes": ["audited"]`. Expected: invoice_lines has neither column, nor the index, nor the stereotype;
  the other tables keep theirs; no MQ2030.
- **Third line:** `A_nullable_exception_states_only_nullable_and_keeps_the_key`. Setup: AT1's copy plus an overlay
  file (`01JC7AVD1TC0000000000000T2`, entity Product) with the `redefines` entry of the example. Expected:
  products.created_at is nullable, with the key
  `01J92P0V0JR8BE8253SKT29ZG7@01J92P0V1QRN2181XM2ZWE02W4.01JC7AVD1TC0000000000000C1` it had without the entry,
  `Redefined` `["nullable"]`, and `timestamptz(6)` and `CURRENT_TIMESTAMP` from `audited`. After `audited`'s
  `created_at` gains `precision: 3`, products follows. Removing the entry changes no key and no seed.
- **Fourth line:** `A_stereotype_column_type_change_changes_every_table_in_one_plan`. Setup: AT1's copy with
  `updated_at` at `precision: 3`, generated once so the committed snapshot holds it; then `updated_at` goes to
  `precision: 6`. Expected: one `plan` whose one migration for main alters `updated_at` in products, invoice_lines,
  payment_invoice and notes, in one schema diff; no table file changes; no migration fact (a widening).
- **Fifth line:** `Renaming_the_stereotype_column_changes_no_table_file`. Setup: AT1's copy, generated once; then
  `updated_at` is renamed `modified_at` in `audited`'s columns. Expected: the write touches `audited` and four new fact
  files only; each is a complete `rename` of kind `column` whose `key` is that table's inherited key; the next
  migration renames the column in those four tables.
- **Sixth line:** `Folding_the_audit_columns_of_billing_plans_no_database_step`. Setup: a billing copy where `audited`
  keeps its attributes and gains the two columns only (no index, no default on `created_at`, types as its attributes);
  then one batch materializes main's entity tables (`materialize-tables` for Customer, Invoice, InvoiceLine, Payment
  and Product), and the model is generated once. invoices, whose overlay lists `audited`, gets `excludes` for both
  members. Then the fold operation of the example (schema billing, `redefine` and `excludeMissing` true). Expected:
  customers, invoices and payments lose their own audit columns and list `audited`; invoices' `excludes` are gone;
  their bindings' fields name the keyed ids; notes folds `created_at` with a `defaultSql` redefinition and excludes
  `updated_at`; products and invoice_lines are skipped; one `rekey` fact for main lists the seven column moves; the
  next migration holds no DDL statement, only that fact's row in the ledger; validate reports no error.

**Tasks**

Cross-section order. Within milestone D, a task of sections 3.2 to 3.5 that owns a file a task here owns runs after
it. The direct ones: 3.2's column keys go into `common.json#/$defs/tableColumn`, so 3.2's schema task runs after D1
and D6 (`common.json`, `table.json`, `Physical.cs`); its resolution and view task after D9 (`ResolvedPhysical.cs`,
`DatabaseRun.cs`, `DatabaseViews.cs`, `openapi.yaml`); its materialize task after D11 (`Materializer.cs`); its pack task
after D13 (`packs/sql-ddl/README.md`). Section 4.1's shared errors run after D6, and 4.2's change to `stereotype.json`
runs after D6.

- **D1. Shared member schemas.** Owns: `schemas/v1/common.json` (the six `$defs`, `primaryKey.id`),
  `schemas/v1/table.json` (members by `$ref`), their copies, the regenerated editor types and mocks,
  `Model/Physical.cs` (`PrimaryKey.Id`), `Model/ModelIndexer.cs` (an id'd primary key is a `key`),
  `ResolvedCoverageTests` (`Pending` for the primary key's `id` until D7). Tests: `SchemaConsistencyTests`, the
  `ModelSnapshotTests` primary key case. Verify: `E "~Json.|~Model.|~ValidationFixtureTests|~ResolvedCoverageTests"`;
  `P "~GoldenTests"`; `editor`; `copies`.
- **D2. Defaults, exclusions and the effective set.** Runs after D1. Owns: `excludeStereotypes` in every kind schema
  that has `stereotypes` except `migration.json`, `defaultStereotypes` in `package.json` and `database.json`, their
  copies, the regenerated editor types and mocks; `Model/ElementBase.cs`, `Model/Conceptual.cs`
  (`Package.DefaultStereotypes`), `Model/Physical.cs` (`DbSchema.DefaultStereotypes`), `Model/Vocabularies.cs`
  (`StereotypeSource`, `AppliedStereotype` members), `Model/StereotypeDefaults.cs` (new: holders, `SchemaOf`), the
  parent-lookup overload of `SchemaFor` in `Resolution/DatabaseScope.cs`, `Model/ModelSnapshot.cs` (`HolderOf`, both
  new overloads), `ResolvedCoverageTests` (every `excludeStereotypes` and `defaultStereotypes` in `NotResolved`).
  Tests: the `StereotypeDefaultsTests` cases except the resolver one, the `ModelSnapshotTests` default and exclusion
  cases. Verify: `E "~Model.|~Json.|~ResolvedCoverageTests|~ResolverBehaviorTests"`; `editor`; `copies`.
- **D3. Readers of the effective set.** Runs after D2. Owns: `Resolution/ResolveRun.Conceptual.cs`,
  `Resolution/ResolveRun.ReferenceData.cs`, `Resolution/ResolvedCore.cs` (`RStereotype.Source`),
  `Resolution/DatabaseRun.cs` (`AddTable`: the holder of every table by `SchemaOf`, storage and annotations for tables
  without a file, the package chain in dependency keys), `Resolution/TableBuild.cs` (the table's effective set),
  `Validation/ValidationContext.cs`, `Validation/ExtensionRules.cs` (top-level hosts), `Model/PropertySchemas.cs`
  (`For` and `Sites` take effective keys), `Model/ModelIndexer.cs` (the property walk's host keys, the full-build rule)
  and its `Build` call in `Model/ModelSnapshot.cs`, `Planning/UnitPlanner.cs`, `ModelStore.NamesToIds.cs`. Tests: the
  first two `ResolverBehaviorTests` cases, the resolver case of `StereotypeDefaultsTests`, `DependencyKeyTests`,
  `PropertySchemaSetTests`, `ModelIndexerPatchTests`, `UnitPlannerTests`. Verify:
  `E "~Model.|~Resolution.|~Planning.|~Validation.|~ExtensionFilesTests|~NamesToIdsTests|~FactRecordingTests"`;
  `P "~GoldenTests"`.
- **D4. Defaults in the classifier and the save scope.** Runs after D3. Owns: `ChooseDatabases` and `Carriers` in
  `ModelStore.Migrations.cs`, `Migrations/DatabaseReach.cs`, `ConflictPeers` in `Validation/ModelValidator.cs`. Tests:
  `FactRecordingTests.Selection_covers_the_databases_a_package_default_reshapes`,
  `ModelStoreTests.A_save_that_adds_a_package_default_judges_its_members`. Verify:
  `E "~FactRecordingTests|~ModelStoreTests|~ScopedValidationTests|~FactClassifierTests"`.
- **D5. Rules for defaults and exclusions.** Runs after D4. Owns: the stereotype checks in `Validation/BuiltinRules.cs`
  (`CheckStereotypes` over an element's holder, pointers and quick fixes for defaults, MQ2029 to MQ2031, MQ2003 in
  default lists), `RuleCatalog.cs` (MQ2029 to MQ2031, MQ2013's text), `CatalogTests.cs` (+3). Tests: the
  `StereotypeRuleTests` cases. Verify: `E "~CatalogTests|~StereotypeRule|~ValidationFixtureTests|~ProcessRuleTests"`;
  `C "~McpSurfaceTests"`; `record`.
- **D6. Contributed members in the model.** Runs after D3. Owns: `Model/ContributedMembers.cs` (new),
  `Model/TableMembers.cs` (new: member sets, the facet table, `CheckLinks`), `Model/Vocabularies.cs` (`Stereotype`
  members, `MemberForeignKey`), `Model/Physical.cs` (`Table.Redefines`, `Table.Excludes`, `TableRedefinition`),
  `Model/ModelIndexer.cs` (`MemberForeignKey`), `schemas/v1/stereotype.json` and `schemas/v1/table.json` (members,
  `redefines`, `excludes`) with their copies, the regenerated editor types and mocks, `ResolvedCoverageTests` (the
  member, `redefines` and `excludes` entries). Tests: `ContributedMembersTests`, `TableMembersTests`, the
  `ModelSnapshotTests` reference cases. Verify: `E "~Model.|~Json.|~ResolvedCoverageTests"`; `editor`; `copies`.
- **D7. Resolution of inherited members.** Runs after D5 and D6. Owns: `Resolution/DatabaseRun.Members.cs` (new:
  members, `skip`, parameter targets, MQ4076, MQ4079, MQ4003 for synthesized tables, MQ2013 to MQ2015 for tables
  without a file, overlay links and MQ4008 for an overlay naming a skipped member), `Resolution/DatabaseRun.Finish.cs`
  (the call, origins onto results), `Resolution/TableBuild.cs` (specs carry an origin), `Resolution/ResolvedPhysical.cs`
  (`RMemberOrigin`, `MemberOrigin`, `RPrimaryKey.Id`), `ResolvedCoverageTests` (the primary key's `id` leaves
  `Pending`), `RuleCatalog.cs` (MQ2027, MQ2028, MQ4076, MQ4078, MQ4079), `CatalogTests.cs` (+5). Tests: the other
  `ResolverBehaviorTests` cases, the D7 `FactRecordingTests` cases. Verify:
  `E "~Resolution.|~CatalogTests|~FactRecordingTests"`; `P "~GoldenTests"`; `C "~McpSurfaceTests"`; `record`.
- **D8. Validation of members and links.** Runs after D7. Owns: `Validation/MemberRules.cs` (new: MQ2027, MQ2028 and
  MQ4078 on designed and imported table files through `TableMembers.CheckLinks`), its call in
  `Validation/ModelValidator.cs`, the member checks (MQ2024 to MQ2026) and the `$element` scope in
  `Validation/BuiltinRules.cs`, `Validation/PhysicalRules.cs` (member sets in `Resolves` and MQ4003, the keyed-id hint),
  `Validation/TableSeedRules.cs`, `Validation/ReferenceBindingRules.cs`, `RuleCatalog.cs` (MQ2024 to MQ2026),
  `CatalogTests.cs` (+3); found in D8, `Loading/SeedInput.cs` (decision 32). Tests: `StereotypeMemberRuleTests`,
  `TableSeedTests`, `XrefRuleTests`. Verify:
  `E "~CatalogTests|~Validation.|~TableSeedTests|~ReferenceBindingTests|~XrefRuleTests"`; `C "~McpSurfaceTests"`;
  `record`.
- **D9. Views and the HTTP contract.** Runs after D7 and D17. Owns: `Editor/DatabaseViews.cs`, `docs/api/openapi.yaml`,
  the regenerated editor types and mocks, the `get_database_view` and `get_resolved_model` descriptions in
  `Agent/tools.json` and their rows in `docs/mcp.md`. Tests: `DatabaseViewTests`,
  `McpReadTests.Get_database_view_shows_inherited_members`. Verify: `E "~Editor."`;
  `C "~McpReadTests|~McpSurfaceTests"`; `editor`.
- **D10. Planner links on edit and delete.** Runs after D6. Owns: `Loading/ChangePlanner.cs` (`DropSeedColumns` on
  keyed values, member links of removed members), `Loading/ChangePlanner.Cascade.cs` (the stereotype delete),
  `DeletePlan.cs`, `ModelStore.DeletePlan.cs`; found in D10, `schemas/v1/seed.json` with its copy and the regenerated
  editor types and mocks (decision 27). Tests: the `ReferenceDataStoreTests` case, `ModelStoreDeleteTests`,
  `DeleteCascadeTests`. Verify: `E "~Store.|~DeleteCascadeTests"`; `editor`; `copies`.
- **D11. The fold.** Runs after D8, D9, D10, D16 and D17. Owns: `ModelStore.StereotypeFold.cs` (new),
  `Loading/StereotypeFold.cs` (new), `Loading/QueryColumnRewrite.cs` (new), `RewriteReferences` in
  `Loading/Materializer.cs` (calls it), `Loading/BatchParser.cs`, `ModelStore.cs` (`BatchOp`, `BatchOperation`,
  `IsExpandedOperation`, the fold's key maps into the recording request), `ModelStore.Schemas.cs` (dispatch),
  `schemas/v1/batch.json` with its copy, the regenerated editor types and mocks, `RuleCatalog.cs` (MQ4077),
  `CatalogTests.cs` (+1). Tests: `StereotypeFoldTests`. Verify:
  `E "~StereotypeFoldTests|~MaterializeTests|~FactClassifierTests|~FactRecordingTests|~CatalogTests|~BatchNamesTests"`;
  `C "~McpSurfaceTests"`; `editor`; `copies`; `record`.
- **D12. Fold on MCP and the CLI.** Runs after D9 and D11. Owns: the `apply_batch` description in
  `src/Maquettiste.Cli/Mcp/ModelTools.cs`, the `apply_batch` and `delete_element` rows of `docs/mcp.md`. Tests:
  `McpWriteTests.Apply_batch_folds_into_a_stereotype`,
  `ModelApplyTests.Model_apply_folds_into_a_stereotype_with_a_dry_run`. Verify:
  `C "~McpWriteTests|~McpSurfaceTests|~ModelApplyTests"`.
- **D13. sql-ddl renders inherited members.** Runs after D7. Owns:
  `tests/Maquettiste.Packs.Tests/StereotypeMembersDdlTests.cs` (new); only on a gap the test finds,
  `packs/sql-ddl/_table.scriban` and `packs/sql-ddl/README.md`. Tests: `StereotypeMembersDdlTests`. Verify:
  `P "~StereotypeMembersDdlTests|~GoldenTests"`; `PG P "~StereotypeMembersDdlTests"`.
- **D14. Acceptance.** Runs after D12 and D13. Owns:
  `tests/Maquettiste.Engine.Tests/Integration/StereotypeMembersTests.cs` (new). Tests: the six acceptance tests.
  Verify: `E "~Integration.StereotypeMembersTests"`; `PG P "~StereotypeMembersDdlTests"`.
- **D15. Docs.** Runs after D12. Owns: `docs/user-guide.md` (the new section), `docs/guides/schema-changes.md`,
  `docs/releases.md`, `skills/maquettiste-modeling/SKILL.md`, `docs/engineering/engine-design.md` (sections 7.1 and
  7.3), `Model/README.md`, `Resolution/README.md`, `Migrations/README.md`. Tests: none new. Verify:
  `C "~SkillRefreshTests|~McpSurfaceTests"`.
- **D16. Materialize keeps inherited members.** Runs after D17. Owns: `TablesAsync`, `TableNode` and `ColumnsAsync` in
  `Loading/Materializer.cs`. Tests: the `MaterializeTests` cases. Verify: `E "~MaterializeTests|~FactRecordingTests"`.
- **D17. Rekeys that move constraints, and undoing a fold.** Runs after D4, D6 and D7. Owns:
  `Migrations/FactClassifier.cs` (`ClassifierInput.MemberRekeys`, their place in the rekey, the column undo),
  `Migrations/FactFolder.cs` (`TablesOf`, `Compose`), `Validation/MigrationRules.cs` (`RekeyKinds`, a member entry's
  `table`), the classifier input in `ModelStore.Migrations.cs`, the `MaterializeResult` record in
  `Loading/Materializer.cs` (`MemberRekeys`), the `table` description in `schemas/v1/migration.json` with its copy, the
  regenerated editor types and mocks; found in D17 (decisions 34 and 35), `Migrations/FactKeys.cs` (`ConstraintKinds`),
  `RekeysCancel` in `Migrations/StaleFacts.cs`, the rekey summary in `Migrations/FactDocuments.cs`, and the `Kind` and
  `Table` comments of `MigrationRekey` and `RMigrationRekey`. Tests: the `FactClassifierTests` cases,
  `MigrationRulesTests`, the D17 `FactRecordingTests` case and
  `FactRecordingTests.Restoring_the_files_from_before_a_fold_records_its_column_and_index_back`. Verify:
  `E "~FactClassifierTests|~FactRecordingTests|~MigrationRulesTests|~SqlObjectRekeyTests|~ValidationFixtureTests"`;
  `editor`; `copies`.

### 3.2 Columns typed by reference types, enums and scalar types

**Why every team**

Most teams that model a database from its tables have columns that hold codes and kinds: a status, a currency, a
country, a channel. Today such a column is a string with a length, and nothing ties it to the reference type or enum
that says which codes exist. The tie lives in people's heads, or in a foreign key to a lookup table that only the
database checks. So renames, code checks, seeds, usage lists and generated code cannot see it. A logical type next to
the physical type says it once, and every layer reads it: validation checks codes and defaults, packs render the
constraint and a typed value, the usage list shows who depends on a type, and refactors (3.5) can reach the column.
Sensitivity is the same kind of statement: the team that owns a table knows which columns hold personal data, and
today it can only say so on attributes.

**Today**

- A designed column's `type` is a built-in keyword only (`schemas/v1/table.json:176-178`,
  `common.json#/$defs/columnType`). Attributes have a logical type (`common.json#/$defs/typeRef`, `common.json:214-231`)
  and `sensitive` (`common.json:444-446`); columns have neither (`Model/Physical.cs:317-392`).
- The index walks every property of type `TypeRef` as a type reference (`Model/ModelIndexer.cs:839-841`, `:899-900`).
  `remove-references` clears a reference by removing the property it sits in (`Loading/ChangePlanner.cs:559-616`) and
  keeps the change only when the referrer still reads (`Loading/ChangePlanner.Cascade.cs:148-153`, `:597-606`). An
  optional `{ "ref": ... }` object would be left as `{}`, which does not read, so the planner needs one case for it, as
  it has for diagram members (`Loading/ChangePlanner.cs:565-567`).
- Designed columns resolve from their file only (`Resolution/DatabaseRun.cs:207-228`; extra columns of an overlay the
  same way, `Resolution/DatabaseRun.Columns.cs:363-382`). `RColumn.ReferenceType` is set only for a projected column of
  an attribute typed by a reference type, whose `Type` is then `reference` (`Resolution/ResolvedPhysical.cs:587-603`,
  `Resolution/DatabaseRun.Columns.cs:30-40`); that column's table depends on the type and on `s:referenceData`
  (`:36`). A projected column of a scalar type takes the scalar's native type when its physical type is the scalar's
  base (`:319-326`); an enum column stored as text takes the longest stored code as its length (`:146-163`, `:179`).
- `type_of` maps a column through its reference type only when its `Type` is `reference` (`Rendering/TypeOf.cs:57-64`);
  given an `RType` it maps the type itself (`:72-73`, `:90-109`). The physical snapshot captures a column's reference
  state on the same condition (`SchemaDiff/SnapshotCapture.cs:222-224`), and a change of strategy or bound table is a
  conversion (`Migrations/Widening.cs:127`, `Migrations/FactClassifier.cs:365-371`).
- `reference_type_usage` lists attributes only (`ModelStore.LocalizationApi.cs:573-603`); it resolves the whole model
  again for each call (`:581`). The records are `ReferenceTypeUse` and `ReferenceTypeUsage` (`:100`, `:104`).
- `materialize-tables` writes a projected reference column as a column of the code's type and drops the reference type
  (`Loading/Materializer.cs:299-305`), so the stored table loses the storage objects packs rendered for it. It copies an
  overlay entry's annotations only (`:379`), so an overlay entry's other own keys are lost.
- Release 0.11.0 added reference types stored in a table. A reference type's `bindings` name, per database, the table
  file that holds its rows (`schemas/v1/reference-type.json:152-227`); the resolver links them after every database
  (`Resolution/ResolveRun.ReferenceData.cs:131`, `:275`), and a reference column reads the binding of its database as
  `column.reference_binding` (`Resolution/ResolvedPhysical.cs:597`). sql-ddl treats a bound type as realization `table`
  (`packs/sql-ddl/_reference.scriban:21-31`) and renders, for each column with a `reference_type`, a key into the bound
  table, a key into a lookup table or a CHECK (`_reference.scriban:447-468`); on PostgreSQL a `native` type replaces the
  column's SQL type with an enum type (`packs/sql-ddl/_sql.scriban:339-345`). MQ7118 checks that a binding's code column
  fits the code (`Validation/ReferenceBindingRules.cs:317-342`).
- A column's literal default is checked for credentials with no sensitivity (`Validation/PhysicalRules.cs:419`); an
  attribute's passes its own (`Validation/AttributeRules.cs:85`).

**Decisions taken here**

1. **`typeRef` on a column** names an enum, a reference type or a scalar type: `{ "ref": "<id>" }`, the object form
   only, since the column's `type` already holds the built-in keyword. A value object is refused (MQ4082). The model
   property has type `TypeRef`, so the index walks it as a type reference with no change to the indexer
   (`ModelIndexer.cs:839-841`), and `get_references`, delete plans and 3.5's refactors reach the column by id.
   `remove-references` removes the whole `typeRef` object (a new case beside the diagram member case in `RemoveAt`);
   the column keeps its physical type.
2. **The physical shape stays the column's.** `type` stays required on designed and extra columns. What the column
   states wins; a facet it leaves out comes from its type as a projected column's does (`ResolveRun.Conceptual.cs:905`
   for attributes, `DatabaseRun.Columns.cs:157` for enums): the code's length, the scalar type's length, precision and
   scale, or the longest stored code of an enum in a text column. A scalar type's native type for the dialect applies
   when the column's `type` is the scalar's base and the column sets no `nativeType`, as on a projected column
   (`DatabaseRun.Columns.cs:319-326`). MQ4083 refuses a stated type or facet that cannot hold the type's values.
3. **Where `typeRef` may appear:** designed and imported table columns, extra columns of an overlay, and columns a
   stereotype contributes (3.1 shares the column schema). Not on an overlay entry of a projected column (its attribute
   types it), and not on a column a reference type's binding fills with a field (the binding says what that column
   holds). Both are MQ4082; the second is reported on the binding's field, since the reference type's file is the one
   that changes it. A table that carries the stereotype cannot redefine `typeRef` or `sensitive` on an inherited
   column: 3.1's redefinition entries take neither key, so such a table excludes the member and declares its own.
4. **How a typed column relates to a stored reference binding.** Three statements, each made once:
   - the binding, on the reference type, says where the type's rows live in one database;
   - `typeRef`, on the column, says whose codes the column holds;
   - how the database enforces it is derived. A designed, imported or extra column typed by a reference type becomes a
     reference column for templates exactly like a projected one: `reference_type`, `code_type`, `strategy` and
     `reference_binding` are set, so packs render the same objects (a key into the bound table, a key into a lookup
     table, or a CHECK).

   One case keeps the table's own statement: when the table declares a foreign key that includes the typed column, that
   key is the enforcement. MQ4084 requires it to point at the type's bound table in that database, pairing the typed
   column with the binding's code column; packs then render no key of their own for the column. A link is never stated
   twice, and a table imported from a database keeps the key name the database has. A declared key from a typed column
   is refused when the type has no binding in that database, since no table file holds its rows there.
5. **`column.type` keeps the physical keyword** for a typed designed column. `reference` stays the type of a projected
   reference column only. Templates test `column.reference_type` for "holds codes", as sql-ddl already does.
6. **`column.type_ref`** (`RColumn.TypeRef`) is the type the column's own entry names. It is null on a projected
   column, whose logical type is `column.attribute.type`. Each column has one place that states its logical type.
7. **`type_of <column>` is unchanged:** it maps the column's physical type, so the row classes packs fill from the
   database keep their types. A pack that wants the logical type passes `column.type_ref`, an `RType`, which `type_of`
   already maps (`TypeOf.cs:72-73`, `:90-109`).
8. **Migration facts.** The snapshot captures the reference state of every column with a `ReferenceType`, not only of
   columns whose `Type` is `reference`. Typing a designed column by a reference type stored as a lookup table, a CHECK,
   a native type or a bound table, or changing that storage, is then a conversion (`Migrations/Widening.cs:127`), as it
   is today for a projected column: rows already in the column may hold values that are not codes, so the fact asks how
   they convert (decision 15). Typing it by a type the templates store (no strategy, no binding) changes only the
   captured `referenceType`, which `Widening.SameReference` does not count as a storage change, so no fact is
   recorded. Materializing a projected reference column keeps its reference state, so it records no conversion.
   Changing `typeRef` between two types with the same storage records no fact; 3.5's value maps cover moving codes.
   The ledger is unchanged.
9. **`sensitive` on columns** takes `pii` or `secret`, like attributes, which keep theirs. It is said once, on either
   side, and the other side shows it. A projected column shows its overlay entry's own value, else its attribute's own.
   A designed, imported or extra column shows its own value, else the strongest own value of the attributes bindings
   map to it (`secret` over `pii`). An attribute shows its own value, else the strongest own value of the columns its
   entity's bindings map it to. Each side reads only the other's own value, so nothing cycles. Both sides stating
   different values is MQ4085, a warning. Column values are set in the database run, after its bindings; attribute
   values in one pass after every database (`ResolveRun.cs:146`, after `FinishConceptual`). A column's own `secret`
   also feeds MQ3017 on its literal default, as an attribute's does.
10. **Values written into a typed column** are members or codes of its type: table seed cells, the column's literal
    default, and constants a binding (an entity's or a reference type's) gives the column (MQ7014). An integer column of
    an enum holds member values; a text column holds member codes, or names where a member has no code
    (`DatabaseRun.Columns.cs:157`). Cells hold stored values, never member names.
11. **`reference_type_usage` lists columns:** the designed, imported, extra and inherited columns whose `typeRef` names
    the type, with table, database, domain, required, the effective storage choice and the bound table, ordinal by
    table id then column id. Projected columns are already covered by their attributes. The read takes the shared
    resolved snapshot (`ModelStore.ResolverFindings.cs:22-39`) when it has a model, and resolves as today only when
    validation errors leave it without one.
12. **`materialize-tables` and `materialize-columns` write `typeRef`** for a column that stores one attribute or member
    typed by an enum, a reference type or a scalar type, so a projection stored as a file keeps its logical type and its
    storage objects; they leave out a `nativeType` the scalar type gives. They write no `sensitive` from the attribute:
    the attribute keeps saying it, and the bound column shows it. An overlay entry's own `sensitive` is the column's own
    statement, so `materialize-tables` keeps it on the stored column.
13. **PostgreSQL first.** A designed column whose type's realization is `native` keeps its own SQL type and gets the
    CHECK the other dialects get, since an enum type would replace the type the column states. Every other object is
    one sql-ddl already renders for projected reference columns, so typed columns need no new dialect work.
14. **Recording sees bound tables** (D21). The partial runs that record facts (`ResolveRun.RunDatabases`, and so the
    explorer's one-database run) skipped what runs after the databases, reference bindings included, so a reference
    column's bound table was captured as none there while validate and generate captured it. They now link the
    bindings into the databases they resolve (`ResolveRun.LinkReferenceBindings`), so typing a column by a bound type,
    or binding the type of a typed or projected column, records the conversion of decision 8.
15. **What the fact asks for** (D21). A change of reference state alone is a conversion, so the recorded `change-type`
    asks for its `conversion` (MQ4101), where the team says how values that are not codes are handled; it asks for a
    `dataCheck` as well only when the step also narrows the column, as for any other `change-type`.
16. **The view's sensitivity arrives with D23** (D21). D21 adds `ColumnView.TypeRef` and `ColumnView.Sensitive` and
    their contract in one change, so the editor types are regenerated once; `ColumnView.Sensitive` reads `null` until
    D23 adds `RColumn.Sensitive` and passes it in `DatabaseViews.Column`. D21 makes `ResolveRun.TypeOf` internal so
    `TypeColumn` types a column as an attribute's type is resolved.
17. **What the table rules reach** (D22). The inherited columns judged are those of designed and imported tables: an
    overlay's member set is a candidate set that may hold members its synthesized table skips, which the resolver knows.
    MQ4084 judges the table's own foreign keys and the inherited ones that name `referencesTable`; one that names
    `referencesParameter` resolves per table and waits for a team that needs it. A `typeRef` naming a kind outside the
    model's type references (an entity, a table) stays MQ2002 and is not also MQ4082. The new conflict peers reach the
    tables that list a stereotype with a typed column, not those that get it through a default list. A number or boolean
    in a table seed cell of a text column stands for its text, as MQ7109 reads it, before the code check.
18. **A value object member keeps its own sensitivity** (D23). The pass after every database sets only the entity's own
    attributes: a value object's member, which a field may map, is one object shared by every embedding, so it never
    borrows a column's value. The column still shows the member's own value. The column's own value is kept apart from
    the one it shows (`RColumn.OwnSensitive`, internal), so an attribute reads only what the column states.
19. **What a usage row names** (D24). A column's `table` is the resolved table's id: the table file's id, or, for an
    overlay's extra column and a column a synthesized table inherits, the synthesized table's key
    (`<entityId>@<databaseId>`), as the database view and keyed ids name it. `domain` is the table's package; a
    synthesized table without one of its own takes its entity's or relation's. A missing domain, storage choice or bound
    table is written as null, as the attribute rows write theirs.
20. **What materialize types** (D25). A column typed by a scalar type gets `typeRef` only when its physical type is the
    scalar's base, the case where the scalar's facets and native type reach it; another physical type would fail MQ4083.
    The native type left out is the scalar's own for the dialect; an overlay entry's `nativeType` is kept. A child
    table's value column of a collection stores one value, so it is typed like a single attribute's column.
21. **Pack details** (D26). A typed column in a declared foreign key gets no statement in `seed.sql`, not even drops of
    `_ref` objects, as a column that stops holding codes gets none today. csharp-dapper types the fields of generated
    collection item records as it types row records, and the summary sentence names the member plainly: "Status holds
    a code of `<see cref="global::App.Model.InvoiceState"/>`."

#### Schema and files

`common.json` gains two definitions; the attribute's `sensitive` refers to the second (same values, no change on disk):

| Definition | Shape |
| --- | --- |
| `columnTypeRef` | `{ ref: id }`, `x-order` `["ref"]`, `required: ["ref"]`, no other keys; described as "an enum, a reference type or a scalar type" |
| `sensitivity` | `enum: ["pii", "secret"]` |

`common.json#/$defs/tableColumn` (the column items 3.1's D1 moves out of `table.json`, so table columns and stereotype
columns share them): `typeRef` follows `type` in `x-order`, and `sensitive` follows `comment`.

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| `typeRef` | `#/$defs/columnTypeRef` | absent | Designed, imported, extra and contributed columns (MQ4082) |
| `sensitive` | `#/$defs/sensitivity` | absent | Any column; an overlay entry states it for its projected column |

Example, in a billing copy (`InvoiceState` is an invented reference type with one-letter codes):

```json
{
  "id": "01K9X3C0000000000000000003",
  "name": "status",
  "type": "string",
  "typeRef": { "ref": "01K9X3A0000000000000000001" },
  "length": 1,
  "nullable": false
}
```

`common.json` gets its byte-identical copy under `samples/reference-app/.maquettiste/.schema/v1/`.

#### Resolution and the resolved model

- **Typed columns.** One helper, `TypeColumn`, is called where designed columns (`DatabaseRun.cs:207-228`), extra
  columns (`DatabaseRun.Columns.cs:363-382`) and 3.1's inherited columns (`Resolution/DatabaseRun.Members.cs`) are
  built, before `ApplyFacetDefaults`. It sets `TypeRef` (an `RType` of kind `enum`, `reference` or `scalar`), fills the
  facets the column leaves out from the type, and for a reference type sets `ReferenceType`, `CodeType` and `Strategy`
  (`_run.StorageChoice`, as `DatabaseRun.Columns.cs:38` does). For a scalar type whose base is the column's type,
  `ColumnNativeType` (`DatabaseRun.cs:630-633`) passes the scalar, so its native type applies. The table's dependencies
  gain the type's keys as a projected column's do: the element key, `s:referenceData` for a reference type
  (`DatabaseRun.Columns.cs:36`), the scalar type's keys (`:321`).
- **Sensitivity.** `ApplyColumnFile` (`DatabaseRun.cs:304`) sets a column's own value; `AddColumn` takes a projected
  column's from its attribute when the overlay entry states none. After the database's bindings resolve, a designed,
  imported or extra column that states none takes the strongest own value of the attributes its binding fields map; the
  table already depends on each entity bound to it (`DatabaseRun.Bindings.cs:63-71`). The pass after every database
  (`Resolution/ResolveRun.Shared.cs`, new, called after `FinishConceptual`) sets each bound attribute that states none
  from the columns its bindings map; such an attribute depends on what states those columns' entries whether or not
  they state a value, so a later `sensitive` on the column replans what reads it (`RTable.EntryKeys`: a designed or
  imported table's file, database, stereotypes and package chain, not the other entities bound to the table; a table
  without such a file, its whole dependency set). One-database runs (`ResolveRun.cs:84-125`) skip
  that pass: the explorer's one-database views show columns' values, not attributes' borrowed ones, and nothing there
  reads attributes.
- **Snapshots.** `SnapshotCapture.cs:222-224` keys on `column.ReferenceType is not null`. No column has a `typeRef`
  today, so no existing snapshot or fact changes. The partial runs that record facts link reference bindings into the
  databases they resolve (decision 14), so they capture a bound table as validate and generate do.

**Public API**

```csharp
namespace Maquettiste.Engine.Model;
public sealed record Column : ElementBase
{   // existing members, plus:
    /// <summary>The enum, reference type or scalar type whose values the column holds (designed, imported, extra and contributed
    /// columns); <see langword="null"/> when its physical type is all it says. Only <see cref="Model.TypeRef.Ref"/> is set.</summary>
    public TypeRef? TypeRef { get; init; }
    /// <summary>Whether the column holds personal data or a secret.</summary>
    public Sensitivity? Sensitive { get; init; }
}

namespace Maquettiste.Engine.Resolution;
public sealed class RColumn
{   // existing members, plus:
    /// <summary>The type the column's own entry names; <see langword="null"/> for a projected column (its attribute's type).</summary>
    public RType? TypeRef { get; internal set; }
    /// <summary><c>pii</c> or <c>secret</c>: its own; else a projected column's attribute's own; else the strongest own value of
    /// the attributes bindings map to it.</summary>
    public string? Sensitive { get; internal set; }
    // ReferenceType, CodeType, Strategy and ReferenceBinding: also set when TypeRef names a reference type.
}
public sealed class RAttribute
{   /// <summary>Changed: its own; else the strongest own value of the columns its entity's bindings map it to.</summary>
    public string? Sensitive { get; internal set; }
}

namespace Maquettiste.Engine;
public sealed record ColumnView(/* existing parameters */, TypeRecord? TypeRef, string? Sensitive);
public sealed record ReferenceTypeUsage(IReadOnlyList<ReferenceTypeUse> Usages, IReadOnlyList<ReferenceTypeColumnUse> Columns);
/// <param name="Table">The table id.</param> <param name="Column">The column id (a keyed id for an inherited column).</param>
/// <param name="Database">The database id.</param> <param name="Domain">The table's package, or null.</param>
/// <param name="Required">Whether the column is not nullable.</param>
/// <param name="Storage">The type's effective storage choice in the database, or null for template-defined.</param>
/// <param name="BoundTable">The table of the type's binding in the database, or null.</param>
public sealed record ReferenceTypeColumnUse(string Table, string Column, string Database, string? Domain, bool Required,
    StorageChoice? Storage, string? BoundTable);
```

`ResolvedCoverageTests` matches schema keys to resolved members: D20 lists `("column", "typeRef")` and
`("column", "sensitive")` in `Pending`, D21 removes the first when `RColumn.TypeRef` exists and D23 the second.
`ResolvedNullContractTests.Derived` gains `RColumn.Sensitive` and `RAttribute.Sensitive` with their fallback rules.

#### Rules

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ4082 | E | A column's typeRef is misplaced: it names no enum, reference type or scalar type, it sits on an overlay entry of a projected column, or a reference type's binding fills the column with a field. | Remove it, or type the attribute |
| MQ4083 | E | A typed column's type or a facet it states cannot hold its type's values: a reference type's codes, an enum's stored values, or a scalar type's base and facets. | Fix the type or facet, or drop the facet |
| MQ4084 | E | A foreign key that includes a column typed by a reference type does not point where the type's rows are: the type has no binding in the column's database, or the key does not pair the column with the code column of the bound table. | Point the key at the bound table, or remove it |
| MQ4085 | W | A column and an attribute it stores, or that a binding maps to it, state different sensitivities. | State it on one side |
| MQ7014 | E | A value written into a column typed by an enum or a reference type is not one of its stored values or codes: a table seed cell, the column's literal default, or a binding constant. | Use a code of the type |

- **What fits (MQ4083).** A reference type's code: text in a long enough text column, an integer in an integer or
  decimal column, a UUID in a uuid or text column. An enum: every member value in an integer column, every stored code
  in a long enough text column. A scalar type: its base keyword, with facets no smaller than the scalar's.
- **Placement.** MQ4082 (first two cases) to MQ4084 and MQ7014 for defaults are table rules in
  `Validation/ColumnTypeRules.cs` (new), called from the table case of `BuiltinRules.cs`. They judge a table's own
  columns and its inherited ones (3.1's `TableMembers.Of`), and place a finding on an inherited column where 3.1 places
  member findings. MQ4082's binding case and MQ7014 for a reference type binding's constants join
  `ReferenceBindingRules.cs` (pointer `/bindings/<i>/fields/<j>/column`, `/bindings/<i>/constants/<j>`). MQ7014 for
  cells joins `TableSeedRules.cs` (in each environment the row loads in), and for entity binding constants the entity
  case (`BuiltinRules.cs:33-35`, pointer `/bindings/<i>/constants/<j>`). MQ4085 runs in the entity case for binding
  fields (pointer `/bindings/<i>/fields/<j>`) and in the table case for overlay entries (pointer
  `/columns/<k>/sensitive`). All are validator rules, so a save that introduces an error is refused, and
  `validation.rules` sets each severity (`Validation/ModelValidator.cs:1046`).
- **Code fit.** `ReferenceBindingRules.Fit` (`ReferenceBindingRules.cs:317-342`) becomes an internal `CodeFit` shared
  by MQ7118 and MQ4083, so a bound code column and a typed column are judged alike.
- **Save scope.** A typed column's table refers to its type, and a reference type's binding refers to its table, so a
  save of either re-judges the other as a referrer. `ModelValidator.ConflictPeers` (`ModelValidator.cs:417`) gains one
  case for what no reference reaches: an enum, a reference type or a seed of a reference type brings the tables whose
  columns the type types, the table seeds of those tables and the entities whose bindings give those columns constants.

#### Templates and packs

- **Engine contract.** `column.type_ref`, `column.sensitive` and `attribute.sensitive` (now effective) reach templates.
  `column.reference_type`, `code_type`, `strategy` and `reference_binding` are set on typed designed columns. No
  existing model renders differently: no file has a column `typeRef` or `sensitive` yet, and the one template that reads
  `sensitive` reads event payload attributes, which no binding maps
  (`packs/csharp-dapper/dispatch-behaviours.scriban:219`).
- **sql-ddl.** `column_type` (`_sql.scriban:339-345`) uses the enum type only for a projected reference column
  (`c.type == "reference"`). A new `column_realization(c, db)` gives the type's realization, with `native` on a designed
  column read as `check`. `reference_column_statements` (`_reference.scriban:447-468`) already visits every column with
  a `reference_type`, so typed designed columns get their objects through `column_realization`; a typed column that is
  part of a declared foreign key (`c.is_foreign_key`) gets no `fk_<table>_<column>_ref` (MQ4084 has made that key point
  at the bound table). Bound order is unchanged: the objects follow the bound table's rows in `seed.sql`.
- **csharp-dapper.** In the query unit, a field of a row record that reads a typed column
  (`f.expression.table_column.type_ref`) is typed through its type. For an enum, the record's member is the enum,
  nullable as the field, and `ToItem` converts the stored value as `read_field` converts an enum attribute
  (`query.scriban:38-63`). For a reference type, the member keeps the code's C# type, and the record's summary gains one
  sentence per such field: "`Status` holds a code of `<see cref="global::App.Model.InvoiceState"/>`.", the cref
  qualified with the pack's root namespace, where the reference-type unit declares the record
  (`reference-type.scriban:74`). A summary sentence, not a `<param>` tag, so builds with documentation warnings never
  meet a record whose other members lack tags.
- **seed-data.** Unchanged: it writes stored values.

#### CLI and MCP

`reference_type_usage` (MCP and the assistant) and `GET /api/reference-types/{id}/usage` answer `columns` beside
`usages`. `get_resolved_model`, `get_database_view` and the table views carry `typeRef` and `sensitive` on columns
through `ColumnView`. No verb or tool is added. Rows change in `docs/mcp.md`; `docs/api/openapi.yaml` changes
`ColumnView` and `ReferenceTypeUsage`.

#### Editor follow-ups

- The Columns grid gets a logical type picker (enums, reference types, scalar types) beside the physical type, and a
  Sensitive menu; until then the generic JSON form edits both.
- The Reference data screen's "Used by" lists columns as well as attributes.
- The Storage tab marks a bound attribute's sensitivity as "from column `<table>.<column>`" when it borrows it.

#### Docs

- `docs/guides/reference-data-and-seeds.md`: new section "Type a column of your own table" after section 10, with the
  binding relation of decision 4 in plain words, and "Where the rows go" updated for typed designed columns.
- `docs/user-guide.md`: the column table gains `typeRef` and `sensitive`; the sensitivity rule of decision 9; the five
  rules in the rule tables.
- `docs/mcp.md`: the `reference_type_usage` row. `packs/sql-ddl/README.md`, `packs/csharp-dapper/README.md`: typed
  columns.
- `skills/maquettiste-modeling/SKILL.md`: `typeRef` and `sensitive` on columns, and `reference_type_usage` listing
  columns.
- `docs/releases.md`, under `## 0.12.0 (unreleased)` (created by the first phase 2 task that writes a note): one entry.

**Not doing**

- `typeRef` on view columns, query parameters, routine parameters and composite type fields: each has its own type
  syntax, and a later item can add it the same way.
- A value object on a column, for a JSON column's document shape: a separate item if it comes up.
- Inferring a column's type from a foreign key into a bound table, or from a CHECK. Typing stays an explicit statement.
- Taking `type` itself from `typeRef`: the physical keyword stays explicit, as the scope requires.
- A PostgreSQL enum type for designed columns (decision 13).
- `type_of <column>` mapping through the logical type (decision 7).
- Member names in table seed cells; 3.4 handles natural keys.
- Sensitivity-driven DDL (security labels, masking, grants). Packs may read `column.sensitive`; none does in phase 2.
- A migration fact for a `typeRef` change with unchanged storage (decision 8).

**Tests**

- `ModelLoaderTests.A_column_typeRef_and_sensitive_round_trip_in_canonical_form`.
- `ModelSnapshotTests.A_column_typeRef_is_indexed_as_a_type_reference`.
- `ModelStoreDeleteTests.Remove_references_clears_a_column_typeRef_and_keeps_its_type`.
- `SchemaConsistencyTests` and `ResolvedCoverageTests` (existing) over the new keys and definitions.
- `PhysicalFileTests`: `A_designed_column_typed_by_a_reference_type_is_a_reference_column`,
  `A_typed_column_takes_the_facets_it_leaves_out_from_its_type`,
  `A_designed_column_typed_by_a_scalar_type_takes_its_native_type`, `An_extra_overlay_column_may_be_typed`.
- `DependencyKeyTests`: `A_typed_column_table_depends_on_the_type_and_reference_data`,
  `An_attribute_borrowing_sensitivity_depends_on_the_column_table`.
- `BindingTests`: `A_designed_column_shows_the_strongest_sensitivity_of_its_bound_attributes`,
  `A_bound_attribute_shows_its_column_sensitivity`, `A_projected_column_shows_its_attribute_sensitivity`,
  `Own_sensitivity_wins_on_both_sides`.
- `TemplateCacheAndHelperTests`: `Type_of_a_typed_column_keeps_its_physical_type`,
  `Type_of_a_column_type_ref_maps_through_the_type`.
- `SchemaDifferTests.Typing_a_designed_column_by_a_lookup_type_changes_its_reference_state`.
- `FactRecordingTests`: `Typing_a_designed_column_by_a_bound_reference_type_records_a_conversion`,
  `Retyping_between_types_with_one_storage_records_no_fact`,
  `Materializing_a_projected_reference_column_records_no_conversion`.
- `ColumnTypeRuleTests` (new): a failing and a passing case per rule from MQ4082 to MQ4085 and MQ7014,
  `MQ4082_on_a_binding_field_is_reported_on_the_reference_type`, `An_inherited_typed_column_is_judged_on_the_table`,
  `A_declared_key_into_the_bound_table_passes_MQ4084` and `MQ4085_takes_the_project_severity`.
- `ScopedValidationTests`: `Removing_a_code_from_a_seed_rejudges_a_typed_column_default`,
  `Removing_an_enum_member_rejudges_the_table_seed_cells`; `ColumnDefaultTests.A_secret_column_default_is_MQ3017`.
- `ReferenceDataStoreTests.Usage_lists_designed_columns_typed_by_the_type`;
  `AgentToolsTests.Reference_type_usage_lists_columns`; `McpReadTests.Reference_type_usage_answers_columns`.
- `MaterializeTests`: `Materialize_tables_writes_typeRef_for_enum_reference_and_scalar_columns`,
  `Materialize_tables_keeps_an_overlay_entry_sensitivity_and_writes_no_attribute_one`,
  `Materialize_columns_writes_typeRef`.
- Packs: `ReferenceBindingSqlTests.A_typed_designed_column_gets_a_key_into_the_bound_table`,
  `A_typed_column_in_a_declared_key_gets_no_key_of_its_own`; `ReferenceDataTests` (PostgreSQL):
  `A_typed_designed_column_on_a_lookup_type_gets_its_key`,
  `A_typed_designed_column_on_a_native_type_keeps_its_type_and_gets_a_check`;
  `CompileTests.Query_rows_with_typed_columns_compile`.
- **Acceptance (scope 6.4, done when):**
  `Integration/ColumnTypeTests.An_invoice_event_status_typed_by_a_reference_type_is_in_its_usage_and_typed_in_csharp`
  (new). Setup: a billing copy gains the reference type `InvoiceState` in package `billing` (a `string` code of length
  1, with a seed of rows `D` Draft, `I` Issued, `P` Paid, `V` Void), the designed table `invoice_events` in database
  `main`, schema `billing` (`id` uuid primary key, `invoice_id` uuid not null, `status` `type: string`, `length: 1`, not
  null, `typeRef` `InvoiceState`, `occurred_at` datetimeoffset not null), and the query `events-by-status` that selects
  `id` and `status` from it. Expected: validate is clean. `reference_type_usage` for `InvoiceState` answers `usages: []`
  and one column entry for `invoice_events.status` in `main`, required, with no storage choice and no bound table.
  csharp-dapper writes `EventsByStatusRow(Guid Id, string Status)` whose summary names `InvoiceState` in a cref, and
  `CompileTests.Query_rows_with_typed_columns_compile` compiles the same shape. sql-ddl's `seed.sql` holds
  `fk_invoice_events_status_ref` from `invoice_events.status` to the lookup table's code (the pack's default
  `lookup-table` realization).
- **Acceptance (the binding relation):** `Integration/ColumnTypeTests.A_typed_column_points_at_the_bound_table_once`.
  Setup: the same copy, where `InvoiceState` gains a binding to a designed table `invoice_states` in `main` (`code`
  string(1) primary key, `label` string(40) not null; code and label mapped). Expected: `seed.sql` creates no lookup
  table and holds `fk_invoice_events_status_ref` into `invoice_states (code)` after the type's rows. When
  `invoice_events` declares the foreign key `fk_event_state` from `status` to `invoice_states.code`, `seed.sql` holds no
  `_ref` key for the column and `schema.sql` holds `fk_event_state`. Pointing `fk_event_state` at `notes.id` is MQ4084,
  an error, at `/foreignKeys/0` of the `invoice_events` file.
- **Acceptance (code fit):** `Integration/ColumnTypeTests.A_status_column_shorter_than_the_codes_is_refused`. Setup: the
  first copy, with the code length of `InvoiceState` raised to 2. Expected: MQ4083 at `/columns/2/length` of
  `invoice_events`. Removing the column's `length` clears it, and the resolved column has length 2.
- **Acceptance (sensitivity):**
  `Integration/ColumnTypeTests.Note_body_sensitivity_is_said_once_and_shown_on_both_sides`. Setup: a billing copy where
  column `body` of `notes` states `sensitive: pii`. Expected: the resolved `body` attributes of `InvoiceNote` and
  `CustomerNote` show `pii`. Giving `InvoiceNote`'s `body` `sensitive: secret` is MQ4085, a warning, at
  `/bindings/0/fields/2` of `entities/invoice-note.json`.
- **Acceptance (materialize keeps the type):**
  `Integration/ColumnTypeTests.Materializing_invoice_keeps_status_typed_by_its_enum`. Setup: a billing copy;
  `materialize-tables` stores `Invoice`'s table in `main`. Expected: the stored `status` column has `type: string`,
  `length: 1`, `default: "D"` and `typeRef` `InvoiceStatus`, and validate is clean.

**Tasks**

- **D20. Column `typeRef` and `sensitive` in the schema and model.** Runs after D11 (3.1). D11 follows D1, D6, D7, D9,
  D10, D16 and D17, which own `common.json` (`tableColumn`), `Model/Physical.cs`, `ResolvedCoverageTests`,
  `Loading/ChangePlanner.cs` and the regenerated editor types, and every 3.1 task that owns a file of D21 to D25. Owns:
  `schemas/v1/common.json` (`columnTypeRef`, `sensitivity`, `tableColumn`'s `typeRef` and `sensitive`, the attribute's
  `sensitive` reference) and its copy, the regenerated editor types and mocks, `Model/Physical.cs` (`Column.TypeRef`,
  `Column.Sensitive`), the `Sensitivity` summary in `Model/Conceptual.cs`, the `typeRef` case of `RemoveAt` in
  `Loading/ChangePlanner.cs`, `ResolvedCoverageTests` (the two `Pending` entries). Tests: `ModelLoaderTests`,
  `ModelSnapshotTests`, the `ModelStoreDeleteTests` case, `SchemaConsistencyTests`. Verify:
  `E "~Json.|~Model.|~Loading.|~ModelStoreDeleteTests|~ResolvedCoverageTests"`; `F "~ContractTests"`; `editor`;
  `copies`.
- **D21. Typed designed columns in resolution, snapshots and views.** Runs after D20 (and so after 3.1's D3, D7 and
  D9: `Resolution/DatabaseRun.cs`, `Resolution/ResolvedPhysical.cs`, `Editor/DatabaseViews.cs`,
  `docs/api/openapi.yaml`). Owns: `TypeColumn` and its calls in `Resolution/DatabaseRun.cs`,
  `Resolution/DatabaseRun.Columns.cs` and `Resolution/DatabaseRun.Members.cs`, `ColumnNativeType`,
  `Resolution/ResolvedPhysical.cs` (`RColumn` members and summaries), `SchemaDiff/SnapshotCapture.cs`,
  `Editor/DatabaseViews.cs` (`ColumnView`), `docs/api/openapi.yaml` (`ColumnView`), the regenerated editor types and
  mocks, `ResolvedCoverageTests` (`typeRef` leaves `Pending`), `LinkReferenceBindings` and its call in
  `Resolution/ResolveRun.cs` and `TypeOf` made internal in `Resolution/ResolveRun.Conceptual.cs` (decisions 14 and 16).
  Tests: `PhysicalFileTests`, the first `DependencyKeyTests` case, `TemplateCacheAndHelperTests`, `SchemaDifferTests`,
  the first two `FactRecordingTests` cases, `DatabaseViewTests` (the column's keys and its `typeRef`). Verify:
  `E "~Resolution.|~Rendering.|~SchemaDiff.|~Editor.|~FactRecordingTests"`; `P "~GoldenTests"` (no change);
  `F "~ContractTests"`; `editor`.
- **D22. Column type rules.** Runs after D20 (and so after 3.1's D4, D5, D7, D8 and D11, which own
  `Validation/BuiltinRules.cs`, `Validation/PhysicalRules.cs`, `Validation/TableSeedRules.cs`,
  `Validation/ReferenceBindingRules.cs`, `Validation/ModelValidator.cs`, `RuleCatalog.cs` and `CatalogTests.cs`). Owns:
  `Validation/ColumnTypeRules.cs` (new), its calls in `BuiltinRules.cs` (table and entity cases), MQ7014 in
  `Validation/TableSeedRules.cs`, `CodeFit`, MQ4082's binding case and MQ7014's constants in
  `Validation/ReferenceBindingRules.cs`, the new case of `ModelValidator.ConflictPeers`, the MQ3017 call in
  `Validation/PhysicalRules.cs`, `Diagnostics/RuleCatalog.cs` (MQ4082 to MQ4085, MQ7014), `CatalogTests.cs` (+5).
  Tests: `ColumnTypeRuleTests`, `ScopedValidationTests`, `ColumnDefaultTests.A_secret_column_default_is_MQ3017`.
  Verify:
  `E "~CatalogTests|~ColumnTypeRule|~ColumnDefault|~ScopedValidation|~ValidationFixture|~TableSeed|~ReferenceBinding"`;
  `record`.
- **D23. Sensitivity shown on both sides.** Runs after D21. Owns: the sensitivity lines of `ApplyColumnFile` and
  `AddColumn`, the column step after the bindings in `Resolution/DatabaseRun.Bindings.cs`,
  `Resolution/ResolveRun.Shared.cs` (new: the pass after every database) and its call in `Resolution/ResolveRun.cs`, the
  `RAttribute.Sensitive` summary in `Resolution/ResolvedConceptual.cs`, `RColumn.Sensitive` in
  `Resolution/ResolvedPhysical.cs` and the `Sensitive` argument of `DatabaseViews.Column` in `Editor/DatabaseViews.cs`
  (decision 16), `ResolvedCoverageTests` (`sensitive` leaves `Pending`), `ResolvedNullContractTests` (`Derived`).
  Tests: the `BindingTests` cases, the second `DependencyKeyTests` case. Verify: `E "~Resolution.|~Editor."`;
  `P "~GoldenTests"` (no change).
- **D24. Usage lists columns.** Runs after D21 and D12 (3.1: `docs/mcp.md`; D9, before D20, owns `Agent/tools.json`).
  Owns: `ModelStore.LocalizationApi.cs` (`ReferenceTypeUsage`, `ReferenceTypeColumnUse`, the shared resolved
  snapshot), the `reference_type_usage` description in `Agent/tools.json`, its row in `docs/mcp.md`,
  `ReferenceTypeUsage` in `docs/api/openapi.yaml`, the regenerated editor types and mocks. Tests:
  `ReferenceDataStoreTests`, `AgentToolsTests`, `McpReadTests`. Verify: `E "~ReferenceDataStore|~AgentToolsTests"`;
  `C "~McpReadTests|~McpSurfaceTests"`; `F "~LocalizationEndpointTests|~ContractTests"`; `editor`.
- **D25. Materialize keeps the logical type.** Runs after D21 (and so after 3.1's D11, D16 and D17, which own parts of
  `Loading/Materializer.cs`). Owns: `ColumnNode` in `Loading/Materializer.cs` only (`typeRef`, the scalar's
  `nativeType` left out, the overlay entry's `sensitive`). Tests: `MaterializeTests`, the third `FactRecordingTests`
  case. Verify: `E "~MaterializeTests|~FactRecordingTests"`.
- **D26. Packs render typed designed columns.** Runs after D21 and D13 (3.1: `packs/sql-ddl/README.md`). Owns:
  `packs/sql-ddl/_sql.scriban`, `packs/sql-ddl/_reference.scriban`, `packs/csharp-dapper/query.scriban`, both packs'
  `README.md`, the pack test cases above. Tests: `ReferenceBindingSqlTests`, `ReferenceDataTests`, `CompileTests`.
  Verify: `P "~ReferenceBindingSql|~CompileTests|~GoldenTests"`; `PG P "~ReferenceDataTests"`.
- **D27. Acceptance, guides and the release note.** Runs after D22, D23, D24, D25, D26 and D15 (3.1:
  `docs/user-guide.md`, `docs/releases.md`, `skills/maquettiste-modeling/SKILL.md`). Owns:
  `Integration/ColumnTypeTests.cs` (new), `docs/guides/reference-data-and-seeds.md`, the column, sensitivity and rule
  parts of `docs/user-guide.md`, the 3.2 lines of `skills/maquettiste-modeling/SKILL.md`, the 3.2 entry in
  `docs/releases.md`. Tests: the five acceptance tests. Verify: `E "~Integration.ColumnTypeTests"`;
  `C "~McpSurfaceTests|~SkillRefreshTests"`.

### 3.3 Bindings that record their provenance and drift

**Why every team**

Teams that start from an existing database bind entities to tables they do not fully own, and those tables keep
changing: a migration adds a column, someone renames one, a type widens. The binding keeps working, because it links
columns by id, but the entity stops mirroring the table. A new column is missing from the entity, a renamed column still
carries the old attribute name, a widened column still has the old length in generated code. Today only MQ4047 notices,
and only a new column the database does not fill. Teams need to see what changed in a table since their entity last
followed it, to have validation and CI say so at the severity they choose, and to catch up in one reviewed step from an
agent, the command line or, later, the editor. They also want the table's own comments to document the entity without
copying text.

**Today**

- A binding's field map is explicit (`schemas/v1/entity.json:196-218`), and nothing records how it was made or what the
  table looked like then (`Model/Bindings.cs:13-60`). A field names its column by key or physical name
  (`Model/Bindings.cs:115-117`); the resolver looks a name up exactly, then by key or id, then loosely
  (`Resolution/DatabaseRun.Bindings.cs:300-305`).
- MQ4047 (`Diagnostics/RuleCatalog.cs:166`) is a resolver finding: a column of the source nothing accounts for
  (`Resolution/DatabaseRun.Bindings.cs:664-674`, called at `:179`). Columns the database fills (identity, default,
  computed) count as accounted for (`:613-661`). Its severity follows `validation.rules`
  (`schemas/v1/maquettiste.json:301-315`, `Resolution/ResolveRun.cs:369-386`). Resolver findings reach every validate
  path once per snapshot (`ModelStore.ResolverFindings.cs:22-39`).
- Map by name and Auto-map exist only in the editor (`src/editor/src/editors/storage/fieldMap.ts:476-500` and
  `:627-640`; the "Map by name…" button, `StorageTab.tsx:475-476`). They compare names with case and every non-letter,
  non-digit character dropped (`fieldMap.ts:296-298`) and name a member `attributeMember` and a to-one key the
  navigation plus `Id` (`fieldMap.ts:46`). MCP and the CLI cannot map by name.
- `materialize-entities` and `materialize-attributes` type an attribute by the column's built-in keyword
  (`Loading/Materializer.cs:947-973`); a projected reference column becomes its code's type (`:937-938`); every foreign
  key between the selected tables, or towards a table an entity is already bound to, becomes a relation (`:779-823`).
  These operations plan against the loaded snapshot, not against earlier operations of the batch
  (`ModelStore.Schemas.cs:26-47`), and resolve the whole model (`Materializer.cs:725`, `:999`).
- Their preview resolves the whole candidate model to collect the binding rules (`ModelStore.Materialize.cs:111-117`).
  Fact recording resolves only the databases a write reaches (`ModelStore.Migrations.cs:659-672`, through
  `Resolution/ModelResolver.cs:79`). No file under `Resolution/` imports `Maquettiste.Engine.Loading`.
- `apply_batch`'s description is in the CLI's MCP host (`src/Maquettiste.Cli/Mcp/ModelTools.cs:197`);
  `preview_materialize` maps its `op` to a request (`Agent/AgentTools.Handlers.cs:308-326`), and so does the HTTP
  preview (`src/Maquettiste.Functions/_functions/DatabaseEndpoints.cs:210-221`, body `:228-237`). Exit code 2 is
  `ExitCodes.Drift`, "drift found by `--check`" (`src/Maquettiste.Cli/Program.cs:15-16`).
- An attribute's description is its own text only (`Resolution/ResolvedCore.cs:105-106`, filled by `MakeAttribute`,
  `Resolution/ResolveRun.Conceptual.cs:896`). The other direction exists: with `conventions.comments: descriptions`, a
  projected column takes its attribute's description as its comment (`Resolution/DatabaseRun.Finish.cs:175-186`).

**Decisions taken here**

1. **Provenance records each column, not one hash for the table.** A binding gains
   `provenance: { columns: { "<column id>": "<name part>:<type part>" } }`. The scope proposed one shape hash for the
   table; that tells that the table changed, not which column or how, and the report must name added, renamed and
   retyped columns. Each part is a short hash, so the binding records that a column changed and never holds a second
   copy of the table. The name part hashes the name in the loose form map by name compares (decision 7), so a change of
   case or separators, which no attribute name follows, is not a rename. The parts hash the table file's own values,
   not resolved ones, so a change to type maps or conventions does not make every binding drift, and the resolver, the
   operations and the report compute the same value. No time stamp or author is recorded: model files stay byte-exact.
2. **Provenance is a record, not a link.** Its keys are column ids, but nothing in it is an `[ElementRef]`, so the index
   does not walk it (`Model/ModelIndexer.cs:842-844` walks a nested record's typed members only) and a column delete is
   never blocked by it. A recorded id the source no longer has reads as removed. The canonical writer sorts map keys
   (`Json/CanonicalJson.cs:174`, `:193`), so the bytes are deterministic. 3.1's fold rewrites ids in place; it moves
   provenance keys with the ids it rewrites, so a fold reads as no drift.
3. **Table files only.** Provenance covers sources that are designed or imported table files, the only sources whose
   columns have ids. Their columns are the file's own entries and their inherited members (3.1's `TableMembers.Of`,
   keyed ids `<tableId>.<memberId>`), an inherited member hashed from its entry with the table's redefinition applied.
   A binding to a view, a query or a projected table records none and keeps MQ4047 alone.
4. **The by-name operations write provenance; hand edits never do.** An operation that creates a binding
   (`materialize-tables`, `materialize-entities`) records every column of the source. An operation that adds fields to
   a binding (`map-by-name`, `materialize-attributes`, `materialize-columns`) records the columns whose fields it
   writes, and every column of the source when the binding has none yet. `resync-binding` records every column of the
   source and drops the entries of removed columns, or with `columns` updates only those entries. When the operation
   also writes the table (`materialize-tables`, `materialize-columns`), the parts hash the entries it writes. An
   operation never refreshes an entry it does not write, so it never hides drift. A save, patch or batch update leaves
   provenance as it is. Removing `provenance` by hand stops drift reports for one binding (5.6's per-element
   suppressions are the other way).
5. **Drift, exactly.** For a binding with provenance whose source is a table file holding at least one recorded column:
   - **renamed**: a column a field maps whose name part differs;
   - **retyped**: a column a field maps whose type part differs;
   - **added**: a column not recorded that nothing in the binding names (no field, constant, listing or soft delete),
     whatever the database fills;
   - **removed**: a recorded id the source no longer has.

   A field's column is found as the resolver finds it (exact name, key or id, then loose name). A column that changed
   but that no field maps (a constant, a listed column) is not drift: nothing in the entity follows it. Provenance whose
   recorded ids are all foreign to the source belongs to another source (the binding was pointed elsewhere by hand):
   MQ4088, and the binding is read as having none. The lists follow the source's column order (own entries in file
   order, then inherited members in 3.1's order); removed ids are in ordinal order.
6. **How MQ4047 and MQ4087 split the work.** MQ4047 stays as it is: on every binding, a column nothing accounts for, new
   or old. MQ4087 is new: on a binding with provenance, a column a field maps that was renamed or retyped since it was
   recorded. Added columns the database fills are accounted for, so no rule reports them; the drift report and re-sync
   list them. A removed column a field still maps is MQ4045 already. MQ4047, MQ4087 and MQ4088 take the severity
   `validation.rules` sets; at `error` they fail validate and stop generation (`Generation/GenerationRun.cs:222-223`).
   All three are resolver findings beside each other in `DatabaseRun.Bindings.cs`, so a save that renames a column is
   never refused for drift, even when the project sets MQ4087 to `error`; validate reports it in the CLI, MCP, the
   editor and CI. MQ4087 points at `/bindings/<i>` and lists the binding's columns in one diagnostic, as MQ4047 does.
7. **Map by name in the engine.** The batch operation `map-by-name` (`database`, `entities`) matches, for each entity
   bound to the database, every attribute its binding does not map to a free column of the same loose name: lowercase,
   with every character that is not an ASCII letter or digit dropped, as the editor compares (`fieldMap.ts:296-298`).
   The candidates are the names a field may target, as generation names them: an attribute's name (own, inherited or a
   stereotype's), `attributeMember` for a member of an embedded value object attribute, and a to-one navigation's name
   plus `Id`, which also matches without `Id` (`fieldMap.ts:493`). A free column is one no field, constant, listing or
   soft delete names; each column matches once, in the source's column order. Fields hold column ids for a table file
   and names for a view. A query source is refused (MQ4089), as `materialize-attributes` refuses one. It writes no
   attribute and no column.
8. **Re-sync.** The batch operation `resync-binding` (`database`, `entities`, optional `columns` for one entity as keys
   or physical names, `keepNames`) brings each binding whose source is a table file into step, in one batch, all or
   nothing:
   1. **renamed**: an own attribute whose loose name hashes to the column's old name part (it followed the column)
      takes the column's name as materialize names attributes (camel-cased, made unique among the entity's
      attributes); an attribute that never followed its column keeps its name, and `keepNames` keeps every name;
   2. **retyped**: an own attribute keeps a reference type, enum or scalar type the column's new type still fits (3.2's
      `CodeFit` and the fit of MQ4083), and otherwise takes the type materialize would write; its facets and `required`
      follow the column;
   3. **map by name** for the attributes the binding does not map and the free columns;
   4. **added**: an attribute per added column still free, mapped to it, shaped as `materialize-attributes` shapes it
      and typed as decision 11 says;
   5. **removed**: a field whose column is gone is removed; its attribute stays, since re-sync never deletes attributes;
   6. **provenance** as decision 4 says.

   Inherited and stereotype attributes are not renamed or retyped; a note names them. A binding without provenance
   re-syncs from scratch: steps 3, 4 and 6. A binding already in step is skipped with a note; the operation is refused
   (MQ4089) only when every binding it names is in step, or for the misuses MQ4089 lists.
9. **Names follow the table only where they followed it.** A renamed or retyped attribute keeps its id, so every link
   holds. Renaming an attribute changes generated names; when the entity is also projected into another database, it
   changes that projection, and the dry run lists the migration facts the change would record (`BatchPreview.Facts`,
   `ModelStore.cs:1564-1568`).
10. **Previews never touch the whole model.** `map-by-name`, `resync-binding` and the drift report read the named
    entities, their bases and effective stereotypes, their source table files and the stereotypes those inherit from;
    they never resolve the model. Their plan collects the binding rules from a resolution of the conceptual layer and
    the named database only (`ModelResolver.cs:79`, the scope fact recording uses), never the whole candidate. The
    existing materialize operations keep their resolution.
11. **Materialize types an attribute by its column's type.** `materialize-entities`, `materialize-attributes` and
    re-sync type an attribute by its column's `typeRef` (3.2), or by the reference type of a projected reference column
    (a source no table file holds): one attribute, `type: { ref }`, with no length, precision or scale (the type states
    them), required when the column is not nullable. `materialize-entities` makes no relation and no relation mapping
    for a foreign key that pairs a column typed by a reference type with the code column of that type's bound table; a
    note says the attribute is typed by the type instead.
12. **An attribute without a description shows its column's comment.** In the resolved model, an attribute with no
    description of its own takes the comment of the column its entity's binding maps it to (`RColumn.Comment`, the text
    the database gets). Bindings are read in database name order, then id, and the first comment wins;
    `RAttribute.DescriptionColumn` names that column. Such an attribute depends on the tables of the columns its
    bindings map, whether or not they carry a comment, so a comment added later replans what reads it. Value object
    members and relation keys are left out, since a member is shared by every entity that uses its value object.
    Materialize writes no description, as today (`Materializer.cs:947-973`), so the text stays in one place. The
    fallback runs in the pass 3.2 adds after every database (`Resolution/ResolveRun.Shared.cs`), so projected columns'
    comments (`DatabaseRun.Finish.cs:175-186`) keep reading the attribute's own description and nothing cycles.
13. **`BindingProvenance.Columns` is `required`** (D30). The schema requires `columns`, and the house rule is that a
    schema-required member is `required` in its record with no initializer (`SchemaConsistencyTests` compares the two):
    an initialized `Columns` would need a schema default of `{}`, which the canonical writer would then drop from an
    empty map, writing `"provenance": {}`, which the schema refuses.
14. **An empty `provenance.columns` is not stale** (D30): it describes a source that had no columns, so every column
    the binding does not name reads as added. Stale means at least one recorded id and none of them a column of the
    source. The operations of D33 write no empty provenance.
15. **A source that is nothing of the binding's database has no drift** (D30): a missing source, or an element of
    another database, is MQ4044's finding; `BindingDrift` reads it as unresolved, neither stale nor drifted, so MQ4088
    covers only a source that resolves to a view, a query, a projected table key or an overlay.
16. **MQ4088 points at `/bindings/<i>/provenance`** (D31), the key to remove or re-sync; MQ4087 points at the binding,
    as decision 6 says.
17. **Plans that resolve one database say so through an internal interface** (D33). `IDatabaseResolver`
    (`Resolution/ModelResolver.cs`) carries `ResolveDatabasesAsync`, which `ModelResolver` already had; fact recording
    and the binding plans ask for it instead of the concrete `ModelResolver`, so a test resolver that counts whole-model
    resolves can still answer one database. A resolver without it answers from a whole-model resolve, as before. No
    public signature changes.
18. **Map by name reads table files and views only** (D33). A projected table key or an overlay source has columns only
    after resolution, which the operation never runs (decision 10), so it is refused as "a source it cannot read", as a
    query is. A stale provenance is read as none, so an operation that adds fields replaces it with every column.
19. **Re-sync details** (D33). Without a provenance, or with a stale one, step 4 adds the columns nothing accounts for
    (the drift's added list then), and step 6 records every column even with `columns`. Step 5 removes only fields that
    name a removed recorded id, and runs before the match by name, so a column dropped and added again under the same
    name moves its attribute to the new column instead of adding a second one, also when the re-sync names columns (the
    gone column's provenance entry goes with its field). A binding is in step when the re-sync would change nothing in
    the entity's file. Whether an attribute keeps its type is MQ4083's fit (`ColumnTypeRules.Holds`), and only while the
    column's own `typeRef`, if any, names the same type.
20. **The contract catches up with 3.1** (D33): `docs/api/openapi.yaml`'s `BatchOp` gains `fold-into-stereotype` beside
    `map-by-name` and `resync-binding`, and `BatchOperation` gains `redefine` and `excludeMissing` beside `keepNames`.
21. **The description fallback reads the read column first** (D34): within one binding, a field's source column is read
    before its write column, fields in file order; bindings in database name order, the order of `REntity.Bindings`. The
    fallback and 3.2's sensitivity share one walk of `ResolveRun.Shared.cs`, so an attribute that states a sensitivity
    but no description now depends on its bound columns' entries too (`RTable.EntryKeys`, as in 3.2).
22. **The drift report takes a database by id or name** (D35): `BindingDriftQuery.Database` and `get_binding_drift`'s
    `database` accept either, as `list_migrations` does; an unknown database, an unknown entity id or a cursor no page
    returned is an `ArgumentException` or `FormatException`, which the tool answers as `bad-request`. The CLI resolves
    entity and database names first, so an unknown one is a usage error (exit 4), as in `model materialize`.
23. **`binding map` and `binding resync` print as `model materialize` does** (D35): `ModelCommand.One` and
    `ModelCommand.MaterializeText` become internal so `BindingCommand` shares them; `--column` with more than one entity
    is a usage error (exit 4) before the engine is asked, as `--attribute` is for `materialize columns`.
24. **The stored table keeps the overlay's id, not its file name** (D36): `materialize-tables` writes Invoice's table to
    `tables/invoices.json`, the file named after the table; the acceptance tests check the id
    `01J92P0V1T0J6RH4MY9H81NYB4` and that path.
25. **MQ4087 has no quick fix yet** (D31). The rules table gives it "Re-sync the binding", but a catalog quick fix is
    one value of a closed contract enum the editor acts on, and `resync-binding` needs a database as well as the
    finding's element. Both wait for the editor follow-ups below; until then `maquettiste binding resync` is the fix.

#### Schema and files

`entity.json`, binding items: `provenance` follows `delete` in `x-order`.

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| `provenance` | object, `x-order` `["columns"]`, `required: ["columns"]`, no other keys | absent | Written by the by-name operations (decision 4) |
| `provenance.columns` | object; `propertyNames` an id or a keyed id `<id>.<id>`; values `^[0-9a-f]{8}:[0-9a-f]{8}$` | | Column id to its name part and type part |

`batch.json`: `op` gains `map-by-name` and `resync-binding`; `keepNames` (boolean, `resync-binding` only) joins the
operation's keys; `columns` also serves `resync-binding` (keys or physical names, one entity). Both files get copies
under `samples/reference-app/.maquettiste/.schema/v1/`.

Example, on `InvoiceNote`'s binding to `notes` after a re-sync (hash values illustrative):

```json
"provenance": {
  "columns": {
    "01K6BND0000000000000000002": "8a1f3c20:5be0d7a9",
    "01K6BND0000000000000000003": "41d09e7b:77c2a015",
    "01K6BND0000000000000000004": "0c7e91d4:2f64a8b1",
    "01K6BND0000000000000000005": "e3b20c44:9a71f0d2",
    "01K6BND0000000000000000006": "5f8a2b19:c03e6d48"
  }
}
```

**The shape hash.** `Model/ColumnShapeHash.cs` (new, internal) computes, from a column entry of a table file (an
inherited member's entry with its redefinition applied):

- the name part: the first 8 lowercase hex digits of SHA-256 over the UTF-8 bytes of the loose name (decision 7);
- the type part: the first 8 digits of SHA-256 over the UTF-8 bytes of eight lines joined by `\n`: `type`, `subtype`,
  `typeRef.ref`, `length`, `precision`, `scale`, `nativeType`, `nullable`. Numbers are invariant text, `nullable` is
  `true` or `false` with absent read as `true`, and any other absent value is empty.

Eight digits are 32 bits: enough to tell a change; a collision hides one change to one column, never corrupts anything.
The line list is part of the format: a later facet joins only with a new part, never by changing these lines, or every
binding would drift at once.

#### Drift in the resolver and the resolved model

- `Model/BindingDrift.cs` (new, internal) computes one binding's drift from files, as decision 5 says: renamed,
  retyped, added (with the status the resolver's accounting gives, `DatabaseRun.Bindings.cs:640-655`: `unaccounted`,
  `identity`, `default` or `computed`) and removed, and whether the provenance is stale. It lives in `Model/`, beside
  3.1's `Model/TableMembers.cs`, because the resolver, the drift report and re-sync share it and the resolver uses no
  `Loading/` type.
- `DatabaseRun.Bindings.cs` calls a `ReportDrift` step after `ReportUnaccounted` (`:179`) for a binding with
  provenance: MQ4088 when its source is not a table file or the provenance is stale, else MQ4087 when a column is
  renamed or retyped. Example text: "The binding of entity
  'Invoice' to database 'main' maps columns that changed since its provenance was recorded: 'invoice_number' renamed
  (attribute 'number'). Re-sync the binding (resync-binding)."
- `ResolveRun.Shared.cs` (from 3.2's D23) gains the description fallback of decision 12. `RAttribute.Description` is
  the effective text, and `RAttribute.DescriptionColumn` the column it came from. `AttributeRecord` gains
  `DescriptionColumn` (the column's key).

#### Rules

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ4087 | W | A column a binding maps was renamed or retyped since its provenance was recorded, so the attribute may no longer follow it. | Re-sync the binding |
| MQ4088 | W | A binding's provenance does not describe its source: the source is not a table file, or none of the columns it records belongs to the source. | Re-sync, or remove `provenance` |
| MQ4089 | E | A map-by-name or resync-binding operation is refused: an entity with no binding to the database, a source it cannot read, columns for more than one entity or naming a column the source lacks, or nothing to map or re-sync. | Fix the operation |

MQ4047's text does not change. MQ4089 wraps the refusals of the two operations where `ModelStore.Schemas.cs:41` wraps
materialize refusals in MQ4055. "A source it cannot read" is a query for `map-by-name`, and anything but a table file
for `resync-binding`.

#### Batch operations

| Op | Keys | Writes | Refused (MQ4089) |
| --- | --- | --- | --- |
| `map-by-name` | `database`, `entities` | Fields, and provenance for table files | An entity with no binding to the database; a query source; no attribute matches for every entity named |
| `resync-binding` | `database`, `entities`; `columns` (one entity); `keepNames` | Attributes, fields, provenance | No binding; a source that is not a table file; `columns` misused; every binding in step |

Both run in `Loading/Materializer.Bindings.cs` (new; `Materializer` becomes partial) through the materializer's switch
(`ModelStore.Materialize.cs:65-79`) and are answered as a `MaterializePlan`, so `preview_materialize`,
`POST /api/model/databases/{id}/materialize/preview` and `apply_batch` with `dryRun` show the creates, updates, notes
and diffs before anything is written. `IsBindingOperation` sits beside `IsMaterializeOperation`
(`ModelStore.Materialize.cs:20`), and `IsExpandedOperation` (`ModelStore.cs:395-396`) includes it. Like every expanded
operation, they plan against the saved model, so a binding created in the same batch is not seen.

#### CLI and MCP

- **`get_binding_drift`** (new catalog tool, MCP and the assistant). Input: `database` and `entities` (ids, optional),
  `all` (default false: only bindings with something to report), `cursor`, `limit`. Answer: `{ bindings, next }`. Each
  binding holds `entity`, `entityName`, `binding`, `database`, `databaseName`, `source`, `sourceName`,
  `hasProvenance`, `stale`, `added`, `renamed`, `retyped` and `removed`, ordered by entity name, entity id, database
  name, then database id. `added` items are `{ column, name, status }`; `renamed` and `retyped` items are
  `{ column, name, attribute, attributeName }`; `removed` holds column ids. Pass `next` as `cursor` until it is null.
  It reads files only.
- **`apply_batch`** takes `map-by-name` and `resync-binding` (its description in `ModelTools.cs`);
  **`preview_materialize`** takes them as `op`, with `keepNames`, and so does the HTTP preview. The rows in
  `docs/mcp.md` change.
- **CLI**, a new group `binding`:
  - `maquettiste binding drift [<entity>...] [--database <id|name>] [--all] [--check] [--format text|json]` prints the
    report; `--check` exits 2 (`ExitCodes.Drift`) when a listed binding has drift.
  - `maquettiste binding map --database <id|name> <entity...> [--dry-run] [--format text|json]` maps by name.
  - `maquettiste binding resync --database <id|name> <entity...> [--column <key|name>]... [--keep-names]` with
    `[--dry-run] [--format text|json]` re-syncs. `--column` is for one entity only.

  Entity arguments take ids or names, as `model materialize` does. Exit codes follow `model materialize`: 0 saved,
  1 refused or invalid, 3 on a conflict. `--all` and `--keep-names` join `CommandLine.FlagOptions`; `--database`,
  `--column`, `--check` and `--dry-run` exist already.
- Pins: the `McpSurfaceTests` Tools array gains `get_binding_drift`, and the `AgentTools.All` count rises by 1 in
  `McpSurfaceTests` and `AgentToolsTests`, from whatever they read when D35 lands. No HTTP endpoint is added for the
  report in phase 2; the editor follow-up adds `GET /api/bindings/drift`.

**Public API**

```csharp
namespace Maquettiste.Engine.Model;
public sealed record EntityBinding
{   // existing members, plus:
    /// <summary>The columns of a table-file source as the last by-name operation recorded them; written by the engine only.</summary>
    public BindingProvenance? Provenance { get; init; }
}
public sealed record BindingProvenance
{   /// <summary>Column id to "&lt;name part&gt;:&lt;type part&gt;", 8 hex digits each. Ids here are records, not references.</summary>
    public required IReadOnlyDictionary<string, string> Columns { get; init; }   // required: decision 13
}

namespace Maquettiste.Engine;
public enum BatchOp { /* existing members */ MapByName /* map-by-name */, ResyncBinding /* resync-binding */ }
public sealed record BatchOperation(/* existing parameters */, bool? KeepNames = null);
public sealed record MaterializeRequest(/* existing parameters */, bool KeepNames = false);
public sealed partial class ModelStore
{
    public static bool IsBindingOperation(BatchOp op);
    public Task<BindingDriftPage> GetBindingDriftAsync(BindingDriftQuery query, CancellationToken ct);
}
public sealed record BindingDriftQuery(string? Database = null, IReadOnlyList<string>? Entities = null, bool All = false,
    string? Cursor = null, int Limit = ModelPages.DefaultLimit);
public sealed record BindingDriftPage(IReadOnlyList<BindingDriftEntry> Bindings, string? Next);
public sealed record BindingDriftEntry(string Entity, string EntityName, string Binding, string Database, string DatabaseName,
    string Source, string SourceName, bool HasProvenance, bool Stale, IReadOnlyList<DriftColumn> Added,
    IReadOnlyList<DriftField> Renamed, IReadOnlyList<DriftField> Retyped, IReadOnlyList<string> Removed);
public sealed record DriftColumn(string Column, string Name, string Status);
public sealed record DriftField(string Column, string Name, string Attribute, string AttributeName);
public sealed record AttributeRecord(/* existing parameters */, string? DescriptionColumn);

namespace Maquettiste.Engine.Resolution;
public sealed class RAttribute
{   /// <summary>The column whose comment <see cref="RAnnotated.Description"/> shows, when the attribute has no description of its
    /// own; otherwise <see langword="null"/>.</summary>
    public RColumn? DescriptionColumn { get; internal set; }
}
```

#### Editor follow-ups

- The Storage tab shows each binding's drift (added, renamed, retyped, removed) with a **Re-sync…** action that opens
  the dry run's diffs, and a **Keep names** choice.
- **Map by name…** and **Auto-map** call `map-by-name`, so the binding records provenance. Auto-map creates the binding
  first and maps in a second save, since an operation plans against the saved model.
- The Attributes tab shows a bound attribute's fallback description greyed, marked "from column `<table>.<column>`".
- `GET /api/bindings/drift` serves the panel.

#### Docs

- `docs/guides/bind-entities.md`: new section "Keep a binding in step with its table" after section 6, the three
  `binding` verbs under "From a terminal", and the rules link widened to MQ4087 to MQ4089.
- `docs/user-guide.md`: bindings record provenance; drift; how MQ4047 and MQ4087 split the work; the description
  fallback; MQ4087 to MQ4089 in "The rules for bindings".
- `docs/mcp.md`: rows for `get_binding_drift`, `apply_batch` and `preview_materialize`; the read-tools list and the tool
  count.
- `skills/maquettiste-modeling/SKILL.md`: `get_binding_drift`, `map-by-name` and `resync-binding` beside the materialize
  operations.
- `docs/releases.md`, under `## 0.12.0 (unreleased)`: one entry.

**Not doing**

- A stored by-name binding mode: the map stays explicit, attribute id to column id (scope section 10).
- Provenance and drift for views, queries and projected tables, whose columns have no ids.
- Map by name over a query's fields: they resolve from the query, which the operation does not resolve.
- Relations inferred from foreign keys at resolve time (scope section 10): materialize writes them.
- Re-sync that deletes attributes, or that adds or changes columns: the entity follows the table, never the reverse.
- Renaming or retyping value object members and relation keys in re-sync: they map, and a note names them.
- An engine Auto-map that creates bindings: the editor keeps it and calls `map-by-name`.
- A time stamp or author in provenance (decision 1).
- Column comments taken from bound attributes' descriptions: a table's comments stay its own.
- Former names in the drift report: `migration list` already shows open rename facts.

**Tests**

- `ColumnShapeHashTests` (new, `Model`): `Name_part_ignores_case_and_separators`,
  `Name_and_type_parts_change_independently`, `Absent_nullable_reads_as_true`, `A_typeRef_change_moves_the_type_part`,
  `An_inherited_column_hashes_its_redefined_entry`, `Pinned_value_for_a_known_column`.
- `BindingDriftTests` (new, `Model`): `A_new_column_nothing_names_is_added`, `A_new_column_with_a_default_is_added_too`,
  `A_renamed_mapped_column_is_renamed`, `A_case_only_rename_is_not_drift`, `A_retyped_mapped_column_is_retyped`,
  `A_field_naming_its_column_by_name_is_followed`, `A_changed_constant_column_is_not_drift`,
  `A_recorded_column_gone_is_removed`, `Provenance_of_another_table_is_stale`,
  `A_binding_without_provenance_reports_only_unaccounted_columns`, `Lists_follow_the_source_column_order`.
- `ModelSnapshotTests.Provenance_ids_are_not_references`; `ModelStoreDeleteTests.A_column_delete_ignores_provenance`.
- `BindingTests`: `A_renamed_column_is_MQ4087_at_the_binding`, `MQ4087_takes_the_project_severity`,
  `MQ4087_off_reports_nothing`, `Stale_provenance_is_MQ4088`, `An_added_column_is_MQ4047_not_MQ4087`,
  `An_attribute_without_a_description_shows_its_column_comment`, `An_own_description_wins`,
  `The_first_database_by_name_gives_the_comment`, `A_value_object_member_takes_no_fallback`.
- `DependencyKeyTests.An_attribute_without_a_description_depends_on_its_bound_column_table`.
- `CommentConventionTests.A_projected_column_comment_reads_the_attribute_own_description`.
- `ValidateResolverFindingsTests.Validate_reports_binding_drift`.
- `MaterializeTests`: `Materialize_entities_types_an_attribute_by_its_column_typeRef`,
  `Materialize_entities_makes_no_relation_for_a_key_into_the_types_bound_table`,
  `Materialize_attributes_types_by_typeRef`, `Every_materialize_operation_records_provenance`.
- `BindingSyncTests` (new, `Store`): `Map_by_name_matches_case_and_separators_and_writes_ids`,
  `Map_by_name_matches_members_and_navigation_keys`, `Map_by_name_on_a_view_writes_names_and_no_provenance`,
  `Map_by_name_on_a_query_is_refused`, `Resync_renames_attributes_that_followed_their_column_and_keeps_ids`,
  `Resync_keeps_a_name_that_never_followed_its_column`, `Resync_with_keep_names_records_the_new_name`,
  `Resync_retype_keeps_a_ref_type_that_still_fits`, `Resync_adds_attributes_for_added_columns`,
  `Resync_removes_fields_of_removed_columns_and_keeps_attributes`, `Resync_with_columns_keeps_other_entries`,
  `Resync_notes_inherited_attributes`, `Resync_of_bindings_in_step_is_refused`,
  `Binding_operations_and_their_plan_never_resolve_the_whole_model` (a resolver that counts whole-model resolves).
- `StereotypeFoldTests.The_fold_moves_provenance_keys`.
- `BatchPreviewTests.A_resync_dry_run_lists_the_facts_of_a_projected_entity`.
- `AgentToolsTests.Get_binding_drift_lists_and_pages`; `McpReadTests.Get_binding_drift_answers_only_drifted_bindings`;
  `McpWriteTests.Apply_batch_resync_binding_is_one_batch`; `McpSurfaceTests` (the pins and the docs list).
- `BindingCommandTests` (new, CLI): `Drift_prints_added_renamed_and_retyped`, `Drift_check_exits_2_on_drift`,
  `Map_dry_run_writes_nothing`, `Resync_keep_names_keeps_attribute_names`, `Resync_column_takes_a_name_or_a_key`.
- **Acceptance (scope 3.5, done when, first part):**
  `Integration/BindingProvenanceTests.A_new_invoice_column_is_drift_and_one_resync_maps_it` (new). Setup: a billing
  copy where `materialize-tables` stores `Invoice`'s table in `main`, keeping the overlay's id
  (`tables/01j92p0v1t0j6rh4my9h81nyb4.json`), and binds `Invoice` to it with provenance. Then the column `due_on`
  (`date`, nullable) is added to that table file. Expected: `entities/invoice.json` keeps its hash.
  `binding drift --database main Invoice` lists `due_on` as added. Validate reports MQ4047 on `Invoice` as a warning,
  and as an error that makes validate exit 1 when `maquettiste.json` sets `validation.rules` `MQ4047` to `error`.
  `binding resync --database main Invoice` writes, in one batch, the attribute `dueOn` (`date`), the field mapping it to
  `due_on` and the column's provenance entry; validate then reports neither MQ4047 nor MQ4087 on `Invoice`.
- **Acceptance (renamed):**
  `Integration/BindingProvenanceTests.A_renamed_invoice_column_is_MQ4087_and_resync_renames_the_attribute`. Setup: the
  same copy after `materialize-tables`, with the column `number` renamed to `invoice_number`. Expected: MQ4087, a
  warning, at `/bindings/0` of `entities/invoice.json`, naming `invoice_number` and the attribute `number`. Re-sync
  renames the attribute, which followed the column, to `invoiceNumber` and keeps its id `01J92P0V0R3VSP7D5238DTNZX1`.
  With `--keep-names` the name stays `number` and the provenance entry takes the new name part. Either way MQ4087 is
  gone.
- **Acceptance (scope 3.5, done when, last part):**
  `Integration/BindingProvenanceTests.An_event_status_typed_by_a_reference_type_becomes_one_attribute`. Setup: the
  billing copy of 3.2's first acceptance test, where `InvoiceState` is bound to `invoice_states` and `invoice_events`
  declares `fk_event_state` from `status` to `invoice_states.code`; `materialize-entities` makes an entity of
  `invoice_events` in package `billing`. Expected: `InvoiceEvent` has one `status` attribute,
  `type: { "ref": <InvoiceState> }`, required, with no length; no relation and no relation mapping for
  `fk_event_state`, and a note saying the attribute is typed by `InvoiceState`; the binding records provenance for its
  four columns.
- **Acceptance (comment fallback):** `Integration/BindingProvenanceTests.An_invoice_note_body_shows_the_column_comment`.
  Setup: a billing copy where the `body` column of `notes` gets `comment: "Text of the note, as typed."`. Expected: the
  resolved `body` attribute of `InvoiceNote` has that description and `descriptionColumn` the column's id;
  csharp-dapper's `InvoiceNote.g.cs` documents `Body` with `    /// <summary>Text of the note, as typed.</summary>`.
  Giving the attribute a description of its own replaces both.

**Tasks**

- **D30. Provenance, the shape hash and the drift of one binding.** Runs after D20 (`Column.TypeRef`; D20 follows 3.1's
  D11, so `Model/TableMembers.cs` exists and D2, which owns `entity.json`, is done) and D24 (the last 3.2 task that
  regenerates the editor's types before this one). Owns: `schemas/v1/entity.json` and its copy, the regenerated editor
  types and mocks, `Model/Bindings.cs` (`EntityBinding.Provenance`, `BindingProvenance`), `Model/ColumnShapeHash.cs`
  (new), `Model/BindingDrift.cs` (new). Tests: `ColumnShapeHashTests`, `BindingDriftTests`, the `ModelSnapshotTests` and
  `ModelStoreDeleteTests` cases, `SchemaConsistencyTests`. Verify:
  `E "~Model.|~Json.|~Loading.|~ModelStoreDeleteTests"`; `F "~ContractTests"`; `editor`; `copies`.
- **D31. The drift rules.** Runs after D30, D22 (`RuleCatalog.cs`) and D23 (`DatabaseRun.Bindings.cs`). Owns:
  `ReportDrift` in `Resolution/DatabaseRun.Bindings.cs`, MQ4087 and MQ4088 in `Diagnostics/RuleCatalog.cs`,
  `CatalogTests.cs` (+2). Tests: the drift cases of `BindingTests`, `ValidateResolverFindingsTests`. Verify:
  `E "~CatalogTests|~BindingTests|~ValidateResolverFindings"`; `record`.
- **D32. Materialize types attributes by their column's type.** Runs after D21 and D25 (both precede it in
  `Materializer.cs`). Owns: `Source`, `AttributeFor` and the relation step of `EntitiesAsync` in
  `Loading/Materializer.cs`. Tests: the typing cases of `MaterializeTests`. Verify: `E "~MaterializeTests"`.
- **D33. Map by name and re-sync; provenance from every by-name operation.** Runs after D24 (`openapi.yaml`), D30, D31,
  D32 and D11 (3.1: `ModelStore.cs`, `ModelStore.Schemas.cs`, `Loading/BatchParser.cs`, `Loading/StereotypeFold.cs`,
  `batch.json`). Owns: `BatchOp.MapByName`, `BatchOp.ResyncBinding`, `BatchOperation.KeepNames` and
  `IsExpandedOperation` in `ModelStore.cs`, `ModelStore.Materialize.cs` (`IsBindingOperation`, `ToRequest`,
  `MaterializeRequest.KeepNames`, the materializer's switch, the binding plan's one-database resolution),
  `ModelStore.Schemas.cs` (dispatch, the MQ4089 wrap), `Loading/BatchParser.cs`, `Loading/Materializer.cs` (partial, a
  file-level `Source(Column)`, the provenance writes of the four materialize operations),
  `Loading/Materializer.Bindings.cs` (new: `MapByNameAsync`, `ResyncAsync`), the provenance keys in
  `Loading/StereotypeFold.cs`, `schemas/v1/batch.json` and its copy,
  `src/Maquettiste.Functions/_functions/DatabaseEndpoints.cs` (the preview's ops and `KeepNames`), the batch op enum,
  `keepNames` and the materialize preview body in `docs/api/openapi.yaml`, the regenerated editor types and mocks,
  MQ4089 in `Diagnostics/RuleCatalog.cs`, `CatalogTests.cs` (+1). Tests: `BindingSyncTests`,
  `Every_materialize_operation_records_provenance`, the `StereotypeFoldTests` case, `BatchPreviewTests`. Verify:
  `E "~BindingSyncTests|~MaterializeTests|~ModelStoreBatchTests|~BatchPreviewTests"`;
  `E "~StereotypeFoldTests|~CatalogTests|~FactRecordingTests"`; `F "~ContractTests|~BulkReadTests"`; `record`; `editor`;
  `copies`.
- **D34. An attribute without a description shows its column's comment.** Runs after D23, D26 (pack goldens) and D33
  (`openapi.yaml`). Owns: the description part of `Resolution/ResolveRun.Shared.cs`, `RAttribute.DescriptionColumn` in
  `Resolution/ResolvedConceptual.cs`, `AttributeRecord` in `Editor/ResolvedRecords.cs`, `AttributeRecord` in
  `docs/api/openapi.yaml`, the regenerated editor types and mocks. Tests: the description cases of `BindingTests`, the
  `DependencyKeyTests` case, `CommentConventionTests`. Verify: `E "~Resolution.|~Editor."`; `P "~GoldenTests"`;
  `F "~ContractTests"`; `editor`. No pack fixture has a bound attribute without a description over a commented column
  today; if `P "~GoldenTests"` shows one, run it once with `MAQUETTISTE_UPDATE_GOLDEN=1` and name the change
  "attribute descriptions from bound column comments".
- **D35. Drift and re-sync through MCP and the CLI.** Runs after D31 and D33. Owns: `ModelStore.BindingDrift.cs` (new:
  `GetBindingDriftAsync` and its records), the `get_binding_drift` handler in `Agent/AgentTools.cs` and
  `Agent/AgentTools.Handlers.cs`, the new ops and `keepNames` in the `preview_materialize` handler
  (`Agent/AgentTools.Handlers.cs`), `Agent/tools.json` (the new tool; `preview_materialize`),
  the `apply_batch` description in `src/Maquettiste.Cli/Mcp/ModelTools.cs`,
  `src/Maquettiste.Cli/Commands/BindingCommand.cs` (new), the dispatch and usage in `src/Maquettiste.Cli/CliApp.cs`,
  `src/Maquettiste.Cli/CommandLine.cs` (`--all`, `--keep-names`), the rows, the read-tools list and the tool count in
  `docs/mcp.md`, `README.md` (the tool count, which `McpSurfaceTests` reads), the pins (the `McpSurfaceTests` Tools
  array and `AgentTools.All` count, the `AgentToolsTests` `AgentTools.All` count). Tests: `AgentToolsTests`,
  `McpReadTests`, `McpWriteTests`, `BindingCommandTests`. Verify:
  `E "~AgentToolsTests"`;
  `C "~McpSurfaceTests|~McpReadTests|~McpWriteTests|~BindingCommandTests|~ParserAndGlobalsTests"`.
- **D36. Acceptance, guides, the skill and the release note.** Runs after D27 (the guides, the skill and the notes),
  D32, D34 and D35. Owns: `docs/guides/bind-entities.md`, the binding parts of `docs/user-guide.md`, the 3.3 lines of
  `skills/maquettiste-modeling/SKILL.md`, the 3.3 entry in `docs/releases.md`, `Integration/BindingProvenanceTests.cs`
  (new). Tests: the four acceptance tests. Verify: `E "~Integration.BindingProvenanceTests"`;
  `C "~McpSurfaceTests|~SkillRefreshTests"`.

### 3.4 Seeds by natural key, stable ids and scripted rows

**Why every team**

Every team that ships reference data, demo data or test fixtures into a database has to link rows across tables. Today a
seed row links to another row only by repeating that row's key value. So people type surrogate ids into seed files, copy
them from table to table and keep them in step by hand, and a table whose database generates its keys cannot be pointed
at from a seed at all. Three mechanisms close the gap for any project. A foreign-key cell names the row it points to by
that row's natural key, the value people already know (an email, an invoice number). A column may take a stable id: a
UUID that any tool computes again from the table and the row key, so ids survive rebuilds and agree across environments.
And a seed may compute its rows from the model with a script, so data that mirrors the model (one row per element of
some kind) is stated once.

**Today**

- A table seed lists column ids and finds its rows again by a row key: the unique constraint `key` names, else the
  primary key (`Model/ReferenceData.cs:110-121`, `Validation/TableSeedRules.cs:69-89`). The resolver takes the first
  table seed's key, else the key of a reference type bound to the table, else the primary key
  (`Resolution/ResolveRun.TableSeeds.cs:101`).
- A foreign-key cell holds the referenced column's value itself. MQ7111 checks that a seeded row has that value
  (`Validation/TableSeedRules.cs:266-285`). Resolved rows carry the cells as the database stores them
  (`Resolution/ResolvedPhysical.cs:1056-1084`, built in `Resolution/ResolveRun.TableSeeds.cs:52-61`), and sql-ddl writes
  them as literals (`packs/sql-ddl/_data.scriban:39-48`).
- A column the database fills (identity, generated, computed) cannot be seeded (MQ7108,
  `Validation/TableSeedRules.cs:51-55`). So no seed row can point at a row of a table whose key the database fills.
- Seed columns are keyed references (`Model/ReferenceData.cs:114`), so any sub-element id may stand there, and removing
  a sub-element drops its seed column and cells in the same save (`Loading/ChangePlanner.cs:431-471`).
- Row ids are ULIDs minted by the editor, batches and CSV import (`ModelStore.LocalizationApi.cs:499`). `row_uuid`
  writes a row's ULID as a UUID (`Rendering/BuiltinHelpers.cs:1650-1673`). Nothing derives an id from a key.
- `rowsFrom` names a CSV file beside the seed (`Model/ReferenceData.cs:146-151`, `schemas/v1/seed.json:127-139`), read
  as a sidecar (`Loading/DocumentReader.cs:539-544`, `Loading/ModelLoader.cs:522-552`). No rows are computed.
- The scope's `source: { "script": ... }` cannot be used: `source` is the import provenance every element carries
  (`Model/ElementBase.cs:54`, `:119-138`).
- Project scripts exist only as rule scripts in `extensions/rules/*.js` (`Loading/ModelLoader.cs:652`,
  `ModelStore.Extensions.cs:94-97`). They see the snapshot through `get`, `all`, `referencesTo`, `withStereotype`,
  `withTag`, `withProperty` and `byQualifiedName` (`Scripting/ScriptSandbox.Calls.cs:198-217`), each recording a read
  key: `e:`, `k:`, `ix:` and `rt:` (`:225-291`), which only the rule result cache hashes
  (`Validation/RuleKeyHasher.cs:8-19`).
- Planning's dependency keys know the families `e`, `k`, `r`, `s`, `l`, `t`, `d` and `n`
  (`Planning/DependencyHasher.cs:196-212`); any other key hashes as absent, so neither an extension file nor the rule
  view's `ix:` and `rt:` reads can make a unit render again.

**Decisions taken here**

1. **A foreign key is a seed column.** A table seed's `columns` may name a foreign key of its target by id, beside
   column ids. Its cell gives the referenced table's row key as that table's seeds write it: a value for a one-column
   key, an array in key order for a composite key. The foreign key's own columns are then not listed. CSV rows files,
   export and import keep one plain cell per column (a composite key is JSON array text), and phase 1's seed input by
   name (section 5.3) finds a foreign key by its name; an unnamed foreign key is given by id. A per-cell object form was
   rejected: it breaks the one-cell CSV shape and repeats the target on every row.
2. **The referenced row key** is the key the resolver gives the referenced table's rows: its table seeds' key
   (MQ7121), else the key of the reference type bound to it, else its primary key (`ResolveRun.TableSeeds.cs:101`). A
   cell is matched against the referenced table's rows in seed terms, as MQ7112 compares keys, across every seed that
   loads into that table. A key column that a foreign-key entry fills takes that entry's cell. A row key that holds
   some but not all columns of a foreign-key entry is MQ7126.
3. **Resolved at generation.** The resolver fills the foreign key's columns with the referenced row's values in database
   terms. Where the database fills a referenced value (identity, sequence, a default), the value stays null and the
   row's `refs` carry the referenced row and key, so a pack writes a lookup. sql-ddl writes it as a subquery on
   PostgreSQL; elsewhere MQ4056 reports it and the cell is NULL with a TODO comment.
4. **A natural-key cell is data, not a model link**, as a code cell is. It is not indexed: deleting a referenced row is
   not refused, and MQ7111 reports a cell that finds no row.
5. **A stable id is a column facet.** `stableId: true` goes on a `uuid` column the database does not fill. Every row a
   database receives into that table gets a derived value there (table seed rows, the rows of a reference type bound to
   the table, entity rows that reach it through a binding), unless the row's source already writes the column (a
   binding's field or constant). A table seed gives no cell for it (MQ7108).
6. **The algorithm is RFC 9562 UUID version 5** (name-based, SHA-1). The namespace is the table id's 128 bits as a UUID:
   the ULID's 16 bytes, big-endian, as `row_uuid` writes them. The name is the UTF-8 bytes of the row key's values in
   database terms, each as text, joined by U+001F. Text forms: a string as it is; an integer in invariant decimal
   digits with a leading `-` when negative; a decimal in invariant digits with no exponent, no trailing fractional zeros
   and no `.` when it is whole (`0`, never `-0`); a float or double as its shortest round-trip invariant text; `true` or
   `false`; a UUID in lowercase with hyphens; a date, time or timestamp as the cell writes it. PostgreSQL's
   `uuid_generate_v5` and the UUID libraries of most languages compute the same value.
7. **Version 5, not version 8.** RFC 9562 recommends version 5 for name-based ids, and version 8 fixes no hash, so other
   tools could not reproduce it. SHA-1's weakness does not matter for an id that is not a secret.
8. **A retired key's id never goes to another row, by construction.** The id is a function of the table and the key. No
   row with another key can get it, and a key that comes back is the same row by the meaning of a natural key. No list
   of retired keys is kept.
9. **The row key may hold neither the stable id column nor a value the database fills**, directly or through a
   foreign-key entry (MQ7125): the id would depend on itself, or on a value the model never knows. The rule judges the
   key of decision 2, whatever fills the table.
10. **The namespace is the table id.** Two tables never share stable ids, and a table file deleted and created again
    gets new ids. Changing `stableId` or a seed's row key changes the ids of rows already in databases: a `converge`
    seed then updates their keys, a `once` seed leaves them. The guide says so.
11. **Scripted rows: `rowsFrom: { "script": "seeds/<name>.js" }`.** The path is under `extensions/`, beside the rule
    scripts, and the extension file tools write it, so a hosted model with no file access can author one. A script file
    beside the seed file was rejected for that reason.
12. **Script shape.** A rows script calls `maquettiste.rows(fn)` exactly once. `fn(model, seed)` gets the snapshot
    through the rule-script model view and a frozen copy of the seed document. It returns an array of plain objects
    keyed by the seed's column names as `RSeedColumn.Name` gives them: `code`, `label` and `description` for a
    reference type, an attribute's name or an end's role, a table column's name, a foreign key's name for a foreign-key
    entry. Rows scripts load as rule scripts do (one scope per file, frozen globals, MQ5005 for top-level `let` or
    `var`) under the project's sandbox limits (`limits` in `maquettiste.json`). Several seeds may share a file; `seed`
    tells them apart. A rows script that calls `maquettiste.rule`, or a rule or pack script that calls
    `maquettiste.rows`, fails to load.
13. **The snapshot, not the resolved model.** A rows script reads model documents, as a rule does, so rows never depend
    on the resolution they feed, nor on a pack. Once 4.2 adds the `custom` kind, `model.all("custom")` lists custom
    elements like any kind, so a seed can take one row per element of a project's own kind (4.2 says how).
14. **Derived row ids, never named from the model.** A scripted row's id is the ULID text of a UUID version 5 with the
    seed id as namespace and the row key in seed terms as name, with the text forms of decision 6 (an array cell gives
    its items in order). The ids hold as long as the keys do, so previews, CSV export and templates see the same row
    ids on every run. Scripted rows are not in the index: an `x-ref` value, an end cell or a translation entry that
    names one is reported as any unknown id is. A target with no row key (a relation, an entity without a key, a table
    without a primary key or named key) takes no scripted rows (MQ7128).
15. **Lazy and cached.** A scripted seed's rows are built on first read: by a template reading `seed.rows`,
    `reference_type.rows`, `database.seed_tables` or `model.seeds_in_order`, by a preview that shows the seed, and by
    validation. Load and the index never run a script, and neither does an extension file write. A save runs one only
    when the seed is in its validation scope (the seed or its target changed). A preview runs one only when the unit it
    renders reads those rows. An engine-wide cache keys the rows by the seed's dependency hash, the script's path and
    hash, the sandbox limits and the hashes of the keys the last run read, as rule results are cached
    (`Validation/RuleResultCache.cs:42-57`).
16. **Planning.** Scripted rows carry, as their list's membership keys and each row's keys, the seed's keys, the
    script's `x:` key (new: the content hash of a file under `extensions/`) and the keys the run read.
    `DependencyHasher` hashes the rule view's `ix:` and `rt:` keys as `RuleKeyHasher` does, so a unit that read scripted
    rows renders again exactly when one of those keys changes.
17. **Validation.** Whole-model validation and a save in scope evaluate the rows and apply the row rules to them.
    Findings point at `/rowsFrom/script` and name the scripted row by key. Because scripted rows are not in the index, a
    save does not report a default, `allowedValues` entry or cell that names a code no stored row of the type holds when
    the type has a scripted seed; whole-model validation judges it against the stored and scripted rows. The refactors
    of 3.5 refuse such codes.
18. **PostgreSQL first.** Stable ids and natural-key values are the same on every dialect; only the lookup subquery is
    PostgreSQL's.
19. **`stableId` sits on `common.json`'s `tableColumn`** (found in D40): 3.1 moved the column definition there, so a
    stereotype's column takes `stableId` too, every carrier inherits it, and MQ7124 checks the stereotype's columns as
    it checks a table file's.
20. **MQ7125's table case runs only when an entity's binding writes the table** (found in D40). A table nothing fills
    yet has no rows to key, so a table file takes `stableId` before its seeds exist and its first seed then decides the
    key; otherwise a table keyed by a unique constraint could not be created before the seed that names that key. A
    binding writes the table as the resolver decides it: no `write` writes the source, `write.table` writes that table
    and `write: "none"` writes nothing, so a binding that only reads the table does not bring MQ7125.
21. **A reference type's binding never meets MQ7125, and MQ7120 skips a stable id column** (found in D40): the binding
    keys its rows by columns it writes, and the engine fills the stable id.
22. **A fold refuses a column whose `stableId` differs from its member's** (found in D40's review). The capture holds
    no `stableId`, so step 5 of 3.1's fold compares the resolved columns' flags, and a difference refuses the table as a
    type facet does. Taking the member's flag would add or drop the ids of rows already in databases, and `redefines`
    does not take `stableId`.
23. **The acceptance tables sit in schema `sales` of `main`** (found in D41). Billing projects its `Customer` and
    `Invoice` entities into `billing.customers` and `billing.invoices`, so table files of those names in schema
    `billing` break MQ4068 and render two files to one path (MQ6020). The tests add schema `sales` to `main` and keep
    every other name; the lookup reads `(SELECT id FROM sales.customers WHERE email = 'ada@example.com')`, quoted as the
    pack's `quoting` parameter says.
24. **Lookups follow lookups, and only a named row has a ref** (found in D41). A ref's `unknown` also lists a column
    whose referenced value is itself filled through the referenced row's own entry from a value the database fills, and
    sql-ddl nests the lookup in the `WHERE`. A null cell names no row: the row has no ref for it and its columns are
    null. A row depends on the keys of every seed of the tables its entries name, transitively, since a lookup reads
    them all. MQ7125 also judges a key column an entry of the deciding seed fills from a value the database fills.
25. **seed-data's `unresolved` is one list after `tables`**, written only when some cell needs it, so files of models
    without lookups stay as they were (found in D42). Each entry also gives the `row` id, its `index` in the table's
    rows (the CSV line, after the header), `schema`, `referencedSchema` and `referencedColumn`; `key` is the referenced
    row's key in database terms. Outside PostgreSQL sql-ddl writes `NULL /* TODO: row <id> names <table>.<column> WHERE
    <key column> = <value> */`.
26. **Scripted rows are matched to columns by the names the model files give** (found in D43), so validation and the
    resolver derive the same rows without resolving: an attribute's name, an end's role, a table column's name, and for
    a foreign-key entry the foreign key's name in its table file, else its id, as CSV files head it. A name the
    conventions give an unnamed foreign key is not a key.
27. **A scripted row without a row key is left out with MQ7128** (found in D43): a key the columns do not name, a key
    part the row leaves null, or a seed of a reference type that does not list `code`. The rest of the rows stay.
28. **MQ7127 sits on the seed's file at `/rowsFrom/script`** (found in D43), its message naming the script's path, line
    and column; a write of the script reports it on the script's own file with its line and column, and MQ5005 stays on
    the script's file.
29. **A reference type with a scripted seed builds all its rows on first read** (found in D43), its stored seeds' rows
    too, so each row keeps its place among the type's rows. None of them is found by id in the resolved model, as
    scripted rows are not. A failed script makes those reads fail with its MQ7127; the resolved model's records list
    no rows instead.
30. **A save judges only the scripted seeds in its scope** (found in D43). Decision 17's rule for codes also covers
    natural keys and foreign keys: a save does not report a key no judged row holds when a seed of the referenced table
    has a script outside the scope, and whole-model validation judges it.
31. **The validation previews wait on leaves scripted seeds out** (found in D43). The store's shared validation per
    snapshot, which previews, path listings and the resolved model's reader wait on, and the validation of the SQL
    previews and table lists, treat every seed with a rows script as a save treats one outside its scope (decision
    30): no script runs, and no code or key is reported missing for want of scripted rows. A preview runs a script only
    when its unit reads the rows (decision 15), and a failed script fails that unit. Validate, plan and generate judge
    every scripted seed; validate does so in a whole-model validation of its own per snapshot, run only when a seed has
    a rows script, and the rows cache keeps the runs they share.
32. **The seed preview names what it shows** (found in D44). `database` takes a database id or name, and
    `SeedRowsPreview.Database` answers the id. Without a database, `Values` are keyed by the seed's column names in
    column order, as written (a whole number as an integer, a fraction as a decimal); with one, by the table's column
    names, ordinal, as `RDataRow.Values` holds them. A ref's `ForeignKey` is the foreign key's name and `Table` the
    referenced table's name, schema-qualified when it has a schema. The cursor holds a row's position, so pages keep the
    seed's order. With a database, a model whose errors stop resolution answers no rows and those errors.
33. **Exports and imports of a scripted seed fail loudly** (found in D44). An export whose script fails throws its
    MQ7127 (`Scripting.ScriptErrorException`, public since public calls throw it): `export_seed_csv` answers `invalid`
    with it, the editor API's CSV export answers 422 with it as a validation report, and `seed export` prints it and
    exits 1. The store refuses an import into a scripted seed, the batch import included, with a `FormatException`,
    which `import_seed_csv` and the editor's API already answer as `bad-request` (400); `seed import` checks first and
    exits 4.

#### Schema and files

| File | Change |
| --- | --- |
| `common.json` | `tableColumn` `stableId`: boolean, default `false`, after `computedStored` in its `x-order` (3.1 moved the column definition here, so stereotype columns take it too; decision 19) |
| `seed.json` | `columns` items: also foreign-key ids of a table target (description). `rowsFrom` becomes `oneOf` `{ file }` and `{ script }`; `script` matches `^seeds/[A-Za-z0-9][A-Za-z0-9._-]*\.js$`, the store's extension path pattern |
| `extensions/seeds/*.js` | New project script folder for rows scripts, loaded non-recursively, not a schema file |

The invoices seed of the acceptance test. `01JCA4M2A1B2C3D4E5F6G7H8KC` is the foreign key `fk_invoices_customer`, whose
referenced table `customers` is keyed by its unique `email`; `number` and `issued_on` are columns, and the `id` column
takes a stable id, so it is not listed:

```json
{ "$schema": "../../../.schema/v1/seed.json", "kind": "seed", "id": "01JCA4M3Q8K2T6V9X1Z3B5D7F9", "name": "invoices",
  "target": "01JCA4M2A1B2C3D4E5F6G7H8J9", "key": "01JCA4M2A1B2C3D4E5F6G7H8KD",
  "columns": ["01JCA4M2A1B2C3D4E5F6G7H8KA", "01JCA4M2A1B2C3D4E5F6G7H8KB", "01JCA4M2A1B2C3D4E5F6G7H8KC"],
  "rows": [
    { "id": "01JCA4M5R1S2T3V4W5X6Y7Z8A0", "values": ["INV-1", "2026-01-05", "ada@example.com"] },
    { "id": "01JCA4M5R1S2T3V4W5X6Y7Z8A1", "values": ["INV-2", "2026-01-06", "grace@example.com"] }
  ] }
```

A composite natural key is an array, `["ACME", "EU"]`, in the referenced key's column order.

A rows script that gives a reference type one row per entity the stereotype `audited` marks
(`extensions/seeds/audited-records.js`, named by the seed as `"rowsFrom": { "script": "seeds/audited-records.js" }`):

```js
maquettiste.rows((model, seed) => model.withStereotype("audited")
  .filter(d => d.kind === "entity")
  .map(d => ({ code: d.name.toLowerCase(), label: d.displayName ?? d.name })));
```

#### Stable ids

`Model/StableIds.cs` (new, public) holds the algorithm, so the resolver, the `stable_id` helper, tests and hosts share
one implementation. Worked example for the docs: a table with id `01JCA4M2A1B2C3D4E5F6G7H8J9` has the namespace
`0193144a-0941-5898-3691-c579a078a249`. The row keyed `ada@example.com` gets `713ef598-46bf-59bd-aecd-d0be95b552e3`; a
row keyed `["ACME", "EU"]` gets `e2aa38bc-b70c-52f3-ac0e-c50c98d04b1e` (name `ACME`, U+001F, `EU`). In PostgreSQL:
`select uuid_generate_v5('0193144a-0941-5898-3691-c579a078a249', 'ada@example.com')` with the `uuid-ossp` extension.

- The value is derived for each row a table with a `stableId` column receives, from the row's key in database terms
  (`RDataRow.Key`). It is never written to a seed file or a snapshot. The resolver hands a `uuid` key part to
  `StableIds` as a UUID, so a cell written in capitals or without hyphens names the id in lowercase with hyphens, as
  the database stores it; the `stable_id` helper takes its key as given, in database terms. A key with a null part
  (a value the database fills) derives nothing, and the column stays unset.
- `StableIds.Of` and `RowId` also take a UUID text as namespace, so the RFC's own namespaces work in tests and hosts.
  `RColumn.StableId` is set in `ApplyColumnFile` (`Resolution/DatabaseRun.cs`), which every column with an entry of
  its own passes through (designed, imported, extra, inherited and overlay entries).
- A key part that a foreign-key entry fills is the referenced row's value in database terms, which may itself be a
  stable id or a value filled through another foreign-key entry. Values are computed on demand per table and key,
  recursively and memoized for one build, so the result does not depend on the order tables or rows are built in. A
  value that depends on itself through a cycle of such lookups stays null.

#### Foreign keys by natural key

- `TableSeedRules.Check` (`Validation/TableSeedRules.cs:24`) accepts a `columns` entry that is a foreign key of the
  target. Its columns count as given for MQ7109's NOT NULL check (`:60-67`) and for MQ7114's row key (`:69-89`).
- MQ7109 checks the cell's shape: a scalar for a one-column referenced key, an array of the key's length otherwise, each
  part fitting its key column as a cell of that column would.
- MQ7111 reports a cell whose key finds no row of the referenced table's rows (its table seeds and bound reference
  types), or one they do not hold in every environment the row is loaded in, with today's environment logic
  (`:306-331`).
- `TableSeedRules` reports MQ4056 for a foreign-key entry whose referenced value the database fills when the target's
  database is not PostgreSQL.
- `RSeedRow.Values` keeps the cell as written under the foreign key's name; `RSeedColumn.Kind` is `foreign-key`.
- `ResolveRun.TableSeeds` fills each column of the foreign key from the referenced `RDataRow`: the value of the
  referenced column (`ReferencesColumns`, else the referenced primary key). `RDataRow.Refs` holds one `RDataRef` per
  foreign-key entry, by the foreign key's name, whether or not every value is known.
- The rows of one seed in database terms come from an internal per-seed builder (`RDatabase.DataRowsOf(RSeed)`) that
  `database.seed_tables` and `preview_seed_rows` share; it reads only the seeds of the tables the seed's foreign-key
  entries name, transitively.
- The insert order counts a foreign-key entry as the row reference it is: `SeedOrdering` in
  `Resolution/ResolveRun.ReferenceData.cs:541-799` (`model.seeds_in_order`, `RSeed.OrderedRows`) and
  `OrderBySelfReferences` in `Resolution/ResolveRun.TableSeeds.cs:311-384` match the entry's natural key against the
  referenced rows' keys in seed terms, where today they compare the foreign key's cells.
- Deleting a foreign key drops its entry and cells from table seeds through the existing planner step
  (`Loading/ChangePlanner.cs:431-471`).

#### Scripted rows

- `ModelLoader` reads `extensions/seeds/*.js` (a new `ModelFileKind.SeedScript` in `Loading/ModelPaths.cs`) into
  `ModelSnapshot.SeedScripts`, as it reads rule scripts. `ModelSnapshot.Create` and the internal `CreateAfter` take them
  as an optional last argument; a call without them keeps the previous snapshot's, as locale shards are kept today
  (`Model/ModelSnapshot.cs:147`). The CSV paths that read `rowsFrom` (`Loading/ModelLoader.cs:522`,
  `Loading/ChangePlanner.cs:656`, `:731`, `:744`) act on `rowsFrom.file` only.
- A seed naming a script that is not there, or a seed with rows of its own beside a script, is MQ7116.
- `ScriptRegistrationKind.Rows` (`"rows"`): the registration's name is the script's path. Registering more than once, or
  not at all, fails the script with MQ7127. `ScriptSandbox.IsRuleScript` (`Scripting/ScriptSandbox.cs:320-339`) treats
  `extensions/seeds` like `extensions/rules`, so load and run errors carry the script's line and column; the seed rules
  report them as MQ7127.
- `IScriptSandbox.RunRows` calls the function with the rule-script model view, whose reads go to the call's
  `ScriptCallContext.Reads`, and the seed's canonical JSON, frozen. `Math.random` is seeded with the seed id. The result
  must be an array of objects whose values are JSON values; anything else is MQ7127.
- `Resolution/SeedScripts.cs` (new, internal) runs one seed's script in a one-engine pool created for the call, through
  the rows cache (`SeedRowsCache`, held by `EngineServices`). It turns each object into a `SeedRow`: keys matched
  ordinally to the seed's column names, numbers as JSON numbers in shortest round-trip form, trailing nulls dropped,
  the id derived (decision 14). A key the columns do not name is MQ7128. The rows keep the script's order.
- Laziness: for a seed with a rows script, `RSeed.Rows` and `RSeed.OrderedRows`, and for its reference type
  `RReferenceType.Rows`, `RReferenceUsage.Allowed` and `DefaultRow`, are built on first read, as `RSeedRow.Values` is
  (`Resolution/ResolvedReferenceData.cs:341-351`). Today they are built during resolution
  (`Resolution/ResolveRun.ReferenceData.cs:197`, `:242`, `:856-857`). Every other seed keeps its eager lists.
- In a render, a seed whose script fails has no rows, and the unit that read them fails with that MQ7127.
- `ModelResolver` (`Resolution/ModelResolver.cs:10`) takes the engine's `IScriptSandboxFactory` and the rows cache
  (`Generation/EngineServices.cs:139` passes its own); the limits come from the snapshot's settings.
- `ValidationContext.RowsOf(Seed)` gives a seed's stored rows, or its scripted rows evaluated once per validation
  through the cache. The seed rules in `ReferenceDataRules` and `TableSeedRules` read rows through it. MQ7104 counts
  scripted rows (`Validation/ReferenceDataRules.cs:255` skips rows files only).

#### Resolution and the resolved model

Compressed C# as in phase 1 section 3.2: in a record body each `Type Name[ = default];` is a public `{ get; init; }`
property; resolved members are `{ get; }` with an internal setter.

```csharp
namespace Maquettiste.Engine.Model;
public sealed record Column : ElementBase { /* existing members, plus: */ bool StableId = false; }
public sealed record SeedRowsFile { string? File; string? Script; }        // exactly one; File was required
public sealed class ModelSnapshot
{   public IReadOnlyList<ScriptSource> SeedScripts { get; }                 // extensions/seeds/*.js, ordinal by path
    public static ModelSnapshot Create(/* existing parameters, then: */
        IReadOnlyList<ScriptSource>? seedScripts = null); }
public static class StableIds
{
    /// <summary>RFC 9562 version 5: namespace the ULID's 16 bytes, name the key's text forms joined by
    /// U+001F.</summary>
    public static string Of(string namespaceId, IReadOnlyList<object?> key);   // lowercase, hyphenated
    public static string KeyText(IReadOnlyList<object?> key);                   // the name, before UTF-8
    public static string RowId(string seedId, IReadOnlyList<object?> key);      // the ULID text of Of(seedId, key)
}

namespace Maquettiste.Engine.Resolution;
public sealed class RColumn { bool StableId; }
public sealed class RSeedColumn { RForeignKey? ForeignKey; }               // Kind "foreign-key" for such an entry
public sealed class RSeed { string? RowsScript; }                          // the script path; Rows lazy for a script
public sealed class RDataRow { IReadOnlyDictionary<string, RDataRef> Refs; }   // by foreign key name
public sealed class RDataRef : RObject {                                   // Kind "data-ref"
    RForeignKey ForeignKey; RTable Table;        // the referenced table
    IReadOnlyList<RColumn> KeyColumns;           // its row key
    IReadOnlyList<object?> Key;                  // the cell, in seed terms
    RDataRow? Row;                               // null when no row has the key (MQ7111)
    IReadOnlyList<RColumn> Unknown; }            // foreign key columns whose referenced value the database fills

namespace Maquettiste.Engine.Scripting;
public enum ScriptRegistrationKind { /* ... */ Rows }                      // "rows"
public interface IScriptSandbox
{   /// <summary>Runs a rows script; the array it returned, as JSON. Reads go to ctx.Reads.</summary>
    JsonElement RunRows(string script, ElementDocument seed, ModelSnapshot model, ScriptCallContext ctx); }

namespace Maquettiste.Engine;
public sealed partial class ModelStore
{   public Task<SeedRowsPreview?> PreviewSeedRowsAsync(string seedId, string? database, string? cursor, int limit,
        CancellationToken ct); }                                           // null: no such seed
public sealed record SeedRowsPreview(string Seed, string? Script, string? Database,
    IReadOnlyList<SeedRowsPreviewRow> Rows, int Total, string? Next, IReadOnlyList<Diagnostic> Diagnostics);
public sealed record SeedRowsPreviewRow(string Id, IReadOnlyList<object?> Key,
    IReadOnlyDictionary<string, object?> Values, IReadOnlyList<SeedRowsPreviewRef> Refs);   // Values in database terms
                                                                   // when a database is given
public sealed record SeedRowsPreviewRef(string ForeignKey, string Table, IReadOnlyList<object?> Key, bool Found,
    IReadOnlyList<string> Unknown);
```

#### Rules

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ7124 | E | `stableId` is set on a column that is not of type `uuid`, or whose value the database fills (identity, sequence, generated, computed). | Clear it |
| MQ7125 | E | The row key of a table with a stable id column holds that column, or a value the database fills directly or through a foreign-key entry, so the stable id cannot be derived. | Name a unique constraint as the seeds' key |
| MQ7126 | E | A table seed's column entry names a foreign key that is not its target's, is listed beside one of that key's own columns, references a table that has no rows in the model, or the seed's row key holds part of the entry's columns. | Fix the entry |
| MQ7127 | E | A seed's rows script fails: it does not load, registers anything but rows exactly once, throws, exceeds a sandbox limit, or returns something that is not an array of row objects. | Fix the script |
| MQ7128 | E | A scripted row has a key its seed's columns do not name, or the seed's target has no row key to derive row ids from. | Fix the script or the target |
| MQ7107 | E | Changed text: a table seed targets a table that is not a table file of a database, or lists a column or foreign key the table does not have. | |
| MQ7108 | E | Changed text: a table seed lists an identity, computed, generated or stable id column, whose value the database or the engine gives. | |
| MQ7109 | E | Changed text: a table seed's cell does not fit its column (type, length, precision or scale, or NULL in a NOT NULL column without a default), or a foreign-key cell does not have the referenced row key's shape. | |
| MQ7111 | E | Changed text: a foreign key that a table seed or a reference type's binding fills, or a foreign-key cell's natural key, names no row of the referenced table's rows, or one they do not hold in every environment the row is loaded in. | |
| MQ7116 | E | Changed text: a seed's rows file or rows script is missing or cannot be read, or the seed has rows of its own beside it. | |
| MQ5005 | W | Changed text: a rule or rows script declares top-level `let` or `var` variables; it keeps no state between calls, so its results are never cached. | |
| MQ4056 | W | Changed text: also a seed cell that names a row whose referenced value the database fills, outside PostgreSQL (written as NULL with a TODO comment). | |

MQ7125 is reported on the table seed that decides the key (at `/key`, or `/target` without one), else, when an entity's
binding writes the table (decision 20), on the table at `/columns/<i>/stableId` (at the document for an inherited
column). MQ6013 reserves the helper name `stable_id`. MQ7129 and MQ7130 stay reserved.

#### CLI and MCP

- `preview_seed_rows` (new catalog tool, MCP and the assistant): `{ seed, database?, cursor?, limit? }` returns
  `SeedRowsPreview`. Without `database` it gives the seed's rows as written or scripted, with their ids and keys. With
  `database` it gives them in database terms: stable ids, foreign-key values and `refs`, from the per-seed builder,
  never from `database.seed_tables`. It runs only the seed's script and the scripts of the seeds its foreign-key
  entries name, transitively. Pages: default 100, at most 1000. An unknown seed is `not-found`; an unknown database or
  cursor is `bad-request`.
- `maquettiste seed preview <seed> [--database <name or id>] [--format text|json]` prints every page: one line per row,
  `<key> <id>: <column>=<value> ...`. An unknown seed or database exits 4. `--database` and `--format` are value
  options already.
- `create_element`, `save_element`, `apply_batch`, `model apply` and `patch_element` take foreign keys by name in a
  table seed's `columns` (`Loading/SeedInput.cs`: candidates are the target's columns, then its named foreign keys; a
  name both match is MQ1002), and `rowsFrom.script`.
- `export_seed_csv` and `seed export` head a foreign-key entry with the foreign key's name (its id when it has none) and
  write a composite key as JSON array text; import reads that text back, and a one-column key as its key column's type
  (found in D41). A scripted seed exports its scripted rows with their derived `@id`.
  `import_seed_csv` refuses a seed with a rows script (`bad-request`), and `seed import` exits 4 for one.
- `write_extension_file`, `read_extension_file`, `list_extension_files`, `move_extension_file` and
  `delete_extension_file` accept `seeds/*.js` (kind `seed-rows`). A write checks that the script loads and registers
  rows once, as a rule file's write checks it (`ModelStore.Extensions.cs:259-296`), and never runs it.
- Pins: MCP tools +1 and `AgentTools.All` +1, both in D44 (`McpSurfaceTests`, `AgentToolsTests`, the counts in
  `docs/mcp.md` and `README.md`).

#### Templates and packs

- New members: `column.stable_id`, `seed.rows_script`, `seed_column.foreign_key`, `data_row.refs` (by foreign key name:
  `foreign_key`, `table`, `key_columns`, `key`, `row`, `unknown`).
- Helper `stable_id <table> <key>`: the stable id of a key (a value or a list, database terms) in a table (an `RTable`),
  for packs that emit constants for seeded rows. It records `e:<table id>`. A table without a `stableId` column is
  MQ6006.
- `DependencyHasher` gains `x:<path under extensions>`, the file's content hash or absent, and hashes `ix:` and `rt:`
  keys through `RuleKeyHasher.Of(model)`. `PlanExplainer` words an `x:` key ("The extension file <path>"); the cause
  kind of all three stays `inputs` (`Generation/PlanExplainer.cs:206-216`), so the contract enum does not change.
- **sql-ddl.** `_data.scriban` writes a foreign-key column whose value is null and listed in a ref's `unknown` as
  `(SELECT <referenced column> FROM <table> WHERE <key column> = <value> [AND ...])` on PostgreSQL. Rows of one table
  that name rows of the same table through such a subquery are written as one INSERT per dependency level, so each
  subquery sees the rows inserted before it. Elsewhere the cell is NULL with a TODO comment naming the row and key.
- **seed-data.** Known values are written as today. A cell whose value the database fills is left empty and listed under
  `unresolved` (table, column, referenced table, key columns, key; decision 25): in `manifest.json` with `format` `csv`,
  in `seed-data.json` with `json`.
- No golden changes: no fixture has a foreign-key entry, a stable id column or a scripted seed.

#### Editor follow-ups

- Seed grid: foreign-key columns with a row picker by natural key; stable id columns shown, read-only, with their value
  from `preview_seed_rows`; scripted seeds read-only, with Preview rows and Open script.
- Table editor: a Stable id checkbox on `uuid` columns, with `stableId` on `ColumnView`. Extensions screen: a seeds
  group and a New rows script action (D44 only teaches its file list the `seed-rows` kind: the Generate explorer lists
  rows scripts and the Extensions tab opens one with JavaScript highlighting).
- Until then the generic JSON form edits `stableId`, foreign-key entries and `rowsFrom.script`.

#### Docs

`docs/guides/reference-data-and-seeds.md` (natural-key cells; stable ids with the algorithm, the worked example and the
SQL one-liner; scripted rows and their limits), `docs/user-guide.md`, `docs/mcp.md` (`preview_seed_rows`; the extension
tools take `seeds/*.js`), `packs/sql-ddl/README.md`, `packs/seed-data/README.md`,
`skills/maquettiste-modeling/SKILL.md`, `docs/engineering/engine-design.md` (section 10: `rows`; section 11: the `x:`,
`ix:` and `rt:` keys) and `docs/releases.md` under `## 0.12.0 (unreleased)`.

**Not doing**

- Natural keys in the end cells of entity and relation seeds: they keep row ids, which survive key changes. Tables are
  where natural keys live, and a binding carries entity rows into a table.
- Where-used, delete refusal and refactors for table rows named by natural key: such cells are data (decision 4).
- Natural keys that name rows the model does not hold (MQ7126): a lookup could find them, but nothing could check them.
- A stable id namespace other than the table id, and tombstones for retired keys (decisions 8 and 10).
- Storing stable ids in seed files or snapshots: they are derived on every resolve.
- Lookup subqueries outside PostgreSQL (MQ4056), and lookups in the C# seed loader: seed-data lists such cells under
  `unresolved` for a loader to act on.
- Rows from pack scripts or from the resolved model (decision 13), and scripted rows for relation seeds.
- Naming scripted rows from the model (translations, `x-ref` values, end cells): the index does not hold them.
- An action that freezes scripted rows into the seed file: export the CSV and import it into a seed without a script.
- Library packs (5.2) in rows scripts: as for calculation files (5.5), the script's `x:` key would not follow the
  library files into the units that read its rows.
- Editor screens (follow-ups above).

**Tests**

- `StableIdTests` (Model, new): `Version_5_matches_the_RFC_9562_example` (the DNS namespace and `www.example.com` of its
  appendix), `The_namespace_is_the_table_id_as_row_uuid_writes_it`,
  `Key_parts_join_with_the_unit_separator_in_key_order`, `Numbers_use_the_documented_text_forms`,
  `The_worked_example_of_the_guide_holds`, `Ten_thousand_keys_give_ten_thousand_ids`.
- `TableSeedTests` (Store): `A_stable_id_column_is_filled_for_every_row_the_table_receives`,
  `A_value_a_binding_writes_is_kept`, `A_stable_id_on_a_text_column_is_MQ7124`,
  `A_row_key_holding_the_stable_id_column_is_MQ7125`, `A_table_only_a_bound_entity_fills_reports_MQ7125_on_the_table`,
  `A_table_a_binding_only_reads_does_not_meet_MQ7125`,
  `A_seed_cell_for_a_stable_id_column_is_MQ7108`, `A_foreign_key_entry_fills_its_columns_from_the_referenced_row`,
  `A_foreign_key_entry_beside_its_own_column_is_MQ7126`, `A_natural_key_that_finds_no_row_is_MQ7111`,
  `A_composite_natural_key_must_be_an_array_in_key_order`, `A_natural_key_finds_the_rows_of_a_bound_reference_type`,
  `A_value_the_database_fills_stays_null_with_a_ref`, `Outside_PostgreSQL_a_lookup_cell_is_MQ4056`.
- `ReferenceDataResolutionTests` (Resolution): `Data_rows_carry_refs_by_foreign_key_name`,
  `Stable_ids_follow_the_row_key_in_database_terms`, `A_stable_id_in_a_key_part_comes_from_the_referenced_row`,
  `Stable_ids_do_not_depend_on_table_order`, `Insert_order_follows_natural_key_references`.
- `SeedInputTests` (Store): `A_foreign_key_given_by_name_is_stored_as_its_id`,
  `A_name_that_is_a_column_and_a_foreign_key_is_MQ1002`.
- `SeedHelperTests` (Integration): `Stable_id_matches_the_resolved_value`.
- `RegistrationTests` (Scripting): `Rows_registers_once_per_file`, `Rows_twice_fails_the_script`,
  `A_rule_script_that_calls_rows_fails_to_load`, `Run_rows_returns_the_array_and_records_its_reads`.
- `ScriptedSeedTests` (Integration, new): `Scripted_row_ids_derive_from_the_seed_and_the_key`,
  `A_preview_of_a_unit_that_reads_no_seed_runs_no_script`, `A_save_outside_the_seed_scope_runs_no_script`,
  `An_extension_write_runs_no_rows_script`, `Unchanged_inputs_reuse_the_cached_rows`,
  `A_change_to_a_read_element_renders_the_reading_unit_again`,
  `A_script_that_throws_is_MQ7127_and_fails_the_reading_unit`, `A_key_outside_the_columns_is_MQ7128`,
  `A_relation_seed_with_a_script_is_MQ7128`, `A_missing_script_or_rows_beside_it_is_MQ7116`,
  `Top_level_let_in_a_rows_script_is_MQ5005`, `A_default_naming_a_scripted_code_is_judged_by_whole_model_validation`,
  `An_x_ref_to_a_scripted_row_is_reported_as_unknown`, `Snapshots_keep_seed_scripts_across_writes`.
- `DependencyKeyTests` (Resolution): `An_x_key_hashes_the_extension_file`,
  `Rule_view_keys_hash_as_the_rule_cache_hashes_them`. `PlanCauseTests` (Editor):
  `An_x_key_is_worded_as_an_extension_file`.
- `ExtensionFilesTests` (Integration): `A_rows_script_is_listed_written_and_checked_as_seed_rows`.
- `SeedPreviewTests` (Store, new): `Pages_hold_one_seeds_rows_with_ids_and_keys`,
  `Database_terms_run_only_the_scripts_of_the_seed_and_the_seeds_it_names`.
- `AgentToolsTests` (Editor): `Preview_seed_rows_gives_database_terms_for_one_seed` and the `AgentTools.All` pin.
  `McpReadTests`: `Preview_seed_rows_pages_scripted_rows`. `McpSurfaceTests`: the pins.
  `McpExtensionToolTests`: `Write_extension_file_checks_a_rows_script`. `L10nAndSeedCommandTests` (Cli):
  `Seed_preview_prints_one_line_per_row`, `Seed_export_heads_a_foreign_key_entry_with_its_name`.
- `TableSeedSqlTests` (Packs, PostgreSQL): `Natural_keys_and_stable_ids_apply_twice_with_no_change`,
  `A_lookup_subquery_names_an_identity_keyed_row`, `Rows_that_name_rows_of_their_own_table_insert_by_level`.
  `PackLayoutTests` (Packs): `Unresolved_cells_are_listed_in_the_manifest`.
- **Acceptance (scope "Done when"):**
  `Integration/SeedNaturalKeyTests.An_invoice_seed_names_its_customers_by_email_and_carries_no_ids` (new). Setup: a
  billing copy gains schema `sales` in `main` and two table files there (decision 23). `customers` has `id uuid`
  primary key with `stableId`, `name text not null` and `email varchar(254) not null`, unique as `uq_customers_email`.
  `invoices` has `id uuid` primary key with `stableId`, `number varchar(32)` unique as `uq_invoices_number`,
  `issued_on date` and `customer_id uuid not null` with `fk_invoices_customer` to `customers(id)`. Two seeds are
  created through the store with every column and key by name: `customers` (Ada and Grace with their emails, keyed by
  `uq_customers_email`) and `invoices` (`INV-1` and `INV-3` for `ada@example.com`, `INV-2` for `grace@example.com`,
  keyed by `uq_invoices_number`). Expected: validation has no error; no seed cell is a UUID or a ULID; each invoice
  row of `database.seed_tables` has `customer_id` equal to its customer row's `id`, which equals
  `StableIds.Of(<customers id>, ["ada@example.com"])` for Ada.
- **Acceptance (same line, on PostgreSQL):** `TableSeedSqlTests.Natural_keys_and_stable_ids_apply_twice_with_no_change`.
  Setup: the model above; `schema.sql` and `seed.sql` applied twice. Expected: the join of `invoices` to `customers` on
  `customer_id` gives two invoices for `ada@example.com`; the second run changes no row.
- **Acceptance (keys the database fills):** `TableSeedSqlTests.A_lookup_subquery_names_an_identity_keyed_row`. Setup: as
  above, but `customers.id` is `bigint` generated as identity, without `stableId`, and `invoices.customer_id` is
  `bigint`. Expected: `seed.sql` writes `(SELECT id FROM sales.customers WHERE email = 'ada@example.com')`, and the join
  gives the same result.
- **Acceptance (scripted rows):** `ScriptedSeedTests.A_rows_script_gives_a_reference_type_one_row_per_audited_entity`.
  Setup: a billing copy gains a reference type `AuditedRecordType` (string code), a seed whose rows come from the script
  above, and a test pack with one unit that loops over `reference_type.rows` and one that does not read them.
  Expected: `reference_type.rows` has `customer`, `invoice` and `payment`; after adding `audited` to `Product` the next
  plan renders the looping unit again, with `product` among its rows, and not the other.

**Tasks**

Legend in section 2.1 of `next-phase-1-design.md`; order in section 7. Each task updates the docs rows of what it
builds. D40 follows 3.1's D8 and 3.2's D22, which edit the same table, column and seed rule files first, 3.3's D33,
the last 3.3 task on `Diagnostics/RuleCatalog.cs` and its pin (it follows 3.2's D21, which edits
`Resolution/DatabaseRun.Columns.cs` and `Resolution/ResolvedPhysical.cs`), and 3.3's D34, which also regenerates the
editor's types. D40, D41 and D43 change `table.json` or `seed.json`, so each regenerates the editor's types and mocks
and owns them; D41 and D43 follow D40 for that reason.

- **D40. Stable ids.** Runs after D8, D22, D33 and D34 (both regenerate the editor's types). Owns: `Model/StableIds.cs`
  (new), `Column.StableId` in `Model/Physical.cs`, `tableColumn` in `schemas/v1/common.json` and its copy (decision
  19), the regenerated editor types and mocks (`src/editor/src/api/schema.d.ts`, `src/editor/src/mocks/openapi.json`),
  MQ7124 and the table case of MQ7125 in `Validation/PhysicalRules.cs`, MQ7124 on a stereotype's columns in
  `Validation/BuiltinRules.cs`, MQ7120's skip of a stable id column in `Validation/ReferenceBindingRules.cs`, the seed
  case of MQ7125 and MQ7108's stable id case in `Validation/TableSeedRules.cs`, `RColumn.StableId` in
  `Resolution/ResolvedPhysical.cs` and where columns resolve (`ApplyColumnFile` in `Resolution/DatabaseRun.cs`), the
  stable id values in `Resolution/ResolveRun.TableSeeds.cs`, `stable_id` and its reserved name in
  `Rendering/BuiltinHelpers.cs` and the helper list in `packs/README.md`, `stableId` in `Loading/Materializer.cs` (an
  overlay entry's stays), the stable id comparison in `Loading/StereotypeFold.cs` (decision 22),
  `Diagnostics/RuleCatalog.cs` (MQ7124, MQ7125, MQ7108's text), `CatalogTests.cs` (+2), `ResolvedCoverageTests`
  (`stableId` covered). Tests: `StableIdTests`, the stable id cases of `TableSeedTests`,
  `ReferenceDataResolutionTests.Stable_ids_follow_the_row_key_in_database_terms`,
  `SeedHelperTests.Stable_id_matches_the_resolved_value`,
  `StereotypeFoldTests.Fold_refuses_a_stable_id_difference_with_MQ4077`. Verify:
  `E "~StableIdTests|~TableSeedTests|~ReferenceDataResolution|~SeedHelperTests|~StereotypeFoldTests"`;
  `E "~CatalogTests|~ResolvedCoverageTests|~ValidationFixtureTests"`; `P "~GoldenTests"` (unchanged); `editor`;
  `copies`; `record`.
- **D41. Foreign keys by natural key.** Runs after D40 (both regenerate the editor's types) and D24
  (`ModelStore.LocalizationApi.cs`). Owns: `schemas/v1/seed.json` (`columns`) and its copy, the regenerated editor types
  and mocks (`src/editor/src/api/schema.d.ts`, `src/editor/src/mocks/openapi.json`), the doc comments in
  `Model/ReferenceData.cs`, MQ7126, MQ4056's seed case and the MQ7107, MQ7109 and MQ7111 cases in
  `Validation/TableSeedRules.cs`, `Validation/ReferenceBindingRules.cs` (bound rows' keys in seed terms),
  `RSeedColumn.ForeignKey` in `Resolution/ResolvedReferenceData.cs`, `RDataRow.Refs`, `RDataRef` and the internal
  `RDatabase.DataRowsOf` in `Resolution/ResolvedPhysical.cs`, `Resolution/ResolveRun.TableSeeds.cs` (values on demand,
  the per-seed builder, `OrderBySelfReferences`), `SeedOrdering` in `Resolution/ResolveRun.ReferenceData.cs`,
  `Loading/SeedInput.cs`, the foreign-key column of the CSV export and import in `ModelStore.LocalizationApi.cs`,
  `Diagnostics/RuleCatalog.cs` (MQ7126; the MQ7107, MQ7109, MQ7111 and MQ4056 texts), `CatalogTests.cs` (+1),
  `ResolvedCoverageTests`. Tests: the foreign-key cases of `TableSeedTests`, `ReferenceDataResolutionTests` (with
  `A_stable_id_in_a_key_part_comes_from_the_referenced_row` and `Stable_ids_do_not_depend_on_table_order`, which need
  foreign-key entries) and `SeedInputTests`,
  `L10nAndSeedCommandTests.Seed_export_heads_a_foreign_key_entry_with_its_name`. Verify:
  `E "~TableSeedTests|~ReferenceDataResolution|~SeedInputTests|~ReferenceBinding|~ResolvedCoverageTests"`;
  `E "~CatalogTests|~ValidationFixtureTests"`; `C "~L10nAndSeedCommandTests"`; `P "~GoldenTests"` (unchanged); `editor`;
  `copies`; `record`.
- **D42. sql-ddl lookups and seed-data.** Runs after D41, and after D13 and D26 (`packs/sql-ddl/README.md`). Owns:
  `packs/sql-ddl/_data.scriban`, `packs/sql-ddl/README.md`, `packs/seed-data/data.scriban`,
  `packs/seed-data/README.md`. Tests: the three `TableSeedSqlTests` cases above,
  `PackLayoutTests.Unresolved_cells_are_listed_in_the_manifest`. Verify: `PG P "~TableSeedSqlTests"`;
  `P "~GoldenTests|~PackLayoutTests"` (goldens unchanged).
- **D43. Scripted rows.** Runs after D41 (both edit `seed.json`, `Model/ReferenceData.cs` and the seed rule files), D40
  (both regenerate the editor's types) and D10 (`Loading/ChangePlanner.cs`). Owns: `rowsFrom` in `schemas/v1/seed.json`
  and its copy, the regenerated editor types and mocks (`src/editor/src/api/schema.d.ts`,
  `src/editor/src/mocks/openapi.json`), `SeedRowsFile` in `Model/ReferenceData.cs`, `Model/ModelSnapshot.cs`
  (`SeedScripts`, the optional argument), `Loading/ModelLoader.cs`, `Loading/ModelPaths.cs`, the `rowsFrom.file` checks
  in `Loading/ChangePlanner.cs`, `Scripting/ScriptingContracts.cs`, `Scripting/ScriptSandbox.cs`,
  `Scripting/ScriptSandbox.Calls.cs`, `Resolution/SeedScripts.cs` (new), `Resolution/ModelResolver.cs`,
  `Generation/EngineServices.cs`, `Resolution/ResolveRun.ReferenceData.cs` and `Resolution/ResolvedReferenceData.cs`
  (lazy rows, `RSeed.RowsScript`), `Planning/DependencyHasher.cs`, `Generation/PlanExplainer.cs`,
  `Validation/ValidationContext.cs`, `Validation/ReferenceDataRules.cs` and `Validation/TableSeedRules.cs` (rows through
  `RowsOf`; MQ7127, MQ7128; the scoped code checks of decision 17), `ModelStore.Extensions.cs` (`seeds/*.js`, kind
  `seed-rows`), the validations previews wait on (decision 31: internal `ValidationScope.Previews` in
  `Diagnostics/Diagnostic.cs`, `ModelStore.ResolverFindings.cs`, `ModelStore.cs`, `GenerationService.cs`,
  `Editor/DatabaseTables.cs`), `Diagnostics/RuleCatalog.cs` (MQ7127, MQ7128; the MQ7116 and MQ5005 texts),
  `CatalogTests.cs` (+2), `ResolvedCoverageTests`. Tests: `ScriptedSeedTests` (all but the acceptance case),
  `RegistrationTests`, `DependencyKeyTests`, `PlanCauseTests`, `ExtensionFilesTests`. Verify: `E
  "~ScriptedSeedTests|~Scripting.|~DependencyKeyTests|~PlanCauseTests|~ExtensionFilesTests|~HashingAndStateTests"`; `E
  "~ReferenceData|~CatalogTests|~ValidationFixtureTests|~ReadTrackingTests|~ResolvedCoverageTests"`; `P "~GoldenTests"`
  (unchanged); `editor`; `copies`; `record`.
- **D44. Seed preview, CSV and extension tools.** Runs after D43, D12 (`docs/mcp.md`), D34 (`docs/api/openapi.yaml`) and
  D35 (`Agent/tools.json`, `Agent/AgentTools.cs`, `src/Maquettiste.Cli/CliApp.cs`, the tool pins). Owns:
  `ModelStore.SeedPreview.cs` (new), `Agent/tools.json`, `Agent/AgentTools.cs`, `Agent/AgentTools.Handlers.cs`, the
  export and import changes in `ModelStore.LocalizationApi.cs`, `src/Maquettiste.Cli/Commands/SeedCommand.cs`, the
  `seed` usage in `src/Maquettiste.Cli/CliApp.cs`, the extension and seed CSV tool descriptions in
  `src/Maquettiste.Cli/Mcp/ModelTools.Extensions.cs` and `src/Maquettiste.Cli/Mcp/ModelTools.cs`, the `kind` enum of
  `ExtensionFileInfo` and `ExtensionFileContent` in `docs/api/openapi.yaml`, the `seed-rows` kind in
  `src/editor/src/workspaces/generate/extensionsModel.ts`, `docs/mcp.md` (rows and counts), `README.md` (the tool
  count), the pins in `tests/Maquettiste.Cli.Tests/McpServerTests.cs` and `AgentToolsTests` (MCP tools +1,
  `AgentTools.All` +1). Tests: `SeedPreviewTests`, `AgentToolsTests`, `McpReadTests`, `McpSurfaceTests`,
  `L10nAndSeedCommandTests.Seed_preview_prints_one_line_per_row`,
  `McpExtensionToolTests.Write_extension_file_checks_a_rows_script`. Verify: `E "~AgentToolsTests|~SeedPreviewTests"`;
  `C "~McpSurfaceTests|~McpReadTests|~McpExtensionToolTests|~L10nAndSeedCommandTests"`; `editor`.
- **D45. Guide and acceptance.** Runs after D42, D43 and D44, and after D15, D27 and D36 (the guides, the skill,
  `docs/engineering/engine-design.md` and the release notes). Owns: `docs/guides/reference-data-and-seeds.md`,
  `docs/user-guide.md`, `skills/maquettiste-modeling/SKILL.md`, `docs/engineering/engine-design.md` (sections 10 and
  11), the 0.12.0 notes in `docs/releases.md`, `Integration/SeedNaturalKeyTests.cs` (new) and the acceptance case of
  `ScriptedSeedTests`. Verify: `E "~SeedNaturalKeyTests|~ScriptedSeedTests"`; `PG P "~TableSeedSqlTests"`;
  `C "~SkillRefreshTests"`.

### 3.5 Reference-type refactors

**Why every team**

Reference data outlives the code that first used it. Codes get misspelled, renamed, retired and merged, and two parts of
a model often grow the same list twice. Every such change is a data migration, because the code is what databases store,
whatever strategy keeps the list. Without help a team edits codes by hand, misses a default or a seed cell, and writes
UPDATE statements for each environment. Four refactors serve any project that keeps reference data in a database: rename
a code, retire a code in favour of another, merge two types, and rename a type. Each shows what it touches before it
writes. Each keeps the old code as a former code that imports, validation and generated code still understand. And each
records the database step once, as a migration fact that every environment applies in order.

**Today**

- A reference type has a code, a label, fields, storage choices and bindings; nothing records a code it used to have
  (`Model/ReferenceData.cs:12-40`, the keys of `schemas/v1/reference-type.json:6`).
- Rows are referenced by code, so a code change is a data migration. "Rename code" is designed as one batch that
  rewrites defaults, `allowedValues` entries and cells (`docs/engineering/reference-types-seeds-localization.md:44`),
  and "Replace with" as part of a used row's delete (`:209`). Neither is built: no batch operation does it
  (`ModelStore.cs:1385-1527`).
- Code uses are references to row ids in the index (`Model/ReferenceDataIndex.cs:6-12`), so where-used reaches defaults,
  `allowedValues` entries and cells. A default or `allowedValues` entry that names no code is MQ3019 or MQ7011, a code
  cell MQ7009 (`Validation/ReferenceDataRules.cs:176`, `:187`, `:514`).
- `reference_type_usage` lists attributes, and resolves the whole model to do so
  (`ModelStore.LocalizationApi.cs:573-603`, the resolve at `:581`).
- The `value-map` fact has had a format since phase 1: `referenceType`, `objects` (columns) and `values`
  (`schemas/v1/migration.json:74`, `:218-234`, `:365-386`). MQ4106 checks its objects and values
  (`Validation/MigrationRules.cs:219-226`). Nothing records one, it is never judged stale
  (`Migrations/StaleFacts.cs:129-136`), and sql-ddl writes a TODO line for it
  (`packs/sql-ddl/_migration.scriban:1986-2000`, `:2124-2125`).
- A reference column's state in a fact carries its strategy and bound table, not its reference type
  (`Migrations/FactClassifier.cs:402-406`), though the snapshot records it (`SchemaDiff/SnapshotCapture.cs:222`) and the
  diff reports a change of it (`SchemaDiff/SchemaDiffer.cs:495`). sql-ddl's migration neither retypes the column nor
  moves its strategy objects for that change: it is not among the changes the column step handles, so it becomes a TODO
  line (`packs/sql-ddl/_migration.scriban:738`, `:897-906`).
- sql-ddl names a lookup table after the type's plural name and a PostgreSQL enum type after its name
  (`packs/sql-ddl/_reference.scriban:86-90`, `packs/sql-ddl/_sql.scriban:333-335`). It creates a lookup table for every
  type a database realizes that way, used or not (`_reference.scriban:100-109`), deletes lookup rows whose code left the
  seeds (`:282-289`), and stops on an enum value that left them (`:296-306`).
- Batch operations that rewrite many files expand into document changes before the batch is planned
  (`ModelStore.Schemas.cs:31-44`).

**Decisions taken here**

1. **Four batch operations**: `rename-code`, `retire-code`, `merge-reference-types` and `rename-reference-type`. They go
   through `apply_batch`, `model apply` and the editor's batch endpoint, and expand like `retag`. Each shows its usage
   first through `dryRun` (phase 1 section 5.1): the operation's item carries `usage`, read from the index, never from a
   whole-model resolve. Editor menus are follow-ups.
2. **Former codes live on the type**: `formerCodes: [{ code, replacedBy }]`, sorted by code. `replacedBy` is a row id,
   indexed as a reference. A rename lists the old code with the row itself; a retire and a merge list it with the row
   that takes over. One shape serves the three. Former codes are model content, read by imports, validation,
   `names-to-ids` and generated code; the value-map fact stays the only home of the database step. A team removes an
   entry by hand once no database stores the code.
3. **A code never comes back.** A former code is not a live code of its type (MQ7017), and a code that an open value map
   maps away is not live again (MQ4113): in an environment that applies the fact late, the map would move the new row's
   data. Renaming a code back is therefore refused (MQ7020). Once no database stores the code, remove its entry and the
   facts, then rename.
4. **`value-map` is the fact type.** Phase 1 gave it this meaning and its format; 3.5 is its first producer and gives it
   a step. A second fact type would split one meaning in two. The ledger is unchanged: a value map's row is a fact row,
   with its id and content hash.
5. **The value map widens.** `referenceType` is the type that holds the codes after the fact. `fromReferenceType` (new)
   is a merge's source. `from` and `to` carry the type's `name` and, new in the state, its `pluralName`, so a pack finds
   the objects it named after the type. `collections` (new) lists template-defined collections that hold the codes.
6. **The write records it.** An operation hands its code map to the write's recording step, as a materialize hands its
   key maps (phase 1 section 3.2, decision 5). The classifier never infers a code map from documents: by that same
   decision it compares physical captures, and reference rows are not in a capture. A plain edit that changes the code
   of a kept row, or renames a type, records nothing and gets MQ4114 on the save; `validate --against` asks for the map
   (MQ4112).
7. **One fact per database per operation.** Holders come from the before resolve of the databases the change reaches
   (phase 1's footprint selection; every refactor writes the type's file, so the databases that reach the type are
   chosen): the columns whose `RColumn.ReferenceType` is the type, which 3.2 sets for columns typed by it as well as for
   columns mapped from an attribute, and the template-defined collections of it (owner table key, attribute id and
   name). Holder keys are historical, moved by later `rekey` facts like object keys. A database with no holder gets no
   value map. A rename of a type also records a fact in every database without a binding of the type, holders or not,
   since a pack may have named objects after it there.
8. **Values** are sorted by `from`. An identity entry is never written, and no `to` is also a `from` of the same fact: a
   chain would not be idempotent. Integer codes are canonical decimal text and UUID codes canonical lowercase.
9. **Merging A into B.** An A code equal to a live B code maps to it with no entry; `codes` maps the others; an A code
   in neither moves into B as a row, keeping its row id, values and translations. A's seeds are retargeted to B, their
   field columns remapped to B's fields of the same name (an authoring match that writes ids); an A seed whose name a B
   seed has joins it when their environments and apply match. Rows that do not move are removed with their translations.
   Every reference to A's id moves to B's id through the index: attribute and field types, columns typed by A (3.2),
   `of` scopes of `x-ref` schemas and `x-ref` values. `x-ref` values naming a removed row move to the B row its code
   maps to. B gains A's former codes and one former code per mapped A code; A is deleted. A's own storage choices go
   with it; where B's strategy differs, the classifier records its usual `change-type` facts, after the value map.
10. **One refactor per type per batch.** Each operation reads the model as it was before the batch; a second operation
    on a type the batch already refactors is MQ7020.
11. **Stale and fold.** A value map is judged against a baseline model only: it is stale when its type did not hold any
    of its `from` codes there and its names did not change. With only a committed snapshot it is never stale, since the
    snapshot holds no rows. `migration fold` removes stale value maps; value maps still never join.
12. **sql-ddl writes a data step on PostgreSQL**, before the schema statements, as phase 1 places a fact's own steps.
    For each holder it makes the target rows exist, drops the column's strategy objects by name, maps the stored codes
    and reconciles the objects. A natively stored code is renamed in place where PostgreSQL allows it. The step can run
    twice. Other dialects keep the TODO line, which MQ4056 already covers.
13. **Scripted rows (3.4) are not refactored.** Their codes are refused (MQ7020); a script that keeps writing an old
    code meets MQ7019 at whole-model validation.
14. **What changes from the earlier "Rename code" design** (`reference-types-seeds-localization.md:44` and `:209`). It
    still rewrites every default, `allowedValues` entry and cell found through the index, in one batch. It now also
    keeps the old code as a former code, records a value map per database, refuses a code that would come back, and is
    an operation of the batch contract that MCP and the CLI call, not an editor action. "Replace with" on a row delete
    becomes `retire-code`. Both paragraphs are rewritten in D56.
15. **Taken during the build of D50.** MQ7019 replaces MQ7014 for a typed column's default and a table seed's cell
    through `ColumnTypeRules.FormerCodeProblem`; a binding constant that names a former code stays MQ7014, whose message
    then names the code to write, since the rule names defaults, `allowedValues` entries and cells only. A seed of a
    reference type brings the type (MQ7017) and the type's value maps (MQ4113, D51) into a save's validation as conflict
    peers (`Validation/ModelValidator.cs`). An import line skipped for a former code marks the row that replaced it as
    kept, so `replace` does not remove that row, and `seed import` lists MQ7019 without refusing the dry run
    (`src/Maquettiste.Cli/Commands/SeedCommand.cs`). An `RFormerCode`'s id is `<type id>:<code>`.
16. **Taken during the build of D51.** The canonical writer sorts by several keys when `x-sort` joins them with commas:
    `collections` is `"x-sort": "table,attribute"`, and `ObjectLayout.SortKey` keeps its one string, so no signature
    changes (`Json/CanonicalJson.cs`, `Json/CanonicalCheck.cs`). The value-map summary is `FactDocuments.Summary`
    (`Migrations/FactDocuments.cs`), which `ModelStore.MigrationTools.cs` and a write's answer both call; a merge reads
    `maps CreditTerm codes n90 -> net60 into PaymentTerm in 2 columns`. MQ4106 also reports a collection listed twice,
    as it reports an object listed twice.
17. **Taken during the build of D52.** `usage` goes on the item whose id is the type the operation names (the
    merged-away type's delete for a merge): an expanded batch answers one item per element it writes, so no item is the
    operation's own. `ModelStore.ReferenceRefactor` (internal) carries the code map's parts for D53's handoff. Besides
    the index's code uses, a code that moves rewrites what 3.2 lets a typed column hold: its literal default, the table
    seed cells that list it and the binding constants that name it by id, found through the column's `typeRef` reference
    and the references to the column; without them the rename leaves MQ7019 behind. The refactors of one batch share
    their working copies, so two refactors of different types may rewrite one document: values land first at the index's
    pointers, the structural edits after. MQ7020 also refuses a `codes` key the target holds itself or keeps as a former
    code, a merged-away former code that is a code or former code of the target, a target seed with a rows script that a
    seed would join, and a retire or merge whose removed row or type an `x-ref` `of` of an extension file or a local
    pack, or a pack parameter value in `maquettiste.json`, names: a batch writes model documents only, and a delete only
    warns about those. A diagram that shows both types keeps the target's member. `rename-reference-type` compares names
    case-insensitively and rewrites no use. The planner gains three things (`Loading/ChangePlanner.cs`,
    `Loading/ChangePlanner.Translations.cs`): a seed in its target's folder as the snapshot had it follows the target
    the plan renames when the plan updates it after the target, its CSV rows file with it (the refactor plans every seed
    of the type after the type); and a sub-element that moves to another element of the plan, such as a row a merge
    moves into a joined seed, keeps its translations and does not count as deleted in the delete check.
18. **Taken during the build of D53.** A partial run publishes no entity's or relation's mappings, so `RDatabase` gains
    an internal `TemplateCollections` list, filled where `ResolveRun` records a mapping
    (`Resolution/ResolvedPhysical.cs`, `Resolution/ResolveRun.Conceptual.cs`); `CodeHolders.Of` reads it in both runs.
    `ClassifierInput` gains `CodeMaps`, `Holders` and `BoundTypes` (the renamed types bound in its database). A holder
    is moved through the write's key maps and named as the after capture names it; a holder the write drops is left out.
    Value maps chain after every rename the write records and before its other facts; a write without value maps keeps
    its order. A code map chooses the databases whose footprint or reach holds the type whose codes move or an element
    that references it, and for a type rename every database without a binding. `ModelStore.Schemas.cs` hands the
    refactors' code maps to the write. MQ4114 is not reported while `migrations.record` is `off`; a `pluralName` change
    has no refactor, so its MQ4114 asks for a value map written by hand.
19. **Taken during the build of D54.** MQ4112 reads where the baseline stored codes from the baseline's own resolve
    (`CaptureDatabasesAsync` and `BaselineCheck.CapturesAsync` take `holders`). Only value maps the baseline lacks
    explain a change, as for MQ4102. A row's changed code and the former code it leaves are one finding, on the cell. An
    added former code needs a value map only when a type held the code in the baseline: the type itself, or a type the
    working model no longer has (a merge); a former code a merge moved over, or one born after the baseline, needs none.
    A rename needs its fact in the databases that held the type in the baseline and in those without a binding of it; a
    name and a plural name changed together are one finding, on `/name`. `StaleFacts.Chains` and `FactFolder.Fold` take
    the baseline's codes (`BaselineCodes`, in `StaleFacts.cs`); a type that a rows script fills may have held any code,
    so its value maps are never stale. Fold counts a value map's collections as changes of their owner tables
    (`Migrations/FactFolder.cs`).
20. **Taken during the build of D55 and D56.** The step's guard on an enum type reads the codes a column holds after the
    map and names those that are no label, instead of every `to`: a `to` no stored value needs never stops the
    migration. A column whose enum type changes (a merge, or a strategy that stops being `native`) is retyped through
    text inside one `DO` block guarded by its current type, its default dropped and set again; a merged native array
    goes through `text[]`, since a `USING` expression cannot hold a subquery. The `UPDATE` carries `ELSE <c>`, so the
    `CASE` takes the column's type for integer and UUID codes. A merge whose codes all match records a value map without
    values; its step still retypes and reconciles the columns. `column_objects_sql` splits into
    `column_objects_drop_sql` and `column_objects_add_sql` (output unchanged), `migration.scriban` includes
    `_data.scriban` for a bound type's rows, and outside PostgreSQL `value_map_todos` writes the TODO line, which no
    dialect but PostgreSQL wrote before. A merged-away seed whose rows all matched or mapped is deleted with its type
    (`ModelStore.ReferenceRefactors.cs`), as the merge acceptance expects; decision 9 still retargets a seed that keeps
    a moving row. The acceptance seed is `DemoInvoices` (a seed name is an identifier) with the `createdAt` cells
    `audited` requires, and the rename acceptance adds a database that stores no codes, so its dry run computes facts.
21. **Taken during the review fixes (issue #2).** Step 2 upserts only the rows the map points to (every row after a
    merge), each row a reference field of it names first, in foreign-key order, after creating every lookup table those
    fields reference, and each table and row once per migration. Before the first enum retype the step drops every view
    of the model, each before the views it reads, and the migration creates them all again at its end, after the schema
    statements: the model does not say which views read which column, and a view created inside the step would use
    definitions whose tables and columns are not renamed or added yet. A view outside the model needs `raw` facts
    (`packs/sql-ddl/README.md`). A value map without values reads as a merge when it names a `fromReferenceType`, and as
    a rename when only the plural changes. `rename-reference-type` plans only the seeds in the type's own seed folder.

#### Schema and files

| File | Change |
| --- | --- |
| `reference-type.json` | `formerCodes` after `bindings` in `x-order`: array of `{ code, replacedBy }`, `x-sort` `code`, default `[]`; `code` is a string (the code's canonical text), `replacedBy` an id |
| `migration.json` | `collections` after `objects` (array of `{ table, schema, tableName, attribute, name }`, `table` a `factKey`, `attribute` a historical id, `x-sort` by `table` then `attribute`); `fromReferenceType` after `referenceType` (historical id); `migrationState.pluralName` after `name`; a `then` of its own for `value-map`, apart from `state-map`'s: `referenceType` required, `objects`, `collections` and `values` may be empty, MQ4106 judges the rest |
| `batch.json` | `op` gains the four operations; `code` (string), `to` (string) and `codes` (object of strings) join the operation's keys, after `name` in `x-order` |

`PaymentTerm` after `rename-code` from `net30` to `net-30` (invented name and ids):

```json
{ "$schema": "../../.schema/v1/reference-type.json", "kind": "reference-type", "id": "01JCB7P2N4Q6S8T0V2W4X6Y8Z0",
  "name": "PaymentTerm", "code": { "id": "01JCB7P2N4Q6S8T0V2W4X6Y8Z1", "length": 8 },
  "label": { "id": "01JCB7P2N4Q6S8T0V2W4X6Y8Z2", "length": 64 },
  "formerCodes": [ { "code": "net30", "replacedBy": "01JCB7P3A0B1C2D3E4F5G6H7J8" } ] }
```

The fact it records in `main`, where `Invoice.paymentTerm` maps to `invoices.payment_term`:

```json
{ "$schema": "../../../../../.schema/v1/migration.json", "kind": "migration", "id": "01JCB7Q1D2E3F4G5H6J7K8M9N0",
  "database": "01J92P0V1QRN2181XM2ZWE02W4", "fact": "value-map",
  "objects": [ { "kind": "column", "table": "01J92P0V0FJ23CGSNKM7P1W5V7@01J92P0V1QRN2181XM2ZWE02W4",
    "key": "01JCB7P4K5M6N7P8Q9R0S1T2V3", "schema": "billing", "tableName": "invoices", "name": "payment_term" } ],
  "from": { "name": "PaymentTerm" }, "to": { "name": "PaymentTerm" },
  "referenceType": "01JCB7P2N4Q6S8T0V2W4X6Y8Z0", "values": [ { "from": "net30", "to": "net-30" } ] }
```

One operation of each kind. A batch refactors a type once (decision 10), so these are four batches; phase 1's batch
names may stand for `id` and `target`:

```json
{ "operations": [ { "op": "rename-code", "id": "01JCB7P2N4Q6S8T0V2W4X6Y8Z0", "code": "net30", "to": "net-30" } ] }
{ "operations": [ { "op": "retire-code", "id": "01JCB7P2N4Q6S8T0V2W4X6Y8Z0", "code": "due", "to": "net60" } ] }
{ "operations": [ { "op": "merge-reference-types", "id": "01JCB7R5S6T7V8W9X0Y1Z2A3B4",
                    "target": "01JCB7P2N4Q6S8T0V2W4X6Y8Z0", "codes": { "n90": "net60" } } ] }
{ "operations": [ { "op": "rename-reference-type", "id": "01JCB7P2N4Q6S8T0V2W4X6Y8Z0", "name": "PaymentCondition" } ] }
```

#### The four operations

| Operation | Input | Writes | Refused (MQ7020) when |
| --- | --- | --- | --- |
| `rename-code` | `id` (type), `code`, `to` | The row's code cell; every default, `allowedValues` entry and cell naming `code`; a former code `{ code, replacedBy: the row }` | `code` is not a live code, or a scripted row's; `to` equals `code`, is live, is a former code, or does not fit the code (type, length, pattern, canonical UUID) |
| `retire-code` | `id`, `code`, `to` | Removes `code`'s row and its translations; rewrites uses of `code` to `to`; moves `x-ref` values and former codes naming the removed row to `to`'s row; a former code `{ code, replacedBy: to's row }` | `code` or `to` is not a live code, or a scripted row's; `to` equals `code` |
| `merge-reference-types` | `id` (A), `target` (B), `codes` (optional) | Decision 9 | A equals B; either is not a reference type; their code types differ; A has a binding or scripted rows; a `codes` key is not a live A code or its value not a live B code; a moving code is a former code of B or does not fit B's code; a moving row holds a value for a field B lacks (same name and type); two seeds of one name differ in environments or apply |
| `rename-reference-type` | `id`, `name` | The type's `name`; its file and its seeds' folder move as on any rename | `name` is the current name, is not a valid element name, or another reference type has it |

Every operation is also refused when `id` is not a reference type, or when another operation of the batch refactors the
same type. A refused operation refuses the batch, as an expanded operation does today.

`usage` (`RefactorUsage`) on the operation's item, in a dry run and on apply: the number of uses rewritten, the first
200 (`ReferenceInfo`, ordinal by element id, then pointer), and the rows moved, rows removed and translations removed.
The batch's `facts` list the value maps the write records (phase 1 section 3.2: null in a dry run whose change reaches
every database, computed on save).

#### Recording the value map

- `Migrations/CodeMap.cs` (new, internal):
  `CodeMap(ReferenceType, FromReferenceType, FromNames, ToNames, Values, Names)`. The expansion in
  `ModelStore.ReferenceRefactors.cs` builds one per operation; `Names` marks a type rename, which reaches every
  database.
- `Migrations/CodeHolders.cs` (new, internal) lists a type's holders in a resolved database: columns whose
  `RColumn.ReferenceType` is the type (after 3.2's D21 that includes typed designed columns, with no code of its own
  here), and the entries of `REntityMapping.TemplateDefined` and `RRelationMapping.TemplateDefined` typed by it, with
  the owner table's key.
- `RecordingRequest` (`ModelStore.Migrations.cs:135`) gains the write's code maps, and `ClassifierInput` gains them with
  each database's holders from its before resolve. A write with code maps resolves a chosen database again when the
  capture cache holds its capture but not its holders.
- `FactClassifier` emits one `value-map` per code map and database that has holders, or for a type rename, per database
  without a binding of the type. It ranks after the write's renames and before its `change-type` facts on the same
  columns, so a conversion reads mapped codes. Its `after` links follow phase 1's rule.
- `migrations.record: off` records no value map, as it records no other fact.
- MQ4114: a save, create or batch with no code map that changes the code of a row it keeps (same row id) in a seed of a
  reference type, or the type's `name` or `pluralName`, gets the warning on that change, as MQ4110 is attached today.

#### Rules

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ7017 | E | A former code is a live code of its type, is listed twice, or does not fit the type's code (type, length, pattern). | Remove the entry |
| MQ7018 | E | A former code's `replacedBy` names a row that is not a row of its type. A missing row is MQ2001. | Point it at a row of the type |
| MQ7019 | E | A default, `allowedValues` entry or seed cell names a former code. It replaces MQ3019, MQ7009, MQ7011 and 3.2's MQ7014 for that value. | Write the code of the `replacedBy` row |
| MQ7020 | E | A reference-type refactor cannot apply (an unknown or scripted code, a code taken or former, mismatched types, a binding, a missing field, a second refactor of the type in the batch); the message gives the reason. | |
| MQ4112 | E | Since the baseline, a kept row's code changed, a former code was added, or a type's name or plural name changed, and no open value-map fact of a database holding the type records it. | Run the refactor, or write the fact |
| MQ4113 | E | An open value-map fact maps away a code that is a live code of its reference type. | Give the row another code, or fold the fact if it is stale |
| MQ4114 | W | A write changed a kept reference row's code, or a reference type's name or plural name, outside the refactor operations, so no value map was recorded. | Use the refactor |
| MQ4106 | E | Changed text: a fact's fields do not fit its type: from equal to to, a conversion or data check with both forms or neither, released without held, a key or object listed twice, a values code listed twice or mapped to itself, an object kind the type does not take, or a value map that maps codes in no column or collection and changes no name, or whose `to` is also a `from`. | |
| MQ4103 | W | Changed text: an open fact the baseline lacks is stale: the model no longer shows its combined effect (a change made and undone, a pending change reverted or widened back), or a value map's codes were not codes of its type in the baseline model and its names did not change; migration fold removes it. | |

MQ4112 points at the seed cell of a changed code, `/formerCodes/<i>` of an added former code, or `/name` and
`/pluralName` of the type. It names the databases that lack the fact.

#### Resolution and the resolved model

```csharp
namespace Maquettiste.Engine.Model;
public sealed record ReferenceType { /* existing members, plus: */ IReadOnlyList<FormerCode> FormerCodes = []; }
public sealed record FormerCode { required string Code; required string ReplacedBy; }   // a row id; indexed
public sealed record Migration { /* plus: */ string? FromReferenceType;
    IReadOnlyList<MigrationCollection> Collections = []; }
public sealed record MigrationCollection { required string Table; string? Schema; string? TableName;
    required string Attribute; string? Name; }                        // Table a historical key; Attribute not walked
public sealed record MigrationState { /* plus: */ string? PluralName; }

namespace Maquettiste.Engine;                                         // Editor/ResolvedRecords.cs
public sealed record ReferenceTypeRecord(/* existing parameters, then: */ IReadOnlyList<FormerCodeRecord> FormerCodes);
public sealed record FormerCodeRecord(object? Code, string ReplacedBy);   // ReplacedBy: the row id

namespace Maquettiste.Engine;
public enum BatchOp { /* ... */ RenameCode, RetireCode, MergeReferenceTypes, RenameReferenceType }
public sealed record BatchOperation(/* existing parameters, then: */ string? Code = null, string? To = null,
    IReadOnlyDictionary<string, string>? Codes = null);
public sealed record SaveResult { /* plus: */ public RefactorUsage? Usage { get; init; } }
public sealed record RefactorUsage(int UseCount, IReadOnlyList<ReferenceInfo> Uses, int RowsMoved, int RowsRemoved,
    int TranslationsRemoved);

namespace Maquettiste.Engine.Resolution;
public sealed class RReferenceType { IReadOnlyList<RFormerCode> FormerCodes; }      // sorted by code
public sealed class RFormerCode : RObject { object? Code; RRow ReplacedBy; }        // Kind "former-code"
public sealed class RMigration { string? FromReferenceType; IReadOnlyList<RMigrationCollection> Collections; }
public sealed class RMigrationCollection { string Table; string? Schema; string? TableName; string Attribute;
    string? Name; string CurrentTable; }                              // CurrentTable moved through later rekeys
public sealed class RMigrationState { string? PluralName; }
```

- The index records `replacedBy` as a reference to the row (field `replacedBy`), so `get_references` on a row lists the
  former codes pointing at it, and deleting that row is refused while they do. `ReferenceDataIndex` keeps each type's
  former codes, so the rules and `names-to-ids` find them without a resolve.
- A former code is part of its type's document, so `e:<type id>` covers it in planning.
- `get_resolved_model` gives `formerCodes` on reference-type records, and the `from` and `to` of migration records carry
  `pluralName` (they are `MigrationState`). Migration records carry no `objects`, `values` or `collections` today, and
  3.5 adds none: `list_migrations` summarizes a value map and the fact file holds it.
- `ResolvedCoverageTests` gains the targets `former code` (`reference-type.json`, `formerCodes/[]`, `RFormerCode`) and
  `migration collection` (`migration.json`, `collections/[]`, `RMigrationCollection`).
- The fact content hash covers the new fields as it covers every field it does not remove (phase 1 section 3.2).

#### CLI and MCP

- `apply_batch` and `maquettiste model apply <batch.json> [--dry-run] [--format text|json]` take the four operations;
  the `apply_batch` description (`src/Maquettiste.Cli/Mcp/ModelTools.cs:196-197`) lists them. The text output prints,
  per refactor, `<op> <type>: <n> uses, <m> rows moved, <k> rows removed, <t> translations removed`, then the facts as
  today. No new verb or tool.
- `list_migrations` and `migration list` summarize a value map as `maps <type> codes <from> -> <to>, ... in <n> columns`
  or `renames reference type <from> to <to>`.
- `import_seed_csv` and `seed import` skip a row whose `@code` is a former code and list it with MQ7019 in the preview.
- `names-to-ids` turns a row value given as a former code into the id of its `replacedBy` row.
- `get_references` lists `replacedBy` references. `reference_type_usage` is unchanged here (3.2 extends it).
- `docs/api/openapi.yaml`: `BatchOperation` (the operations, `code`, `to`, `codes`), `SaveResult.usage`,
  `RefactorUsage`, `ReferenceTypeRecord.formerCodes` with `FormerCodeRecord`, and `MigrationRecordState.pluralName`.

#### Templates and packs

- `reference_type.former_codes`: each with `code` and `replaced_by` (the row). `row <type> "<code>"` keeps finding live
  codes only.
- **sql-ddl, the value-map step** (`_migration.scriban`, `fact_steps`, replacing the TODO line on PostgreSQL), in the
  fact's applied order, before the schema statements and the native type prelude:
  1. A type rename (`from` and `to` names differ, no `fromReferenceType`): where the type is realized as `lookup-table`,
     rename the lookup table from the name `from` gives to the current one when only the old one exists (a `DO` block
     over `to_regclass`); where it is `native`, rename the enum type the same way. A `tableName` option keeps the name,
     so nothing is written.
  2. Each holder column, named as the database knows it before this migration (the diff's old name when the diff renames
     it): make the target's rows exist where its realization keeps rows (`lookup_table_sql` and `lookup_rows_sql`; for a
     bound type, insert its missing rows into the bound table as a `once` seed would); drop the column's strategy
     objects by name; map with `UPDATE <t> SET <c> = CASE <c> WHEN 'a' THEN 'b' ... END WHERE <c> IN ('a', ...)`; then
     reconcile the objects (`column_objects_sql`).
  3. Where the type is `native` and was not merged: once per fact, not per column (every column of the enum type follows
     its labels), each value whose `from` is a label of the type and whose `to` is not is
     `ALTER TYPE ... RENAME VALUE`, in a `DO` block that checks `pg_enum` first, so it can run twice. The remaining
     values are each holder column's UPDATE, in a `DO` block that first raises, naming the codes, when a `to` is not a
     label of the type: PostgreSQL cannot use an enum value in the transaction that adds it, and a migration is one
     transaction.
  4. A column that stays `native` after a merge:
     `ALTER COLUMN ... TYPE <new>_t USING (CASE <c>::text WHEN ... ELSE <c>::text END)::<new>_t`, with the same guard on
     the new type for every code the column may hold after the map (the `to` codes and the codes that move into B).
  5. Each collection: a junction table drops the rows the map would duplicate for one owner, then updates its code
     column, with the same drop and reconcile of its strategy objects; a native array keeps the first position of each
     mapped code.
  6. The fact's ledger row.
- **sql-ddl, elsewhere.** On PostgreSQL the column step of `_migration.scriban` handles a `referenceType` change,
  which today falls through to a TODO line (`:897-906`), unless a value map in the diff covers the column: it drops and
  reconciles the column's strategy objects (`column_objects_sql`) and, under `native`, retypes the column
  (`USING <c>::text::<new>_t`). The guard in `native_type_reconcile_sql` leaves out the type's former codes, which may
  stay enum labels after a map. Lookup rows of former codes are deleted by the existing retire step once no column
  stores them.
- **csharp-dapper.** `reference-type.scriban` writes one `[Obsolete]` field per former code that returns the replacing
  row, so code that names the old constant still compiles and points at the new row. A former code whose member name a
  live row's member already has is left out: `net30` and `net-30` both give `Net30`.
- Goldens unchanged: no golden fixture has former codes, and the steps above write nothing without a value map or a
  `referenceType` change. The alias test copies the reference-data fixture into a temporary folder.

#### Editor follow-ups

- Reference data screen: row menu Rename code and Retire code, type menu Merge into and Rename, each opening the dry
  run's usage and files before it applies; former codes listed in the type's Fields tab.
- Until then the generic JSON form edits `formerCodes`, and the batch endpoint takes the operations.

#### Docs

`docs/guides/reference-data-and-seeds.md` (former codes, the four refactors, a worked billing example),
`docs/guides/schema-changes.md` (the value-map step per realization),
`docs/engineering/reference-types-seeds-localization.md` (the paragraphs at `:44` and `:209`), `docs/mcp.md` (the four
operations, `usage`), `docs/user-guide.md`, `packs/sql-ddl/README.md`, `packs/csharp-dapper/README.md`,
`skills/maquettiste-modeling/SKILL.md` and `docs/releases.md` under `## 0.12.0 (unreleased)`.

**Not doing**

- Value maps recorded from plain edits: the refactors record them, MQ4114 points a plain edit at them and MQ4112 catches
  hand edits against a baseline (decision 6).
- Retiring a code with no replacement: delete the row, which is refused while the model uses it; a deprecation without a
  replacement is a field of the type that the project declares.
- Splitting a code into several (one stored value cannot go to two rows), swaps and chains in one fact (decision 8).
- Joining value maps in fold; judging them stale without a baseline model (decision 11).
- Dropping the strategy objects a merged-away type leaves in a database (its lookup table or enum type): they stay, as a
  deleted type's do today, until a `raw` fact removes them or the live planner (scope 6.1, phase 3) reports them.
- Merging a type that has a binding or scripted rows (the table above, decision 13).
- Value maps for codes a rows script changes: scripted rows are not documents a refactor writes.
- Mapping `native` values to labels the enum type gains in the same migration: PostgreSQL cannot use them before the
  transaction commits. The step stops naming the codes; the guide shows adding the labels in an earlier migration (a
  `raw` fact with `ALTER TYPE ... ADD VALUE`).
- Data steps outside PostgreSQL.
- Dedicated CLI verbs: `model apply` takes a one-operation batch.
- Editor screens (follow-ups above).

**Tests**

- `FormerCodeRuleTests` (Validation, new): `A_former_code_that_is_live_is_MQ7017`,
  `A_former_code_listed_twice_is_MQ7017`, `A_former_code_that_does_not_fit_the_code_is_MQ7017`,
  `A_replaced_by_row_of_another_type_is_MQ7018`, `A_dangling_replaced_by_is_MQ2001`,
  `A_default_naming_a_former_code_is_MQ7019_and_not_MQ3019`, `A_cell_naming_a_former_code_is_MQ7019_and_not_MQ7009`,
  `An_allowed_value_naming_a_former_code_is_MQ7019_and_not_MQ7011`.
- `ReferenceDataStoreTests` (Store): `Import_skips_a_row_whose_code_is_a_former_code`,
  `Deleting_a_row_that_a_former_code_points_to_is_refused`. `NamesToIdsTests` (Store):
  `A_former_code_converts_to_the_replacing_row`. `ReferenceDataResolutionTests` (Resolution):
  `Former_codes_resolve_to_the_replacing_rows`.
- `ReferenceRefactorStoreTests` (Store, new): `Rename_code_rewrites_uses_and_keeps_a_former_code`,
  `Rename_code_to_a_live_or_former_code_is_MQ7020`, `Rename_code_of_a_scripted_row_is_MQ7020`,
  `Retire_code_removes_the_row_and_moves_x_ref_values`, `Retire_code_moves_the_former_codes_of_the_removed_row`,
  `Merge_moves_unmatched_rows_with_their_ids_and_translations`,
  `Merge_maps_codes_and_moves_every_reference_to_the_target`, `Merge_joins_a_seed_of_the_same_name`,
  `Merge_with_a_binding_or_a_missing_field_is_MQ7020`, `Rename_type_moves_its_file_and_seed_folder`,
  `Two_refactors_of_one_type_in_a_batch_are_MQ7020`, `A_dry_run_writes_nothing_and_lists_usage`.
- `BatchPreviewTests` (Store): `A_refactor_preview_carries_usage_and_facts`.
- `MigrationRulesTests` (Validation): `A_value_map_with_a_chain_is_MQ4106`,
  `A_value_map_with_no_values_and_the_same_names_is_MQ4106`, `A_value_map_that_maps_away_a_live_code_is_MQ4113`.
- `FactHashTests` (Migrations): `Collections_and_names_count_in_the_value_map_hash`. `MigrationResolutionTests`
  (Resolution): `Value_map_collections_move_with_later_rekeys`.
- `FactRecordingTests` (Integration): `Rename_code_records_one_value_map_per_database_holding_the_type`,
  `A_value_map_lists_collections_by_owner_table_and_attribute`,
  `Rename_type_records_names_in_every_database_without_a_binding`,
  `A_merge_records_its_value_map_before_the_strategy_change_type`, `A_plain_code_edit_records_nothing_and_warns_MQ4114`,
  `Recording_off_records_no_value_map`.
- `BaselineValueMapTests` (Integration, new): `A_code_changed_since_the_baseline_without_a_map_is_MQ4112`,
  `A_former_code_added_since_the_baseline_is_explained_by_its_map`,
  `A_type_renamed_since_the_baseline_needs_its_names_fact`,
  `A_value_map_for_a_code_born_after_the_baseline_is_stale_and_fold_removes_it`,
  `Without_a_baseline_model_no_value_map_is_stale`.
- `ModelApplyTests` (Cli): `A_dry_run_prints_refactor_usage`. `McpWriteTests`:
  `Apply_batch_takes_a_rename_code_operation`. `MigrationTests` (Cli): `Migration_list_summarizes_a_value_map`.
- `MigrationFactTests` (Packs): `A_value_map_writes_a_data_step_on_PostgreSQL_and_a_TODO_elsewhere`.
- `ReferenceDataTests` (Packs; PostgreSQL but the last): `A_renamed_code_moves_stored_values_under_lookup_table`,
  `A_renamed_code_moves_stored_values_under_check`, `A_renamed_code_is_renamed_in_place_under_native`,
  `A_retired_code_moves_into_an_existing_code`, `A_merge_maps_stored_codes_under_lookup_table_and_native` (a theory over
  the two), `A_renamed_type_renames_its_lookup_table_and_enum_type`, `A_value_map_step_runs_twice_with_no_change`,
  `A_native_map_to_a_value_the_enum_lacks_stops_with_the_codes_named`, `Collections_lose_the_duplicates_a_map_makes`,
  `A_reference_type_change_without_a_map_reconciles_the_column`, `Former_codes_become_obsolete_aliases_in_csharp`
  (csharp-dapper over a temporary copy of the reference-data fixture).
- **Shared setup of the acceptance tests.** A billing copy gains the reference type `PaymentTerm` (codes `net30`,
  `net60` and `due`; string code of length 8). Its settings declare the strategies `lookup-table`, `check` and `native`
  and choose `lookup-table` for the project, so `main` realizes it as a lookup table. `Invoice` gains
  `paymentTerm` typed by it with default `net30`, so `invoices.payment_term` holds its codes. An `Invoice` seed
  `demo-invoices` has cells `net30` and `due`, and the `due` row has a `de` label. The reference type `CreditTerm`
  (codes `net30` and `n90`) types `Customer.creditTerm` and, through 3.2, a column `term` added to the `notes` table.
- **Acceptance (rename a code, alias kept):**
  `Integration/ReferenceRefactorTests.Renaming_net30_keeps_a_former_code_and_records_one_value_map` (new). Setup: the
  shared setup; `rename-code` `net30` to `net-30` as a dry run, then applied. Expected: the dry run writes nothing; its
  usage lists the default of `paymentTerm` and one cell of `demo-invoices`; its facts hold one value map in `main` for
  `invoices.payment_term`, `net30` to `net-30`. After applying, `PaymentTerm` lists the former code `net30` with its own
  row, the default and the cell read `net-30`, and validation has no error.
- **Acceptance (the data step):** `ReferenceDataTests.A_renamed_code_moves_stored_values_under_lookup_table`. Setup: a
  PostgreSQL database built from the model before the rename, with invoices storing `net30`; generate after the rename;
  apply the migration and `seed.sql`. Expected: every `payment_term` is `net-30`; the lookup table has `net-30` and not
  `net30`; the ledger holds the fact's row; applying both again changes nothing.
- **Acceptance (retire a code):** `ReferenceRefactorTests.Retiring_due_into_net60_removes_its_row_and_moves_its_uses`.
  Expected: the `due` row and its `de` label are gone; the cell reads `net60`; `PaymentTerm` lists `due` replaced by the
  `net60` row; a value map `due` to `net60`. On PostgreSQL,
  `ReferenceDataTests.A_retired_code_moves_into_an_existing_code` shows stored `due` becoming `net60`.
- **Acceptance (merge two types):** `ReferenceRefactorTests.Merging_credit_terms_into_payment_terms_moves_every_use`
  (runs after 3.2's D21). Setup: `merge-reference-types` `CreditTerm` into `PaymentTerm` with codes
  `{ "n90": "net60" }`. Expected: `Customer.creditTerm` and `notes.term` are typed by `PaymentTerm`; `CreditTerm` and
  its seed are gone; `PaymentTerm` lists `n90` replaced by the `net60` row; one value map in `main` with `referenceType`
  `PaymentTerm`, `fromReferenceType` `CreditTerm`, `from.name` `CreditTerm`, `n90` to `net60`, for
  `customers.credit_term` and `notes.term`. On PostgreSQL,
  `ReferenceDataTests.A_merge_maps_stored_codes_under_lookup_table_and_native` shows customers storing `n90` holding
  `net60`.
- **Acceptance (rename a type):** `ReferenceDataTests.A_renamed_type_renames_its_lookup_table_and_enum_type`. Setup:
  `rename-reference-type` `PaymentTerm` to `PaymentCondition`, under `lookup-table`, then under `native`. Expected: one
  value map in `main` with the two names; the migration renames `payment_terms` to `payment_conditions` with its rows
  and foreign keys kept, and under `native` renames `payment_term_t` to `payment_condition_t`.
- **Acceptance (safety net):** `BaselineValueMapTests.A_code_changed_since_the_baseline_without_a_map_is_MQ4112`. Setup:
  a snapshot of the shared setup; then `net60` becomes `net-60` in the seed file on disk. Expected:
  `validate --against <snapshot>` reports MQ4112 at that cell, naming `main`; the same change through `save_element`
  carries MQ4114.

**Tasks**

Legend in section 2.1 of `next-phase-1-design.md`; order in section 7. Each task updates the docs rows of what it
builds. D50 follows 3.4's D44, which follows the tasks of 3.1, 3.2 and 3.3 that edit the same model, rule, record and
batch files, so 3.5 meets no shared file in parallel.

- **D50. Former codes.** Runs after D44, which follows D43: they edit `Model/ReferenceData.cs`,
  `Validation/ReferenceDataRules.cs`, `Validation/TableSeedRules.cs` and `ModelStore.LocalizationApi.cs` too; and after
  D45, so the docs rows each task updates never meet 3.4's guide task in parallel. Owns:
  `schemas/v1/reference-type.json` and its copy, `FormerCode` and `ReferenceType.FormerCodes` in
  `Model/ReferenceData.cs`, `Model/ModelIndexer.cs` (`replacedBy`), `Model/ReferenceDataIndex.cs`, MQ7017 to MQ7019 and
  the former-code cases of MQ3019, MQ7009 and MQ7011 in `Validation/ReferenceDataRules.cs`, the former-code case of
  3.2's MQ7014 where 3.2 reports it (`Validation/TableSeedRules.cs`, `Validation/ColumnTypeRules.cs`), `RFormerCode` in
  `Resolution/ResolvedReferenceData.cs`, `Resolution/ResolveRun.ReferenceData.cs`, the import change in
  `ModelStore.LocalizationApi.cs`, the `import_seed_csv` description in `src/Maquettiste.Cli/Mcp/ModelTools.cs`,
  `ModelStore.NamesToIds.cs`, `ReferenceTypeRecord.FormerCodes` and `FormerCodeRecord` in `Editor/ResolvedRecords.cs`
  and `docs/api/openapi.yaml`, `Diagnostics/RuleCatalog.cs` (MQ7017 to MQ7019), `CatalogTests.cs` (+3),
  `ResolvedCoverageTests` (the `former code` target). Tests: `FormerCodeRuleTests`, the former-code cases of
  `ReferenceDataStoreTests`, `NamesToIdsTests` and `ReferenceDataResolutionTests`. Verify:
  `E "~FormerCodeRuleTests|~ReferenceDataStoreTests|~NamesToIdsTests|~ReferenceDataResolution"`;
  `E "~CatalogTests|~ResolvedCoverageTests|~ValidationFixtureTests|~Model."`; `editor`; `copies`; `record`.
- **D51. The value-map format.** Runs after D50 and D41 (they edit `Editor/ResolvedRecords.cs`, `docs/api/openapi.yaml`
  and `Resolution/ResolvedPhysical.cs` too). Owns: `schemas/v1/migration.json` and its copy, `Model/Migrations.cs`,
  `RMigration`, `RMigrationCollection` and `RMigrationState` in `Resolution/ResolvedPhysical.cs`,
  `Resolution/ResolveRun.Migrations.cs`, the MQ4106 cases and MQ4113 in `Validation/MigrationRules.cs`, the value-map
  summary in `Migrations/FactDocuments.cs` (decision 16), `MigrationRecordState` in `docs/api/openapi.yaml`,
  `Diagnostics/RuleCatalog.cs` (MQ4113, MQ4106's text), `CatalogTests.cs` (+1), `ResolvedCoverageTests` (the
  `migration collection` target). Tests: the value-map cases of `MigrationRulesTests`, `FactHashTests` and
  `MigrationResolutionTests`, `MigrationTests.Migration_list_summarizes_a_value_map`. Verify:
  `E "~MigrationRulesTests|~FactHashTests|~MigrationResolutionTests|~Migrations."`;
  `E "~CatalogTests|~ResolvedCoverageTests"`; `C "~MigrationTests"`; `editor`; `copies`; `record`.
- **D52. The four operations.** Runs after D51 (both edit `docs/api/openapi.yaml`, `Diagnostics/RuleCatalog.cs` and
  `CatalogTests.cs`). Owns: `BatchOp`, `BatchOperation`, `SaveResult.Usage` and `RefactorUsage` in `ModelStore.cs`,
  `Loading/BatchParser.cs`, `schemas/v1/batch.json` and its copy, the dispatch in `ModelStore.Schemas.cs`
  (`IsExpandedOperation`), `ModelStore.ReferenceRefactors.cs` (new), the batch parts of `docs/api/openapi.yaml`, the
  `apply_batch` description in `src/Maquettiste.Cli/Mcp/ModelTools.cs`, `src/Maquettiste.Cli/Commands/BatchOutput.cs`,
  `Diagnostics/RuleCatalog.cs` (MQ7020), `CatalogTests.cs` (+1). Tests: `ReferenceRefactorStoreTests`,
  `BatchPreviewTests`, `ModelApplyTests`, `McpWriteTests.Apply_batch_takes_a_rename_code_operation`. Verify:
  `E "~ReferenceRefactorStoreTests|~BatchPreviewTests|~ModelStoreBatchTests|~BatchNamesTests|~CatalogTests"`;
  `C "~ModelApplyTests|~McpWriteTests|~McpSurfaceTests"`; `editor`; `copies`; `record`.
- **D53. Recording value maps.** Runs after D52. Owns: `Migrations/CodeMap.cs` and `Migrations/CodeHolders.cs` (new),
  `Migrations/FactClassifier.cs`, `ModelStore.Migrations.cs`, the code-map handoff in
  `ModelStore.ReferenceRefactors.cs`, MQ4114 where saves and batches attach write findings (`ModelStore.cs`),
  `Diagnostics/RuleCatalog.cs` (MQ4114), `CatalogTests.cs` (+1). Tests: the value-map cases of `FactRecordingTests` and
  `FactClassifierTests`. Verify:
  `E "~FactRecordingTests|~FactClassifierTests|~BatchPreviewTests|~CatalogTests"`; `record`.
- **D54. Baseline checks and fold.** Runs after D53. Owns: `Migrations/BaselineCheck.cs` (MQ4112),
  `Migrations/StaleFacts.cs` (value maps), the fold path in `ModelStore.MigrationTools.cs`, `Diagnostics/RuleCatalog.cs`
  (MQ4112, MQ4103's text), `CatalogTests.cs` (+1). Tests: `BaselineValueMapTests`. Verify:
  `E "~BaselineValueMapTests|~FactRecordingTests|~NoVersionControlTests|~CatalogTests"`;
  `C "~MigrationTests|~ValidateTests"`; `record`.
- **D55. The data step and generated aliases.** Runs after D53, D42 (sql-ddl templates and `packs/sql-ddl/README.md`)
  and 3.1's D15 (`docs/guides/schema-changes.md`). Owns: `packs/sql-ddl/_migration.scriban` (the value-map step, the
  `referenceType` column change), `packs/sql-ddl/_reference.scriban` (the step's helpers, the native guard),
  `packs/sql-ddl/README.md`, `packs/csharp-dapper/reference-type.scriban`, `packs/csharp-dapper/README.md`,
  `docs/guides/schema-changes.md`. Tests: `MigrationFactTests`, the `ReferenceDataTests` cases. Verify:
  `PG P "~ReferenceDataTests|~MigrationFactTests"`; `PG P "~MigrationSafetyTests|~LedgerSqlTests"`;
  `P "~GoldenTests|~CompileTests"` (goldens unchanged).
- **D56. Guide and acceptance.** Runs after D54, D55, D45 (both edit the reference data guide, the user guide, the skill
  and the release notes) and 3.2's D21 (columns typed by a reference type, which the merge test moves). Owns:
  `docs/guides/reference-data-and-seeds.md`, `docs/engineering/reference-types-seeds-localization.md` (`:44` and
  `:209`), the operations in `docs/mcp.md`, `docs/user-guide.md`, `skills/maquettiste-modeling/SKILL.md`, the 0.12.0
  notes in `docs/releases.md`, and the acceptance cases of `Integration/ReferenceRefactorTests.cs` (new),
  `ReferenceDataTests` and `BaselineValueMapTests`. Verify:
  `E "~Integration.ReferenceRefactorTests|~BaselineValueMapTests"`; `PG P "~ReferenceDataTests"`;
  `C "~SkillRefreshTests|~McpSurfaceTests"`.

## 4. Milestone E: operations and the project's own concepts

### 4.1 Operations

**Why every team**

Most teams that model their data also expose commands over it: an HTTP API, message handlers, a command line, jobs.
Today the model holds the data and its lifecycles but not the commands, so request and response types, error codes and
the list of endpoints are written by hand beside the model and drift from it. An operation states one command once:
what it acts on, whether it changes data, its input, its results, its errors and the process events it fires. The shape
names no protocol, so each team's pack maps it to its own transport and language. Errors that many commands share (not
found, a stale version) come from a stereotype, stated once and inherited by id; a package can give them to every
operation in it through its default stereotypes. Requirements and properties cite an operation, its input members, its
outcomes and its errors by id, so a rename breaks no link.

**Today**

- No operation kind: `Model/ElementKind.cs:7-83` lists 25 kinds, and `Model/KindInfo.cs:18-45` registers each one with
  its schema file and folder. `KindInfo.Get` indexes that list by the enum's value (`Model/KindInfo.cs:64`). SPEC
  lists operations as an optional kind with implied CRUD (`SPEC.md:488`).
- Phase 1 reserved the `x-ref` kinds `operation`, `outcome`, `error` and `member`, all four as sub-element kinds. Their
  values report MQ2001 with an "arrives in a later phase" note (`Model/PropertySchemas.cs:29`, `:49-50`;
  `Validation/BuiltinRules.cs:199-208`).
- The index knows 19 sub-element kinds (`Model/ModelIndexer.cs:75-105`). None belongs to an operation.
- Process context and event payloads reuse the attribute record (`Model/Processes.cs:44`, `:254`) and resolve through
  `MakeAttribute` (`Resolution/ResolveRun.Processes.cs:169-190`). An attribute's type names a built-in keyword, an
  enum, a value object, a scalar type or a reference type, never an entity (`Model/Unions.cs:30-32`).
- A process event is a sub-element whose id is meant to survive its later promotion to a kind
  (`Model/Processes.cs:241`). A resolved event lists its transitions, not what fires it from outside the process
  (`Resolution/ResolvedProcesses.cs:206-222`).
- An `[ElementRef]` without targets takes any element or sub-element (`Model/ElementRefAttribute.cs:33`). A diagram
  member uses it (`Model/Diagram.cs:46`).
- Seeds and scenarios live in a folder named after the element they belong to (`Loading/ModelPaths.cs:246-247`).
- The `r:<id>` key hashes the files that reference that exact id, element or sub-element
  (`Model/ModelSnapshot.cs:324-338`): a reference to an event changes `r:<event id>`, not `r:<process id>`.
- Template scopes end at `each scenario` (`Planning/UnitRules.cs:19-23`, `schemas/v1/pack.json:109`), and no unit
  variable names an operation (`Rendering/BuiltinHelpers.cs:126-128`, `Rendering/RenderRun.cs:468-471`).
- `type_of` maps attributes, columns, view columns, types, enums, value objects, scalar types and keywords. It applies
  the `collection` pattern to attributes only and the `nullable` pattern to attributes, columns and view columns
  (`Rendering/TypeOf.cs:6-21`, `:46-70`).
- A write chooses no database for an element kind its database selection does not list
  (`ModelStore.Migrations.cs:416`), but it still classifies every non-annotation change that `Shapes` lets through
  (`ModelStore.Migrations.cs:204`, `:343-347`); only migration facts are skipped there.
- Translations of a seed or a scenario live in the shard of a related element's package
  (`Localization/LocalizationIndex.cs:644-657`), and a write moves entries only for the elements it changes
  (`Loading/ChangePlanner.Translations.cs:50-62`).
- The editor types its kind icons and labels as `Record<ElementKind, …>` (`src/editor/src/app/icons.tsx:31`,
  `src/editor/src/model/labels.ts:239`), and the stereotype form fails to compile when `appliesTo` gains a kind its
  groups leave out (`src/editor/src/workspaces/settings/stereotypeKinds.ts:18-19`).
- csharp-dapper writes plain process contracts (`process-contracts`, `packs/csharp-dapper/pack.json:90`) apart from the
  HTTP endpoints, which it writes only when `endpointsFolder` names a folder (`process-endpoints`, `:140`;
  `packs/csharp-dapper/process-endpoints.scriban:1-12`).

**Decisions taken here**

- **One built-in kind, `operation`.** It is appended last to `ElementKind`, with its `KindInfo` entry last, since
  `KindInfo.Get` indexes the registry by the enum's value. Schema `operation.json`. Section 4.2's `custom` and section
  4.3's `requirement` are appended after it (their first tasks run after E1).
- **Folder by subject.** An operation with a subject lives in `model/operations/<subject file stem>/`, one without in
  `model/operations/`, as seeds follow their target. Names are unique per subject, so `archive` on Invoice and `archive`
  on Customer never share a folder. Changing the subject moves the file, as a table follows its database.
- **Names.** An operation's name is an identifier (MQ3018), unique among the operations of its subject, or without a
  subject among the subject-less operations of its file's package (MQ3001). Input members are unique within `input`, the
  members of one `returns` within it, and outcomes within the operation (MQ3001, MQ3018). Errors are unique by name and
  by code, own and inherited together (MQ3026).
- **Name forms (batches).** An operation is found by `<name>`, by `<package path>.<name>` when its file states a
  package, and by `<subject form>.<name>` (`Invoice.send`, `Billing.Invoice.send`), as a scenario adds its process's
  forms. A member, outcome or error is `<operation form>.<name>`; a member of a `returns` is
  `<operation form>.<outcome name>.<name>`; a stereotype's error is `<stereotype form>.<name>`.
- **`subject`.** An optional entity id, not owning: deleting the entity is refused while an operation names it, and both
  resolutions clear the value. The entity's derived list is `REntity.Operations`.
- **`level`, not the scope's `target`.** Seeds and batches use `target` for an element id, and one key keeps one
  meaning. `level` is `record` (the default) or `collection`, and means something only with a subject. A record-level
  operation takes the subject record's key implicitly; `input` holds the rest. A collection-level one addresses the
  whole set (a search, a create, a bulk change).
- **`effect`** is required: `read` or `change`, independent of whether the operation takes input.
- **Members are their own sub-element.** `OperationMember`, index kind `operation-member`, after the `enum-member`
  pattern; `member` stays reserved for the members of custom elements (section 4.2). A member is exactly one of three
  forms (MQ3024): a typed value (`type`, a type reference as attributes take), a record key (`keyOf`, an entity id: the
  member holds the key of one record of that entity, and with `collection` a list of keys, the batch input of related
  ids) or a file (`file: true`, bytes with a name and a media type; `mediaTypes` lists the accepted ones and `length`
  caps the size in bytes). The attribute record does not fit: its type cannot name an entity, a file is not a value
  type, and `unique`, `indexed`, `readOnly`, `immutable`, `derived` and `defaultExpression` mean nothing in a request.
  The facet and default checks reuse `AttributeRules` (MQ3013, MQ3019, MQ3017, MQ2007).
- **Outcomes.** One or more (`minItems: 1`), each with `completes` (`now`, the default, or `later` for "accepted and
  waiting") and an optional `returns`. `defaultOutcome` names one by id; absent, the first is the default, the way a
  process's `initial` names a state (`Model/Processes.cs:58-60`).
- **`returns`, not the scope's "output".** Outputs are a pack's generated files; a routine's `returns` already means
  "the result" (`schemas/v1/routine.json:149-150`). A `returns` is exactly one of `members` (an inline shape, at least
  one member), `entity` (records of an entity, its attributes as the shape, until projections, phase 3, add
  `projection`) or `file` (a download; streaming is a file). `list: true` makes it a list; a list may set `paging`
  (`page` or `cursor`) with `maxPageSize`, always together (MQ3025). An outcome without `returns` returns nothing. The
  paging inputs (a page or a cursor, and a page size) are implied by `paging`, never declared as members.
- **Errors.** Each has a `name` (an identifier), an optional `code` (the stable text clients see; absent, the name) and
  an optional `input`, the input member it points at (MQ3027). Its display name and description are the translatable
  texts a client shows. The key is `input`, not `member`, because a redefinition entry (section 3.1) names the member it
  redefines with `member`.
- **Shared errors, through section 3.1's mechanism.** `ContributedMembers` (section 3.1, D6) serves operations as a
  second holder kind. The holder key is the operation id, the members are `Stereotype.Errors`, the redefinition record
  is `OperationErrorRedefinition` (an `IMemberRedefinition`), and applying it replaces `input`, `displayName` and
  `description`, the only facets an error redefinition may state. An inherited error's id is
  `ContributedMembers.KeyOf(<operationId>, <errorId>)`. `ContributedMembers.CheckLinks` findings map onto MQ2027 and
  MQ2028, worded for any holder. `name` and `code` are not redefinable, so a shared code stays shared; a different code
  is an own error beside an exclusion. Resolved order: own errors in file order, then inherited ones in effective-set
  order, each stereotype's in its file order. Every resolved error carries section 3.1's `RMemberOrigin` (`own`,
  `stereotype` or `package-default`; a schema default never reaches an operation).
- **Package defaults reach operations.** Section 3.1's package defaults reach every kind with a package. An operation's
  `StereotypeHolder.Package` is its file's package, else its subject's (`Model/StereotypeDefaults.cs`), the same
  fallback as its resolved package. `operation.json` takes `excludeStereotypes` like every kind schema with
  `stereotypes`.
- **Stereotype errors resolve on the definition too.** Unlike table members, which resolve only on a carrying table
  (section 3.1), an error needs no carrier to be complete, so `RStereotypeDefinition.Errors` lists them and a pack can
  write one set of constants per stereotype.
- **Removing a contributed error** from a stereotype (a save) removes the `redefines` and `excludes` entries that name
  it, as section 3.1 does for table members; as for a table's inherited column, an optional `x-ref` value naming the
  keyed id is cleared in the same save and a required one is reported by MQ2001, which refuses the save until the user
  removes it. Deleting the
  stereotype refuses while operations carry it, and `remove-references` clears those entries with the `stereotypes`
  entry.
- **`fires`** lists process event ids (`[ElementRef(IndexKinds = ["event"])]`) of any process, and every one stays an id
  when events become a kind. A `read` operation that fires an event is warning MQ3030: an event changes state.
  `REvent.FiredBy` comes from the index's references to the event, never from a scan of every operation.
- **`requires`** lists ids of any element or sub-element, untyped like a diagram member, so a custom element (section
  4.2), a code of a reference type (`row`) or an actor can stand for the project's authorization concept. The engine
  gives the list no meaning. MQ3031 refuses the operation itself, its own sub-elements, a diagram and a migration fact.
- **No guard** until the expression core (phase 3). No text field stands in for one meanwhile.
- **What a project says about its commands** (approval, anonymous access, caching, rate limits, a version) is said
  with stereotypes whose typed parameters apply to operations; the engine reserves none of those words.
- **Membership.** `package` (`IPackaged`); the resolved package falls back to the subject's, as a scenario takes its
  process's. Layering (MQ2020) judges `subject`, `keyOf`, `entity`, `fires` and `requires` like every reference.
- **Never a database change.** No database resolves an operation, so `Shapes` (`ModelStore.Migrations.cs:343`) returns
  false for an operation before and after, as it does for a migration fact. An operation write is never classified and
  chooses no database, even when `requires` names a table and so puts the operation in that database's reach.
- **`x-ref` kinds.** `operation` becomes an element kind, so an `of` beside it is MQ2009 as for every element kind;
  `operation-member`, `outcome` and `error` join `ModelIndexer.SubElementKinds`; `XRef.ReservedSubElementKinds` keeps
  only `member`. A value naming an inherited error is its keyed id, judged on its last id as phase 1 decided. A keyed
  value whose operation does not inherit that error is MQ2001. `of: "$element"` on an operation or its parts reaches
  its own sub-elements and its inherited errors by keyed id; a bare stereotype error id is out of scope (MQ2011), as
  section 3.1 decides for tables.
- **Properties and stereotypes** apply to `operation`, `operation-member`, `outcome` and `error` (`appliesTo` in
  `extension.json` and `stereotype.json`), and `PropertyHost.Of` visits the operation, its members (input and returns),
  outcomes and errors, and a stereotype's errors.
- **Localization.** The display name and description of an operation, and of each member, outcome and error, are
  localizable. The shard is the operation file's own package, else the root. The subject's package is not consulted:
  moving the entity would otherwise strand the operation's entries, since a write moves entries only for the elements
  it changes. An inherited error's texts come from the stereotype error's entries unless its redefinition states them.
- **Templates.** Scope `each operation`, unit variable `operation`. `where` takes packages, tags, stereotypes,
  categories and a script; `database` and `abstract` are refused at pack load, as for processes. `type_of` accepts an
  operation member.
- **Index rows.** Operation rows carry `subject` (the key processes use) and a new `effect`; the index format becomes
  `maquettiste-index/e10`.
- **The example pack.** csharp-dapper gains two units, `operation-contracts` and `operation-endpoints`, as justified in
  "The example pack" below.

#### Schema and files

`operation.json` (new kind schema). `x-order`: `$schema`, `kind`, `id`, `name`, `displayName`, `pluralName`, `package`,
`subject`, `level`, `effect`, `input`, `outcomes`, `defaultOutcome`, `errors`, `redefines`, `excludes`, `fires`,
`requires`, `description`, `rationale`, `decision`, `status`, `stereotypes`, `excludeStereotypes`, `tags`, `category`,
`properties`, `generation`, `source`.

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| `subject` | `common.json#/$defs/id` | absent | An entity; not owning |
| `level` | `record` or `collection` | `record` | Means something only with a subject (MQ3023) |
| `effect` | `read` or `change` | required | |
| `input` | array of `#/$defs/member` | `[]` | Input members |
| `outcomes` | array of `#/$defs/outcome`, `minItems: 1` | required | |
| `defaultOutcome` | `common.json#/$defs/id` | absent | One of its own outcomes (MQ3028); absent: the first |
| `errors` | array of `common.json#/$defs/operationError` | `[]` | Own errors |
| `redefines` | array of `#/$defs/errorRedefinition`, `x-sort` `member` | `[]` | Section 3.1's entry shape with error facets |
| `excludes` | `common.json#/$defs/idList`, `x-sort` `.`, `uniqueItems` | `[]` | Inherited errors dropped, by the stereotype's error id |
| `fires` | `common.json#/$defs/idList` | `[]` | Process event ids |
| `requires` | `common.json#/$defs/idList` | `[]` | Any element or sub-element id |
| `excludeStereotypes` | `common.json#/$defs/keyList` | `[]` | Section 3.1's exclusion of default stereotypes |

`#/$defs/member` requires `id` and `name`. `x-order`: `id`, `name`, `displayName`, `type`, `keyOf`, `file`,
`mediaTypes`, `length`, `precision`, `scale`, `required`, `default`, `collection`, `sensitive`, `validation`,
`description`, `rationale`, `decision`, `status`, `stereotypes`, `tags`, `category`, `properties`, `generation`,
`source`. `type` is `common.json#/$defs/typeRef`; `keyOf` an id; `file` a boolean (default `false`); `mediaTypes` a
unique array of non-empty strings (default `[]`); the other keys take their `common.json` definitions as the attribute
does.
Every `ElementBase` sub-element schema lists `pluralName` after `displayName`, so the member, outcome and error
definitions do too.

`#/$defs/outcome` requires `id` and `name`. `x-order`: `id`, `name`, `displayName`, `completes`, `returns`,
`description`, `rationale`, `decision`, `status`, `stereotypes`, `tags`, `category`, `properties`, `generation`,
`source`. `completes` is `now` (default) or `later`.

`#/$defs/returns` has no id; it is part of its outcome. `x-order`: `members`, `entity`, `file`, `mediaTypes`, `list`,
`paging`, `maxPageSize`. `members` is an array of `#/$defs/member` with `minItems: 1` and no default; `entity` an id;
`file` and `list` booleans (default `false`); `mediaTypes` as on a member; `paging` is `page` or `cursor`;
`maxPageSize` an integer of at least 1.

`common.json#/$defs/operationError` (shared with `stereotype.json`) requires `id` and `name`. `x-order`: `id`, `name`,
`displayName`, `code`, `input`, `description`, `rationale`, `decision`, `status`, `stereotypes`, `tags`, `category`,
`properties`, `generation`, `source`. `code` is a string of 1 to 128 characters matching
`^[A-Za-z0-9][A-Za-z0-9_.:-]*$`.

`#/$defs/errorRedefinition` requires `member`, has `minProperties: 2` and `additionalProperties: false`. `x-order`:
`member`, `input`, `displayName`, `description`. It has no id of its own (section 3.1).

Section 4.3 adds `satisfies` to `ElementBase`, so operations, members, outcomes and errors carry it. 4.3's E40 runs
after E5 and writes the key into `operation.json`, its `member` and `outcome` definitions and
`common.json#/$defs/operationError`, at the place 4.3 gives it in every kind schema.

`stereotype.json` gains `errors` (array of `common.json#/$defs/operationError`, default `[]`), after the last of its
member keys in `x-order` (`indexes`, the last key section 3.1 adds) and before `icon`. `stereotype.json`
`appliesTo` and `extension.json` `appliesTo.kinds` gain `operation`, `operation-member`, `outcome` and `error`.
`pack.json`'s `for` pattern gains `operation`. Every changed file gets its copy, and `editor` regenerates the editor's
types and mocks (`src/editor/src/api/schema.d.ts`, `src/editor/src/mocks/openapi.json`,
`src/editor/src/code/extensionSchema.json`).

`send` on Invoice in the billing fixture, as the acceptance test writes it
(`model/operations/invoice/send.json`; the event id is the invented lifecycle's):

```json
{
  "$schema": "../../../.schema/v1/operation.json",
  "kind": "operation",
  "id": "01JR5ND000000000000000000A",
  "name": "send",
  "package": "01J92P0V01KDRN8GX5PGYCNKSX",
  "subject": "01J92P0V0FJ23CGSNKM7P1W5V7",
  "effect": "change",
  "input": [
    { "id": "01JR5ND000000000000000000B", "name": "recipient", "type": "string", "length": 254, "required": true }
  ],
  "outcomes": [
    { "id": "01JR5ND000000000000000000C", "name": "applied", "returns": { "entity": "01J92P0V0FJ23CGSNKM7P1W5V7" } },
    {
      "id": "01JR5ND000000000000000000D",
      "name": "waiting",
      "completes": "later",
      "returns": { "members": [ { "id": "01JR5ND000000000000000000E", "name": "trackingId", "type": "ulid" } ] }
    }
  ],
  "errors": [
    { "id": "01JR5ND000000000000000000F", "name": "already_sent" },
    { "id": "01JR5ND000000000000000000G", "name": "missing_recipient", "input": "01JR5ND000000000000000000B" }
  ],
  "fires": ["01JR5ND000000000000000000H"]
}
```

#### Model records and the public API

```csharp
namespace Maquettiste.Engine.Model;
public enum ElementKind { /* existing */ [JsonStringEnumMemberName("operation")] Operation }   // last
public sealed record Operation : Element, IPackaged
{
    [ElementRef(ElementKind.Package)] public string? Package { get; init; }
    [ElementRef(ElementKind.Entity)] public string? Subject { get; init; }
    public OperationLevel Level { get; init; } = OperationLevel.Record;
    public required OperationEffect Effect { get; init; }
    public IReadOnlyList<OperationMember> Input { get; init; } = [];
    public required IReadOnlyList<OperationOutcome> Outcomes { get; init; }
    [ElementRef(IndexKinds = ["outcome"])] public string? DefaultOutcome { get; init; }
    public IReadOnlyList<OperationError> Errors { get; init; } = [];
    public IReadOnlyList<OperationErrorRedefinition> Redefines { get; init; } = [];      // E5
    [ElementRef(IndexKinds = ["error"])] public IReadOnlyList<string> Excludes { get; init; } = [];   // E5
    [ElementRef(IndexKinds = ["event"])] public IReadOnlyList<string> Fires { get; init; } = [];
    [ElementRef] public IReadOnlyList<string> Requires { get; init; } = [];
}
// Each enum value takes [JsonStringEnumMemberName] with its lower-case JSON name, as ProcessUse does.
public enum OperationLevel { Record, Collection }           // "record", "collection"
public enum OperationEffect { Read, Change }                // "read", "change"
public sealed record OperationMember : ElementBase
{   public TypeRef? Type { get; init; }
    [ElementRef(ElementKind.Entity)] public string? KeyOf { get; init; }
    public bool File { get; init; }
    public IReadOnlyList<string> MediaTypes { get; init; } = [];
    public int? Length { get; init; }  public int? Precision { get; init; }  public int? Scale { get; init; }
    public bool Required { get; init; }  public JsonElement? Default { get; init; }
    public bool Collection { get; init; }
    public Sensitivity? Sensitive { get; init; }  public AttributeValidation? Validation { get; init; } }
public sealed record OperationOutcome : ElementBase
{   public OperationCompletion Completes { get; init; } = OperationCompletion.Now;
    public OperationReturns? Returns { get; init; } }
public enum OperationCompletion { Now, Later }              // "now", "later"
public sealed record OperationReturns
{   public IReadOnlyList<OperationMember>? Members { get; init; }      // null: not this form
    [ElementRef(ElementKind.Entity)] public string? Entity { get; init; }
    public bool File { get; init; }  public IReadOnlyList<string> MediaTypes { get; init; } = [];
    public bool List { get; init; }  public OperationPaging? Paging { get; init; }
    public int? MaxPageSize { get; init; } }
public enum OperationPaging { Page, Cursor }                // "page", "cursor"
public sealed record OperationError : ElementBase
{   public string? Code { get; init; }
    [ElementRef(IndexKinds = ["operation-member"])] public string? Input { get; init; } }
public sealed record OperationErrorRedefinition : IMemberRedefinition                           // E5
{   [ElementRef(IndexKinds = ["error"])] public required string Member { get; init; }
    [ElementRef(IndexKinds = ["operation-member"])] public string? Input { get; init; }
    public string? DisplayName { get; init; }  public Description? Description { get; init; }
    [JsonIgnore] public IReadOnlyList<string> Facets { get; } }  // the non-null facets' JSON names, ordinal
// E5: a stereotype's contributed errors.
public sealed record Stereotype { /* existing */ public IReadOnlyList<OperationError> Errors { get; init; } = []; }
public sealed record ElementSummary(/* existing, then */ string? Effect = null);  // JsonIgnore when null, as the others
```

All new members carry XML documentation. `ElementSummary.Equals` compares `Effect` (`Model/Documents.cs:88`).
`ModelIndexer.SubElementKind` maps `OperationMember` to `operation-member`, `OperationOutcome` to `outcome` and
`OperationError` to `error`, wherever the type appears, so a stereotype's errors are indexed under the stereotype.
Keyed ids of inherited errors are not index entries; references to them split into their ids, as phase 1 decided for
keyed values.

`ModelValidator.SubElementIds` (`Validation/ModelValidator.cs:657`) adds an operation's members (input and returns),
outcomes and errors, and a stereotype's errors, so a save that drops one validates the files that name it.
`ModelPaths.ConventionalFolder` and `MatchesConvention` add the operation case (a subject folder suffixed on a
collision, as a seed's target folder is), and `ModelLoader` computes subject stems when an operation is loaded
(`Loading/ModelLoader.cs:559`). `ChangePlanner.TargetStemOf` already serves any id (`Loading/ChangePlanner.cs:1106`).

#### Resolution and the resolved model

```csharp
namespace Maquettiste.Engine.Resolution;
public sealed class ResolvedModel
{   /// <summary>Operations, by (package qualified name, subject name with none first, name, id).</summary>
    public RList<ROperation> Operations { get; internal init; } }
public sealed class ROperation : RElement                                  // Kind "operation"
{   public REntity? Subject { get; internal set; }
    public string? Level { get; internal set; }          // "record" or "collection"; null without a subject
    public string Effect { get; internal set; }          // "read" or "change"
    public RList<ROperationMember> Input { get; internal set; }
    public RList<ROperationOutcome> Outcomes { get; internal set; }
    public ROperationOutcome? DefaultOutcome { get; internal set; }   // the named one, else the first
    public RList<ROperationError> Errors { get; internal set; }       // own, then inherited (E5)
    public RList<REvent> Fires { get; internal set; }                 // file order
    public RList<IResolvedObject> Requires { get; internal set; } }   // file order; ids that resolve to nothing skipped
public sealed class ROperationMember : RElement                            // Kind "operation-member"
{   public ROperation Operation { get; internal set; }
    public ROperationOutcome? Outcome { get; internal set; }          // set on a member of a returns
    public RType? Type { get; internal set; }    // a typed value's type, or the single key attribute's type of KeyOf
    public REntity? KeyOf { get; internal set; }
    public bool IsFile { get; internal set; }
    public IReadOnlyList<string> MediaTypes { get; internal set; }
    public bool Required { get; internal set; }  public object? Default { get; internal set; }
    public bool Collection { get; internal set; }  public int? Length { get; internal set; }
    public int? Precision { get; internal set; }  public int? Scale { get; internal set; }
    public string? Sensitive { get; internal set; }  public RValidation? Validation { get; internal set; }
    public int Order { get; internal set; } }
public sealed class ROperationOutcome : RElement                           // Kind "outcome"
{   public ROperation Operation { get; internal set; }
    public bool IsDefault { get; internal set; }
    public string Completes { get; internal set; }                    // "now" or "later"
    public ROperationReturns? Returns { get; internal set; } }        // null: returns nothing
public sealed class ROperationReturns
{   public RList<ROperationMember> Members { get; internal set; }     // empty unless the members form
    public REntity? Entity { get; internal set; }
    public bool IsFile { get; internal set; }  public IReadOnlyList<string> MediaTypes { get; internal set; }
    public bool IsList { get; internal set; }  public string? Paging { get; internal set; }  // "page", "cursor"
    public int? MaxPageSize { get; internal set; } }
public sealed class ROperationError : RElement                             // Kind "error"
{   public ROperation? Operation { get; internal set; }               // null in RStereotypeDefinition.Errors
    public string Code { get; internal set; }                         // code, else the name
    public ROperationMember? Input { get; internal set; }
    public RMemberOrigin MemberOrigin { get; internal set; } }        // section 3.1's; own errors share one instance
public sealed class REntity { public RList<ROperation> Operations { get; internal set; } }  // by Operations order
public sealed class REvent { public RList<ROperation> FiredBy { get; internal set; } }      // by Operations order
public sealed class RStereotypeDefinition { public RList<ROperationError> Errors { get; internal set; } }  // E5
```

Engine behaviour:

1. **Pass.** `ResolveRun.Operations.cs` (new) runs after `ResolveProcesses` (`Resolution/ResolveRun.cs:148`), since
   `Fires` and `FiredBy` need the resolved events. `ResolveRun` sets `Operations` with the membership key `k:operation`.
2. **Requires last.** `Requires` is linked after every pass has registered its objects, after `ResolveDefinitions`
   (`Resolution/ResolveRun.cs:149`) and the passes sections 4.2 and 4.3 add, just before `ResolvedModel` is built, so
   an id of a stereotype or a custom element resolves whatever the pass order.
3. **Annotations.** Each operation and sub-element takes the common fill (display name, description, rationale,
   effective stereotypes with section 3.1's defaults, properties merged with stereotype defaults) that other kinds use,
   so stereotype parameters on an operation reach templates as properties.
4. **Members.** A typed member's `Type` comes from the resolver's type resolution, as an attribute's does. A `keyOf`
   member's `Type` is the key attribute's type when the entity's key has one attribute, else null.
5. **Package.** The file's package, else the subject's.
6. **Dependency keys.** Every operation object lists the operation file's `e:` key. The operation adds its subject's
   `e:` key (its order and package fallback read it); a `keyOf` member adds that entity's; an outcome adds its
   `returns.entity`'s; an inherited error adds its stereotype's, and the fill adds section 3.1's package keys for
   defaults.
7. **Derived lists.** `REntity.Operations` holds the operations whose `subject` is the entity, from
   `ModelSnapshot.ReferencesTo`; its membership keys are `r:<entity id>` and `k:operation`, as `REntity.Seeds` uses
   (`Resolution/ResolveRun.ReferenceData.cs:256`). `REvent.FiredBy` holds the operations whose `fires` names the event;
   its membership keys are `r:<event id>` and `k:operation`, since a reference to the event changes the event's key
   and not the process's.
8. **Lookups.** Every operation, member, outcome and error is registered by id, inherited errors by keyed id, so
   `ResolvedModel.Find` reaches them.
9. **Coverage.** `ResolvedCoverageTests` gains the targets operation, operation member, outcome, outcome returns and
   error (E3; `file` and `list` are renamed to `IsFile` and `IsList`), and stereotype error (E5). E5 adds `redefines`
   and `excludes` to the schema and excuses both as `NotResolved`, with the reason "applied to `Errors`
   (`MemberOrigin`)".

#### Rules

`Validation/OperationRules.cs` (new) runs from `ModelValidator` beside `ProcessRules` (`:173`) for operation and
stereotype documents. It also reports, on an operation's sub-elements: MQ3001 and MQ3018 (names, as
`ProcessRules.CheckNodeNames` does, `Validation/ProcessRules.cs:75`); MQ3013, MQ3019, MQ3017 and MQ2007 (member
facets, defaults and validation rule ids through `AttributeRules.Resolve`, `CheckFacets`, `CheckRuleIds` and the default
check, extracted from `AttributeRules.Check` so it takes a member's facets, `Validation/AttributeRules.cs:42`, `:60`,
`:98`, `:121`); and the stereotype and tag rules of every sub-element (MQ2003, MQ2004, MQ2006, MQ2008, MQ2013 to MQ2015)
through `BuiltinRules.CheckCommon` (`Validation/BuiltinRules.cs:331`), with the kind names `operation-member`,
`outcome` and `error`. `ValidationContext.NameKeys` (`Validation/ValidationContext.cs:313`) adds the operation's key:
`operation|s:<subject>|<name>` or `operation|p:<package>|<name>`.

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ3023 | W | `level` is `collection` on an operation without a `subject`; it is ignored. | Set a subject or remove `level` |
| MQ3024 | E | An operation member is not exactly one of a typed value (`type`), a record key (`keyOf`) or a file (`file`); `mediaTypes` sits on a member that is not a file; a record key or a file sets `default`, `precision`, `scale` or `validation`, or a record key sets `length`; or a member of a `returns` sets `default`. | Keep one form |
| MQ3025 | E | An outcome's `returns` is not exactly one of `members`, `entity` or `file`; `mediaTypes` sits beside no `file`; `list` sits beside `file`; `paging` is set without `list`; or `paging` and `maxPageSize` are not set together. | Fix the returns |
| MQ3026 | E | Two errors of an operation, own and inherited counted, share a name or a code, or two stereotypes give it an error of one id (the first stays). | Rename one, or exclude the inherited one; give a repeated id a new one |
| MQ3027 | E | An error's `input`, or a redefinition's `input`, is not an input member of the operation that holds it; a stereotype's error has none. | Pick an input member |
| MQ3028 | E | `defaultOutcome` names an outcome of another operation. | Pick one of its own outcomes |
| MQ3029 | E | A stereotype declares `errors` but does not apply to operations. | Add `operation` to `appliesTo` |
| MQ3030 | W | A `read` operation fires a process event; an event changes state. | Set `effect` to `change`, or drop the event |
| MQ3031 | E | `requires` names the operation itself, one of its own sub-elements, a diagram or a migration fact. | Remove the entry |

MQ3032 stays free. Wrong kinds (`subject` naming an enum, `fires` naming a state) are MQ2002, dangling ids MQ2001,
references across packages MQ2020, all as today. Section 3.1's MQ2027 and MQ2028 judge `redefines` and `excludes`
entries through `ContributedMembers.CheckLinks`. MQ3026 on an inherited error is placed as section 3.1 places findings
about an inherited member: at its `redefines` entry when it has one, else at the contributing stereotype's entry in
`stereotypes`, else at `/stereotypes` (a package default).

#### CLI and MCP

No verb and no tool is added; the pinned tool counts stay. Element tools (`create_element`, `save_element`,
`delete_element`, `get_element`, `get_elements`, `get_schema`, `apply_batch`, `get_references`) and the CLI's `model`
verbs take the kind through `KindInfo` and need no change. Changed:

- `get_resolved_model` and `maquettiste model export --resolved` gain the scope `operations`: one `OperationRecord` per
  operation, with `input` (`OperationMemberRecord`), `outcomes` (`OperationOutcomeRecord` with `isDefault`, `completes`
  and `returns` as an `OperationReturnsRecord`), `errors` (`OperationErrorRecord` with `code`, `input` and
  `memberOrigin` as section 3.1's `MemberOriginView`, null for own errors), `fires` and `requires` by id, and the
  annotations. `all` includes it. `Editor/ResolvedRecords.cs` builds it; `tools.json`, `openapi.yaml` (the scope enum
  and the records) and `docs/mcp.md` say so.
- `get_model_index` rows of operations carry `subject` and `effect` (`tools.json` text, `ElementSummary` in
  `openapi.yaml`).
- `get_template_context` answers for an `each operation` unit with the `ROperation` members
  (`GenerationService.Authoring.cs:432-455` gains the scope).

#### Templates

- `UnitRules.Scopes`, `pack.json`'s pattern and `UnitPlanner.Candidates` (`Planning/UnitPlanner.cs:355-378`) gain
  `each operation` over `model.Operations`. `PackLoader` (`Planning/PackLoader.cs:218`) refuses `database` and
  `abstract` in its `where`.
- `BuiltinHelpers.Variables` and the alias list in `RenderRun.CreateGlobals` gain `operation`, so MQ6013 also refuses a
  pack helper named `operation`. No example pack has one.
- `type_of` (`Rendering/TypeOf.cs`) accepts an `ROperationMember`: a typed member maps as an attribute does, wrapped by
  the `collection` pattern, or by `nullable` when not required; a `keyOf` member maps through its single key
  attribute's type, and a composite key is MQ6006 naming `member.key_of.key`; a file member takes the pack map's `file`
  entry, and MQ6006 when the map has none or the target is a SQL dialect.
- The localization helpers (`Rendering/LocalizationHelpers.cs:84-132`) read an inherited error's display name and
  description through the stereotype error's id (`MemberOrigin.MemberId`), so its translations apply; a facet the
  redefinition states shows its own text in every locale.

#### The example pack

**Choice: two units in csharp-dapper, `operation-contracts` and `operation-endpoints`, and nothing else.** The pack
already maps attribute types to C# through `types/csharp.json`, already names namespaces and folders by package
(`ns`, `package_folder`), and already splits process output into plain contracts and HTTP endpoints written only when
`endpointsFolder` names a folder. Operations follow the same split, so the change is two templates, two units, one
type-map entry and no new parameter. A new pack would repeat all of that; one unit would force the web framework on
every project that models operations without serving HTTP.

- **`operation-contracts`** (`each operation`, mode overwrite) writes
  `<generatedFolder>/Operations/<package folder>/<OpType>.g.cs`, where `OpType` is the Pascal name followed by the
  subject's (`SendInvoice`), or the Pascal name alone without a subject. It holds:
  - `<OpType>Request`, a record of the input members (absent without input);
  - one record per outcome whose `returns` has members (`<OpType><Outcome>`); an `entity` returns uses the entity's
    class; a list is `IReadOnlyList<T>`, and a paged list a record with `Items` and `Next` (cursor) or `Page`;
  - `<OpType>Errors`, one constant per error code, own and inherited;
  - `<OpType>Result`, one nested record per outcome and `Failed(string Code, string? Input)`;
  - `I<OpType>Handler`, whose `HandleAsync` takes the subject's key (record level), the request, the paging inputs a
    paged list implies, and a cancellation token. Its documentation lists the events the operation fires.
- **`operation-endpoints`** (`each operation`, no `output` and no regions; the template writes through `file` only when
  `endpointsFolder` is set, as `process-endpoints` does) writes
  `<endpointsFolder>/Operations/<package folder>/<OpType>Endpoint.g.cs` with one route that calls the handler from the
  service container. HTTP mapping, the pack's own convention: the route is `/<kebab plural subject>`, then `/{id}` at
  record level, then `/<kebab name>`, or `/operations/<kebab name>` without a subject; GET for a read without input,
  else POST; 200 for an outcome that completes now with a result, 204 without one, 202 for `later`; an error answers
  with problem details (RFC 9457) carrying `code` and `input`, status 422 when it points at an input and 409 otherwise,
  unless the error has an integer property `httpStatus`. A file input is a multipart part; a file result is streamed
  with its first media type.
- `types/csharp.json` gains `"file": "System.IO.Stream"`. The README describes both units and the mapping, and the
  manifest's `description` names them.
- The reference app's copy of the pack (`samples/reference-app/.maquettiste/templates/csharp-dapper/`) takes the same
  files and stays identical. The reference app models no operation, so its output does not change.

For `send` the contracts read, abridged:

```csharp
public sealed record SendInvoiceRequest(string Recipient);
public sealed record SendInvoiceWaiting(string? TrackingId);
public static class SendInvoiceErrors { public const string AlreadySent = "already_sent";
                                        public const string MissingRecipient = "missing_recipient"; }
public abstract record SendInvoiceResult
{   public sealed record Applied(Invoice Value) : SendInvoiceResult;
    public sealed record Waiting(SendInvoiceWaiting Value) : SendInvoiceResult;
    public sealed record Failed(string Code, string? Input) : SendInvoiceResult; }
/// <summary>send on Invoice; fires InvoiceLifecycle.sent.</summary>
public interface ISendInvoiceHandler
{   Task<SendInvoiceResult> HandleAsync(Guid id, SendInvoiceRequest request, CancellationToken cancellationToken); }
```

#### Editor follow-ups

E1 keeps the editor compiling: the kind's icon and label, and an "Operations" group in the stereotype form's
`appliesTo` choices. E11 makes an operation open in the editor with its General fields and the shared JSON tab
(`src/editor/src/inspector/Inspector.tsx:352`), instead of the type editor the tab switch falls back to
(`src/editor/src/editors/EditorTabs.tsx:386`), lists operations in the explorer under their package, and offers
`each operation` in the unit grid's scopes. Deferred, each a follow-up:

- An operation screen: subject, level and effect; an input grid; outcomes with their `returns`; errors, inherited ones
  read-only with a link to their stereotype and "Redefine here"; pickers for `fires` and `requires`.
- An Operations tab on the entity editor (`entity.operations`), and "Fired by" on a process event.
- A "New operation…" dialog with a subject picker.

#### Docs

- `docs/user-guide.md`: new section "Operations" (the file shape, outcomes and returns, errors and shared errors, the
  rules MQ3023 to MQ3031, the template scope), after "Processes, actors and scenarios".
- `docs/mcp.md`: the `get_resolved_model` and `get_model_index` rows.
- `skills/maquettiste-modeling/SKILL.md`: an `operations/<subject>/` row in "Kinds and folders" and a short
  "Operations" section.
- `packs/csharp-dapper/README.md`: the two units and the HTTP mapping.
- `docs/engineering/spec-errata.md`: the next free erratum (E46 on 2026-11-01) for SPEC section 10's operations (no
  implied CRUD, no event kind, no guard, shared errors from stereotypes).
- `docs/engineering/engine-design.md`: `each operation` in the scope list.
- `docs/releases.md`: a paragraph under `## 0.12.0 (unreleased)`.

**Not doing**

- A guard on operations: it arrives with the expression core (phase 3); no text field stands in for it.
- Implied CRUD operations per entity and a domain-event kind: packs already generate CRUD from bindings; both are
  revisited with charts joined to the model (phase 4).
- Error categories, HTTP verbs, routes or status codes in the engine, and the words approval, anonymous, caching or rate
  limit: projects state them as stereotypes and properties, and packs map them.
- Projections as input or result: `input` stays inline and `returns.entity` covers a whole record until projections
  (phase 3).
- Streaming as its own concept: it is a file result.
- A list of actors allowed to call an operation: `requires` names actors, roles as custom elements or permission codes.
- Promoting a process action to an operation: phase 4, which keeps the action's name.
- Folding own errors into a stereotype as an authoring action: no repeated need yet; section 3.1's fold for tables is
  the model to copy when one appears.
- Translations of a redefinition's own texts: locale entries are keyed by ids, and a redefinition has none. A redefined
  display name or description shows in every locale; a team that needs it translated declares an own error and
  excludes the inherited one.
- Nested input shapes beyond value objects: a member typed by a value object carries the nesting.
- An editor screen in this phase (see "Editor follow-ups").

**Tests**

- `Operations/OperationModelTests` (new): `An_operation_round_trips_canonically_with_every_key`,
  `With_a_subject_it_lives_in_the_subjects_folder_and_without_one_in_model_operations`,
  `Changing_the_subject_moves_the_file`, `Members_outcomes_and_errors_are_indexed_by_their_kinds`,
  `The_index_row_carries_subject_and_effect`,
  `Batch_names_reach_an_operation_through_its_subject_and_its_parts_through_it`,
  `A_package_default_stereotype_reaches_an_operation_through_its_subjects_package`.
- `CatalogTests.Kind_info_covers_every_element_kind_in_order` (its `operation` assertion inverted);
  `ProcessModelTests` (the index format pin reads `e10`).
- `XrefRuleTests.Operation_kinds_resolve_and_only_member_stays_reserved` (replaces
  `Reserved_kinds_are_valid_and_dangle`); `PropertySchemaSetTests.Operation_hosts_take_their_kind_names`.
- `ModelIndexerPatchTests`: the random-edit test also edits operation files.
- Cli `ModelCommandTests.Stats_and_export_list_operations_by_package`;
  `McpReadTests.Get_model_index_lists_an_operation_row`.
- `Operations/OperationRuleTests` (new): one failing and one passing case per rule from MQ3023 to MQ3031, and
  `Archive_on_two_subjects_is_clean_and_twice_on_one_subject_is_MQ3001`, `Sub_element_names_are_MQ3001_and_MQ3018`,
  `Member_facets_defaults_and_rule_ids_are_MQ3013_MQ3019_and_MQ2007`,
  `Stereotypes_on_sub_elements_are_MQ2003_and_MQ2004`.
- `Operations/OperationResolutionTests` (new): `Send_resolves_subject_level_effect_input_outcomes_errors_and_fires`,
  `The_default_outcome_is_the_named_one_else_the_first`, `A_key_member_takes_its_entitys_key_type`,
  `Invoice_lists_its_operations_and_an_event_lists_what_fires_it`, `Without_a_package_it_takes_its_subjects`,
  `Operations_sort_by_package_subject_name_and_id`, `Requires_resolves_ids_of_any_kind_in_file_order`,
  `Requires_reaches_a_stereotype_resolved_after_the_operations_pass`.
- `ResolvedCoverageTests`: the new targets.
- `UnitPlannerTests.Each_operation_renders_once_per_operation_and_filters_by_package_and_stereotype`;
  `PackLoaderTests.An_each_operation_unit_refuses_database_and_abstract_filters`;
  `TemplateCacheAndHelperTests.Type_of_maps_typed_key_and_file_members`;
  `PackAuthoringMoreTests.Template_context_of_an_each_operation_unit_lists_the_operation_members`;
  `ReadTrackingTests.Adding_an_operation_that_fires_an_event_re_renders_units_that_read_fired_by`.
- `Operations/OperationInheritanceTests` (new): `A_stereotype_contributes_errors_with_keyed_ids`,
  `Excludes_drops_one_and_a_redefinition_states_only_its_facets`,
  `Redefines_and_excludes_entries_that_name_no_inherited_error_are_MQ2027_and_MQ2028`,
  `A_redefinition_input_naming_another_operations_member_is_MQ3027`,
  `An_inherited_and_an_own_error_sharing_a_code_are_MQ3026_at_the_stereotype_entry`,
  `A_contributed_errors_code_change_edits_no_operation_file`,
  `Removing_a_contributed_error_drops_the_entries_that_name_it`,
  `A_property_xref_to_an_inherited_error_takes_the_keyed_id_and_one_not_inherited_is_MQ2001`.
- `FactRecordingTests.An_operation_edit_is_never_classified_even_when_requires_names_a_table`.
- `DeleteCascadeTests`:
  `Deleting_the_lifecycle_whose_event_an_operation_fires_is_refused_and_remove_references_clears_the_entry`,
  `Deleting_a_subject_entity_is_refused_and_both_resolutions_clear_subject`.
- `LocalizationTests.Operation_members_outcomes_and_errors_are_localizable_in_the_operations_package_shard`;
  `LocalizationHelperTests.An_inherited_error_takes_its_stereotype_errors_translation_unless_redefined`.
- `ResolvedRecordsTests.Operations_scope_lists_send_with_members_outcomes_errors_and_fires`;
  `McpReadTests.Get_resolved_model_scope_operations_pages_operation_records`.
- **Acceptance (3.6, first line):**
  `Integration/OperationTests.Send_on_invoice_changes_data_has_two_outcomes_two_errors_and_fires_sent` (new). Setup: an
  `E2ERepo` billing copy with an invented lifecycle `InvoiceLifecycle` (`use` lifecycle, subject Invoice, states
  `Draft` and `Sent`, event `sent`, a transition from `Draft` to `Sent` on `sent`), Invoice's `lifecycle` naming it
  (MQ9201), and `send` as written above, its `fires` naming `sent`. Expected: validation reports nothing. The resolved
  `send` has subject Invoice, level `record`, effect `change`; outcomes `applied` (default, completes `now`, returns the
  Invoice entity) and `waiting` (completes `later`, returns `trackingId`); errors `already_sent` (code `already_sent`)
  and `missing_recipient` (input `recipient`); fires `sent`. `Invoice.Operations` is `[send]` and `sent.FiredBy` is
  `[send]`.
- **Acceptance (3.6, ids hold):**
  `Integration/OperationTests.Removing_sent_or_recipient_is_refused_while_send_names_them`. Same setup. Saving the
  lifecycle without `sent` is `invalid`, with MQ2001 on `send` at `/fires/0`. Saving `send` without `recipient` is
  `invalid`, with MQ2001 at `/errors/1/input`. Renaming `recipient` to `to` keeps the model clean.
- **Acceptance (shared errors):** `Integration/OperationTests.Shared_errors_come_from_a_stereotype_by_id`. Setup: the
  same copy plus an invented stereotype `common-errors` (applies to operations) contributing `not_found` and
  `stale_version`; `send` carries it; an invented `void` on Invoice (effect `change`, no own errors) carries it with
  `excludes: [stale_version's id]` and a redefinition of `not_found` stating only `description`. Expected: `send` has
  four errors, the inherited ones with ids `<send>.<not_found>` and `<send>.<stale_version>`; `void` has one,
  `<void>.<not_found>`, whose `MemberOrigin.Redefined` is `[description]`. Changing `not_found`'s code to `missing`
  writes only the stereotype file and changes both resolved codes. An own error `not_found` on `send` is MQ3026.
- **Acceptance (first cut, the rest):**
  `Integration/OperationTests.Search_takes_keys_returns_a_list_requires_an_actor_and_each_operation_renders_both`.
  Same setup plus an actor `Clerk` raising `sent`, and `search` on Invoice (level `collection`, effect `read`, input
  `customers` as `keyOf` Customer with `collection`, outcome `found` returning a list of Invoice, error `too_broad` with
  code `search.too_broad`, `requires` naming `Clerk`). Expected: validation reports nothing, the resolved fields match,
  and an `each operation` unit writes one file for `search` and one for `send`.
- **Acceptance (3.6, second line):** Packs
  `OperationPackTests.Csharp_dapper_generates_the_send_endpoint_request_and_response_types_from_the_operation_alone`
  (new). Setup: `PackRepo.Billing()` with `PackRepo.AddSendOperation` (the lifecycle, Invoice's `lifecycle` and `send`
  above) and `endpointsFolder` set. Expected: `src/Generated/Operations/Billing/SendInvoice.g.cs` and
  `<endpointsFolder>/Operations/Billing/SendInvoiceEndpoint.g.cs` match the golden tree
  `tests/fixtures/golden/csharp-dapper/operations/`: the request, the `waiting` record, the error constants, the
  result, the handler, and `POST /invoices/{id}/send` answering 200, 202, 409 for `already_sent` and 422 for
  `missing_recipient`. `OperationPackTests.Without_endpoints_folder_only_the_contracts_are_written`.
  `GoldenTests.Csharp_dapper_over_the_billing_fixture_matches_the_golden_tree` is unchanged, since the shared fixture
  has no operation. `CompileTests.Csharp_dapper_operation_output_compiles_with_the_web_framework` builds the contracts
  and the endpoints with `WriteProject`, which gains an optional framework reference and a second source folder.
- Editor: `src/editor/src/editors/operation/OperationEditor.test.tsx` (new) opens `send`, edits its JSON and saves.

**Tasks**

Milestone D's gate precedes every task here. The DEP lines on D tasks name what this section uses from section 3.1:
D2 (the effective set with defaults and `excludeStereotypes`), D8 (`ContributedMembers`, `RMemberOrigin` and the
member link rules, after D6 and D7) and D10 (removing a contributed member's entries).

- **E1. The kind, its records, schema, index and folders.** Runs after D2. Owns: `Model/ElementKind.cs`,
  `Model/KindInfo.cs`, `Model/Operations.cs` (new; every record above except `Redefines`, `Excludes`,
  `OperationErrorRedefinition` and `Stereotype.Errors`), `Model/ElementBase.cs` (`[JsonDerivedType]`),
  `Model/ModelIndexer.cs` (`SubElementKind`, `SubElementKinds`, the summary), `Model/Documents.cs`
  (`ElementSummary.Effect` and `Equals`), `Model/PropertySchemas.cs` (`XRef.ReservedSubElementKinds`, the
  class comment, `PropertyHost.Of` for operations), `Model/StereotypeDefaults.cs` (the operation's package fallback),
  `Loading/ModelPaths.cs`, `Loading/ModelLoader.cs` (subject stems), `Loading/NameIndex.cs` (forms and holders),
  `Validation/ModelValidator.cs` (`SubElementIds` only), `Editor/ModelReads.cs` (`IndexFormat` e10), `operation.json`
  (new, without `redefines` and `excludes`), `common.json` (`operationError`), `extension.json` and `stereotype.json`
  (`appliesTo`) with their copies, `openapi.yaml` (`ElementKind`, the element union, `ElementSummary.effect`), the
  regenerated editor types and mocks, `src/editor/src/mocks/model/store.ts` (`INDEX_FORMAT`, the operation folder by
  subject), `src/editor/src/app/icons.tsx` and `src/editor/src/model/labels.ts` (the kind's icon and label),
  `src/editor/src/workspaces/settings/stereotypeKinds.ts` (an "Operations" group), `CatalogTests.cs` (the kind
  assertion), `ProcessModelTests.cs` (the index format pin), `ResolvedCoverageTests` (an `operation` target with no
  type, in `Unresolved` until E3). Tests: `OperationModelTests`, `CatalogTests`, `ProcessModelTests`, `XrefRuleTests`,
  `PropertySchemaSetTests`, `ModelIndexerPatchTests`, `SchemaConsistencyTests`, `ModelCommandTests`, `McpReadTests`
  (index row). Verify:
  `E "~Operations.|~Model.|~Json.|~Loading.|~Store.|~XrefRuleTests|~ProcessModelTests|~ResolvedCoverageTests"`;
  `C "~ModelCommandTests|~McpReadTests"`; `F "~ContractTests"`; `editor`; `copies`.
- **E2. Rules on an operation's own structure.** Runs after E1. Owns: `Validation/OperationRules.cs` (new; MQ3023 to
  MQ3028, MQ3030, MQ3031, and the existing rules on sub-elements), `Validation/ModelValidator.cs` (wiring),
  `Validation/ValidationContext.cs` (`NameKeys`), `Validation/AttributeRules.cs` (the default check extracted),
  `RuleCatalog.cs` (8 rules), `CatalogTests.cs` (+8). Tests: `OperationRuleTests`. Verify:
  `E "~CatalogTests|~OperationRuleTests|~Validation.|~ModelValidatorTests"`; `record`.
- **E3. Resolution.** Runs after E1. Owns: `Resolution/ResolvedOperations.cs` (new),
  `Resolution/ResolveRun.Operations.cs` (new), `Resolution/ResolveRun.cs` (wiring, `Requires` linked last),
  `Resolution/ResolvedCore.cs` (`ResolvedModel.Operations`), `Resolution/ResolvedConceptual.cs` (`REntity.Operations`),
  `Resolution/ResolvedProcesses.cs` (`REvent.FiredBy`), `ResolvedCoverageTests` (the targets; `operation` leaves
  `Unresolved`). Own errors only. Tests: `OperationResolutionTests`, `ResolvedCoverageTests`. Verify:
  `E "~Resolution.|~OperationResolutionTests"`; `P "~GoldenTests"` (unchanged).
- **E4. Templates.** Runs after E3. Owns: `Planning/UnitRules.cs`, `Planning/UnitPlanner.cs`, `Planning/PackLoader.cs`,
  `GenerationService.Authoring.cs` (`ScopeType`), `Rendering/BuiltinHelpers.cs` (`Variables`), `Rendering/RenderRun.cs`
  (the alias), `Rendering/TypeOf.cs`, `pack.json` with its copy, the regenerated editor types and mocks. Tests:
  `UnitPlannerTests`, `PackLoaderTests`, `TemplateCacheAndHelperTests`, `PackAuthoringMoreTests`, `ReadTrackingTests`.
  Verify: `E "~Planning.|~Rendering.|~PackAuthoringMoreTests|~PreviewBoundsTests"`; `P "~GoldenTests"`; `editor`;
  `copies`.
- **E5. Shared errors from stereotypes.** Runs after E2, E4 (both regenerate the editor's bundled contract), D8 and D10.
  Owns: `Model/Vocabularies.cs` (`Stereotype.Errors`), `Model/Operations.cs` (`Redefines`, `Excludes`,
  `OperationErrorRedefinition`), `stereotype.json` (`errors`) and `operation.json` (`redefines`, `excludes`) with their
  copies, the regenerated editor types and mocks, `Model/PropertySchemas.cs` (stereotype error hosts),
  `Loading/NameIndex.cs` (stereotype error forms), `Validation/ModelValidator.cs` (`SubElementIds`: stereotype errors),
  `Loading/ChangePlanner.cs` and `Loading/ChangePlanner.Cascade.cs` (the entries of removed errors and deleted
  stereotypes), `Resolution/ResolveRun.Operations.cs` (inherited errors through `ContributedMembers.Inherit`),
  `Resolution/ResolveRun.cs` and `Resolution/ResolvedVocabularies.cs` (`RStereotypeDefinition.Errors`),
  `Resolution/ResolvedOperations.cs` (`MemberOrigin` on inherited errors), `Validation/OperationRules.cs` (MQ3026 and
  MQ3027 over inherited errors and redefinitions, MQ3029, MQ2027 and MQ2028 through `CheckLinks`),
  `Validation/BuiltinRules.cs` (`CheckXRef`: keyed error values and the `$element` scope of operations),
  `RuleCatalog.cs` (MQ3029), `CatalogTests.cs` (+1), `ResolvedCoverageTests`. `openapi.yaml` needs no text change, since
  it refers to the schema files. Tests: `OperationInheritanceTests`, `ResolvedCoverageTests`. Verify:
  `E "~CatalogTests|~Operations.|~Resolution.|~Json.|~Validation.|~Store.|~DeleteCascadeTests"`; `F "~ContractTests"`;
  `editor`; `copies`; `record`.
- **E6. Facts and deletes.** Runs after E1. Owns: `ModelStore.Migrations.cs` (`Shapes`), `FactRecordingTests.cs` and
  `DeleteCascadeTests.cs` (the cases above). Tests: `FactRecordingTests`, `DeleteCascadeTests`. Verify:
  `E "~FactRecordingTests|~DeleteCascadeTests|~ModelStoreDeleteTests"`.
- **E7. Localization.** Runs after E5. Owns: `Localization/LocalizationIndex.cs` (`ScopeOf` for operations;
  `operation-member`, `outcome` and `error` as sub-nodes, a stereotype's errors included),
  `Localization/LocaleChains.cs` (`SubElementKinds`), `Model/PropertySchemas.cs` (`TranslatedHolderKinds`),
  `Rendering/LocalizationHelpers.cs` (inherited errors read their stereotype error's entries). Tests:
  `LocalizationTests`, `LocalizationHelperTests`. Verify: `E "~Localization."`;
  `C "~L10nAndSeedCommandTests|~McpLocalizationToolTests"`.
- **E8. Agent and API surfaces.** Runs after E5. Owns: `Editor/ResolvedRecords.cs` (`OperationRecord` and its parts,
  scope `operations`), `Agent/tools.json` (`get_resolved_model`, `get_model_index`), `openapi.yaml` (the scope enum, the
  records), the regenerated editor types and mocks, `docs/mcp.md` (both rows). Tests: `ResolvedRecordsTests`,
  `McpReadTests`, `ModelCommandTests` (scope `operations`). Verify: `E "~Editor.|~AgentToolsTests"`;
  `C "~McpSurfaceTests|~McpReadTests|~ModelCommandTests"`; `F "~ContractTests"`; `editor`.
- **E9. Engine acceptance.** Runs after E2, E3 and E5. Owns: `Integration/OperationTests.cs` (new). Tests: the three
  engine acceptance tests. Verify: `E "~OperationTests|~ValidationFixtureTests"`.
- **E10. The example pack.** Runs after E4 and E5. Owns: `packs/csharp-dapper/pack.json`,
  `packs/csharp-dapper/operation-contracts.scriban` (new), `packs/csharp-dapper/operation-endpoints.scriban` (new),
  `packs/csharp-dapper/_operation.scriban` (new; shared names and the HTTP mapping),
  `packs/csharp-dapper/types/csharp.json`, `packs/csharp-dapper/README.md`, the same files under
  `samples/reference-app/.maquettiste/templates/csharp-dapper/`,
  `tests/Maquettiste.Packs.Tests/OperationPackTests.cs` (new), `tests/Maquettiste.Packs.Tests/PackRepo.cs`
  (`AddSendOperation`), `tests/Maquettiste.Packs.Tests/CompileTests.cs` (`WriteProject` takes a framework reference
  and a second source folder), `tests/fixtures/golden/csharp-dapper/operations/` (new). Tests: `OperationPackTests`,
  `CompileTests`. Verify: `MAQUETTISTE_UPDATE_GOLDEN=1 P "~OperationPackTests"` once (intended change: the new golden
  folder `operations`), then `P "~OperationPackTests|~GoldenTests|~CompileTests|~ProcessTests|~PackLayoutTests"`;
  `diff -r packs/csharp-dapper samples/reference-app/.maquettiste/templates/csharp-dapper` prints nothing.
- **E11. Editor: open and list operations.** Runs after E1, E4 and E8 (E8, which follows E5, changes the contract and
  regenerates the editor's types, as this task's `editor` run does). Owns:
  `src/editor/src/editors/operation/OperationEditor.tsx` (new; General and the shared JSON tab),
  `src/editor/src/editors/EditorTabs.tsx` (the `operation` case), `src/editor/src/editors/tabs.ts` (`EDITOR_KINDS`),
  `src/editor/src/model/model.ts` (the operation row's `subject` and `effect`), `src/editor/src/model/labels.ts` (the
  explorer's folder label), `src/editor/src/explorer/tree.ts` (operations under their package),
  `src/editor/src/workspaces/generate/unitsModel.ts` and `src/editor/src/workspaces/generate/previewScope.ts`
  (`each operation`). Tests: `src/editor/src/editors/operation/OperationEditor.test.tsx`. Verify: `editor`.
- **E12. Docs.** Runs after E7, E8 and E10. Owns: `docs/user-guide.md` (section "Operations"),
  `skills/maquettiste-modeling/SKILL.md`, `docs/engineering/spec-errata.md` (the next erratum),
  `docs/engineering/engine-design.md` (the scope list), `docs/releases.md`. Tests: none new. Verify:
  `C "~McpSurfaceTests|~SkillRefreshTests|~AgentSetupTests"`, where the skill hash changes on purpose.

E13 to E19 are free.

Shared files with sections 4.2 and 4.3 put their first tasks after these: 4.2's E20 after E1 (`ElementKind.cs`,
`KindInfo.cs`, `ElementBase.cs`, `ModelPaths.cs`, `ModelLoader.cs`, the two `appliesTo` lists), E21 after E7
(`PropertySchemas.cs`, `ModelIndexer.cs`, `BuiltinRules.cs`), E24 after E8 (`openapi.yaml`, `docs/mcp.md`), E25 after E4
and E8 (`Planning/`, `Rendering/`, `GenerationService.Authoring.cs`, `ResolvedRecords.cs`, `tools.json`); 4.3's E40
after E5 and E6 (`operation.json`, `stereotype.json`, `ModelStore.Migrations.cs`).

### 4.2 Project-defined kinds

**Why every team**

Every project has concepts of its own that are neither entities nor tables: settings with defaults, feature flags, job
types and their triggers, message texts with named parts, role templates and their permissions, event topics. Today a
team keeps them in code or in loose files, where no link is checked and no template reads them. One generic kind lets a
team declare the shape of each such concept once, keep its instances as model elements with ids, packages, tags and
translations, link to them from operations, stereotype parameters and other elements, and generate constants, seeds or
code from them. The product reserves none of these words: a custom kind is whatever the project declares.

**Today**

- The model has 25 fixed kinds, the last being `migration` (`src/Maquettiste.Engine/Model/ElementKind.cs:82`), each with
  a fixed schema file and folder (`Model/KindInfo.cs:18-45`). A project cannot add one.
- The loader reads `maquettiste.json`, `model/**`, `extensions/*.json` and `extensions/rules/*.js` and nothing else
  (`Loading/ModelLoader.cs:645-653`, `Loading/ModelPaths.cs:116-132`). Snapshots already hold every file under
  `extensions/` (`Snapshots/SnapshotArchive.cs:43`). The extension file tools accept `<name>.json` and `rules/<name>.js`
  only (`ModelStore.Extensions.cs:75`).
- `x-ref` parses `member`, `custom` and `custom:<type>` as reserved kinds whose values dangle
  (`Model/PropertySchemas.cs:29`, `:49-50`, `:117-121`).
- Typed property schemas come from extension files and stereotype parameters only (`Model/PropertySchemas.cs:491`,
  `:545-610`). Their holders are the fixed set of `PropertyHost.Of` (`:265`).
- Sub-element kinds are a fixed set (`Model/ModelIndexer.cs:101-105`). Ids are minted only where a built-in schema
  requires one (`Loading/IdMinter.cs:71`). A save widens its validation to the referrers of the ids `SubElementIds`
  lists, a typed switch over the kinds (`Validation/ModelValidator.cs:657-697`).
- A free-form object is written with its keys in ordinal order at every level (`Json/CanonicalJson.cs:189-195`), which
  is how `properties` values are written.
- Unit scopes are a fixed list (`schemas/v1/pack.json:109`, `Planning/UnitRules.cs:19-23`,
  `Planning/UnitPlanner.cs:357-377`), and so are the unit variables (`Rendering/BuiltinHelpers.cs:126-128`). A name
  read is recorded as `n:<kind>:<form>` and hashed from that kind's name index (`Planning/DependencyHasher.cs:196-230`).
- `x-translatable` is allowed on extension files and stereotype parameters only (MQ7212,
  `Model/PropertySchemas.cs:920`).
- A save that changes an element's conventional folder moves its file (`Loading/ChangePlanner.cs:763-764`).
- `maquettiste format` rewrites settings, element files and locale shards, not extension files
  (`ModelStore.Format.cs:121-122`, `src/Maquettiste.Cli/Commands/FormatCommand.cs:48-49`).
- `ModelReads.IndexFormat` changes whenever `ElementSummary` gains a member (`Editor/ModelReads.cs:19-23`), and the
  editor's mock store repeats it (`src/editor/src/mocks/model/store.ts:1280`).
- The editor and the user guide already say "custom type" for a scalar type (`src/editor/src/model/labels.ts:243`,
  `docs/user-guide.md:17`). This section therefore says "custom kind" for a type a project declares.

**Decisions taken here**

1. **One kind, many custom kinds.** `custom` is one more built-in kind. A custom element names its custom kind in
   `type`. Where a kind is written as a filter, a scope or an `x-ref` target, a custom kind is written `custom:<type>`;
   rows carry kind `custom` and a `type` member. No engine switch grows a case per custom kind.
2. **Declarations live in `extensions/kinds/<type>.json`.** The scope sketched `kinds/<type>.json` at the project root.
   The extension folder already holds the project's own schemas and rules; snapshots, hosted projects and the extension
   file tools cover it, so no tool and no content folder is added.
3. **The type key is the file name.** `<type>` is a kebab key (`common.json#/$defs/key`), and the file's `type` equals
   its file stem (MQ1017). Renaming a custom kind is moving its file. Deleting or moving a custom kind file is refused
   while an element of that type exists.
4. **Declared fields sit under `content`.** The engine's keys (`name`, `package`, `status`, `tags` and the rest) and the
   project's keys never meet, and the engine reserves no key inside `content`. `content` is closed: a key the custom
   kind does not declare is MQ3033. A custom kind that wants open data declares an object property for it.
5. **A declaration has the shape of an extension file.** `properties` (name to JSON Schema) and `required`, compiled
   with the dialect of extension files plus `x-member` as a keyword the evaluation ignores.
6. **Canonical form does not read declarations.** `content` is free-form for the canonical writer: keys in ordinal order
   at every level, arrays in written order, exactly like `properties`. Loading and `format` never depend on a custom
   kind file, and editing a custom kind file never rewrites an element file.
7. **Members.** `"x-member": true` on an array whose `items` is an object schema makes each item a member: a sub-element
   with index kind `member` and an `id` the engine owns. The item schema must not declare `id`; the compiled schema adds
   it as a required ULID. A member's name is its `name` value when the item schema declares a string `name`; otherwise
   it has no name form. Members may nest.
8. **Member ids are minted as in phase 1 section 5.1.** When the minting walk reaches `content` of a custom element, it
   walks the custom kind's member tree from the snapshot: content keys in ordinal order, items by index, a member before
   its nested members. The batch "keep its id" check (MQ1014) covers named members. A member without an id in an update
   is new.
9. **Members carry no standard fields.** No tags, stereotypes, properties, status or description of their own. A custom
   kind that needs one declares it.
10. **Links are `x-ref`, unchanged.** `x-ref` in a custom kind's `properties` types content values exactly as in
    extension files (`kind`, `of`, `sameDatabase`, `optional`). Content may name any element kind (4.1's `operation`
    included), `custom:<type>`, and any sub-element kind: `attribute`, `row`, `member`, and the sub-element kinds 4.1
    adds (`operation-member`, `outcome`, `error`). With kind `member`, `of: <id>` names a custom element and the target
    must be one of its members. `of: "$element"` reaches the host's own members. A `custom:<type>` that the project does
    not declare is well formed and matches no element, so a value typed by it is MQ2002; a shared pack or stereotype may
    name custom kinds a project lacks.
11. **References from content.** `ReferenceInfo.Schema` is `custom-kind:<type>`, `Field` is the top-level content key,
    and `FromId` is the innermost member that holds the value, else the custom element. Deletes, `remove-references`,
    `delete-dependents` and saves that remove a member follow the rules of phase 1 section 4.1, checked against the
    custom kind's compiled schema.
12. **One walk.** Custom kind files join `PropertySchemaSet` beside extension files and stereotype parameters, with
    their sites at `/content` of the elements of their type. The index, the validator, deletes, `names-to-ids` and
    localization read them there. A custom kind source never applies to a holder's `properties`. The set's hash covers
    the custom kind files, so a custom kind file change rebuilds the index in full (phase 1 section 4.1, step 4).
13. **Where findings go.** A custom kind file's own findings (MQ1017, MQ1018, MQ2009, MQ7212) are reported on the file
    in a whole-model validation and in the extension file tools' answers. An element's (MQ1019, MQ3033) are reported on
    the element, so its save reports them.
14. **Names.** A custom element's `name` is an identifier, unique among the elements of its type in one package
    (MQ3001). Member names are unique among the items of one array (MQ3001). Batch forms carry the type, because names
    are unique per type: `<type>.<name>` and `<type>.<package path>.<name>` for kind `custom`
    (`@custom:setting.due_days`), and `<custom form>.<member name>`, parent members first, for kind `member`
    (`@member:message.invoice_sent.subject`). This replaces the default row of the form table of phase 1 section 5.1 for
    this kind.
15. **Translations.** `x-translatable` goes on a top-level string property of `content` or of a member item, whose name
    is an identifier (a custom kind file reports another name as MQ7212). The field is `content/<name>`, on the custom
    element's node or on the member's node. Members are localization sub-nodes of kind `member`. A custom kind's own
    display name and plural are not translated: a custom kind file is not a model node.
16. **Extensions and stereotypes apply to `custom`** for every custom kind: extension `appliesTo.kinds` and stereotype
    `appliesTo` gain `custom`. Per-kind targeting is not needed, since a custom kind's own schema holds its fields.
17. **Templates.** The scope `each custom:<type>`, the unit variable `custom`, the helper `custom_named` and `lookup` by
    id. Content values reach templates as written, ids staying ids; member arrays become lists of members. A scope
    naming a custom kind the project does not declare yields no element, so a shared pack may name kinds a project
    lacks.
18. **Resolution depends on the custom kind file.** Member arrays and the kind's display names come from it, so each
    resolved custom element records a new dependency key `c:<type>`, the hash of its custom kind file (the hasher's
    absent marker when the type is undeclared). `DependencyHasher` computes it, `PlanExplainer.KindOf` reports it as
    `custom-kind`, and engine-design.md section 11 lists it.
19. **No migration concern.** No resolved database object reads a custom element, so a write of one records no fact: a
    database whose reach includes it (through an `x-ref` to a table, say) compares captures that do not change.
    Changing an element's `type` moves its file, as any change of conventional folder does.
20. **Write answers stay bounded.** A write of a custom kind file validates the elements of that type only and returns
    their MQ3033 findings with the answer, at most 50 and the count. It never refuses because of them.

#### Schema and files

New `schemas/v1/custom-kind.json`, a document schema (it joins `KindInfo.DocumentSchemaFiles`), for
`extensions/kinds/<type>.json`. `x-order`: `$schema`, `type`, `displayName`, `pluralName`, `description`, `properties`,
`required`.

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| `type` | `common.json#/$defs/key` | required | Equals the file stem (MQ1017) |
| `displayName` | string, `minLength` 1 | absent | Editor and templates fall back to the type |
| `pluralName` | string, `minLength` 1 | absent | Falls back to the inflected display name |
| `description` | string, `minLength` 1 | absent | Markdown |
| `properties` | object, name to JSON Schema | required | The keys of `content`; `x-ref`, `x-translatable`, `x-member` |
| `required` | array of names | `[]` | Required keys of `content` |

New `schemas/v1/custom.json`, the kind schema, for `model/custom/<type>/<stem>.json`. `x-order`: `$schema`, `kind`,
`id`, `name`, `displayName`, `pluralName`, `package`, `type`, `description`, `rationale`, `decision`, `status`,
`stereotypes`, `tags`, `category`, `content`, `properties`, `generation`, `source`. Required: `kind`, `id`, `name`,
`type`. Every standard key of `ElementBase` is declared with its default, as in every kind schema, so a record written
back never carries an undeclared key.

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| `kind` | `const` `custom` | required | |
| `name` | `common.json#/$defs/identifier` | required | Unique per type and package (MQ3001) |
| `package` | `common.json#/$defs/id` | absent | Indexed reference, as on every packaged kind |
| `type` | `common.json#/$defs/key` | required | Names a custom kind file (MQ1019) |
| `content` | object, free-form | `{}` | Checked against the custom kind (MQ3033) |

`extension.json` and `stereotype.json`: `custom` joins the `appliesTo` kind lists. `common.json`: the `xRef` description
says that `custom:<type>` matches the custom elements of that type (none when the project does not declare it) and that
`of: <id>` with `member` names a custom element. `locale.json`: an entry gains `content` (identifier to non-empty
string, after `properties` in `x-order`), and the `src` `propertyNames` pattern adds
`content/[A-Za-z_][A-Za-z0-9_]*`. `pack.json`: the unit `for` pattern adds `each custom:[a-z][a-z0-9]*(-[a-z0-9]+)*`.
Each changed file gets its byte-identical copy (`copies`). `docs/api/openapi.yaml` follows: `ElementKind` and the
element union gain `custom` (E20), `ElementSummary` gains `type` (E22), the translation field pattern gains
`content/<name>` (E24), the resolved scope enum gains `custom-elements` with `CustomRecord` and `MemberRecord` (E25),
and `ExtensionFileInfo.kind` gains `custom-kind` (E26).

A custom kind and one of its elements in billing terms (ids shortened):

```json
{
  "type": "setting",
  "displayName": "Setting",
  "pluralName": "Settings",
  "properties": {
    "code": { "type": "string", "x-ref": { "kind": "row" } },
    "unit": { "type": "string" },
    "value": { "type": ["integer", "string", "boolean"] }
  }
}
```

```json
{
  "kind": "custom",
  "id": "01JF…D4Y",
  "name": "due_days",
  "package": "<id of package Billing>",
  "type": "setting",
  "content": { "unit": "days", "value": 30 }
}
```

A `message` kind marks its `parts` with `"x-member": true`, and each part declares `name` and an `x-translatable`
`text`. The element `invoice_sent` then holds parts such as
`{ "id": "01JF…P1", "name": "subject", "text": "Invoice {number} sent" }` under `content.parts`, and a French shard
holds `{ "content": { "text": "Facture {number} envoyée" } }` under the part's id.

#### Resolution and the resolved model

- **Loading.** `ModelPaths.Classify` adds `ModelFileKind.CustomKind` for `extensions/kinds/*.json`, and
  `ModelLoader.Enumerate` lists them (not recursively). The snapshot holds them as `CustomKindDocument`s, ordinal by
  type. A file that fails `custom-kind.json` is MQ1002 like any document and declares no custom kind.
- **The property schema set.** `PropertySchemaSource.OfCustomKind` reads `properties` with the existing `Read` and
  `TranslatableFindings` (extended for member items and identifier names, decision 15), plus a member tree
  (`XMemberNode`). Its compiled schema is `{ type: object, properties, required, additionalProperties: false }`, with
  `id` injected as a required ULID property into every member item schema. The set keeps these sources apart from the
  ones `For` and `ForDefaults` filter, so they never type a `properties` object. `PropertySchemaSet.ContentOf(type)`
  returns one; `Sites` adds the sites of a custom element's `content`. `XRef.Parse` stays syntactic: it never reads the
  declared custom kinds, since pack parameter schemas share it (`Loading/PackParameterRefs.cs:90`,
  `Planning/UnitRules.cs:158`). `TranslatedHolderKinds` gains `member`.
- **The index.** `DocumentWalk` registers each member as `IndexEntry(id, customId, "member", pointer)` through the
  custom kind's member tree. `ModelIndexer.SubElementKinds` gains `member`; `XRef.ReservedSubElementKinds` drops it, and
  `XRef.IsReserved` drops `custom` and `custom:<type>`. An `x-ref` to `custom:<type>` matches an entry of kind `custom`
  whose document's `type` is that type, else MQ2002. `ModelIndexer.Summarize` sets `ElementSummary.Type`, and
  `MemberCount` (today set on diagram rows) to the number of members, for a custom element. `SubElementIds` lists a
  custom element's members from the index entries it owns, so a save that changes a member widens to its referrers.
- **The resolver.** `ResolveRun` builds one `RCustom` per custom element, with annotations filled as for every element
  and `Content` converted to template values. A content value at a member array becomes an `RList<RMember>`, and each
  member's own keys (`id` aside) become its `Content` the same way, so nested members are members too. `Find` reaches
  members by id. `ResolvedModel.CustomElements` lists every custom element by (type, name, id). Each `RCustom` records
  `c:<type>`; an `RMember` records its owner's keys. The custom pass registers before 4.1 links `Requires` (its
  resolution step 2, last of all), so an operation's `requires` resolves a custom element or a member.
- **Localization.** `LocalizationIndex` builds a custom element's content texts and one sub-node per member with
  translatable keys. `LocaleChains.ElementKinds` already takes `custom`, since it lists the kinds it leaves out
  (`Localization/LocaleChains.cs:20-24`); `LocaleChains.SubElementKinds` gains `member`.
  `LocalizationIndex.ContentFieldPrefix` is `content/`, and `IsField`, `FieldsOf` and `FieldsIn` accept it as they
  accept `properties/` (phase 1 section 4.4).

#### Public API

```csharp
namespace Maquettiste.Engine.Model;
public enum ElementKind { /* existing */ [JsonStringEnumMemberName("custom")] Custom }   // appended
public sealed record Custom : Element, IPackaged
{
    public override ElementKind Kind => ElementKind.Custom;
    [ElementRef(ElementKind.Package)] public string? Package { get; init; }
    public required string Type { get; init; }                         // a custom kind's type key
    public IReadOnlyDictionary<string, JsonElement> Content { get; init; }
        = ImmutableDictionary<string, JsonElement>.Empty;
}
public sealed record CustomKind                                         // extensions/kinds/<type>.json
{
    public required string Type { get; init; }
    public string? DisplayName { get; init; }
    public string? PluralName { get; init; }
    public string? Description { get; init; }
    public required JsonElement Properties { get; init; }
    public IReadOnlyList<string> Required { get; init; } = [];
}
public sealed record CustomKindDocument(CustomKind Kind, string Path, string Hash);
public sealed class ModelSnapshot { public IReadOnlyList<CustomKindDocument> CustomKinds { get; } }   // ordinal by type
// ElementSummary gains a last positional parameter, left out of the JSON when null, and Equals compares it:
//   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Type = null
public sealed record KindInfo { public const string CustomFolder = "model/custom"; }   // + "/<type>"
public sealed record TranslationEntry
{   public IReadOnlyDictionary<string, string> Content { get; init; } = ImmutableDictionary<string, string>.Empty; }
namespace Maquettiste.Engine.Resolution;
public sealed class RCustom : RElement
{
    public override string Kind => "custom";
    public string Type { get; internal set; } = "";
    public string TypeDisplayName { get; internal set; } = "";        // falls back to Type
    public string TypePluralName { get; internal set; } = "";         // falls back to the inflected display name
    public IReadOnlyDictionary<string, object?> Content { get; internal set; }
        = FrozenDictionary<string, object?>.Empty;
    public RList<RMember> Members { get; internal set; } = RList<RMember>.Empty;   // every member, walk order
}
public sealed class RMember : RObject
{
    public override string Kind => "member";
    public string? Name { get; internal set; }
    public string Array { get; internal set; } = "";                  // the key of the array that holds it
    public RCustom Owner { get; internal set; } = null!;
    public RMember? Parent { get; internal set; }
    public IReadOnlyDictionary<string, object?> Content { get; internal set; }
        = FrozenDictionary<string, object?>.Empty;
}
public sealed class ResolvedModel { public RList<RCustom> CustomElements { get; internal init; } }
namespace Maquettiste.Engine.Localization;
public sealed class LocalizationIndex { public const string ContentFieldPrefix = "content/"; }
namespace Maquettiste.Engine;
// ExtensionFileInfo.Kind and ExtensionFileContent.Kind take "custom-kind" beside "schema" and "rule".
```

These internal names are fixed: `XMemberNode` and `PropertySchemaSource.OfCustomKind` in `Model/PropertySchemas.cs`,
`CustomRules` in `Validation/CustomRules.cs`, `ModelFileKind.CustomKind` in `Loading/ModelPaths.cs`.

#### Rules

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ1017 | E | A custom kind file is invalid: its `type` differs from its file name, its `properties` do not compile as a JSON Schema, or its `required` names a property it does not declare. | Fix the file |
| MQ1018 | E | An `x-member` keyword is misplaced: not `true`, not on an array whose `items` is an object schema, on an item schema that declares `id`, or under an applicator the walk does not follow. | Move or fix it |
| MQ1019 | E | A custom element's `type` names no custom kind file; its content is not checked. | Declare the custom kind, or change `type` |
| MQ3033 | E | A custom element's content fails its custom kind's schema: a required key missing, an undeclared key, a value of the wrong shape, or a member without an id. | Fix the value |
| MQ2009 | E | Changed text: an `of` that names no custom element under kind `member` is malformed too. | Fix the schema |
| MQ2011 | E | Changed text: a member of a custom element other than the one `of` names is out of scope. | Pick a target in scope |
| MQ7212 | E | Changed text: `x-translatable` is also allowed on a top-level string property of a custom kind's content or member items, whose name is an identifier. | Move it |

MQ3001 and MQ1005 keep their text and cover custom names (per type and package), member names (per array) and the folder
`model/custom/<type>/`. A save that changes `type` revalidates the referrers, so an `x-ref` that names
`custom:<old type>` reports MQ2002 there. MQ2001's "arrives in a later phase" note no longer names `member`, `custom` or
`custom:<type>` (`Validation/BuiltinRules.cs:199-208`).

#### CLI and MCP

No MCP tool and no CLI verb is added. The generic element tools cover custom elements.

- **Extension file tools.** `list_extension_files`, `read_extension_file`, `write_extension_file`,
  `delete_extension_file` and `move_extension_file` (and `GET`/`PUT`/`DELETE /api/extensions/...`) accept
  `kinds/<type>.json` with kind `custom-kind`. A write is checked against `custom-kind.json` and the rules above,
  written in canonical form, and answers with the MQ3033 findings of the custom kind's elements (decision 20). A delete
  or a move of a custom kind with elements answers `invalid` with one MQ1019 per element that would lose its kind, at
  most 50, and the count.
- **`get_schema`** takes `custom:<type>`. The answer is `custom.json` with its references, plus `customKind` (the
  declaration with its path) and `contentSchema` (the compiled content schema, member ids injected), so an agent can
  build a valid document.
- **`get_model_index`, `get_elements` and `get_model_kinds`.** Rows of custom elements carry `type` and `memberCount`. A
  kind filter takes `custom:<type>` (`Editor/ModelPages.cs:98-99`), and `custom` matches every custom kind. Kind counts
  list one `custom:<type>` row per custom kind instead of one `custom` row.
- **`get_project`** lists `customKinds`, the declared type keys in ordinal order, beside `extensions`.
- **`get_resolved_model`** gains the scope `custom-elements`: one record per custom element with `type`, `package`,
  `content` as written and `members` (`id`, `name`, `array`, `parent`, `pointer`). `all` includes it.
- **`get_translations`, `set_translations`, `localization_status`** take `content/<name>` fields and member nodes.
- **`get_template_context`** for an `each custom:<type>` unit lists `content`'s declared keys under `custom`.
- **`apply_batch`, `create_element`, `save_element`, `patch_element`** need no new parameter. Their descriptions name
  the `custom` kind, `type`, `content`, member ids minted by the engine and the `@custom:` and `@member:` forms.
- **`format_model`** and **`maquettiste format`** also rewrite custom kind files in canonical form (today both skip
  everything under `extensions/`).
- **CLI.** `maquettiste model export` and `model stats` accept `--kind custom:<type>`.

#### Templates

- **Scope.** `each custom:<type>` yields the elements of that type in (name, id) order; `where` filters on tags,
  stereotypes, categories and packages apply. `UnitRules.Scopes` names the pattern in MQ6021's message.
  `GenerationService.ScopeType` maps it to `RCustom`.
- **Variable.** `custom` joins `BuiltinHelpers.Variables` and the alias list of `RenderRun.CreateGlobals`
  (`Rendering/RenderRun.cs:469-470`).
- **Helper.** `custom_named "<type>" "<name>"` returns the custom element of that type and name, or null; the name may
  carry a package path (`"Billing.due_days"`), and a name two elements share fails the unit (MQ6006). It records
  `n:custom:<type>.<name>`, the batch form, so the existing name hash reads it from the `custom` name index, as
  `seed_named` records `n:seed:<form>`. `custom_named` joins `BuiltinHelpers.Names`.
- **Scripts.** Rule scripts and 3.4's rows scripts read custom elements through the rule model view with no change to
  it: `model.all("custom")` returns every custom element (the call records `k:custom`, each document its `e:` key),
  since `all` takes any kind `KindInfo` knows (`Scripting/ScriptSandbox.Calls.cs:237-243`), and `get(id)` returns an
  element or a member's item. A script filters on `type`; `all` takes no `custom:<type>`. A seed whose rows script
  reads them gets one row per element of a kind, with the row ids 3.4 derives from its keys, and is evaluated again
  when a custom element is added, removed or changed.
- **Example.**
  `{{ for part in custom.content.parts }}{{ part.name }}={{ translate part "content/text" locale }}{{ end }}` and, at
  model scope, `{{ (custom_named "setting" "due_days").content.value }}` or `{{ (lookup id).content.value }}`.

#### Editor follow-ups

The generic JSON form covers custom elements and custom kind files until these screens follow: one explorer section per
custom kind (named by its plural), the content form driven by the custom kind's schema with the `x-ref` picker deferred
in phase 1, a member list editor that keeps ids, the custom kind file editor on the Extensions screen, and
`custom:<type>` in the kind filters. E20 gives the kind a label ("Custom element") and an icon only.

#### Docs

User guide: a new section "Custom kinds (`extensions/kinds/*.json`)" after "Stereotype rules and parameters", and a
paragraph in "Translating the model in the editor". `docs/mcp.md` rows for every tool named above.
`docs/engineering/engine-design.md` section 11 adds the key `c:<type>`. `docs/releases.md` under
`## 0.12.0 (unreleased)`. The modeling skill explains custom kinds, members and `@custom:` names.

**Not doing**

- New engine kinds per project concept (scope section 10).
- Extension `appliesTo` per custom kind (`custom:<type>`): a custom kind's own schema holds its fields.
- Tags, stereotypes, properties, status or description on members.
- Inheritance between custom kinds, and custom kinds declared by packs: packs keep languages and conventions, custom
  kinds are the project's vocabulary.
- A rename action that rewrites `type` in every element: a move is refused while elements exist; rename by creating the
  new custom kind and updating elements with a batch.
- Translating a custom kind's display name and plural.
- Content values resolved to objects in templates: ids stay ids, and `lookup` reaches them.
- `x-translatable` deeper than a member item's top level.

**Tests**

- `CustomKindLoadTests` (new): `Custom_kind_files_load_from_extensions_kinds_only`,
  `A_custom_element_of_an_undeclared_kind_is_MQ1019`, `A_custom_element_outside_its_type_folder_is_MQ1005`,
  `Changing_type_moves_the_file`.
- `CanonicalJsonTests.Custom_content_is_written_in_ordinal_order_at_every_level`; `SchemaConsistencyTests` (existing).
- `PropertySchemaSetTests`: `Custom_kind_sources_type_content_and_join_the_hash`,
  `Custom_kind_sources_never_type_properties`,
  `X_member_marks_parse_and_misplaced_ones_are_MQ1018` (a theory, one row per branch),
  `A_type_differing_from_its_file_or_requiring_an_undeclared_key_is_MQ1017`.
- `XrefRuleTests`: `Content_values_are_checked_like_properties`, `A_member_of_another_custom_element_is_MQ2011`,
  `A_custom_element_of_another_type_is_MQ2002`, `A_value_under_an_undeclared_custom_kind_is_MQ2002`.
- `LocalizationRuleTests`: `X_translatable_below_a_member_item_is_MQ7212`,
  `X_translatable_on_a_content_name_that_is_not_an_identifier_is_MQ7212`.
- `ModelSnapshotIndexTests.Content_xrefs_are_indexed_with_the_custom_kind_schema_and_the_member_as_holder`;
  `ModelIndexerPatchTests.Changing_a_custom_kind_file_rebuilds_the_index`.
- `CustomMemberTests` (new): `Members_are_indexed_as_member_sub_elements`,
  `Nested_members_are_indexed_under_their_owner`, `A_duplicate_member_id_is_MQ1004`,
  `Removing_a_used_member_is_refused_and_an_optional_value_is_cleared`,
  `Saving_a_member_change_revalidates_its_referrers`.
- `IdMintingTests.Members_get_ids_in_walk_order_and_a_deterministic_generator_gives_identical_files`;
  `BatchNamesTests.Custom_and_member_forms_resolve`;
  `ModelStoreBatchTests.Dropping_a_named_member_and_adding_one_without_an_id_is_MQ1014`;
  `NamesToIdsTests.Content_names_become_ids`.
- `CustomContentRuleTests` (new): `Content_failing_its_kind_is_MQ3033_with_a_pointer`,
  `An_undeclared_content_key_is_MQ3033`, `A_member_without_an_id_is_MQ3033`,
  `A_name_repeated_within_a_type_and_package_is_MQ3001`, `A_member_name_repeated_in_one_array_is_MQ3001`,
  `Stereotypes_and_extensions_apply_to_custom_elements`.
- `LocalizationTests`: `Content_texts_join_rows_status_and_pruning`, `Member_texts_are_member_nodes`;
  `LocalizationHelperTests.Translate_reads_a_member_content_field`; Cli
  `McpLocalizationToolTests.Translations_handle_content_fields`.
- `CustomResolutionTests` (new): `Custom_elements_resolve_with_content_and_members`,
  `Member_arrays_become_member_lists`, `Lookup_finds_members`, `A_custom_kind_file_change_re_renders_through_its_c_key`,
  `Custom_named_records_the_batch_form_as_its_name_key`, `A_rows_script_gives_one_row_per_custom_element`.
- `UnitPlannerTests.Each_custom_kind_plans_one_unit_per_element_and_none_for_an_undeclared_kind`;
  `UnitRulesTests.Each_custom_scope_is_known`;
  `TemplateCacheAndHelperTests.Custom_named_finds_by_name_and_package_path_and_records_its_key`;
  `ResolvedRecordsTests.Custom_elements_scope_returns_one_record_per_element`; `ResolvedCoverageTests` (existing).
- `ExtensionFilesTests`: `Custom_kind_files_are_listed_read_and_written_in_canonical_form`,
  `Deleting_or_moving_a_used_custom_kind_is_refused`, `A_custom_kind_write_reports_MQ3033_for_its_elements`; Cli
  `McpExtensionToolTests.Custom_kind_files_round_trip`.
- `FormatTests.Format_rewrites_custom_kind_files`; Cli `PermissionAndFormatTests` (existing).
- `McpReadTests`: `Get_schema_for_a_custom_kind_returns_the_declaration_and_content_schema`,
  `Model_index_and_kinds_present_custom_kinds`; `AgentToolsTests.Get_project_lists_custom_kinds`;
  `ElementSummaryTests.A_custom_row_carries_type_and_member_count`; `ModelCommandTests.Kind_filter_takes_a_custom_kind`.
- **Acceptance (3.7, first line):** `Integration/CustomKindTests.A_setting_in_billing_reaches_templates_by_name_and_id`
  (new). Setup: an `E2ERepo` billing copy adds `extensions/kinds/setting.json` (above) and `due_days` in package
  Billing with value 30, and a pack with a unit `each custom:setting` rendering
  `{{custom.name}}={{custom.content.value}}` and a model unit rendering `custom_named "setting" "due_days"` and
  `lookup` of its id. Expected: validate is clean; the scope unit writes `due_days=30`; the model unit writes 30 twice;
  changing the value to 45 re-renders both units, and a second unchanged run renders none.
- **Acceptance (3.7, second line):**
  `Integration/CustomKindTests.A_message_has_parts_with_ids_and_a_translated_text_per_part`. Setup: the copy adds the
  `message` kind and `invoice_sent` with parts `subject` and `body` and no ids, created through `apply_batch`, and the
  locales `en` (default) and `fr`. Expected: the batch result's `ids.minted` lists two `member` ids named
  `@member:message.invoice_sent.subject` and `.body`; `set_translations` stores a French `content/text` per part; the
  `fr` row of `localization_status` for the shard of package Billing counts two more translated texts than before; a
  template renders the French texts.
- **Acceptance (3.7, third line):**
  `Integration/CustomKindTests.A_setting_whose_code_is_a_row_survives_renaming_the_code`. Setup: the copy adds an
  invented reference type `PaymentTerm` with a seed of rows `net_30` and `net_60`, and a setting `default_payment_term`
  whose `code` is the id of row `net_30`. Renaming the code to `net30` with 3.5's `rename-code` operation keeps the
  row id. Expected: the model stays clean, the file of `default_payment_term` is unchanged, and a template that reads
  the row through `lookup` renders `net30`.
- **Acceptance (3.7, fourth line):** `Integration/CustomKindTests.Deleting_a_setting_an_operation_requires_is_refused`.
  Setup: the first test's copy plus 4.1's operation `send` on Invoice with `requires: [<id of due_days>]`. Expected:
  `delete_element` on `due_days` answers `referenced`, naming `send` and `/requires/0`; after removing the link the
  delete succeeds.

**Tasks**

Section 4.2 starts once 4.1's code tasks are in (E4, E6, E7 and E8, with E1, E2, E3 and E5 before them, and the editor
task E11): they edit the same kind registry, schemas, rule catalog, resolver, template registries and tool texts. The
docs task, and E25 (it edits `docs/engineering/engine-design.md`), follow 4.1's docs task E12 for the same reason.

- **E20. The `custom` kind and custom kind files.** Runs after E4, E6, E7, E8 and E11. Owns: `Model/ElementKind.cs`,
  `Model/KindInfo.cs` (the kind, `CustomFolder`, `DocumentSchemaFiles`), `Model/Custom.cs` (new: `Custom`,
  `CustomKind`, `CustomKindDocument`), the derived type in `Model/ElementBase.cs`, `Loading/ModelPaths.cs`
  (`ModelFileKind.CustomKind`, `ConventionalFolder`), `Loading/ModelLoader.cs`, `Model/ModelSnapshot.cs`
  (`CustomKinds`), `Validation/CustomRules.cs` (new: MQ1019), `Validation/ModelValidator.cs` (wiring only),
  `RuleCatalog.cs` (MQ1019), `CatalogTests.cs` (+1), `ResolvedCoverageTests` (a target for `custom.json`, `Pending`
  for `type` and `content` until E25), `custom.json` and `custom-kind.json` (new), `extension.json` and
  `stereotype.json` (`custom`), with their copies; `openapi.yaml` (`ElementKind`, the element union);
  `src/editor/src/model/labels.ts` (`KIND_LABELS`) and `src/editor/src/app/icons.tsx` (`KIND_ICONS`), which are typed
  over every kind. Tests: `CustomKindLoadTests`, `CanonicalJsonTests`, `SchemaConsistencyTests`. Verify:
  `E "~CatalogTests|~CustomKindLoadTests|~Json.|~ModelLoaderTests|~ResolvedCoverage"`; `P "~GoldenTests"`;
  `F "~ContractTests"`; `record`; `editor`; `copies`.
- **E21. Content schemas in the property schema set.** Runs after E20. Owns: `Model/PropertySchemas.cs`
  (`OfCustomKind`, `XMemberNode`, `ContentOf`, content sites, the set's hash, `XRef` reserved kinds and the `member`
  `of` scope, `TranslatableFindings` for content and member items, `TranslatedHolderKinds`), `Model/Documents.cs`
  (`ReferenceInfo.Schema` documentation), `Model/ModelIndexer.cs` (content references), `Validation/BuiltinRules.cs`
  (`CheckReferences`: `custom:<type>`, the member scope and the MQ2001 note), `RuleCatalog.cs` (MQ1017, MQ1018; MQ2009,
  MQ2011 and MQ7212 texts), `CatalogTests.cs` (+2), `common.json` (`xRef` description) with its copy. Tests:
  `PropertySchemaSetTests`, `XrefRuleTests`, `LocalizationRuleTests`, `ModelSnapshotIndexTests`,
  `ModelIndexerPatchTests`. Verify: `E "~CatalogTests|~PropertySchemaSetTests|~XrefRuleTests|~LocalizationRuleTests"`;
  `E "~ModelSnapshotIndexTests|~ModelIndexerPatchTests"`; `record`; `copies`.
- **E22. Members as sub-elements.** Runs after E21. Owns: `Model/ModelIndexer.cs` (member entries, `SubElementKinds`,
  `Summarize`), `Loading/IdMinter.cs`, `Loading/NameIndex.cs` (the `custom` and `member` forms and holders),
  `Loading/BatchNames.cs`, `Loading/ChangePlanner.cs` (the MQ1014 check for named members),
  `Validation/ModelValidator.cs` (`SubElementIds`: members), `Model/Documents.cs` (`ElementSummary.Type`),
  `Editor/ModelReads.cs` (`IndexFormat` to its next value), `src/editor/src/mocks/model/store.ts` (`INDEX_FORMAT`),
  `openapi.yaml` (`ElementSummary.type`). Tests: `CustomMemberTests`, `IdMintingTests`, `BatchNamesTests`,
  `ModelStoreBatchTests`, `NamesToIdsTests`, `ElementSummaryTests`. Verify:
  `E "~CustomMemberTests|~IdMintingTests|~BatchNamesTests|~ModelStoreBatchTests|~NamesToIdsTests"`;
  `E "~ElementSummaryTests|~DeleteCascadeTests"`; `C "~McpWriteTests"`; `F "~ContractTests|~ProjectAndModelTests"`;
  `editor`.
- **E23. Content validation.** Runs after E21. Owns: `Validation/CustomRules.cs` (MQ3033, the MQ3001 scopes),
  `Validation/BuiltinRules.cs` (name scopes only), `Validation/ExtensionRules.cs` (`custom` hosts), `RuleCatalog.cs`
  (MQ3033), `CatalogTests.cs` (+1). Tests: `CustomContentRuleTests`. Verify:
  `E "~CatalogTests|~CustomContentRuleTests|~ValidationFixtureTests|~StereotypeRuleTests"`; `record`.
- **E24. Content and member translations.** Runs after E22. Owns: `Localization/LocalizationIndex.cs`,
  `Localization/LocaleChains.cs`, `Model/Localization.cs` (`TranslationEntry.Content`), `LocaleShardReader.cs`,
  `Loading/ShardCache.cs`, `TranslationFiles.cs`, `ModelStore.Translations.cs`, `TranslationPrune.cs`,
  `LocalizationHelpers.cs`, `locale.json` with its copy, the translation field pattern in `openapi.yaml`, the
  `localization_status` text in `Agent/tools.json`, the `ModelTools.cs` texts of `get_translations` and
  `set_translations`, and the `docs/mcp.md` rows of the three tools. Tests: `LocalizationTests`,
  `LocalizationHelperTests`, `McpLocalizationToolTests`. Verify: `E "~Localization|~Json."`;
  `C "~McpLocalizationToolTests|~McpSurfaceTests"`; `F "~LocalizationEndpointTests|~ContractTests"`; `editor`;
  `copies`.
- **E25. Resolution and templates.** Runs after E24 (both edit `openapi.yaml` and `Agent/tools.json`) and 4.1's E12
  (`docs/engineering/engine-design.md`). Owns:
  `Resolution/ResolvedCustom.cs` (new: `RCustom`, `RMember`), `Resolution/ResolvedCore.cs` (`CustomElements`),
  `Resolution/ResolveRun.Custom.cs` (new), `Resolution/ResolveRun.cs` (wiring), `Planning/DependencyHasher.cs` (`c:`),
  `Generation/PlanExplainer.cs` (`custom-kind`), `Planning/UnitPlanner.cs`, `Planning/UnitRules.cs`,
  `GenerationService.Authoring.cs` (`ScopeType`, content keys in the template context), `Rendering/BuiltinHelpers.cs`
  (`custom`, `custom_named`), `Rendering/RenderRun.cs` (alias), `Editor/ResolvedRecords.cs` (`CustomRecord`,
  `MemberRecord`, scope `custom-elements`), `ResolvedCoverageTests`, `pack.json` with its copy, the `get_resolved_model`
  text in `Agent/tools.json`, the resolved scope enum with `CustomRecord` and `MemberRecord` in `openapi.yaml`, and the
  key list of `docs/engineering/engine-design.md` section 11. Tests: `CustomResolutionTests`, `UnitPlannerTests`,
  `UnitRulesTests`, `TemplateCacheAndHelperTests`, `ResolvedRecordsTests`. Verify:
  `E "~Resolution.|~Planning.|~Rendering.|~ResolvedRecordsTests|~ResolvedCoverage"`; `P "~GoldenTests"`;
  `C "~McpSurfaceTests"`; `F "~ContractTests"`; `editor`; `copies`.
- **E26. Tools for custom kind files and custom rows.** Runs after E23 and E25. Owns: `ModelStore.Extensions.cs`
  (paths, kind `custom-kind`, delete and move refusals, MQ3033 in write answers), `ModelStore.Format.cs` and
  `src/Maquettiste.Cli/Commands/FormatCommand.cs` (custom kind files), `Editor/ModelPages.cs` (kind filter and counts),
  `Agent/AgentTools.Handlers.cs` (`get_schema`, `customKinds` on `AgentProjectInfo`), `Agent/tools.json`, the
  `ModelTools.Extensions.cs`, `ModelTools.cs` (the write tools and `format_model`) and `ModelTools.Packs.cs` texts,
  `src/Maquettiste.Cli/Commands/ModelCommand.cs` (`--kind custom:<type>`), `ExtensionFileInfo` in `openapi.yaml`, and
  the `docs/mcp.md` rows of the other tools named in "CLI and MCP". Tests: `ExtensionFilesTests`, `FormatTests`,
  `McpExtensionToolTests`, `McpReadTests`, `AgentToolsTests`, `ModelCommandTests`. Verify:
  `E "~ExtensionFilesTests|~AgentToolsTests|~FormatTests"`; `C "~McpSurfaceTests|~McpExtensionToolTests|~McpReadTests"`;
  `C "~ModelCommandTests|~PermissionAndFormatTests"`; `F "~ContractTests|~ExtensionEndpointTests"`; `editor`.
- **E27. Docs for custom kinds.** Runs after E26 and E12. Owns: the user-guide section and paragraph, `docs/releases.md`
  (the custom kinds lines under `## 0.12.0 (unreleased)`), `skills/maquettiste-modeling/SKILL.md` (custom kinds).
  Tests: none new. It reads the tool rows E24 to E26 wrote and checks them against the tool texts, editing neither.
  Verify: `C "~SkillRefreshTests|~AgentSetupTests|~McpSurfaceTests"`, where the skill hash changes on purpose.
- **E28. Acceptance for custom kinds.** Runs after E26. Owns: `Integration/CustomKindTests.cs` (new; the first three
  acceptance tests). Tests: those three. Verify: `E "~CustomKindTests"`.
- **E29. Acceptance with an operation.** Runs after E28 and after 4.1's E1, which adds the `operation` kind with
  `requires`. Owns: the fourth test in `Integration/CustomKindTests.cs`. Tests: that test. Verify:
  `E "~CustomKindTests"`.

E30 to E39 are free.

### 4.3 Requirements

**Not being built as designed (the owner's decision of 2026-11-05).** A project declares requirements as its own custom
kind instead; `next.md` lists the small additions that make this work. This section stays as the record of the built-in
kind, in case a later need brings it back.

**Why every team**

Every team that builds software works from statements of what it must do, whether they come from a contract, a
regulation, a product brief or a support ticket. Today those statements live outside the model, so nobody can ask which
part of the model honours a statement, which statement has no test, or why an element is shaped the way it is. A
requirement kind with two links, `satisfies` from the elements that honour it and `verifies` from the scenarios that
prove it, answers those questions inside the model, for people and for agents. An agent that reads an element also reads
the requirements behind it before it edits. A coverage report lists the gaps, and teams that keep their requirements in
another tool import them by external id, with no connector to any product. These are SysML v2's requirement, satisfy and
verify.

**Today**

- No kind holds a requirement (`src/Maquettiste.Engine/Model/ElementKind.cs:5-83`).
- Every element and `ElementBase` sub-element carries `rationale`, `decision` and `status`
  (`Model/ElementBase.cs:27-34`), and a reference to a retired id already warns (MQ2022,
  `Diagnostics/RuleCatalog.cs:95`). The top-level key `source` is taken by import provenance
  (`Model/ElementBase.cs:53-54`).
- Every kind schema, and every schema of an `ElementBase` sub-element, declares each standard key with its default:
  the canonical writer drops a declared key that holds its default and keeps an undeclared one
  (`Json/CanonicalJson.cs:167-177`). A migration fact's content hash serializes the fact record and writes it that way
  (`Migrations/FactHash.cs:49-53`).
- Process nodes are not `ElementBase` (`Model/Processes.cs:11-15`). A guard holds an id, a name, a display name, an
  expression and a description (`Model/Processes.cs:272-288`, `schemas/v1/process.json:423-457`).
- A scenario names its process and steps and nothing it proves (`schemas/v1/scenario.json:6-27`).
- The index keeps references in both directions (`Model/ModelSnapshot.cs:256`, `:261`), and the resolver has a
  referrers key `r:<id>` (`Resolution/ResolutionValues.cs:113`).
- Seed CSV import previews unless asked to apply (`src/Maquettiste.Cli/Mcp/ModelTools.cs:322-349`,
  `src/Maquettiste.Cli/Commands/SeedCommand.cs:135-142`), and the engine has a CSV reader
  (`Localization/TranslationFiles.cs:22`).
- One rule runs only when `validation.rules` gives it a severity: MQ7205 (`Validation/LocalizationRules.cs:111`).
- A save validates the changed files, the files that referenced them before, their referrers and their conflict peers,
  all worked out on the new model (`ModelStore.cs:1021-1026`, `Validation/ModelValidator.cs:417`).
- Fact recording skips annotation keys (`ModelStore.Migrations.cs:51-54`). Layering skips `package`, `parent`,
  `dependsOn` and diagrams (`Validation/PackageRules.cs:153-160`).
- Localization lists the kinds it leaves out, so a new kind's display name and description translate with no change
  (`Localization/LocaleChains.cs:20-24`).
- `AgentTools.All` is pinned twice (`tests/Maquettiste.Engine.Tests/Editor/AgentToolsTests.cs:26`,
  `tests/Maquettiste.Cli.Tests/McpServerTests.cs:58`).

**Decisions taken here**

1. **A kind of its own.** `requirement`, one file each in `model/requirements/`, in a package like every packaged kind.
   It holds `name` (an identifier, the model's handle), `externalId`, `text` (the statement, Markdown, required),
   `citation` (where it is stated) and the common fields. Its status is the `status` of phase 1 section 4.5, with its
   four values.
2. **The scope's words.** The scope's "key" is `name`, unique among requirements of one package (MQ3001). Its "source"
   is `citation`, because `source` is taken. `citation` is free text, such as a contract clause or a document section,
   and is not checked, like `decision`.
3. **`satisfies` where intent lives.** A list of requirement ids on every kind except `requirement`, `scenario`,
   `diagram`, `tag-vocabulary`, `category-tree` and `migration` (4.1's `operation` included); on attributes, enum
   members and columns; and on the process's states, transitions, events, guards, actions, invokes and gates. Keys,
   seed rows, categories and database schemas are left out: their table, attribute, seed, tree or database satisfies.
   The engine adds it to `ElementBase` as a nullable list, null when the file has none, and to the seven node records
   as a list. Nullable, because records of kinds whose schema has no `satisfies` are serialized and written: the
   migration facts the classifier builds (`Migrations/FactClassifier.cs:124`) and their content hash. Null writes
   nothing, so those files and every fact hash stay as they are. The schemas decide where it may be written.
4. **`verifies` on scenarios only.** A list of requirement ids. A scenario proves; it does not satisfy.
5. **Links are ordinary references.** Both lists carry `[ElementRef(ElementKind.Requirement)]`. So MQ2001 and MQ2002
   judge them, a cited requirement refuses a plain delete, `remove-references` clears the link, batches take
   `@requirement:` names, and MQ2022 warns when a requirement is retired while something still cites it.
6. **Traceability is not a dependency.** Layering (MQ2020) skips `satisfies` and `verifies`, as it skips diagrams, so a
   team may keep requirements in a package of their own without every package depending on it.
7. **Links never change a database.** `satisfies` joins the top-level and nested annotation lists of fact recording;
   `verifies` sits on scenarios, which reach no database.
8. **What counts as covered.** A requirement is satisfied when an element or sub-element lists it that is not retired
   and does not sit in a retired element, and verified when a scenario that is not retired lists it. The gaps are
   `unsatisfied` and `unverified` on a requirement that is neither `proposed` nor `retired` (no status counts as open),
   and `retired-cited` on a retired requirement that something not retired still cites. The report reads the index
   only, never the resolver, and pages its rows.
9. **Two ways to gate.** `maquettiste requirement coverage --check` exits 2 when the report has a gap, as other
   `--check` runs exit on drift. MQ3038 and MQ3039 report the same gaps from `validate`, but only when
   `validation.rules` gives them a severity, as MQ7205 does, so a model with requirements keeps its findings until the
   team opts in.
10. **Saves keep the requirement rules current.** `ModelValidator.ConflictPeers` gains two cases: a document's peers
    include the requirements its `satisfies` and `verifies` lists name on the new model, and a requirement's peers
    include the requirements that share its external id (MQ3037), as names are matched today. The save's scope also
    takes the requirements the changed files named before the change (`ModelStore`, beside `FormerReferrers`), so a
    requirement whose last link goes is validated in the same save.
11. **Import is one batch.** CSV or JSON rows, matched to requirements by `externalId` (ordinal), become create and
    update operations of one batch: all or nothing, ids minted by the engine, a preview unless applied. A column the
    file lacks leaves its field alone; an empty cell or `null` clears an optional field; `text` cannot be cleared.
    Requirements the file leaves out are not touched. Unknown columns are listed and ignored.
12. **Packages in an import.** A `package` value is an id or a package form of phase 1 section 5.1 (`Billing`,
    `Billing.Catalog`), resolved by the batch, so MQ1012 and MQ1013 report what it cannot find. `--package` (CLI) or
    `package` (MCP) sets the package of new rows that give none; it never moves an existing requirement.
13. **Names in an import.** A new row without `name` takes one from its external id: each run of characters outside
    `[A-Za-z0-9_]` becomes `_`, leading and trailing `_` go, and `req_` comes first when the result is empty or starts
    with a digit. A name already taken in the package gets `_2`, `_3` and so on, in row order. `BIL-12` becomes
    `BIL_12`.
14. **Agents read why first.** `get_element` adds `requirements`: for each requirement the element or its sub-elements
    satisfy, or a scenario verifies, its id, name, external id, status and text. It reads the element's own references
    and those requirement files only.
15. **Templates.** The scope `each requirement` and the unit variable `requirement`. A resolved requirement lists what
    satisfies and verifies it; `satisfies` reaches every annotated object and process node, `verifies` every scenario.
    `SatisfiedBy` and `VerifiedBy` depend on the requirement's referrers key `r:<id>`, so a new link re-renders what
    read them.
16. **Text is not translated in phase 2.** A requirement's `displayName` and `description` are standard fields and
    translate as they do on every kind. `text` stays in the team's working language.

#### Schema and files

New `schemas/v1/requirement.json` for `model/requirements/<stem>.json`. `x-order`: `$schema`, `kind`, `id`, `name`,
`displayName`, `pluralName`, `package`, `externalId`, `text`, `citation`, `description`, `rationale`, `decision`,
`status`, `stereotypes`, `tags`, `category`, `properties`, `generation`, `source`. Required: `kind`, `id`, `name`,
`text`.

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| `name` | `common.json#/$defs/identifier` | required | Unique per package (MQ3001) |
| `externalId` | string, `minLength` 1, `maxLength` 200 | absent | Unique among requirements (MQ3037); import key |
| `text` | string, `minLength` 1 | required | The statement, Markdown |
| `citation` | string, `minLength` 1, `maxLength` 2000 | absent | Where it is stated; not checked |

`common.json` gains `$defs/requirementIds` (an `idList` with `uniqueItems`, described as requirement ids). Every kind
schema that takes `satisfies` (decision 3) adds it after `status` with default `[]`, so files without it stay
byte-identical; so do the attribute (`common.json`), the enum member (`enum.json`) and the column
(`common.json#/$defs/tableColumn`, which 3.1 moves out of `table.json`; a stereotype's contributed column takes it too,
and coverage then lists that member of the stereotype, never each carrying table). In
`process.json` the state, transition, event, guard, action, invoke and gate add it after `description`. `scenario.json`
adds `verifies` after `status`, with default `[]`. `extension.json` and `stereotype.json` add `requirement` to their
`appliesTo` lists. `pack.json` adds `each requirement` to the unit `for` pattern. Each changed file gets its copy
(`copies`). `docs/api/openapi.yaml` follows: `ElementKind` and the element union gain `requirement` (E40), and the
resolved scope enum gains `requirements` with `RequirementRecord` (E42).

```json
{
  "kind": "requirement",
  "id": "01JF…R1",
  "name": "issued_invoice_keeps_lines",
  "package": "<id of package Billing>",
  "externalId": "BIL-12",
  "text": "An issued invoice keeps its lines.",
  "citation": "Billing brief, section 3",
  "status": "approved"
}
```

Import columns, in any order: `externalId` (required), `name`, `displayName`, `text`, `citation`, `status`, `package`,
`description`. JSON takes an array of objects with the same keys.

```text
externalId,name,text,status
BIL-12,issued_invoice_keeps_lines,An issued invoice keeps its lines.,approved
BIL-13,,An invoice total equals the sum of its lines.,approved
```

#### Resolution and the resolved model

- **Index.** The two lists are `[ElementRef]` properties, so `DocumentWalk` indexes them with no new code (a null list
  is skipped): `Field` is `satisfies` or `verifies`, `FromId` the element, sub-element or process node that lists the
  id.
- **Resolver.** `ResolveRun` builds one `RRequirement` per requirement and fills `RAnnotated.Satisfies`,
  `RProcessNode.Satisfies` and `RScenario.Verifies` from the lists (an absent list resolves empty).
  `RRequirement.SatisfiedBy` holds the resolved holders in (kind, name, id) order; `VerifiedBy` the scenarios in (name,
  id) order. Both record `r:<id>`. Requirements register before 4.1 links an operation's `Requires` (last of all), so
  a `requires` entry may name one.
- **Coverage.** `ModelStore.GetRequirementCoverageAsync` walks the requirement summaries and, per requirement,
  `ModelSnapshot.ReferencesTo(id)` with field `satisfies` or `verifies`, skipping a holder when `IsRetired` holds for
  its `FromId` or its `FromElementId`. Rows come in (package qualified name, name, id) order. `package` keeps the
  requirements of that package and its descendants.
- **Import.** `ModelStore.ImportRequirementsAsync` parses the text with `Csv` or the JSON reader, builds one batch of
  `create` and `update` operations (an update carries the hash of the requirement as read), and runs it through
  `PreviewBatchAsync` or `ApplyBatchAsync`. The answer pairs each row with its action and id.

#### Public API

```csharp
namespace Maquettiste.Engine.Model;
public enum ElementKind { /* existing */ [JsonStringEnumMemberName("requirement")] Requirement }   // appended
public sealed record Requirement : Element, IPackaged
{
    public override ElementKind Kind => ElementKind.Requirement;
    [ElementRef(ElementKind.Package)] public string? Package { get; init; }
    public string? ExternalId { get; init; }
    public required string Text { get; init; }
    public string? Citation { get; init; }
}
public abstract record ElementBase
{   [ElementRef(ElementKind.Requirement)] public IReadOnlyList<string>? Satisfies { get; init; } }   // null: absent
// ProcessState, ProcessTransition, ProcessEvent, ProcessGuard, ProcessAction, ProcessInvoke and ProcessGate gain:
//   [ElementRef(ElementKind.Requirement)] public IReadOnlyList<string> Satisfies { get; init; } = [];
public sealed record Scenario
{   [ElementRef(ElementKind.Requirement)] public IReadOnlyList<string> Verifies { get; init; } = []; }
namespace Maquettiste.Engine.Resolution;
public sealed class RRequirement : RElement
{
    public override string Kind => "requirement";
    public string? ExternalId { get; internal set; }
    public string Text { get; internal set; } = "";
    public string? Citation { get; internal set; }
    public RList<RObject> SatisfiedBy { get; internal set; } = RList<RObject>.Empty;
    public RList<RScenario> VerifiedBy { get; internal set; } = RList<RScenario>.Empty;
}
public abstract class RAnnotated
{   public RList<RRequirement> Satisfies { get; internal set; } = RList<RRequirement>.Empty; }
public abstract class RProcessNode
{   public RList<RRequirement> Satisfies { get; internal set; } = RList<RRequirement>.Empty; }
public sealed class RScenario
{   public RList<RRequirement> Verifies { get; internal set; } = RList<RRequirement>.Empty; }
public sealed class ResolvedModel { public RList<RRequirement> Requirements { get; internal init; } }
namespace Maquettiste.Engine;
public sealed record RequirementCoverageQuery(string? Package = null, string? Gaps = null, string? Cursor = null,
    int Limit = ModelPages.DefaultLimit);   // Gaps: unsatisfied, unverified or retired-cited; null for every row
public sealed record RequirementCoverage(RequirementCoverageTotals Totals, IReadOnlyList<RequirementCoverageRow> Items,
    string? Next);
public sealed record RequirementCoverageTotals(int Requirements, int Satisfied, int Verified, int Unsatisfied,
    int Unverified, int RetiredCited);
public sealed record RequirementCoverageRow(string Id, string Name, string? Package, string? Status, string? ExternalId,
    IReadOnlyList<string> Gaps, IReadOnlyList<CoverageLink> SatisfiedBy, IReadOnlyList<CoverageLink> VerifiedBy);
/// <param name="Name">The holder's longest form as a qualified name (@kind:form) when its kind has names.</param>
public sealed record CoverageLink(string ElementId, string Id, string Kind, string? Name, string JsonPointer);
public enum RequirementImportFormat { Csv, Json }
public sealed record RequirementImportRequest(string Text, RequirementImportFormat Format, string? Package = null,
    bool DryRun = true);
public sealed record RequirementImportResult(SaveOutcome Outcome, bool DryRun, IReadOnlyList<RequirementImportRow> Rows,
    IReadOnlyList<string> IgnoredColumns, IReadOnlyList<Diagnostic> Diagnostics);
/// <param name="Row">The CSV line (the header is line 1) or the JSON array index.</param>
/// <param name="Action">created, updated or unchanged.</param>
public sealed record RequirementImportRow(int Row, string ExternalId, string Action, string Id, string Name,
    IReadOnlyList<string> Fields);
public sealed partial class ModelStore
{
    public Task<RequirementCoverage> GetRequirementCoverageAsync(RequirementCoverageQuery query, CancellationToken ct);
    public Task<RequirementImportResult> ImportRequirementsAsync(RequirementImportRequest request, ChangeSource source,
        CancellationToken ct);
}
```

`RequirementRules` lives in `Validation/RequirementRules.cs`; the store members live in `ModelStore.Requirements.cs`.

#### Rules

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ3037 | E | Two requirements share an external id. | Change one |
| MQ3038 | I | A requirement that is neither proposed nor retired is satisfied by nothing (runs only when `validation.rules` gives it a severity). | Add `satisfies` where it is honoured |
| MQ3039 | I | A requirement that is neither proposed nor retired is verified by no scenario (runs only when `validation.rules` gives it a severity). | Add `verifies` to a scenario |
| MQ3040 | E | A requirement import is refused: a row without an external id, an external id the file repeats or two requirements share, a new requirement without text, an unknown status, or a value of the wrong form. | Fix the row |

MQ3040 comes with the import answer, one per refused row, naming its line or index and its column; nothing is written.
MQ2020 keeps its text and stops counting `satisfies` and `verifies` (decision 6). MQ2022 keeps its text and covers
retired requirements.

#### CLI and MCP

- **`get_requirement_coverage`** (new catalog tool, MCP and the assistant). Arguments `package`, `gaps`, `cursor`,
  `limit`. Answers `{totals, items, next}`; the totals always count the whole model or package.
- **`import_requirements`** (new). Arguments `text`, `format` (`csv` or `json`; absent: JSON when the text starts with
  `[`), `package`, `apply` (absent or false: preview). Answers the import result. Destructive, not idempotent, as
  `import_seed_csv` is.
- **`get_element`** adds `requirements` (decision 14); its `tools.json` description says so.
- **`get_resolved_model`** gains the scope `requirements`: one record per requirement with `externalId`, `text`,
  `citation`, `status`, `satisfiedBy` and `verifiedBy` ids. `all` includes it. Other records do not gain
  `satisfies`; `get_element` and `get_references` show it.
- **`apply_batch`, `create_element`, `save_element`** need no new parameter; their descriptions name `satisfies`,
  `verifies` and `@requirement:` names.
- **CLI.** A new group `requirement`, wired in `CliApp.cs` beside `seed` and `process`:
  - `maquettiste requirement coverage [--package <name or id>] [--check] [--format text|json]`: a table of name, status,
    satisfied and verified counts and gaps, then the totals; `--check` exits 2 when a gap exists.
  - `maquettiste requirement import <file.csv|file.json> [--package <name or id>] [--apply] [--format text|json]`: a
    preview of each row's action, written only with `--apply`; exit 1 when refused. The file extension picks the format.

No option is added to `CommandLine.ValueOptions`: `--package` and `--format` take values already, and `--check` and
`--apply` are flags (`src/Maquettiste.Cli/CommandLine.cs:17-31`).

Pins: `McpSurfaceTests.Tools` gains both names (E43 and E44); `AgentTools.All` grows by one in both of its pins (E43).

#### Templates

`each requirement` yields every requirement in (name, id) order; `where` filters on tags, stereotypes, categories and
packages apply. `requirement` joins `BuiltinHelpers.Variables` and the alias list of `RenderRun.CreateGlobals`.
`GenerationService.ScopeType` maps the scope to `RRequirement`. A traceability page reads
`{{ for holder in requirement.satisfied_by }}…{{ end }}`; an entity template reads `{{ entity.satisfies }}`.

#### Editor follow-ups

The generic JSON form covers requirements until these follow: a Requirements explorer with a coverage view, a
requirement picker for `satisfies` in the common fields and for `verifies` in the scenario editor, and an import dialog.
E40 gives the kind a label ("Requirement") and an icon only.

#### Docs

A new user-guide section "Requirements" after "Rationale, decision and status": the kind, the two links, coverage,
gating and import. `docs/mcp.md` rows for the tools above; `docs/releases.md` under `## 0.12.0 (unreleased)`; the
modeling skill tells agents to read `requirements` from `get_element` before they edit, and to add `satisfies` when they
build what a requirement asks.

**Not doing**

- Links between requirements (derive, refine, a parent requirement): packages group requirements, and such a link can
  come later without a format change.
- `satisfies` on keys, seed rows, categories, database schemas, gate meanings and scenario steps.
- Connectors to outside requirement tools (scope section 10): CSV or JSON import by external id instead.
- Retiring or deleting requirements an import leaves out: a batch does it, with a preview.
- Running scenarios inside the coverage report: `verify_scenarios` runs them.
- Translating `text`.
- Requirement kinds such as performance or security as product words: stereotypes and tags label them.

**Tests**

- `SchemaConsistencyTests` (existing); `CanonicalJsonTests.Absent_satisfies_and_verifies_keep_files_byte_identical`.
- `RequirementLinkTests` (new): `Satisfies_and_verifies_are_indexed_references`,
  `Satisfies_on_a_guard_and_a_column_are_indexed`, `A_cited_requirement_refuses_delete_and_remove_references_clears`,
  `Citing_a_retired_requirement_is_MQ2022`, `Links_do_not_count_for_layering`, `A_wrong_kind_in_satisfies_is_MQ2002`,
  `Batch_names_resolve_in_satisfies`, `Satisfies_on_a_scenario_or_a_migration_fact_is_MQ1002`.
- `FactRecordingTests`: `Satisfies_edits_resolve_no_database`, `Fact_hashes_are_unchanged_by_the_satisfies_member`.
- `RequirementRuleTests` (new): `Two_requirements_sharing_an_external_id_is_MQ3037`,
  `Coverage_rules_run_only_when_configured`, `Proposed_and_retired_requirements_have_no_gaps`,
  `A_save_that_adds_satisfies_revalidates_the_requirement`, `A_save_that_drops_the_last_satisfies_reports_MQ3038`.
- `RequirementResolutionTests` (new): `Requirements_resolve_with_satisfied_by_and_verified_by`,
  `Satisfies_reach_templates_on_elements_and_process_nodes`, `A_new_satisfier_re_renders_through_the_referrers_key`.
- `UnitPlannerTests.Each_requirement_plans_one_unit_per_requirement`;
  `ResolvedRecordsTests.Requirements_scope_lists_links`; `ResolvedCoverageTests` (existing).
- `RequirementCoverageTests` (new): `Coverage_counts_and_lists_gaps`, `Retired_holders_do_not_count`,
  `Nodes_of_a_retired_process_do_not_count`, `A_retired_requirement_still_cited_is_a_gap`,
  `Rows_page_and_filter_by_package`, `Coverage_reads_the_index_without_resolving`.
- `McpReadTests`: `Get_requirement_coverage_pages_and_filters`, `Get_element_lists_the_requirements_behind_it`;
  `AgentToolsTests` (the pin).
- `RequirementImportTests` (new): `Csv_rows_create_and_update_by_external_id`, `Json_rows_import_like_csv`,
  `A_preview_writes_nothing`, `Names_are_derived_and_made_unique`, `A_bad_row_refuses_the_import_with_MQ3040`,
  `Absent_columns_keep_fields_and_empty_cells_clear_them`, `A_second_import_changes_nothing`,
  `Package_values_resolve_like_batch_names`.
- `McpWriteTests.Import_requirements_previews_then_applies`.
- Cli `RequirementCommandTests` (new): `Coverage_prints_text_and_json`, `Coverage_check_exits_2_on_a_gap`,
  `Import_previews_and_applies_with_apply`.
- **Acceptance (3.10):**
  `Integration/RequirementTests.An_issued_invoice_keeps_its_lines_is_satisfied_and_verified_and_a_second_is_unverified`
  (new). Setup: an `E2ERepo` billing copy adds, in package Billing, the requirement `issued_invoice_keeps_lines`
  (above); an invented lifecycle `InvoiceLifecycle` of `Invoice` with a boolean context attribute `issued` (default
  false), states `draft` and `issued`, events `issue` and `change_lines`, a transition from `draft` to `issued` on
  `issue` whose action `mark_issued` sets `issued`, and a targetless `change_lines` transition on each state guarded by
  `not_issued` (`!context.issued`), whose `satisfies` lists the requirement; a scenario
  `issued_invoice_refuses_line_change` that sends `issue`, then `change_lines`, expects `issued` after both steps and
  `verifies` the requirement; and a second requirement `invoice_total_matches_lines` that Invoice's `total` attribute
  satisfies and no scenario verifies. Expected: validate is clean; `verify_scenarios` passes; coverage totals are 2
  requirements, 2 satisfied, 1 verified, 1 unverified; `--gaps unverified` lists only `invoice_total_matches_lines`;
  `requirement coverage --check` exits 2; with MQ3039 set to `error`, validate reports one error, on the second
  requirement; `get_element` on the process lists the first requirement with its text.

**Tasks**

- **E40. The kind and the two links.** Runs after E20, E50, E51, E52 and 4.1's E1 (they edit the kind registry,
  `custom.json`, `process.json` and `operation.json`, which gain `satisfies` here; E51 rewrites the recorded rule list,
  as this task's `record` does; E52 edits `openapi.yaml` and `ResolvedCoverageTests`). Owns: `Model/ElementKind.cs`,
  `Model/KindInfo.cs`, `Model/Requirement.cs` (new), `Model/ElementBase.cs` (`Satisfies`, the derived type),
  `Model/Processes.cs` (`Satisfies` on seven node records, `Scenario.Verifies`), `requirement.json` (new), `common.json`
  (`requirementIds`, the attribute, `tableColumn`), every kind schema of decision 3 (`custom.json` and `operation.json`
  included), `enum.json`, `table.json` (the table), `process.json`, `scenario.json`, `extension.json` and
  `stereotype.json`, with their copies; the annotation lists in `ModelStore.Migrations.cs`; the layering skip in
  `Validation/PackageRules.cs`; `ResolvedCoverageTests` (a target for `requirement.json`; `Pending` for `satisfies` and
  `verifies` until E42); `openapi.yaml` (`ElementKind`, the element union); `src/editor/src/model/labels.ts` and
  `src/editor/src/app/icons.tsx` (the kind's label and icon). Tests: `SchemaConsistencyTests`, `CanonicalJsonTests`,
  `RequirementLinkTests`, `FactRecordingTests`, `PackageLayeringTests`. Verify:
  `E "~Json.|~RequirementLinkTests|~FactRecordingTests|~PackageLayeringTests|~ElementStatusTests|~ResolvedCoverage"`;
  `P "~GoldenTests"`; `F "~ContractTests"`; `record`; `editor`; `copies`.
- **E41. Requirement rules.** Runs after E40, E51 and E52 (E51 edits `RuleCatalog.cs`, E52
  `Validation/ModelValidator.cs`). Owns: `Validation/RequirementRules.cs` (new), `Validation/ModelValidator.cs` (wiring
  and the two `ConflictPeers` cases), `ModelStore.cs` (the requirements the changed files named before a save, beside
  `FormerReferrers`), `RuleCatalog.cs` (MQ3037 to MQ3039), `CatalogTests.cs` (+3). Tests: `RequirementRuleTests`.
  Verify: `E "~CatalogTests|~RequirementRuleTests|~ValidationFixtureTests|~ModelValidatorTests|~ModelStoreTests"`;
  `record`.
- **E42. Resolution and templates.** Runs after E40, E26 and E52 (E26 and E52 edit `Agent/tools.json`,
  `openapi.yaml`, `Resolution/ResolvedCore.cs` and `Editor/ResolvedRecords.cs`). Owns:
  `Resolution/ResolvedRequirements.cs` (new), `Resolution/ResolveRun.Requirements.cs` (new), `Resolution/ResolveRun.cs`
  (wiring), `Resolution/ResolvedCore.cs` (`RAnnotated.Satisfies`, `Requirements`), `Resolution/ResolvedProcesses.cs`
  (`RProcessNode.Satisfies`, `RScenario.Verifies`), `Resolution/ResolveRun.Conceptual.cs` and
  `Resolution/ResolveRun.Processes.cs` (filling them), `Planning/UnitPlanner.cs`, `Planning/UnitRules.cs`,
  `GenerationService.Authoring.cs` (`ScopeType`), `Rendering/BuiltinHelpers.cs` and `Rendering/RenderRun.cs` (the
  variable), `Editor/ResolvedRecords.cs` (`RequirementRecord`, scope `requirements`), `ResolvedCoverageTests` (every
  target resolves `satisfies`, or excuses it in `NotResolved` with its reason), `pack.json` with its copy, the
  `get_resolved_model` text in `Agent/tools.json`, the resolved scope enum and `RequirementRecord` in `openapi.yaml`.
  Tests: `RequirementResolutionTests`, `UnitPlannerTests`, `ResolvedRecordsTests`. Verify:
  `E "~Resolution.|~Planning.|~ResolvedRecordsTests|~ResolvedCoverage"`; `P "~GoldenTests"`; `C "~McpSurfaceTests"`;
  `F "~ContractTests"`; `editor`; `copies`.
- **E43. Coverage.** Runs after E42 (both edit `Agent/tools.json`). Owns: `ModelStore.Requirements.cs` (new: coverage
  and its records), `Agent/AgentTools.cs` and `Agent/AgentTools.Handlers.cs` (`get_requirement_coverage`,
  `requirements` on `get_element`), `Agent/tools.json`, `src/Maquettiste.Cli/Commands/RequirementCommand.cs` (new:
  `coverage`), `src/Maquettiste.Cli/CliApp.cs` (the group and its help line), the pins in
  `tests/Maquettiste.Cli.Tests/McpServerTests.cs` (`Tools` +1, `AgentTools.All` +1) and
  `tests/Maquettiste.Engine.Tests/Editor/AgentToolsTests.cs` (`AgentTools.All` +1), `README.md` (the tool count), the
  `docs/mcp.md` rows of `get_requirement_coverage` and `get_element`. Tests: `RequirementCoverageTests`,
  `McpReadTests`, `AgentToolsTests`, `RequirementCommandTests`. Verify:
  `E "~RequirementCoverageTests|~AgentToolsTests"`; `C "~McpSurfaceTests|~McpReadTests|~RequirementCommandTests"`.
- **E44. Import.** Runs after E43 and E41 (E41 edits `RuleCatalog.cs`). Owns: `ModelStore.Requirements.cs` (import),
  `RuleCatalog.cs` (MQ3040), `CatalogTests.cs` (+1), `src/Maquettiste.Cli/Mcp/ModelTools.cs` (`import_requirements`,
  and the `apply_batch`, `create_element` and `save_element` texts), `RequirementCommand.cs` (`import`), the `Tools`
  pin in `McpServerTests.cs` (+1), `README.md` (the tool count), the `docs/mcp.md` rows of `import_requirements` and the
  three write tools. Tests:
  `RequirementImportTests`, `McpWriteTests`, `RequirementCommandTests`. Verify:
  `E "~CatalogTests|~RequirementImportTests|~ModelStoreBatchTests"`;
  `C "~McpSurfaceTests|~McpWriteTests|~RequirementCommandTests"`; `record`.
- **E45. Docs and acceptance for requirements.** Runs after E41, E42, E44 and E27 (E27 edits the same docs). Owns: the
  user-guide section, `docs/releases.md` (the requirements lines under `## 0.12.0 (unreleased)`),
  `skills/maquettiste-modeling/SKILL.md` (requirements), `Integration/RequirementTests.cs` (new). Tests: the
  acceptance test. It reads the tool rows E42 to E44 wrote and checks them against the tool texts, editing neither.
  Verify: `E "~RequirementTests"`; `C "~SkillRefreshTests|~AgentSetupTests|~McpSurfaceTests"`, where the skill hash
  changes on purpose.

E46 to E49 are free.

### 4.4 Shared guards, actions and gate policies

**Decision 9 was built first, and went further (the owner's decision of 2026-11-05).** Beside guards, actions, invokes and
gates, the kinds `check` (an entity's check, a table's check, a stereotype's check) and `constraint` take custom properties,
and an operation's guards are holders under `guard`. A table's check holds `properties` only. Line numbers below are from
when this section was written.

**Why every team**

Once a team models more than one process, the same checks and effects recur: an amount must be positive, a record must
not be locked, a notification goes out, two people from finance sign off. Today each process must declare its own copy,
so copies drift apart and a fix lands in one place only. A library process holds guards, actions and gate policies once,
in a package, and any process uses them by id. Guards, actions, invokes and gates also take properties and stereotypes,
so a team can state its own facts about them (an owner, a timeout, a cost centre) in its own words, exactly as it
already does on states, transitions and events.

**Today**

- MQ9014 refuses any reference to a state, event, guard, action or invoke of another process
  (`src/Maquettiste.Engine/Validation/ProcessRules.cs:746-760`, `Diagnostics/RuleCatalog.cs:307`).
- A process requires states, at least one (`schemas/v1/process.json:131-137`, `:163-168`, `Model/Processes.cs:56`),
  and `use` is `lifecycle` or `orchestration` (`Model/Processes.cs:68-75`). Every process that is not an orchestration
  is judged as a lifecycle (`Validation/ProcessRules.cs:614`), and the index row maps any other use to `orchestration`
  (`Model/ModelIndexer.cs:519`); the API row's `use` is an enum of those two (`docs/api/openapi.yaml:7592`).
- A gate lives inline on its transition (`Model/Processes.cs:205`), and the chart reads it there
  (`Processes/StatechartModel.cs:142`). Signatures are kept per transition id
  (`Processes/StatechartInterpreter.cs:756`). A gate's name form comes from its transition
  (`Loading/NameIndex.cs:313-321`), and the validator lists gate ids through transitions
  (`Validation/ModelValidator.cs:721-728`, `:794-797`).
- The chart and the compiled expressions read only the process's own guards and actions
  (`Processes/StatechartModel.cs:184-199`, `Processes/ProcessExpressions.cs:49-52`), and both caches key on the process
  alone (`Processes/StatechartModel.cs:259-270`, `Processes/ProcessExpressions.cs:158-165`). Charts are built in
  `Processes/ScenarioReplayer.cs:42`, `Resolution/ResolveRun.Processes.cs:121`, `Processes/ProcessApi.cs:303` and
  `src/Maquettiste.Cli/Commands/ProcessCommand.cs:124`; expressions are read in
  `Processes/StatechartInterpreter.cs:483`, `:490`, `:528`, `Processes/ScenarioReplayer.cs:52`,
  `Processes/ProcessApi.cs:538` and, for MQ9501, `Validation/ScenarioRules.cs:22`.
- Guards, actions, invokes and gates have no `stereotypes` or `properties` (`schemas/v1/process.json:423-629`,
  `Model/Processes.cs:272-391`). Extension and stereotype `appliesTo` lists stop at `event`
  (`schemas/v1/extension.json:25-55`, `schemas/v1/stereotype.json:95-126`), `PropertyHost.Of` stops at events
  (`Model/PropertySchemas.cs:265-306`), the holders with translations stop there too (`:899-900`), and node stereotypes
  are checked on states, transitions and events only (`Validation/ProcessRules.cs:130-149`). The resolved node already
  has `Stereotypes` and `Properties` for every node kind (`Resolution/ResolvedProcesses.cs:78-82`).
- MQ9013 counts uses inside the process only (`Validation/ProcessRules.cs:493-530`). `RGuard.UsedBy` and
  `RAction.UsedBy` list the process's own users (`Resolution/ResolvedProcesses.cs:240`, `:262`), and `RGate.Transition`
  is one transition (`:297`), which the API record requires (`docs/api/openapi.yaml:9566`).

**Decisions taken here**

1. **A library is a process with `use: library`.** It holds guards, actions and gates, the last in a new top-level
   `gates` list. It holds nothing that runs: no states, initial, transitions, context, events, subject or bound
   attribute, and its actions raise no events (MQ9020). `states` leaves `required` in `process.json`, drops `minItems`
   and gains `default: []`, so a library record written back carries no `states` key; a process that is not a library
   and has no states is MQ9020 too, with a clearer message than a schema violation.
2. **Uses go where they go today.** `transition.guard`, `transition.actions`, `state.entry` and `state.exit` may name a
   library's guard or action by id. MQ9014 allows those and nothing else from another process. `gatePolicy` has its own
   rule (MQ9021).
3. **Gate policies.** A library gate is a whole gate: required count, signers, required actors, repeat signing, reasons,
   meanings and audit attributes. A transition uses one through the new key `gatePolicy`, a gate id, instead of an
   inline `gate`; both on one transition, or a policy that names a gate no library holds, is MQ9021. MQ9103 counts a
   policy as a gate. Signatures stay per transition, so one policy on several transitions keeps separate counts, and a
   scenario step's `meaning` may name a meaning of the policy its transition uses.
4. **Library references are index references.** Layering (MQ2020), delete refusal, `remove-references`, MQ2022 and
   `get_references` see them with no new code. A library in package Billing serves processes of packages that depend on
   Billing.
5. **Library expressions run in the using process.** A library guard or action reads the using process's `context` and
   `event`; it is compiled into the using process's expressions under its own id. `ProcessExpressions.Problems` keeps
   listing the process's own expressions only, so MQ9501 for a library expression is reported on the library. Nothing
   checks the context names a library expression reads: the expression core (scope 3.8, phase 3) types them.
6. **Names stay unique where code meets them.** Guard, action and gate names are unique across a process's own nodes and
   the library members it uses (MQ3001, text unchanged), so a pack never generates two methods of one name.
7. **What a save revisits.** Unused library members report MQ9013 on the library when no process uses them, counting
   index references from every process. A save of a library validates the processes that use it, as referrers; a save
   of a using process does not revalidate its libraries, and a library save does not replay the scenarios of the
   processes that use it. So a stale MQ9013 (a warning) or a stale scenario result clears at the next whole-model
   validation, and `verify_scenarios` replays at once.
8. **Nothing runs a library.** A process invoke and a scenario never name one (MQ9022). `simulate_process`,
   `record_scenario`, `export_process` and `sync_enum_from_process` refuse a library as a process that cannot run, with
   their existing problem codes (`bad-request`, MQ9019 for the enum sync). A library is created like any element, with
   `create_element` or `apply_batch`; `import_process` keeps refusing `use: library`.
9. **Properties and stereotypes on four more nodes.** Guards, actions, invokes and gates gain `stereotypes` and
   `properties`. `PropertyHost.Of` yields them with kind names `guard`, `action`, `invoke` and `gate` (library gates and
   inline gates alike), so extension files, stereotype parameters and `x-ref` reach them. Extension `appliesTo.kinds`
   and stereotype `appliesTo` gain the four names. MQ2003 and MQ2004 judge their stereotypes as they judge states'.
   Their translatable property texts join localization, which already knows these nodes
   (`Localization/LocaleChains.cs:14-16`) and finds property texts through `PropertyHost.Of`
   (`Localization/LocalizationIndex.cs:744`); `TranslatedHolderKinds` gains the four names, so MQ7213 stays quiet on
   them.
10. **Packs that turn charts into code are safe.** `ResolvedModel.Processes`, and so `each process`, keep lifecycles and
    orchestrations only. Libraries are in `ResolvedModel.ProcessLibraries` and in `RProcess.Libraries` of each process
    that uses one. The existing `RGuard.UsedBy` and `RAction.UsedBy` now list users across processes. `RGate.UsedBy`
    lists the transitions that use a gate, and `RGate.Transition` is null for a library gate. A using process's `Gates`
    holds the library gates its transitions use, once each, in transition priority order. The `processes` scope of
    `get_resolved_model` lists libraries with `use: library`.
11. **The chart reads its libraries.** `ProcessLibraries.For` collects, from the index, the library guards, actions and
    gates a process references, with a hash of those library documents. `StatechartModel.Get` and
    `ProcessExpressions.Get` take it, and its hash joins their cache keys, so a library edit re-keys every chart that
    uses it.
12. **Interchange stays local.** `export_process` writes a library guard or action by name like a local one;
    `import_process` creates local nodes and never library references.

#### Schema and files

`process.json`, with its copy:

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| `use` | enum gains `library` | `orchestration` | |
| `gates` (after `actions` in `x-order`) | array of `#/$defs/gate` | `[]` | Library gates (MQ9020 elsewhere) |
| `states` | `minItems` 1 dropped | `[]` | Leaves `required` (MQ9020) |
| transition `gatePolicy` (after `gate`) | `common.json#/$defs/id` | absent | A library gate (MQ9021) |
| guard, action, invoke, gate: `stereotypes`, `properties` (after `description`) | `keyList`; `common.json#/$defs/properties` | `[]`; `{}` | Absent values write nothing |

`extension.json` and `stereotype.json` add `guard`, `action`, `invoke` and `gate` to their `appliesTo` lists, with their
copies. `docs/api/openapi.yaml` follows: `ElementSummary.use` gains `library` (E50), and `GateRecord.transition` becomes
nullable beside a new `usedBy` (E52).

```json
{
  "kind": "process",
  "id": "01JF…L1",
  "name": "BillingChecks",
  "package": "<id of package Billing>",
  "use": "library",
  "guards": [ { "id": "01JF…G1", "name": "amount_positive", "expression": "context.amount > 0" } ]
}
```

A transition of `InvoiceLifecycle` then says `"guard": "01JF…G1"`, and a transition of an invented `PaymentFlow` says
the same.

#### Resolution and the resolved model

- **Validation.** `ProcessRules.CheckProcess` (`Validation/ProcessRules.cs:59-71`) runs, for a library, MQ9020, the node
  names, the node stereotypes, MQ9013 and the gate rules MQ9101, MQ9102 and MQ9104; nothing that needs states.
  `CheckLifecycle` runs for lifecycles only. `CheckLocal` (`:747-752`) accepts a guard or action whose owner is a
  library; `gatePolicy` is checked by MQ9021. `ProcessNodeIds` (`Validation/ModelValidator.cs:700-730`) adds a
  library's gates with their meanings and audit attributes, so a library save reaches the transitions that use them,
  and `UsesType` (`:794-797`) reads those audit attributes too.
- **Index rows and names.** `ModelIndexer.Summarize` (`Model/ModelIndexer.cs:519`) writes `library` for a library.
  `NameIndex` gives a library gate, its meanings and its audit attributes the forms `<library form>.<gate>` and on, as
  it gives a transition's gate today.
- **Chart.** `ChartTransition.Gate` becomes the inline gate, else the policy's gate from `ProcessLibraries`;
  `StatechartModel` builds `Guards` and `Actions` from the process's own and its libraries'. The replayer reads
  `ChartTransition.Gate` (`Processes/ScenarioReplayer.cs:258-259`), so it follows a policy with no change of its own.
  The expression reads listed under Today pass `inst.Chart.Libraries` or `chart.Libraries`, and every chart built
  there passes `ProcessLibraries.For`. The refusals of decision 8 sit in `ModelStore.ProcessApi.cs`.
- **Resolver.** `ResolveRun.Processes` resolves libraries first, so a using process's transitions point at the library's
  `RGuard`, `RAction` and `RGate`. Each using process records the `e:` keys of its libraries.

#### Public API

```csharp
namespace Maquettiste.Engine.Model;
public enum ProcessUse { /* existing */ [JsonStringEnumMemberName("library")] Library }
public sealed record Process : Element, IPackaged
{   public IReadOnlyList<ProcessState> States { get; init; } = [];      // no longer required
    public IReadOnlyList<ProcessGate> Gates { get; init; } = [];        // library gates
}
public sealed record ProcessTransition { [ElementRef(IndexKinds = ["gate"])] public string? GatePolicy { get; init; } }
// ProcessGuard, ProcessAction, ProcessInvoke and ProcessGate gain:
//   [StereotypeKeys] public IReadOnlyList<string> Stereotypes { get; init; } = [];
//   public IReadOnlyDictionary<string, JsonElement> Properties { get; init; }
//       = ImmutableDictionary<string, JsonElement>.Empty;
namespace Maquettiste.Engine.Processes;
/// <summary>The guards, actions and gates of library processes that one process references, by id.</summary>
public sealed record ProcessLibraries(FrozenDictionary<string, ProcessGuard> Guards,
    FrozenDictionary<string, ProcessAction> Actions, FrozenDictionary<string, ProcessGate> Gates, string Hash)
{
    public static ProcessLibraries Empty { get; }
    public static ProcessLibraries For(ModelSnapshot snapshot, Process process);
}
public sealed partial class StatechartModel
{
    public static StatechartModel Get(Process process, string? hash, ProcessLibraries? libraries = null);
    public ProcessLibraries Libraries { get; }
}
public sealed class ChartTransition { public ProcessGate? Gate { get; } }   // inline, else the gatePolicy's gate
public sealed class ProcessExpressions
{   public static ProcessExpressions Get(Process process, ProcessLibraries? libraries = null); }
namespace Maquettiste.Engine.Resolution;
public sealed class RProcess { public RList<RProcess> Libraries { get; internal set; } = RList<RProcess>.Empty; }
public sealed class RGate
{
    public RTransition? Transition { get; internal set; }               // null for a library gate
    public RList<RTransition> UsedBy { get; internal set; } = RList<RTransition>.Empty;
}
public sealed class ResolvedModel { public RList<RProcess> ProcessLibraries { get; internal init; } }
```

#### Rules

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ9020 | E | A library process holds what only a running process has (states, an initial, transitions, context, events, a subject or a bound attribute, or an action that raises events), or a process that is not a library has no states or lists library gates. | Move the item, or change `use` |
| MQ9021 | E | A transition sets both `gate` and `gatePolicy`, or its `gatePolicy` names a gate that no library holds. | Keep one, or point at a library gate |
| MQ9022 | E | A process invoke or a scenario names a library process, which has no states to run. | Name a running process |
| MQ9013 | W | Changed text: a library's guard, action or gate counts as used when any process uses it. | Use it or remove it |
| MQ9014 | E | Changed text: references to a library's guards and actions are allowed. | Declare it here or move it to a library |
| MQ9103 | E | Changed text: a gate policy counts as a gate. | Keep one gate |

MQ3001 and MQ2004 keep their text; their reach grows as decisions 6 and 9 say.

#### CLI and MCP

No tool or verb is added. `simulate_process`, `verify_scenarios`, `record_scenario`, `export_process` and
`sync_enum_from_process` describe libraries in their texts (`src/Maquettiste.Cli/Mcp/ProcessTools.cs`) and
`docs/mcp.md` rows. `get_references` on a library guard lists every transition that uses it. `get_model_index` rows of
libraries carry `use: library`. `maquettiste process simulate` prints library guard and action names in its trace.

#### Templates

`each process` does not change. A template that generates shared code reads `model.process_libraries`, or
`process.libraries` from a process unit, and each library's `guards`, `actions` and `gates`. `guard.used_by` spans
processes. Guards, actions, invokes and gates carry `stereotypes` and `properties` in templates, as other nodes do.

#### Editor follow-ups

The JSON view covers libraries until these follow: a library view without a canvas, library members in the guard, action
and gate pickers of the process editor, and stereotypes and properties in the inspector of guards, actions, invokes and
gates.

#### Docs

User guide: a subsection "Libraries: shared guards, actions and gate policies" in "Processes, actors and scenarios", and
the three rules in "The rules for processes (MQ9xxx)". `docs/guides/processes.md` gains a short step that moves a guard
into a library. `docs/releases.md` under `## 0.12.0 (unreleased)`.

**Not doing**

- Shared events, invokes or states: they belong to a running chart.
- Library actions that raise events: events are local to a process.
- A typed contract between a library expression and the context it reads: the expression core (3.8) brings it.
- Overriding part of a gate policy on one transition: define a second policy.
- Library references in the chart interchange format of `export_process` and `import_process`.
- Replaying the scenarios of every using process when a library is saved (decision 7).

**Tests**

- `SchemaConsistencyTests` (existing);
  `CanonicalJsonTests.Absent_node_stereotypes_properties_gates_and_policies_keep_files_byte_identical`,
  `CanonicalJsonTests.A_library_record_written_back_has_no_states_key`.
- `PropertySchemaSetTests.Guards_actions_invokes_and_gates_are_property_hosts`;
  `LocalizationRuleTests.X_translatable_for_a_guard_is_not_MQ7213`.
- `BatchNamesTests.Library_gates_and_meanings_have_name_forms`.
- `ProcessLibraryRuleTests` (new): `A_library_holding_states_or_events_is_MQ9020`,
  `A_process_without_states_that_is_not_a_library_is_MQ9020`, `A_library_action_that_raises_is_MQ9020`,
  `Gate_and_gate_policy_on_one_transition_is_MQ9021`, `An_invoke_or_a_scenario_naming_a_library_is_MQ9022`,
  `A_gate_policy_naming_an_inline_gate_is_MQ9021`, `Library_guards_and_actions_pass_MQ9014`,
  `A_guard_of_another_running_process_is_still_MQ9014`, `A_gate_policy_counts_for_MQ9103`,
  `An_unused_library_member_is_MQ9013`, `A_used_library_name_clashing_with_an_own_guard_is_MQ3001`,
  `Node_stereotypes_are_checked_on_all_four_kinds`, `A_library_is_not_judged_as_a_lifecycle`,
  `A_library_expression_that_does_not_parse_is_MQ9501_on_the_library`,
  `Saving_a_library_revalidates_the_processes_that_use_its_gates`.
- `ProcessLibraryRuntimeTests` (new): `A_library_guard_runs_with_the_using_process_context`,
  `A_gate_policy_counts_signatures_per_transition`, `A_library_edit_re_keys_the_chart_and_expression_caches`,
  `Simulate_record_export_and_enum_sync_refuse_a_library`.
- `ScenarioReplayTests.A_step_meaning_of_a_gate_policy_replays`.
- `ProcessResolutionTests`: `Libraries_resolve_apart_from_processes`, `Used_by_spans_processes`,
  `A_library_gate_has_no_transition_and_lists_its_users`.
- `ElementSummaryTests.A_library_row_says_library`.
- **Acceptance (4.2):** `Integration/SharedGuardTests.An_amount_positive_guard_exists_once_and_two_processes_use_it`
  (new). The scope's `amount-positive` is written `amount_positive`, since guard names are identifiers. Setup: an
  `E2ERepo` billing copy adds, in package Billing, the library `BillingChecks` above; an invented lifecycle
  `InvoiceLifecycle` of `Invoice` and an invented orchestration `PaymentFlow`, each with a context attribute `amount`
  and one transition guarded by `amount_positive`; and an invented stereotype `finance-check` with `appliesTo: [guard]`
  on the guard. Expected: validate is clean; `get_references` on the guard lists both transitions; `simulate_process`
  refuses each transition with `amount` 0 and takes it with 10; the resolved guard's `used_by` holds both transitions;
  deleting the guard is refused with both transitions named; and a transition of `InvoiceLifecycle` that names a guard
  declared in `PaymentFlow` itself still reports MQ9014.

**Tasks**

- **E50. Libraries and node annotations in the schema.** Runs after E26, so that every task of section 4.2 that edits
  `PropertySchemas.cs`, `ModelIndexer.cs`, `NameIndex.cs`, `openapi.yaml`, `ResolvedCoverageTests` or the `appliesTo`
  lists has landed. Owns: `process.json`, `extension.json` and `stereotype.json` with their copies,
  `Model/Processes.cs`, `Model/PropertySchemas.cs` (`PropertyHost.Of`, `TranslatedHolderKinds`),
  `Model/ModelIndexer.cs` (`Summarize` use), `Model/Documents.cs` (the `Use` documentation), `Loading/NameIndex.cs`
  (library gates), `openapi.yaml` (`ElementSummary.use`), `ResolvedCoverageTests` (`Pending` for the new members until
  E52). Tests: `SchemaConsistencyTests`, `CanonicalJsonTests`, `PropertySchemaSetTests`, `LocalizationRuleTests`,
  `BatchNamesTests`, `ElementSummaryTests`. Verify:
  `E "~Json.|~PropertySchemaSetTests|~LocalizationRuleTests|~BatchNamesTests|~ProcessModelTests|~ElementSummaryTests"`;
  `E "~ResolvedCoverage"`; `P "~GoldenTests"`; `F "~ProcessEndpointTests|~ContractTests"`; `record` (process element
  bodies in the recorded editor mocks may gain the new keys, intended); `editor`; `copies`.
- **E51. Library rules.** Runs after E50. Owns: `Validation/ProcessRules.cs`, `Validation/ScenarioRules.cs` (MQ9022 on
  scenarios), `RuleCatalog.cs` (MQ9020 to MQ9022; MQ9013, MQ9014 and MQ9103 texts), `CatalogTests.cs` (+3). Tests:
  `ProcessLibraryRuleTests`. Verify:
  `E "~CatalogTests|~ProcessLibraryRuleTests|~ProcessRuleTests|~ValidationFixtureTests"`; `record`.
- **E52. Charts, replay and the resolved model.** Runs after E50. Owns: `Processes/ProcessLibraries.cs` (new),
  `Processes/StatechartModel.cs`, `Processes/ProcessExpressions.cs`, `Processes/StatechartInterpreter.cs`,
  `Processes/ScenarioReplayer.cs`, `Processes/ProcessApi.cs`, `Processes/XStateExporter.cs`,
  `ModelStore.ProcessApi.cs` (the refusals), `src/Maquettiste.Cli/Commands/ProcessCommand.cs` (the chart it prints
  from), `Validation/ModelValidator.cs` (`ProcessNodeIds`, `UsesType`), `Resolution/ResolvedProcesses.cs`,
  `Resolution/ResolveRun.Processes.cs`, `Resolution/ResolvedCore.cs` (`ProcessLibraries`), `Editor/ResolvedRecords.cs`
  (libraries in the `processes` scope, `GateRecord`), `ResolvedCoverageTests`, `openapi.yaml` (`GateRecord`), the
  texts of the five process tools in `src/Maquettiste.Cli/Mcp/ProcessTools.cs` and their `docs/mcp.md` rows. Tests:
  `ProcessLibraryRuntimeTests`, `ScenarioReplayTests`, `ProcessResolutionTests`. Verify:
  `E "~Processes.|~ProcessLibraryRuntimeTests|~ScenarioReplayTests|~ProcessResolutionTests|~ResolvedCoverage"`;
  `E "~ProcessGenerationTests|~InterpreterConformanceTests"`; `P "~GoldenTests"`;
  `C "~ProcessCommandTests|~McpSurfaceTests"`; `F "~ProcessEndpointTests|~ContractTests"`; `editor`.
- **E53. Docs and acceptance for libraries.** Runs after E51, E52 and E45 (E45 edits the same docs). Owns: the
  user-guide subsection and rule rows, the `docs/guides/processes.md` step, `docs/releases.md` (the library lines under
  `## 0.12.0 (unreleased)`), `skills/maquettiste-modeling/SKILL.md` (libraries), `Integration/SharedGuardTests.cs`
  (new). Tests: the acceptance test. Verify: `E "~SharedGuardTests"`; `C "~SkillRefreshTests|~AgentSetupTests"`, where
  the skill hash changes on purpose.

E54 to E59 are free.

## 5. Milestone F: packs, scripts and rules

### 5.1 DDL as structured steps from the engine

**Why every team**

Every team that keeps a PostgreSQL database in its model needs the DDL that builds and changes that database to be
right, complete and in an order the server accepts, whichever tool writes it. Today that knowledge lives in the
templates of one pack. A team that writes its own pack has to copy it, an agent that wants the DDL of one table has to
render a pack and read files, and the live planner of phase 3 would need a third copy. Built once in the engine as
typed steps, the DDL serves every pack, every agent and the planner alike: a pack keeps only its conventions (file
layout, headers, transactions, reference data), an agent previews one element's DDL with no pack at all, and the
planner reuses a core that the pack's goldens already prove byte for byte.

**Today**

- The engine has the SQL pieces around DDL, not the DDL of the model's objects: `sql_quote`, `sql_literal`,
  `type_of`, `query_sql`, `binding_sql`, `ledger_ddl` and `toposort` (`Rendering/BuiltinHelpers.cs:183-202`), quoting
  and literals in the internal `SqlDialects` (`Rendering/SqlDialects.cs:13`), and two public statement renderers,
  `QuerySql` (`Rendering/QuerySql.cs:43`) and `BindingSql` (`Rendering/BindingSql.cs:27`).
- `schema_diff` is `SchemaDiffResult` (`SchemaDiff/SchemaDiffTypes.cs:133`), with the migration facts that render
  (next-phase-1-design.md section 3.2). The differ is public behind `ISchemaDiffer` (`Pipeline/Support.cs:266-279`).
  Seed order is `RDatabase.SeedTables` (`Resolution/ResolvedPhysical.cs:14`) and `SeedOrder`
  (`Resolution/SeedOrder.cs:14`). The ledger DDL is `LedgerContract.Ddl` (`Ledger/LedgerContract.cs:50`).
- CREATE, ALTER, DROP and their order live in `packs/sql-ddl`: 5,474 lines in 19 templates and `helpers.js`, five
  dialects in one set of functions. `schema.scriban:20-26` orders tables through a text graph
  (`_objects.scriban:6-25`) and the pack's JavaScript `ddl_order` and `ddl_cycle_breaks` (`helpers.js:45`, `:49`).
  `migration.scriban` collects statements in phases (`:288`, `:343`, `:351`) and joins them with blank lines
  (`:525-527`). Fact steps are PostgreSQL only (`migration.scriban:57-66`, `_migration.scriban:2112`); the step of a
  `value-map` or `state-map` fact is a TODO line (`_migration.scriban:2124-2125`).
- A pack convention reaches column types: `column_type` (`_sql.scriban:339-345`) writes the pack's enum type for a
  column whose reference type the pack realizes as `native`, and the pack's enum prelude goes before the tables
  (`schema.scriban:18`, `:90-93`; `migration.scriban:284-287`).
- The foreign keys that close a cycle are left out of their `CREATE TABLE` by constraint name, not by table
  (`_objects.scriban:38-49`, `_table.scriban:37`): another table's key of the same name is left out too, and never
  added.
- A migration finds the model view or sequence of a diff change by name (`migration.scriban:195`, `:396`, `:423`,
  `_migration.scriban:1401`), so of two views of one name in different schemas it finds the first.
- Ledger fact rows name their writer as the literal `'sql-ddl'` (`_ledger.scriban:34`).
- Templates read resolved types, renderer views and the schema-diff types (`Rendering/TemplateValues.cs:71-74`);
  scripts read resolved types only (`Scripting/MemberCatalog.cs:74-77`). A unit may carry a warning that does not fail
  it (MQ6011, `Rendering/RenderRun.cs:330-335`).
- The editor's DDL preview renders a pack unit it picks by the unit's name (one that mentions schema or table), and
  shows the whole database when that unit writes nothing for an object
  (`src/editor/src/workspaces/database/ddlPreview.ts:1-8`).
- The oracle exists: 326 golden files under `tests/fixtures/golden/sql-ddl/`, 144 of them scripts of PostgreSQL
  databases.

**Decisions taken here**

1. **PostgreSQL only.** The engine builds steps for PostgreSQL. SQL Server, MySQL, SQLite and Oracle keep the pack's
   template path unchanged; `ddl_steps` on one of their databases is MQ6040. A step kind a later section adds is
   PostgreSQL only too, and that section grows MQ4056's text. A builder for another dialect follows this shape when
   someone needs it.
2. **One step per block the pack writes today.** Each block that sql-ddl separates with a blank line becomes one
   top-level step; the lines it joins with a single line feed become the step's children. This one-to-one mapping
   makes the port checkable against the goldens, block by block.
3. **The builder writes the default text as it builds.** Every step carries its PostgreSQL text (`Sql`), computed from
   the same decisions that shaped the step; `ddl_sql` and `DdlSql.Render` only join. A pack that wants other text
   reads the typed fields and writes its own.
4. **Names as they stand.** A step names its object as it is when the step runs: after the schema renames, table
   renames and moves earlier in the same tree. The planner of phase 3 needs exactly that, and so does a pack's own
   text.
5. **Ids, not names.** The builder finds the model object of a diff change by its key: the element id of a view,
   sequence, routine, database type or SQL object, the derived key of a constraint or index within its table
   (`SchemaDiff/README.md`). It never looks an object up by name (difference 3). A diff names its database by id too:
   `SchemaDiffResult` gains `DatabaseId`, which the differ sets, so `ddl_steps` finds a diff's database by id.
6. **Conventions stay in the pack; the ledger is in the tree.** Transaction control, headers, labels, file names,
   include directives and reference data stay with the pack. The ledger DDL, the fact rows and the run-once guards are
   steps, so a tree is complete for any writer, the planner included. The writer's name is an option.
7. **Options are data.** `Comments`, `Idempotent`, `Quoting`, `Environment`, `AppliedBy`, `ReferenceTypes` and
   `FactStepSql`. The last two carry the pack conventions that reach DDL, both about how the pack stores a reference
   type's codes. `ReferenceTypes` is the SQL type of the columns holding a reference type's codes, keyed by reference
   type id. `FactStepSql` is the text of the step of a `value-map` or `state-map` fact, keyed by fact id: how stored
   codes are rewritten depends on how the writer stores them (3.5 gives sql-ddl that step on PostgreSQL). The pack
   builds both from what its unit already reads, so no unit reads more than today. Pack type maps named after a
   dialect no longer reach DDL (difference 2).
8. **Byte-identical, with three named differences.** sql-ddl's PostgreSQL output keeps every byte; the engine writes
   the pack's words, its TODO lines included (even "this pack writes no adopt step ... yet"). Differences 1 to 3 below
   are the only intended ones, and no golden reaches them.
9. **Read keys by construction.** The builder reads the resolved model only through `IDdlReads`, which records what
   each read touches, as the template walk did. A unit's read keys stay equal or wider (`DdlKeySetTests`).
10. **Visible to templates, not to scripts.** Step types are template-visible like the schema diff; scripts do not see
    them, as they do not see the schema diff.
11. **One server version table.** `ServerVersions` holds the PostgreSQL major version each modeled feature needs. It
    sets `DdlStep.Requires` and drives MQ4090: validation warns first, and the planner can later refuse a step a server
    cannot run. Versions before 14 are past their end of life and get no finer check.
12. **A step that needs a decision is a warning on the run that renders it.** Each TODO step of a change tree is
    MQ6041 on the unit, at the element. A migration unit renders only while its diff is not empty, so the warning shows
    on the plan and the run that write the file; like every unit diagnostic, it comes from the runs that render the
    unit, not from runs that skip it.
13. **`preview_ddl` previews picked elements only.** It builds the create steps of the tables, views, sequences,
    routines, database types and SQL objects it is given, from the store's shared resolution. A migration preview
    needs a diff of the touched tables, so the candidate model resolved; phase 2 builds none. Impact before apply
    (6.1) lists the units and files a change touches without resolving or rendering (section 1, decision 5).
14. **A project's copy of sql-ddl keeps working.** Packs live in the project's `templates/` folder. A copy taken before
    this section renders as it does today and moves to the engine when the team takes the new pack. Only a copy that
    registers a helper named `ddl_steps` or `ddl_sql` breaks (MQ6013).
15. **`helpers.js` stays.** `ddl_order` also orders lookup tables (`_reference.scriban:209`) and serves the other
    dialects; `reference_realization` is a convention.

#### What moves and what stays

| The engine builds (PostgreSQL) | The pack keeps |
| --- | --- |
| Every CREATE, ALTER, DROP, RENAME and COMMENT for schemas, tables (columns, defaults, identity, computed columns, collations, keys, uniques, foreign keys, checks, exclusions, partitions, storage), indexes, sequences, views, routines, database types and SQL objects | Units, file paths, migration file names and their key, banners and script headers (title, counts, states, facts, the TODO legend, the ledger and cycle header lines), the `-- <schema>.<table>` labels |
| DDL order: tables in foreign-key order with cycle-closing keys after the last table; the groups of `schema.sql`; `dependsOn` order | `BEGIN;` and `COMMIT;`, and where they go |
| Migration statements from the schema diff: every phase, views dropped and created again, foreign keys dropped and added back, identity, range casts, data-check TODOs, data-loss, held and unchanged notes | `schemaScript` include directives, `sqliteBail` |
| Migration fact steps, the ledger DDL, fact rows, rekey rows, run-once guards, the facts a build reaches; where a value-map or state-map step goes, and its row | Reference data: realizations (`strategyMap`, `referenceStrategy`), the enum prelude, lookup tables, checks, reconciliation, the `reference_types` map and the value-map text (`fact_step_sql`) it passes |
| Idempotent forms of all of the above; the server version each step needs | Seeds (`seed`, `seed-environments`, `_data.scriban`), process tables, `objectScripts`, environment scripts; every dialect but PostgreSQL; `helpers.js` |

#### The step tree

Three builds give a tree, each one a `DdlResult` whose top-level steps are in run order:

| Build | Input | What it holds | sql-ddl unit |
| --- | --- | --- | --- |
| `ForDatabase` | a database | what `schema.sql` creates, in its order; with `Environment`, only that environment's SQL objects | `schema`, `object-environments` |
| `ForChange` | a database and its schema diff | what a migration runs | `migration` |
| `ForObject` | a table, view, sequence, routine, database type or SQL object | its own script (a table declares every foreign key inline) | `table`, `view`, `sequence`, `routine`, `database-type`, `sql-object` |

**Text.** A step's text is its `Sql`, when not empty, followed by the text of each child, joined by line feeds. A list
of steps joins with one blank line. No step text ends with a line feed.

**Phases.** Every step has a phase, and phases never go back within a tree. A pack puts its own text at a phase
boundary: sql-ddl's enum prelude goes before the first step whose phase is not `before`, or at the end.

| Phase | `ForDatabase` | `ForChange` |
| --- | --- | --- |
| `before` | schemas; the ledger DDL; SQL objects of phase before; database types; sequences | the ledger DDL; in applied order, raw facts of phase before, rekey rows, value-map and state-map steps, fact TODOs and the end-state steps of facts whose change the diff does not hold; schema renames and new schemas; views and routines dropped before the tables change; SQL object drop notes; SQL objects of phase before; database types; new sequences |
| `tables` | `CREATE TABLE` blocks in foreign-key order; then the cycle-closing foreign keys | table renames, moves, comments, partitions and storage; foreign key, constraint and index drops; dropped tables; columns; new tables and their cycle-closing keys; new and changed constraints and indexes; foreign keys added back, then new ones; unchanged notes |
| `after` | routines and views; SQL objects of phase after; the fact rows a build reaches | views renamed in place and their comments; routines and views created; view rename rows; SQL objects of phase after and those run again; sequences changed and dropped; types dropped; schemas dropped; raw facts of phase after (from nothing, the fact rows a build reaches) |

`ForObject` holds the ledger DDL (`before`) for a run-once SQL object, then the object's own step in its phase.

**Catalog.** The verbs, kinds and details a PostgreSQL tree uses, and the pack function whose PostgreSQL text each one
takes over:

| Verb | Kinds | Detail | Text, as the pack wrote it |
| --- | --- | --- | --- |
| `create` | `schema` | | `schema_sql` |
| `create` | `table` | `cycle` when it leaves out keys that close a cycle | `_table.scriban`; children: partitions, indexes, comments |
| `create` | `column`, `primary-key`, `unique`, `check`, `exclusion`, `foreign-key`, `index`, `partition` | `cycle`, `again` | `add_column_sql`, `constraint_statements`, `index_statements`, `cycle_fk_statements`, `table_partition_statements` |
| `create` | `sequence`, `view`, `routine`, `database-type` | `again` | `sequence_sql`, `view_sql`, `routine_sql`, `type_sql` |
| `create` | `ledger` | | `ledger_prelude` |
| `alter` | `column` | `type`, `nullable`, `default`, `identity` | `column_change_statements`, `identity_statements` |
| `alter` | `table`, `index` | `storage` | `storage_alter_statements` |
| `alter` | `partition` | `bounds` | `table_partition_statements` |
| `alter` | `sequence` | `options` | `sequence_statements` |
| `drop` | every kind but `ledger` and `fact` | `again` | the drops of `_migration.scriban` and `migration.scriban` |
| `rename` | `schema`, `table`, `column`, `primary-key`, `unique`, `check`, `exclusion`, `foreign-key`, `index`, `view`, `sequence`, `database-type` | `end-state` | `table_rename_statements`, `rename_column_sql`, `constraint_statements`, `view_rename`, `own_step` |
| `move` | `table`, `sequence` | | `table_move_statements`, `sequence_statements` |
| `comment` | `table`, `column`, `view` | | `comment_sql`, `view_comment_sql` |
| `run` | `sql-object` | `once`, `again` | `object_run_sql`, `ledger_once_sql`, `reapplied_objects` |
| `run` | `fact` | | `raw_step` |
| `guard` | `column`, `fact` | `data-check` | `data_check_statements` |
| `update` | `column` | `backfill`, `restart` | `backfill_statements`, the identity restart of `identity_statements` |
| `update` | `fact` | `value-map`, `state-map` | the writer's text from `FactStepSql`; without it the fact is a `todo` note (`fact_todo`) |
| `record` | `fact` | | `ledger_fact_row` |
| `record` | `sql-object` | `rekey` | `ledger_rekey_rows` |
| `record` | `ledger` | `facts` | the fact rows a build reaches, one group with empty `Sql` |
| `note` | any | `todo`, `data-loss`, `held`, `unchanged`, `privileges`, `info` | the `-- ` lines for the reviewer |

Details: `cycle` marks what the foreign-key order defers; `again` marks a drop and its create that let another change
through (views around a retyped column, foreign keys around a recreated key, SQL objects after a recreated view);
`end-state` marks a fact's own step, guarded by its end state, for a change the diff does not hold; `once` marks a
run-once SQL object inside its ledger guard. A `todo` note is a block whose first line starts with `-- TODO:`.

**Value-map and state-map steps.** The engine knows where such a step goes (phase `before`, in the fact's applied
order) and that the fact's row follows it, but not its text: rewriting stored codes depends on how the writer stores
them (a lookup table, a check, an enum type, a bound table). When `FactStepSql` holds the fact's id, the step is
`update fact` with that text as its `Sql`, written as given, then the fact's `record fact` row; `FactRows` counts the
row, so the tree starts with the ledger DDL. Without it, the step is the `todo` note sql-ddl writes today and no row.

#### Public API

```csharp
namespace Maquettiste.Engine.Ddl;

// Each member of the three enums carries [JsonStringEnumMemberName("<kebab-case name>")], as GenerationService's enums
// do (GenerationService.cs:737): JsonStringEnumConverter<T> alone writes PascalCase. Templates read the same names.
[JsonConverter(typeof(JsonStringEnumConverter<DdlVerb>))]
public enum DdlVerb { Create, Alter, Drop, Rename, Move, Comment, Run, Guard, Update, Record, Note }

[JsonConverter(typeof(JsonStringEnumConverter<DdlKind>))]   // primary-key, foreign-key, database-type, sql-object, ...
public enum DdlKind { Schema, Table, Column, PrimaryKey, Unique, ForeignKey, Check, Exclusion, Index, Partition, View,
                      Sequence, Routine, DatabaseType, SqlObject, Ledger, Fact }

[JsonConverter(typeof(JsonStringEnumConverter<DdlPhase>))]
public enum DdlPhase { Before, Tables, After }

/// <summary>One step: what it does, to what, and its PostgreSQL text; its children run right after it.</summary>
public sealed record DdlStep
{
    public required DdlVerb Verb { get; init; }
    public required DdlKind Kind { get; init; }
    public string? Detail { get; init; }                       // kebab-case, from the catalog
    public required DdlPhase Phase { get; init; }
    public string? Id { get; init; }                           // the object's element or member id, when it has one
    public string? Key { get; init; }                          // the object's schema-diff key
    public string? Schema { get; init; }                       // the object's schema as the step finds it
    public string? Name { get; init; }                         // the object's name as the step finds it
    public string? NewSchema { get; init; }                    // a move's target schema
    public string? NewName { get; init; }                      // a rename's new name
    public RTable? Table { get; init; }                        // the table, in the current model
    public RColumn? Column { get; init; }                      // the column, in the current model
    public object? Member { get; init; }                       // RPrimaryKey, RUnique, RForeignKey, RCheck, RExclusion,
                                                               // RIndex or RPartition, in the current model
    public RAnnotated? Element { get; init; }                  // RSchema, RView, RSequence, RRoutine, RDatabaseType or
                                                               // RSqlObject, in the current model
    public IReadOnlyList<PropertyChange> Changes { get; init; } = [];  // an alter's changes, from the schema diff
    public RMigration? Fact { get; init; }                     // the migration fact whose step this is
    public string? Using { get; init; }                        // a type change's conversion expression
    public int? Requires { get; init; }                        // the PostgreSQL major version it needs, or null
    public required string Sql { get; init; }                  // its own text, without its children's
    public IReadOnlyList<DdlStep> Steps { get; init; } = [];
}

/// <summary>A tree for one database.</summary>
public sealed record DdlResult
{
    public required string Database { get; init; }             // the database's id, as QuerySqlPreview.Database
    public required string Dialect { get; init; }
    public required IReadOnlyList<DdlStep> Steps { get; init; }
    public int FactRows { get; init; }                         // fact rows the steps write
    public int OnceObjects { get; init; }                      // run-once SQL objects the steps run in their guard
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];  // MQ6041 per todo note; hidden from templates
}

public sealed record DdlOptions
{
    public static DdlOptions Default { get; } = new();
    public bool Comments { get; init; } = true;                // false: no comment steps
    public bool Idempotent { get; init; }                      // the forms that can run again
    public string? Quoting { get; init; }                      // always, reserved or never; null: the database's
    public string? Environment { get; init; }                  // ForDatabase: only this environment's SQL objects
    public string AppliedBy { get; init; } = "maquettiste";    // the writer the ledger rows name
    public IReadOnlyDictionary<string, string> ReferenceTypes { get; init; } = FrozenDictionary<string, string>.Empty;
                                                               // reference type id -> SQL type of its code columns
    public IReadOnlyDictionary<string, string> FactStepSql { get; init; } = FrozenDictionary<string, string>.Empty;
}                                                              // value-map or state-map fact id -> its step's text

/// <summary>Each build throws <see cref="NotSupportedException"/> when the database's dialect has no builder.</summary>
public static class DdlBuilder
{
    public static bool Supports(string dialect);               // postgresql (and its aliases) only
    public static DdlResult ForDatabase(RDatabase database, DdlOptions? options = null);
    /// <exception cref="ArgumentException">The diff's DatabaseId is not the database's id.</exception>
    public static DdlResult ForChange(RDatabase database, SchemaDiffResult diff, DdlOptions? options = null);
    /// <exception cref="ArgumentException">Not a table, view, sequence, routine, database type or SQL object.
    /// </exception>
    public static DdlResult ForObject(RAnnotated element, DdlOptions? options = null);
}

public static class DdlSql
{
    public static string Render(DdlStep step);                 // its Sql, then its children, joined by line feeds
    public static string Render(IReadOnlyList<DdlStep> steps); // the steps, joined by a blank line
    public static string Render(DdlResult result);             // Render(result.Steps)
}

namespace Maquettiste.Engine.SchemaDiff;
public sealed record SchemaDiffResult      // positional today; the new member is an init property
{   // existing members, plus:
    public string DatabaseId { get; init; } = "";              // the capture's PhysicalSnapshot.Database, set by the
}                                                              // differ; not hashed again (the hash covers the id)

namespace Maquettiste.Engine;
public sealed partial class GenerationService
{   /// <summary>The create steps of the picked elements, from the store's shared resolution; nothing else is
    /// built.</summary>
    /// <exception cref="ArgumentException">No id, more than 100, or an unknown quoting.</exception>
    public Task<DdlPreviewResult> GetDdlAsync(IReadOnlyList<string> elementIds, DdlOptions? options,
        CancellationToken ct);
}
public sealed record DdlPreviewResult(IReadOnlyList<DdlPreview> Previews, IReadOnlyList<Diagnostic> Diagnostics);
public sealed record DdlPreview(string Id, string Kind, string Name, string Database, string Dialect,
    IReadOnlyList<DdlPreviewStep> Steps, string Sql);
public sealed record DdlPreviewStep(DdlVerb Verb, DdlKind Kind, string? Detail, DdlPhase Phase, string? Id,
    string? Schema, string? Name, string? NewSchema, string? NewName, string? Fact, int? Requires, string Sql,
    IReadOnlyList<DdlPreviewStep> Steps);
```

`DdlPreview.Database` is the database's id, as in `QuerySqlPreview`; `DdlPreviewStep.Fact` is the fact's id. Internal,
not public: `IDdlReads` (`Ddl/DdlReads.cs`), `ServerVersions` (`Ddl/ServerVersions.cs`), the builders under
`Ddl/PostgreSql/` (namespace `Maquettiste.Engine.Ddl.PostgreSql`), and an overload of each `DdlBuilder` method that
takes an `IDdlReads`.

#### Building, reads and determinism

- **Inputs.** A build reads the resolved database and, for `ForChange`, the diff. It never resolves, captures or
  reads a file, a snapshot, the ledger or a database. The planner of phase 3 calls `ForChange` with a diff it computes
  from a catalog snapshot; nothing here changes for it.
- **Order.** Tables follow `ddl_order`, ported as it is: an iterative depth-first walk in the resolver's (schema, name)
  order, each table after the tables its foreign keys name, the first reached first in a cycle. The cycle-closing keys
  are the keys to a table placed later, keyed by table and constraint (difference 1). Routines, views, database types
  and SQL objects follow `dependsOn` in the same walk; views drop in the reverse of that order. Every diff list keeps
  the order `SchemaDiff/README.md` gives it.
- **Column types.** A column's SQL type is its resolved native type (the project's `typeMaps` and the column's
  `nativeType` apply). A column whose `type` is `reference`, whose reference type is a key of `ReferenceTypes` and
  which has no reference binding takes the mapped type instead; this is the rule 3.2 (D26) gives sql-ddl's
  `column_type`. Defaults that depend on the type read that same type. A column whose reference type changed, and
  whose new type takes a mapped type, converts through text (`USING <column>::text::<mapped type>`) unless a
  value-map fact of the diff covers the column: the retype 3.5 (D55) gives sql-ddl's migration. A designed column
  that 3.2 types by a reference type keeps its physical `type`, so it takes its resolved native type here. The check
  or key the pack adds for it is reference data and stays the pack's (3.2's `column_realization`); a foreign key the
  table declares from it (3.2's MQ4084) is a table member like any other.
- **Inherited members.** Columns, keys, uniques, foreign keys, checks and indexes a table inherits from its
  stereotypes (3.1) are members of the resolved table, in its resolved order, with keyed ids. The builder reads them
  as it reads own members, and a diff change finds them by the keys their capture derives from their columns. A
  fold's `rekey` writes no DDL, as every rekey does; its column and constraint entries move no ledger object rows,
  which only run-once SQL objects have.
- **Options.** `Quoting` overrides the database's quoting as the pack's `quoting` parameter did. `Idempotent` changes
  steps, not only text: it adds the drops of a renamed or added view a second run finds (the pack's `again`), and the
  catalog guards of type changes. `Environment` applies to `ForDatabase` only. `ReferenceTypes` and `FactStepSql`
  values are written as given, quoting included; a `FactStepSql` key that is not a value-map or state-map fact of the
  diff is ignored, and `FactStepSql` applies to `ForChange` only.
- **Reads.** Every resolved object the builder reads goes through `IDdlReads`:

| Read | Recorded as |
| --- | --- |
| A database, schema, table, column, view, sequence, routine, database type, SQL object or fact | the object's dependencies |
| A list it walks (tables, columns, views, sequences, routines, types, objects, migrations) | the list's membership keys |
| The ledger schema | `s:migrations`, as `ledger_ddl` records it |
| A schema diff | `k:database`, the database's dependencies and `d:<database id>`, as reading `schema_diff[<name>]` does |

  The public overloads record nothing. A unit that builds a change tree reads the schema diff, so a run limited by unit
  or element refuses it (MQ6033), as it refuses sql-ddl's migration unit today.
- **Determinism.** A build is a pure function of the resolved database, the diff and the options: no clock, random
  value, culture, environment variable or file. Collections that decide output are lists in model or diff order;
  dictionaries serve lookups only and are never enumerated for output. Text uses `\n` only, numbers in the invariant
  culture, literals through `SqlDialects.Literal`, identifiers through `SqlDialects.Quote` and the ledger schema
  through `LedgerContract.QuoteIdentifier`; a dollar-quote tag is the first free one, as the pack chose it. Static
  tables are frozen, and a build shares no state, so units that build on several workers agree.

#### Byte identity and the oracle

The oracle, built before anything moves:

1. The existing goldens (326 files) and every pack test that asserts PostgreSQL text.
2. `fact-steps` and `fact-steps-idempotent`, new goldens F1 records from today's templates: one migration per fact
   case that the tests assert and no golden holds.
3. The `ddl-probe` test pack (F6, F10): units that write, with the steps, what sql-ddl writes for PostgreSQL. In every
   golden scenario with a PostgreSQL database, and in the fact-steps scenarios, it runs beside sql-ddl, and
   `DdlParityTests` compares each probe file with its sql-ddl counterpart byte for byte, except the banner line that
   names the pack, before sql-ddl moves. F7 and F12 then move the probe's text into sql-ddl.
4. The read keys of sql-ddl's DDL units (`schema`, `migration`, `table`, `view`, `sequence`, `routine`,
   `database-type`, `sql-object`, `object-environments`) in those scenarios, recorded by F1 without `t:` keys.
   `DdlKeySetTests` holds them equal or wider, and a task that narrows one names why output cannot depend on it. The
   test stays after F13: a later task that changes what one of those units reads records the keys again, once, with
   `MAQUETTISTE_UPDATE_GOLDEN=1`, and names why.
5. The bench's sql-ddl outputs, kept by F1 and compared by F18, and the sample's generated `db/` folder.

Intended differences (no golden reaches any of them):

- **Difference 1.** A foreign key that shares its name with a cycle-closing key of another table stays in its
  `CREATE TABLE`. Today it is left out and never added (`_objects.scriban:38-49`, `_table.scriban:37`).
- **Difference 2.** A pack type map named after a dialect (`types/postgresql.json`) no longer changes sql-ddl's
  PostgreSQL column types: the resolved native type and `reference_types` decide. sql-ddl ships no type maps.
- **Difference 3.** A change to one of two views or sequences of one name in different schemas reaches that object,
  found by its id; the pack found the first of the name (`migration.scriban:195`, `_migration.scriban:1401`).

Unit input hashes change once (the templates change, and read keys may widen), so the first run after taking the new
pack renders sql-ddl's units again with the same bytes. A milestone D task that edits sql-ddl's templates lands before
F1 records the oracle (3.1's D13, 3.2's D26, 3.5's D55, whose value-map step and native retype the engine then keeps
byte for byte); an edit of sql-ddl's PostgreSQL text that lands later, before F13, is ported to the builder in the
same change.

#### Templates

- `ddl_steps <target> [<options>]` returns a `DdlResult`. The target is a database (`ForDatabase`), a schema diff
  (`ForChange`; its database is the one whose id the diff's `database_id` holds), or a table, view, sequence, routine,
  database type or SQL object (`ForObject`). Another dialect is MQ6040; another value, an unknown option or a wrong
  option type is MQ6006.
- `ddl_sql <result | step | list of steps>` returns the text.
- Options, an object with snake_case keys: `comments` (default `true`), `idempotent` (`false`), `quoting` (`""` for the
  database's), `environment`, `applied_by` (default: the unit's pack name), `reference_types` (reference type id to
  SQL type) and `fact_step_sql` (value-map or state-map fact id to its step's text).
- Templates read every `DdlStep` and `DdlResult` member in snake_case (`new_name`, `fact_rows`, `once_objects`), the
  enums as their kebab-case names; `DdlResult.Diagnostics` is hidden. Children are `step.steps`.
- MQ6013 keeps its text and reserves `ddl_steps` and `ddl_sql`. A function a template defines with `func` may still
  take either name in that template.

sql-ddl on PostgreSQL after F7 and F12, in outline (`ddl_options` is a new pack function in `_sql.scriban`):

```scriban
r = ddl_steps database (ddl_options(database, "database"))   # migration: ddl_steps schema_diff[database.name] (...)
# headers as today; the ledger header line reads r.fact_rows and r.once_objects; the cycle list reads the steps of
# kind foreign-key with detail cycle; schema.sql writes BEGIN; when r.fact_rows + r.once_objects > 0
prelude = false
for s in r.steps
  if !prelude && s.phase != "before"            # the enum prelude goes before the first step past "before",
    prelude = true                              # or after the last step when there is none
    # each enum type of native_type_prelude, after a blank line (migration: only when the diff adds a table or a
    # sequence or holds a table change, as migration.scriban:285 decides today)
  end
  # a blank line, then: in include mode, a create table step without detail cycle is its include directive;
  # in schema.sql a create table step is "-- <schema>.<table>" and a line feed before (ddl_sql s); anything else
  # is (ddl_sql s)
end
```

`ddl_options(db, scope)` passes `comments`, `idempotent` and `quoting` from the pack parameters, `applied_by: "sql-ddl"`
(so a renamed pack keeps the rows' writer), and `reference_types`: for a database unit, each type `types_realized(db,
"native")` lists, mapped to `q(native_type_name(rt), db)`; for a table unit, only the types of its own reference
columns that `column_type` would type. For the migration unit it also passes `fact_step_sql`: for each value-map fact
of `schema_diff[db.name].facts`, the text of 3.5's value-map step (D55), without its row, which the engine writes.
Each unit reads what it read before.

#### Rules

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ4090 | W | A table, column or view of a PostgreSQL database uses a feature that needs a later server version than the database's `version` names: a multirange column (14); NULLS NOT DISTINCT, a foreign key's onDeleteColumns, a view's securityInvoker (15); a temporal key (withoutOverlaps) or a foreign key's period (18). | Raise the version or drop the feature |
| MQ6040 | E | A template (`ddl_steps`) or a preview (`preview_ddl`) needs DDL steps for a database whose dialect has no step builder; only PostgreSQL has one in this version. The unit fails; the preview leaves the element out. | Write that dialect's DDL in the pack |
| MQ6041 | W | A migration's DDL steps hold one that needs a decision the schema diff cannot make (a TODO line): a narrowing or a retype with no migration fact, a new NOT NULL column without a default, a change PostgreSQL cannot make in place (partitioning, a database type), a changed or dropped SQL object, or a fact whose step has no PostgreSQL text. | Give the model what the step needs, or edit the migration |

- **MQ4090** is a validation rule in `Validation/PhysicalRules.cs`, next to MQ4056's dialect checks, at the feature's
  pointer (`/uniques/0/nullsNotDistinct`, `/securityInvoker`, `/columns/2/type`). It reads `database.version` up to
  its first character that is not a digit; a version with no leading number, or none, checks nothing. It judges the
  model's documents, so a save reports it; a save of the database file validates the tables and views that reference
  it, as MQ4056's dialect checks rely on.
- **MQ6040** fails the unit at the template position of the call. The message names the database and its dialect. In
  `preview_ddl` it is a diagnostic on the element, which gets no preview.
- **MQ6041** is added to the unit through `UnitRun.Report` (a helper's diagnostic that does not fail the unit), once
  per `todo` note, deduplicated within the unit. Its element is the note's fact, else its object (view, sequence,
  database type, SQL object), else its table; its message is the database, the unit and the note's words. Its severity
  follows `validation.rules`, as MQ6011's does.
- MQ4056 keeps its text: no step kind here is new to the model.

#### CLI and MCP

- **`preview_ddl`** (new catalog tool, MCP and the assistant): `elements` (ids, 1 to 100; required), `idempotent`
  (default `false`), `comments` (default `true`). Answer `{previews: [{id, kind, name, database, dialect, steps, sql}],
  diagnostics}`, one preview per picked element in the order given; `steps` hold `{verb, kind, detail, phase, id,
  schema, name, newSchema, newName, fact, requires, sql, steps}`. An id that names no table, view, sequence, routine,
  database type or SQL object is MQ6017 and gets no preview; an element of another dialect is MQ6040 and gets none; a
  model with errors answers no previews and its errors, as `get_resolved_model` does. A column of a reference type
  shows its resolved native type: the preview applies no pack convention. No id, more than 100, or a bad option is
  `bad-request`.
- No CLI verb: `generate` writes the scripts, and MQ6041 reaches its output and `plan`'s like any unit diagnostic.

#### Editor follow-ups

- An HTTP endpoint for an element's DDL, and the Database screen's DDL preview reading the engine's steps for
  PostgreSQL databases: no more unit picked by name, and no whole-database fallback for a view
  (`src/editor/src/workspaces/database/ddlPreview.ts`).
- An outline of a migration's steps (verb, kind, fact) next to its text in the generation screen.

#### Docs

- `docs/user-guide.md`: a section "DDL steps" (the helpers, options, fields, phases, the text rule, MQ6040, MQ6041,
  MQ4090).
- `packs/README.md`: the two helpers in the helper list and the MQ6013 names.
- `packs/sql-ddl/README.md`: "How the PostgreSQL DDL is built" (the steps, what the pack keeps, `ddl_options`), the
  Files table, differences 1 to 3 in Notes; the sample's copy.
- `docs/mcp.md`: `preview_ddl` in the read tools list, its row (API operation `(none)`, as `list_migrations`) and the
  two tool counts; `README.md`: the tool count; `skills/maquettiste-modeling/SKILL.md`: one line.
- `docs/guides/schema-changes.md`: PostgreSQL migration statements come from the engine; a team's own migrator calls
  `DdlBuilder.ForChange`.
- `src/Maquettiste.Engine/Ddl/README.md` (new), `Rendering/README.md` (the helpers), `docs/releases.md` under
  `## 0.12.0 (unreleased)`.

**Not doing**

- Step builders for SQL Server, MySQL, SQLite or Oracle. They keep the pack's templates; `ddl_steps` reports MQ6040.
- Seeds, reference data realizations and process tables in the tree: data and pack-decided persistence stay in the
  pack, and `reference_types` and `fact_step_sql` are the only hooks.
- Engine text for value-map and state-map steps: rewriting stored codes depends on how the writer stores them, so the
  writer passes the text and the engine places it and writes the row.
- Grading steps safe, needs a fact or refused, `canServe`, and running steps: the live planner (scope 6.1, phase 3).
- Recreating only the views that read a retyped column: it needs a SQL parser; today's all-views rule stays.
- New DDL features (an enum database type that gains a value with `ALTER TYPE ... ADD VALUE`, say): this section moves
  what exists.
- Column type overrides by column id: only reference types reach a column type from a pack.
- A migration preview for picked elements: it would resolve the candidate model (decision 13). `ForChange` serves it
  later.
- A CLI verb that prints steps; steps for JavaScript helpers; a change to the ledger contract.

**Tests**

Unit tests (paths under `tests/Maquettiste.Engine.Tests/` unless named):

- `Ddl/DdlSqlTests` (new): `A_step_is_its_sql_then_its_children_on_their_own_lines`,
  `A_group_with_empty_sql_starts_with_its_first_child`, `Steps_join_with_one_blank_line`, `A_result_renders_its_steps`.
- `Ddl/DdlStepTests` (new): `Verbs_kinds_and_phases_have_kebab_case_names_in_json_and_templates`,
  `Step_types_are_visible_to_templates_and_result_diagnostics_are_not`.
- `Ddl/PgTableTextTests` (new): `The_billing_invoices_table_matches_its_table_script`,
  `Column_features_write_as_the_pack_did` (identity, computed, collation, range, defaults),
  `Keys_constraints_and_exclusions_write_as_the_pack_did`, `Indexes_write_method_include_where_nulls_and_storage`,
  `Partitions_and_storage_follow_the_create_table`, `Idempotent_creates_add_if_not_exists`,
  `Comments_false_writes_no_comment_steps`, `Reference_types_type_the_code_columns_of_an_unbound_reference_type`,
  `Quoting_always_quotes_every_identifier`, `A_nulls_not_distinct_unique_requires_15`,
  `Inherited_members_of_audited_write_as_own_members`, `A_column_typed_by_a_reference_type_keeps_its_native_type`.
- `Ddl/PgObjectTextTests` (new): `Views_write_materialized_security_column_list_and_check_option`,
  `Routines_take_the_first_free_dollar_tag`, `Database_types_write_domain_enum_composite_and_range`,
  `A_run_once_sql_object_runs_inside_its_ledger_guard`, `Sequences_write_their_options`.
- `Ddl/PgLedgerTextTests` (new): `The_ledger_step_is_the_contract_ddl`, `Fact_rows_name_the_applied_by_option`,
  `Rekey_rows_move_the_object_rows_of_sql_objects`.
- `Ddl/PgBuildTests` (new): `Tables_follow_the_tables_they_reference`, `Cycle_closing_keys_come_after_the_last_table`,
  `A_key_named_like_another_tables_cycle_break_stays_inline` (difference 1),
  `Routines_and_views_follow_their_depends_on`, `The_ledger_follows_the_schemas_when_the_build_writes_a_row`,
  `Environment_builds_only_that_environments_objects`, `The_rows_a_build_reaches_are_one_group_at_the_end`,
  `Phases_never_go_back`.
- `Ddl/PgChangeTableTests` (new): `A_retyped_column_takes_using_and_drops_every_view_first`,
  `A_dropped_column_gets_a_data_loss_note`, `A_new_not_null_column_without_default_gets_a_todo`,
  `Foreign_keys_relying_on_a_recreated_key_go_and_come_back`, `An_added_identity_restarts_after_the_largest_value`,
  `A_renamed_schema_names_later_steps_by_its_new_name`, `A_table_whose_change_writes_nothing_gets_an_unchanged_note`,
  `A_narrowing_writes_its_data_check_todo`, `Idempotent_changes_are_guarded`,
  `A_column_moved_to_another_mapped_reference_type_converts_through_text`.
- `Ddl/PgChangeObjectTests` (new): `Views_that_read_a_dropped_view_go_and_come_back`,
  `View_drops_follow_reverse_dependency_order`, `Routines_drop_before_the_tables_and_return_after`,
  `A_changed_sql_object_gets_a_todo_and_runs_again`, `Objects_on_a_recreated_view_run_again`,
  `Database_types_rename_and_drop_last`, `Sequences_alter_rename_move_and_drop`, `A_dropped_schema_goes_last`,
  `A_view_change_finds_its_view_by_id` (difference 3).
- `Ddl/PgFactStepTests` (new): `A_change_type_fact_guards_then_converts_then_records`,
  `A_backfill_updates_before_set_not_null`, `A_raw_fact_runs_in_its_phase_behind_skip_when`,
  `A_rekey_moves_object_rows_and_writes_no_ddl`, `A_held_drop_writes_its_note_and_no_row`,
  `A_released_drop_whose_object_left_writes_drop_if_exists`, `A_fact_completed_after_a_todo_writes_its_end_state_step`,
  `A_fact_without_postgresql_text_falls_back_to_the_diff_and_writes_no_row`,
  `From_nothing_writes_raw_steps_and_the_rows_a_build_reaches`, `Adopt_and_baseline_write_a_todo`,
  `A_value_map_with_writer_text_runs_it_then_its_row_behind_the_ledger`,
  `A_value_map_without_writer_text_is_a_todo_with_no_row`.
- `Ddl/DdlDeterminismTests` (new): `Two_builds_give_equal_text`, `Parallel_builds_of_one_database_agree`,
  `Text_does_not_depend_on_the_culture`.
- `Rendering/DdlHelperTests` (new): `Ddl_steps_of_a_database_returns_its_result`,
  `Ddl_steps_of_a_sql_server_database_is_MQ6040`, `An_unknown_option_is_MQ6006`,
  `A_pack_helper_named_ddl_steps_is_MQ6013`, `Applied_by_defaults_to_the_pack_name`,
  `Fact_step_sql_reaches_the_value_map_step`, `Ddl_steps_records_what_it_reads`,
  `A_change_tree_reads_the_schema_diff_so_a_scoped_run_refuses_it`, `A_todo_step_is_MQ6041_at_its_element`,
  `MQ6041_takes_its_severity_from_validation_rules`.
- `Validation/ServerVersionRuleTests` (new): `Nulls_not_distinct_on_14_is_MQ4090`, `Security_invoker_on_14_is_MQ4090`,
  `A_temporal_key_on_17_is_MQ4090`, `Multirange_on_13_is_MQ4090`, `No_version_checks_nothing`,
  `A_version_without_a_leading_number_checks_nothing`.
- `Editor/AgentToolsTests`: `Preview_ddl_builds_the_picked_elements_only`,
  `Preview_ddl_reports_MQ6017_for_an_id_it_cannot_build`, `Preview_ddl_reports_MQ6040_outside_postgresql`.
- Packs: `FactStepGoldenTests` and `DdlKeySetTests` (F1), `DdlParityTests` (F6, F10; retired by F13).

Acceptance (this scope item has no "Done when" line; these are the design's):

- `tests/Maquettiste.Packs.Tests/DdlStepsTests.The_invoices_table_steps_render_its_table_script` (new). Setup: a
  billing copy, resolved; `DdlBuilder.ForObject` on the invoices table
  (`databases/main/tables/01j92p0v1t0j6rh4my9h81nyb4.json`). Expected: one `create table` step, `Id`
  `01J92P0V1T0J6RH4MY9H81NYB4`, schema `billing`, name `invoices`, whose children are `create index`
  `ix_invoices_issued_on`, `comment table` and `comment column` (`number`); `DdlSql.Render` equals
  `tests/fixtures/golden/sql-ddl/billing/main/billing/tables/invoices.sql` from its fourth line, without its last
  line feed.
- `DdlStepsTests.Billing_builds_the_schema_and_sequence_then_customers_before_invoices`. Setup: a billing copy;
  `ForDatabase` on `main`. Expected: `before` holds `create schema billing` and `create sequence invoice_number_seq`;
  `tables` holds `customers` before `invoices` before `invoice_lines`, none with detail `cycle`; `after` holds
  `create view outstanding_invoices`; `FactRows` is 0. sql-ddl's `schema.sql` matches the golden.
- `DdlStepsTests.The_library_call_and_the_pack_write_the_same_migration`. Setup: a billing copy, generated once; the
  committed snapshot of `main` loaded; `Invoice` gains an optional `paidOn` date attribute; generated again. Expected:
  `DdlSql.Render(DdlBuilder.ForChange(main, differ.Diff(snapshot, main)))` equals the new migration's text between
  `BEGIN;` and `COMMIT;` without the blank lines around it, and the tree is one `create column` step `paid_on` in phase
  `tables`.
- `DdlStepsTests.A_narrowing_with_a_fact_guards_converts_and_records`. Setup: as
  `MigrationFactTests.A_complete_change_type_renders_its_data_check_and_conversion`: `invoices.number` narrowed to
  `varchar(20)`, the fact completed with `{ "cast": true }` and the data check
  `select 1 from {table} where length({column}) > 20`. Expected: the migration holds the guard `DO $mq$ ... END $mq$;`,
  a blank line and `ALTER TABLE billing.invoices ALTER COLUMN number TYPE varchar(20) USING number::varchar(20);` as
  today; the tree holds `drop view outstanding_invoices` (detail `again`), then `guard column number` (detail
  `data-check`), `alter column number` (detail `type`, `Using` `number::varchar(20)`) and `record fact`, the last
  three with the fact's id.
- `DdlStepsTests.A_required_due_date_without_default_is_MQ6041_on_invoices`. Setup: a billing copy, generated once;
  `Invoice` gains a required `dueOn` date attribute with no default; generated again. Expected: the migration holds
  `-- TODO: due_on is NOT NULL without a default; fill existing rows first (the ADD above fails on a table with rows).`;
  the run has one MQ6041, a warning, on the invoices table's id, naming `due_on`; a third run reports no MQ6041.
- `DdlStepsTests.A_sql_server_database_is_MQ6040`. Setup: `PackRepo.BillingDialects()` (`main` on PostgreSQL,
  `reporting` on SQL Server, `local` on SQLite) and a one-unit pack the test writes into the copy, whose
  `each database` unit is `{{ ddl_sql (ddl_steps database) }}`; a dry run of that pack. Expected: MQ6040 on the units
  of `reporting` and `local`, none on `main`'s. sql-ddl's output for the same model still matches the
  `billing-dialects` golden.
- `DdlStepsTests.Editing_a_column_comment_renders_its_table_and_the_schema_again`. Setup: a billing copy, generated;
  the comment of `invoices.number` changes; generated again. Expected: `db/main/billing/tables/invoices.sql` and
  `db/main/schema.sql` change, and a migration holds one `COMMENT ON COLUMN` step; every other table script keeps its
  bytes; the `table` units of `customers`, `products`, `notes` and `payments`, which read nothing of invoices, are not
  rendered (`invoice_lines` and `payment_invoice` reference invoices, so their units may render again, with the same
  bytes, as today).
- `tests/Maquettiste.Engine.Tests/Integration/ServerVersionTests.An_invoker_view_on_postgresql_14_is_MQ4090` (new).
  Setup: a billing copy; `databases/main/database.json` takes `"version": "14"`;
  `databases/main/views/outstanding-invoices.json` gains `"securityInvoker": true`. Expected: `validate` reports
  MQ4090, a warning, at `/securityInvoker` of the view; with `"16"`, or with no version, it does not.
- `tests/Maquettiste.Cli.Tests/McpServerTests.cs` `McpReadTests.Preview_ddl_answers_the_invoices_table`. Setup: a
  billing copy over MCP; `preview_ddl` with `elements: ["01J92P0V1T0J6RH4MY9H81NYB4"]`. Expected: one preview, kind
  `table`, dialect `postgresql`, `sql` equal to the golden table script from its fourth line without its last line
  feed; with the database's id instead, no preview and MQ6017.

**Tasks**

Within this section no two tasks that may run at once own the same file: the rule catalog, its pin and the recorded
rules go F14, F6, F11 in that order, and `Rendering/DdlHelpers.cs` goes F6, F10, F11. Tasks of 5.2, 5.3 and 5.5 also
own `Diagnostics/RuleCatalog.cs`, `CatalogTests.cs`, `Rendering/BuiltinHelpers.cs`, `Rendering/RenderRun.cs`,
`Rendering/TemplateValues.cs`, `Rendering/README.md` and `packs/README.md`. 5.2's F21, F26 and F27 run after F11, F15
and F16, and every task of 5.3 to 5.6 that owns one of these files follows 5.2's tasks.

- **F1. The oracle, before anything moves.** Runs after D13, D26 and D55. Owns:
  `tests/Maquettiste.Packs.Tests/FactStepGoldenTests.cs` (new), `tests/fixtures/golden/sql-ddl/fact-steps/` and
  `fact-steps-idempotent/` (new), `tests/Maquettiste.Packs.Tests/DdlKeySets.cs` (new: the read keys of each DDL unit
  after a run, from the unit states, without `t:` keys), `tests/Maquettiste.Packs.Tests/DdlKeySetTests.cs` (new) and
  its recordings under `tests/fixtures/golden/sql-ddl-keys/`. The fact-steps scenario starts from billing and
  generates once per case, so each case writes one migration: every PostgreSQL text that `MigrationFactTests`,
  `MigrationSafetyTests`, `RangeColumnDdlTests`, `SqlObjectEnvironmentTests`, `LedgerSqlTests` and 3.5's value-map
  cases assert and no golden holds (a change-type by cast and by expression, a backfill, raw facts before and after, a
  rekey of a run-once object, held and released drops and renames, a fact completed after a TODO, a range cast with no
  path, a fact without PostgreSQL text, a value map). Tests: `FactStepGoldenTests`, `DdlKeySetTests`. Verify:
  `PG MAQUETTISTE_UPDATE_GOLDEN=1 P "~FactStepGoldenTests|~DdlKeySetTests"` once (intended: new files only), then
  `PG P` (no existing golden changes); `dotnet run -c Release --project bench/Maquettiste.Bench -- --keep --out
  bench/out/ddl-before` (kept for F18).
- **F2. Step records, text form, options and reads.** Owns: `Ddl/DdlStep.cs` (`DdlStep`, `DdlVerb`, `DdlKind`,
  `DdlPhase`), `Ddl/DdlResult.cs`, `Ddl/DdlOptions.cs`, `Ddl/DdlSql.cs`, `Ddl/DdlReads.cs`, `Ddl/ServerVersions.cs`,
  `Ddl/README.md` (new folder), `Rendering/TemplateValues.cs` (`IsViewType` admits `Maquettiste.Engine.Ddl`;
  `IsHidden` hides `DdlResult.Diagnostics`). Tests: `DdlSqlTests`, `DdlStepTests`. Verify: `E "~Ddl.|~Rendering."`.
- **F3. PostgreSQL tables.** Runs after F2. Owns: `Ddl/DdlBuilder.cs` (`Supports`; `ForObject` for tables, the other
  kinds refused until F4), `Ddl/PostgreSql/PgNames.cs`, `Ddl/PostgreSql/PgTable.cs`. Tests: `PgTableTextTests`.
  Verify: `E "~Ddl."`.
- **F4. PostgreSQL objects and the ledger.** Runs after F3. Owns: `Ddl/PostgreSql/PgObjects.cs`,
  `Ddl/PostgreSql/PgLedger.cs`, `ForObject` for the other kinds in `Ddl/DdlBuilder.cs`. Tests: `PgObjectTextTests`,
  `PgLedgerTextTests`. Verify: `E "~Ddl."`.
- **F5. Build trees.** Runs after F4. Owns: `Ddl/PostgreSql/PgOrder.cs` (the `ddl_order` port, cycle breaks by table
  and constraint, `dependsOn` order), `Ddl/PostgreSql/PgBuild.cs`, `ForDatabase` in `Ddl/DdlBuilder.cs`. Tests:
  `PgBuildTests`. Verify: `E "~Ddl."`.
- **F6. Helpers, MQ6040 and build parity.** Runs after F1, F5 and F14. Owns: `Rendering/DdlHelpers.cs` (new:
  `ddl_steps` for databases and objects, `ddl_sql`, every option, the `IDdlReads` adapter over the unit recorder; a
  schema diff is MQ6006 until F10), `Rendering/BuiltinHelpers.cs` (`Names`, registration), `Rendering/RenderRun.cs`
  (`UnitRun.Report`), `Rendering/README.md`, `Diagnostics/RuleCatalog.cs` (MQ6040), `CatalogTests.cs` (+1),
  `packs/README.md` (the helpers, the MQ6013 names), `tests/fixtures/templates/ddl-probe/` (new: units `table`,
  `schema`, `view`, `sequence`, `routine`, `database-type`, `sql-object` and `object-environments`, writing for
  PostgreSQL databases what sql-ddl writes), `tests/Maquettiste.Packs.Tests/DdlParityTests.cs` (new). Tests:
  `DdlHelperTests` (all but the schema-diff and MQ6041 cases), `DdlParityTests`. Verify:
  `E "~CatalogTests|~DdlHelperTests|~Rendering."`; `PG P "~DdlParityTests"`; `record`.
- **F7. sql-ddl builds `schema.sql` and the object scripts with the steps.** Runs after F6. Owns:
  `packs/sql-ddl/{schema,table,view,sequence,routine,database-type,sql-object,object-environments}.scriban` (the
  PostgreSQL branch takes the probe's text), `packs/sql-ddl/_sql.scriban` (`ddl_options` for the database, table and
  object scopes), and their copies under `samples/reference-app/.maquettiste/templates/sql-ddl/`. Tests: none new.
  Verify: `PG P`; `diff -r packs/sql-ddl samples/reference-app/.maquettiste/templates/sql-ddl` prints nothing; with
  `samples/reference-app/db` and `samples/reference-app/.maquettiste/manifest` copied aside,
  `dotnet src/Maquettiste.Cli/bin/Release/net10.0/Maquettiste.Cli.dll --repo samples/reference-app generate --quiet`,
  then `diff -r` against each copy prints nothing.
- **F8. Change trees: tables, columns, constraints and indexes.** Runs after F5. Owns: `Ddl/PostgreSql/PgChange.cs`
  (the tree and its phases), `Ddl/PostgreSql/PgChange.Tables.cs`, `Ddl/PostgreSql/PgChange.Columns.cs` (the native
  retype through text included), `ForChange` in `Ddl/DdlBuilder.cs`, `SchemaDiffResult.DatabaseId` in
  `SchemaDiff/SchemaDiffTypes.cs` and its value in `SchemaDiff/SchemaDiffer.cs`. Tests: `PgChangeTableTests` (expected
  text from the `ddl-coverage`, `ddl-coverage-idempotent`, `postgres-features` and `range-columns` migrations),
  `SchemaDifferTests.A_diff_carries_its_database_id`. Verify: `E "~Ddl.|~SchemaDiff"`.
- **F9. Change trees: views, routines, types, sequences, schemas and SQL objects.** Runs after F8. Owns:
  `Ddl/PostgreSql/PgChange.Views.cs`, `Ddl/PostgreSql/PgChange.Objects.cs`. Tests: `PgChangeObjectTests`. Verify:
  `E "~Ddl."`.
- **F10. Change trees: migration facts, the schema-diff target and migration parity.** Runs after F6 and F9. Owns:
  `Ddl/PostgreSql/PgFacts.cs` (value-map and state-map steps from `FactStepSql` included), `Rendering/DdlHelpers.cs`
  (a schema diff as the target of `ddl_steps`, found by `database_id`, and its reads), the probe's `migration` unit
  under `tests/fixtures/templates/ddl-probe/`, the migration cases of `DdlParityTests`. Tests: `PgFactStepTests`, the
  schema-diff cases of `DdlHelperTests`, `DdlParityTests`. Verify: `E "~Ddl.|~DdlHelperTests"`;
  `PG P "~DdlParityTests"`.
- **F11. MQ6041.** Runs after F10. Owns: `Rendering/DdlHelpers.cs` (a `todo` note's diagnostic through
  `UnitRun.Report`), `Diagnostics/RuleCatalog.cs` (MQ6041), `CatalogTests.cs` (+1). Tests: the MQ6041 cases of
  `DdlHelperTests`. Verify: `E "~CatalogTests|~DdlHelperTests"`; `PG P "~DdlParityTests"`; `record`.
- **F12. sql-ddl writes PostgreSQL migrations with the steps.** Runs after F7 and F11. Owns:
  `packs/sql-ddl/migration.scriban` (the PostgreSQL branch takes the probe's text: header, `BEGIN;`, the steps with the
  enum prelude at the `before` boundary, `COMMIT;`), `packs/sql-ddl/_sql.scriban` (`ddl_options` for the migration
  scope, `fact_step_sql` included), their sample copies, and any pack, engine or CLI test that asserts no warning on a
  run whose migration holds a TODO (it now expects MQ6041). Tests: none new. Verify: `PG P`; `E "~Integration."`;
  `C "~GenerateTests|~MigrationTests|~McpGenerationTests"`; the `diff -r` and sample checks of F7.
- **F13. Prune the PostgreSQL template code; retire the probe.** Runs after F12 and 5.2's F20 (both regenerate the
  editor's types: F20 changes the contract, and F13 runs `editor` when it records the pack mock). Owns:
  `packs/sql-ddl/{_sql,_table,_objects,_migration,_ledger}.scriban` (the PostgreSQL-only functions and branches no unit
  reaches; 3.5's value-map text stays, since `ddl_options` reaches it), `packs/sql-ddl/README.md`, the sample's copies
  (the README included), the removal of `tests/fixtures/templates/ddl-probe/` and `DdlParityTests.cs`,
  `src/editor/src/mocks/recorded/pack-sql-ddl.json` (recorded again). Tests: none new. Verify: `PG P`; the `diff -r` and
  sample checks of F7; `MAQUETTISTE_RECORD=1 F "~PackEndpointTests"`, then `F "~PackEndpointTests"`; `editor`.
- **F14. MQ4090.** Runs after F2. Owns: `Validation/PhysicalRules.cs` (tables and views against `ServerVersions`),
  `Diagnostics/RuleCatalog.cs` (MQ4090), `CatalogTests.cs` (+1), `Validation/ServerVersionRuleTests.cs` (new),
  `Integration/ServerVersionTests.cs` (new), both under `tests/Maquettiste.Engine.Tests/`. Tests:
  `ServerVersionRuleTests`, `ServerVersionTests`. Verify: `E "~CatalogTests|~ServerVersion|~ValidationFixtureTests"`;
  `record`.
- **F15. `preview_ddl`.** Runs after F5. Owns: `GenerationService.Ddl.cs` (new: `GetDdlAsync`), `Editor/DdlViews.cs`
  (new: `DdlPreviewResult`, `DdlPreview`, `DdlPreviewStep`), `Agent/AgentTools.cs`, `Agent/AgentTools.Handlers.cs`,
  `Agent/tools.json`, `docs/mcp.md` (the read tools list, the row, the two tool counts), `README.md` (the tool count),
  `skills/maquettiste-modeling/SKILL.md`, the `AgentTools.All` pins in `Editor/AgentToolsTests.cs` and
  `tests/Maquettiste.Cli.Tests/McpServerTests.cs` (+1 each), the `McpSurfaceTests` Tools array (+1). Tests: the
  `AgentToolsTests` cases, `McpReadTests.Preview_ddl_answers_the_invoices_table`. Verify: `E "~AgentToolsTests"`;
  `C "~McpSurfaceTests|~McpReadTests|~SkillRefreshTests|~AgentSetupTests"`.
- **F16. Docs and release notes.** Runs after F13, F14 and F15. Owns: `docs/user-guide.md` (the section "DDL steps"),
  `docs/guides/schema-changes.md`, `docs/releases.md` (the 5.1 lines under `## 0.12.0 (unreleased)`). Tests: none new.
  Verify: `C "~SkillRefreshTests|~AgentSetupTests|~McpSurfaceTests"`.
- **F17. Acceptance and determinism.** Runs after F12. Owns: `tests/Maquettiste.Packs.Tests/DdlStepsTests.cs` (new),
  `Ddl/DdlDeterminismTests.cs` (new, under `tests/Maquettiste.Engine.Tests/`). Tests: the seven `DdlStepsTests`
  acceptance tests, `DdlDeterminismTests`. Verify: `PG P "~DdlStepsTests"`; `E "~DdlDeterminismTests"`.
- **F18. Bench parity and timing.** Runs after F13. Owns: `bench/README.md` (one short note: the `schema` unit's time
  and the cold render before and after). Tests: none. Verify:
  `dotnet run -c Release --project bench/Maquettiste.Bench -- --keep --out bench/out/ddl-after`; `diff -r` of the
  sql-ddl outputs under `bench/out/ddl-before` and `bench/out/ddl-after` prints nothing.

Cross-section:

- 3.5's D55 gives sql-ddl the PostgreSQL value-map step and the native retype in milestone D, before F1 records the
  oracle. F8 ports the retype; the value-map step stays the pack's text, which F12's `ddl_options` passes as
  `fact_step_sql`, and F10 places it, with its row, where D55 put it. A state-map step (phase 4) uses the same option.
- 6.3's G22 edits `object-environments.scriban` and `packs/sql-ddl/README.md` after F13; its unit then loops the
  declared environments, or the names the database's SQL objects use when none is declared, and calls
  `ddl_steps database` with each name as `environment`. If that changes what the unit reads, G22 records
  `DdlKeySetTests` again and says why.
- 5.4's F44 runs after F13 and gives sql-ddl one small pack test case with a model of its own. sql-ddl's goldens stay
  C# tests against the billing fixture, `fact-steps`, `fact-steps-idempotent` and the key recordings under
  `tests/fixtures/golden/sql-ddl-keys/` included, so no task of this section has to follow a moved tree.

### 5.2 Library packs

**Why every team** (scope 5.1)

A team that generates more than one artifact from its model soon has two packs that need the same small things: how a
class or a file is named, a header comment, a helper that turns a reference type's code into a constant name. Today
each pack carries its own copy, so a naming rule changes once per pack or drifts. A library pack holds those partials
and helpers once. Every pack that uses it renders with the same definitions, and the project's own rule scripts can
check the model with the helpers that name the generated code. A library lives in the project's templates folder like
any other pack, so it works the same in a repository, in CI and in the hosted product.

**Today**

- An `include` name resolves only inside the unit's own pack folder: `PackTemplateLoader` normalizes it as a
  pack-relative path and reads it from the unit's pack
  (`src/Maquettiste.Engine/Rendering/TrackingTemplateContext.cs:186-208`), and `CreateTemplate` records
  `t:<unit's pack>/<path>` through `UnitRun.TemplateKey` (`:177-183`, `Rendering/RenderRun.cs:235`). A path that holds
  `:` is refused (`Rendering/TemplateCache.cs:265`).
- JavaScript helpers belong to one pack. `PackRuntime.Create` loads the pack's scripts into its own pool and registers
  each helper at the template root; a name that collides with a builtin or a variable is MQ6013
  (`Rendering/RenderRun.cs:132-175`). A unit calls them through its one lease (`:251-256`).
- Pack scripts keep one scope per pack. Rule scripts are each wrapped in their own function, cannot publish anything
  to another file, and no script can import (`Scripting/README.md:29`, `:81-83`). The validator's rule pool holds
  only the snapshot's rule scripts (`Validation/ModelValidator.cs:88-106`), which the loader takes from
  `extensions/rules/*.js` alone (`Loading/ModelLoader.cs:652`). Selectors and script filters run while planning, in a
  one-engine pool per pack (`Planning/UnitPlanner.cs:84-98`).
- `units` is required with at least one item (`schemas/v1/pack.json:356`, `:359-364`), so a pack of partials and
  helpers alone does not load.
- The loader reads packs in ordinal name order and, with a pack filter, only the packs asked for; a disabled pack is
  skipped (`Planning/PackLoader.cs:38-42`, `:53`, `:62`). Shared packs were left for later (`:19`); `SPEC.md:917`
  sketches them as references into version control pinned in `packs.lock.json`, and nothing implements that.
- A template key `t:<pack>/<path>` hashes the file of a pack in `PackSet.Packs` and is absent for any other name
  (`Planning/DependencyHasher.cs:293-303`). A unit's static hash folds in its own pack's scripts hash and parameters
  only (`Planning/UnitPlanner.cs:591`).
- `maquettiste validate` loads every pack and reports its diagnostics with the model's
  (`src/Maquettiste.Cli/Commands/ValidateCommand.cs:66-69`). The MCP `validate` tool reports model diagnostics only;
  `list_packs` carries the packs' load diagnostics.

**Decisions taken here**

- **A library is a pack with no units.** `units` becomes optional. Any pack may be used: a pack with units offers its
  partials, helpers and, with 5.3, its exports; a pack without units offers partials and helpers. There is no
  `library` flag; list and editor answers call a pack without units a library.
- **`uses` names packs with a version range.** An entry is `{ "pack": "<name>", "version": "<range>" }`. The range has
  the syntax of `engine` (`Planning/EngineRange.cs`: comparators, `^`, `~`, `x`, `||`; a pre-release suffix is
  ignored) and is checked against the used pack's `version` (MQ6043). The range is required, so a pack states which
  versions it was written against.
- **Local packs only.** A used pack is a folder of `.maquettiste/templates/`. There is no registry, no download, no
  lock file and no reference into version control: Maquettiste does not depend on version control. A team that shares
  a library across projects copies its folder, and the range reports a copy that is too old or too new. This
  supersedes the shared packs that `SPEC.md:917` sketches.
- **A pack's name is its key.** Packs have no ids: the loader requires the name to equal the folder
  (`Planning/PackLoader.cs:175-176`), and generation hints are keyed by it. `uses` names packs by that key, and
  `rename_pack` rewrites every `uses` entry that names the pack, in packs and in `maquettiste.json`, as it already
  moves settings, manifests, unit states and hints. It does not edit template or script text; it lists the files
  whose text names the old pack.
- **A pack sees its uses closure.** The closure (every pack reachable through `uses`, in dependency order) is computed
  once at load, and each edge's range is checked. Transitive packs are visible, so a library partial that calls its
  own dependencies' helpers works in any pack that includes it.
- **Used packs always load.** A pack in the closure of a run pack loads whether or not the run asks for it and whatever
  its `packs.<name>.enabled` (which says only whether the pack's own units run). A used pack the run does not select
  goes to `PackSet.Used`: it is never rendered or written, and its manifest and unit states are left alone; only 5.3
  plans its units that declare exports, for their exports. Packs order after the packs they use, ties by ordinal
  name, so a project without `uses` keeps today's order. A cycle fails every pack in it (MQ6044).
- **Cross-pack includes are `<pack>:<path>`.** The scope wrote `include "naming/class_name"`, but a bare first segment
  collides with a folder of the including pack. A `:` is refused in pack paths today, so the prefixed form cannot
  change the meaning of any existing include. An unprefixed name resolves in the pack that holds the including file,
  which today is always the unit's pack. A partial of another pack is parsed with `{{ }}` whatever the unit's
  `delimiters`, since a library has no unit to declare other ones. It renders with the unit's variables (`element`,
  `pack`, `data`), as any partial does.
- **Helper namespaces.** A used pack that has scripts gets a namespace: its name with `-` replaced by `_`
  (`sql-names` becomes `sql_names`; pack names hold no `_`, so two packs never share one). Its helpers are
  `<namespace>.<helper>` in every unit that sees it, callable and pipeable (`{{ entity.name | naming.class_name }}`),
  and also in its own units, so its partials render there too. A pack's own helpers stay at the root, as today. Only
  helpers cross packs; selectors, filters and transforms stay in their pack.
- **Each used pack runs in its own sandbox pool.** Its scripts load exactly as they do for the pack itself, so using a
  pack never changes how its scripts load or what their globals are. A call from a template or from another pack's
  script goes to a sandbox leased from the used pack's pool for the rest of the unit, with the caller's read recorder
  and seed and the used pack's own parameters (its `pack.json` defaults overlaid by `packs.<name>.parameters`). Values
  cross as they cross into templates. Pools are sized to the render workers, and since `uses` is acyclic a worker holds
  at most one lease per pool.
- **Scripts reach used packs through `maquettiste.use(name)`.** It returns a frozen object of that pack's helpers, and
  may run while scripts load, so `const naming = maquettiste.use("naming");` at the top of a file works. A name outside
  the calling script's closure is a `TypeError` (MQ6016 in packs, MQ5002 in rules). Calling one of its helpers while
  scripts load is a `TypeError`, since no call context exists yet, as `maquettiste.params` is empty while loading. A
  helper reached this way receives what the script passes; `{ plain: true }` still applies in the used pack's sandbox.
  Selectors and script filters run while planning, where no used pack's pool exists: a call through
  `maquettiste.use` there is a `TypeError` (MQ6016) that says so.
- **Project rule scripts use libraries through `uses` in `maquettiste.json`.** Same entry shape. The loader reads the
  closure's `pack.json` files and scripts into the snapshot (`ModelSnapshot.ScriptLibraries`), so editing a library
  script is a change the store sees by stat. The libraries' hash folds into every rule file's cache key, so a cached
  rule result is never served after a library changed. Calculation scripts (5.5) and seed rows scripts (3.4) do not
  reach them in phase 2: their values feed units through `x:` keys that name their own file only, so a library change
  would not render those units again (5.5, Not doing).
- **Incremental keys.** A partial of another pack records `t:<used pack>/<path>`, and the hasher resolves the name
  through `PackSet.Find`, so a used pack outside the run's selection hashes too. The static hash of a unit whose pack
  has `uses` folds, for each pack of its closure in dependency order, that pack's name, version, scripts hash and
  canonical parameters into the scripts field. For a pack without `uses` the hashed bytes are unchanged, so an upgrade
  renders nothing again.

#### Schema and files

`schemas/v1/pack.json` (+ the byte-identical copy under `samples/reference-app/.maquettiste/.schema/v1/`). New x-order,
and the `properties` written in that order:
`$schema, name, version, engine, description, uses, parameters, parameterSchema, scripts, usesSchemaDiff, units`.

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| `uses` | array of `{pack, version}`, both required, no other keys | `[]` | `pack` is `common.json#/$defs/key`; `version` a non-empty string |
| `units` | array of units | `[]` | No longer required; `minItems` removed. A pack without units is a library |

`schemas/v1/maquettiste.json` (+ copy): a top-level `uses` with the same item schema, default `[]`, in x-order after
`packs`. It lists the packs whose helpers the project's own scripts may call.

A library and two packs that use it:

```json
{ "name": "naming", "version": "1.4.0", "engine": ">=1.0 <2.0", "parameters": { "suffix": "Record" } }
```

```json
{ "name": "records", "version": "1.0.0", "engine": ">=1.0 <2.0",
  "uses": [{ "pack": "naming", "version": "^1.2.0" }],
  "units": [{ "id": "record", "template": "record.scriban", "for": "each entity",
              "output": "{{ entity.package.name }}/{{ naming.class_name entity }}.cs" }] }
```

`record.scriban` starts with `{{ include "naming:partials/header.scriban" }}`; `templates/naming/helpers.js` registers
`class_name`.

#### Loading, order and the closure

- `PackLoader.LoadAsync` reads the `uses` member of every `pack.json` under `templates/` (a light parse that tolerates
  a broken file; the broken file is reported only when that pack loads) to build the used-by map. It then loads the
  run packs and the closure of their `uses`, each pack once.
- Per entry: a name with no folder, or a name listed twice in one list, is MQ6042 at `/uses/<i>/pack`. A range the
  used pack's version does not satisfy, or a range that does not parse, is MQ6043 at `/uses/<i>/version`. A pack that
  names itself, or a cycle, is MQ6044 at the first entry of each pack in the cycle that leads back into it. Any of the
  three fails the pack, as MQ6001 does, so the run is invalid.
- A pack whose closure holds a pack that failed to load fails too (MQ6042 naming the failed pack), so no unit renders
  without the helpers it may call.
- `LoadedPack.Order` becomes the position in dependency order: Kahn's order with the earliest name first among the
  packs that are ready. Plan order (pack order, then key) follows it. `PackSet.Packs` keeps the run packs in that
  order; `PackSet.Used` holds the rest of the closure.
- `PackLoader.LoadClosureAsync(model, name, ct)` (new, internal) loads one pack whether or not it is enabled, as
  `LoadNamedAsync` does, with its closure, and returns a `PackSet` whose `Packs` is that pack and whose `Used` is the
  rest. The pack editor's read, previews, path listings, template context and explanations move to it;
  `LoadNamedAsync` stays for the callers that need one pack only.
- MQ6046 (warning, at `/name`): a loaded pack without units that no pack and no project `uses` lists. A whole-project
  load (`maquettiste validate`, `list_packs`, an unfiltered run) reports it.

#### Templates: includes and helper namespaces

- `PackTemplateLoader.GetPath` reads an optional `<pack>:` prefix, where `<pack>` matches `common.json#/$defs/key`,
  before normalizing the rest. The pack must be the unit's pack or in its closure, else the include fails the unit
  with MQ6006 ("Pack 'x' is not in the uses of pack 'y'"). Without a prefix, the pack is the one whose folder holds the
  including file, found from the caller's source path (the part before `#` for an inline `output` expression), which
  the template cache sets to the repo-relative path (`Rendering/TemplateCache.cs:201`).
- The loader returns the path as `<pack>:<path>`; `CreateTemplate` records `t:<pack>/<path>` and asks the cache for
  that pack's file, found with `PackSet.Find`. A path under another pack's `tests/` folder is refused (5.4).
- `DependencyHasher.TemplateHash` finds the pack with `PackSet.Find` instead of `PackSet.Packs`, so the `t:` key of a
  used pack's partial hashes its file whether or not the run selects that pack.
- `PackRuntime.Create` builds, for the unit's pack, one read-only `ScriptObject` per namespace it sees: its closure's
  packs that have scripts, and itself when it is used and has scripts. Each holds a `HelperFunction` per registered
  helper that calls the used pack's runtime (`RenderRun.Pack(used)`, created once per run). `RenderFiles` pushes the
  namespaces between the pack's root helpers and the unit's variables (`Rendering/RenderRun.cs:314-315`).
- MQ6045 at load: a used pack with scripts whose namespace equals a member of the builtin object (the template
  language's `string`, `array`, `date`, `math`, `object`, `regex`, `html`, `timespan` and the Maquettiste helpers), a
  keyword of the template language (`for`, `if`, `end`, `func`, `import`, `with`, `include` and the others) or a
  template variable (`BuiltinHelpers.Variables`). MQ6045 at render, beside MQ6013 in `PackRuntime.Create`: a namespace
  equal to a root helper of a pack that sees it. The second fails every unit of that pack, as MQ6013 does.
- A used pack's helper call leases from that pack's pool on first use in the unit and holds the lease to the end of
  the unit, which returns it. A used pack whose scripts fail to load fails every unit that sees it with that pack's
  MQ6016 or MQ6007.

#### Scripts and project rules

- `IScriptSandboxFactory.CreatePool` gains an overload with `libraries`: each `ScriptLibrary` names a pack and its
  helper names. The setup installs `maquettiste.use(name)`, which returns the frozen object built for that library;
  each member is a host function that calls `ScriptCallContext.Libraries.Call(pack, helper, args, caller)`, and is a
  `TypeError` when the call has no `Libraries` (while loading, and in selectors and filters). Arguments convert out as
  a script's results do (`ScriptValues`, a model proxy leaving as its object), and the result converts in as helper
  results do.
- `UnitRun` implements `IScriptLibraryCalls` for renders: it leases the used pack's sandbox and calls
  `CallHelper(helper, args, ctx)` with the unit's recorder and seed and the used pack's parameters. `ctx.Libraries` is
  the same object, so a used pack reaches its own closure. A failure keeps the used pack's script path and position;
  it is MQ6016 or MQ6007 for the unit. Transforms run in the unit's call context and reach used packs too.
- The model loader resolves `uses` in `maquettiste.json` with the same rules (MQ6042 and MQ6043 on `maquettiste.json` at
  `/uses/<i>/...`; problems inside a library's own `pack.json` are the pack loader's). For the closure it reads each
  `pack.json` and the scripts the pack loader would load (`PackFileRoles.Scripts`), adds those paths to the files it
  enumerates and stats (`ModelPaths` gains the kind `LibraryFile`, which the loader gives only to the paths it lists
  for the closure; `Classify` is unchanged for every other path), and builds one `ScriptLibrarySource` per pack: name,
  version, uses, effective parameters, scripts and a hash over all of them.
- `ModelValidator` creates one pool per script library beside the rule pool, sized like it, only when rule scripts
  run, and passes the rule pool the libraries. `RuleRun` implements `IScriptLibraryCalls` for rule calls, with the
  rule's recorder and the library's parameters. A library failure during a rule is MQ5002 on the rule file (MQ5003
  for a limit), naming the library script and line.
- `CachedRuleFiles` (`Validation/ModelValidator.cs:849`) returns each cached file with its hash replaced by
  `H("mq-rule-libraries-1", file hash, library hash₁, …)` over the snapshot's libraries when the project has `uses`.
  `RuleResultCache` compares that hash as it compares the file's own (`Validation/RuleResultCache.cs:42-52`), so a
  library change runs every cached rule again; with no `uses` the hashes are unchanged.

#### Public API

```csharp
namespace Maquettiste.Engine.Model;
public sealed record PackManifest
{   // existing members, plus:
    /// <summary>The packs this pack uses, in pack.json order.</summary>
    public IReadOnlyList<PackUse> Uses { get; init; } = [];
    /// <summary>The render units; empty for a library.</summary>
    public IReadOnlyList<PackUnit> Units { get; init; } = [];   // was required
}
/// <summary>A pack that a pack, or the project's scripts, use.</summary>
public sealed record PackUse
{   /// <summary>The used pack's name (its folder).</summary>
    public required string Pack { get; init; }
    /// <summary>A range in the engine-range syntax that the used pack's version must satisfy.</summary>
    public required string Version { get; init; }
}
public sealed record ProjectSettings { /* ... */ public IReadOnlyList<PackUse> Uses { get; init; } = []; }
public sealed class ModelSnapshot
{   /// <summary>The closure of maquettiste.json uses, in dependency order; empty without uses.</summary>
    public IReadOnlyList<ScriptLibrarySource> ScriptLibraries { get; }
}
/// <summary>A pack whose helpers the project's scripts may call, as the model loader read it.</summary>
public sealed record ScriptLibrarySource(string Pack, string Version, IReadOnlyList<string> Uses,
    IReadOnlyDictionary<string, JsonElement> Parameters, IReadOnlyList<ScriptSource> Scripts, string Hash);

namespace Maquettiste.Engine.Pipeline;
// LoadedPack keeps its parameters; Order becomes the position in dependency order (after every pack it uses, ties
// by ordinal name).
public sealed record LoadedPack /* (...) */
{   /// <summary>The names in its uses, in pack.json order.</summary>
    public IReadOnlyList<string> Uses { get; init; } = [];
    /// <summary>Its uses closure in dependency order, itself left out.</summary>
    public IReadOnlyList<string> Sees { get; init; } = [];
    /// <summary>The packs whose uses name it, ordinal, then "maquettiste.json" when the project's uses does.</summary>
    public IReadOnlyList<string> UsedBy { get; init; } = [];
}
public sealed record PackSet /* (...) */
{   /// <summary>Packs a run pack uses that the run does not select; never rendered or written.</summary>
    public IReadOnlyList<LoadedPack> Used { get; init; } = [];
    /// <summary>A loaded pack by name, run or used; null when neither.</summary>
    public LoadedPack? Find(string name);
}

namespace Maquettiste.Engine.Scripting;
public interface IScriptSandboxFactory
{   // existing CreatePool unchanged, plus:
    IScriptSandboxPool CreatePool(IReadOnlyList<ScriptSource> scripts, IReadOnlyList<ScriptLibrary> libraries,
        SandboxLimits limits, int size, CancellationToken ct)
        => libraries.Count == 0 ? CreatePool(scripts, limits, size, ct)
            : throw new NotSupportedException("This factory cannot reach other packs' helpers.");
}
/// <summary>A pack whose helpers maquettiste.use reaches: its name and its helper names, ordinal.</summary>
public sealed record ScriptLibrary(string Pack, IReadOnlyList<string> Helpers);
/// <summary>Calls a used pack's helper for the current call.</summary>
public interface IScriptLibraryCalls
{
    object? Call(string pack, string helper, IReadOnlyList<object?> args, ScriptCallContext caller);
}
public sealed record ScriptCallContext /* (...) */
{   /// <summary>Where maquettiste.use calls go; null when no pack is reachable (loading, selectors, filters).</summary>
    public IScriptLibraryCalls? Libraries { get; init; }
}

namespace Maquettiste.Engine;
public sealed record PackSummary /* (...) */
{   public IReadOnlyList<PackUse> Uses { get; init; } = [];
    public IReadOnlyList<string> UsedBy { get; init; } = [];
}
public sealed record PackDocument /* (...) */ { public IReadOnlyList<string> UsedBy { get; init; } = []; }
public sealed record PackRenameResult /* (...) */
{   /// <summary>The pack.json files and maquettiste.json whose uses were rewritten, ordinal.</summary>
    public IReadOnlyList<string> UsesUpdated { get; init; } = [];
    /// <summary>Repo-relative template and script files whose text names the old pack; not edited.</summary>
    public IReadOnlyList<string> TextReferences { get; init; } = [];
}
public sealed record TemplateContextResult /* (...) */
{   public IReadOnlyList<TemplateNamespace> Namespaces { get; init; } = []; }
/// <summary>A helper namespace a unit sees: its name, its pack and the pack's helper names, ordinal.</summary>
public sealed record TemplateNamespace(string Name, string Pack, IReadOnlyList<string> Helpers);
public sealed partial class GenerationService
{   // the existing overload calls this one with library: false.
    /// <summary>Creates a pack; with library, one without units, a partial and a helpers file (from: empty).</summary>
    public Task<PackWriteResult> CreatePackAsync(string pack, string from,
        IReadOnlyDictionary<string, IReadOnlyList<KeyValuePair<string, byte[]>>>? starters, bool library,
        CancellationToken ct);
}
```

`IPackLoader.LoadAsync` keeps its signature and also fills `PackSet.Used`. `PackUnit` in `docs/api/openapi.yaml` is
unchanged here; `PackSummary`, `PackDocument`, `PackRenameResult`, `NewPackRequest` (`library`; its record lives in
`src/Maquettiste.Functions/_functions/PackEndpoints.cs`), `TemplateContextResult` and `ProjectSettings` gain the
members above, and `PackFileInfo.usedBy` documents `include:<pack>:<path>`.

#### CLI and MCP

- `maquettiste pack new <name> --library`: a pack with no units, `partials/header.scriban`, a `helpers.js` with one
  helper, and (5.4) one helper test. `--library` with a `--from` other than `empty` is a usage error (exit 4).
  `--library` joins `CommandLine.FlagOptions` and the options `pack new` expects.
- `new_pack` gains `library` (boolean, default false), with the same meaning; so does `POST /api/packs`
  (`NewPackRequest.library`).
- `list_packs` (an assistant catalog tool, described in `Agent/tools.json`) and `get_pack`: rows carry `uses` and
  `usedBy`. A library partial that another pack includes with a literal name lists `include:<pack>:<path>` among its
  users (`PackFileRoles.DescribeAsync`).
- `delete_pack`: refused as `referenced` while a pack or `maquettiste.json` uses the pack; the diagnostics name them.
- `delete_pack_file` and `move_pack_file`: refused as `referenced` while another pack includes the file with a
  literal name. Both already refuse a file with `include:` users; the cross-pack users join that list.
- `rename_pack` and `maquettiste pack rename --apply`: the result carries `usesUpdated` and `textReferences`. The
  rewrite goes through the canonical `pack.json` writer and the settings save before the folder moves. A refused
  write renames nothing, and a failed move puts the entries back, as it puts the settings entry back today.
- `get_template_context`: `namespaces`.
- `maquettiste validate` and `list_packs` load every pack, so MQ6042 to MQ6046 appear there; MQ6042 and MQ6043 on
  `maquettiste.json` come from the model loader and appear in every validation, the MCP `validate` tool included.

#### Rules

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ6042 | E | A `uses` entry in `pack.json` or `maquettiste.json` names a pack that does not exist or did not load, or a list names one pack twice. | Fix the name |
| MQ6043 | E | A used pack's version does not satisfy the `uses` range, or the range does not parse. | Widen the range or update the pack |
| MQ6044 | E | A pack's `uses` names the pack itself or forms a cycle. | Remove one entry |
| MQ6045 | E | A used pack's helper namespace collides with a builtin, a template keyword or variable, or a root helper of a pack that sees it. | Rename the pack |
| MQ6046 | W | A pack has no units, and no pack and no project `uses` lists it. | Add a unit or a `uses` entry |

#### Editor follow-ups

Until these land, `pack.json` and `maquettiste.json` are edited through the generic JSON form. Generate workspace: a
"library" label on packs without units, the `uses` list with ranges, and "used by" on each pack. Template editor:
completion for namespaces and their helpers from `get_template_context`. Settings: the project's `uses`.

#### Docs

`packs/README.md` gains "Library packs" (layout, `uses`, cross-pack includes, namespaces, `maquettiste.use`, the
project's `uses` for rule scripts). `docs/user-guide.md` gains the same in the generation chapter, with the rules. A
new guide, `docs/guides/share-and-test-packs.md` (in the `mkdocs.yml` guides list after "Generate code"), walks the
billing example; 5.3 and 5.4 add to it. `docs/engineering/engine-design.md` sections 8 and 10, the Planning,
Rendering, Scripting, Loading and Validation READMEs, `src/Maquettiste.Cli/README.md` (`pack new --library`),
`docs/mcp.md` rows, both schema files, `docs/api/openapi.yaml` and `docs/releases.md` under `## 0.12.0 (unreleased)`.

**Not doing**

- A registry, downloads, lock files or references into version control for packs. Sharing across projects is a copied
  folder checked by its range; a shared registry is later.
- Two versions of one pack in a project. A pack is one folder per name.
- Overriding a single partial of a used pack, as `SPEC.md:918` sketches. A pack includes its own partial instead.
- Selectors, filters, transforms and type maps across packs. Precedence between several type maps would need its own
  rule; a library helper or partial covers a shared type mapping.
- Library helpers inside selectors and script filters. They run while planning, before any render pool exists; a
  selector that needs a shared rule calls a helper of its own pack.
- An alias for a namespace (`as`). A pack whose name collides is renamed, and the rename rewrites every `uses`.
- Rewriting template or script text on rename. The rename lists the files; the author edits them.
- Calling a used pack's unregistered top-level functions. Only registered helpers cross.

**Tests**

- `Planning/PackLoaderTests`: `Uses_naming_a_missing_pack_is_MQ6042`, `A_pack_listed_twice_in_uses_is_MQ6042`,
  `A_version_outside_the_range_is_MQ6043`, `An_unparseable_range_is_MQ6043`, `A_pack_using_itself_is_MQ6044`,
  `A_cycle_is_MQ6044_on_every_pack_in_it`, `A_requested_pack_loads_its_closure_as_used_packs`,
  `A_disabled_pack_loads_when_an_enabled_pack_uses_it`, `Packs_order_after_the_packs_they_use_then_by_name`,
  `A_project_without_uses_keeps_ordinal_order`, `A_pack_without_units_loads`,
  `A_pack_without_units_that_nothing_uses_is_MQ6046`, `A_used_pack_named_like_a_variable_is_MQ6045`,
  `A_used_pack_named_like_a_keyword_is_MQ6045`, `A_pack_whose_closure_failed_is_MQ6042`,
  `Load_closure_returns_the_pack_and_its_used_packs`.
- `Scripting/ScriptLibraryTests` (new): `Use_returns_the_library_helpers_frozen`,
  `Use_of_a_pack_outside_the_libraries_is_a_type_error`, `Use_at_load_time_returns_the_object`,
  `Calling_a_library_helper_while_loading_is_a_type_error`, `A_library_call_records_on_the_callers_recorder`,
  `A_library_helper_sees_its_own_parameters`, `Values_cross_engines_as_they_cross_into_templates`,
  `A_library_limit_is_MQ6007_at_the_library_script`, `A_library_reaches_its_own_closure`.
- `Rendering/LibraryRenderTests` (new):
  `A_prefixed_include_renders_the_used_partial_with_the_units_variables`,
  `An_unprefixed_include_in_a_used_partial_resolves_in_that_pack`,
  `A_used_partial_parses_with_default_delimiters_under_a_unit_with_custom_ones`,
  `A_namespaced_helper_runs_in_the_used_packs_pool_and_pipes`, `A_used_pack_reaches_its_own_namespace_in_its_units`,
  `An_include_of_a_pack_outside_the_closure_is_MQ6006`, `A_namespace_equal_to_a_root_helper_is_MQ6045`,
  `A_used_partial_records_its_own_template_key`, `A_used_pack_whose_scripts_fail_fails_every_unit_that_sees_it`,
  `A_selector_calling_a_library_helper_is_MQ6016`.
- `Planning/HashingAndStateTests.A_template_key_of_a_used_pack_outside_the_run_hashes_its_file`.
- `Planning/UnitPlannerTests`: `The_static_hash_of_a_pack_without_uses_is_unchanged`,
  `A_used_packs_version_script_or_parameter_changes_the_static_hash`.
- `Editor/PreviewBoundsTests.A_preview_session_sees_an_edit_of_a_used_partial`;
  `Editor/PackAuthoringMoreTests.Template_context_lists_the_namespaces_a_unit_sees`.
- `Validation/RuleLibraryTests` (new): `A_rule_calls_a_project_library_helper`,
  `A_library_change_runs_cached_rules_again`, `A_project_uses_entry_naming_a_missing_pack_is_MQ6042`,
  `A_project_uses_range_the_library_misses_is_MQ6043`, `A_library_failure_in_a_rule_is_MQ5002_naming_the_library`,
  `Editing_a_library_script_reloads_the_snapshot`.
  `Validation/RuleCacheTests.Without_project_uses_rule_cache_keys_are_unchanged`.
- `Editor/PackListTests.List_packs_shows_uses_used_by_and_libraries`; `Editor/PackRenameTests`:
  `Renaming_a_used_pack_rewrites_uses_in_packs_and_settings`, `Renaming_lists_files_whose_text_names_the_old_pack`;
  `Editor/PackAuthoringTests`: `Deleting_a_used_pack_is_referenced`,
  `Deleting_a_partial_another_pack_includes_is_referenced`, `Moving_a_partial_another_pack_includes_is_referenced`,
  `Create_pack_library_writes_a_pack_without_units`.
  `Cli` `PackNewTests` (in `OtherCommandTests.cs`): `Pack_new_library_writes_a_pack_without_units`,
  `Library_with_from_sql_ddl_is_a_usage_error`.
- **Acceptance:** `Integration/LibraryPackTests.Two_packs_share_a_naming_library_and_a_rule_uses_it` (new). Setup: a
  billing copy with three packs from `tests/fixtures/integration/library/`: `naming` 1.4.0 (no units;
  `partials/header.scriban`; `helpers.js` registering `class_name`, the element's name in Pascal case plus the
  parameter `suffix`, default `Record`), `records` and `services` (each one `each entity` unit; both use `naming`
  `^1.2.0`, include `naming:partials/header.scriban` and name files with `naming.class_name`). The project's
  `uses` lists `naming` `^1.0.0`, and `extensions/rules/class-names.js` registers `class-name-length` on entities:
  `maquettiste.use("naming").class_name(element)` longer than 20 characters is an error. Expected: generate writes
  `records/Billing/InvoiceRecord.cs` and `services/Billing/InvoiceRecordService.cs` for Invoice (and the same for
  the seven other entities, Product under `Catalog/`), both files open with the same header, and validate is clean
  (the longest names, `CustomerNoteRecord` and `RevenueMonthRecord`, have 18 characters). Then
  `packs.naming.parameters.suffix` becomes `RecordEntry`: one plan renders every entity unit of both packs again and
  nothing else, and validate reports `x/class-name-length` on InvoiceLine, InvoiceNote, CustomerNote and RevenueMonth
  (22 and 23 characters), not on Invoice (`InvoiceRecordEntry`, 18).
- **Acceptance:** `Integration/LibraryPackTests.A_range_the_library_does_not_satisfy_is_MQ6043`. Setup as above with
  `records` using `naming` `^2.0.0`. Expected: generate is invalid with MQ6043 at
  `.maquettiste/templates/records/pack.json` `/uses/0/version`, and nothing is written.

**Tasks**

- **F20. Manifest and settings.** `uses` in `pack.json` and `maquettiste.json`, `units` optional. No "Runs after": it
  starts beside 5.1's F1, and 5.1's F13, which also regenerates the editor's types, runs after it. Owns:
  `schemas/v1/pack.json` and `schemas/v1/maquettiste.json` with their copies, `Model/PackManifest.cs` (`Uses`,
  `PackUse`, `Units` default), `Model/ProjectSettings.cs` (`Uses`), `docs/api/openapi.yaml` (`ProjectSettings.uses`) and
  the generated editor files. Tests: `Json/SchemaConsistencyTests` (existing; the defaults and x-order match),
  `Json/PackManifestJsonTests.Uses_round_trips_and_units_may_be_absent` (new class). Verify: `E "~Json.|~Model."`;
  `copies`; `editor`.
- **F21. Loader: closure, ranges, cycles and order.** Runs after F20 and 5.1's F11 (both edit
  `Diagnostics/RuleCatalog.cs` and the `CatalogTests` pin). Owns: `Planning/PackLoader.cs` (the closure,
  `LoadClosureAsync`), `Planning/PackUses.cs` (new: used-by map, closure, ranges, cycle check, dependency order),
  `Pipeline/PipelineTypes.cs` (`LoadedPack.Uses`, `Sees`, `UsedBy`, the meaning of `Order`; `PackSet.Used`, `Find`),
  `Planning/EngineRange.cs` (comment: also `uses` ranges), `Diagnostics/RuleCatalog.cs` (MQ6042, MQ6043, MQ6044,
  MQ6046), `CatalogTests.cs` (+4), `Planning/README.md`. Tests: the `PackLoaderTests` above except the MQ6045 cases.
  Verify: `E "~Planning.|~CatalogTests"`; `P "~GoldenTests"`; `record`.
- **F22. Sandbox calls into used packs.** Owns: `Scripting/ScriptingContracts.cs` (`ScriptLibrary`,
  `IScriptLibraryCalls`, `ScriptCallContext.Libraries`, the `CreatePool` overload), `Scripting/ScriptSandboxFactory.cs`,
  `Scripting/ScriptSandboxPool.cs`, `Scripting/ScriptSandbox.cs`, `Scripting/ScriptSandbox.Calls.cs`,
  `Scripting/README.md`. Tests: `ScriptLibraryTests`. Verify: `E "~Scripting."`.
- **F23. Rendering: cross-pack includes and namespaces.** Runs after F21 and F22. Owns:
  `Rendering/TrackingTemplateContext.cs` (`PackTemplateLoader`, `CreateTemplate`), `Rendering/TemplateCache.cs`
  (`PackPaths` prefix parsing; another pack's file with default delimiters), `Rendering/RenderRun.cs` (namespaces in
  `PackRuntime`, used-pack runtimes, leases and `IScriptLibraryCalls` in `UnitRun`, MQ6045 at render),
  `Planning/PackUses.cs` (MQ6045 at load), `Planning/DependencyHasher.cs` (`TemplateHash` through `PackSet.Find`),
  `Diagnostics/RuleCatalog.cs` (MQ6045), `CatalogTests.cs` (+1), `Rendering/README.md`. Tests:
  `LibraryRenderTests`, the MQ6045 cases of `PackLoaderTests`, the `HashingAndStateTests` case. Verify:
  `E "~Rendering.|~Planning.|~CatalogTests"`; `P "~GoldenTests|~CultureTests"`; `record`.
- **F24. Static hashes, previews and template context.** Runs after F23 and 5.1's F13 (both regenerate the editor's
  types). Owns: `Planning/UnitPlanner.cs` (`UnitHashPrefix` scripts field; `BuildParts` lists `uses:<pack>=<version>`),
  `GenerationService.Packs.cs` (preview sessions load the closure with `LoadClosureAsync` and keep its `PackSet`;
  `PackStamp` covers the closure's folders), `GenerationService.Authoring.cs` (`TemplateNamespace`, `Namespaces`; path
  listings, template context and explanations load the closure), `docs/api/openapi.yaml`
  (`TemplateContextResult.namespaces`), the generated editor files. Tests: the `UnitPlannerTests`, `PreviewBoundsTests`
  and `PackAuthoringMoreTests` cases above. Verify: `E "~Planning.UnitPlannerTests|~Editor."`;
  `F "~PackAuthoringEndpointTests"`; `editor`.
- **F25. Project rule scripts use libraries.** Runs after F21 and F22. Owns: `Loading/ModelLoader.cs` (the closure's
  files in the enumeration; `ScriptLibrarySource`s), `Loading/ModelPaths.cs` (`LibraryFile`),
  `Loading/ProjectUses.cs` (new: MQ6042 and MQ6043 on `maquettiste.json`), `Model/ModelSnapshot.cs`
  (`ScriptLibraries`), `Model/ScriptLibrarySource.cs` (new), `Validation/ModelValidator.cs` (library pools, `RuleRun`
  calls, `CachedRuleFiles`), `Loading/README.md`, `Validation/README.md`. Tests: `RuleLibraryTests`, the
  `RuleCacheTests` case. Verify: `E "~Validation.|~Loading.|~Scripting.|~Store."`.
- **F26. Authoring: list, delete, move, rename and the library starter.** Runs after F24 (both edit
  `GenerationService.Packs.cs`, `docs/api/openapi.yaml` and the generated editor files), 5.1's F15 (`Agent/tools.json`,
  `docs/mcp.md`) and 5.1's F13 (`src/editor/src/mocks/recorded/pack-sql-ddl.json`). Owns: `Editor/PackAuthoring.cs`
  (`UsedBy` on `PackDocument`; the pack read loads the closure; the file list also scans the packs whose `uses` name
  the pack for literal `<pack>:` includes; `CreateAsync` `library`), `Editor/PackAuthoring.More.cs` (move refused for
  cross-pack users), `Editor/PackAuthoring.Remove.cs`, `Editor/PackAuthoring.Rename.cs`, `Editor/PackList.cs`
  (`PackSummary.Uses`, `UsedBy`), `Planning/PackFileRoles.cs` (the `include:<pack>:<path>` users),
  `GenerationService.Packs.cs` (the `CreatePackAsync` overload), `Agent/tools.json` (the `list_packs` description),
  `src/Maquettiste.Cli/Commands/PackNewCommand.cs` (`--library`), `src/Maquettiste.Cli/StarterPacks.cs`
  (`Library()`), `src/Maquettiste.Cli/CommandLine.cs`, `src/Maquettiste.Cli/CliApp.cs` (usage),
  `src/Maquettiste.Cli/README.md`, `src/Maquettiste.Cli/Mcp/ModelTools.Packs.cs` (`new_pack` `library`; the
  descriptions of `get_pack`, `delete_pack`, `delete_pack_file`, `move_pack_file`, `rename_pack`),
  `src/Maquettiste.Functions/_functions/PackEndpoints.cs` (`NewPackRequest.Library`), `docs/mcp.md`,
  `docs/api/openapi.yaml` (`PackSummary`, `PackDocument`, `PackRenameResult`, `NewPackRequest`,
  `PackFileInfo.usedBy`), the generated editor files, `src/editor/src/mocks/recorded/packs.json` and
  `pack-sql-ddl.json` (recorded again). Tests: the `PackListTests`, `PackRenameTests`, `PackAuthoringTests` and
  `PackNewTests` cases above. Verify: `E "~Editor.|~AgentToolsTests"`;
  `C "~PackNewTests|~McpSurfaceTests|~McpPackToolTests"`; `MAQUETTISTE_RECORD=1 F "~PackEndpointTests"`, then
  `F "~PackEndpointTests|~PackAuthoringEndpointTests"`; `editor`.
- **F27. Docs and acceptance.** Runs after F24, F25, F26 and 5.1's F16 (`docs/releases.md`, `docs/user-guide.md`,
  `packs/README.md`). Owns: `packs/README.md` ("Library packs"),
  `docs/user-guide.md`, `docs/guides/share-and-test-packs.md` (new), `mkdocs.yml` (nav),
  `docs/engineering/engine-design.md` sections 8 and 10, `docs/releases.md`, `tests/fixtures/integration/library/`
  (new), `tests/Maquettiste.Engine.Tests/Integration/LibraryPackTests.cs` (new). Tests: both acceptance tests.
  Verify: `E "~Integration.LibraryPackTests"`.

Cross-section: 5.5 (calculation scripts) and 3.4 (seed rows scripts) do not pass `ModelSnapshot.ScriptLibraries` to
their pools in phase 2. F25 and those sections' loader tasks both edit `Loading/ModelLoader.cs`, `Loading/ModelPaths.cs`
and `Model/ModelSnapshot.cs`, and 5.6 edits `Validation/ModelValidator.cs`, so they land one after the other. F20 edits
`schemas/v1/pack.json` beside 4.1, 4.2 and 4.3 (the unit `for` pattern) and `schemas/v1/maquettiste.json` beside 6.3
(`environments`).

### 5.3 Packs that read each other's exports

**Why every team** (scope 5.3)

Generated code refers to other generated code: a service class calls a record class, a client calls a route, a
repository maps a row type, a test builds a request. When two packs produce the two sides, today the consuming pack
repeats the producing pack's naming rules, so a renamed class in one pack breaks the other until someone mirrors the
change by hand. Exports let the producing pack state each name once, per element, and let every pack that uses it read
those names by element id. A naming change then flows through in one plan, and only the files that read a changed name
render again. This holds for any pair of packs and any languages, which is why it belongs to the engine.

**Today**

- No pack can read another pack's names. `get_pack_outputs` lists a pack's files with unit, element and state
  (`src/Maquettiste.Cli/Mcp/ModelTools.Packs.cs:207-209`), which templates cannot read.
- Packs plan in pack order, then by unit key (`Planning/UnitPlanner.cs:153-163`); pack order is ordinal by name
  (`Planning/PackLoader.cs:38-42`) until 5.2 makes it dependency order. Units render on parallel workers and stream in
  plan order (`Rendering/Renderer.cs:12-18`), so no unit may wait on another's rendered text.
- A unit's state is its input hash, read keys and outputs (`Pipeline/PipelineTypes.cs:163`), stored in unit state
  format 4 (`Planning/UnitStateStore.cs:31`). Dependency keys use the prefixes `e`, `k`, `r`, `s`, `l`, `t`, `d` and
  `n` (`Planning/DependencyHasher.cs:196-214`), and `PlanExplainer.KindOf` reads a one-letter prefix only
  (`Generation/PlanExplainer.cs:207`).
- Templates read properties, not methods (`Rendering/TemplateValues.cs:186-190`), so lookups across the model are
  helpers such as `table_named` and `lookup` (`Rendering/BuiltinHelpers.cs:200`, `:206`).
- An `output` expression is a template rendered with the unit's context after its main template
  (`Rendering/RenderRun.cs:323-328`, `:424-433`).
- A run plans every run pack, applies a scoped run's filter to the plan, then builds the hasher
  (`Generation/GenerationRun.cs:233-253`), and loads unit states of the run packs only (`:198-199`).
  `UnitPlanner.WhyNot` answers for one unit and element by listing the unit's whole scope in a pool it creates for the
  call (`Planning/UnitPlanner.cs:200-221`), which suits one preview but not every element of a run.
- Applying a plan recomputes each planned unit's input hash from its recorded read keys
  (`GenerationService.cs:583-590`), so any new key must be computable at apply time.

**Decisions taken here**

- **Exports are declared per unit, as templates.** A unit's `exports` maps a name to a template, rendered with the
  unit's variables as `output` is, or to `{ "each": "<list member>", "value": "<template>" }`, rendered once per item
  of that member of the element with the item as `item` and keyed by the item's id. Values are text: line endings are
  LF and leading and trailing whitespace is trimmed, as output paths are. Names are snake_case. The product reserves no
  export name; packs choose `class_name`, `namespace`, `file`, `members` or anything else.
- **Exports are computed from the model, never read back from rendered files.** A consumer does not wait for the
  exporting unit to render. Each record is evaluated on demand from the exporting units' declarations, or taken from
  their stored states when the inputs of their exports are unchanged. Rendering order therefore cannot change a byte,
  and the renderer keeps its parallel streaming. The plan lists packs in dependency order (5.2) and a cycle is an error
  (MQ6044, 5.2). The same on-demand evaluation serves previews and scoped runs, which evaluate the records of the
  elements they touch and nothing else.
- **`exports` to declare, `exported` to read.** `outputs` already means a pack's generated files, so the scope's
  `element.outputs["<pack>"]` becomes the helper `exported <element> "<pack>"`. A helper, because templates read
  properties, not methods, and because a record is not part of the resolved model: it exists per run, after planning.
  `exported model "<pack>"` reads the exports of the pack's `for: model` units. A unit's own templates, its `output`
  and its companion read its own values as the variable `exports`, so a file's name and the name other packs read
  have one definition.
- **Keys are ids.** A record is keyed by pack name, the pack's key (5.2), and element id; `each` maps by item id.
  `exported` takes an element, an element id or the `model` variable, never a name.
- **One record per pack and element.** It merges the exports of every unit of that pack that plans the element, with
  its scope, `where` filter and skip hints applied as the planner applies them. Two units that give one name different
  values for one element is MQ6048; equal values are one value. An element the pack plans no exporting unit for reads
  `null`, by the null contract.
- **Which units plan an element comes from the plan.** A run's planner already plans the run packs; it also plans the
  units of used packs (5.2's `PackSet.Used`) that declare exports, into `UnitPlan.ExportOnly`, which nothing renders
  or writes. A scoped run keeps the unfiltered plan for its exports. Only previews and `get_pack_exports`, bounded to
  the elements they show (at most `MaxRenderedPaths`), ask `UnitPlanner.WhyNot` per unit and element.
- **What a template may read.** A unit's template, partials and `output` may read the exports of its own pack and of
  the packs it sees (its uses closure, 5.2). An export template may read only the packs it sees, never its own pack,
  so evaluation cannot recurse. Anything else is MQ6047. A record that failed to evaluate fails its readers with
  MQ6050 rather than rendering empty names.
- **Export templates see less.** They see `model`, `element` and its scope alias, `pack`, `project`, `unit`,
  `mapping`, `mappings`, `hints`, the pack's helpers and namespaces (5.2), includes and `exported`. They do not see
  `data` (transforms do not run for exports, which keeps an export-only evaluation cheap), `schema_diff` (a name
  must not depend on a baseline) or `exports`.
- **Incremental key `px:<pack>/<id>`** (3.4 takes `x` for extension files, 4.2 `c` for custom kinds and 5.5 `tr`), or
  `px:<pack>` for model-scope exports, hashed as `H("mq-exports-1", canonical record)`, `absent` when no unit exports
  for the element. A failed record hashes its sorted diagnostic messages, so its readers render again, and fail,
  until it is fixed. A consumer renders again when a record it read changed, and only then. A change that leaves the
  values alone, such as an edit to the exporting template's body, renders only the exporting units.
- **Export inputs are kept apart.** Evaluating a unit's exports records its read keys on their own recorder, and
  `UnitExports` holds the values, those keys and `InputHash(static hash, export keys)`. The keys also join the unit's
  read keys, so the unit renders again when they change. Whether stored values still serve depends on the export keys
  alone: a unit's main template may read its own pack's exports (another element's), but its export templates may
  not, so checking a stored record never needs the record being checked.
- **Stored records.** Every unit that declares exports evaluates them once per run, through the run's export table,
  when it renders or when a reader needs them, and never takes stored values for its own render. Its state stores
  its `UnitExports` (unit state format 5). A later run serves them to readers while their input hash, recomputed over
  the stored export keys, is unchanged, whether or not the unit itself renders again. Forced and check runs reuse
  nothing.
- **Recorded in the plan.** `PlanUnit.Exports` holds the `UnitExports` of every planned unit that declares exports,
  rendered or skipped. `GenerationPlan.Exports` holds the exporting units the plan used without planning them: units
  of used packs and units outside a scoped run's selection. Apply seeds the records from the plan and checks each of
  those units' export input hashes as it checks a planned unit's. Apply never evaluates, and a changed input is
  `Stale`.

#### Schema

`schemas/v1/pack.json` (+ copy), unit: `exports` after `transforms` in x-order.

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| `exports` | object; names `^[a-z][a-z0-9_]*$` | `{}` | Each value a non-empty template string, or `{each, value}` (x-order `each, value`) |
| `exports.<name>.each` | string, `^[a-z][a-z0-9_]*$` | none | A list member of the element, as templates name it (`attributes`, `columns`, an enum's `members`) |
| `exports.<name>.value` | string, minLength 1 | none | Rendered per item, with `item` |

The unit's canonical JSON in the static hash leaves out an empty `exports`, as it leaves out empty `sources` and
`tokens` (`Planning/UnitPlanner.cs:493-504`), on every path, the early return for a unit with both `sources` and
`tokens` included, so units without exports keep their static hash.

```json
{ "id": "record", "template": "record.scriban", "for": "each entity",
  "output": "{{ entity.package.name }}/{{ exports.class_name }}.cs",
  "exports": {
    "class_name": "{{ pascal entity.name }}Record",
    "namespace": "{{ project.properties.baseNamespace }}.{{ entity.package.name }}",
    "members": { "each": "attributes", "value": "{{ pascal item.name }}" } } }
```

A services template in a pack whose `uses` lists `records`:

```scriban
{{ r = entity | exported "records" }}
public sealed class {{ pascal entity.name }}Service
{
    public {{ r.namespace }}.{{ r.class_name }} Load(Guid id) => ...;
{{ for a in entity.attributes }}    // {{ r.members[a.id] }}
{{ end }}}
```

#### The export table

`Rendering/Exports.cs` (new, internal) holds one `ExportTable` per run, preview session or apply. The run's
`RenderContext` carries it (`RenderContext.Exports`, internal). The table and the hasher each need the other, so the
table is created first and bound to the hasher once it exists.

- `Record(pack, elementId)` returns the merged record of a pack for an element (`null` id: model scope). In a run it
  takes the units of the unfiltered plan and of `UnitPlan.ExportOnly` that declare exports and plan that key, indexed
  once by (pack, element id). In a preview or `get_pack_exports` it finds the pack in the session's `PackSet` (run or
  used, 5.2), resolves the element with `ResolvedModel.Find` and keeps the units with `exports` for which
  `UnitPlanner.WhyNot` answers null. For each unit it takes the values (below), then merges them in unit key order. A
  conflict is MQ6048 at the second unit's `/units/<i>/exports/<name>`.
- `Unit(plannedUnit)` returns one unit's `UnitExports` for readers. When the table may reuse states, and the unit's
  pack has a stored state for its key whose `Exports.InputHash` equals `InputHash(static hash, Exports.ReadKeys)`
  recomputed through the run's hasher, the stored values serve. Otherwise it returns `Evaluate(plannedUnit)`.
- `Evaluate(plannedUnit)` renders each export template in a `UnitRun` of that unit: values as text, `each` items by
  id (MQ6049 for a member that is not a list of items with string ids, or two items with one id), errors as MQ6006 at
  the export's pointer in `pack.json`. The unit's own render takes its values from here, never from a stored state.
  Stored values serve only while their inputs are unchanged, so both give the same bytes.
- All three are cached (`ConcurrentDictionary` of `Lazy`), so each record and each unit is computed once per run
  whatever the number of readers or threads. A failed record keeps its diagnostics, which go to each reader beside
  its MQ6050; `Outcomes.Sort` removes the repeats.
- Units of used packs, and units outside a scoped run's selection, whose values the table served are listed for the
  plan (`GenerationPlan.Exports`) with their `UnitExports`: the evaluation's, or the stored state's.
- The evaluator holds one `RenderRun` for the table's lifetime, whose sandbox pools are sized to the run's
  parallelism, created on first use and disposed with the table; the renderer's units keep their own. Evaluation
  never recurses into the same pack (`uses` is acyclic and an export template cannot read its own pack), so no
  worker waits on a pool it holds or on a record it is computing.
- A run loads the unit states of the used packs that have exporting units, read only, beside the run packs' states;
  nothing saves them. A used pack that is never generated therefore has its exports evaluated in each run that reads
  them, once per element.

#### Reading exports in templates

- `exported <element | id | model> "<pack>"` (`Rendering/BuiltinHelpers.cs`). It checks MQ6047 against the unit's
  pack and closure (against the closure alone inside an export template), records `px:<pack>/<id>` or `px:<pack>`,
  and returns the record as a read-only map, `null` when there is none. A failed record fails the unit with MQ6050,
  naming the exporting unit and its first diagnostic.
- The variable `exports` is the unit's own values as a read-only map, `null` when the unit declares none. `UnitRun`
  takes them from `ExportTable.Evaluate` before the main template and adds their export keys to its recorder.
  `RenderedUnit.Exports` carries the `UnitExports` to the writer, which stores them in the unit state.
- `exports` joins `BuiltinHelpers.Variables` and `exported` joins `BuiltinHelpers.Names`, so MQ6013 (a pack helper)
  and MQ6045 (a namespace, 5.2) report collisions with them.

#### Incremental keys, unit states and plans

- `DependencyHasher.Compute` gains `"px" => ExportHash(rest)` through the bound `ExportTable`.
  `EngineServices.CreateHasher` takes the table. `GenerationRun.PrepareAsync` builds the table from the plan before it
  applies a scoped run's filter, with `reuseStates` off for forced and check runs, and keeps it in `PreparedRun`.
- `UnitPlanner.PlanAsync` plans, after the run packs, the units of `PackSet.Used` that declare exports, into
  `UnitPlan.ExportOnly`, with their static hashes. Plan order and `UnitPlan.Units` are unchanged.
- `UnitStateStore` format 5 (`.v5.bin`) stores an optional `UnitExports` per state: the values as canonical JSON, the
  export keys through the state file's key table, and the input hash. A format 4 file loads empty, as earlier formats
  do, so the first run after the upgrade renders every unit once; saving deletes the format 4 file as it deletes the
  older ones.
- `PlanCapture` writes `PlanUnit.Exports` for every planned unit with exports (rendered: from the render; skipped:
  from the table) and `GenerationPlan.Exports` from the table's list.
- `ApplyLockedAsync` (`GenerationService.cs:575`) seeds a table from the plan before it recomputes input hashes; the
  units come from the plan the apply prepares again (unfiltered, with `ExportOnly`), the values from the stored plan.
  For each `GenerationPlan.Exports` entry it recomputes `InputHash(static hash, export keys)` and adds the key to
  `StaleUnits` when it differs or the unit no longer exists. A read of a record whose units the plan did not seed makes
  the plan stale; it is never evaluated at apply. The states apply writes for skipped and rendered units carry their
  `PlanUnit.Exports` (`GenerationService.cs:653`, `Writing/WriteRun.cs:924`).
- `PlanExplainer.KindOf` reads the prefix up to the colon, so `px:` keys map to the cause kind `exports`: "The exports
  of Invoice (entity) from records changed" or "The model exports of records changed".

#### Previews and scoped runs

- A preview session (5.2 loads the closure) builds its own table without stored states, beside its preview hasher
  (`GenerationService.Packs.cs:418`); path listings (`unit_paths`) render `output` with the same table. Rendering one
  unit for one element evaluates the records that unit reads, typically one element of each used pack, and nothing
  else. `explain_unit` prepares a dry run (`GenerationService.Authoring.cs:315`) and so gets the run's table.
- A run limited by unit or element (`--unit`, `--element`) builds the same table as a full run from its unfiltered
  plan. Only the records its units read are evaluated; `GenerationPlan.Exports` lists the units behind them that the
  selection left out.
- `generate --pack services` loads `records` as a used pack (5.2). Its exporting units are planned for exports only
  and neither render nor write.

#### Public API

```csharp
namespace Maquettiste.Engine.Model;
public sealed record PackUnit
{   // existing members, plus:
    /// <summary>Names this unit exports per element: a template, or a template per item of a list member.</summary>
    public IReadOnlyDictionary<string, PackExport> Exports { get; init; }
        = ImmutableSortedDictionary<string, PackExport>.Empty;
}
/// <summary>One export: a template string in JSON, or { each, value }.</summary>
[JsonConverter(typeof(PackExportJsonConverter))]   // in Model/Unions.cs, beside DescriptionJsonConverter
public sealed record PackExport
{   public required string Value { get; init; }
    /// <summary>A list member of the element; each item renders Value with `item`, keyed by its id.</summary>
    public string? Each { get; init; }
}

namespace Maquettiste.Engine.Pipeline;
/// <summary>A unit's export values, the keys their evaluation read (ordinal) and the input hash over them.</summary>
public sealed record UnitExports(JsonElement Values, IReadOnlyList<string> ReadKeys, string InputHash);
public sealed record UnitPlan /* (...) */
{   /// <summary>Exporting units of used packs, planned for their exports only; never rendered or written.</summary>
    public IReadOnlyList<PlannedUnit> ExportOnly { get; init; } = [];
}
public sealed record UnitState /* (...) */ { public UnitExports? Exports { get; init; } }   // format 5
public sealed record RenderedUnit /* (...) */ { public UnitExports? Exports { get; init; } }

namespace Maquettiste.Engine;
public sealed record PlanUnit /* (...) */ { public UnitExports? Exports { get; init; } }
public sealed record GenerationPlan /* (...) */ { public IReadOnlyList<PlanExport> Exports { get; init; } = []; }
/// <summary>An exporting unit whose values the plan used without planning it.</summary>
public sealed record PlanExport(string Key, UnitExports Exports);
public sealed record PackExportsResult(string Pack, IReadOnlyList<ElementExports> Elements,
    IReadOnlyList<Diagnostic> Diagnostics);
/// <param name="ElementId">The element, or null for model scope.</param>
/// <param name="Values">The merged record, or null when no unit exports for it.</param>
/// <param name="Units">The keys of the units whose exports it merges, ordinal.</param>
public sealed record ElementExports(string? ElementId, string? Name, string? Kind, JsonElement? Values,
    IReadOnlyList<string> Units);
public sealed record TemplateContextResult /* (...) */
{   public IReadOnlyList<TemplateExportSource> ExportsFrom { get; init; } = []; }
/// <summary>A pack whose exports a unit may read, with the names its units declare, ordinal.</summary>
public sealed record TemplateExportSource(string Pack, IReadOnlyList<string> Names);
public sealed partial class GenerationService
{   /// <summary>A pack's export records for at most MaxRenderedPaths elements, or its model-scope record when
    /// elementIds is empty; evaluated now, under the preview deadline. Nothing is written.</summary>
    public Task<PackExportsResult> GetPackExportsAsync(string pack, IReadOnlyList<string>? elementIds,
        CancellationToken ct);
}
```

`docs/api/openapi.yaml`: `PackUnit.exports`, `UnitExports`, `PlanUnit.exports`, `GenerationPlan.exports`,
`PlanExport`, `TemplateContextResult.exportsFrom`.

#### CLI and MCP

- `maquettiste pack exports <pack> [--element <id>]... [--format text|json]`: the records of the elements asked (at
  most 20), or the pack's model-scope record without `--element`. Text: one block per element (`Invoice (entity)`),
  one line per value (`class_name = InvoiceRecord`, `members/<attribute id> = Number`). Exit 1 when a record failed.
- `get_pack_exports` (new): `pack` (required), `elementIds` (at most 20). Answers `PackExportsResult`.
- `get_plan` (an assistant catalog tool, described in `Agent/tools.json`; with `units`): units carry `exports`; the
  plan carries `exports`. `plan` and `generate` answers are unchanged.
- `explain_unit`: causes of kind `exports`. `get_template_context`: `exportsFrom`.

#### Rules

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ6047 | E | A template reads the exports of a pack its pack does not see, or an export template reads its own pack's exports. | Add the pack to `uses` |
| MQ6048 | E | Two units of one pack export one name for one element with different values. | Rename one export |
| MQ6049 | E | An export's `each` names a member that is not a list of items with ids, or two items share an id. | Fix `each` |
| MQ6050 | E | A unit reads exports that failed to evaluate; it is not rendered. | Fix the exporting unit |

Each fails the unit that meets it, as a render error does: the pack's other units render and the run is invalid.
MQ6048 and MQ6049 found while evaluating for another pack reach its readers through MQ6050. `Outcomes.IsUnitLevel`,
which classifies planning errors, does not change.

#### Editor follow-ups

The unit form gains an Exports table (name, template, optional `each`). The plan view shows a unit's exports and the
`exports` cause. Template completion offers `exported` with the packs and names from `exportsFrom`. Until then,
`exports` is edited in the generic `pack.json` form.

#### Docs

`packs/README.md` gains "Exports" (declaring, `exports` in `output`, `exported`, `each` by id, what export templates
see, the rules). `docs/user-guide.md` gains the same in the generation chapter. `docs/guides/share-and-test-packs.md`
gains the records and services example. `docs/engineering/engine-design.md` sections 8 (unit `exports`), 9
(`exported`, `exports`) and 11 (`px:` keys, unit state format 5), the Rendering, Planning and Generation READMEs,
`src/Maquettiste.Cli/README.md` (`pack exports`), `docs/mcp.md`, `docs/api/openapi.yaml` and `docs/releases.md`.

**Not doing**

- Exports read back from rendered files, or written by template code while it renders. Either would order rendering
  and tie a consumer to the exporting unit's render succeeding.
- Exports in JavaScript selectors, filters, transforms or helpers. A template passes the values a helper needs.
- Values other than text and id-keyed maps of text. Names, namespaces, paths and members are text.
- Reading another pack's generated files or manifest from a template. `get_pack_outputs` stays a tool for agents.
- Exports across projects. A pack reads only the packs of its own project.
- Storing the exports of used packs that a run does not generate. They are evaluated again in the next run that
  reads them; generating the used pack stores them.

**Tests**

- `Json/PackManifestJsonTests.Exports_take_a_template_or_each_and_value`.
- `Planning/UnitPlannerTests`: `A_unit_without_exports_keeps_its_static_hash`,
  `A_copy_unit_with_sources_and_tokens_keeps_its_static_hash`, `Changing_an_export_changes_the_static_hash`,
  `Exporting_units_of_used_packs_are_planned_export_only`.
- `Rendering/ExportTests` (new): `A_unit_reads_its_own_exports_in_its_template_and_output`,
  `Exported_returns_a_used_packs_record_for_an_element`, `Exported_model_returns_the_model_units_record`,
  `Exported_takes_an_element_id`, `Each_maps_items_by_id`, `Values_are_trimmed_with_lf_endings`,
  `An_element_the_pack_does_not_plan_reads_null`, `A_skip_hint_leaves_no_record`,
  `Reading_a_pack_outside_the_closure_is_MQ6047`, `An_export_template_reading_its_own_pack_is_MQ6047`,
  `Two_units_giving_one_name_different_values_is_MQ6048`, `Equal_values_from_two_units_merge`,
  `Each_over_items_without_ids_is_MQ6049`, `Two_items_with_one_id_is_MQ6049`, `A_reader_of_a_failed_record_is_MQ6050`,
  `Export_templates_do_not_see_data_schema_diff_or_exports`, `A_helper_named_exported_is_MQ6013`.
- `Planning/HashingAndStateTests`: `An_exports_key_hashes_the_canonical_record`,
  `An_exports_key_with_no_record_is_absent`, `Unit_states_round_trip_exports`, `A_format_4_state_file_loads_empty`.
- `Generation/ExportRunTests` (new): `A_consumer_is_skipped_when_only_the_exporting_template_body_changes`,
  `A_consumer_renders_when_an_export_value_changes`, `A_skipped_exporting_unit_serves_its_stored_record`,
  `Two_units_reading_each_others_exports_in_one_pack_skip_on_a_second_run`,
  `Forced_and_check_runs_evaluate_every_record`, `Generating_the_consumer_alone_writes_nothing_for_the_used_pack`,
  `Output_is_identical_with_one_and_eight_jobs`, `A_scoped_run_evaluates_only_the_records_its_units_read`.
- `Generation/PlanApplyTests`: `The_plan_records_exports_of_rendered_skipped_and_export_only_units`,
  `Apply_seeds_exports_from_the_plan_without_evaluating`, `Apply_is_stale_when_an_export_only_units_inputs_changed`.
- `Editor/PlanCauseTests.An_exports_key_reads_as_the_exports_of_the_element`;
  `Editor/PreviewBoundsTests.A_preview_evaluates_the_records_of_its_element_only`;
  `Editor/PackAuthoringMoreTests.Template_context_lists_the_exports_a_unit_may_read`.
- `Cli`: `McpPackToolTests.Get_pack_exports_returns_the_records_of_the_elements_asked`,
  `PackExportsCommandTests.Pack_exports_prints_one_block_per_element` (new).
- **Acceptance (scope 5.3):**
  `Integration/PackExportTests.A_services_pack_takes_class_names_from_records_and_a_rename_flows_through_in_one_plan`
  (new). Setup: a billing copy with two packs from `tests/fixtures/integration/exports/`. `records` has the `record`
  unit shown above (output under `records`). `services` uses `records` `^1.0.0` and has a `service` unit (`each
  entity`, the template above, output under `services`) and a `status` unit (`each enum`, reads no exports). The
  project property `baseNamespace` is `App`. Expected after a first apply: `services/Billing/InvoiceService.cs`
  returns `App.Billing.InvoiceRecord` and names one member per attribute of Invoice by attribute id (`Number`,
  `IssuedOn` and `Total` among them, with the `CreatedAt`, `UpdatedAt` and `DeletedAt` that `audited` and
  `soft-delete` add). Then `class_name` becomes `{{ pascal entity.name }}Row`. Expected: one plan adds
  `records/Billing/InvoiceRow.cs` and deletes `records/Billing/InvoiceRecord.cs` (likewise for the seven other
  entities, Product under `Catalog/`), modifies the eight service files, and skips the `status` unit for InvoiceStatus
  with reason `unchanged`. The `service` unit for Invoice has a cause of kind `exports`, and the plan's
  `records/record:<Invoice id>` unit carries `exports.values.class_name = "InvoiceRow"`. After apply, a second plan
  renders no unit.
- **Acceptance:** `Integration/PackExportTests.Renaming_an_entity_renders_only_its_two_units`. Setup as above after
  the first apply. Invoice is renamed `Bill` through `save_element`. Expected: one plan renders the `record` and
  `service` units of that entity and skips every other unit.
- **Acceptance:** `Integration/PackExportTests.Generating_services_alone_uses_records_exports`. Setup: the first
  billing copy, `generate --pack services` only. Expected: the eight service files as above; nothing is written under
  `records/`, no `records` manifest exists, and the plan's `exports` lists the eight `record` units.
- **Acceptance:** `Integration/PackExportTests.A_uses_cycle_between_records_and_services_is_MQ6044`. Setup: `records`
  also uses `services`. Expected: generate is invalid with MQ6044 on both `pack.json` files, and nothing is written.

**Tasks**

- **F30. Unit `exports` in the manifest.** Runs after F20 and F26 (`Planning/UnitPlanner.cs` follows F24;
  `docs/api/openapi.yaml` and the generated editor files follow F26). Owns: `schemas/v1/pack.json` (unit `exports`)
  and its copy, `Model/PackManifest.cs` (`PackUnit.Exports`, `PackExport`), `Model/Unions.cs`
  (`PackExportJsonConverter`), `Planning/UnitPlanner.cs` (`UnitJson` leaves out empty `exports` on every path),
  `docs/api/openapi.yaml` (`PackUnit.exports`), the generated editor files. Tests: the `PackManifestJsonTests` case and
  the static-hash cases of `UnitPlannerTests`. Verify: `E "~Json.|~Planning.UnitPlannerTests"`; `P "~GoldenTests"`;
  `copies`; `editor`.
- **F31. The export table, `exported` and `exports`.** Runs after F30, F21 and F23. Owns: `Rendering/Exports.cs` (new),
  `Rendering/RenderRun.cs` (`UnitRun` takes its exports from the table, the `exports` variable, export-template
  variables), `Rendering/BuiltinHelpers.cs` (`exported`, `Names`, `Variables`), `Pipeline/PipelineTypes.cs`
  (`UnitExports`, `UnitState.Exports`, `RenderedUnit.Exports`, `UnitPlan.ExportOnly`), `Pipeline/Stages.cs`
  (`RenderContext.Exports`), `Planning/UnitPlanner.cs` (`ExportOnly`), `Planning/DependencyHasher.cs` (`px:` keys),
  `Generation/EngineServices.cs` (`CreateHasher`), `Generation/GenerationRun.cs` (the table per run from the unfiltered
  plan; used packs' states read), `Diagnostics/RuleCatalog.cs` (MQ6047 to MQ6050), `CatalogTests.cs` (+4),
  `Rendering/README.md`, `Planning/README.md`. Tests: `ExportTests`, the `HashingAndStateTests` key cases, the
  `UnitPlannerTests` export-only case, the `ExportRunTests` cases that need no stored exports. Verify:
  `E "~Rendering.|~Planning.|~Generation.|~CatalogTests"`; `P "~GoldenTests|~CultureTests"`; `record`.
- **F32. Unit states, plans and apply.** Runs after F31. Owns: `Planning/UnitStateStore.cs` (format 5),
  `Writing/WriteRun.cs` (states carry exports), `Generation/PlanCapture.cs`, `GenerationService.cs` (`PlanUnit.Exports`,
  `GenerationPlan.Exports`, `PlanExport`, the apply seed and stale check, the rebuilt states at `:653`),
  `Generation/PlanExplainer.cs` (`KindOf` by prefix; `exports` causes), `Agent/tools.json` (the `get_plan` description),
  `Generation/README.md`, `docs/api/openapi.yaml` (`UnitExports`, `PlanUnit.exports`, `GenerationPlan.exports`,
  `PlanExport`), the generated editor files, `src/editor/src/mocks/recorded/plan.json` and `plan-unit.json` (recorded
  again). Tests: the `HashingAndStateTests` state cases, the `PlanApplyTests` and `PlanCauseTests` cases, and the
  `ExportRunTests` cases that need stored exports (`A_skipped_exporting_unit_serves_its_stored_record`,
  `Two_units_reading_each_others_exports_in_one_pack_skip_on_a_second_run`,
  `Forced_and_check_runs_evaluate_every_record`). Verify:
  `E "~Planning.|~Generation.|~Writing.|~Editor.PlanCauseTests|~AgentToolsTests"`;
  `MAQUETTISTE_RECORD=1 F "~GenerationTests|~PackEndpointTests"`, then
  `F "~GenerationTests|~PackEndpointTests|~ContractTests"`; `editor`.
- **F33. Previews, `pack exports` and `get_pack_exports`.** Runs after F32 and F24. Owns: `GenerationService.Packs.cs`
  (preview sessions build a table), `GenerationService.Exports.cs` (new: `GetPackExportsAsync`, `PackExportsResult`,
  `ElementExports`), `GenerationService.Authoring.cs` (path listings use the session's table; `ExportsFrom`,
  `TemplateExportSource`), `src/Maquettiste.Cli/Commands/PackExportsCommand.cs` (new),
  `src/Maquettiste.Cli/Commands/PackNewCommand.cs` (dispatch), `src/Maquettiste.Cli/CliApp.cs` (usage),
  `src/Maquettiste.Cli/README.md`, `src/Maquettiste.Cli/Mcp/ModelTools.Packs.cs` (`get_pack_exports`), `docs/mcp.md`,
  `README.md` (the tool count), `docs/api/openapi.yaml` (`TemplateContextResult.exportsFrom`), the generated editor
  files. Tests: the `PreviewBoundsTests`, `PackAuthoringMoreTests`, `McpPackToolTests` and `PackExportsCommandTests`
  cases. MCP tools +1 (`McpSurfaceTests` Tools array and counts). Verify: `E "~Editor."`;
  `C "~McpSurfaceTests|~McpPackToolTests|~PackExportsCommandTests"`; `editor`.
- **F34. Docs and acceptance.** Runs after F33 and F27. Owns: `packs/README.md` ("Exports"), `docs/user-guide.md`,
  `docs/guides/share-and-test-packs.md`, `docs/engineering/engine-design.md` sections 8, 9 and 11, `docs/releases.md`,
  `tests/fixtures/integration/exports/` (new), `tests/Maquettiste.Engine.Tests/Integration/PackExportTests.cs` (new).
  Tests: the four acceptance tests. Verify: `E "~Integration.PackExportTests"`.

Cross-section: 6.1's impact before apply treats a `px:` key as touched when a key the exporting units' stored export
keys hold is touched (its decision 3 lists `px:` with 3.4's `x:`, 4.2's `c:` and 5.5's `tr:`). 5.5 also reserves the
helper name `has_calculated` (`calculated` is a member, not a name); both sets join MQ6013's.

### 5.4 Tests for packs

**Why every team** (scope 8.2)

Every team that writes or changes a pack needs to know that a template edit changes only what it meant to change.
Today the only check is to generate the team's whole project and read the diff, which mixes model changes with
template changes and cannot run before the model exists. A pack test pins a small model and the exact files it must
produce, and a helper test pins what a JavaScript helper returns. Both run in seconds, in a repository, in CI or in the
hosted product, and both travel with the pack folder, so a library (5.2) or an exporting pack (5.3) brings its own proof
when it is copied into another project.

**Today**

- Byte-exact golden tests exist only for the repository's own packs: `GoldenTests` compares the output over the
  billing fixture with trees under `tests/fixtures/golden/` (`tests/Maquettiste.Packs.Tests/GoldenTests.cs:7-19`),
  and `MAQUETTISTE_UPDATE_GOLDEN=1` rewrites them (`tests/Maquettiste.Testing/Golden.cs:7-14`). Several variants are
  built in code rather than stored as models (`tests/Maquettiste.Packs.Tests/PackRepo.cs:9-21`).
- No command tests a pack. The `pack` group has `new`, `remove` and `rename` (`src/Maquettiste.Cli/CliApp.cs:166-173`,
  dispatched at `src/Maquettiste.Cli/Commands/PackNewCommand.cs:24-29`), and no MCP tool runs a test.
- A pack without a `scripts` list loads every `*.js` file of its folder, recursively, except copy sources
  (`src/Maquettiste.Engine/Planning/PackFileRoles.cs:62-65`), and the parse pass parses every file whose role is
  template, partial or other (`:143-147`). `write_pack_file` parses every file it writes, except `.js`, `.json` and
  `.md`, as a template (`Editor/PackAuthoring.cs:241-244`). A `tests/x.test.js` or an expected output file inside a
  pack would load as a helper script or be parsed as a template.
- The last-run record hashes every file under the templates folder (`Generation/LastRun.cs:41-42`, `:451-456`).
- The CLI embeds `packs/sql-ddl/**` and `packs/csharp-dapper/**` as starters
  (`src/Maquettiste.Cli/Maquettiste.Cli.csproj:62-63`), the bench embeds the same folders
  (`bench/Maquettiste.Bench/Maquettiste.Bench.csproj:38-39`), and a test checks that every file of those folders is
  embedded (`tests/Maquettiste.Cli.Tests/InitTests.cs:259-272`).
- The sandbox wraps each rule file in its own function, keeping its line numbers, and freezes the globals before
  rule files run (`src/Maquettiste.Engine/Scripting/README.md:77-84`).

**Decisions taken here**

- **Two kinds of tests, in the pack's `tests/` folder.** A case is a folder `tests/<case>/`: a small project and the
  expected output. A helper test file is `tests/<name>.test.js`, directly in `tests/`. Other files directly in
  `tests/` (a README) are ignored.
- **`tests/` is reserved.** No file under it is a script, a type map, a template, a partial or a copy source, the
  parse pass skips it and `write_pack_file` does not parse it; its role is `test`. A `scripts` entry or a unit
  template under `tests/` is MQ6001, and the template cache refuses an include there. The static hash never reads it,
  and the last-run record's templates hash leaves out each pack's `tests/` folder, since nothing a run reads lives
  there: an edit under `tests/` costs no run, and a large case tree slows none.
- **A case is a small project.** The case folder holds what `.maquettiste/` holds (`maquettiste.json`, `model/`,
  `extensions/`, locales), without `templates/`, plus `expected/`. An optional `case.json` takes the model of another
  case of the same pack (`model`) and overrides parameters of the pack under test (`parameters`), so parameter
  variants share one model.
- **A case runs as a fresh project in a temporary folder outside the project.** The runner copies the case files
  into `<temp>/.maquettiste/`, the pack under test and its uses closure (5.2) from the project's templates folder into
  `<temp>/.maquettiste/templates/` (without their `tests/` folders), and runs one apply of the pack under test:
  validation, resolution, rendering, the formatters the case configures, writing. Its index cache, unit states,
  manifests, plans and last-run record live under the temporary folder, so nothing in the project changes. The folder
  is deleted afterwards.
- **What is compared.** The output folder is the case's `packs.<pack>.output`, or `out` when it is empty, and the
  runner sets `outputs.allow` to exactly that folder and `packs.<pack>.enabled` to true. `expected/` mirrors that
  folder: the same file paths (ordinal, case-sensitive) with the same bytes. Files outside it, such as manifests,
  are not compared.
- **`--update` writes `expected/`.** It adds, replaces and deletes files under `tests/<case>/expected/` until the tree
  equals the output, through the pack write gate and the model write guard (`CheckEngineWrite(Model)`), and touches
  nothing else. A case without `expected/` is MQ6053 unless `--update` runs.
- **Helper tests run in the sandbox.** The pack's scripts load first; the globals are frozen; then each test file
  loads in its own function, as rule files do, so a test reads the pack's top-level functions but cannot redeclare
  or change them. `maquettiste.test(name, fn)` registers a test; `fn(assert, helpers)` gets the assert API and a
  frozen object of the pack's registered helpers, called directly. `maquettiste.use` reaches the uses closure (5.2).
  `maquettiste.params` holds the pack's own defaults from `pack.json`, never a project's values, and the libraries it
  reaches see their own `pack.json` defaults, so a helper test gives the same result in every project.
- **The assert API.** `assert.equal(actual, expected[, message])` (`===`, with `NaN` equal to itself),
  `assert.deepEqual(actual, expected[, message])` (plain values; arrays by position, objects by key whatever the key
  order), `assert.ok(value[, message])`, `assert.throws(fn[, message])` (returns the error's message) and
  `assert.fail(message)`. A failure reads `expected <JSON>, got <JSON>` after the optional message.
- **Results are diagnostics.** MQ6051 for a case whose output differs, MQ6052 for a failing helper test, MQ6053 for a
  malformed case or test file, beside the diagnostics of a case's own run (a model error, MQ6006) when it fails.
- **Order and determinism.** Packs in ordinal order, cases in ordinal order, one case after another (each run uses
  the configured parallelism), then helper test files in ordinal order and tests in registration order. Each test is
  one sandbox call under the project's limits; `limits.scriptTimeoutMs: 0` keeps a CI result independent of load.
  The output of a case is the same for any `--jobs`, as every run's is.
- **The repository keeps its goldens; each shipped pack gets one small case.** The golden tests stay in C# against
  the shared billing fixture (`tests/fixtures/models/billing`), so they follow every edit of it; moving them into
  cases would give each case its own copy of that model, and the copies would stop following it. Instead each shipped
  pack (`sql-ddl`, `csharp-dapper`, `process-docs`, `seed-data`) holds one small case under `packs/<pack>/tests/`,
  with a model of its own (billing names, a handful of elements), and CI runs it through `maquettiste pack test`. The
  command is then used on real packs, and a pack author has an example to copy.
- **New packs start with tests; starters carry none.** `maquettiste pack new` writes an example case (one package, one
  entity with two attributes, and the expected file, whose banner line carries the new pack's name) and a helper test
  for its `shout` helper. `new_pack` with `empty` writes the example case only, since the engine's empty pack has no
  helpers. A library (5.2) gets a helper test. The embedded `sql-ddl` and `csharp-dapper` starters leave `tests/` out,
  and `new_pack` copying a project pack leaves its `tests/` out, since the copy's banner and name differ.
- **Where packs come from.** `pack test` tests the packs of the project's templates folder. The repository's shipped
  packs are tested the same way: a CLI test copies them into a temporary project and runs `pack test`. A command-line
  way to test packs outside a project is later.

#### Schema and files

`schemas/v1/pack-case.json` (new, + copy under `samples/reference-app/.maquettiste/.schema/v1/`), for
`tests/<case>/case.json`; x-order `$schema, description, model, parameters`, no other keys:

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| `$schema` | `common.json#/$defs/schemaPath` | none | |
| `description` | string | none | Shown in the report |
| `model` | string, `^[a-z0-9][a-z0-9-]*$` | none | Another case of the pack whose files serve as this case's project |
| `parameters` | object | `{}` | Merged over `packs.<pack>.parameters` of the case's `maquettiste.json` |

The runner reads it into an internal record, `PackCaseSettings`, which `SchemaConsistencyTests.Documents` lists.

A case folder:

```
.maquettiste/templates/records/tests/
  billing-small/
    maquettiste.json   { "formatVersion": 1, "name": "billing-small", "packs": { "records": { "output": "out" } } }
    model/packages/billing.json, model/entities/invoice.json, model/entities/customer.json,
    model/enums/invoice-status.json
    expected/Billing/InvoiceRecord.cs, expected/Billing/CustomerRecord.cs
  billing-small-rows/
    case.json          { "model": "billing-small", "parameters": { "suffix": "Row" } }
    expected/Billing/InvoiceRow.cs, expected/Billing/CustomerRow.cs
  helpers.test.js
```

`helpers.test.js`:

```js
maquettiste.test("record_name adds the suffix", (assert, helpers) => {
  assert.equal(helpers.record_name({ name: "invoice" }), "InvoiceRecord");
});
maquettiste.test("record_name keeps acronyms", (assert, helpers) => {
  assert.equal(helpers.record_name({ name: "VATRate" }), "VATRateRecord");
});
```

A folder directly under `tests/` is a case. It needs a `maquettiste.json`, or a `case.json` that names a `model`.

#### The runner

`Generation/PackTestRunner.cs` (new, internal) runs one pack's tests against a templates folder:

1. Load the pack and its closure with `PackLoader.LoadClosureAsync` (5.2). A load error is the result; nothing runs.
2. For each case: check it (MQ6053: no `maquettiste.json` and no `case.json` `model`; `model` naming a missing case
   or a case that itself names a model; a `templates/` folder; an invalid `case.json`; no `expected/` without update;
   a pack without units). Build the temporary project. Apply `case.json` `parameters` and the output, allow and
   enabled settings, written canonically. Run `GenerationService.RunAsync` for the pack under test with a fresh
   `ModelStore` and `EngineOptions` whose repo root and cache folder are the temporary folder. A run that is not
   `Succeeded` fails the case with its diagnostics. Otherwise compare the output folder with `expected/`, per file:
   `added` (produced, not expected), `missing` (expected, not produced) or `differs` (with a unified diff from
   `Writing/DiffGenerator`, at most 200 lines per file). Any difference is one MQ6051 on the case folder, listing
   the paths.
3. For the helper tests: create a test pool (below) with the pack's scripts, the test files and the closure as
   libraries, each library in its own pool with its `pack.json` defaults, and run each test. A test file that fails
   to load, registers anything but tests, or registers a name another test has, is MQ6053 at its position, and none
   of its tests run. A failing test is MQ6052 at the failing statement.
4. With update: rewrite each case's `expected/` from its output through `PackAuthoring.ReplaceTreeAsync` (new,
   internal), which takes the pack write gate. A case whose run failed is not updated.

`GenerationService.TestPackAsync` runs the runner for the named packs of the project (every pack with a `tests/`
folder when none is named) and returns one report. It takes no run lock, since it writes nothing in the project
except `expected/` under update; one test run at a time per service, so a hosted server is not flooded.

#### The sandbox's test pool

- `ScriptSandboxFactory.CreateTestPool(scripts, tests, libraries, limits, ct)` (internal) prepares the pack's scripts
  as usual and each test file as a wrapped script (`PreparedScript.PrepareAll`, the rule-file form). The setup
  freezes the globals after the pack's scripts and before the test files, and offers `maquettiste.test` only while
  test files load.
- `ScriptSandbox.Tests` (internal) lists the registered tests (file, name, order); `ScriptSandbox.RunTest(index, ctx)`
  calls one with the assert object and the helpers object and returns pass or the failure message with its
  position, mapped as script errors are. A limit fails the test with MQ6052 naming the limit.
- The assert object is built once per engine by a small script prelude in `Scripting/ScriptTests.cs` and frozen with
  the globals; failures throw an error the runner reads as an assertion failure.
- No public type changes: `ScriptRegistrationKind` gains no test kind, and normal pools never see test files.

#### Public API

```csharp
namespace Maquettiste.Engine;
public sealed partial class GenerationService
{   /// <summary>Runs the tests of the named packs (every pack with a tests folder when null or empty).</summary>
    public Task<PackTestReport> TestPackAsync(IReadOnlyList<string>? packs, PackTestOptions options,
        IProgress<ProgressUpdate>? progress, CancellationToken ct);
}
public sealed record PackTestOptions
{   /// <summary>Case names to run; empty runs every case. Helper tests run when this is empty.</summary>
    public IReadOnlyList<string> Cases { get; init; } = [];
    /// <summary>Rewrite each run case's expected tree from its output.</summary>
    public bool Update { get; init; }
    /// <summary>Parallelism of each case's run; null for the engine default.</summary>
    public int? Jobs { get; init; }
}
public sealed record PackTestReport(IReadOnlyList<PackTestResult> Packs, int Passed, int Failed, int Updated);
public sealed record PackTestResult(string Pack, IReadOnlyList<PackCaseResult> Cases,
    IReadOnlyList<PackHelperTestResult> Tests, IReadOnlyList<Diagnostic> Diagnostics);
public sealed record PackCaseResult(string Name, string? Description, PackTestOutcome Outcome,
    IReadOnlyList<PackCaseFile> Files, IReadOnlyList<Diagnostic> Diagnostics);
/// <param name="Diff">A unified diff for <c>differs</c> on text files; null otherwise.</param>
public sealed record PackCaseFile(string Path, PackCaseFileChange Change, string? Diff);
public sealed record PackHelperTestResult(string File, string Name, PackTestOutcome Outcome, string? Message,
    int? Line, int? Column);
[JsonConverter(typeof(JsonStringEnumConverter<PackTestOutcome>))]
public enum PackTestOutcome { [JsonStringEnumMemberName("passed")] Passed, [JsonStringEnumMemberName("failed")] Failed,
    [JsonStringEnumMemberName("updated")] Updated, [JsonStringEnumMemberName("error")] Error }
[JsonConverter(typeof(JsonStringEnumConverter<PackCaseFileChange>))]
public enum PackCaseFileChange { [JsonStringEnumMemberName("added")] Added,
    [JsonStringEnumMemberName("missing")] Missing, [JsonStringEnumMemberName("differs")] Differs }
```

`error` is a case whose run did not succeed or a malformed case or test file. `PackFileInfo.Role` gains `test` (in
`docs/api/openapi.yaml`, the role enum after `copy-source`).

#### CLI and MCP

- `maquettiste pack test [<pack>...] [--case <name>]... [--update] [--format text|json]` (new); the global `--jobs`
  sets each case run's parallelism. Text: one line per case and per helper test (`PASS`, `FAIL`, `UPDATED`, `ERROR`),
  the differing paths with their diffs under a failing case, `file:line:column` and the message under a failing
  test, then `N passed, M failed`. Exit 0 when everything passed or was updated, 1 when any case or test failed or
  errored, 4 on a usage error or an unknown pack. `--case` joins `CommandLine.ValueOptions` and `--update` joins
  `FlagOptions`.
- `test_pack` (new): `pack` (optional; all packs with tests when absent), `cases` (optional), `update` (boolean,
  default false; the tool is marked destructive because of it), `maxDiffLines` (default 40 per file, at most 200).
  Answers the report.
- `list_pack_files`, `get_pack`: files under `tests/` have the role `test`. `write_pack_file` writes case and test
  files as any other pack file, without parsing them.

#### Rules

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ6051 | E | A pack test case's output differs from its expected tree: a file added, missing or different. | Fix the pack, or run with `--update` |
| MQ6052 | E | A pack helper test fails: an assertion fails, the test throws or it hits a sandbox limit. | Fix the helper or the test |
| MQ6053 | E | A pack test is malformed: a case without a project or `expected/`, a case holding `templates/`, an invalid `case.json` or one naming a missing case, cases in a pack without units, a test file that does not load or registers anything but tests, or two tests with one name. | Fix the test folder |

#### Editor follow-ups

A Tests tab in the pack editor: the cases and helper tests with their last result, a Run button, an Update button
per case and the diff of a failing file. It needs an HTTP endpoint (`POST /api/packs/{pack}/test`), which comes with
the tab. Until then, agents and the command line run tests, and case files are edited as pack files.

#### Docs

`packs/README.md` gains "Testing a pack" (cases, `case.json`, `expected/`, `--update`, helper tests and the assert API),
and its paragraph on where the example packs' tests live changes. Each shipped pack's README names its case and how to
run it. `docs/user-guide.md` (the command line, the MCP tool, the rules), `docs/guides/share-and-test-packs.md` (testing
the records and naming packs), `docs/mcp.md`, `src/Maquettiste.Cli/README.md`, `docs/releases.md`. The guide advises
keeping `expected/` away from any tool that converts line endings, since the comparison is byte for byte.

**Not doing**

- Reporting template branches no case reaches (the scope's "later").
- Cases for a pack without units. A library's partials are tested through the cases of a pack that uses them, and its
  helpers through helper tests.
- Cases that start from existing output files, run generate twice or edit the model between runs. Those stay tests
  in code, as the repository's multi-step tests do.
- Comparing files outside the pack's output folder, such as manifests or unit states.
- A model or the resolved model inside helper tests. A test passes the plain values a helper reads.
- Testing packs that are not in a project from the command line.
- Moving the repository's goldens into packs: they stay C# tests against the shared billing fixture, and each shipped
  pack gets one small case of its own instead.

**Tests**

- `Generation/PackTestRunnerTests` (new): `A_case_whose_output_matches_passes`,
  `A_changed_template_fails_with_the_differing_path_and_a_diff`, `A_file_produced_but_not_expected_is_added`,
  `A_file_expected_but_not_produced_is_missing`, `Update_rewrites_expected_and_deletes_stale_files`,
  `A_case_json_reuses_another_cases_model_with_parameters`, `A_case_without_a_project_is_MQ6053`,
  `A_case_without_expected_is_MQ6053_unless_updating`, `A_case_holding_templates_is_MQ6053`,
  `A_case_whose_model_is_invalid_errors_with_the_runs_diagnostics`, `A_case_runs_the_used_packs_of_the_project`,
  `A_case_leaves_the_project_untouched` (manifests, unit states, plans, last-run record and outputs compared before
  and after), `A_pack_without_units_holding_cases_is_MQ6053`, `Results_are_identical_with_one_and_eight_jobs`.
- `Planning/PackLoaderTests`: `Scripts_under_tests_are_not_loaded`, `A_scripts_entry_under_tests_is_MQ6001`,
  `A_unit_template_under_tests_is_MQ6001`, `A_copy_unit_never_copies_tests`.
  `Generation/RunRecordTests.An_edit_under_a_packs_tests_folder_keeps_the_last_run`.
  `Editor/PackAuthoringTests`: `Files_under_tests_have_the_test_role_and_are_not_parsed`,
  `Writing_a_file_under_tests_does_not_parse_it`.
  `Rendering/TemplateCacheAndHelperTests.An_include_under_tests_is_refused`.
- `Scripting/ScriptTestApiTests` (new): `Equal_passes_and_fails_with_expected_and_got`,
  `Deep_equal_ignores_key_order`, `Throws_returns_the_message`, `A_test_reads_the_packs_top_level_functions`,
  `A_test_cannot_change_the_packs_globals`, `Helpers_holds_the_registered_helpers`,
  `Params_hold_the_pack_defaults`, `A_test_file_registering_a_helper_is_MQ6053`, `Two_tests_with_one_name_are_MQ6053`,
  `A_limit_fails_the_test_with_MQ6052`, `A_failure_points_at_the_failing_statement`, `Use_reaches_the_closure`.
- `Cli`: `PackTestCommandTests` (new): `Pack_test_exits_0_when_every_case_and_test_passes`,
  `Pack_test_exits_1_on_a_failing_case_and_prints_the_diff`, `Update_rewrites_and_exits_0`,
  `Case_selects_one_case`, `An_unknown_pack_exits_4`.
  `McpPackToolTests.Test_pack_reports_a_failing_case_and_updates_it`.
  `PackNewTests` (in `OtherCommandTests.cs`): `A_new_pack_passes_its_own_tests`,
  `A_new_library_passes_its_helper_test`. `InitTests`: `Starter_packs_carry_no_tests`, and
  `Every_file_of_an_example_pack_folder_is_embedded` leaves `tests/` out.
  `Editor/PackAuthoringTests`: `An_empty_pack_passes_its_own_case`, `New_pack_from_a_project_pack_leaves_tests_out`.
- `Cli`: `ShippedPackTests` (new): `Pack_test_passes_for_every_shipped_pack` copies `packs/sql-ddl`,
  `packs/csharp-dapper`, `packs/process-docs` and `packs/seed-data`, `tests/` included, into a temporary project's
  templates folder and runs `maquettiste pack test` in-process: exit 0, one case passed per pack and every helper test
  passed. With `MAQUETTISTE_UPDATE_GOLDEN=1` it runs `pack test --update` and copies each rewritten `expected/` back
  under `packs/<pack>/tests/`. `Every_shipped_pack_has_a_case`. The `build-and-test` job of `.github/workflows/ci.yml`
  runs the CLI tests on ubuntu and macos, so CI runs the command on real packs with no new step.
- **Acceptance:** `Integration/PackTestTests.A_case_passes_fails_with_a_diff_and_updates` (new). Setup: a billing
  copy whose `records` pack (one `each entity` unit writing `<Package>/<Name>Record.cs` through a `record_name`
  helper) holds the `billing-small` case and `helpers.test.js` shown above. Expected: one case passed and two tests
  passed. A line added to the unit's template makes the case fail with MQ6051 and `differs` for
  `Billing/InvoiceRecord.cs` and `Billing/CustomerRecord.cs`, each diff showing the added line. Update rewrites both
  files, a rerun passes, and the project's own `records` output, manifest and unit states are byte-identical to
  before. Changing `record_name` to drop the suffix fails both helper tests with MQ6052 at
  `tests/helpers.test.js` lines 2 and 5.
- **Acceptance:** `Integration/PackTestTests.A_parameter_variant_shares_its_model`. Setup: the `billing-small-rows`
  case above. Expected: it passes with `InvoiceRow.cs` and `CustomerRow.cs`, and its folder holds no model files.
- **Acceptance:** `Integration/PackTestTests.A_case_of_a_pack_that_uses_a_library_runs_with_the_library`. Setup:
  `records` uses `naming` (5.2) and names its files with `naming.class_name`; the case's expected tree uses the
  library's default suffix. Expected: the case passes; changing the library's helper makes it fail with MQ6051.
- **Acceptance (repository):** `C "~ShippedPackTests"` passes: `maquettiste pack test` over the four shipped packs
  exits 0 with one case passed per pack. `P "~GoldenTests"` passes unchanged, since the goldens stay where they are.

**Tasks**

- **F40. The tests folder and the case runner.** Runs after F33, which comes after every 5.2 and 5.3 task that edits a
  file below (`Diagnostics/RuleCatalog.cs`, `docs/api/openapi.yaml`, `Planning/PackFileRoles.cs`,
  `Rendering/TemplateCache.cs`, `Editor/PackAuthoring.cs`, the Planning and Generation READMEs). Owns:
  `Planning/PackFileRoles.cs` (role `test`; scripts, type maps and the parse pass skip `tests/`; `Parse` answers nothing
  for a path under it), `Planning/CopySources.cs` (copy sources skip `tests/`), `Planning/PackLoader.cs` (MQ6001 for
  `scripts` and unit templates under `tests/`), `Rendering/TemplateCache.cs` (refuse `tests/` paths),
  `Generation/LastRun.cs` (the templates hash leaves out each pack's `tests/`), `Editor/PackAuthoring.cs` (the
  `PackFileInfo.Role` doc; `ReplaceTreeAsync`), `schemas/v1/pack-case.json` (new) with its copy,
  `Generation/PackTestRunner.cs` (new; cases and update, `PackCaseSettings`), `Generation/PackTestReport.cs` (new: the
  public report types), `Diagnostics/RuleCatalog.cs` (MQ6051, MQ6053), `CatalogTests.cs` (+2),
  `tests/Maquettiste.Engine.Tests/Json/SchemaConsistencyTests.cs` (the `pack-case.json` row), `docs/api/openapi.yaml`
  (role `test`), the generated editor files, `Generation/README.md`, `Planning/README.md`. Tests: `PackTestRunnerTests`
  (the case tests), the `PackLoaderTests`, `RunRecordTests`, `PackAuthoringTests` (role and write) and
  `TemplateCacheAndHelperTests` cases. Verify:
  `E "~Generation.PackTestRunnerTests|~Generation.RunRecordTests|~Planning.|~CatalogTests"`;
  `E "~Editor.PackAuthoringTests|~Rendering.TemplateCache|~SchemaConsistencyTests"`; `copies`; `record`; `editor`.
- **F41. Helper tests in the sandbox.** Runs after F40 and F22. Owns: `Scripting/ScriptSandboxFactory.cs`
  (`CreateTestPool`), `Scripting/ScriptSandbox.cs` (`maquettiste.test`, `Tests`, `RunTest`), `Scripting/ScriptTests.cs`
  (new: the assert prelude), `Generation/PackTestRunner.cs` (helper tests), `Diagnostics/RuleCatalog.cs` (MQ6052),
  `CatalogTests.cs` (+1), `Scripting/README.md`. Tests: `ScriptTestApiTests`, the helper-test cases of
  `PackTestRunnerTests`. Verify: `E "~Scripting.|~Generation.PackTestRunnerTests|~CatalogTests"`; `record`.
- **F42. `pack test` and `test_pack`.** Runs after F41 (and so after F33, the last task before it on the CLI files and
  `docs/mcp.md`). Owns: `GenerationService.PackTests.cs` (new: `TestPackAsync`, `PackTestOptions`),
  `src/Maquettiste.Cli/Commands/PackTestCommand.cs` (new), `src/Maquettiste.Cli/Commands/PackNewCommand.cs`
  (dispatch), `src/Maquettiste.Cli/CommandLine.cs` (`--case`, `--update`), `src/Maquettiste.Cli/CliApp.cs` (usage),
  `src/Maquettiste.Cli/README.md`, `src/Maquettiste.Cli/Mcp/ModelTools.Packs.cs` (`test_pack`), `docs/mcp.md`,
  `README.md` (the tool count). Tests:
  `PackTestCommandTests`, the `McpPackToolTests` case. MCP tools +1 (`McpSurfaceTests` Tools array and counts).
  Verify: `C "~PackTestCommandTests|~McpSurfaceTests|~McpPackToolTests"`; `E "~Generation.PackTestRunnerTests"`.
- **F43. New packs start with tests; starters carry none.** Runs after F42 and F26. Owns:
  `src/Maquettiste.Cli/StarterPacks.cs` (an example case and helper test for `empty`, a helper test for `--library`),
  `src/Maquettiste.Cli/Commands/PackNewCommand.cs`, `src/Maquettiste.Cli/Maquettiste.Cli.csproj` and
  `bench/Maquettiste.Bench/Maquettiste.Bench.csproj` (`Exclude` of `tests/**` on the embedded packs),
  `tests/Maquettiste.Cli.Tests/InitTests.cs` (the embedded-files check leaves `tests/` out), `Editor/PackAuthoring.cs`
  (`CreateAsync`: the example case for `empty`, a helper test for `library`; a pack copied by `new_pack` leaves
  `tests/` out). Tests: the `PackNewTests`, `InitTests` and `PackAuthoringTests` cases. Verify:
  `C "~PackNewTests|~InitTests|~McpPackToolTests"`; `E "~Editor.PackAuthoringTests"`.
- **F44. A small case for each shipped pack.** Runs after F43 (`pack test`, and starters and an embedded-files check
  that leave `tests/` out) and 5.1's F13 (`packs/sql-ddl/README.md`, and the pruned templates that the sql-ddl case's
  expected tree reflects). Owns: `packs/sql-ddl/tests/`, `packs/csharp-dapper/tests/`, `packs/process-docs/tests/`
  and `packs/seed-data/tests/` (one case each; each folder with a `.gitattributes` holding `* -text`, as
  `tests/fixtures/golden/.gitattributes` does), `tests/Maquettiste.Cli.Tests/ShippedPackTests.cs` (new), the four
  packs' READMEs (`packs/<pack>/README.md`: the case and how to run it) and
  `src/editor/src/mocks/recorded/pack-sql-ddl.json` (recorded again: the file list holds `tests/`). Each case is
  `billing-small`: a `maquettiste.json` that sends the pack's output to `out`, and a model of its own with billing
  names and a handful of elements. For `sql-ddl`, a PostgreSQL database `main` with the tables `customers` and
  `invoices`, a foreign key between them and a table seed of two customers; for `csharp-dapper`, a package `billing`
  with the entities `Customer` and `Invoice` and the enum `InvoiceStatus`; for `process-docs`, an `InvoiceLifecycle`
  process with three states, an actor `Clerk` and one scenario; for `seed-data`, a database `main` with a `customers`
  table and a seed of two rows. The task writes each expected tree with `--update` and reviews it. The goldens under
  `tests/fixtures/golden/` and the C# tests that read them stay as they are. Tests: `ShippedPackTests`. Verify:
  `C "~ShippedPackTests|~InitTests"`; `P "~GoldenTests|~PackLayoutTests"` (unchanged);
  `diff -r -x tests packs/sql-ddl samples/reference-app/.maquettiste/templates/sql-ddl` and the same for
  `csharp-dapper` print nothing (the sample's copies carry no tests); `MAQUETTISTE_RECORD=1 F "~PackEndpointTests"`,
  then `F "~PackEndpointTests"`.
- **F45. Helper tests for the shipped packs.** Runs after F44 and F41. Owns: `packs/sql-ddl/tests/helpers.test.js`,
  `packs/csharp-dapper/tests/helpers.test.js`, `packs/process-docs/tests/helpers.test.js` (each registered helper
  with at least one test). Tests: the helper tests that `ShippedPackTests.Pack_test_passes_for_every_shipped_pack`
  runs. Verify: `C "~ShippedPackTests"`.
- **F46. Docs and acceptance.** Runs after F43 and F34 (which last edits the shared docs and the guide). Owns:
  `packs/README.md` ("Testing a pack"; where the example packs' tests live), `docs/user-guide.md`,
  `docs/guides/share-and-test-packs.md`, `docs/releases.md`,
  `tests/Maquettiste.Engine.Tests/Integration/PackTestTests.cs` (new) with its fixture under
  `tests/fixtures/integration/pack-tests/`. Tests: the three integration acceptance tests. Verify:
  `E "~Integration.PackTestTests"`.

Cross-section: the goldens stay C# tests against `tests/fixtures/models/billing`, so a later task updates them as
today. From F44 on, a task that changes what `sql-ddl`, `csharp-dapper`, `process-docs` or `seed-data` writes for its
small case also updates that case's expected tree with `MAQUETTISTE_UPDATE_GOLDEN=1 C "~ShippedPackTests"` and names
the intended change; 6.3's G22 runs `C "~ShippedPackTests"` and changes nothing there, since no case names an
environment. A task that compares a pack with the sample's copies uses `diff -r -x tests`.

### 5.5 Values calculated by project scripts

**Why every team**

Every team derives values from its model that several consumers need: the name an entity takes in URLs and payloads,
the code of a permission, the topic an event goes to, whether an entity is the root of its aggregate. Today each pack
computes such a value in a helper of its own, and a rule that checks it repeats the logic in its own JavaScript, so the
copies drift apart. A calculated value is a named value that the project defines once, in a project script, for the
objects of a kind. The engine computes it once per object after resolution, and every template, pack script, rule, agent
and editor view reads that one value. The engine reserves no names for calculated values; each project names its own.

**Today**

- Derived values live in each consumer. A pack's scripts register helpers, selectors, filters and transforms for that
  pack only (`Scripting/ScriptingContracts.cs:62-79`), and a transform's result reaches one unit's `data`
  (`Scripting/ScriptingContracts.cs:116-122`). An attribute's `derived` is an expression that templates interpret
  (`schemas/v1/common.json:335`).
- Rule scripts see the element's canonical JSON and the snapshot, never the resolved model
  (`Scripting/ScriptingContracts.cs:124-130`), so a rule cannot read a name a pack computes from resolved names.
- The resolver runs no scripts (`Resolution/ModelResolver.cs:10`). An annotated resolved object carries `Properties`,
  stereotype defaults merged under its own (`Resolution/ResolvedCore.cs:129-130`), and nothing the project computes.
- The loader knows `maquettiste.json`, model files, `extensions/*.json` and `extensions/rules/*.js`
  (`Loading/ModelPaths.cs:116-132`).
- Script rule results are cached per element by the keys a call read (`Validation/RuleResultCache.cs:9-18`). Units
  record `e:`, `k:`, `r:`, `s:`, `l:`, `t:`, `d:` and `n:` keys (`Planning/DependencyHasher.cs:202-213`).
- The store resolves once per snapshot and shares that model with previews, resolved pages and resolver findings
  (`ModelStore.ResolverFindings.cs:13-48`; a preview takes it at `GenerationService.Packs.cs:334`).
- A virtual or inherited attribute is one resolved object per entity that holds it, and it keeps the declaring id
  (`Resolution/ResolveRun.Conceptual.cs:781-783`, `:891-899`). An id alone therefore does not name one resolved object.

**Decisions taken here**

- **The name is `calculated`.** The owner chose it (2026-11-01): it is plain and names what the value is. A database
  column's `computed` flag is a different thing, a column the database fills; the user guide says so in one sentence.
- **Calculation files.** `extensions/calculated/<name>.js`, the files directly in that folder, beside
  `extensions/rules/`. Calculated values belong to the project, so packs declare none; a pack keeps its own helpers.
- **A function of the resolved object.** A calculated value reads what generation reads: names after conventions, plural
  names, flattened attributes, resolved types and, with 3.1, inherited members. Its `value(element, model)` gets the
  read-only resolved views that pack scripts already get.
- **Kinds are resolved kinds.** A calculated value names the kinds it applies to from `ResolvedKinds.All`: the `Kind` of
  every concrete `RAnnotated` type, found by reflection. Today that is 27 kinds (`package`, `entity`, `attribute`,
  `value-object`, `enum`, `scalar-type`, `relation`, `reference-type`, `seed`, `stereotype`, `tag-vocabulary`,
  `category-tree`, `process`, `actor`, `scenario`, `binding`, `database`, `schema`, `table`, `column`, `view`,
  `sequence`, `routine`, `database-type`, `sql-object`, `query`, `migration`); milestone E adds `operation`,
  `operation-member`, `outcome` and `error` (4.1), `custom` (4.2) and `requirement` (4.3), whose types derive from
  `RElement`. Kinds without resolved objects (`diagram`, `mapping`) are refused.
- **Values are JSON.** Null, booleans, numbers, strings, and arrays and objects of them; integral numbers within
  ±2^53−1 become `long`, others `double`, as script results do today. A calculated value never holds a model object: it
  holds an id instead, so every link stays an id. Each value must fit the declared `schema`.
- **Lazy, once per object.** Resolution gives each annotated object an `RCalculated` view and evaluates nothing. The
  first read of a calculated value on an object evaluates it; later reads in the same resolved model reuse the value.
- **Whole-model runs evaluate every calculated value.** `validate`, `plan` and `generate` evaluate each calculated value
  on each object of its kinds, so a failing one is reported whatever reads it, and the same model gives the same
  diagnostics whatever units are skipped. An error stops generation, as any resolver error does.
- **Previews evaluate what they read.** A unit preview, `get_calculated`, a page of `get_resolved_model` and the
  database, query and binding previews evaluate only the calculated values they read, of the objects they read. The
  store shares one resolved model per snapshot, so a preview after a validate reuses its values.
- **Cached by the keys a value read.** A long-lived host (the MCP server, the editor host, watch mode) keeps each value
  in memory with the calculation file's hash, the limits and the current hash of every key the call read, and evaluates
  again only when one changed. Nothing is written to disk. The keys are the ones incremental generation trusts: every
  read of a resolved object records its `Dependencies` and every list read its membership keys.
- **Sites.** The cache keys a value by name, kind and site. The site is the object's id, except for an attribute
  whose holder (`RAttribute.Owner`) is not the element that owns the attribute's id in the index (an inherited
  attribute, a stereotype's virtual attribute, an attribute copied onto a relation's entity); its site is
  `<holder id>/<attribute id>`. Invoice and Customer both carry `audited`, and each gets its own value for the
  `createdAt` attribute it adds.
- **Calculated values may read calculated values.** A value may read `calculated.<other>` of any object; the outer value
  records the inner value's keys and its file. Calculated values that read each other in a cycle are MQ5013, reported
  once per cycle: the chain starts at its least (kind, site, name) in ordinal order, so the text does not depend on
  which thread found it.
- **A read of a failed calculated value fails the reader.** A calculated value, a rule, a pack script or a template that
  read a failed value fails with that value's diagnostic, even when a script caught the error: the recorder notes the
  failed read and the call's result is dropped. So no value depends on how another one failed, and nothing that read a
  failure is cached.
- **An undeclared name is an error, not null.** Reading a name the object's kind does not declare is MQ5012, so a typo
  fails instead of rendering an empty string. Templates test for a calculated value with `has_calculated`, scripts with
  `in`.
- **Script rules see calculated values through `scope: "resolved"`.** A rule registered with `scope: "resolved"` and
  `kinds` runs once per resolved object of those kinds after resolution, with the same views and `calculated`. Element
  rules keep reading canonical documents and see no calculated values; whole-model rules keep the snapshot. Rule files
  keep the project's script libraries (5.2) whatever scope their rules take.
- **Only calculation files declare calculated values, and they declare nothing else.** `maquettiste.calculated` in a
  rule file or a pack script, and `maquettiste.helper`, `selector`, `filter`, `transform` or `rule` in a calculation
  file, are a `TypeError` while the file loads (MQ5002 in project files, MQ6016 in packs).
- **Calculated values see no pack.** No pack parameter and no pack export (5.3) reaches a calculated value or a resolved
  rule: they run before planning and belong to the project, so no pack can change a calculated value.
- **One stage after resolution.** `ResolvedChecks` (internal) evaluates every calculated value, runs the resolved rules
  and, with 5.6, the declared rules and suppressions. It runs where resolver findings and generation's resolver errors
  are made, only on a model whose validation has no error other than MQ4101, as resolution does.
- **Calculation files are project scripts.** They load like rule files: a function scope per file, globals frozen before
  the files run, and MQ5005 for a top-level `let` or `var`. Their load failures reuse MQ5002 and MQ5003, whose texts
  grow, and MQ5005's text (as 3.4 changes it for rows scripts) names calculation files too.
- **Determinism.** The sandbox clock is fixed, `Math.random` is seeded with `calculated:<name>:<site>`, and no
  parameters reach a calculated value. A nested read does not count against its reader's memory limit: Jint measures
  memory by what the thread allocates, and a nested value is evaluated on the reader's thread, so after every read of a
  calculated value inside a sandbox call the reader's `MemoryLimitConstraint` takes its baseline again
  (`Engine.Constraints.Find<MemoryLimitConstraint>()`, then `Reset()`), whether the value was evaluated or reused. A
  cancelled evaluation publishes nothing, so a cancelled preview never leaves a failure in the shared resolved model.
  With `scriptTimeoutMs: 0` the values and findings do not depend on the machine's load or on the order objects are
  evaluated in.
- **Nothing else changes.** Calculated values write no model file, record no migration fact, never read a database or
  the ledger and need no version control.

#### Calculation files

```js
// extensions/calculated/names.js
function kebab(text) {
  return text.replace(/([a-z0-9])([A-Z])/g, "$1-$2").toLowerCase();
}

maquettiste.calculated({
  name: "publicName",
  kinds: ["entity"],
  schema: { type: "string", pattern: "^[a-z][a-z0-9-]*$" },
  description: "The name clients see in URLs and payloads.",
  value(entity) {
    return kebab(entity.pluralName);
  },
});
```

In the billing fixture `Invoice` gets `invoices`, `InvoiceLine` gets `invoice-lines` and `Customer` gets `customers`.

| Part | Meaning |
| --- | --- |
| `name` | `^[a-z][A-Za-z0-9]*$`, at most 64 characters. Unique per kind across the calculation files; one name may serve several kinds through one registration, or different kinds through separate ones. |
| `kinds` | Required: one or more names from `ResolvedKinds.All`. |
| `schema` | Required: a JSON Schema (draft 2020-12, as extension files use) that every value must fit. `null` is a value only when the schema allows it. |
| `description` | Optional text: what the value means. `list_extension_files` and `get_calculated` show it. |
| `value(element, model)` | Called once per object of those kinds. `element` and `model` are the read-only resolved views of pack scripts (`Scripting/README.md`, "Model view"). Returns the value; `undefined` is `null`. |

Any other key, a missing or malformed part, an unknown kind, a schema that does not compile or a (name, kind) pair
another file already declares is a `TypeError` while the file loads, so MQ5002 on that file with its line and column;
of two files that declare one pair, the later by path gets the error. Each call runs under the limits of `limits` in
`maquettiste.json`, with its own statement budget. A calculation file reaches no script library (Not doing).

#### Evaluation and the resolved model

- `ModelResolver` takes the long-lived `CalculationService` (`Resolution/CalculationService.cs`, internal, built by
  `EngineServices`). When the snapshot has calculation files, `ResolveAsync` gives every `RAnnotated` it builds an
  `RCalculated` bound to the run's `CalculationHost` (`Resolution/CalculationHost.cs`, internal); otherwise
  `RCalculated.Empty`. Partial runs (`ResolveDatabasesAsync`, which the migration classifier uses) carry no calculated
  values.
- The host builds one sandbox pool of the calculation files on first use and keeps it as long as its resolved model. The
  pool is created without a run token (`CancellationToken.None`); each call observes its own token through
  `ScriptCallContext`. A pool holds managed engines only, so nothing leaks when the model is collected. The host also
  answers the declarations of a kind from the pool's registrations; the internal `ResolvedModel.Calculated` reaches it.
- A read looks in the host's memo, keyed by object reference and name. On a miss it asks the cache (below); on a cache
  miss it rents an engine and calls `IScriptSandbox.RunCalculation` with a recorder that collects the keys the call
  reads. The value, converted to JSON, is checked against the compiled `schema` (MQ5011). The memo publishes the first
  finished result: two threads may evaluate one calculated value at once and get equal results, no thread waits on
  another, and a cancelled call publishes nothing.
- A value that reads another calculated value evaluates it first, on another engine of the pool (`Rent` never blocks),
  and records that value's keys and its calculation file's `x:` key. A stack of (site, name) pairs per thread finds a
  cycle; the MQ5013 diagnostic names the canonical chain (decisions), and every read inside the cycle fails with it.
  Since no thread waits on another, a cycle across threads cannot deadlock.
- `CalculationService` keeps a `ResolvedResultCache` (`Validation/ResolvedResultCache.cs`, internal, shared with
  resolved rules), keyed by (name, kind, site). An entry holds the value, the calculation file's path and hash, the
  limits, and the sorted keys the call read with their hashes. It is reused when every one of them is unchanged. A
  `DependencyHasher` over the snapshot and the resolved model, with no packs and no schema diffs, hashes the keys, since
  calculation calls never record `t:` or `d:`. Failures are not cached, nor is any value of a file with MQ5005, nor a
  value whose call read a failed calculated value. A whole-model run sweeps the entries it did not use, which bounds
  memory; runs take tickets as `RuleResultCache` runs do, so a sweep keeps what a concurrent run used.
- The cache is sound for the reasons section 5.7 of phase 1 gives for rules: a function scope per file and the early
  freeze make a calculation file's hash, with the shared sandbox setup, its behaviour; the recorded keys cover every
  resolved object and list the call read; no parameters and no library reach the call; the clock is fixed and the random
  seed is the site's.
- `ResolvedKinds` (`Resolution/ResolvedKinds.cs`, internal) holds `All`, `TypeOf(kind)`, `SiteOf(object)` and
  `ObjectsOf(resolved, kinds)`: every `RAnnotated` reachable from the model's lists through the members scripts see,
  once per object by reference, in (kind, site) ordinal order. `SiteOf` reads the index owner of an attribute's id
  through `ResolvedModel.Source`. `ObjectsOf` is built once per resolved model, and only whole-model runs call it.

#### Script rules on the resolved model

```js
// extensions/rules/public-names.js
maquettiste.rule({
  id: "public-names-unique",
  scope: "resolved",
  kinds: ["entity"],
  check(entity, model, report) {
    const name = entity.calculated.publicName;
    const other = model.entities.find((e) => e.id !== entity.id && e.calculated.publicName === name);
    if (other) report(`${entity.name} and ${other.name} share the public name ${name}.`);
  },
});
```

- `scope: "resolved"` needs `kinds`, from `ResolvedKinds.All`; a resolved rule without kinds, or with an element-only
  kind, is a `TypeError` (MQ5002). `check(element, model, report)` runs once per object of those kinds, an attribute
  copy once per holder, in the checks after resolution. It never runs on element saves or in previews.
- `report(message, { pointer, severity })`. The finding goes where `ResolvedLocations` puts the object (below);
  `pointer` is relative to the located entry, and is dropped when the location is a holder's.
- The checks create their rule pool as the validator does, with the project's script libraries (5.2).
- Results are cached per (rule id, kind, site) in the same `ResolvedResultCache`, with the rule file's hash (folded
  with the libraries' hash, as 5.2 folds it for element rules) in place of the calculation file's. Raw diagnostics are
  cached, before `ResolvedLocations` and `validation.rules` apply, so a renamed file never serves a stale path; `off`
  skips the rule. A rule that throws or breaks a limit is MQ5002 or MQ5003 on the rule file, with the line and the
  object's id, as element rules are.
- `includeScriptRules: false` (MCP `validate`) leaves resolved rules out, with the other script rules: the checks
  return their findings apart (`ResolvedCheckResult.ScriptFindings`), and `ModelStore.ValidateCoreAsync` drops them
  when the scope says so.
- The validator's per-document loop skips resolved rules as it skips whole-model rules
  (`Validation/ModelValidator.cs:109`); `list_extension_files` marks them `resolved`.

#### The checks after resolution

```csharp
namespace Maquettiste.Engine.Validation;
internal sealed class ResolvedChecks(EngineOptions options, IScriptSandboxFactory scripts, CalculationService calculations)
{
    /// <summary>Evaluates every calculated value and runs the resolved rules (with 5.6, also the declared rules and
    /// suppressions) on a model whose validation has no error but MQ4101. Positioned, validation.rules applied, in
    /// diagnostic order.</summary>
    internal Task<ResolvedCheckResult> RunAsync(ModelSnapshot snapshot, ResolvedModel resolved, CancellationToken ct);
}
/// <param name="Findings">Failures of calculated values and, with 5.6, declared rule findings.</param>
/// <param name="ScriptFindings">The findings of rules registered with scope "resolved", which a validation without
/// script rules leaves out.</param>
internal sealed record ResolvedCheckResult(IReadOnlyList<Diagnostic> Findings, IReadOnlyList<Diagnostic> ScriptFindings);
```

- Callers: `ModelStore.FindAsync` (`ModelStore.ResolverFindings.cs:81`), so every validate path reports the checks
  once per snapshot, scoped validations filtered to their elements as resolver findings are (`ModelStore.cs:491-498`);
  and `GenerationRun.PrepareAsync` right after the resolver's diagnostics (`Generation/GenerationRun.cs:222-224`), where
  an error stops the run. Explain's dry-run plan prepares a run, so it runs them too. No preview calls them, and element
  saves never do: they validate through the validator alone (`ModelStore.cs:1026`).
- Work runs in parallel over objects (`EngineOptions.EffectiveParallelism`); the result does not depend on the degree.
  A failed calculated value is reported once, on the value and object that failed; a value that only read it is not
  reported again (the diagnostics are distinct).
- `ResolvedLocations.Of(object, snapshot)` (`Validation/ResolvedLocations.cs`, internal) gives a finding its file and
  pointer, first rule that applies:
  1. An attribute whose site is `<holder id>/<attribute id>`: its holder's location.
  2. An id the index holds: its owner's file and the entry's pointer.
  3. A column: its table's location. A table without an index entry (a synthesized table, keyed
     `<entityId>@<databaseId>` and the like): its overlay file when it has one, else the location of its entity,
     relation or owning attribute. The resolver records the file in the internal `RTable.FileId`, set where
     `TableBuild` takes the designed table or the overlay (`Resolution/TableBuild.cs:8-14`).
  4. Any other synthesized key (`<id>.sequence@<databaseId>`, `<a>.<b>`, `<a>/<b>`): the location of the id it starts
     with.
  5. Otherwise the element id alone, without a file.

  The finding keeps the object's own id as its element id, so a suppression's `member` can name it (5.6).

#### Templates and pack scripts

- Templates read `element.calculated.publicName`. `calculated` is the member (snake case of `Calculated`); names are map
  keys and keep their case. A read records the calculation file's `x:` key (`x:calculated/<name>.js`, the key 3.4 adds
  for files under `extensions/`) and the value's keys into the unit's recorder. `for t in element.calculated` evaluates
  every declared name in ordinal order. An undeclared name fails the unit with MQ5012 at the template's line.
- The built-in helper `has_calculated <object> "<name>"` answers whether the object's kind declares the calculated
  value, evaluating nothing. MQ6013 reserves `has_calculated`.
- What a kind declares is a key of its own, `cv:<kind>` (new): the kind's (name, declaring file) pairs in ordinal
  order, hashed with the declaring files' hashes; absent when the kind declares none. `has_calculated`, enumeration, the
  `in` test, `RCalculated.Names`, a plain copy and an undeclared read record it, so a unit that tested for a calculated
  value renders again when a calculation file adds or drops one. `PlanExplainer` words it "The calculated values
  declared for <kind>".
- `TrackingTemplateContext.GetMemberAccessorImpl` (`Rendering/TrackingTemplateContext.cs:156-174`) gives `RCalculated` a
  `CalculatedAccessor` (`Rendering/TemplateValues.cs`) instead of the map view, which would read every entry
  (`Rendering/TrackingTemplateContext.cs:114-115`).
- Pack scripts and rules see `element.calculated` as a frozen object whose own keys are the declared names. Reading a
  key evaluates it and records its keys into the call's recorder; `"publicName" in element.calculated` evaluates
  nothing; reading an undeclared key is a `TypeError` that carries MQ5012. A plain helper (`{ plain: true }`) copies
  `calculated` as a frozen object of every value for the objects at hop 0 only; at hop 1 a copy has no `calculated`, so
  a plain copy never evaluates the calculated values of a whole neighbourhood. `MemberCatalog.IsProxyType`
  (`Scripting/MemberCatalog.cs:74-77`) would expose `RCalculated`'s CLR members, so `ScriptValues` gives it its own
  proxy.
- The `json` helper writes `calculated` with every value of the object.
- `DependencyHasher` reads `x:calculated/<name>.js` as 3.4 defines `x:` and gains `cv:<kind>` (above), computed through
  the resolved model's calculation host. A unit that read a calculated value is skipped until the file or a key the
  value read changes.
- A failed calculated value fails the unit that reads it with the value's diagnostic, even when a helper caught the
  error (decisions). On whole-model runs that cannot happen, since the checks stop the run first, unless
  `validation.rules` lowered the rule.

#### Public API

```csharp
namespace Maquettiste.Engine.Resolution;
public abstract class RAnnotated
{   // existing members, plus:
    /// <summary>The named values the project's calculation files compute for this object (section 5.5); empty when
    /// its kind declares none. Never null.</summary>
    public RCalculated Calculated { get; }
}
public sealed class RCalculated
{
    public static RCalculated Empty { get; }
    /// <summary>The names declared for the object's kind, ordinal.</summary>
    public IReadOnlyList<string> Names { get; }
    public bool Contains(string name);
    /// <summary>The value, evaluated on first read. Throws <see cref="CalculatedValueException"/> when the value
    /// failed or the kind does not declare the name.</summary>
    public object? this[string name] { get; }
}
public sealed class CalculatedValueException : Exception { public Diagnostic Diagnostic { get; } }

namespace Maquettiste.Engine.Scripting;
public enum ScriptRegistrationKind { /* existing */ [JsonStringEnumMemberName("calculated")] CalculatedValue }
public sealed record ScriptRegistration(ScriptRegistrationKind Kind, string Name, string DeclaredIn)
{   // existing WholeModel, plus:
    /// <summary>A rule registered with scope "resolved": run on each resolved object of its kinds.</summary>
    public bool Resolved { get; init; }
    /// <summary>What a calculated value declares; null for other registrations.</summary>
    public CalculatedValueDeclaration? CalculatedValue { get; init; }
}
/// <param name="Kinds">Resolved kind names, ordinal.</param>
/// <param name="Schema">The JSON Schema every value must fit.</param>
public sealed record CalculatedValueDeclaration(string Name, IReadOnlyList<string> Kinds, JsonElement Schema,
    string? Description);
public interface IScriptSandbox   // gains
{
    /// <summary>Evaluates a calculated value on one resolved object: null, bool, long, double, string, or a list or
    /// map of them.</summary>
    object? RunCalculation(string name, IResolvedObject element, ResolvedModel model, ScriptCallContext ctx);
    /// <summary>Runs a rule registered with scope "resolved" on one object; report(message, { pointer, severity }).
    /// The diagnostics carry the object's id and the given pointer; the caller locates them.</summary>
    IReadOnlyList<Diagnostic> RunResolvedRule(string ruleId, IResolvedObject element, ResolvedModel model,
        ScriptCallContext ctx);
}

namespace Maquettiste.Engine.Model;
public sealed class ModelSnapshot
{   // existing members, plus:
    /// <summary>The calculation files (extensions/calculated/*.js), ordinal by path.</summary>
    public IReadOnlyList<ScriptSource> CalculationScripts { get; }
    // Create gains a last optional parameter: IReadOnlyList<ScriptSource>? calculationScripts = null.
}

namespace Maquettiste.Engine;
public sealed record CalculatedValueQuery(IReadOnlyList<string> Ids, IReadOnlyList<string>? Names = null);
/// <param name="Holder">The holder's id, for an attribute held by an element other than its declaring one.</param>
public sealed record CalculatedValueItem(string Id, string Kind, string? Holder, IReadOnlyDictionary<string, object?> Calculated);
public sealed record CalculatedValueAnswer(IReadOnlyList<CalculatedValueItem> Items, IReadOnlyList<string> Missing,
    IReadOnlyList<CalculatedValueDeclaration> Declarations, IReadOnlyList<Diagnostic> Diagnostics);
public sealed partial class GenerationService
{
    public Task<CalculatedValueAnswer> GetCalculatedAsync(CalculatedValueQuery query, CancellationToken ct);
}
public sealed record ResolvedPage(/* unchanged */)
{   /// <summary>Site to (name to value) for the objects on the page and the sub-objects their records list.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Calculated { get; init; } =
        FrozenDictionary<string, IReadOnlyDictionary<string, object?>>.Empty;
}
public sealed record ExtensionFileInfo(/* unchanged; Kind also takes "calculated" */)
{
    public IReadOnlyList<CalculatedValueDeclaration> Calculations { get; init; } = [];
}
public sealed record ExtensionRuleInfo(string Id, DiagnosticSeverity Severity) { public bool Resolved { get; init; } }
```

Implementers to update: `IScriptSandbox` by `ScriptSandbox` and the fake `Lease` in
`tests/Maquettiste.Engine.Tests/Validation/ValidationSupport.cs`; the `CountingSandboxFactory` there passes the new
members through and counts `RunCalculation` and `RunResolvedRule` calls. `RCalculated` reads through an internal
`Read(name, IReadRecorder?)` that the accessors use; the public indexer records nothing.

#### CLI and MCP

- `maquettiste model calculated --element <id>... [--name <name>]... [--format text|json]` (new): the calculated values
  of the elements given, evaluated for them only. At least one `--element` (usage error, exit 4, otherwise). Text prints
  one line per object and calculated value, `entity Invoice 01J92P0V0FJ23CGSNKM7P1W5V7 publicName = "invoices"`,
  ordinal; JSON prints the `CalculatedValueAnswer`. Exit 1 when a calculated value failed. `--element` and `--name` are
  already value options.
- MCP `get_calculated` (new, a catalog tool the assistant shares): `{ ids: [1 to 100 ids], names?: [...] }` returns the
  `CalculatedValueAnswer`: one item per resolved object an id names (an attribute held by several entities gives one
  item per holder), the declarations for their kinds, the ids that name nothing, and the failures. It evaluates the
  calculated values of those objects and whatever they read, nothing else.
- `get_resolved_model`: pages carry `calculated` for their objects. A calculated value that fails is left out of the
  page's `calculated` and its diagnostic joins the page's `Diagnostics`. `get_template_context`: the scope's members
  list `calculated.<name>` for every name the scope's kind declares.
- Extension file tools: `calculated/<name>.js` is kind `calculated`, written as sent and loaded alone in the sandbox, so
  MQ5002, MQ5003 and MQ5005 come back at once; a move keeps a calculation file a calculation file.
  `list_extension_files` lists each calculation file's declarations and marks rules registered with `scope: "resolved"`.
- HTTP mirrors the contract: `GET /api/model/resolved` pages carry `calculated`, `GET /api/extensions/files` the new
  kind, `calculations` and `resolved` (`docs/api/openapi.yaml`).

#### Rules

| Id | Severity | Rule | Where |
| --- | --- | --- | --- |
| MQ5010 | E | A calculated value failed on an object: its function threw, broke a sandbox limit or returned a value that is not JSON. | The calculation file, at the failing line, with the object's id |
| MQ5011 | E | A calculated value does not fit its schema. | The calculation file, at its `maquettiste.calculated` call, with the object's id |
| MQ5012 | E | A template, script or rule reads a calculated value that is not declared for the object's kind. | The reader: the template line, or the script line |
| MQ5013 | E | Calculated values read each other in a cycle. | The calculated value that starts the canonical chain (least kind, site and name), at its call, naming the chain |
| MQ5002 | E | Changed text: "Validation rule or calculation script error." Also a calculation file that does not load and a resolved rule that throws. | |
| MQ5003 | E | Changed text: "Validation rule or calculation script exceeded a sandbox limit." Also while a calculation file loads. | |
| MQ5005 | W | Changed text, after 3.4's: "A rule, rows or calculation script declares top-level let or var variables; it keeps no state between calls, so its results are never cached." | |

The catalog grows by 4. `validation.rules` may lower or turn off MQ5010 to MQ5013, as any rule outside MQ1xxx.

#### Editor follow-ups

Engine, CLI and MCP first (decision 2). The generic views show `calculated` in resolved records, and the Extensions tab
lists calculation files with their kind once the types are regenerated. Deferred: a panel of calculated values in the
element inspector; "New calculation file…" with the contract in its comments; completion for `maquettiste.calculated`,
`element.calculated.` in rule, calculation and template editors, and `has_calculated`; a resolved-rule badge on the
Extensions tab; an HTTP `get_calculated` endpoint for the editor.

#### Docs

`docs/user-guide.md`: a new section "Calculated values (`extensions/calculated/*.js`)" after "Script rules"; the script
rules section gains `scope: "resolved"`. `packs/README.md` section 6 (templates and scripts read `calculated`;
`has_calculated`). `Scripting/README.md` (calculation files as project scripts, `RunCalculation`, `RunResolvedRule`, the
`calculated` view). `Validation/README.md` (the checks after resolution, `ResolvedLocations`). `Rendering/README.md`
(`CalculatedAccessor`, `has_calculated`). `docs/engineering/engine-design.md` §7 (calculated values on `RAnnotated`),
§10 (the sandbox), §11 (`cv:<kind>` and the calculation files' `x:` keys).
`docs/mcp.md` (`get_calculated`, the changed rows). `skills/maquettiste-modeling/SKILL.md`. `docs/releases.md` under
`## 0.12.0 (unreleased)`.

**Not doing**

- Calculated values declared by packs. A value one pack needs stays a pack helper; calculated values are the project's
  shared definitions, and 5.2's library packs share helpers, not calculated values.
- Calculated values on the raw snapshot, and calculated values for element rules. Element rules keep judging what a file
  says; a rule about derived values uses `scope: "resolved"`.
- Model objects as values. A calculated value holds ids.
- A cache on disk. As for rules (phase 1, 5.7), values live in memory in long-lived hosts.
- Calculated values in `model export --resolved`. Export writes records as today; pages and `get_calculated` carry
  values.
- Packs that declare the calculated values they need. A pack tests with `has_calculated`; a declared requirement can
  follow.
- Calculated values written in the expression core. Phase 3's 3.8 brings that core; calculated values stay JavaScript.
- Script libraries (5.2) in calculation files. A calculation file holds its own functions; `maquettiste.use` there is a
  `TypeError`. Passing libraries needs a key that follows the library files into every unit that read a calculated
  value; a later version can add it.

**Tests**

- `CalculationScriptTests` (Scripting, new): `A_calculated_value_registers_its_name_kinds_schema_and_description`,
  `A_bad_name_unknown_kind_or_invalid_schema_is_MQ5002_with_its_line`,
  `A_name_declared_twice_for_one_kind_is_MQ5002_on_the_later_file`,
  `Calculation_files_have_a_scope_each_and_top_level_let_is_MQ5005`,
  `A_calculated_value_outside_a_calculation_file_or_another_registration_inside_one_is_a_load_error`,
  `Whole_model_validate_reports_a_calculation_file_that_does_not_load`.
- `CalculationEvaluationTests` (Resolution, new): `A_calculated_value_is_evaluated_once_per_object_and_reused`,
  `A_calculated_value_may_read_another_calculated_value_and_records_its_keys`,
  `Calculated_values_that_read_each_other_are_one_MQ5013_with_the_same_chain_whatever_the_order`,
  `A_value_that_fails_its_schema_is_MQ5011`, `A_throw_or_a_limit_is_MQ5010_with_the_line_and_object`,
  `A_value_that_is_not_json_is_MQ5010`, `An_undeclared_name_is_MQ5012`,
  `A_value_that_read_a_failed_calculated_value_fails_even_when_the_script_caught_it`,
  `A_cancelled_read_publishes_nothing`, `A_nested_read_does_not_count_against_the_readers_memory_limit`,
  `Virtual_attributes_of_two_entities_have_their_own_values`,
  `Values_are_byte_identical_across_runs_and_parallelism_with_the_clock_off`,
  `Partial_database_runs_carry_no_calculated_values`.
- `CalculationCacheTests` (Resolution, new): `An_unchanged_object_reuses_its_value`,
  `A_change_to_any_read_key_evaluates_again`, `A_changed_calculation_file_evaluates_again`,
  `Values_of_a_file_with_MQ5005_are_never_cached`, `Failures_are_not_cached`,
  `A_whole_model_run_sweeps_unused_entries`.
- `CalculatedValueReadTests` (Rendering, new): `A_template_reads_a_calculated_value_and_records_its_file_and_keys`,
  `A_unit_that_reads_a_calculated_value_is_skipped_until_the_calculated_value_changes`,
  `An_undeclared_name_fails_the_unit_with_MQ5012`, `Has_calculated_answers_without_evaluating`,
  `A_unit_that_tested_for_a_calculated_value_renders_again_when_a_file_declares_it`,
  `A_helper_reads_calculated_values_through_the_proxy`, `A_plain_helper_gets_every_value_at_hop_0_only`.
  `HashingAndStateTests.Calculated_value_declaration_keys_hash_the_kinds_declarations_or_absent`.
- `JsModelProxyTests`: `Calculated_values_are_a_frozen_object_whose_keys_are_the_declared_names`,
  `The_in_operator_tests_a_calculated_value_without_evaluating_it`.
- `ResolvedRuleTests` (Validation, new): `A_resolved_rule_runs_once_per_object_of_its_kinds`,
  `Scope_resolved_needs_kinds`, `A_resolved_rule_sees_calculated_values_and_virtual_attributes`,
  `Element_saves_and_previews_do_not_run_resolved_rules`, `Results_are_cached_by_site_and_read_keys`,
  `A_resolved_rule_calls_a_project_library`, `Include_script_rules_false_leaves_them_out`.
- `ResolvedLocationsTests` (Validation, new): one case per rule of `ResolvedLocations`, including a virtual attribute
  located on its entity, the invoice register (a synthesized table) and a column of it located on its overlay file, a
  synthesized table without an overlay located on its entity, and a synthesized key sequence.
- `ResolvedChecksTests` (Validation, new):
  `Validate_reports_calculation_failures_and_resolved_findings_once_per_snapshot`,
  `Generation_stops_on_an_error_the_checks_report`, `A_model_with_validation_errors_runs_no_checks`,
  `A_scoped_validate_filters_the_checks_to_its_elements`.
- `ResolvedNullContractTests`: `Calculated` is never null.
- `AgentToolsTests.Get_calculated_evaluates_only_the_ids_asked`; `McpSurfaceTests` (tool counts);
  `McpReadTests.Get_resolved_model_pages_carry_calculated_values`;
  `McpExtensionToolTests.Calculation_files_list_their_calculated_values`;
  `Integration/ExtensionFilesTests.A_calculation_file_is_checked_alone_on_write`;
  `ModelCommandTests.Model_calculated_prints_the_values_of_the_elements_given`.
- **Acceptance (5.2, done when):**
  `Integration/CalculatedValueAcceptanceTests.The_public_name_of_an_entity_has_one_definition_for_two_packs_and_a_rule`
  (new). Setup: an `E2ERepo` billing copy (`demo: false`) gains `extensions/calculated/names.js` as above and
  `extensions/rules/public-names.js` as above. The test writes two packs into the temporary repo, with outputs under
  `src/Generated`: `api`, one unit per entity writing `api/{{ element.name }}.txt` with
  `GET /{{ element.calculated.publicName }}`; and `client`, whose script registers
  `maquettiste.helper("endpoint", (e) => "/api/" + e.calculated.publicName)` and whose unit writes
  `client/{{ element.name }}.txt` with `{{ endpoint element }}`. Expected: `generate` writes `api/Invoice.txt` as
  `GET /invoices` and `client/Invoice.txt` as `/api/invoices`; `validate` reports no error. Then a new entity
  `InvoiceArchive` with `pluralName: "Invoices"` makes `validate` report `x/public-names-unique` on both
  `invoice.json` and the new file, and `generate` stops before planning. Then, the new entity removed, the calculation
  script returns the kebab-case singular name: one plan rewrites `api/Invoice.txt` to `GET /invoice` and
  `client/Invoice.txt` to `/api/invoice`, plans the `api` and `client` units of every entity, and skips every unit of
  the `e2e` pack.
- **Acceptance (previews):**
  `Integration/CalculatedValueAcceptanceTests.A_preview_evaluates_the_calculated_values_of_the_objects_it_reads_only`.
  Setup: the same copy, a store whose sandbox factory, validator, resolver and calculation service are built over
  `CountingSandboxFactory` (the internal `ModelStore(EngineOptions, EngineServices)` constructor), no validate before.
  Expected: `preview_unit` of `api` for `Invoice` makes one `RunCalculation` call; `get_calculated` for `Customer` makes
  one more; a following `validate` makes one call per remaining entity (six more in the billing fixture's eight
  entities).

**Tasks**

Tasks of 5.1 to 5.4 also own `Scripting/ScriptSandbox.cs`, `Scripting/ScriptSandbox.Calls.cs`,
`Scripting/ScriptingContracts.cs`, `Scripting/ScriptSandboxFactory.cs`, `Scripting/ScriptValues.cs`,
`Validation/ModelValidator.cs`, `Diagnostics/RuleCatalog.cs`, the `CatalogTests` pin, `Rendering/RenderRun.cs`,
`Rendering/BuiltinHelpers.cs`, `Rendering/TemplateValues.cs`, `Rendering/TrackingTemplateContext.cs`,
`Planning/DependencyHasher.cs`, `Generation/GenerationRun.cs`, `Generation/EngineServices.cs`,
`Generation/PlanExplainer.cs`, `Model/ModelSnapshot.cs`, `Loading/ModelLoader.cs`, `Loading/ModelPaths.cs`, the docs
and the generated editor files. F50 therefore runs after 5.2's F25 and 5.4's F41, F54 after 5.4's F42 and F55 after
5.4's F46; the earlier tasks of 5.1 to 5.4 precede those through their own order.

- **F50. Calculation files load and register.** Runs after 5.2's F25 (the model loader, paths and snapshot, the
  validator) and 5.4's F41 (the sandbox files, the rule catalog and its pin). Owns: `Loading/ModelPaths.cs`
  (`CalculationScript`), `Loading/ModelLoader.cs` (enumerate `extensions/calculated`), `Model/ModelSnapshot.cs`
  (`CalculationScripts`, `Create`, `CreateAfter`), the pass-through of the calculation files at `ModelStore.cs:652`,
  `Loading/ChangePlanner.cs` (:912, :928) and `Loading/Materializer.cs` (:175), `Scripting/ScriptSandbox.cs`
  (`IsCalculationScript`; rule and calculation files share the scope per file and the early freeze;
  `maquettiste.calculated`, refused outside calculation files, and the other registrations refused inside them),
  `Scripting/ScriptingContracts.cs` (`ScriptRegistrationKind.CalculatedValue`, `ScriptRegistration.CalculatedValue`,
  `CalculatedValueDeclaration`), `Scripting/ScriptSandboxFactory.cs`, `Resolution/ResolvedKinds.cs` (new: `All`,
  `TypeOf`), `Validation/ModelValidator.cs` (whole-model validations load the calculation files and report MQ5002,
  MQ5003 and MQ5005 on them), `Diagnostics/RuleCatalog.cs` (the texts of MQ5002, MQ5003, MQ5005). Tests:
  `CalculationScriptTests`, `RegistrationTests` additions. Verify:
  `E "~Scripting|~Loading|~ModelValidatorTests|~CatalogTests"`; `record`.
- **F51. Evaluation, sites and the cache.** Runs after F50 and D43 (3.4's `x:` key). Owns: `Resolution/RCalculated.cs`
  (new: `RCalculated`, `CalculatedValueException`), `Resolution/ResolvedCore.cs` (`RAnnotated.Calculated`, internal
  `ResolvedModel.Calculated`), `Resolution/CalculationService.cs` and `Resolution/CalculationHost.cs` (new),
  `Resolution/ModelResolver.cs` (constructor, attach), `Resolution/ResolveRun.cs` (the host on annotated objects),
  `Resolution/ResolvedKinds.cs`
  (`SiteOf`), `Validation/ResolvedResultCache.cs` (new), `Scripting/ScriptingContracts.cs` (`RunCalculation`),
  `Scripting/ScriptSandbox.cs`, `Scripting/ScriptSandbox.Calls.cs` (the memory baseline after a nested read),
  `Scripting/ScriptValues.cs` and `Scripting/JsModelProxy.cs` (the `calculated` view, plain copies at hop 0),
  `Planning/DependencyHasher.cs` (`cv:<kind>`), `Generation/EngineServices.cs` (`CalculationService`), the fake `Lease`
  and `CountingSandboxFactory` in `ValidationSupport.cs`, `Diagnostics/RuleCatalog.cs` (MQ5010 to MQ5013),
  the `CatalogTests` pin (+4). Tests: `CalculationEvaluationTests`, `CalculationCacheTests`, the `JsModelProxyTests`
  additions, `ResolvedNullContractTests`, the `HashingAndStateTests` case. Verify:
  `E "~Scripting|~Resolution|~Planning|~CatalogTests"`; `record`.
- **F52. Templates read calculated values.** Runs after F51. Owns: `Rendering/TrackingTemplateContext.cs`,
  `Rendering/TemplateValues.cs` (`CalculatedAccessor`), `Rendering/BuiltinHelpers.cs` (`has_calculated`, reserved),
  `Rendering/RenderRun.cs` (a failed calculated value fails its unit with its diagnostic), `Generation/PlanExplainer.cs`
  (the words for `tr:`), `Rendering/README.md`. Tests: `CalculatedValueReadTests`. Verify:
  `E "~Rendering|~Planning|~Generation"`; `P "~GoldenTests"`.
- **F53. Checks after resolution and resolved rules.** Runs after F51 and F25 (5.2's libraries in rule pools). Owns:
  `Validation/ResolvedChecks.cs` and `Validation/ResolvedLocations.cs` (new), `Resolution/ResolvedKinds.cs`
  (`ObjectsOf`), `Resolution/ResolvedPhysical.cs` and `Resolution/TableBuild.cs` (internal `RTable.FileId`),
  `Scripting/ScriptSandbox.cs` (`scope: "resolved"`), `Scripting/ScriptSandbox.Calls.cs`,
  `Scripting/ScriptingContracts.cs` (`RunResolvedRule`, `ScriptRegistration.Resolved`), `Validation/ModelValidator.cs`
  (skip resolved rules), `ModelStore.ResolverFindings.cs` (`FindAsync` runs the checks), `ModelStore.cs`
  (`ValidateCoreAsync` drops resolved-rule findings when `IncludeScriptRules` is false), `Generation/GenerationRun.cs`
  (`PrepareAsync` runs them), `Generation/EngineServices.cs` (`ResolvedChecks`), the fake `Lease` and
  `CountingSandboxFactory`. Tests: `ResolvedRuleTests`, `ResolvedLocationsTests`, `ResolvedChecksTests`. Verify:
  `E "~Validation|~Integration|~ModelStoreTests|~Generation|~Resolution"`.
- **F54. Reads for agents, the CLI and extension files.** Runs after F52, F53 and 5.4's F42 (the agent tools,
  `src/Maquettiste.Cli/CliApp.cs`, `ModelTools.Packs.cs`, `GenerationService.Authoring.cs`, `docs/api/openapi.yaml`,
  `docs/mcp.md`). Owns: `GenerationService.Resolved.cs`
  (`GetCalculatedAsync`, `CalculatedValueQuery`, `CalculatedValueItem`, `CalculatedValueAnswer`, page calculated
  values), `Editor/ResolvedRecords.cs` (`ResolvedPage.Calculated`), `GenerationService.Authoring.cs` (template context),
  `ModelStore.Extensions.cs` (path pattern, kind `calculated`, `CheckCalculationScript`,
  `ExtensionFileInfo.Calculations`, `ExtensionRuleInfo.Resolved`), `Agent/AgentTools.cs`,
  `Agent/AgentTools.Handlers.cs`, `Agent/tools.json`, `src/Maquettiste.Cli/Mcp/ModelTools.cs`,
  `ModelTools.Extensions.cs` and `ModelTools.Packs.cs` (descriptions), `src/Maquettiste.Cli/Commands/ModelCommand.cs`
  (`model calculated`), `src/Maquettiste.Cli/CliApp.cs` (usage), `docs/api/openapi.yaml`, the generated editor files,
  `src/editor/src/mocks/model/extensions.ts` (the new kind), `docs/mcp.md`, `README.md` (the tool count). Raises the MCP
  tool pin by 1 and the `AgentTools.All` pin by 1 (in `AgentToolsTests` and `McpSurfaceTests`). Tests: the
  `AgentToolsTests`, `McpSurfaceTests`, `McpReadTests`, `McpExtensionToolTests`, `ExtensionFilesTests` and
  `ModelCommandTests` cases. Verify: `E "~AgentToolsTests|~ExtensionFilesTests"`;
  `C "~McpSurfaceTests|~McpReadTests|~McpExtensionToolTests|~ModelCommandTests"`;
  `F "~ExtensionEndpointTests|~ContractTests"`; `editor`.
- **F55. Docs, skill and acceptance.** Runs after F54 and 5.4's F46 (the docs, `packs/README.md`, the skill and
  `docs/engineering/engine-design.md`). Owns: `docs/user-guide.md` (the calculated values section,
  `scope: "resolved"` and the `model calculated` row), `packs/README.md`, `Scripting/README.md`, `Validation/README.md`,
  `docs/engineering/engine-design.md`, `skills/maquettiste-modeling/SKILL.md`, `docs/releases.md`,
  `tests/Maquettiste.Engine.Tests/Integration/CalculatedValueAcceptanceTests.cs` (new). Tests: both acceptance tests.
  Verify: `E "~Integration"`; `C "~SkillRefreshTests"`.

### 5.6 Declarative rules

**Why every team**

Every team has conventions that are simple to say and tedious to check: every audited table records when a row last
changed, every entity in a package has a description, every enum is used somewhere, every column named `email` is
sensitive. Today each one needs JavaScript, which many modelers do not write and every reviewer must read line by line.
A rule declared in JSON says the same thing in a few lines that people, agents and tools can read, and it runs without
the sandbox. Every team also meets findings that are right in general and wrong for one element. Today the only switch
is project-wide, so silencing one exception hides the rule everywhere. A suppression on that element, with a required
reason, keeps the rule on for the rest of the model and records why this element is the exception.

**Today**

- Project checks are JavaScript only: `maquettiste.rule` (`Scripting/ScriptSandbox.cs:651-704`). The only switch per
  rule is project-wide: `validation.rules` sets a severity, or `off`, for every element
  (`Validation/ModelValidator.cs:1046-1080`, `schemas/v1/maquettiste.json:301-315`).
- Elements carry `rationale`, `decision` and `status` (`Model/ElementBase.cs:27-34`), and nothing that records an
  exception to a rule.
- A report holds diagnostics and their counts (`Diagnostics/Diagnostic.cs:78`); a SARIF result has no `suppressions`
  (`Validation/SarifWriter.cs:119`).
- Validation reads files; generation reads the resolved model. The billing invoice register is an overlay file with one
  column (`tests/fixtures/models/billing/.maquettiste/model/databases/main/tables/01j92p0v1t0j6rh4my9h81nyb4.json:7`,
  `:17-31`), yet the generated table has `updated_at`, from Invoice's virtual attribute `updatedAt`
  (`tests/fixtures/golden/sql-ddl/billing/main/billing/tables/invoices.sql:13`). A check over the file would miss it.
- Resolver findings are already a channel after validation that every validate path and generation report
  (`ModelStore.ResolverFindings.cs:51-79`). Section 5.5 adds the checks after resolution to it.
- Extension files have two kinds, `schema` and `rule` (`ModelStore.Extensions.cs:361`), at `<name>.json` and
  `rules/<name>.js` (`ModelStore.Extensions.cs:75-76`). Extension schemas name stereotypes by key
  (`schemas/v1/extension.json:61`).

**Decisions taken here**

- **Rule declaration files.** `extensions/rules/<name>.json`, beside the rule scripts, so one folder holds a project's
  rules in both forms. A file holds a list of rules. Their findings are `x/<id>`, the namespace of script rules, and
  `validation.rules` overrides them alike. They are extension files, not elements, as rule scripts are; the extension
  file tools read and write them. The schema is `schemas/v1/declared-rules.json`; the guide calls them declared rules.
- **They judge the resolved model.** A declared rule reads what generation reads: names after conventions, columns a
  binding shapes, stereotype attributes, and, with 3.1, inherited members. A rule over files would wrongly flag the
  invoice register above. So declared rules run in the checks after resolution (5.5): `validate` (whole, or scoped and
  filtered to its elements, as resolver findings are), `plan` and `generate`. A save never waits for them, and they
  report once the model validates without other errors, as resolver findings do. An error stops generation.
- **Paths, not pointers.** A path names members as pack scripts see them, in camel case (`name`, `nullable`,
  `pluralName`), a map key after a map (`properties.retentionDays`, `calculated.publicName`) and a member of an object
  after an object (`table.name` on a column). Templates see the same members in snake case. JSON pointers address files,
  which the resolved model is not.
- **Links.** Stereotypes by key, which is immutable (MQ3020) and resolves to an id, as in element files and extension
  schemas; packages by id; tags by key; kinds and custom property names by name. Nothing matches names.
- **A closed set of conditions.** `all`, `any` and `not` combine; `has` (present), `equals`, `count` (related objects
  in a list), `references` and `referencedBy` (links of a kind, from the index) test. Phase 3 adds an `expression`
  from 3.8, judged at a guard site.
- **Two-valued, with null as absent.** A path through a null value yields null. `has` is false for null, an empty
  string, an empty list and an empty map. `equals` with `null` matches a null or missing value.
- **Inherited members are members.** A rule reads the resolved lists, so an inherited column counts like an own one.
  The subsection below says exactly what a rule sees.
- **Static checks before resolution.** Whole-model validation checks every declaration (MQ5015, MQ5016), so a broken
  file stops resolution, as a broken rule script does. Whether a rule selects anything (MQ5017) needs the resolved
  model.
- **Suppressions belong to elements.** Every element file may hold `suppressions`: a rule id, a required reason and,
  optionally, the id of one of its parts. They travel with the element, a reader of the element sees them, and deleting
  the element removes them. The rule stays on everywhere else.
- **Suppressions apply after severities.** `validation.rules` first decides each finding's severity, or drops it; then a
  matching suppression moves the finding into the report's `suppressed` list with its reason. That happens wherever
  findings leave validation and resolution, so a suppressed error neither refuses a save nor stops generation.
- **What cannot be suppressed.** MQ1xxx, which `validation.rules` cannot turn off, and MQ7012, whose severity it cannot
  change (`Validation/ModelValidator.cs:38-39`, `:1062`): hiding them would let a file vanish from generation without
  an error. Nor the three suppression rules.
- **Unused suppressions warn.** A suppression that matches no finding in a whole-model validate (no element ids,
  referrers and script rules on) is MQ5020, so stale exceptions get noticed. It is judged only when resolution ran,
  since otherwise some findings were never made, and once, after every finding of that validate is in.
- **`member` is an id, not indexed.** It names a part of the same file, an attribute the element holds through a base
  or a stereotype, or an inherited member by its keyed id (3.1: the table's key and the member's id joined by `.`,
  where a synthesized table's key holds `@`). Where-used gains nothing; a member that no longer exists warns (MQ5019)
  instead of refusing the save that removed it.
- **Nothing else changes.** A suppression changes no generated byte and records no migration fact; declared rules read
  no database and the ledger stays at version 1.

#### Rule declaration files

```json
{
  "$schema": "../../.schema/v1/declared-rules.json",
  "rules": [
    {
      "id": "audited-tables-have-updated-at",
      "severity": "error",
      "description": "Audited tables record when a row last changed.",
      "select": { "kind": "table", "stereotype": "audited" },
      "require": {
        "count": { "of": "columns", "where": { "equals": { "path": "name", "value": "updated_at" } }, "min": 1 }
      },
      "message": "Table {name} carries audited but has no updated_at column."
    }
  ]
}
```

`schemas/v1/declared-rules.json` (new, with its sample copy; `KindInfo.DocumentSchemaFiles` lists it). In `x-order`,
the root holds `$schema` and `rules`; a rule holds the keys in the order below.

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| `rules` | array of rules, at least one | — | |
| `id` | string, `^[A-Za-z0-9][A-Za-z0-9._-]*$` | required | Findings are `x/<id>`; unique across declarations and rule scripts (MQ5015). |
| `severity` | `error`, `warning` or `info` | `error` | `validation.rules` overrides it by `x/<id>`. |
| `description` | string | absent | Shown by `list_extension_files` and in the guide's tables. |
| `select` | selector | required | Which resolved objects the rule judges. |
| `require` | condition | required | What must hold for each of them; a finding where it does not. |
| `message` | string, at least one character | required | The finding's text, with placeholders. |

Selector keys combine with AND. Each takes one value or a list, and a list means any of its values.

| Key | Values | Matches an object |
| --- | --- | --- |
| `kind` (required) | names from `ResolvedKinds.All` (5.5) | of that kind |
| `stereotype` | stereotype keys | whose `stereotypes` (the effective set: own, implied and, with 3.1, schema and package defaults) hold the key |
| `tag` | tag keys | whose `tags` hold the key |
| `property` | custom property names | whose `properties` hold a non-null value for the name |
| `package` | package ids | whose `package` is the package or one nested in it (a table falls back to its entity's or relation's package) |

A condition is an object with exactly one of these keys:

| Key | Value | Holds when |
| --- | --- | --- |
| `all` | array of conditions, at least one | every one holds |
| `any` | array of conditions, at least one | at least one holds |
| `not` | condition | it does not hold |
| `has` | path | the value is present |
| `equals` | `{ path, value }` | the value equals `value` (below) |
| `count` | `{ of, where, min, max }` | the number of objects in the list at path `of` for which `where` holds (all when absent) is at least `min` and at most `max`; `min` 1 when both are absent |
| `references` | `{ kind, field, min, max }` | the object links to at least `min` (default 1) and at most `max` objects of `kind`, through `field` when given |
| `referencedBy` | `{ kind, field, min, max }` | objects of `kind` link to the object, through `field` when given, within the same bounds |

- **Values.** Strings compare ordinally and numbers by value; booleans as booleans; an enum member by its JSON name; a
  resolved object by its id; lists item by item; maps by keys and values.
- **Lists.** `count.of` ends at a list of objects: resolved objects (`columns`, `attributes`, `ownAttributes`, an
  enum's `members`) or the records a table holds (`indexes`, `checks`, `foreignKeys`, `uniques`, which are not
  resolved objects of their own); `where` is relative to each item and may nest. A list of plain values (`tags`,
  `formerNames`) takes `has` or `equals`.
- **Links.** `references` and `referencedBy` read the index (`ModelSnapshot.ReferencesFrom`, `ReferencesTo`), so they
  see objects whose id the index holds. `kind` is an element kind or a part kind as the index names it (`attribute`,
  `enum-member`, `column`, `row`...); `field` is the reference's field (`type`, `subject`, `package`...). References
  from diagrams and from inside the object itself do not count: a diagram shows an element, it does not use it.

#### How a rule sees inherited members

A declared rule reads the resolved object, never its file:

- A table's `columns` lists every column generation sees: own columns, the columns 3.1 contributes from the table's
  stereotypes (keyed id `<table key>.<memberId>`, with the facets the table redefines applied), and, for a table a
  binding or mapping shapes, the columns resolution adds. A column the table `excludes` is absent. The same holds for
  `indexes`, `checks`, `foreignKeys` and `primaryKey`. The member through which 3.1 states where a column came from is
  a path like any other, so a rule can ask for own columns only.
- An entity's `attributes` lists inherited, own and stereotype attributes, and `ownAttributes` its own
  (`Resolution/ResolvedConceptual.cs:50-55`).
- So `audited` contributing `updated_at` through 3.1 satisfies the rule on every table that carries `audited`; a table
  that excludes it fails the rule again; a redefinition that makes it nullable keeps it counted.
- A finding about an inherited column, from a rule that selects `column`, lands on the table's file (rule 3 of
  `ResolvedLocations`, 5.5) and keeps the column's keyed id as its element id, so a suppression on the table with that
  `member` matches it.

#### Messages

`{<path>}` inserts the value at a path of the selected object: a string as it is, a number in invariant form, `true`
or `false`, an enum member's JSON name, a resolved object's name (its id when it has none), nothing for null, and a
list or map as compact JSON. `{$count}` inserts the number the top-level `count`, `references` or `referencedBy`
found. `{{` and `}}` write braces.

#### Where declared rules run

- **Load.** The loader reads `extensions/rules/*.json` (`ModelFileKind.DeclaredRules`, `Loading/DocumentReader.cs`)
  and checks each against `declared-rules.json`; a file that fails is MQ5014, a load diagnostic on the file, and loads
  no rules. The loader's backstop for content it did not expect reports MQ5014 on these files, not MQ1001
  (`Loading/ModelLoader.cs:359`), so `validation.rules` treats them as the other MQ50xx findings.
  `ModelSnapshot.DeclaredRules` holds the valid files, ordinal by path.
- **Static checks** (`Validation/DeclaredRuleChecks.cs`, whole-model validations only, on the declaration file with a
  pointer into it). MQ5015: an id another declaration or a rule script already uses, on the later path; judged when
  every rule script loads, as MQ2007 is. MQ5016: a `kind` outside `ResolvedKinds.All`; a stereotype key or package id
  the model lacks; a path whose first member no selected kind has, or that goes on past a value that is neither a map
  nor an object; a `calculated.<name>` that a selected kind does not declare (when the calculation files load; so a
  declared rule never reaches 5.5's MQ5012 at evaluation); a `count.of` that is not a list of objects; a link `kind` the
  index does not know; `min` above `max`; a placeholder that is not a path, or `{$count}` without a top-level count. Tag
  keys and property names are not checked: a tag no element uses selects nothing (MQ5017).
- **Evaluation** (`Validation/DeclaredRuleEvaluator.cs`, inside `ResolvedChecks.RunAsync`). For each rule, ordinal by
  id, over `ResolvedKinds.ObjectsOf` for its kinds: objects the selector matches are judged, in parallel. A failing
  object gives `x/<id>` with the rule's severity, then `validation.rules` (`off` skips the rule), its rendered message,
  and the location `ResolvedLocations` gives. A rule whose selector matches no object is MQ5017 on its `select`. Paths
  that read calculated values evaluate them through the object's `RCalculated`, cached as 5.5 says.
  `includeScriptRules: false` does not leave declared rules out, since they are not scripts.

#### Suppressions

`schemas/v1/common.json` gains `$defs/suppressions`, and every kind schema gains `suppressions` in `x-order` after
`status`, or after `satisfies` (a scenario's `verifies`) where 4.3 puts it after `status`. That is the 25 kind schemas
of today and the three milestone E adds before this task runs (`operation.json`, `custom.json`, `requirement.json`): 28
files, each with its copy. An entry's `x-order` is `rule`, `member`, `reason`; it takes no other key.

| Key | Type | Notes |
| --- | --- | --- |
| `suppressions` | array of entries, default `[]` | On the element's file only, never inside a part. |
| `rule` | string, `^(MQ[0-9]{4}\|x/\S+)$`, required | A built-in rule, a script rule or a declared rule. |
| `member` | string, ids joined by `.` or `@`: `^[0-7][0-9A-HJKMNP-TV-Z]{25}([.@][0-7][0-9A-HJKMNP-TV-Z]{25})*$`, optional | Limits the entry to one part of the element: a part in its file (its id), an attribute it holds through a base or a stereotype (the attribute's id), or an inherited member (3.1's keyed id, `<table key>.<member id>`). |
| `reason` | string, at least one non-space character, required | Why this element is the exception. |

```json
"suppressions": [
  {
    "rule": "x/audited-tables-have-updated-at",
    "reason": "Notes are append-only; a row never changes after it is written."
  }
]
```

- **Matching.** A finding is on element E when its file is E's file. It is on member M of E when, in addition, its
  element id is M, or its pointer is M's entry pointer or lies under it (the entry is looked up in the index at match
  time, so moving a part in its array changes nothing). Entry i of E's `suppressions` matches a finding on E (on M, when
  `member` is set) whose rule is `rule`. The first matching entry wins. `Validation/Suppressions.cs` (new) builds the
  entries once per snapshot.
- **Where they apply**, after severities each time:
  1. `ModelValidator.ValidateAsync`, after `ApplySettings` (`Validation/ModelValidator.cs:205`) and before tag findings
     are collapsed, so a save sees suppressed findings removed.
  2. The resolver's diagnostics when its run ends (`Resolution/ResolveRun.cs:172`); the internal
     `ResolvedModel.Suppressed` keeps the ones it moved.
  3. The output of `ResolvedChecks.RunAsync`; `ResolvedCheckResult` (5.5) gains `Suppressed`.
  4. `ModelStore.ValidateCoreAsync` (`ModelStore.cs:479`), for the stale findings it adds, and
     `ValidateAgainstAsync` (`ModelStore.Migrations.cs:867-869`), for the baseline findings it adds. Each merges every
     suppressed list into the report. MQ5020 is judged once, by the outermost of them, after its own findings, on a
     whole-model report whose model resolved: one warning for each entry nothing matched.

  Every place that builds a report again from another keeps `Suppressed`: `ModelStore.cs:501`,
  `ModelStore.Migrations.cs:869` and the CLI's merge with pack findings
  (`src/Maquettiste.Cli/Commands/ValidateCommand.cs:69`).
  Suppressed findings are positioned (line and column) as reported ones are. `ModelValidator`'s `NamesRules`
  (`Validation/ModelValidator.cs:799`) also counts `x/` ids in suppressions, so a save loads the rule pool to judge
  them, as it does for MQ2007.
- **The report.** `ValidationReport.Suppressed` lists `SuppressedFinding`s in diagnostic order: the finding as it would
  have been reported, the reason, and the element and pointer (`/suppressions/<i>`) of the entry. The counts exclude
  them. `TruncateTo` empties the list before it drops diagnostics.
- **Outputs.** `maquettiste validate` prints `<n> findings suppressed` after the summary when n > 0; `--format json`
  writes `suppressed` (`schemas/v1/diagnostics.json`); `--format sarif` writes each suppressed finding as a result with
  `suppressions: [{ "kind": "inSource", "status": "accepted", "justification": <reason> }]`, as SARIF 2.1.0 defines.
  The rules list covers the suppressed findings' rules too, and an `x/` rule's descriptor reads "Project validation
  rule <id>." (`Validation/SarifWriter.cs:104-117` says "JavaScript", which a declared rule is not). The MCP `validate`
  answer and `POST /api/validate` carry `suppressed`.
- **Rules on the entries** (`Validation/SuppressionRules.cs`, per element, so saves see them): MQ5018 at
  `/suppressions/<i>/rule` for a rule that cannot be suppressed; MQ5019 for a rule nothing defines (built-in ids from
  the catalog; `x/` ids when every rule script loads and the declarations are valid), a `member` the element does not
  hold, or an entry that repeats another's rule and member.
- `ResolvedCoverageTests` excuses `suppressions` in `NotResolvedAnywhere` ("accepted findings: validation reads
  them, templates describe the model"), not in `Pending`, whose entries a later round must close.

#### Public API

```csharp
namespace Maquettiste.Engine.Model;
public abstract record Element : ElementBase
{   // existing members, plus:
    /// <summary>Findings this element accepts, each with its reason (section 5.6).</summary>
    public IReadOnlyList<Suppression> Suppressions { get; init; } = [];
}
public sealed record Suppression
{
    public required string Rule { get; init; }
    public required string Reason { get; init; }
    /// <summary>The id of one part of the element the entry is limited to (an inherited member's keyed id included),
    /// or null for the whole element.</summary>
    public string? Member { get; init; }
}
/// <summary>extensions/rules/*.json.</summary>
public sealed record DeclaredRuleFile { public IReadOnlyList<DeclaredRule> Rules { get; init; } = []; }
public sealed record DeclaredRule
{
    public required string Id { get; init; }
    public DiagnosticSeverity Severity { get; init; } = DiagnosticSeverity.Error;
    public string? Description { get; init; }
    public required RuleSelector Select { get; init; }
    public required RuleCondition Require { get; init; }
    public required string Message { get; init; }
}
/// <summary>Each key reads one value or a list into a list; a list means any of its values.</summary>
public sealed record RuleSelector
{
    public required IReadOnlyList<string> Kind { get; init; }
    public IReadOnlyList<string> Stereotype { get; init; } = [];
    public IReadOnlyList<string> Tag { get; init; } = [];
    public IReadOnlyList<string> Property { get; init; } = [];
    public IReadOnlyList<string> Package { get; init; } = [];
}
/// <summary>Exactly one member is set.</summary>
public sealed record RuleCondition
{
    public IReadOnlyList<RuleCondition>? All { get; init; }
    public IReadOnlyList<RuleCondition>? Any { get; init; }
    public RuleCondition? Not { get; init; }
    public string? Has { get; init; }
    [JsonPropertyName("equals")] public RuleEquality? Equal { get; init; }
    public RuleCount? Count { get; init; }
    public RuleReferences? References { get; init; }
    public RuleReferences? ReferencedBy { get; init; }
}
public sealed record RuleEquality(string Path, JsonElement Value);
public sealed record RuleCount(string Of, RuleCondition? Where, int? Min, int? Max);
public sealed record RuleReferences(IReadOnlyList<string> Kind, string? Field, int? Min, int? Max);
public sealed record DeclaredRuleDocument(DeclaredRuleFile Rules, string Path, string Hash);
public sealed class ModelSnapshot
{   // existing members, plus:
    /// <summary>The valid rule declaration files, ordinal by path.</summary>
    public IReadOnlyList<DeclaredRuleDocument> DeclaredRules { get; }
    // Create gains a last optional parameter: IReadOnlyList<DeclaredRuleDocument>? declaredRules = null.
}

namespace Maquettiste.Engine.Diagnostics;
public sealed record ValidationReport(/* unchanged */)
{
    /// <summary>The findings suppressions moved out of the report, in diagnostic order; not counted.</summary>
    public IReadOnlyList<SuppressedFinding> Suppressed { get; init; } = [];
}
/// <param name="ElementId">The element whose suppressions hold the entry.</param>
/// <param name="JsonPointer">The entry: /suppressions/i.</param>
public sealed record SuppressedFinding(Diagnostic Finding, string Reason, string ElementId, string JsonPointer);

namespace Maquettiste.Engine.Validation;
public static class SarifWriter
{   /// <summary>Writes a report's diagnostics, then its suppressed findings as results with in-source suppressions.
    /// </summary>
    public static Task WriteAsync(Stream output, ValidationReport report, string toolVersion, CancellationToken ct);
}
```

#### CLI and MCP

- No new verb or tool. `maquettiste validate` reports declared rule findings and the suppressed count; `--format json`
  and `--format sarif` carry the suppressed findings, as above.
- MCP `validate`: the report carries `suppressed`; its description says so. `get_schema` already takes any embedded
  schema file by name (`Agent/AgentTools.Handlers.cs:198-205`), so it returns `declared-rules` once the file is
  embedded; only its description changes.
- Extension file tools: `rules/<name>.json` is kind `declared-rules`. A write must be valid JSON; it is written in
  canonical form (as schemas are) and checked alone: MQ5014, MQ5015 against the other declarations and rule scripts,
  and MQ5016, with pointers. `list_extension_files` lists each file's rules as `ExtensionRuleInfo` with `Resolved`
  true, whose meaning widens from 5.5's to "runs on the resolved model"; a move keeps a declaration file a declaration
  file.
- HTTP mirrors the contract: `ValidationReport.suppressed`, `SuppressedFinding`, the extension file kind
  (`docs/api/openapi.yaml`).

#### Rules

| Id | Severity | Rule | Where |
| --- | --- | --- | --- |
| MQ5014 | E | A rule declaration file does not fit `declared-rules.json`. | The file, a load diagnostic |
| MQ5015 | E | Two project rules share an id: a declared rule and another declared rule or a rule script. | The later file, at the id |
| MQ5016 | E | A declared rule names what does not exist or does not apply: a kind, a stereotype key, a package id, a path, a calculated value, a link kind, bounds or a message placeholder. | The declaration file, at the value |
| MQ5017 | W | A declared rule selects no object. | The declaration file, at `select` |
| MQ5018 | E | A suppression names a rule that cannot be suppressed: MQ1xxx, MQ7012 or MQ5018 to MQ5020. | The element, at the entry's `rule` |
| MQ5019 | W | A suppression names a rule nothing defines or a member the element does not hold, or repeats another entry. | The element, at the entry |
| MQ5020 | W | A suppression matched no finding in a whole-model validate. | The element, at the entry |

The catalog grows by 7. Findings of declared rules are `x/<id>`, not catalog rules.

#### Editor follow-ups

Engine, CLI and MCP first. The generic JSON form edits `suppressions`; the Extensions tab lists declaration files with
their kind once the types are regenerated. Deferred: a form for rule declarations with live counts of what a selector
matches; "Suppress here…" on a finding in the Problems panel, asking for the reason; a view of suppressed findings;
declared rule ids in Settings › Validation beside script rule ids.

#### Docs

`docs/user-guide.md`: new sections "Declared rules (`extensions/rules/*.json`)", with the inherited-members paragraph
above, and "Suppressions"; the MQ50xx family paragraph. `Validation/README.md` (static checks, evaluation, where
suppressions apply). `docs/engineering/engine-design.md` §6. `docs/mcp.md` (`validate`, `get_schema`, the extension
file rows). `skills/maquettiste-modeling/SKILL.md` (prefer a declaration to a script for a simple convention; a
suppression needs a reason). `docs/releases.md` under `## 0.12.0 (unreleased)`.

**Not doing**

- Expressions in conditions. Phase 3 adds them from 3.8; until then the closed set above.
- Declared rules as model elements. They stay extension files, like rule scripts.
- Declared rules on element saves. They need the resolved model; validate, plan and generate report them.
- Quick fixes in declarations.
- Suppressions with an expiry date. A date would make validation depend on the clock.
- Suppressions outside elements, or by selector. A rule that is wrong for a group of elements needs a narrower
  `select`; settings and extension files keep `validation.rules`.
- Wildcards in suppression rule ids.

**Tests**

- `DeclaredRuleFileTests` (Loading, new): `A_declaration_file_loads_into_the_snapshot`,
  `A_file_that_fails_its_schema_is_MQ5014_and_loads_no_rules`, `A_condition_takes_exactly_one_operator`,
  `Selector_values_read_one_value_or_a_list`.
- `DeclaredRuleCheckTests` (Validation, new): `An_id_taken_by_a_rule_script_is_MQ5015_on_the_later_file`,
  `An_unknown_kind_stereotype_or_package_is_MQ5016`, `A_path_no_selected_kind_has_is_MQ5016`,
  `An_undeclared_calculated_value_in_a_path_is_MQ5016`, `A_count_of_a_list_of_values_is_MQ5016`,
  `An_unknown_placeholder_or_count_without_a_count_is_MQ5016`, `A_broken_declaration_stops_resolution`.
- `DeclaredRuleEvaluationTests` (Validation, new): one passing and one failing case per condition (`all`, `any`, `not`,
  `has`, `equals`, `count`, `references`, `referencedBy`); `Selector_keys_combine_and_lists_mean_any_of`;
  `Package_selects_nested_packages`; `Stereotype_reads_the_effective_set`; `Messages_render_paths_and_count`;
  `A_rule_that_selects_nothing_is_MQ5017`; `Validation_rules_overrides_and_off_apply_to_x_ids`;
  `Diagram_references_do_not_count`; `A_path_through_calculated_values_reads_the_cached_value`;
  `Count_reads_lists_of_records_such_as_indexes`; `Findings_are_byte_identical_across_parallelism`.
- `SuppressionRuleTests` (Validation, new): `An_MQ1xxx_or_MQ7012_rule_is_MQ5018`,
  `An_unknown_rule_or_missing_member_is_MQ5019`, `A_repeated_entry_is_MQ5019`,
  `A_member_may_be_an_inherited_or_virtual_member`, `A_member_may_be_the_keyed_id_of_a_synthesized_tables_member`,
  `A_save_judges_x_ids_in_suppressions_against_the_loaded_rules`.
- `SuppressionTests` (Validation, new): `A_suppression_hides_a_finding_on_its_element_only`,
  `A_member_limits_it_to_that_part_and_survives_a_move_in_its_array`, `A_suppressed_error_does_not_refuse_a_save`,
  `Resolver_and_check_findings_are_suppressed_too`, `Suppressions_apply_after_severity_overrides`,
  `An_unused_suppression_is_MQ5020_only_when_the_model_resolved`, `Suppressed_findings_carry_their_reason_and_entry`,
  `Validate_against_a_baseline_suppresses_its_findings_and_judges_MQ5020_after_them`.
- `SarifWriterTests.Suppressed_findings_are_results_with_in_source_suppressions`;
  `ValidateTests` (CLI): `Text_output_counts_suppressed_findings`, `Json_output_lists_them`,
  `The_report_merged_with_pack_findings_keeps_suppressed`.
- `Integration/ExtensionFilesTests.A_declaration_file_is_written_canonically_and_checked_alone`;
  `McpExtensionToolTests.Declaration_files_list_their_rules`;
  `AgentToolsTests.Get_schema_returns_the_declared_rules_document`.
- **Acceptance (8.3, done when):**
  `Integration/DeclaredRuleAcceptanceTests.Every_audited_table_has_an_updated_at_column_is_a_declaration` (new). Setup:
  an `E2ERepo` billing copy gains `extensions/rules/audit.json` as above, and the `notes` table
  (`tables/notes.json`) gains the stereotype `audited`. Expected: `validate` reports one error,
  `x/audited-tables-have-updated-at` on `.maquettiste/model/databases/main/tables/notes.json` at the root, with the
  message "Table notes carries audited but has no updated_at column."; the invoice register, which also carries
  `audited` and gets `updated_at` from Invoice's virtual attribute, has none; `generate` stops with the error.
- **Acceptance (inherited members):**
  `Integration/DeclaredRuleAcceptanceTests.An_updated_at_inherited_from_audited_satisfies_the_rule`. Setup: the same
  copy; `audited` gains a contributed column `updated_at` (3.1; `datetimeoffset`, nullable). The invoice register
  keeps `audited`: 3.1 skips, on a synthesized table, the members of a stereotype whose virtual attributes the table
  already stores as columns, and Invoice's `updatedAt` already gives it `updated_at`. Expected: `validate` reports
  nothing for the rule; then `notes` excludes the contributed column, and the finding on `notes.json` is back.
- **Acceptance (suppressions, for declared and built-in rules):**
  `Integration/DeclaredRuleAcceptanceTests.A_suppression_with_a_reason_accepts_one_exception`. Setup: the first case's
  copy; `notes` gains the suppression shown above. Expected: `validate` has no error, `suppressed` holds the finding
  with its reason and the pointer `/suppressions/0`, SARIF has the result with that justification, and `generate`
  runs. Then `InvoiceStatus` gets `status: "retired"`, and Invoice gains an entry with rule `MQ2022`, member
  `01J92P0V0VB1Z49SWERMAGR4TV` (its `status` attribute) and the reason "Kept until open invoices move to the new status
  list."; the MQ2022 warning on that attribute moves to `suppressed`. Then `notes` drops `audited`, and `validate`
  warns MQ5020 at `/suppressions/0` of `notes.json`.

**Tasks**

Tasks of 5.1 to 5.4 also own `Validation/ModelValidator.cs`, `Diagnostics/RuleCatalog.cs`, the `CatalogTests` pin,
`Model/ModelSnapshot.cs`, `Loading/ModelLoader.cs`, `Loading/ModelPaths.cs`, `docs/api/openapi.yaml`,
`Agent/tools.json`, `docs/mcp.md`, the docs and the generated editor files. They all precede F60 through F53, since
5.5's F50 runs after 5.2's F25 and 5.4's F41, and 5.2's F21 after 5.1's F11.

- **F60. Rule declaration files load.** Runs after F53 (both own `ModelStore.cs`; F53 runs after F51). Owns:
  `schemas/v1/declared-rules.json` (new) and its copy, `Model/DeclaredRules.cs` (new), `Loading/ModelPaths.cs`
  (`DeclaredRules`), `Loading/DocumentReader.cs` (`ReadDeclaredRules`, MQ5014), `Loading/ModelLoader.cs` (enumerate
  `extensions/rules/*.json`; the backstop's MQ5014), `Model/ModelSnapshot.cs` (`DeclaredRules`, `Create`,
  `CreateAfter`), the pass-through in `ModelStore.cs`, `Loading/ChangePlanner.cs` and `Loading/Materializer.cs`,
  `Model/KindInfo.cs` (`DocumentSchemaFiles`), `Diagnostics/RuleCatalog.cs` (MQ5014), the `CatalogTests` pin (+1).
  Tests: `DeclaredRuleFileTests`. Verify: `E "~Loading|~Json|~CatalogTests"`; `copies`; `record`.
- **F61. Static checks.** Runs after F60 and F53. Owns: `Validation/DeclaredRuleChecks.cs` (new),
  `Validation/ModelValidator.cs` (whole-model reports call it), `Diagnostics/RuleCatalog.cs` (MQ5015, MQ5016),
  the `CatalogTests` pin (+2). Tests: `DeclaredRuleCheckTests`. Verify: `E "~Validation|~CatalogTests"`; `record`.
- **F62. Evaluation.** Runs after F61. Owns: `Validation/DeclaredRuleEvaluator.cs` (new),
  `Validation/ResolvedChecks.cs` (runs it), `Diagnostics/RuleCatalog.cs` (MQ5017), the `CatalogTests` pin (+1). Tests:
  `DeclaredRuleEvaluationTests`. Verify: `E "~Validation|~Integration|~CatalogTests"`; `record`.
- **F63. Suppressions in the model.** Runs after F62, F54 (both own the generated editor files, which the kind
  schemas reach through `docs/api/openapi.yaml`) and D6 (3.1's contributed members, for keyed `member` ids). Owns:
  `schemas/v1/common.json` (`$defs/suppressions`), the 28 kind schemas (25 of today, `operation.json`, `custom.json`,
  `requirement.json`), their copies, the generated editor files, `Model/ElementBase.cs` (`Element.Suppressions`,
  `Suppression`), `Validation/SuppressionRules.cs` (new), `Validation/ModelValidator.cs` (per document; `NamesRules`),
  `tests/Maquettiste.Engine.Tests/Resolution/ResolvedCoverageTests.cs` (`NotResolvedAnywhere`),
  `Diagnostics/RuleCatalog.cs` (MQ5018, MQ5019), the `CatalogTests` pin (+2). Tests: `SuppressionRuleTests`. Verify:
  `E "~Model.|~Json.|~Validation|~Resolution.|~CatalogTests"`; `copies`; `editor`; `record`.
- **F64. Suppressions apply.** Runs after F63. Owns: `Validation/Suppressions.cs` (new),
  `Validation/ModelValidator.cs`, `Resolution/ResolveRun.cs` and `Resolution/ResolvedCore.cs` (internal
  `ResolvedModel.Suppressed`), `Validation/ResolvedChecks.cs` (`ResolvedCheckResult.Suppressed`),
  `ModelStore.ResolverFindings.cs` (`FindAsync` passes the suppressed lists on), `ModelStore.cs` (`ValidateCoreAsync`,
  MQ5020), `ModelStore.Migrations.cs` (`ValidateAgainstAsync`),
  `Diagnostics/Diagnostic.cs` (`ValidationReport.Suppressed`, `SuppressedFinding`, `TruncateTo`),
  `Validation/SarifWriter.cs`, `src/Maquettiste.Cli/Commands/ValidateCommand.cs`,
  `src/Maquettiste.Cli/DiagnosticOutput.cs`, `schemas/v1/diagnostics.json` and its copy, `docs/api/openapi.yaml`, the
  generated editor files, `Diagnostics/RuleCatalog.cs` (MQ5020), the `CatalogTests` pin (+1). Tests: `SuppressionTests`,
  the `SarifWriterTests` and `ValidateTests` cases. Verify:
  `E "~Validation|~Resolution|~ModelStoreTests|~Migrations|~CatalogTests"`; `C "~ValidateTests|~McpReadTests"`;
  `F "~ContractTests"`; `copies`; `editor`; `record`.
- **F65. Declaration files as extension files.** Runs after F54 and F64. Owns: `ModelStore.Extensions.cs` (path
  pattern, kind `declared-rules`, canonical write, `CheckDeclaredRules`, the rules list), `Agent/tools.json` (the
  descriptions of `get_schema`, which names `declared-rules`, and of `validate`, which names `suppressed`),
  `src/Maquettiste.Cli/Mcp/ModelTools.Extensions.cs` (descriptions), `docs/api/openapi.yaml` (the kind), the generated
  editor files, `src/editor/src/mocks/model/extensions.ts`, `docs/mcp.md`. Tests: the `ExtensionFilesTests`,
  `McpExtensionToolTests` and `AgentToolsTests` cases. Verify: `E "~ExtensionFilesTests|~AgentToolsTests"`;
  `C "~McpExtensionToolTests|~McpSurfaceTests"`; `F "~ExtensionEndpointTests|~ContractTests"`; `editor`.
- **F66. Docs, skill and acceptance.** Runs after F55, F65 and D7 (3.1's contributed columns, for the second
  acceptance test). Owns: `docs/user-guide.md` (the two sections), `Validation/README.md`,
  `docs/engineering/engine-design.md`, `skills/maquettiste-modeling/SKILL.md`, `docs/releases.md`,
  `tests/Maquettiste.Engine.Tests/Integration/DeclaredRuleAcceptanceTests.cs` (new). Tests: the three acceptance
  tests. Verify: `E "~Integration"`; `C "~SkillRefreshTests"`.

## 6. Milestone G: agents and environments

### 6.1 Impact before apply

**Why every team**

Before a change lands, every team needs to know how far it reaches: which other elements point at what it changes,
which generated files will be written again, and which databases it can reshape. Agents need this most. They change
many files in one batch, and they need the answer in the dry run they already make, bounded by what the change
touches, so a model with thousands of tables answers as fast as a small one.

**Today**

- `ModelStore.PreviewBatchAsync` (`ModelStore.cs:244-257`) runs a batch as a dry run. When it would save, it returns the
  model files with diffs, the diagnostics added and removed over the change's scope, the ids it would mint and the
  migration facts it would record (`BatchPreview`, `ModelStore.cs:1550-1569`). The preview branch of `ExecuteCoreAsync`
  fills them (`ModelStore.cs:845-858`) from `BatchPreviewSink` (`ModelStore.cs:1621-1634`).
- `facts` is `null` when the change reaches every database (`ModelStore.Migrations.cs:248-250`): a dry run never
  captures every database. With one database that is every change.
- The databases a change can reshape are chosen by ids alone (`ChooseDatabases`, `ModelStore.Migrations.cs:408`), but
  only for changes that reshape (`Shapes`, `ModelStore.Migrations.cs:203-208`); the choice is not returned.
- `SaveResult.Referrers` holds only the references that blocked a delete or that a delete removed (`ModelStore.cs:1346`,
  `Loading/ChangePlanner.cs:40-41`). A dry run says nothing about the elements that point at what it changes.
- Each pack's unit states record, per unit, its key, its read keys and its outputs (`UnitState`,
  `Pipeline/PipelineTypes.cs:163`; `UnitOutput`, `:156`), in one file per pack under the cache
  (`Planning/UnitStateStore.cs:45`, `:139`). The read keys have eight kinds: `e`, `k`, `r`, `s`, `l`, `t`, `d` and `n`
  (`Planning/DependencyHasher.cs:196-211`). `r:` and `k:` hash over the snapshot (`Model/ModelSnapshot.cs:324-343`).
- The unit preview renders one unit on a session that holds the snapshot's resolved model, shared per snapshot
  (`GenerationService.Packs.cs:53-120`, `:334`). The resolver has no partial run of the conceptual layer: a model or a
  set of databases resolves the whole conceptual model (`Resolution/ModelResolver.cs:49-87`).
- MCP `apply_batch` with `dryRun` answers through `BatchOutput.Preview`
  (`src/Maquettiste.Cli/Mcp/ModelTools.cs:196-215`, `src/Maquettiste.Cli/Commands/BatchOutput.cs:31`);
  `model apply --dry-run` prints `BatchOutput.PreviewText` (`src/Maquettiste.Cli/Commands/ModelCommand.cs:261-266`,
  `src/Maquettiste.Cli/Commands/BatchOutput.cs:65`). Neither has an impact.

**Decisions taken here**

1. **Three parts.** A dry run that would save also answers `impact`: the `referrers` of what the change touches, the
   `databases` it can reshape, and per pack the `units` whose stored inputs it touches, with their output files.
2. **No resolve, no plan, no render.** The impact comes from what the dry run already holds (the model before,
   the candidate model, the change's documents) and from each pack's stored unit states. Rendering the touched units
   would need the candidate resolved, and the resolver resolves the whole conceptual model
   (`Resolution/ModelResolver.cs:49-87`). Decision 5 of section 1 keeps whole-model work for validate, plan and
   generate. So the impact names the files whose units would render again; it does not say whether their bytes change.
   To see the bytes, apply and run `plan` with `elements` or `units` (phase 1, section 5.6 of `next-phase-1-design.md`).
   The dry run's own fact recording (phase 1) keeps the partial resolution it already does for a change that reaches
   some databases but not all; the impact adds none.
3. **Touched keys.** A unit is touched when one of its stored read keys is touched. The table below says when; it reads
   only the change's documents, the two snapshots' indexes, the reached databases and the listed facts, plus the
   candidate's name index for a kind that a stored `n:` key names and the change can reach (built once, no resolution).
   A key kind the table does not list counts as touched, so a later section that adds a kind gets a superset until it
   adds its row. The kinds sections 3.4, 4.2, 5.3 and 5.5 add before this section lands have their rows below.

   | Key | Touched when |
   | --- | --- |
   | `e:<id>` | the document that holds `<id>` (in either model) is written or deleted, and its `DependencyHash` differs (absent counts) |
   | `k:<kind>` | the change creates or deletes an element of that kind |
   | `r:<id>` | a written or deleted document references `<id>` in either model, and `ReferrersHash(<id>)` differs |
   | `n:<kind>:<form>` | the change writes or deletes a document that holds entries of `<kind>` or one their qualified forms read (a package, a database, a process, a reference type), and the ids the form finds differ |
   | `n:table:<text>` | the change reaches a database (decision 6) |
   | `l:<locale>:<owner>` | the change writes or deletes `<owner>`'s document or a shard holding its entries, and the owner's hash differs |
   | `s:<section>` | the change writes `maquettiste.json` (a delete's cascade or a schema operation edits it) and the section's hash differs |
   | `d:<database id>` | the change reaches the database (decision 6) |
   | `k:migration`, `r:<database id>` | the save records, or may record, migration facts in that database: the databases of the listed facts, or every reached database when `facts` is `null` |
   | `t:<pack>/<path>` | never: a batch writes no pack file |
   | `x:<path>`, `c:<type>` | the change writes that file under `extensions/` (3.4's extension files, 4.2's custom kind files) and its content hash differs |
   | `cv:<kind>` | never: calculation files (5.5) are scripts, which a batch does not write |
   | `ix:<index>:<value>`, `rt:<id>` | always: the rule view's keys (3.4 hands them to units that read scripted rows) count as touched |
   | `px:<pack>/<id>`, `px:<pack>` | a key the pack's exporting units recorded with their stored exports (5.3, `UnitState.Exports`) is touched |

4. **Units and their change.** Each listed unit has a `change`:
   - `inputs`: a stored read key is touched; its outputs are the paths its state records.
   - `deleted`: the element of its key is deleted; the next generate treats its outputs as orphans, under the rules it
     follows today.
   - `added`: a created element (or a created attribute, for `each attribute`) enters the unit's `each` scope. Its
     output paths are known only after rendering, so `outputs` is empty. `where` is not evaluated, so a unit with any
     `where` filter (`script` and `database` included) is listed with `filtered: true`; plan decides. Generation hints
     that skip the pack (`generation.<pack>.skip` or `generation.*.skip`) drop it. An element the change writes that
     both models hold (or an attribute it declares) is `added` too for every unit of its scope with no stored state at
     its key, since a `where` filter or a skip hint left it out and the change may let it in; it is `filtered` when the
     unit has a `where` or the element's hints skipped the pack before. A `where` that reads other elements stays
     plan's job.
   Selector units (`select ...`) and units of tables that only resolution makes are not listed for created elements:
   finding them needs a script run or the resolved candidate. The docs say so, and plan finds them.
5. **Packs.** Every enabled pack of the model before the change is listed in pack order with a `state`: `known` (unit
   states were read), `none` (the pack has no unit states: never generated here, or the cache was cleared) or `invalid`
   (the pack does not load). Units already stale from earlier edits are not listed; plan shows them.
6. **Databases.** `databases` lists every database the change can reach: `ChooseDatabases` over every written or
   deleted document, not only those that reshape (`Shapes` ignores `description` and the other annotations), since a
   description change reaches a database's comments. It is a superset chosen by ids alone, as for facts, with the
   footprints the fact recorder already reads (cached per snapshot). `facts` keeps its phase 1 meaning, including
   `null` when the change reaches every database; that rule stays (the dry run would otherwise capture whole
   databases). After the live planner (scope 6.1, phase 3), each `databases` entry gains the planner's steps with their
   grades (safe, needs a fact, refused) for a chosen environment; phase 2 reports the reached databases and the facts
   instead.
7. **Referrers.** The targets are the deleted elements and sub-elements, and the elements and sub-elements whose own
   fields changed (an object compared with its id-bearing child objects reduced to their ids, so a renamed attribute is
   a target and its entity is not). The referrers are each target's references on the model before the change
   (`ModelSnapshot.ReferencesTo`), minus those held by documents the batch writes or deletes: those are in `files`.
   Created elements have no referrers.
8. **Bounds and order.** At most 200 referrers (`BatchImpact.MaxReferrers`) with `referrersTotal`, ordered by target,
   referring element and pointer. At most 200 units per pack (`ImpactPack.MaxUnits`) with `unitsTotal`, ordered by key.
   A unit lists at most five touched keys, ordinal. Databases by name, then id. Every list is ordinal, so two dry runs
   of one batch on one model answer the same bytes.
9. **Where it is built.** The store fills `referrers` and `databases` on every dry run (both are cheap and need no
   pack). `GenerationService.PreviewBatchAsync` runs the same dry run once, through an internal store method that also
   hands back the two models and the changed ids, and fills `packs`, since packs and unit states belong to generation.
   MCP `apply_batch` and `model apply --dry-run` call the service. The assistant's proposals keep calling the store and
   read only its files; their answer does not change in phase 2.

#### Schema and files

No schema file changes. `BatchPreview` is not part of `docs/api/openapi.yaml` (only `BatchPreviewFile` is), so the
contract does not change.

#### The impact

```csharp
namespace Maquettiste.Engine;
public sealed record BatchPreview   // existing positional members and Ids, DiagnosticsAdded, DiagnosticsRemoved, Facts
{
    /// <summary>When saved, how far the change reaches beyond its own files; null otherwise.</summary>
    public BatchImpact? Impact { get; init; }
}

/// <param name="Referrers">At most MaxReferrers, by target, referring element and pointer.</param>
/// <param name="ReferrersTotal">How many there are.</param>
/// <param name="Databases">The databases the change can reach, by name then id.</param>
public sealed record BatchImpact(IReadOnlyList<ImpactReferrer> Referrers, int ReferrersTotal,
    IReadOnlyList<ImpactDatabase> Databases)
{
    public const int MaxReferrers = 200;
    /// <summary>Per enabled pack, in pack order; null when the dry run ran on the store alone.</summary>
    public IReadOnlyList<ImpactPack>? Packs { get; init; }
}

/// <param name="Target">The id the change touches.</param>
/// <param name="Change"><c>changed</c> or <c>deleted</c>.</param>
/// <param name="FromKind">The kind of the referring element.</param>
/// <param name="FromName">Its name, when it has one.</param>
public sealed record ImpactReferrer(string Target, string Change, string FromKind, string? FromName,
    ReferenceInfo Reference);

public sealed record ImpactDatabase(string Id, string Name);

/// <param name="State"><c>known</c>, <c>none</c> or <c>invalid</c>.</param>
/// <param name="Units">At most MaxUnits, by key.</param>
public sealed record ImpactPack(string Pack, string State, IReadOnlyList<ImpactUnit> Units, int UnitsTotal)
{
    public const int MaxUnits = 200;
}

/// <param name="Change"><c>inputs</c>, <c>deleted</c> or <c>added</c>.</param>
/// <param name="Keys">At most five touched read keys, ordinal; empty for added.</param>
/// <param name="Outputs">The output paths the unit's state records, ordinal; empty for added.</param>
public sealed record ImpactUnit(string Key, string Change, IReadOnlyList<string> Keys, IReadOnlyList<string> Outputs)
{
    /// <summary>An added unit whose where filter was not evaluated.</summary>
    public bool Filtered { get; init; }
}

public sealed partial class GenerationService
{
    /// <summary>A dry run of the batch (ModelStore.PreviewBatchAsync) with the impact's packs filled.</summary>
    public Task<BatchPreview> PreviewBatchAsync(ModelBatch batch, CancellationToken ct);
}
```

The five new records live in `BatchImpact.cs` (new), beside `ModelStore.cs`, so the store file changes only where
`BatchPreview` gains `Impact`. `Impact` is written only when it is set, in the engine and in the CLI's answer.

Internal pieces:

- `BatchPreviewSink` gains the model before, the candidate and the keys of `plan.ChangeByElement`; the preview branch of
  `ExecuteCoreAsync` sets them and fills `Impact` with `referrers` and `databases`.
- `ModelStore.PreviewBatchCoreAsync(ModelBatch, CancellationToken)` (internal): the dry run of `PreviewBatchAsync`,
  answering the preview with the sink's two models and changed ids (null models when it would not save), so the service
  runs one dry run, not two.
- `Loading/ImpactTargets.cs` (new): the targets of decision 7, found by walking the before and after documents of each
  written or deleted element and pairing objects by `id`; then the referrers.
- `ModelStore.ReachedDatabases(before, candidate, changedIds)` (internal, `ModelStore.Migrations.cs`): `ChooseDatabases`
  over every changed id with the same footprint function, without the `Shapes` filter.
- `Planning/ImpactKeys.cs` (new): the touched-key test of decision 3. It compares hashes only for keys the change's
  documents can reach (`DependencyHash`, `ReferrersHash`, `KindSetHash`, the name index, `Localization.OwnerHash`) and
  remembers each answer, so its cost follows the change and the distinct stored keys, not the model.
  `DependencyHasher.SettingsHashOf(ProjectSettings, string)` (internal static) is the settings switch of
  `DependencyHasher.SettingsHash`, extracted so both models can be hashed without a resolved model.
- `Generation/OutputImpact.cs` (new): loads the enabled packs (`PackLoader`, model before), reads each pack's states
  with `UnitStateStore.LoadAsync` (which returns the decoded states it remembers when the bytes match), tests each
  distinct read key once (memoized; one test per entry of the state file's key table, `UnitStateStore.TableKeys.Table`,
  when the states come from the file), then makes one pass over the states' keys, and adds the `added` units from the
  created ids and the units' `for` scopes (`UnitPlanner` scope nouns, `Planning/UnitPlanner.cs:351-378`). It takes no
  run lock and writes no file.
- `GenerationService.Impact.cs` (new): `PreviewBatchAsync`, a partial of the service, so the busy
  `GenerationService.cs` does not change.

#### CLI and MCP

- MCP `apply_batch` with `dryRun` answers `impact: { referrers: [{ target, change, fromKind, fromName, reference }],
  referrersTotal, databases: [{ id, name }], packs: [{ pack, state, units: [{ key, change, keys, outputs, filtered }],
  unitsTotal }] }` beside today's members, through `BatchOutput.Preview` (its `BatchPreviewAnswer` gains `impact`,
  left out when the dry run would not save; `filtered` is left out when false). The tool description gains one
  sentence; the server instructions (`src/Maquettiste.Cli/Mcp/McpServerSetup.cs`) say to read `impact` before applying.
- `maquettiste model apply <batch.json> --dry-run` runs through `GenerationService.PreviewBatchAsync`. Text output
  adds, after the diagnostic lines, an `impact` block: one line per referrer
  (`referrer <kind> <name or id> <pointer> -> <target> (<change>)`), one `database <name>` line per database, then per
  pack `<pack>: <n> units` and one line per unit (`<change> <key> <outputs>`), or
  `<pack>: no unit states; generate once to list generated files`. `--format json` answers the MCP shape. Exit codes do
  not change.

#### Templates

None.

#### Editor follow-ups

The assistant's proposal card shows the referrers and the generated files per pack; a "What this touches" panel before
applying a batch in the editor.

#### Docs

`docs/mcp.md`: the `apply_batch` row and an "Impact" paragraph under "Batches by name" (the touched-key table, the
three unit changes, the bounds, why no bytes). `docs/user-guide.md`: `model apply --dry-run`.
`skills/maquettiste-modeling/SKILL.md`: dry-run, read `impact.referrers` and `impact.packs`, then apply.
`docs/guides/work-with-an-agent.md`, step 4: the dry run's impact on the guide's showcase model. `docs/releases.md`
under `## 0.12.0 (unreleased)`.

**Not doing**

- Rendering the touched units or diffing generated files in a dry run: it needs the whole candidate resolved
  (decision 2). Apply, then `plan` with `elements` or `units`.
- Facts for a dry run whose change reaches every database: the phase 1 rule stays.
- Database grades: phase 3, with the live planner.
- Units that selectors, or tables only resolution makes, would add for created elements: plan finds them.
- An impact on `save_element` and `patch_element`, which have no dry run. The editor's HTTP API has no batch dry run and
  gains none.

**Tests**

Unit tests:

- `Planning/ImpactKeysTests` (new): `An_element_key_is_touched_when_its_document_is_written_or_deleted`,
  `A_kind_set_key_is_touched_by_a_create_or_a_delete_only`,
  `A_referrers_key_is_touched_when_a_written_document_starts_or_stops_referencing`,
  `A_name_key_is_touched_when_the_ids_its_form_finds_change`,
  `A_name_key_is_touched_when_a_package_rename_changes_the_ids_its_form_finds`,
  `An_element_key_is_not_touched_when_its_dependency_hash_is_unchanged`,
  `A_schema_diff_key_and_a_table_name_key_are_touched_when_the_change_reaches_the_database`,
  `A_settings_key_is_touched_only_when_its_section_changes`, `Template_keys_are_never_touched`,
  `An_extension_file_key_is_touched_only_when_the_batch_writes_its_file`, `Calculated_value_keys_are_never_touched`,
  `Rule_view_keys_count_as_touched`,
  `The_facts_a_save_may_record_touch_the_migration_kind_set_and_the_databases_referrers`,
  `An_unknown_key_kind_counts_as_touched`.
- `Store/BatchImpactTests` (new, on `BillingStore`): `Referrers_of_a_renamed_attribute_exclude_the_batch_documents`,
  `Referrers_of_a_deleted_element_say_deleted`, `An_entity_whose_own_fields_did_not_change_is_not_a_target`,
  `Databases_list_what_the_change_reaches_when_facts_are_null`, `A_description_change_reaches_its_database`,
  `An_invalid_dry_run_has_no_impact`, `The_store_alone_leaves_packs_null`, `Referrers_are_capped_with_a_total`.
- `Generation/OutputImpactTests` (new, on `EditorRepo`):
  `Units_whose_stored_keys_hold_a_touched_key_are_listed_with_their_outputs`,
  `Deleting_an_element_lists_its_units_as_deleted`,
  `A_created_entity_lists_the_each_entity_units_as_added_without_outputs`,
  `A_unit_with_a_where_filter_is_added_as_filtered`, `A_unit_with_a_script_where_is_added_as_filtered_too`,
  `A_skip_hint_drops_an_added_unit`, `A_pack_without_unit_states_is_state_none`,
  `Units_are_capped_per_pack_with_a_total`, `Two_dry_runs_answer_the_same_bytes`,
  `An_exports_key_is_touched_through_the_stored_export_keys`.
- CLI `ModelApplyTests.Model_apply_dry_run_prints_the_impact`; MCP
  `McpWriteTests.Apply_batch_dry_run_answers_the_impact`.

Acceptance (the scope's "Done when"), `Generation/ImpactBeforeApplyTests` (new):

- `The_dry_run_of_renaming_Invoice_number_lists_what_a_plan_renders_after_apply`. Setup: `EditorRepo` (a billing copy
  with the csharp-dapper and sql-ddl packs), generated once as `ScopedGenerationTests` settles it. The batch updates
  `@entity:Billing.Invoice`, renaming attribute `01J92P0V0R3VSP7D5238DTNZX1` from `number` to `code` and keeping its id.
  Expected from `GenerationService.PreviewBatchAsync`: outcome saved; `diagnosticsAdded` empty; `facts` null (one
  database); `impact.databases` is `main` (`01J92P0V1QRN2181XM2ZWE02W4`); `impact.referrers` are the table overlay
  `01J92P0V1T0J6RH4MY9H81NYB4` and the query `InvoicesByCustomer` (`01K6QRY0000000000000000001`), each with target
  `01J92P0V0R3VSP7D5238DTNZX1` and change `changed`, and no relation that references Invoice; both packs are `known`.
  Then the batch is applied and a full `plan` runs: every unit the plan renders is among the impact's `inputs` units,
  every `inputs` unit the plan skips was listed only through a reach-based key (`d:`, `n:table:` or the facts row of
  decision 3), and every path the plan changes is among the listed outputs.
- `The_dry_run_resolves_plans_and_renders_nothing`. Same setup, with the store and the service built over counting
  substitutes for `EngineServices.Resolver`, `Planner` and `RendererFactory`, as `PreviewBoundsTests` builds them:
  during the dry run `ResolveAsync`, `PlanAsync` and any render are called zero times (the rename reaches the one
  database, so the dry run records no facts and resolves nothing for them); `EditorRepo.Files()` and the unit state
  files under the cache folder have the same bytes before and after.

**Tasks**

- **G1. Impact records, referrers and reached databases.** Owns: `BatchImpact.cs` (new: the five records),
  `ModelStore.cs` (`BatchPreview.Impact`, the sink's members, the preview branch, `PreviewBatchCoreAsync`),
  `Loading/ImpactTargets.cs` (new), `ModelStore.Migrations.cs` (`ReachedDatabases`). Tests: `BatchImpactTests`. Verify:
  `E "~BatchImpactTests|~BatchPreviewTests|~FactRecordingTests|~AgentToolsTests"`.
- **G2. Touched keys.** Owns: `Planning/ImpactKeys.cs` (new), `Planning/DependencyHasher.cs` (`SettingsHashOf`
  extracted, `SettingsHash` calls it). Tests: `ImpactKeysTests`. Verify:
  `E "~ImpactKeysTests|~HashingAndStateTests|~IncrementalTests"`.
- **G3. Units per pack and the service's dry run.** Runs after G1 and G2. Owns: `Generation/OutputImpact.cs` (new),
  `GenerationService.Impact.cs` (new: `PreviewBatchAsync`). Tests: `OutputImpactTests`, `ImpactBeforeApplyTests`.
  Verify: `E "~OutputImpactTests|~ImpactBeforeApplyTests|~ScopedGenerationTests|~IncrementalTests|~PreviewBoundsTests"`.
- **G4. MCP and CLI.** Runs after G3. Owns: `src/Maquettiste.Cli/Mcp/ModelTools.cs` (`apply_batch`),
  `src/Maquettiste.Cli/Mcp/McpServerSetup.cs`, `src/Maquettiste.Cli/Commands/BatchOutput.cs`,
  `src/Maquettiste.Cli/Commands/ModelCommand.cs` (the dry run through the service), `src/Maquettiste.Cli/CliApp.cs`
  (usage), the `apply_batch` row and "Impact" paragraph of `docs/mcp.md`, the `model apply` row of
  `docs/user-guide.md`. Tests: `ModelApplyTests.Model_apply_dry_run_prints_the_impact`,
  `McpWriteTests.Apply_batch_dry_run_answers_the_impact`. Verify:
  `C "~ModelApplyTests|~McpWriteTests|~McpSurfaceTests|~ParserAndGlobalsTests"`.
- **G5. Skill, guide and release note.** Runs after G4. Owns: `skills/maquettiste-modeling/SKILL.md` (the dry-run step),
  `docs/guides/work-with-an-agent.md` (step 4), `docs/releases.md` (the 6.1 lines). Tests: none new. Verify:
  `C "~SkillRefreshTests|~AgentSetupTests"`.

G6 to G9 are unused.

### 6.2 A model diff between two models

**Why every team**

Every team reviews model changes, and the older copy lives in different places: in CI it is a checkout of the base
in a folder, in a hosted model it is the snapshot taken at the last deploy, and for an agent it is the snapshot it took
before it started. Reviewers read a change by package and by member (this attribute was renamed, that column's type
changed), not by file. A markdown form pastes into any review comment or change log, and an agent reads the same
comparison through MCP.

**Today**

- `SnapshotLibrary.CompareAsync(from, to, offset, limit, ct)` (`Snapshots/SnapshotLibrary.Compare.cs:31`) compares two
  sides, each a snapshot id or `working` (`SideAsync`, `:242`; `WorkingSideAsync`, `:261`). A snapshot side reads only
  the archive's index; the working side reads the store's documents and hashes. Elements match by id and differ by hash;
  other documents match by path. The list is ordered by kind, name and id (`:62`) and paged.
- `CompareElementAsync` (`:101`) returns one element's two documents and its changed fields as JSON pointers; arrays of
  objects with ids match by id (`Diff`, `:137`). A field change says nothing about the sub-element it belongs to.
- The records: `SnapshotKindChanges` (`Snapshots/SnapshotRecords.cs:45`), `SnapshotElementChange` (`:55`),
  `SnapshotComparison` (`:81`), `SnapshotFieldChange` (`:90`), `SnapshotElementDiff` (`:108`). No record names a
  package.
- MCP `compare_snapshots` is a catalog tool shared with the editor's assistant (`Agent/AgentTools.Snapshots.cs:11-31`,
  `Agent/tools.json:4`): `from`, `to` (snapshot ids or `working`), `id`, `offset`, `limit`.
- CLI `snapshot compare <from> [<to>]` (`src/Maquettiste.Cli/CliApp.cs:239-241`,
  `src/Maquettiste.Cli/Commands/SnapshotCommand.cs:159`); HTTP `GET /api/snapshots/compare`
  (`docs/api/openapi.yaml:5724`). Neither takes a folder.
- A folder can already serve as a baseline: `ModelBaseline.OpenAsync` resolves a value to a snapshot or a folder and
  opens a read-only store (`Snapshots/ModelBaseline.cs:90-148`); only `maquettiste` and its MCP server allow folders
  (`AgentToolsOptions.AllowFolderBaselines`, `Agent/AgentTools.cs:43`; used by `validate`,
  `Agent/AgentTools.Handlers.cs:443`).
- The kind of a sub-element is found from the record type a JSON path reaches (`Loading/IdMinter.cs:95-103`); a
  sub-element's name is read at its pointer, the first of `name`, `role`, `code`, `key` and `label` that holds text
  (`SubName`, `Loading/ChangePlanner.Cascade.cs:895-906`).
- Opening a folder baseline hashes its files and loads a read-only store over them (`OpenFolderAsync`,
  `Snapshots/ModelBaseline.cs:196-218`); it validates and resolves nothing. A snapshot id is a kebab slug of the
  snapshot's name with a time stamp (`NewId`, `Snapshots/SnapshotLibrary.cs:388-401`).

**Decisions taken here**

1. **Sides.** Each side is `working`, a model snapshot id or a folder. A value resolves in one order: `working` first,
   then phase 1's baseline order (path-like means a folder; else a snapshot id when the archive exists; else a folder
   when the directory exists; else an error naming the value). Folders are allowed only in `maquettiste` and
   `maquettiste mcp`; the editor's host and its assistant answer `bad-request`, as for baselines.
2. **Reading a side.** A snapshot side keeps reading its index; the working side reads its store; a folder side opens
   through `ModelBaseline` (a read-only store, no index cache, nothing written) and is read like the working side.
   A folder side compares the model only, as a baseline loads without packs. Nothing is validated or resolved. A
   snapshot or the working model costs a hash comparison plus a read of the documents that differ; a folder has no
   index, so opening it reads and parses its files once, as `validate --against` does.
3. **Grouped by package.** With `ByPackage`, each element row carries the package its document states (`package`; a
   package's own row carries its `parent`), from the `to` side, or the `from` side for a removed element. A group's
   label is the package's qualified name on that side, built from that side's package documents; a package the side
   does not hold labels its group with its id. Migration facts have no package: they form one group per database
   (`Database` on the row, from the fact's `database`). Groups come in this order: packages by qualified name
   (ordinal), then "(no package)", then the facts of each database by database name, then id. Inside a group: kind,
   name, id. The order needs every changed element's package, so those documents are read before the page is cut;
   field changes are computed only for the page's elements.
4. **Changes in model terms.** With `FieldChanges`, each changed element row carries its field changes in `fields`,
   at most 50 (`SnapshotCompareOptions.MaxElementFields`), with `fieldsTruncated`. Every field change, in a list or in
   the one-element detail, names the innermost sub-element with an `id` on its pointer: `subId`, `subKind` (from the
   record type, as `IdMinter` finds it; `sub-element` when unknown), `subName` (as `SubName` reads it, on the `to` side,
   else the `from` side) and `subPointer` (the pointer inside it). A renamed attribute reads as attribute `code`
   (`01J92P0V0R3VSP7D5238DTNZX1`), `/name` from `"number"` to `"code"`. This is the object 6.1 calls a target
   (its decision 7): in both, a change belongs to the innermost object with an id, never to its element.
5. **Grades.** The scope's "graded as in 7.2" (section 6.1 here) waits for the live planner (scope 6.1, phase 3),
   as 6.1's grades do (its decision 6). In phase 2 the diff shows the migration facts one side holds and the other
   lacks, per database. A recorded fact has no name, so with `ByPackage` its row carries a `label`: its `fact`, then
   its object's kind and names as the fact records them (`rename column billing.invoices.number`), or the fact alone
   when it has no object. The docs point to `validate --against <a>` for the changes that need a fact and have none.
   Phase 3 adds each database's steps and grades to the same group.
6. **One renderer.** `ModelDiffFormat.Markdown` and `ModelDiffFormat.Text` in the engine render a comparison made with
   `ByPackage` and `FieldChanges`, so the CLI and MCP print the same bytes. Markdown: a heading naming both sides, one
   counts line, a `##` section per group, one bullet per element (change, kind, name, id in code spans) with one nested
   bullet per field change (at most 20, then `and <n> more fields`), values as compact JSON cut at 80 characters with
   `...`, then "Other files". A code span that holds a backtick is wrapped in double backticks. Text is the same lines
   without markdown marks.
7. **Defaults keep today's answers.** Without options, `CompareAsync` answers exactly as today: kind order, no
   `package`, `database`, `fields` or `packages`, snapshot and `working` sides only. `CompareElementAsync` adds the four
   `sub*` members to each field change, which are additive. So `GET /api/snapshots/compare` and `snapshot compare`
   keep their bytes apart from those members.
8. **`model diff` beside `snapshot compare`.** `snapshot compare` stays the document view (counts per kind, paths,
   renames and moves, snapshot and `working` sides) with its output unchanged; `model diff` is the review form. Exit
   codes: 0 when the two sides compared (with or without differences), 4 when a side resolves to nothing, names a
   folder that is not a project, or holds a newer model format.
9. **Determinism.** The same two sides give byte-identical output in every format.

#### Schema and files

No file under `schemas/v1/` changes. `docs/api/openapi.yaml` adds the optional members below to `SnapshotComparison`,
`SnapshotElementChange` and `SnapshotFieldChange` and a `SnapshotPackageChanges` schema, written only when set; the
editor's generated types follow (`npm run gen`).

#### The comparison

```csharp
namespace Maquettiste.Engine;
public sealed record SnapshotCompareOptions
{
    public const int MaxElementFields = 50;
    public bool ByPackage { get; init; }          // group by package, facts by database; fill Package, Packages
    public bool FieldChanges { get; init; }       // fill each changed element's Fields
    public bool AllowFolders { get; init; }       // a side may be a folder (maquettiste and its MCP server only)
    public string? CurrentDirectory { get; init; } // where a relative folder resolves; required with AllowFolders
}
public sealed partial class SnapshotLibrary
{
    /// <exception cref="ArgumentException">With options: a side resolves to nothing, names a folder where folders are
    /// not allowed or that is not a project, or holds a newer model format (MQ1007's text).</exception>
    public Task<SnapshotComparison?> CompareAsync(string from, string to, int offset, int limit,
        SnapshotCompareOptions? options, CancellationToken ct);
    public Task<SnapshotElementDiff?> CompareElementAsync(string from, string to, string id,
        SnapshotCompareOptions? options, CancellationToken ct);
}
public sealed record SnapshotComparison    // existing positional members
{
    public IReadOnlyList<SnapshotPackageChanges>? Packages { get; init; }   // with ByPackage, in group order
}
/// <param name="Id">The package id, or null for "(no package)" and for a database's facts.</param>
/// <param name="Name">The qualified name, "(no package)", or "migrations of <database name>".</param>
public sealed record SnapshotPackageChanges(string? Id, string Name, int Added, int Removed, int Changed);
public sealed record SnapshotElementChange // existing positional members
{
    public string? Package { get; init; }                           // with ByPackage
    public string? Database { get; init; }                          // with ByPackage, migration facts only
    public string? Label { get; init; }                             // with ByPackage, a migration fact without a name
    public IReadOnlyList<SnapshotFieldChange>? Fields { get; init; } // with FieldChanges, changed elements only
    public bool FieldsTruncated { get; init; }
}
public sealed record SnapshotFieldChange   // existing positional members
{
    public string? SubId { get; init; }
    public string? SubKind { get; init; }
    public string? SubName { get; init; }
    public string? SubPointer { get; init; }
}
public static class ModelDiffFormat
{
    /// <exception cref="ArgumentException">The comparison was not made with ByPackage and FieldChanges.</exception>
    public static string Markdown(SnapshotComparison comparison);
    /// <exception cref="ArgumentException">The comparison was not made with ByPackage and FieldChanges.</exception>
    public static string Text(SnapshotComparison comparison);
}
```

Every new member is left out of the JSON when null or false (`JsonIgnore` `WhenWritingNull` or `WhenWritingDefault`),
so an answer made without options keeps today's bytes.

Internal pieces: the side reader in `Snapshots/SnapshotLibrary.Compare.cs` gains the folder side (one `ModelBaseline`
per folder for the call, disposed at its end, through the store of the baseline instead of the library's) and reads
every document it needs from a snapshot through one open archive (today `ReadEntry` opens the archive once per
document); `Snapshots/FieldOwners.cs` (new) attributes a pointer to its sub-element, sharing the record-type walk with
`IdMinter`.

#### CLI and MCP

- `maquettiste model diff <a> [<b>] [--format text|json|markdown] [--element <id>]`, `<b>` defaulting to `working`.
  It compares with `ByPackage`, `FieldChanges` and `AllowFolders` (the current directory) in pages of `MaxCompareLimit`
  (one call for most reviews; each further page opens a folder side again), joins the pages, and prints
  `ModelDiffFormat.Text` or `Markdown`, or the joined comparison as JSON. `--element <id>` prints one element's field
  changes (`CompareElementAsync`). `diff` joins the `model` verbs (`src/Maquettiste.Cli/Commands/ModelCommand.cs`)
  and `CliApp.Usage`. `--format` and `--element` are value options already; `markdown` is a new choice of `--format`
  for this verb only.
- MCP `compare_snapshots`: `from` and `to` may be folders when the host allows them; the answer is always grouped by
  package (`ByPackage`); new arguments `fieldChanges` (boolean, default false; with it `limit` defaults to 50, so one
  page of field changes fits an agent's context) and `format` (`json`, the default, or `markdown`, which answers
  `{ markdown, next }` for the page). `id` answers the one-element detail as today, with the `sub*` members. The tool
  description and the `docs/mcp.md` row change. No tool is added, so no pin moves.

#### Editor follow-ups

The snapshot compare view groups by package and shows each element's field lines with their sub-element; a "copy as
markdown" action. The HTTP endpoint would take `byPackage` and `fieldChanges` then.

#### Docs

`docs/user-guide.md`: `model diff` in the command table. `docs/guides/schema-changes.md`: a part "Review a change"
(a snapshot per deploy, a folder in CI, a snapshot before an agent starts; the markdown in a review comment; facts in
their own group; `validate --against` for missing facts). `docs/engineering/snapshots.md`: folder sides and the options.
`docs/mcp.md`: the `compare_snapshots` row. `skills/maquettiste-modeling/SKILL.md`: compare with the snapshot taken
before starting, then summarize by package. `docs/releases.md` under `## 0.12.0 (unreleased)`.

**Not doing**

- Grades: phase 3, with the live planner.
- Classifying the difference into facts inside the diff: `validate --against` does that and resolves both models.
- Pack files on a folder side.
- A package filter, an `--out` file and posting to a review service: the markdown is printed for the team's own tools.
- A diff of resolved models: reviewers read documents, and resolving two large models is whole-model work twice.
- Grouping the HTTP answer: an editor follow-up.

**Tests**

Unit tests:

- `Snapshots/ModelDiffTests` (new, temporary billing copies): `A_folder_side_compares_like_the_snapshot_it_copies`,
  `Two_folders_compare_without_the_working_model`, `Elements_group_by_package_and_a_package_by_its_parent`,
  `Field_changes_name_their_sub_element_and_the_pointer_inside_it`, `Migration_facts_group_by_their_database`,
  `A_fact_row_carries_its_label`, `A_package_the_side_lacks_labels_its_group_with_its_id`,
  `Paging_follows_the_group_order`,
  `A_folder_side_is_refused_unless_folders_are_allowed`, `An_unknown_side_names_itself_in_the_error`,
  `A_folder_that_is_not_a_project_is_refused`, `Without_options_the_comparison_answers_as_before`,
  `Fields_are_capped_per_element`, `The_comparison_is_byte_identical_over_two_runs`,
  `A_folder_side_writes_nothing_into_its_folder_or_the_cache`.
- `Snapshots/ModelDiffFormatTests` (new): `Markdown_lists_groups_elements_fields_facts_and_files_in_order`,
  `Long_values_and_many_fields_are_cut`, `Backticks_in_names_stay_inside_code_spans`,
  `Text_and_markdown_carry_the_same_counts`.
- `Editor/AgentToolsTests.Compare_snapshots_groups_by_package_and_answers_markdown`.
- CLI `ModelCommandTests`: `Model_diff_prints_markdown_grouped_by_package`, `Model_diff_takes_a_folder_and_a_snapshot`,
  `Model_diff_element_prints_one_elements_fields`, `Model_diff_exits_4_for_a_side_that_names_nothing`. MCP
  `McpReadTests.Compare_snapshots_takes_a_folder_and_answers_by_package`.
- Functions `AssistTests.Compare_snapshots_with_a_folder_through_the_assistant_answers_bad_request`;
  `SnapshotEndpointTests.Compare_answers_as_before_and_field_changes_name_their_sub_element`.

Acceptance (the scope gives no "Done when"; these two lines turn its shape into billing terms):

- `Integration/ModelReviewTests.A_review_of_a_rename_and_a_new_table_reads_by_package` (new, engine). Setup: a billing
  copy; a snapshot named `before-review` taken (its id is `before-review-<time>`); the project copied into a folder
  `base` (a CI checkout stands in). Then in the working copy one batch renames attribute `01J92P0V0R3VSP7D5238DTNZX1`
  of `Invoice` from `number` to `code`, and creates table `payment_method` in database `main` with `package` Billing
  (`01J92P0V01KDRN8GX5PGYCNKSX`). Expected `ModelDiffFormat.Markdown` of the comparison from the snapshot's id to
  `working` with `ByPackage` and `FieldChanges`: a section `Billing` holding `changed entity Invoice` with the nested
  line for attribute `code`, `/name` from `"number"` to `"code"`, and `added table payment_method`; a section for the
  migrations of `main` holding the added fact labelled `rename column billing.invoices.number`; counts 2 added (the
  table and the fact), 1 changed. The comparison from folder `base` (`AllowFolders`) renders the same lines after the
  heading.
- `McpReadTests.Compare_snapshots_answers_the_markdown_model_diff_prints` (new, CLI). Same setup on `CliRepo.Billing()`:
  `compare_snapshots` with `from` the snapshot's id, `fieldChanges` true and `format` `markdown` answers, byte for
  byte, what `maquettiste model diff <snapshot id> --format markdown` prints (one page: the diff holds three elements).

**Tasks**

- **G10. Sides.** Owns: `Snapshots/SnapshotLibrary.Compare.cs` (side resolution, the folder side, one open archive),
  `Snapshots/SnapshotRecords.cs` (`SnapshotCompareOptions`), `Snapshots/ModelBaseline.cs` (the value order shared,
  internal). Tests: the side cases of `Snapshots/ModelDiffTests`. Verify:
  `E "~ModelDiffTests|~SnapshotTests|~ModelBaselineTests|~NoVersionControlTests"`.
- **G11. Packages and field changes.** Runs after G10. Owns: `Snapshots/SnapshotLibrary.Compare.cs` (grouping,
  `Packages`, `Fields`), `Snapshots/SnapshotRecords.cs` (the new members and `SnapshotPackageChanges`),
  `Snapshots/FieldOwners.cs` (new), `Loading/IdMinter.cs` (the record-type walk shared). Tests: the rest of
  `Snapshots/ModelDiffTests`. Verify: `E "~ModelDiffTests|~SnapshotTests|~IdMintingTests"`; `F "~SnapshotEndpointTests"`
  (the HTTP detail answer gains the `sub*` members; its schema allows them until G15 documents them).
- **G12. Formats.** Runs after G11. Owns: `Snapshots/ModelDiffFormat.cs` (new). Tests: `ModelDiffFormatTests`. Verify:
  `E "~ModelDiffFormatTests"`.
- **G13. `model diff`.** Runs after G12 and G4. Owns: `src/Maquettiste.Cli/Commands/ModelCommand.cs` (the `diff` verb),
  `src/Maquettiste.Cli/CliApp.cs` (usage), the `model diff` row of `docs/user-guide.md`. Tests: the four
  `ModelCommandTests` cases. Verify: `C "~ModelCommandTests|~SnapshotCommandTests|~ParserAndGlobalsTests"`.
- **G14. MCP `compare_snapshots`.** Runs after G12 and G4. Owns: `Agent/AgentTools.Snapshots.cs`, `Agent/tools.json`
  (the tool's description and arguments), the `compare_snapshots` row of `docs/mcp.md`. Tests:
  `AgentToolsTests.Compare_snapshots_groups_by_package_and_answers_markdown`,
  `McpReadTests.Compare_snapshots_takes_a_folder_and_answers_by_package`, the `AssistTests` case. Verify:
  `E "~AgentToolsTests"`; `C "~McpReadTests|~McpSurfaceTests"`; `F "~AssistTests"`.
- **G15. Contract, acceptance and docs.** Runs after G13, G14, G5 and G20 (G20 also regenerates the editor's types).
  Owns: `docs/api/openapi.yaml` (the optional members and `SnapshotPackageChanges`), the generated
  `src/editor/src/api/schema.d.ts` and `src/editor/src/mocks/openapi.json`,
  `tests/Maquettiste.Engine.Tests/Integration/ModelReviewTests.cs` (new), the acceptance case in
  `tests/Maquettiste.Cli.Tests/McpServerTests.cs`, `docs/guides/schema-changes.md`, `docs/engineering/snapshots.md`,
  `skills/maquettiste-modeling/SKILL.md` (the review step), `docs/releases.md` (the 6.2 lines). Tests:
  `Integration/ModelReviewTests`, `McpReadTests.Compare_snapshots_answers_the_markdown_model_diff_prints`,
  `SnapshotEndpointTests.Compare_answers_as_before_and_field_changes_name_their_sub_element`. Verify: `editor`;
  `F "~ContractTests|~SnapshotEndpointTests"`; `E "~ModelReviewTests"`; `C "~McpReadTests|~SkillRefreshTests"`.

G16 to G19 are unused.

### 6.3 Declared environments

**Why every team**

Most teams deploy one model to several databases: a developer's own, a shared test database, staging, production.
They want some rows and some SQL objects in only some of them: demo rows in dev, a test helper in test. Today the
environment names are free text on each seed and SQL object, so a typo, or a name the pack was not told about, sends
rows to no script without a word, and the seeds of entities mapped to a table ignore their environments altogether.
A project that declares its names once gets every use checked, and packs write one script per declared environment.
Declaring is opt-in: many projects already use such names, and a project that declares none keeps free names.

**Today**

- Seeds and SQL objects take `environments`, free names where empty means every environment
  (`schemas/v1/seed.json:108-116`, `schemas/v1/sql-object.json:101-109`; `Seed.Environments`,
  `Model/ReferenceData.cs:124`; `SqlObject.Environments`, `Model/Physical.cs:1120`). Nothing lists the names, and no
  rule checks them.
- Generation passes every seed row with its seed's environments (`RSeedRow.Environments`,
  `Resolution/ResolvedPhysical.cs:1077`) and every SQL object with its own (`RSqlObject.Environments`, `:1018`); packs
  choose. How rows reach scripts today:
  - sql-ddl writes the rows of table seeds, of seeds of entities bound to a table and of reference types bound to a
    table into `seed.sql` when their seed has no environments, and into `seed.<env>.sql` when it names `<env>`
    (`packs/sql-ddl/_data.scriban:11-19`), but only for the names in its `environments` parameter
    (`packs/sql-ddl/seed-environments.scriban:11`), whose default is empty (`packs/sql-ddl/pack.json:8`). Rows of a
    seed whose environment the parameter does not list are written nowhere. `objects.<env>.sql` follows the same
    parameter (`packs/sql-ddl/object-environments.scriban:12`). Both file names take the kebab form of the name
    (`seed-environments.scriban:26`, `object-environments.scriban:47`), so `staging_eu` and `staging-eu` would write
    one file.
  - The seeds of entities and relations mapped to a table are written into `seed.sql` whatever their environments
    (`seed_statements` and `seed_sql`, `packs/sql-ddl/_reference.scriban:714-721`, `:573`; called from
    `packs/sql-ddl/seed.scriban:15`).
  - A reference type's seed rows are the type's codes in every environment
    (`Resolution/ResolveRun.ReferenceData.cs:185`, `:197-212`), since checks, native types and lookup tables cannot vary
    by environment.
  - seed-data writes each seed's environments into its manifest (`packs/seed-data/data.scriban:60`); csharp-dapper's
    loader applies the rows of every environment plus those of the name it is given at run time
    (`packs/csharp-dapper/seed-loader.scriban:97-98`).
- MQ4069 (`Validation/DatabaseObjectRules.cs:190-230`), MQ7111 and MQ7112 (`Validation/TableSeedRules.cs:246-247`,
  `:322-331`) compare environment lists, with empty meaning every environment.
- An adopt fact names an environment as history (`schemas/v1/migration.json:214-217`).
- Settings-level rules run in whole-model validation only (`Validation/ModelValidator.cs:82-87`), and a settings save
  validates the whole model before and after, refusing only the errors it introduces (`ModelStore.cs:648-661`).
- Declared locales are the precedent for a settings list templates read: `ResolvedModel.Locales`
  (`Resolution/ResolvedCore.cs:259-261`), `RLocale` with its settings key `s:localization` (`:306-329`), hashed in
  `DependencyHasher.SettingsHash` (`Planning/DependencyHasher.cs:256-283`).
- A project value for a parameter a pack does not declare is MQ6024, a warning (`Planning/UnitRules.cs:134`).

**Decisions taken here**

1. **The list.** `environments` in `maquettiste.json` is an optional map from name to settings, like `databases` and
   `packs`. The names are the project's, written as kebab keys (`common.json#/$defs/key`, at most 64 characters:
   `dev`, `staging`, `eu-west`). Packs put the names in file names, and sql-ddl writes their kebab form (Today); a
   kebab key is its own kebab form and has no capitals, so two declared names can never give one file name, on any
   file system, and no rule is needed for it. Seeds and SQL objects keep their item pattern (`tagLabel`), so a project
   that declares nothing names environments as freely as today. Once it declares one, a name there that is not
   declared is reported (decision 3), so the names in use are kebab keys too. In phase 2 an entry holds only
   `description`, the one field something reads now (templates through `model.environments`; the editor's settings
   panel later).
2. **What waits for phase 3.** The name of the environment variable that holds a connection string, a dialect version,
   `db plan --env`, `db apply --env`, `db adopt --env`, the view of the facts each environment has applied, and squash's
   check against declared environments' ledgers all read a database, which nothing in phase 2 does (decision 4 of
   section 1). Phase 3 adds them to the entry with the live planner; the live reader reads the server's version, so a
   declared version may not be needed then.
3. **Rules, once a project declares.** While `environments` is absent or empty, names stay free as today: MQ7131 and
   MQ4094 do not fire, a name need not be a kebab key, and packs write a script per name in use (decision 8). Failing
   every name of a project that declares none would break every such project on upgrade. Once the project declares at
   least one environment, a seed that names an undeclared environment is MQ7131 and a SQL object, MQ4094. Both are
   errors: packs then write scripts only for declared names, so an undeclared name means rows or statements that no
   script holds. A team that wants them softer sets their severity in `validation.rules`. Seeds and SQL objects of
   every environment (empty lists) need no declaration. MQ4095 is not used (decision 1) and stays reserved.
4. **Where the rules run.** MQ7131 and MQ4094 are element rules of the seed and the SQL object, so a save that adds a
   typo is refused at once. A settings save runs the whole-model check (`ModelStore.cs:648-661`), which refuses only
   the errors the save introduces: dropping a name still in use is refused while another name stays declared;
   declaring the first environments is refused unless the list covers every name in use; removing the last entry
   returns the project to free names, so nothing is refused then. The rules live in one new file,
   `Validation/EnvironmentRules.cs`, with one call at the start of `ReferenceDataRules.CheckSeed` (before its
   table-seed branch returns), one from `DatabaseObjectRules.CheckSqlObject` and one from the end-cell case of
   `ReferenceDataRules.CellProblem` (`Validation/ReferenceDataRules.cs:449`, `:478-493`; decision 11). The two name
   checks return at once when the settings declare no environment; the end-cell check runs either way. No
   settings-level rule is added.
5. **History is not checked.** An adopt fact's `environment` records where it was adopted; a later removal from the
   list must not fail old facts.
6. **"Every environment" keeps its meaning.** MQ7111, MQ7112 and MQ4069 do not read an empty list as the declared set.
   Rows and objects of every environment go to `seed.sql`, `schema.sql` and the migrations, which run before the
   environment scripts, so a row or object there cannot rely on one that only environment scripts create, even when
   those scripts cover every declared name.
7. **Templates read the list.** `model.environments` lists the declared environments, ordinal by name, each with `name`
   and `description`, and is empty when none is declared; reading it records `s:environments`, so a unit that read it
   renders again when the list changes.
8. **sql-ddl follows the list, or the names in use.** `seed-environments` and `object-environments` write a script per
   declared environment that some seed or object of the database names, as today; with none declared, a script per name
   that some seed or object of the database names. The mapped entity seeds of decision 9 count. The `environments`
   parameter goes: a project that still sets it gets MQ6024, a warning, and its scripts follow the declared list, or the
   names in use. A name the parameter listed keeps its script; a name in use that it left out now gets one, where its
   rows or statements went nowhere before (Today). Keeping the parameter as a narrowing override would serve one team's
   layout only. With none declared, two names in use with one kebab form (`staging_eu`, `staging-eu`) still give one
   file name, as today; declaring the names removes the case (decision 1).
9. **Every seed source follows environments.** In sql-ddl, the seeds of entities and relations mapped to a table move
   to the same rule as the others: `seed.sql` holds the seeds of every environment, and `seed.<env>.sql` the seeds that
   name `<env>`. Today their rows of one environment reach every database. This holds with no environments declared
   too: the seed's name is a name in use, so its script exists (decision 8). Beside decision 8's names that the
   parameter left out, it is the only output change a project that declares nothing sees, and only where a mapped
   entity's seed names environments. Reference-type codes stay model-wide (Today).
10. **Names are external names, not links.** Environment names appear in script names, deploy steps and run-time loader
    calls, like physical names; they are not elements and have no ids. Renaming one edits the uses, and the list when
    the project declares one; once environments are declared, MQ7131 and MQ4094 point at every use left behind.
11. **A row's references follow its environments.** After decision 9, an entity seed row of every environment whose end
    cell names a row of a `dev` seed would reach `seed.sql` while the row it names reaches only `seed.dev.sql`, and the
    insert would fail on its foreign key everywhere else. Entity seeds bound to a table have the same gap today. MQ7009
    ("a seed cell names a row that does not exist") gains the case: an end cell that names a row its seed does not load
    in every environment the cell's row loads in (the coverage MQ7111 applies to table seeds: a seed of every
    environment needs a row of every environment; a seed of named environments needs, for each, a row of every
    environment or of that one). The seed is a referrer of the rows it names, so a save of either seed checks it. The
    check compares the names seeds use, declared or not, so it runs with no environments declared too, as decision 9
    applies there.

#### Schema and files

`schemas/v1/maquettiste.json` and its copy under `samples/reference-app/.maquettiste/.schema/v1/`: `environments`
joins `x-order` after `migrations`.

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| `environments` | object: `propertyNames` `common.json#/$defs/key` with `maxLength` 64, values `#/$defs/environment` | `{}` | Optional. The environments the project deploys to, by kebab name. Absent or empty, seeds and SQL objects name environments freely and packs write a script per name in use; once one is declared, seeds and SQL objects name only declared ones and packs write a script per declared name. A key that is not a kebab key is MQ1002. |
| `$defs/environment` | object, `additionalProperties: false`, `x-order` `description` | | One environment. |
| `$defs/environment/description` | string, `minLength` 1 | absent | What the environment is for. |

Example: `"environments": { "dev": {}, "staging": { "description": "Shared before release." } }`.

#### Resolution and the resolved model

```csharp
namespace Maquettiste.Engine.Model;
public sealed record ProjectSettings       // existing members, plus:
{
    public IReadOnlyDictionary<string, EnvironmentSettings> Environments { get; init; }
        = ImmutableDictionary<string, EnvironmentSettings>.Empty;
}
public sealed record EnvironmentSettings { public string? Description { get; init; } }

namespace Maquettiste.Engine.Resolution;
public sealed class ResolvedModel { public RList<REnvironment> Environments { get; internal init; } } // by name
/// <summary>A declared environment, an item of model.environments; it depends on the environments settings.</summary>
public sealed class REnvironment : RObject
{
    public const string SettingsKey = "s:environments";
    public override string Kind => "environment";
    public string Name { get; internal set; } = "";
    public string? Description { get; internal set; }
}
```

`ResolveRun` fills `Environments` from the settings as it fills `Locales` (`Resolution/ResolveRun.cs:168`): one
`REnvironment` per entry, ordinal by name, with `Id` `environment:<name>` and `Dependencies` `[s:environments]`, in
`new RList<REnvironment>(..., [REnvironment.SettingsKey])`; the list is empty when none is declared.
`DependencyHasher` hashes `s:environments` as `CanonicalForm.Hash("s:environments", settings.Environments)` (keys in
ordinal order, so map order never reaches it). `AgentProjectInfo` gains
`public IReadOnlyList<string> Environments { get; init; } = [];`, the declared names, ordinal (empty when none is
declared).

#### Rules

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ4094 | E | A SQL object names an environment that `environments` in `maquettiste.json` does not declare, once it declares at least one; with none declared, names are free and the rule does not run. Reported at `/environments/<i>`. | Declare it, or fix the name |
| MQ7131 | E | A seed names an environment that `environments` in `maquettiste.json` does not declare, once it declares at least one; with none declared, names are free and the rule does not run. Reported at `/environments/<i>`. | Declare it, or fix the name |
| MQ7009 | E | Changed text: a seed cell names a row that does not exist, or an end cell names a row its seed does not load in every environment the cell's row loads in. Reported at the cell, as today. | Load the named row there too, or narrow this seed |

The messages of MQ4094 and MQ7131 name the declared environments (`declared: dev, staging`); neither fires while none
is declared. MQ7009's new message names the environments the named row is missing from. MQ4095 stays reserved.

#### CLI and MCP

No verb, flag or tool is added. `get_settings` and `save_settings` carry the new key. `get_project`'s summary lists
`environments` (the declared names, empty when none is declared), so an agent writing a seed sees them; its
description in `Agent/tools.json` and its `docs/mcp.md` row change.

#### Templates

- `model.environments` (decision 7).
- sql-ddl: a new `environment_names(db, of)` in `_reference.scriban` answers the declared names when
  `model.environments` holds any, and otherwise the distinct names, ordinal, that the database's seeds (`of`
  `"seeds"`: table seeds, bound and mapped entity and relation seeds) or its SQL objects (`"objects"`) use.
  `seed-environments.scriban` and `object-environments.scriban` loop over it; `pack.json` loses the `environments`
  parameter and its description says "one seed script and one SQL object script per declared environment, or per
  environment named when none is declared". `seed_statements(db, out, env)` and a new `seed_here(seed, env)` in
  `_reference.scriban` (`env` `""` for every environment, as `data_row_here` reads it); `seed.scriban` calls it with
  `""`, `seed-environments.scriban` with each name. In `seed.<env>.sql` the mapped entity seeds follow the table rows,
  in `seed.sql`'s order, and a script is written when either list holds something. The file names keep `kebab env`,
  which is the name itself once names are declared (decision 1). The sample copies under
  `samples/reference-app/.maquettiste/templates/sql-ddl/` follow. No golden or pack test case changes: no fixture model
  and no shipped pack's case (5.4) names environments, and the pack tests that name them drop the parameter, declare
  nothing and keep their goldens, since the parameter listed every name they use (Tests).

#### Editor follow-ups

An Environments panel in the project settings. Once environments are declared, the seed and SQL object forms pick
from the list and flag an undeclared name; with none declared, they take free names. The generic JSON form covers the
key until then.

#### Docs

`docs/user-guide.md`: the settings reference (`environments`: optional, kebab names, what declaring changes), SQL
objects (where they run: per declared environment, or per name in use when none is declared), seeds (where the rows
go, every source alike), the sql-ddl unit list (no `environments` parameter), `model.environments` beside
`model.locales` in the generation part, and MQ4094, MQ7131 and MQ7009's new case.
`docs/guides/reference-data-and-seeds.md`: declaring the environments, which is optional, and what it checks (its
step that sets the pack's `environments` parameter changes). `packs/sql-ddl/README.md`: the two units, the removed
parameter, mapped entity seeds by environment. `docs/mcp.md`: `get_project`. `skills/maquettiste-modeling/SKILL.md`:
when the project declares environments, name only those. `docs/releases.md` under `## 0.12.0 (unreleased)`: you may
declare the environments your seeds and SQL objects name, and once you declare one, every name is checked; a project
that declares none keeps free names. The sql-ddl parameter is gone: environment scripts follow the declared list, or
the names in use when none is declared, so a name the parameter left out now gets its script. The seeds of mapped
entities now go to their environment's script, declared or not.

**Not doing**

- Requiring a declaration: the list is opt-in, and a project without one keeps free names (decision 3).
- Connection settings, a dialect version, `db plan --env` and the ledger view per environment: phase 3 (decision 2).
- Environments as elements with ids, and a rename operation (decision 10).
- Reading an empty list as the declared set in MQ7111, MQ7112 and MQ4069 (decision 6).
- Per-environment properties for templates, and typed environment names in csharp-dapper's loader: a pack can read
  `model.environments` when one needs it.
- Checking an adopt fact's `environment` (decision 5).
- Varying a reference type's codes by environment.
- Keeping sql-ddl's `environments` parameter as an override (decision 8).
- A rule for declared names that differ only in letter case: declared names are kebab keys (decision 1).

**Tests**

Unit tests:

- `Validation/EnvironmentRuleTests` (new): `A_seed_naming_an_undeclared_environment_is_MQ7131_at_its_pointer`,
  `A_sql_object_naming_an_undeclared_environment_is_MQ4094_at_its_pointer`,
  `With_no_environments_declared_no_name_is_reported` (`Dev` and `staging_eu`, which are not kebab keys, included),
  `A_table_seed_naming_an_undeclared_environment_is_MQ7131_too`,
  `Seeds_and_objects_of_every_environment_need_no_declaration`, `An_adopt_facts_environment_is_not_checked`,
  `The_severity_follows_validation_rules`, `An_end_cell_naming_a_row_of_fewer_environments_is_MQ7009` (with and
  without declared environments), `An_end_cell_naming_a_row_of_every_environment_is_clean`.
- `Editor/SettingsTests.An_invalid_body_is_refused_and_writes_nothing` gains the case
  `{ "formatVersion": 1, "environments": { "Staging": {} } }`, MQ1002.
- `Store/ModelStoreTests`: `A_settings_save_that_drops_an_environment_a_seed_names_is_refused_with_MQ7131`,
  `Declaring_the_first_environments_is_refused_unless_they_cover_the_names_in_use`,
  `Removing_every_environment_returns_to_free_names`.
- `Resolution/ResolverBehaviorTests.Declared_environments_reach_model_environments_ordinal_by_name`;
  `Planning/HashingAndStateTests.The_environments_section_has_its_own_settings_key`.
- `Editor/AgentToolsTests.Get_project_lists_the_declared_environments`.
- Packs `TableSeedSqlTests`: `Environment_scripts_follow_the_declared_environments`,
  `A_mapped_entity_seed_of_one_environment_goes_to_its_script_only` (with `dev` declared and with none declared),
  `The_retired_environments_parameter_is_MQ6024`. `TableSeedSqlTests` and `SqlObjectEnvironmentTests` each gain
  `With_no_environments_declared_no_rule_fires_and_the_output_is_unchanged`: their repos without the parameter and
  without `environments` validate with no MQ7131, MQ4094 or MQ6024, and generate writes their goldens byte for byte,
  since the parameter listed every name they use. Their other cases drop the parameter the same way and declare only
  where a case tests the declared list; their goldens stay byte-identical.

Acceptance (the scope gives no "Done when"; these lines turn its shape into billing terms),
`Integration/DeclaredEnvironmentsTests` (new), on `EditorRepo` (a billing copy with sql-ddl writing to `db`). Every
case but the first declares `environments: { "dev": {}, "staging": {} }`:

- `Without_declared_environments_names_stay_free`. With no `environments` in the settings, a seed of table `notes`
  (`01K6BND0000000000000000001`) with one row and `environments: ["staging_eu"]`, and a SQL object of database `main`
  with `environments: ["qa"]` and a rerunnable body, save clean; generate writes `db/main/seed.staging-eu.sql` and
  `db/main/objects.qa.sql`. Saving the settings with `environments: { "dev": {} }` is then refused with MQ7131 on the
  seed and MQ4094 on the object, and nothing is written.
- `A_typo_in_a_seeds_environment_is_refused_at_save`. Creating a seed of table `notes` with one row and
  `environments: ["stagng"]` is invalid: MQ7131 at `/environments/0`, the message listing `dev, staging`; nothing is
  written.
- `Declared_names_drive_the_environment_scripts`. The same seed with `["staging"]` and a SQL object of database `main`
  with `environments: ["dev"]` and a rerunnable body: generate writes `db/main/seed.staging.sql` holding the row and
  `db/main/objects.dev.sql` holding the object, and neither `db/main/seed.dev.sql` nor `db/main/objects.staging.sql`.
- `A_mapped_entity_seed_of_dev_stays_out_of_seed_sql`. A seed of `Customer` of every environment with one row, and a
  seed of `Invoice` (mapped by `invoice-in-main`) with one row whose required `customer` end cell names it and
  `["dev"]`: the invoice insert is in `db/main/seed.dev.sql` and not in `db/main/seed.sql`; the customer insert is in
  `db/main/seed.sql`.
- `Removing_a_used_environment_is_refused`. Saving the settings without `staging`, with `dev` still declared, while the
  seed names `staging` is invalid with MQ7131 on the seed.
- `An_invoice_row_of_every_environment_cannot_name_a_customer_row_of_dev`. A seed of `Customer` with one row and
  `["dev"]`, then a seed of `Invoice` of every environment whose `customer` end cell (relation `places`) names that
  row: the save is invalid with MQ7009 at the cell, the message saying the row is missing in every environment but
  `dev`; with `["dev"]` on the `Invoice` seed it saves, and generate writes both inserts into `db/main/seed.dev.sql`,
  the customer first.

**Tasks**

- **G20. The list, the resolved model and its key.** Runs after G2. Owns: `schemas/v1/maquettiste.json` and its sample
  copy, the generated `src/editor/src/api/schema.d.ts` and `src/editor/src/mocks/openapi.json` (the contract
  references the settings schema), `Model/ProjectSettings.cs` (`Environments`, `EnvironmentSettings`),
  `Resolution/ResolvedCore.cs` (`ResolvedModel.Environments`, `REnvironment`), `Resolution/ResolveRun.cs` (filling the
  list), `Planning/DependencyHasher.cs` (`s:environments` in `SettingsHashOf`), the `SettingsTests` case. Tests: the
  `ResolverBehaviorTests`, `HashingAndStateTests` and `SettingsTests` cases. Verify:
  `E "~Resolution.|~Json.|~HashingAndStateTests|~SchemaConsistencyTests|~SettingsTests"`; `editor`; `copies`.
- **G21. Rules.** Runs after G20. Owns: `Validation/EnvironmentRules.cs` (new), the MQ7131 call and the MQ7009 end-cell
  case in `Validation/ReferenceDataRules.cs` (`CheckSeed`, `CellProblem`), the MQ4094 call in
  `Validation/DatabaseObjectRules.cs` (`CheckSqlObject`), `Diagnostics/RuleCatalog.cs` (MQ4094 and MQ7131 new, MQ7009's
  text), `CatalogTests.cs` (+2). The engine tests that save seeds or SQL objects naming environments declare none, so
  they pass unchanged. Tests: `EnvironmentRuleTests`, the `ModelStoreTests` cases. Verify:
  `E "~EnvironmentRuleTests|~CatalogTests|~ValidationFixtureTests|~SqlObject|~TableSeed|~Reference|~ModelStoreTests"`;
  `record`.
- **G22. sql-ddl follows the list.** Runs after G20 and G21. Owns: `packs/sql-ddl/pack.json`,
  `packs/sql-ddl/seed-environments.scriban`, `packs/sql-ddl/object-environments.scriban`,
  `packs/sql-ddl/_reference.scriban` (`environment_names`, `seed_statements`, `seed_here`),
  `packs/sql-ddl/seed.scriban`, `packs/sql-ddl/README.md`, their copies under
  `samples/reference-app/.maquettiste/templates/sql-ddl/`, `tests/Maquettiste.Packs.Tests/TableSeedSqlTests.cs`,
  `tests/Maquettiste.Packs.Tests/SqlObjectEnvironmentTests.cs`,
  `tests/Maquettiste.Engine.Tests/Integration/DeclaredEnvironmentsTests.cs` (new). Tests: the `TableSeedSqlTests` and
  `SqlObjectEnvironmentTests` cases, `DeclaredEnvironmentsTests`. Verify:
  `P "~TableSeedSql|~SqlObjectEnvironment|~GoldenTests|~DdlCoverage"`; `PG P "~TableSeedSql|~SqlObjectEnvironment"`;
  `E "~DeclaredEnvironmentsTests"`; `C "~ShippedPackTests"` (5.4's sql-ddl case, unchanged). No golden update.
- **G23. `get_project` and docs.** Runs after G20, G14 and G15. Owns: `Agent/AgentTools.Handlers.cs`
  (`AgentProjectInfo.Environments`), `Agent/tools.json` (`get_project`), the `get_project` row of `docs/mcp.md`,
  `docs/user-guide.md` (the environment parts), `docs/guides/reference-data-and-seeds.md`,
  `skills/maquettiste-modeling/SKILL.md` (environments), `docs/releases.md` (the 6.3 lines). Tests:
  `AgentToolsTests.Get_project_lists_the_declared_environments`. Verify: `E "~AgentToolsTests"`;
  `C "~McpReadTests|~McpSurfaceTests|~SkillRefreshTests"`.

G24 to G29 are unused.

## 7. Implementation order

Tasks run in the order below. A task starts only after the tasks in its "After" column; the reason says what it needs
from them. The column lists every "Runs after" of the task's section. A task of an earlier milestone in it is met by
that milestone's gate, and a task with no entry depends only on earlier milestones. Tasks that share a step number may
run in parallel: no "Runs after" joins them, and they own no file in common, counting the editor's generated types and
mocks (`editor` regenerates them) and the recorded rule list (`record` rewrites it). Each milestone ends with a gate.

**Gate** (the same at the end of each milestone):

1. `build`.
2. `E`, `C` and `F` without filters: every .NET suite. From F44 on, `C` also runs `maquettiste pack test` on the
   shipped packs' cases and helper tests (`ShippedPackTests`).
3. `PG P` without a filter: the pack tests, including those that need the PostgreSQL container.
4. `editor`, since every milestone touches `src/editor` or `docs/api/openapi.yaml`.
5. A pull request into `main` whose description is the customers' record of the milestone (decision 9 of section 1):
   what changed and why, how to try it, upgrade checks, new rule ids and what is deferred, with billing and invented
   names only. `next.md` is updated in the same change. No task waits for the pull request to be read; the owner
   merges it.
6. A commit script `tmp/NN-commit-<slug>.sh` (the next free number) that commits the milestone. The owner runs it; no
   task commits.

### Milestone D: section 3

3.1 runs first: the other sections of milestone D build on its member schemas, its effective set and its fold, and
they edit files its tasks own. 3.2 to 3.5 then follow in order, each section's first task waiting for the earlier tasks
that own its files. 45 tasks in 27 steps.

Joined because they regenerate the editor's types: D34 does, and so do D40, D41 and D43, which change `table.json`
and `seed.json`, run `editor` and name the generated files in their Owns lines. D40 therefore runs after D34, and D41
and D43 after D40, so D34 takes step 16 and D40 step 17.

| Step | Task | After | Reason |
| --- | --- | --- | --- |
| 1 | D1 Shared member schemas |  |  |
| 2 | D2 Defaults, exclusions and the effective set | D1 | The moved member `$defs`; `Physical.cs` |
| 3 | D3 Readers of the effective set | D2 | The effective set |
| 4 | D4 Defaults in the classifier and the save scope | D3 | The readers of the effective set |
| 4 | D6 Contributed members in the model | D3 | `Vocabularies.cs`, `ModelIndexer.cs` and the kind schemas after D2 and D3 |
| 5 | D5 Rules for defaults and exclusions | D4 | Holders and the save scope |
| 5 | D10 Planner links on edit and delete | D6 | The member model |
| 6 | D7 Resolution of inherited members | D5, D6 | The member model; D5's stereotype checks for tables without a file; `RuleCatalog.cs` |
| 7 | D8 Validation of members and links | D7 | Member sets and origins; `RuleCatalog.cs` |
| 7 | D13 sql-ddl renders inherited members | D7 | Inherited members in the resolved model |
| 7 | D17 Rekeys that move constraints, and undoing a fold | D4, D6, D7 | `ModelStore.Migrations.cs` after D4; members and origins |
| 8 | D9 Views and the HTTP contract | D7, D17 | Origins on resolved members; D17 regenerates the editor's types first |
| 8 | D16 Materialize keeps inherited members | D17 | `MaterializeResult` in `Materializer.cs` |
| 9 | D11 The fold | D8, D9, D10, D16, D17 | Member rules, views, delete links, materialize and the rekey; `Materializer.cs` |
| 10 | D12 Fold on MCP and the CLI | D9, D11 | The fold; `docs/mcp.md` after D9 |
| 10 | D20 Column `typeRef` and `sensitive` in the schema and model | D11 | Every 3.1 task on `common.json`, `Physical.cs`, `ChangePlanner.cs` and the editor's types |
| 11 | D14 Acceptance | D12, D13 | The fold through MCP and the CLI; the sql-ddl pack test |
| 11 | D15 Docs | D12 | The fold through MCP and the CLI |
| 11 | D21 Typed designed columns in resolution, snapshots and views | D20 | `Column.TypeRef` |
| 11 | D22 Column type rules | D20 | `Column.TypeRef`; the 3.1 rule files |
| 12 | D23 Sensitivity shown on both sides | D21 | Typed columns |
| 12 | D24 Usage lists columns | D12, D21 | Typed columns; `docs/mcp.md` after D12 |
| 12 | D25 Materialize keeps the logical type | D21 | Typed columns; `Materializer.cs` |
| 12 | D26 Packs render typed designed columns | D13, D21 | Typed columns; `packs/sql-ddl/README.md` after D13 |
| 13 | D27 Acceptance, guides and the release note | D15, D22, D23, D24, D25, D26 | The user guide, skill and release notes after D15 |
| 13 | D30 Provenance, the shape hash and the drift of one binding | D20, D24 | `Column.TypeRef`; the editor's types after D24 |
| 13 | D32 Materialize types attributes by their column's type | D21, D25 | `Materializer.cs` |
| 14 | D31 The drift rules | D22, D23, D30 | `RuleCatalog.cs` after D22; `DatabaseRun.Bindings.cs` after D23 |
| 15 | D33 Map by name and re-sync; provenance from every by-name operation | D11, D24, D30, D31, D32 | Drift and its rules; the fold's batch files (D11); `openapi.yaml` after D24 |
| 16 | D34 An attribute without a description shows its column's comment | D23, D26, D33 | `ResolveRun.Shared.cs`; pack goldens; `openapi.yaml` |
| 16 | D35 Drift and re-sync through MCP and the CLI | D31, D33 | The drift report and the operations |
| 17 | D36 Acceptance, guides, the skill and the release note | D27, D32, D34, D35 | The guides, skill and notes after D27 |
| 17 | D40 Stable ids | D8, D22, D33, D34 | `PhysicalRules.cs`, `TableSeedRules.cs`, `RuleCatalog.cs`; the editor's types after D34 |
| 18 | D41 Foreign keys by natural key | D24, D40 | `seed.json` and the seed rules; `ModelStore.LocalizationApi.cs`; the editor's types after D40 |
| 19 | D42 sql-ddl lookups and seed-data | D13, D26, D41 | Natural-key refs; `packs/sql-ddl/README.md` |
| 19 | D43 Scripted rows | D10, D40, D41 | `seed.json`, `ReferenceData.cs` and the seed rule files; `ChangePlanner.cs`; the editor's types after D40 and D41 |
| 20 | D44 Seed preview, CSV and extension tools | D12, D34, D35, D43 | Scripted rows; `docs/mcp.md`, `openapi.yaml`, the agent tools and the tool pins |
| 21 | D45 Guide and acceptance | D15, D27, D36, D42, D43, D44 | The guides, skill, `engine-design.md` and release notes |
| 22 | D50 Former codes | D44, D45 | `ReferenceData.cs`, the seed rule files, `ModelStore.LocalizationApi.cs`; the docs after D45 |
| 23 | D51 The value-map format | D41, D50 | `ResolvedRecords.cs`, `openapi.yaml`, `ResolvedPhysical.cs` |
| 24 | D52 The four operations | D51 | `openapi.yaml`, `RuleCatalog.cs` |
| 25 | D53 Recording value maps | D52 | The four operations |
| 26 | D54 Baseline checks and fold | D53 | Recorded value maps |
| 26 | D55 The data step and generated aliases | D15, D42, D53 | Recorded value maps; sql-ddl after D42; `schema-changes.md` after D15 |
| 27 | D56 Guide and acceptance | D21, D45, D54, D55 | The shared docs after D45; columns typed by a reference type (D21) |

Gate D.

### Milestone E: section 4

4.1 runs first. 4.2 follows 4.1's E4, E6, E7, E8 and E11, which own the kind registry, the `appliesTo` lists,
`PropertySchemas.cs` and `openapi.yaml`. Section 4.4's E50 to E52 come next, after 4.2's E26, and 4.3's E40 follows
them, since it writes `satisfies` into `process.json`, `openapi.yaml` and `ResolvedCoverageTests` after E50 and E52,
and rewrites the recorded rule list after E51. 4.4's E53 closes the milestone after 4.3's docs. 32 tasks in 20 steps.

Joined because they share generated files: E11 runs `editor` after E5 and E8 change the contract, so it runs after E8
and takes step 6 beside E12. E40 and E51 both rewrite the recorded rule list, so E40 runs after E51.

| Step | Task | After | Reason |
| --- | --- | --- | --- |
| 1 | E1 The kind, its records, schema, index and folders | D2 | The effective set with defaults and `excludeStereotypes` |
| 2 | E2 Rules on an operation's own structure | E1 | The kind |
| 2 | E3 Resolution | E1 | The kind |
| 2 | E6 Facts and deletes | E1 | The kind |
| 3 | E4 Templates | E3 | `ROperation` |
| 4 | E5 Shared errors from stereotypes | D8, D10, E2, E4 | E2 and E4 regenerate the editor's contract first; contributed members (D8) and their delete links (D10) |
| 5 | E7 Localization | E5 | Inherited errors |
| 5 | E8 Agent and API surfaces | E5 | Records with inherited errors |
| 5 | E9 Engine acceptance | E2, E3, E5 | Rules, resolution and shared errors |
| 5 | E10 The example pack | E4, E5 | `each operation`; inherited errors |
| 6 | E11 Editor: open and list operations | E1, E4, E8 | The kind and `each operation`; the editor's types after E8, which follows E5 |
| 6 | E12 Docs | E7, E8, E10 | Localization, surfaces and the example pack |
| 7 | E20 The `custom` kind and custom kind files | E1, E4, E6, E7, E8, E11 | The kind registry, the `appliesTo` lists, `PropertySchemas.cs`, `openapi.yaml`, `labels.ts` |
| 8 | E21 Content schemas in the property schema set | E7, E20 | Custom kind files |
| 9 | E22 Members as sub-elements | E21 | Content schemas |
| 9 | E23 Content validation | E21 | Content schemas |
| 10 | E24 Content and member translations | E8, E22 | Members |
| 11 | E25 Resolution and templates | E4, E8, E12, E24 | `openapi.yaml`, `tools.json`; `engine-design.md` after E12 |
| 12 | E26 Tools for custom kind files and custom rows | E23, E25 | Content validation; resolution and templates |
| 13 | E27 Docs for custom kinds | E12, E26 | The tools; the shared docs after E12 |
| 13 | E28 Acceptance for custom kinds | E26 | The tools |
| 13 | E50 Libraries and node annotations in the schema | E26 | Every 4.2 task on `PropertySchemas.cs`, `ModelIndexer.cs`, `NameIndex.cs`, `openapi.yaml` and the `appliesTo` lists |
| 14 | E29 Acceptance with an operation | E1, E28 | `CustomKindTests.cs`; the `operation` kind |
| 14 | E51 Library rules | E50 | Libraries in the schema |
| 14 | E52 Charts, replay and the resolved model | E50 | Libraries in the schema |
| 15 | E40 The kind and the two links | E1, E5, E6, E20, E50, E51, E52 | `custom.json`, `process.json` and `operation.json` gain `satisfies`; `openapi.yaml` after E52; the recorded rule list after E51 |
| 16 | E41 Requirement rules | E40, E51, E52 | `RuleCatalog.cs` after E51; `ModelValidator.cs` after E52 |
| 16 | E42 Resolution and templates | E26, E40, E52 | `tools.json`, `openapi.yaml`, `ResolvedCore.cs`, `ResolvedRecords.cs` |
| 17 | E43 Coverage | E42 | `tools.json` |
| 18 | E44 Import | E41, E43 | `RuleCatalog.cs`; the tool pins after E43 |
| 19 | E45 Docs and acceptance for requirements | E27, E41, E42, E44 | The shared docs after E27 |
| 20 | E53 Docs and acceptance for libraries | E45, E51, E52 | The shared docs after E45 |

Gate E.

### Milestone F: section 5

5.1 runs first, since its sql-ddl oracle (F1) must record the output before anything moves. 5.2 follows 5.1's F11
(the rule catalog), F13, F15 and F16; 5.3 follows 5.2; 5.4 follows 5.3's F33; 5.5 follows 5.2's F25 and 5.4's F41 and
F42; 5.6 follows 5.5's F53 and F54. F2, F20 and F22 depend on nothing in the milestone and start beside F1. 51 tasks
in 28 steps.

Joined because they regenerate the editor's types: F13 does when it records the pack mock, and so do F20 and F24,
which change the contract. F13 runs after F20, and F24 after F13, so each takes a different step.

| Step | Task | After | Reason |
| --- | --- | --- | --- |
| 1 | F1 The oracle, before anything moves | D13, D26, D55 | sql-ddl with inherited members, typed columns and the value-map step |
| 1 | F2 Step records, text form, options and reads |  |  |
| 1 | F20 Manifest and settings |  |  |
| 1 | F22 Sandbox calls into used packs |  |  |
| 2 | F3 PostgreSQL tables | F2 | Step records |
| 2 | F14 MQ4090 | F2 | `ServerVersions` |
| 3 | F4 PostgreSQL objects and the ledger | F3 | Table builders |
| 4 | F5 Build trees | F4 | Object builders |
| 5 | F6 Helpers, MQ6040 and build parity | F1, F5, F14 | The oracle and the build trees; `RuleCatalog.cs` after F14 |
| 5 | F8 Change trees: tables, columns, constraints and indexes | F5 | `DdlBuilder.cs` |
| 5 | F15 `preview_ddl` | F5 | Build trees |
| 6 | F7 sql-ddl builds `schema.sql` and the object scripts with the steps | F6 | The helpers |
| 6 | F9 Change trees: views, routines, types, sequences, schemas and SQL objects | F8 | Table change trees |
| 7 | F10 Change trees: migration facts, the schema-diff target and migration parity | F6, F9 | The helpers; object change trees |
| 8 | F11 MQ6041 | F10 | Change trees and their notes |
| 9 | F12 sql-ddl writes PostgreSQL migrations with the steps | F7, F11 | `_sql.scriban` after F7; MQ6041 |
| 9 | F21 Loader: closure, ranges, cycles and order | F11, F20 | `RuleCatalog.cs` and the `CatalogTests` pin after F11 |
| 10 | F13 Prune the PostgreSQL template code; retire the probe | F12, F20 | Migrations on the steps; the editor's types after F20 |
| 10 | F17 Acceptance and determinism | F12 | Migrations on the steps |
| 10 | F23 Rendering: cross-pack includes and namespaces | F21, F22 | The closure; sandbox calls |
| 10 | F25 Project rule scripts use libraries | F21, F22 | The closure; sandbox calls |
| 11 | F16 Docs and release notes | F13, F14, F15 | The pruned pack, MQ4090 and `preview_ddl` |
| 11 | F18 Bench parity and timing | F13 | The pruned pack |
| 11 | F24 Static hashes, previews and template context | F13, F23 | Cross-pack rendering; the editor's types after F13 |
| 12 | F26 Authoring: list, delete, move, rename and the library starter | F13, F15, F24 | `GenerationService.Packs.cs`, `openapi.yaml`; `tools.json` after F15; the recorded pack mock after F13 |
| 13 | F27 Docs and acceptance | F16, F24, F25, F26 | Release notes, user guide and `packs/README.md` after F16 |
| 13 | F30 Unit `exports` in the manifest | F20, F26 | `UnitPlanner.cs`; `openapi.yaml` and the editor files after F26 |
| 14 | F31 The export table, `exported` and `exports` | F21, F23, F30 | The manifest; the closure and cross-pack rendering |
| 15 | F32 Unit states, plans and apply | F31 | The export table |
| 16 | F33 Previews, `pack exports` and `get_pack_exports` | F24, F32 | Plans; template context after F24 |
| 17 | F34 Docs and acceptance | F27, F33 | The shared docs and the guide after F27 |
| 17 | F40 The tests folder and the case runner | F33 | Every 5.2 and 5.3 task on `RuleCatalog.cs`, `openapi.yaml`, `PackFileRoles.cs`, `TemplateCache.cs`, `PackAuthoring.cs` |
| 18 | F41 Helper tests in the sandbox | F22, F40 | The runner; sandbox calls into used packs |
| 19 | F42 `pack test` and `test_pack` | F41 | Helper tests; the CLI files and `docs/mcp.md` after F33 |
| 19 | F50 Calculation files load and register | F25, F41 | The loader, snapshot and validator after F25; sandbox files and the rule catalog after F41 |
| 20 | F43 New packs start with tests; starters carry none | F26, F42 | `pack test`; the starters after F26 |
| 20 | F51 Evaluation, sites and the cache | D43, F50 | Calculation files; 3.4's `x:` key |
| 21 | F44 A small case for each shipped pack | F13, F43 | `pack test` and starters without `tests/`; `packs/sql-ddl/README.md` after F13 |
| 21 | F46 Docs and acceptance | F34, F43 | The shared docs and the guide after F34 |
| 21 | F52 Templates read calculated values | F51 | Evaluation |
| 21 | F53 Checks after resolution and resolved rules | F25, F51 | Evaluation; libraries in rule pools |
| 22 | F45 Helper tests for the shipped packs | F41, F44 | The shipped packs' `tests/` folders; helper tests |
| 22 | F54 Reads for agents, the CLI and extension files | F42, F52, F53 | The agent tools, `CliApp.cs`, `openapi.yaml`, `docs/mcp.md` after F42 |
| 22 | F60 Rule declaration files load | F53 | `ModelStore.cs` |
| 23 | F55 Docs, skill and acceptance | F46, F54 | The docs, `packs/README.md` and the skill after F46 |
| 23 | F61 Static checks | F53, F60 | Declaration files; the checks after resolution |
| 24 | F62 Evaluation | F61 | Static checks |
| 25 | F63 Suppressions in the model | D6, F54, F62 | The editor files after F54; keyed member ids (D6) |
| 26 | F64 Suppressions apply | F63 | Suppressions in the model |
| 27 | F65 Declaration files as extension files | F54, F64 | Agent reads; suppressions apply |
| 28 | F66 Docs, skill and acceptance | D7, F55, F65 | The docs after F55; contributed columns (D7) |

Gate F.

### Milestone G: section 6

Three chains start together: 6.1 (G1 and G2), 6.2 (G10) and 6.3 (G20, after G2's keys in `DependencyHasher.cs`). G22
also needs 5.1's F13, which gate F has met. G15 joins the chains through the editor's types (G20) and the shared docs
(G5), and G23 closes the milestone. 15 tasks in 6 steps.

| Step | Task | After | Reason |
| --- | --- | --- | --- |
| 1 | G1 Impact records, referrers and reached databases |  |  |
| 1 | G2 Touched keys |  |  |
| 1 | G10 Sides |  |  |
| 2 | G3 Units per pack and the service's dry run | G1, G2 | Referrers; touched keys |
| 2 | G11 Packages and field changes | G10 | Sides |
| 2 | G20 The list, the resolved model and its key | G2 | `DependencyHasher.cs` |
| 3 | G4 MCP and CLI | G3 | The service's dry run |
| 3 | G12 Formats | G11 | Packages and field changes |
| 3 | G21 Rules | G20 | The declared list |
| 4 | G5 Skill, guide and release note | G4 | The MCP and CLI answers |
| 4 | G13 `model diff` | G4, G12 | Formats; `CliApp.cs`, `ModelCommand.cs`, `docs/user-guide.md` after G4 |
| 4 | G14 MCP `compare_snapshots` | G4, G12 | Formats; `docs/mcp.md` after G4 |
| 4 | G22 sql-ddl follows the list | F13, G20, G21 | The list and its rules; sql-ddl after F13 |
| 5 | G15 Contract, acceptance and docs | G5, G13, G14, G20 | The release notes and skill after G5; the editor's types after G20 |
| 6 | G23 `get_project` and docs | G14, G15, G20 | `tools.json`, `docs/mcp.md` and the docs after G14 and G15 |

Gate G.

### Phase close

After gate G the gate runs once more over the whole phase, with a commit script. Before that run, three checks hold.
The pins read what section 2 gives: 354 rules in `CatalogTests`, 79 MCP tools in the `McpSurfaceTests` Tools array and
32 `AgentTools.All` entries, with the counts in `README.md` and `docs/mcp.md` to match. The ledger stays at version 1
(decision 3): `maquettiste migration ledger` prints the same DDL as at the start of phase 2, and `LedgerContractTests`
passes unchanged. `docs/releases.md` holds one entry per section under `## 0.12.0 (unreleased)`; the owner sets the
version and the date. The editor follow-ups that the sections list are then gathered into one list for the owner, and
the status line at the top of this design says what was built.
