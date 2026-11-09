# Relations with several types at an end: design

**Status (2026-11-08): part 1 released in 0.21.0 (#70); part 2 not built. One independent check is applied (section 14). The owner answered all six questions as recommended on 2026-11-08 (section 15): part 1 is built as designed, and part 2 serves the team's designed and imported tables (path B) first.** It designs the item that [Who, what and when](lifecycle-actors-scenarios.md) section 11 describes: "an end lists several entities, all of them, or every entity carrying a stereotype". The owner's drawing of 2026-11-08 in that section is decided and is not reopened here. [Stereotype sets](stereotype-sets-design.md) (0.14.0) gives the meaning of a stereotype as a set of types, and named trigger points ([trigger points](trigger-points-design.md), part 2) give the list form it reuses. [Actors](actors-design.md), [rights](rights-design.md), [object effects](object-effects-design.md) and [lifecycles](lifecycle-design.md) give the readers that change. Every code fact here was checked against the code at 0.19.0; line numbers will move once sign-off part 2 is merged. A file path without a project prefix is under `src/Maquettiste.Engine/`. The examples are invented. Each new name and format is held to the roadmap's test: it stays for good, it must help any team whether they use it or not, and it stays optional.

## 1. In short

Today each end of a relation names one entity. A team that attaches comments to parts, assemblies and documents has to add a shared base entity, or make one relation per type. Neither says what they mean: "a comment is attached to one thing, which is a part, an assembly or a document".

| Layer | What changes |
| --- | --- |
| The model | One end of a two-ended relation may name its **types**: a list of entities and stereotypes, or **any entity**. The end then covers each listed entity, every entity that carries a listed stereotype, and every entity derived from them. Role, cardinality, navigation and delete rule stay one per end. Today's `entity` keeps its meaning and its bytes |
| Rules, processes and scenarios | The other end's navigation is generated on each type. Guard paths through such an end read what every type holds under one id. Rights, trigger points, freezing and scenario links work through it, since they already name ends by id. `in_state` through it is unknown for an object of a type the lifecycle doesn't govern, as it is today through a one-type end |
| Code generation | Part 1: templates see the types of each end, and no database stores such a relation. Part 2: a relation mapping states how a database stores it: a type and an id, a column per type, a table per type, or a key to the types' shared base table. Whether part 2 first serves the team's designed tables with explicit bindings, or tables the engine makes, is the owner's question 2 |
| The editor | The relation's form picks one entity, several types or any entity. The canvas draws the agreed looks: bundled lines for two types, a junction node for three or more, one line to a box for a stereotype or any entity, and one toggle per relation that collapses its lines to one line and a list. The toggle and the node's position are saved in the diagram file |
| The team's system, or a lifecycle system | Nothing changes until the team uses the new output or builds from the model |

**A project that uses none of this** keeps its files, output bytes, findings and messages word for word, keys, replays and speed. Section 12 says how, and which tests pin it.

**Two parts, each its own release:** part 1 is the model, the rules, processes, scenarios, the editor and the resolved model, with no storage (0.21.0). Part 2 is storage (0.22.0).

## 2. Scope

| | What | Section |
| --- | --- | --- |
| **Part 1 (0.21.0)** | `types` and `anyEntity` on a relation end; what an end covers; the rules; navigations on each type; guard paths, `in_state`, owner and actor paths, rights, trigger points, freezing and scenario links through such an end; the resolved model and records; the index; deletes and references; the relation form, the canvas looks and the collapse toggle; tool texts, the API contract and the editor's mock server; refusals where one table is needed (binding fields, query fields and collections, seeds) | 4 to 11 |
| **Part 2 (0.22.0)** | `typeStorage` on a relation mapping with four ways to store; type codes; what the database can't enforce; navigation joins per type; one-to-one uniques that work in every dialect; on the path the owner picks (question 2): the team's designed and imported tables with explicit bindings, or tables the engine makes; the Storage tab and database views; `sql-ddl` and `csharp-dapper` tests | Decisions 24 to 32 |
| **Later, if teams ask** | The part 2 path not picked first; materialize of such storage; seeds of such links; a query through such an end; several types on both ends; several types on an n-ary relation; `exceptEntities` on an end; a stereotype's attributes through a navigation; narrowing a right or trigger point to one type; a database-wide storage default; value-map facts for a renamed type code | 13 |
| **Not in either part** | Per-type cardinality, role or navigation on one end; a new element kind for a named set of types | Decisions 3 and 10 |

**The readers of `RelationEnd.Entity`**, each handled below. `RelationEnd.Entity` turns optional, which makes the compiler flag most typed readers, as `ProcessSubject.Entity` did for stereotype sets. It does not flag string comparisons, interpolation, Scriban templates or the editor's untyped reads, so the build also greps for them (section 18). There are 145 reads of `.Ends` in 33 engine files, many of them rights' and trigger points' own `Ends` lists of end ids, which don't change. The ones that read an end's entity: the schema `schemas/v1/relation.json` (ends at lines 103 to 156) and `Model/Relation.cs`; the index's `EndsOf` (`Model/ModelIndexer.cs:1077`), `RelationEndSummary` (`Model/Documents.cs:280`, compared in `IndexRow` equality at line 242) and `WithSubjectCoverage` (`ModelIndexer.cs:991-1037`); `Loading/SeedInput.cs:185-199`; `Loading/Materializer.cs` and the field candidates of `Materializer.Bindings.cs:428-436`; the cascade (`Loading/ChangePlanner.Cascade.cs`); validation: `CheckRelation` (`Validation/BuiltinRules.cs:1018`), `NavigationHolders` and the composition index (`Validation/ValidationContext.cs:530-600`), `MappingRules.cs` (`CheckRelationBindings` 323, `EffectiveShape` 353, `DependentEnd` 371), the mapping dependents (`ModelValidator.cs:722-731`), `PhysicalRules.cs`, `ReferenceDataRules.cs`; expressions: `NavigationsOf` and `AddNavigations` (`Validation/ExpressionRules.cs:1281-1313`), `CannotGovern` (847), `ExprNavigation` (`Expressions/ExprScope.cs:33-46`), the path dependencies (`Resolution/ResolvedExpressions.cs:270-283`); processes: `Processes/ScenarioObjects.cs:183-196`, `Processes/VerbStepFields.cs:84-87`, `Processes/RelativeActors.cs`; the resolver: `BuildRelation`, `CommitRelation` and `BuildNavigations` (`Resolution/ResolveRun.Conceptual.cs:669-790`, the navigation dependencies at 781), the promoted-name join at line 1226, the relative actor path keys (`ResolveRun.Processes.cs:129`), `ResolveRun.TableSeeds.cs`, `ResolveRun.ReferenceData.cs`; the database run: `Resolution/DatabaseRun.Relations.cs` (the skip at line 40, `ShapeOf` 59, `PrincipalAndDependent` 72, the two shapes, `JunctionName` 333), `DatabaseRun.Bindings.cs`, `DatabaseRun.Finish.cs` (`BuildJoins`, 504-544), `DatabaseRun.Queries.cs` (`FieldTarget`, 511); `Model/BindingDrift.cs`; `Migrations/DatabaseReach.cs` and `CodeHolders.cs`; `Editor/ResolvedRecords.cs` (`EndRecord` 305, `NavigationRecord` 251), `Editor/BindingViews.cs`, `DatabaseTables.cs`, `DatabaseViews.cs`; the assistant's prompt (`Agent/AssistantPrompt.cs`).

## 3. What other tools do, and what this design takes

| What others do | Evidence | What this design takes |
| --- | --- | --- |
| A notation keeps one type per end and reaches several through a shared supertype, or says "one of" with an exclusive-or constraint between associations | UML ends are typed by one class; {xor} joins associations [1] [2]. SysML v2 and GraphQL define a union type first [3] [5]. Barker's notation draws an arc across relationships [4] | An end may still name a shared base (today's form, nothing new). For types that share none, the end lists them, so the "one of" lives in one relation, not in a constraint across several |
| Frameworks store a type and an id, and give up the foreign key | Rails, Laravel, Django and Hibernate all use a type column and an id column, which no database foreign key can cover, as Hibernate's documentation says; Django's has no delete rule for it [6] [7] [8] [9] | One of the four ways to store in part 2, chosen per relation, never the default |
| Databases that want integrity use an exclusive arc, a shared supertype table, or one link table per type | A check that exactly one column is set, with `num_nonnulls` in PostgreSQL [13] [14]; separate intersection tables [15]; table-per-type and shared base tables in EF Core and Prisma [10] [11] [12] | The other three ways to store, each with real foreign keys |
| Lifecycle systems name one type per side of a relation rule, and a rule on a parent type covers its subtypes | Teamcenter's relation rules apply to subtypes [16] [17]; Windchill names one type per role [18]; Aras relationships name a source type and an optional related type [19] | A listed entity covers its derived entities, as everywhere in Maquettiste. A stereotype covers its carriers, as in stereotype sets |
| A type value has to survive renames | Rails and Laravel warn that stored class names break on rename; Laravel's morph map stores short aliases [6] [7] | A type code is the entity's name, overridable per database with the `discriminatorValue` the entity's mapping already has (question 5) |

No source reviewed draws a junction node with a collapse toggle; that look is the owner's.

## 4. Decisions

Each decision gives its confidence: **high**, **medium-high** or **medium**. A decision marked **(part 2)** is built in part 2. The others are part 1.

### How an end names its types

1. **An end names its types in exactly one of three ways: `entity`, `types` or `anyEntity`.** Confidence: high.
   - `entity` is today's key, with today's meaning and bytes.
   - `types` is a list of entries. Each entry is `{ "entity": <id> }` or `{ "stereotype": <key> }`, exactly one of the two. This is the shape named trigger points already use for their `subjects` (`schemas/v1/trigger-point.json`, `$defs/subject`). Stereotype sets said relation ends would "take `{ "stereotype": "<key>" }` on an end" (its section 11). The list holds at least one entry and no exact repeats (`uniqueItems`). Entries may mix entities and stereotypes.
   - In code, the entry mirrors `TriggerPointSubject` (`Model/TriggerPoints.cs:213-225`): no id of its own, `[ElementRef(ElementKind.Entity)]` on `Entity` and `[StereotypeKeys]` on `Stereotype`. References, cascade pointers and batch names then follow the existing pattern.
   - `anyEntity: true` says the end may be any entity of the model. It is a separate key, not a value of `entity`, so a reader that expects an id never gets a word.
   - **Why one list, not three keys** (`entities`, `stereotype`, `anyEntity`): one list covers several entities, a stereotype, several stereotypes and a mix, with one shape that already exists in the format.
2. **What "an end with several types" means, everywhere in this design.** Confidence: high.
   - **An end with several types** is one written with `anyEntity`, or with a `types` list that holds two or more entries or any stereotype entry. The test reads the file only, so it never changes when carriers change.
   - **A one-type end** is one written with `entity`, or with a `types` list that holds a single entity entry. That list means the same as `entity`: it resolves as a one-type end, and warning MQ3042 offers a quick fix that writes `entity`. A delete that leaves one entity entry then leaves a valid file (decision 21). The editor's form always writes `entity` for one entity.
   - So `[Part, PurchasedPart]` is an end with several types, even though it covers one type once MQ3043 is fixed, and a stereotype entry is one even when one entity or none carries the stereotype. `end.has_several_types`, `end.entity`, the rules and storage all follow this one test.
3. **No new element kind for a named set of types.** Confidence: high. A stereotype already names a reusable set. A new kind would be permanent, and most teams would never need it beside stereotypes.

### What an end covers

4. **An entity entry covers the entity and every entity derived from it. A stereotype entry covers each entity that carries the stereotype, and every entity derived from one, as stereotype sets decision 3 says. `anyEntity` covers every entity of the model.** Confidence: high.
   - "Carries" is the stereotype in the entity's effective set: package defaults, implied stereotypes, its own list, less what it excludes.
   - Abstract entities are covered: they have no objects, but their derived entities do, and they hold navigations.
   - Entities the resolver makes when it promotes a relation are not model entities, and no end covers them.
   - A stereotype nothing carries yet covers nothing, with no finding. A team may model the relation first and mark types later.
5. **The end's types are the covered entities that have no covered base, listed in the order each first appears.** Confidence: high.
   - **The set first:** every covered entity whose base, and every base above it, is not covered.
   - **Then the order:** walk the entries in file order; an entity entry gives its entity; a stereotype entry gives its carriers by qualified name, then id (ordinal); `anyEntity` gives every entity with no base by qualified name, then id. Each member of the set takes the place where it first appears in that walk. Every member appears, since it is a listed entity, a carrier or a root.
   - So a carrier derived from an entity listed later is never one of the types, whatever the entry order.
   - Lines on the canvas, the holders of the other end's navigation, the template list `end.types` and the per-type storage of part 2 all follow this list. Derived entities are reached through their base, as with `entity` today.
6. **An entry that another entry already covers is a warning, MQ3043, not an error.** A derived entity listed beside its base, or an entity listed beside a stereotype it carries. Confidence: high. Carriers change with no edit to the relation, so an error would appear in a file nobody touched.
7. **MQ2020 (package dependencies) applies to entity entries only.** They are references written in the file. A stereotype entry or `anyEntity` reaches entities in any package, as a stereotype subject entry does. Confidence: medium-high.

### Which relations may have several types

8. **One end of a two-ended relation may have several types: an association, an aggregation or a composition, with any `min` and `max`.** Several types on both ends, or on an n-ary relation, are refused (MQ3041). Confidence: medium-high; question 3.
   - **The reason is cost, not meaning.** Each end of an n-ary relation could be "one of these types" as well, and both ends of a relation could too. Part 1 stores nothing, so storage is not the reason.
   - **What it saves in part 1:** the canvas would need two junction nodes on one line, and an n-ary relation is drawn as its first two ends only (`canvas/model.ts:116-122`), so a third end with several types would not show. The rules, holders and pickers would each need the both-ends case.
   - **Lifting it later** takes the model and rules (holders from both ends), the canvas showing "+N not shown" until n-ary relations are drawn in full, and storage refused by MQ4117 until it is designed. Nothing in this design blocks it.
9. **A composition's owner end may have several types, and counts as one owner.** "A note is owned by one part, one assembly or one document" is one composition. MQ3016 (a child with two owners) doesn't fire for it. Confidence: high.
   - **The child end may list types or a stereotype.** Each covered entity is then a child of the composition, and MQ3016 checks each against its other compositions.
   - **The child end can't be `anyEntity` (MQ3041).** It would make every entity a part of one owner.
10. **`role`, `navigation`, `min`, `max`, `onDelete`, `ordered`, `displayName`, `pluralName` and `description` keep their meaning and hold for the whole end.** Confidence: high.
    - "At most one of part, assembly or document" is one `max` of 1, not one per type.
    - The navigation an end names is generated on the other end's entity, as today. The other end's navigation is generated on each of this end's types, and so on their derived entities by inheritance.
    - Per-type cardinality or navigation is not offered: it would turn one relation into several.

### Rules

11. **The new and changed rules are in section 6.** The new ones need an end with several types, so no model gets them by upgrading. Changed rules keep their messages word for word for one-type ends. Confidence: high.
12. **Incremental validation catches every change that moves a finding.** Each check below runs only while a relation with such an end exists. Confidence: high.
    - Relations whose ends name a stereotype are checked again when an entity, package or stereotype changes, since carriers may change.
    - Relations with an `anyEntity` end are checked again when an entity is added or changes.
    - Every relation with an end of several types is checked again when a mapping, a database (its `byConvention` and packages) or the settings change, since MQ4115 depends on where the types are placed (`Resolution.DatabaseScope.Places`, used at `Validation/MappingRules.cs:26`). Today's mapping dependents follow direct references only (`ModelValidator.cs:722-731`), which never reach a stereotype or `anyEntity` end.

### Expressions, processes and scenarios

13. **A path through an end with several types reads what every covered entity holds under one id: the attributes and navigations of a shared base.** Confidence: high.
    - A mix of unrelated entities, or `anyEntity`, usually offers nothing to read beyond the end itself. That is correct: the types share nothing.
    - **A stereotype's attributes stay out of every path through a navigation,** including through an end that names a stereotype. Stereotype sets decision 7 stands: "By navigation, not yet … Only the object itself offers them." The runtime agrees: nested rows carry no stereotype attributes (`Processes/ScenarioObjects.cs:345`), and a resolved path follows the far end's entity chain only (`Resolution/ResolvedExpressions.cs:270-283`). Reading them through a navigation is its own item (section 13).
14. **Paths still follow to-one ends only, everywhere they do today:** guard conditions, set values, owner fields, the paths of actors from the object and `within`. An end with several types and `max` 1 is a to-one end. Confidence: high.
15. **`in_state(path, …)` through an end with several types is valid when the lifecycle can govern at least one covered entity (MQ3116 otherwise). Its answer in a run doesn't change.** Confidence: high.
    - The rule is lenient in the same way as today's: a base entity passes when a derived entity is governed (`ExpressionRules.cs:847-857`).
    - **Today, in a run,** an object reached by a path gets its states from `ScenarioObjects.States` (`ScenarioObjects.cs:66-83`), which skips every lifecycle that doesn't govern the object's own entity (line 71). So the object holds no states for that lifecycle, and `in_state` answers **unknown** (null), with no note (`Expressions/ExprLowering.cs:171-176`). Rights read objects the same way (`Processes/RunRights.cs:196-200`). This already happens through a one-type end to a base whose derived entity is governed.
    - **Kept as it is,** through the new ends too: an object of a type the lifecycle doesn't govern gives unknown. No engine change, no note, and no replay changes. A test pins it through both kinds of end (section 17).
    - **What unknown means:** a guard is true only when its condition holds, so a guard reading `in_state(…)` or `not in_state(…)` is false for such an object (`Processes/ProcessExpressions.cs:763`). A signature's "Needed when" treats unknown as needed (`ProcessExpressions.cs:335, 699`), so such a signature is needed. The editor's help and the processes guide say so.
16. **An end with several types is held by every covered entity.** Confidence: high.
    - Rights' and trigger points' `ends`, a subject's `owner` end, and link steps (connect, disconnect, read, modify, freeze, thaw) name ends by id. The check "an end every subject holds" (MQ8102, MQ9903) reads the navigation sets of `ExprSites`, which now include an end held through any covered type.
    - A right or trigger point on such an end speaks for every type. Narrowing to one type waits (section 13).
    - Freezing keys a link on (end id, object id) (`Processes/StatechartInterpreter.cs:101`), so it needs no change.
17. **A subject's owner may sit at a to-one end with several types (MQ9606 unchanged).** The owner is the linked object, whatever its type. Confidence: high.
18. **A scenario object may link through such an end to an object of any covered entity.** MQ9305 says which types fit. Confidence: high.
    - **A connect step through an end with several types** checks that the connected object's entity is covered. Through a one-type end, a connect step keeps today's checks, which don't look at the connected object's entity (`Processes/VerbStepFields.cs:83-86`). So existing scenarios get no new finding.

### Resolution and templates

19. **Templates keep `end.entity` and `navigation.target` for one-type ends, and get a list for the others.** Confidence: high.
    - On an end with several types, `end.entity` is null and `end.types` lists its types (decision 5). A navigation that leads to one has a null `target` and lists `targets`.
    - `end.types` holds the entity alone on a one-type end, so a pack can always loop over it.
    - `end.common_base` is the nearest entity every type is, or derives from: the entity itself on a one-type end, a shared base when there is one, else null. A pack that stores links as one key to a shared base reads it.
    - Records (`get_resolved_model`) carry the same, and leave the new members out on a one-type end.
    - A relation with such an end appears in `entity.relations` of each type, and its navigations in `entity.navigations` of each type that holds them.
    - Section 7 gives the members.
20. **Every dependency key that follows an end's entity follows every covered entity instead.** Confidence: high.
    - For each end with several types: its entity entries key on the entity and its referrers, which cover derived entities; its stereotype entries on `cs:<key>`, the carrier-set key stereotype sets defined, which works for any key (`SubjectCoverage.CarrierSetHash`); and every such end, `anyEntity` included, on its types key `ec:<end id>`, a hash of its types in order and its common base, which a base change inside what the end covers or a rename that reorders `anyEntity`'s roots moves while every other key stays the same.
    - This applies to the relation and its ends, to a navigation (`ResolveRun.Conceptual.cs:781`), to an expression path through such an end (`ResolvedExpressions.cs:270-283`) and to a relative actor's path (`ResolveRun.Processes.cs:129`).
    - `entity.relations` and `entity.navigations` also key on the naming key `et:ends` (decision 35). Section 7 gives the details.
21. **Deletes and operations.** Confidence: high.
    - Deleting an entity removes its entries from every end's `types`, under either delete choice. One entity entry left is valid (decision 2). No entry left is today's "a relation that loses an end": delete-dependents deletes the relation, remove-references refuses. The same for deleting a stereotype and its entries, as the last subject of a named trigger point removes the point.
    - `get_references` on an entity or stereotype lists the entries, with pointers such as `/ends/1/types/0/entity`. `names-to-ids` and names in `apply_batch` already work through the generic reference walker.
    - `map-by-name`, `materialize-attributes` and `resync-binding` offer a relation's to-one far ends as field candidates (`Loading/Materializer.Bindings.cs:428-436`). They skip ends with several types, so they never write a field that MQ4116 refuses.
    - Seed columns leave such ends out (`Loading/SeedInput.cs:185-199`), so a seed never offers a column that MQ7105 refuses.

### Storage

22. **Part 1 stores nothing for a relation with an end of several types.** Confidence: high.
    - No table, column or foreign key, in any database. The database run skips it, as it skips a relation whose end entity isn't placed (`DatabaseRun.Relations.cs:40`).
    - Where every end has a covered type placed in a database, info MQ4115 says the database stores nothing for it.
    - A mapping of such a relation may only set `ignore` in part 1 (MQ4009).
    - Binding fields, query fields and query collections that would need one table for the end, and seeds of such links, are refused (MQ4116, MQ7105).
23. **No storage default for such a relation.** Confidence: high; question 4.
    - Today a one-type relation is stored by default once both its entities are placed in a database: a foreign key, or a junction table for many-to-many, with `relationsWithAttributes` deciding for a relation with attributes (`DatabaseRun.Relations.cs:37-69`). One shape is the natural one, so a default works.
    - For several types there are four valid ways to store, each with different integrity (decision 25). Picking one would be a model decision made through storage. So the mapping says which, and nothing is stored without it.
24. **(part 2) Which path part 2 serves first is the owner's question 2. Part 2's final design follows the answer.** Confidence: high that it is the owner's call.
    - **Path B, designed tables with explicit bindings (recommended):** the team's table files hold the columns, keys and checks, and the mapping only names them (decision 26). It invents nothing, and it fits the owner's standing rule: tables first, bindings explicit, no derived tables.
    - **Path A, tables the engine makes:** the engine adds the columns, keys and checks to the tables it makes from entity mappings (decision 27). One mapping line per relation, but the engine makes up columns and checks from conventions.
    - **Both paths share** decisions 25 and 28 to 32: the four ways to store, type codes, what the database can't enforce, joins, one-to-one uniques, migrations and the other packs.
    - The other path comes later (section 13). Part 1 doesn't depend on the answer.
25. **(part 2) The four ways to store.** A relation with an end of several types is stored in a database only through a mapping of it there. Without one, nothing is stored, and MQ4115 becomes a warning. The mapping gains **`typeStorage`**. `shape` keeps its meaning and may be left out, as today. Confidence: high.

    | The end with several types | `shape` | `typeStorage` | Where the link is held | What the database enforces |
    | --- | --- | --- | --- | --- |
    | Holds the key: it is the many end, or in a one-to-one the end the rules make dependent (the one with `min` 0 when the other has `min` 1; `foreignKeyEnd` only breaks a tie, `MappingRules.cs:371-385`) | `foreign-key` | not written (MQ4009) | Each type's table: a key column set and a foreign key to the other end's table | A foreign key per table, with the other end's delete rule |
    | Is referenced: the other end holds the key | `foreign-key` | `column-per-type` | The other end's table: a key column set and a foreign key per type, and a check that exactly one set is filled (the end's `min` 1) or at most one (`min` 0), each set all filled or all empty | A foreign key per type, with the end's delete rule; the check |
    | Is referenced | `foreign-key` | `type-and-id` | The other end's table: a type column and an id column | A check that the type is one of the end's type codes, and that both are set or neither; no foreign key |
    | Is referenced | `foreign-key` | `base-table` | The other end's table: one key column set and a foreign key to the table of the types' shared base | The foreign key; the type itself isn't checked |
    | Many-to-many | `junction` | `table-per-type` | A junction table per type | Foreign keys on both sides of each junction |
    | Many-to-many | `junction` | `type-and-id` | One junction: the other end's key, a type column and an id column. Its primary key is (the other end's key, type, id), or a surrogate id with that unique key when the relation allows duplicates or is ordered, as junctions are today | The other end's foreign key; the check on type codes |
    | Many-to-many | `junction` | `base-table` | One junction with a foreign key to the base's table | Foreign keys on both sides; the type isn't checked |

    - **`column-per-type` in a junction is not offered.** A unique key over nullable columns doesn't stop duplicate links in most databases.
    - **`promoted` is not offered** for such a relation (MQ4009).
    - **An `anyEntity` end is stored only as `type-and-id`** (the owner, 2026-11-08). `column-per-type` and `table-per-type` are refused there (MQ4117), since a new entity would add a column or a table. An end that names a stereotype may use any of the ways, since the team named the set.
    - **In each database, the end's types are the ones placed there.** A covered type with no placement is left out of that database's storage, with no finding; the Storage tab lists it, and says when a column comes from a carrier that gained the stereotype through a package default. When no type is placed, the relation isn't stored there, as today.
    - **`type-and-id`** needs every placed type's key to be one attribute of one built-in type and length (MQ4117 otherwise). **`base-table`** needs one entity that every placed type is or derives from, placed with TPH or TPT so its table holds every type's rows (MQ4117 otherwise); the nearest such entity is used.
26. **(part 2, path B) Designed and imported tables with explicit bindings: a sketch.** Confidence: medium-high; settled in part 2's design.
    - The team's table files hold the columns, foreign keys and checks, written by hand or imported from a database.
    - The relation's mapping states `typeStorage` and names what implements it: one existing foreign key per type for `column-per-type` and for a key on each type's table; one junction per type, with its end foreign keys, for `table-per-type`; a type column and an id column for `type-and-id`; today's `foreignKey` for `base-table`. This extends today's `foreignKey`, `junctionTable` and `ends`, which already name what exists for one-type relations.
    - An entity's binding maps the end's columns in its `fields`, so its read and write statements (`binding_sql`) carry them, and MQ4047 (every column accounted for) counts them. Part 2 lifts MQ4116 for binding fields.
    - Validation checks that each named key leads from the right table to the right type, that the id column fits the types' keys and the type column holds text, and that the table has a check for the chosen storage (info when it doesn't, with the check text to copy). Drift (`Model/BindingDrift.cs`) follows the named columns.
    - The engine writes no column. `sql-ddl` writes the table files as it writes any designed table.
    - **Waits:** engine-made tables for such relations, and materialize, which writes a projection's shape into table files and so needs path A's shapes.
27. **(part 2, path A) Tables the engine makes: a sketch.** Confidence: medium-high; settled in part 2's design.
    - The mapping's `typeStorage` alone decides. The engine adds the columns, foreign keys and checks to tables it makes from entity mappings.
    - **Checks** are plain SQL text, the same form in all five dialects, since expression-language checks are lowered to PostgreSQL only (MQ4056 elsewhere; `Resolution/ResolvedExpressions.cs:386-391`). Column per type, `min` 1: `(CASE WHEN "attached_to_part_id" IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN "attached_to_assembly_id" IS NOT NULL THEN 1 ELSE 0 END) = 1`, with `<= 1` for `min` 0 and, for a composite key, a clause that each set is all null or all set. Type and id, `min` 1: both columns not null and `"attached_to_type" IN ('Assembly', 'Part')`; `min` 0 adds "both null, or both set". Identifiers follow the database's quoting, codes are sorted ordinally, and the check is named by `checkName` with `{name}` set to the end's role.
    - **Names:** two new conventions, `typeKeyColumn` (default `{role}_{entity}_{key}`, such as `attached_to_part_id`) and `typeColumn` (default `{role}_type`); the id column uses `foreignKeyColumn` with `{key}` set to `id`; a junction per type uses `junctionTable` with each type in the end's place, such as `comment_part`.
    - **Keys** stay the same across renames: `<endId>.<entityId>.<keyAttributeId>`, `<endId>.type`, `<endId>.id`, `<relationId>.<entityId>@<databaseId>`.
    - **Refused for now (MQ4117):** a holding table that is designed, imported or bound; a reference-typed collection attribute beside `table-per-type`, since `sql-ddl` finds those through one junction. Materialize refuses an entity whose table holds such storage (MQ4055).
    - **Waits:** designed and imported tables, entity bindings and materialize.
28. **(part 2) A type code is the code of the object's own entity: the `discriminatorValue` of the entity's mapping in that database, else the entity's name.** This is the rule the TPH discriminator follows today (`DatabaseRun.Finish.cs:436`). Confidence: medium; question 5.
    - Every covered entity that can have objects (not abstract) and is placed in the database has a code, derived ones included, since an object's type column names its own entity.
    - Today `discriminatorValue` is refused on an entity outside an inheritance hierarchy (`Validation/MappingRules.cs:146`, MQ4009). It becomes allowed when a `type-and-id` storage in that database covers the entity.
    - Two covered entities with one code are refused (MQ4117), with the fix to set `discriminatorValue`.
    - The type column is text; an engine-made one has length 64, as the TPH discriminator (`DatabaseRun.cs:1026`). An integer code is written as its text.
29. **(part 2) What the database can't enforce is said once, as info MQ4118.** A `type-and-id` storage can't apply the end's delete rule, nor a composition's cascade, so the team's system applies it. A `base-table` storage can't refuse a link to a base row of a type the end doesn't list. Confidence: high.
30. **(part 2) Joins.** Today `navigation.joins[db]` and an entity mapping's `joins` hold one path of equal columns (`RJoinPath`, `ResolvedConceptual.cs:535-558`), built by `BuildJoins`, which matches on `navigation.From.Entity` (`DatabaseRun.Finish.cs:504-544`, line 524). Confidence: medium-high; the member names come with part 2's design.
    - A navigation to or from an end with several types gets one path per type: for `column-per-type`, `table-per-type` and a key on each type's table, a path of equal columns; for `type-and-id`, the id equality plus "the type column equals the type's code", which `RJoinPath` can't say today, so part 2 adds one member for it; for `base-table`, today's single path to the base's table.
    - `BuildJoins` uses coverage in place of `From.Entity`. Records and tests cover each storage.
31. **(part 2) One-to-one uniques work in every dialect.** Confidence: high.
    - Today a one-to-one adds a plain `UNIQUE` on the key columns (`DatabaseRun.Relations.cs:139-140`), and `sql-ddl` adds `NULLS NOT DISTINCT` on PostgreSQL only (`packs/sql-ddl/_sql.scriban:284`). PostgreSQL, MySQL, SQLite and Oracle let several rows leave the key empty under a unique key; SQL Server allows one such row.
    - With `column-per-type` every set is nullable by design, and so is `type-and-id` with `min` 0. Part 2 makes each of these a unique index filtered to rows where the key is set on SQL Server, and a plain unique key elsewhere. On path B, validation says when a designed table lacks it. A five-dialect golden pins it.
    - Today's optional one-to-one on SQL Server has the same limit; part 2 doesn't change it.
32. **(part 2) Migrations and the other packs follow the tables.** Confidence: high.
    - A carrier added to a stereotype end adds a column set or a junction, or rewrites a check, as any change to a table does. A type that leaves the end drops its column or junction, which needs a migration fact like any drop (MQ4102). A changed type code rewrites the check, so rows that hold the old code make the migration fail loudly rather than lose links quietly. A value-map fact for a type code comes later (section 13).
    - `csharp-dapper` already turns every unmapped column of an entity's table into an entity property (`storage_columns`, `packs/csharp-dapper/_dapper.scriban:101`), and a bound entity's repository uses its binding's statements, so the new columns reach it on either path. Section 10 lists each pack.

### The drawing

33. **The canvas follows the owner's agreed drawing.** Section 11 gives the rules and a sketch. The collapse toggle is the relation member's `collapsed` in the diagram file, and the position of its junction node or box is the member's `x` and `y`. Both exist today and edges ignore them (`schemas/v1/diagram.json`, `Model/Diagram.cs:42-62`), so the schema changes only its descriptions. Confidence: high.
    - Relation members exist in both kinds of diagram file. "All of a domain" becomes a diagram with membership `package` the first time someone arranges it (`canvas/domainDiagram.ts`), and its members include the relations. Toggling or moving a node in a view not yet saved creates that diagram, as dragging a card does today.
34. **The junction node starts as a small filled circle, and the owner sees it in the first browser pass.** A diamond is the UML mark of an n-ary relation, which this item doesn't draw. The owner described the node as "like an association with more than two ends", so the mark may change after that pass. Confidence: medium.

### Code generation and release

35. **Each output depends on what it needs, so a project pays only for what it uses.** Confidence: high.
    - The end coverage helper (section 7) is built once per snapshot, and only when some end has several types.
    - One naming key, **`et:ends`**, records which relation ends name a stereotype or any entity. A model without such ends never records it.
36. **Two parts, each a minor release.** Confidence: high, with question 6.
    - Part 1 is 0.21.0 and part 2 is 0.22.0. Part 1 starts from `main` once sign-off part 2 (0.20.0) is merged, since that branch changes `ExpressionRules.cs`, `ResolvedRecords.cs`, `Documents.cs`, `ModelIndexer.cs` and the MQ9305 and MQ8102 texts. Part 2 starts once part 1 is merged and question 2 is answered.
    - An older engine refuses a file with `types` or `anyEntity` (part 1), or with `typeStorage` and part 2's other keys. `formatVersion` stays 1.
    - Each pull request may be built and made green. The tags wait for the owner.

## 5. Schema and files

**Part 1**

| File | Key | Type | Notes |
| --- | --- | --- | --- |
| `relation.json` | end `types` (after `entity`) | list of `{ entity }` or `{ stereotype }`, `minItems` 1, `uniqueItems` | Decisions 1 and 2. Each entry has exactly one of the two. Title "End type" |
| `relation.json` | end `anyEntity` (after `types`) | `const: true` | Decision 1 |
| `relation.json` | end `required` | `["id", "role"]` and a `oneOf` of `entity`, `types` and `anyEntity` | The same pattern as a process subject entry. Today's files are unchanged |
| `diagram.json` | descriptions of a member's `x`, `y` and `collapsed` | | Decision 33. No new key |
| `openapi.yaml` | `EndRecord`, `NavigationRecord`, `RelationEndSummary`; the index format | | Section 9 |

```json
{
  "kind": "relation",
  "name": "AttachedTo",
  "relationKind": "association",
  "ends": [
    { "id": "…", "entity": "<id of Comment>", "role": "comment", "navigation": "comments", "max": "*" },
    {
      "id": "…",
      "types": [ { "entity": "<id of Part>" }, { "entity": "<id of Assembly>" }, { "stereotype": "controlledDocument" } ],
      "role": "attachedTo", "navigation": "attachedTo", "min": 1, "max": 1
    }
  ]
}
```

Each comment is attached to exactly one part, assembly or controlled document. `Part`, `Assembly` and every entity that carries `controlledDocument` get a `comments` navigation. `Comment.attachedTo` leads to one of them.

**Part 2** follows the owner's answer to question 2. On either path, `mapping.json` gains `typeStorage` (after `shape`): `type-and-id`, `column-per-type`, `table-per-type` or `base-table`, relation mappings only, and `openapi.yaml` gains the storage members of `RelationMappingRecord` and the join member. Path B adds mapping keys that name the existing foreign keys, junctions, type column and id column, and a binding field form for the type column. Path A adds the conventions `typeColumn` and `typeKeyColumn` to `maquettiste.json`. A path B mapping might read:

```json
{ "kind": "mapping", "name": "AttachedTo in main", "database": "<id of main>", "relation": "<id of AttachedTo>",
  "typeStorage": "type-and-id", "typeColumn": "<id of comments.attached_to_type>", "idColumn": "<id of comments.attached_to_id>" }
```

and a path A mapping only `"typeStorage": "column-per-type"`, with the engine adding `attached_to_part_id`, `attached_to_assembly_id`, a column per controlled document type, their foreign keys and the check.

Also updated in both parts: the sample app's schema copies, the editor files generated from `openapi.yaml`, and the recorded rule list, each by its fixed command.

## 6. Rules

| Rule | Sev | Part | Change |
| --- | --- | --- | --- |
| MQ3041 | E | 1 | **New.** An end with several types where it can't be: on an n-ary relation, on both ends of a relation, or `anyEntity` on a composition's child end. "Relation 'Link' is n-ary, so its end 'target' can't have several types; give it one entity." "Relation 'Link' has several types on both ends; keep them on one end." "End 'item' is the part in composition 'Holds', so it can't be any entity; list the types it may be." |
| MQ3042 | W | 1 | **New.** "End 'target' lists only entity 'Part' in types; write it as entity." Quick fix: use entity |
| MQ3043 | W | 1 | **New.** An entry another entry covers. "End 'target' lists entity 'PurchasedPart', which entity 'Part' already covers, since it derives from it; remove the entry." "End 'target' lists entity 'Contract', which carries stereotype 'approvable', also listed; remove the entry." |
| MQ2003 | E | 1 | Also an unknown key in an end's stereotype entry |
| MQ9211 | E | 1 | Also an end's stereotype entry whose stereotype doesn't apply to entities, as named trigger points already share it |
| MQ2020 | E | 1 | Also an end's entity entry from a package the relation's package doesn't depend on. Not a stereotype entry or `anyEntity` (decision 7) |
| MQ3009 | E | 1 | The holders of the other end's navigation are this end's types, a stereotype's carriers and every entity for `anyEntity` included, with their derived entities as today. Same messages |
| MQ3016 | E | 1 | A child end's covered entities each count; an owner end with several types counts as one owner. Same message, at the entry's pointer |
| MQ3116 | E | 1 | Through an end with several types: valid when the lifecycle can govern one covered entity. "Lifecycle Approval can't govern any type the object in_state reaches: Part, Assembly, or an entity derived from one." |
| MQ9604 | E | 1 | A path beyond such an end reads what every covered entity holds under one id (decision 13). Catalog text gains that clause |
| MQ9305 | E | 1 | A link fits when the target's entity is covered; a connect step through such an end checks the same (decision 18). "Object 'n1' links to 't1' through end 'AttachedTo.attachedTo', which leads to Part, Assembly, or an entity derived from one, not to entity 'Tool'; link to an object of one of those, or through another end." |
| MQ8102, MQ9903 | E | 1 | Same texts. The ends every subject holds now include ends held through a covered type |
| MQ7105 | E | 1 | Also an entity seed's column for an end with several types, and a seed of a relation with such an end. "Seed 'Notes' has a column for end 'attachedTo', which leads to several types; seeds can't hold such links yet." |
| MQ4009 | E | 1, 2 | Part 1: a mapping of a relation with an end of several types sets only `ignore`. Part 2: `typeStorage` missing where needed, written where the end holds the key or on a relation without such an end, `table-per-type` beside the foreign-key shape, `column-per-type` beside a junction, the `promoted` shape; `discriminatorValue` allowed on an entity a type-and-id storage covers |
| MQ4115 | I in part 1, W in part 2 | 1, 2 | **New.** "Relation 'AttachedTo' has an end with several types, so database 'main' stores nothing for it." Part 2 adds "; add a mapping for it that states typeStorage, or set ignore." Reported when every end has a covered type placed in the database |
| MQ4116 | E | 1 | **New.** A binding field, a query field or a query collection names an end with several types, or a navigation to one, which leads to more than one table (`schemas/v1/query.json`, `collections`). "Field 3 of the binding of 'Comment' names end 'attachedTo', which leads to several types; a field can't map it." Also, in part 1, a field or collection that names another end of such a relation, as a navigation from one of its types does, since nothing is stored: "Collection 0 of query 'PartNotes' names end 'comment' of relation 'AttachedTo', which has an end with several types; a collection can't fill it." A field of a collection's select list says "Field 0 of collection 1 of query …". Part 2's path B lifts it for binding fields |
| MQ4117 | E | 2 | **New.** A database can't store the relation as its mapping asks: type-and-id over keys that aren't one column of one type; column-per-type or table-per-type on an `anyEntity` end; two types with one code; no shared base held in one table for base-table; on path B, a named key or column that doesn't fit; on path A, a holding table that is designed, imported or bound, or a reference-typed collection attribute beside table-per-type |
| MQ4118 | I | 2 | **New.** What the database can't enforce for the chosen storage (decision 29). "Database 'main' stores end 'attachedTo' as a type and an id, so it can't apply its delete rule 'cascade'; the team's system applies it." |
| MQ4055 | E | 2 | Path A: also an entity whose table holds the storage of a relation with an end of several types |

Every new rule and new case needs an end with several types or `typeStorage`. The catalog descriptions of MQ2003, MQ2020, MQ3016, MQ3116, MQ4009, MQ4055, MQ7105, MQ9211, MQ9305 and MQ9604 grow, so `list_validation_rules` answers differently for every project, as in earlier items.

**Incremental validation,** as decision 12 says: entity, package and stereotype saves check again relations whose ends name a stereotype (MQ3009, MQ3016, MQ3043, MQ4115) and the scenarios that link through them (MQ9305); entity saves check again relations with an `anyEntity` end (MQ3009); mapping, database and settings saves check again every relation with an end of several types (MQ4115). Without such ends nothing extra runs.

## 7. Resolved model and templates

**`REnd`** (`end` in templates)

| Member | One-type end | End with several types |
| --- | --- | --- |
| `entity` | The entity, as today | null |
| `types` | The entity alone | The end's types (decision 5) |
| `has_several_types` | false | true (decision 2) |
| `stereotypes` | empty | The stereotypes its entries name, in entry order |
| `any_entity` | false | true for `anyEntity` |
| `common_base` | The entity | The nearest entity every type is or derives from, else null |
| `role`, `navigation`, `min`, `max`, `is_many`, `on_delete`, `ordered`, `opposite` | Today's | Today's |

**`RNavigation`:** `target` is null when the navigation leads to an end with several types; **`targets`** lists that end's `types` (the target alone otherwise). A navigation held through a several-types end is one object, listed in `navigations` of each type that holds it; its `from.entity` is null.

**`RRelation`:** unchanged. `cardinality` still follows `min` and `max`. A relation with such an end is listed in `entity.relations` of each of its types.

**Records** (`get_resolved_model`): `EndRecord` gains `types` (ids), `hasSeveralTypes`, `stereotypes` (ids), `anyEntity` and `commonBase`, each left out on a one-type end; `entity` may be null. `NavigationRecord` gains `targets`, left out on a one-target navigation; `target` may be null.

**The end coverage helper,** built once per snapshot in a weak table like `SubjectCoverage`, and only when some end has several types: each such end's covered set, its types, its holders and its common base. Carriers are worked out by the same rule as `SubjectCoverage` (`Processes/SubjectCoverage.cs`), without adding relation ends to that class's process index, so `sn:subjects` never changes.

**Dependency keys** (decision 20)
- **The relation, its ends, its navigations, expression paths through it and relative actor paths through it** key on each entity entry and its referrers, `cs:<key>` per stereotype entry, and the end's types key `ec:<end id>`: a hash of `EndCoverage.TypesOf` in order and `CommonBaseOf`. `ImpactKeys` fires it when the change writes an entity, package, stereotype or relation and the hash differs; the plan explainer words it "The types of relation end <id>".
- **`et:ends`,** the naming key: hashes, for each relation with an end that names a stereotype or any entity, the relation's id, the end's id, its stereotype keys in entry order and its `anyEntity` flag.
- **`entity.relations` and `entity.navigations`,** while such an end exists, also key on each such end's `ec:` key, on `et:ends` and on `cs:` of each named key. A new `anyEntity` relation, or an entity gaining a named stereotype, renders them again. Without such ends their keys don't change.
- **The planner** hashes `et` from the model alone. `Planning/ImpactKeys.cs` needs an `et` case that fires only when the change writes a relation and the hash differs, as `rn` and `tn` do (lines 114-117), with a no-change test; without it, the switch's `_ => true` would report units as rendering again on every dry run. The plan explainer words it "The relation ends that name a stereotype or any entity".

**Part 2: `RRelationMapping`** (`relation.mappings[db]`). A sketch, settled with question 2: `type_storage`; one entry per placed type in type order with its `entity`, its foreign key or junction table; `type_column` and `id_column` for `type-and-id`; `type_codes`, each covered entity that can have objects with its `code`, by qualified name, then id; `base_entity` for `base-table`; the check; and `left_out`, the covered types not placed in the database. `RelationMappingRecord` gains the same, as ids and keys, each left out when unused. The tables, columns, foreign keys and checks are ordinary physical objects, so a pack that writes DDL from tables needs nothing more. A pack that stores links its own way reads the part 1 members.

## 8. Simulation and scenarios

- **Links.** A scenario object's link through an end with several types fits when the object holds the end (its entity is covered by the other end) and the target's entity is covered by this end. `ScenarioObjects.cs:183-196` and `VerbStepFields.cs:84-87` read the end coverage helper in place of the entity chain for such ends, and keep today's checks for one-type ends.
- **Link steps** (connect, disconnect, read, modify, freeze, thaw) work as today. Rights, trigger points and freezing key on the end's id. A connect step through such an end checks the connected object's type (decision 18).
- **Guards and set values** read the target object's row, built from the object itself (`RowOf`), and the typer allows only what every type holds (decision 13).
- **`in_state`** through such an end: unknown for an object of a type the lifecycle doesn't govern, as today through a one-type end, with no note (decision 15).
- **Owner and actors from the object:** the owner is the linked object, whatever its type. A path beyond the end reads only common members.
- **Traces, audits, recording and replay** keep their shapes and texts. A model without such ends replays as today.

## 9. Tools and API

- **No new tool.** Tools take documents and answer with records, so they follow the schema and the records.
- **Part 1:** `get_resolved_model`, `get_template_context` and `preview_expression` show the new members and scope. `get_references` and `delete_element` handle entries (decision 21). `apply_batch` takes qualified names in `types[].entity`, as in any reference. `simulate_process`, `record_scenario` and `verify_scenarios` accept links through such ends. `get_model_index` rows gain the end summary fields.
- **Part 2:** `get_database_view` and the database views show each type's storage and what is left out; `plan` and `get_plan_diff` show the tables as usual; on path B, `get_binding_drift` follows the named columns.
- **`openapi.yaml`, part 1:** `EndRecord` and `NavigationRecord` as section 7. `RelationEndSummary` makes `entity` optional and gains `entities` (ids of entity entries), `stereotypes` (ids), `anyEntity` and `covers` (the carriers of its stereotypes, for the explorer), each left out when unused. The index format moves to `maquettiste-index/e14` (`Editor/ModelReads.cs:23`). `RelationEndSummary` gains list members, so its equality must compare them by value, since `IndexRow` equality relies on it (`Model/Documents.cs:242`).
- **`covers` is filled after the rows,** since each index row is built from its own file (`ModelIndexer.cs:1084-1086`). `WithSubjectCoverage` (`ModelIndexer.cs:991-1037`), which fills a process row's `covers` today, also fills each relation end's, in the same order: per entry, by qualified name, then id. Its early return (lines 994-995) also checks for relation ends that name a stereotype.
- **Part 2:** `RelationMappingRecord` and the join member as section 7; on path A, the conventions in the settings schema.
- **Tool texts:** `ModelTools.cs`, `ProcessTools.cs`, `Agent/tools.json` and the Functions endpoints, where they describe relation ends and mappings. The conventions resource (`McpServerSetup.cs`), the modeling skill (`skills/maquettiste-modeling/SKILL.md`) and the assistant's prompt describe `types`, `anyEntity` and, in part 2, `typeStorage`.
- **The editor's mock server** (`src/editor/src/mocks/model/`) re-implements the rules and must change in step. Part 1: `validate.ts`, `refs.ts`, `cascade.ts`, `modelIndex.ts`, `store.ts` (its index rows, line 1631), `rightRules.ts`, `rightsCheck.ts`, `triggerRules.ts`, `triggerRun.ts`, `relativeRules.ts`, `stepper.ts`, `queries.ts` and `bindings.ts` (MQ4116), `seed.ts` and `processSeed.ts` (MQ7105), `physical.ts` (skip such relations, MQ4115). Part 2: `physical.ts` and `bindings.ts` (the ways to store on the chosen path), `validate.ts` (MQ4009, MQ4117, MQ4118).

## 10. Packs

Every change depends on an end with several types or `typeStorage`, so today's output is the same bytes.

| Pack | Part 1 | Part 2 |
| --- | --- | --- |
| `sql-ddl` | Nothing to write: the relation has no table, column or key. Its one relation loop, `ref_collections` (`_reference.scriban:582`), reads a mapping's junction table, which such a relation lacks. Golden test: output unchanged | No template change: tables, columns, foreign keys, checks and filtered unique indexes come from the physical model, designed or engine-made (`_table.scriban:42` writes `$t.checks`). Golden tests in all five dialects for each way to store, the one-to-one uniques, and migration tests for a type added and removed |
| `csharp-dapper` | No change: it generates no navigations (README, "Not covered"), and rights and trigger points print ends by name | No template change: the new columns become entity properties through `storage_columns`, or come through a bound entity's binding statements, and the repository writes them. The README says the team's code sets the type column or the one key column, and the database check guards them. A build test with warnings as errors |
| `process-docs` | No change: it prints ends by name (`_docs.scriban:87-96, 495-500`) and objects by entity. A golden test with a right on such an end and a scenario link through it | No change |
| `seed-data` | Seeds of such links are refused (MQ7105) | The same |
| `samples/typescript-pack` | It reads `nav.target.name` (`entity.scriban:31, 55-61`; `schema.scriban:47`) and would write broken code for a null target. It writes a union type from `nav.targets` (`attachedTo?: Part \| Assembly`), `unknown` for any entity, and no `…Id` property for such a navigation | No change |

A pack outside this repository that reads `end.entity` or `navigation.target` should check `end.has_several_types`. The release notes and `packs/README.md` say so.

## 11. Editor

**The relation's form** (`inspector/fields.tsx`, `EndEditor` and the ends list)
- Each end has a **Type** choice: **One entity**, **Several types** or **Any entity**. Several types shows the entries as chips, in file order, with a picker that lists entities, then a **Stereotypes** group with those that apply to entities. A chip has a remove button with a tooltip.
- Once one end has several types, the other end's choice keeps only One entity, with the tooltip "Only one end may have several types." An n-ary relation keeps only One entity: "An n-ary relation's ends name one entity each." A composition's child end leaves out Any entity.
- Going back to One entity with one entity entry writes `entity`. With more, the person picks one.
- MQ3042 and MQ3043 have quick fixes (`problems/quickFix.ts`): use entity, remove the entry.
- The **Storage** tab: part 1 says "No database stores a relation with an end of several types." Part 2 shows, per database, **Type storage** with the four choices, each with one line saying what it needs or makes, the columns, keys and check involved, what the database can't enforce (MQ4118), the types left out, and why a column appeared when a carrier gained the stereotype.

**The canvas** (`canvas/model.ts`, `RelationEdge.tsx`, `workspaces/entities/EntitiesWorkspace.tsx`, and new node types). The three looks the owner agreed, with `Comment` at the end that names one entity:

```
Two types: bundled lines

+---------+  AttachedTo            attachedTo 1  +----------+
| Comment |------------------+-------------------| Part     |
+---------+ comment *        |                   +----------+
                             |     attachedTo 1  +----------+
                             +-------------------| Assembly |
                                                 +----------+

Three or more: a junction node

+---------+  AttachedTo             attachedTo 1  +----------+
| Comment |--------------------(o)----------------| Part     |
+---------+ comment *           |\                +----------+
                                | \ attachedTo 1  +----------+
                                |  +--------------| Assembly |
                                |                 +----------+
                                |   attachedTo 1  +---------------------------+
                                +-----------------| controlledDocument        |
                                                  | (stereotype), 3 entities  |
                                                  +---------------------------+

Collapsed by the toggle: one line to a list

+---------+  AttachedTo             attachedTo 1  +---------------------------+
| Comment |---------------------------------------| Part                      |
+---------+ comment *                             | Assembly                  |
                                                  | controlledDocument        |
                                                  |   (stereotype)            |
                                                  +---------------------------+
```

- **Starting look,** counted in the end's entries:
  - **One stereotype entry, or any entity:** one line from the other end's card to a box: "approvable (stereotype)" with the number of entities it covers, or "Any entity".
  - **Two entity entries, both cards on the canvas:** bundled lines. One line leaves the other end's card with the relation's label, and forks into a line to each type card.
  - **Three or more entries, or two where one is a stereotype or isn't a card:** a junction node. One line runs from the other end's card to the node, and one from the node to each type card or box.
- **Each line shows its end's role and cardinality,** with today's notation (crow's foot or UML).
- **The toggle** (on the relation's label, the node and the edge's context menu, each with a tooltip: Collapse lines, Expand lines) collapses the lines into one line to a box listing the entries, and back. It is offered when the end has two or more entries. It saves the relation member's `collapsed`.
- **The node or box** sits at the relation member's `x` and `y`. Dragging it saves them. Without them, it sits between the other end's card and the types. Auto-layout places it again.
- **A type not on the canvas** gets no line; the node or box says "+2 not shown", with their names in a tooltip.
- **Which relations show** in a domain view (`viewElements`, `canvas/model.ts:55`): the other end's entity is a card, and the end with several types has an entity entry that is a card, or names a stereotype or any entity.
- **Unchanged:** a one-type relation's drawing, and the n-ary limit (the first two ends only).
- **Exports** (`canvas/export.ts`) include the nodes and boxes. Auto-layout members added near a relation (`canvas/model.ts:258-270`) walk the entity entries.

**Elsewhere:** a shared helper (`model/endTypes.ts`) answers "does this end cover this entity" and "what is this end's label" from index rows. These files use it:
- labels: the explorer's relation labels ("Comment → Part, Assembly or controlledDocument (stereotype)", `explorer/tree.ts:363`) and the entity editor's (`editors/EntityEditor.tsx:505`);
- an entity's relations (`editors/related.ts`; through a stereotype says "through approvable (stereotype)"; an any-entity relation says "any entity");
- the client's index rows (`model/model.ts:240`) and the References tab (`references/data.ts:19-21`, which today matches `/ends/i/entity` only);
- path and end pickers: `model/objectFields.ts`, `editors/actor/useObjectFields.ts`, `editors/process/RightsTab.tsx`, `TriggerPointsTab.tsx`, `WhoCanDo.tsx`, `WhatHappensWhen.tsx`, `editors/triggerPoint/TriggerPointEditor.tsx`;
- scenario link pickers (`editors/scenario/ScenarioEditor.tsx`);
- seeds: `workspaces/reference-data/seedTargets.ts` and the seed grid (`workspaces/reference-data/rowsModel.ts:505-540`), which leave such ends out of the columns;
- databases: query parts (`editors/database/QueryParts.tsx`, which hides such navigations), the junction owner in conventions (`workspaces/settings/DatabaseConvention.tsx:206`), and the storage views (`editors/storage/useStorage.ts:32`, `fieldMap.ts:159`, `RelationStorage.tsx`, `relationStorage.ts`).

Labels use plain words; no product names.

## 12. A project that uses none of this

**After part 1:**
- **Files:** nothing rewrites a file. `entity` keeps its bytes, and the canonical writer writes `types` and `anyEntity` only when set. Saves, `format`, `format --check` and snapshot import behave as today. Diagram files are untouched; a relation member's `x`, `y` and `collapsed` mean something only for a relation with such an end.
- **Output:** the same bytes. Every resolver path that reads an end's types takes today's branch on a one-type end, and the end coverage helper is empty. **Pinned by** the pack golden tests, which pass untouched, and a resolver test that a model without such ends gives the same resolved records.
- **Findings and messages:** the same, word for word. New rules and new cases need an end with several types; connect steps through one-type ends keep today's checks. **Pinned by** the existing rule tests, a scenario test that connects an object of another type through a one-type end and gets no new finding, and the recorded rule list, whose descriptions grow (section 6).
- **Dependency keys:** the same. `et:ends` and the new `cs:` reads are recorded only while such an end exists. **Pinned by** a key test on a model without such ends, and the `ImpactKeys` no-change test.
- **Simulation and replay:** the same outcomes, traces and notes; `in_state` keeps its answers. **Pinned by** the replay goldens and the `in_state` test of section 17.
- **Tool answers about the model:** the same; new record and index members are left out when unused. Some answers change for every project, as in earlier items: `list_validation_rules`, the tool descriptions of `tools/list` (`ModelTools.cs`, `ProcessTools.cs`), the conventions resource, and `get_schema relation`.
- **Speed:** the same. The helper, the extra checks and the extra keys run only with such ends. **Pinned by** the bench and its baseline (`bench/baseline.json`).
- **Once, as on any upgrade:** every unit renders again with the same bytes, since the engine version is part of each unit's key; the `.schema` copies refresh (MQ1008 until then); the index cache is read in full once (format `e14`).

**After part 2:**
- **A project that uses part 1 but no `typeStorage`** keeps its files, output, findings and keys, except that MQ4115 becomes a warning.
- **A project that uses neither part** keeps everything above. New keys and conventions have defaults and change no table that doesn't use them. One-to-one uniques change only for storage of an end with several types.

## 13. What comes next

| Item | What it takes from this |
| --- | --- |
| The part 2 path not picked first | Path B needs path A's shapes for materialize; path A needs path B's naming keys for designed tables. Decisions 26 and 27 sketch both |
| Materialize of such storage | Writes path A's columns, keys and checks into table files, and the mapping keys of path B |
| Seeds of such links | A seed column names the type and the key. Lifts MQ7105's new case |
| A query through such an end | A query names which type it joins, using part 2's joins. Lifts MQ4116 for queries |
| A stereotype's attributes through a navigation | The item stereotype sets left open ("by navigation, not yet"): nested rows in `RowOf`, the resolved path target, pack behavior. For every navigation at once, not only these ends |
| Several types on both ends | Holders from both ends; a junction with two type-and-id pairs, or one per pair of types; two nodes on the canvas |
| Several types on an n-ary relation | The canvas first draws every end of an n-ary relation |
| `exceptEntities` on an end | "Any entity except Comment", as named trigger points have it. An optional key |
| A right or trigger point narrowed to one type of an end | A narrowing key. Until then they speak for every type |
| A database-wide storage default | A convention for `typeStorage`, if teams find one mapping per relation too much |
| A value-map fact for a renamed type code | Code maps exist (`Migrations/CodeMap.cs`), so a rename could rewrite the type column's rows instead of failing the migration |

## 14. What the independent check changed

- **A stereotype's attributes through a navigation.** The first draft let an end that names one stereotype offer its attributes. The runtime can't read them (nested rows carry none), and it contradicted stereotype sets. Dropped; listed in section 13.
- **`in_state`.** The first draft said an ungoverned object falls back to the initial state and proposed making the answer false. The code gives unknown, with no note, because `ScenarioObjects.States` skips lifecycles that don't govern the object. Kept as it is, also through the new ends; a test pins it, and decision 15 says what unknown means for `not in_state(…)` and "Needed when".
- **Part 2's path.** The first draft built part 2 on tables the engine makes and put the team's designed tables "later". That is now the owner's question 2, with both paths sketched and designed tables recommended. Joins per type and one-to-one uniques on SQL Server joined part 2, and its estimate went up.
- **The limits.** Their reason is now cost (drawing and build), not meaning, and the question is split into the limits and what waits.
- **Connect steps.** The new type check applies to the new ends only, so existing scenarios get no new finding.
- **Stale results.** MQ4115 is checked again on mapping, database and settings saves; the index's `covers` comes from the existing per-snapshot pass; dependency keys follow every covered entity for navigations, guard paths and relative actors.
- **Stale output after the build (review).** The first build keyed `anyEntity` on `k:entity` and `k:package`, which hash id sets only, and a stereotype entry on `cs:<key>` alone. Moving an entity under another base inside what the end covers, or renaming a package under `anyEntity`, changed the types but no key. Each end with several types now has its own types key `ec:<end id>`.
- **Missing readers.** Six editor and mock files, the seed grid and seed columns, and the field candidates of map-by-name, materialize-attributes and resync-binding. "Merging entities" is gone: there is no such operation.
- **Definitions.** "An end with several types" is defined once (decision 2), and the type order can no longer list an entity whose base is covered (decision 5).
- **Accuracy.** The `.Ends` count (145 in 33 files), the stereotype-sets quote, the compiler claim (a grep step is added), the one-to-one holding end (`min` first, `foreignKeyEnd` breaks ties), composite keys, the junction's primary key, query collections, part 1 starting after sign-off part 2 merges, the entry record mirroring `TriggerPointSubject`, and the answers that change for every project.
- **The drawing.** A sketch of the three looks is in section 11. The junction node's mark is shown to the owner in the first browser pass rather than settled here.

## 15. The owner's decisions

The owner answered all six questions on 2026-11-08, each as recommended, and said part 1 can be built from this design.

1. **Names: kept.** `types` is a list of `{ entity }` or `{ stereotype }` entries, the same entry a named trigger point uses, and an end writes exactly one of `entity`, `types` or `anyEntity`. A list that holds one entity means the same as `entity`, and MQ3042 offers the rewrite. Each entity entry covers that entity and its derived entities. A stereotype entry is part of part 1, since the agreed drawing draws it as one line to a box. `anyEntity` stays its own key. `typeStorage` is `type-and-id`, `column-per-type`, `table-per-type` or `base-table`.
2. **Part 2 serves path B first:** the team's designed and imported tables, with a mapping that names the existing keys and columns. The engine adds no column. Path A and materialize wait.
3. **Limits.**
   - (a) **Accepted.** Several types are allowed on one end of a two-ended relation. An n-ary relation is refused (MQ3041), which matches a canvas that draws only the first two ends. A composition's child end may list types, and may not be `anyEntity`.
   - (b) **Accepted.** The other path, materialize, seeds, queries, both ends, n-ary relations, a stereotype's attributes through a navigation, and narrowing a right or a trigger point all wait. Part 1 refuses a seed or a field that would need one table (MQ7105, MQ4116).
4. **No storage default: yes.** Part 1 stores nothing, and MQ4115 is info. In part 2 the same finding becomes a warning until a mapping states the storage or sets `ignore`, so a model that already uses part 1 gets that warning on the upgrade to 0.22.0. When the end with several types holds the key, the mapping doesn't set `typeStorage`: it names one foreign key on each type's table (decision 25). That is the natural shape, not a fifth value, and nothing is stored until that mapping exists.
5. **Type codes: the name, with collisions refused.** The stored code is the `discriminatorValue` of the entity's mapping in that database, otherwise the entity's name, the rule a shared-table discriminator already follows. Two covered entities with one code are refused (MQ4117). No second code scheme, such as qualified names, is added.
6. **Releases: two.** Part 1 is 0.21.0, after sign-off part 2 is merged. Part 2 is 0.22.0.

**One constraint for part 2's design, from the owner:** `column-per-type` and `table-per-type` are refused on an `anyEntity` end (MQ4117), since a new entity would add a column or a table. `type-and-id` is the way such an end is stored. An end that names a stereotype may still use the other ways, since the team named the set.

## 16. New names

- **In the format, part 1:** the end keys `types` and `anyEntity`, and the end type entry `{ entity }` or `{ stereotype }` (schema title "End type").
- **In the format, part 2:** the mapping key `typeStorage` and its values `type-and-id`, `column-per-type`, `table-per-type` and `base-table`. Path B: mapping keys naming the existing keys, junctions, type column and id column, and a binding field form for the type column. Path A: the conventions `typeColumn` (default `{role}_type`) and `typeKeyColumn` (default `{role}_{entity}_{key}`), and the physical keys `<endId>.<entityId>.<keyAttributeId>`, `<endId>.type`, `<endId>.id` and `<relationId>.<entityId>@<databaseId>`. Part 2's design lists them exactly.
- **Rules:** MQ3041, MQ3042, MQ3043, MQ4115, MQ4116; part 2: MQ4117, MQ4118.
- **Template members:** `end.types`, `end.has_several_types`, `end.stereotypes`, `end.any_entity`, `end.common_base`, `navigation.targets`; part 2: the storage members of a relation mapping and the join member of a navigation (section 7).
- **API:** `EndRecord.types`, `.hasSeveralTypes`, `.stereotypes`, `.anyEntity`, `.commonBase`; `NavigationRecord.targets`; `RelationEndSummary.entities`, `.stereotypes`, `.anyEntity`, `.covers`; the index format `e14`; part 2: the storage members of `RelationMappingRecord` and the join member.
- **In the engine:** the naming key `et:ends`; the end coverage helper; the entry record mirroring `TriggerPointSubject`.
- **In the editor:** Type, One entity, Several types, Any entity, Stereotypes, Collapse lines, Expand lines, "+N not shown", "through *key* (stereotype)", "any entity"; part 2: Type storage, A type and an id, A column per type, A table per type, A key to the shared base table, Not in this database.
- **Names a builder adds** follow these patterns and are listed in the pull request and the release notes for the review to settle.

## 17. Tests

**Part 1**
- **`RelationTypesRuleTests` (new):** failing and passing cases for MQ3041 (n-ary, both ends, any-entity child), MQ3042, MQ3043 (derived beside base, carrier beside its stereotype), MQ2003 and MQ9211 on entries, MQ2020 on entity entries only, MQ3009 for listed types, carriers, derived entities and any entity, MQ3016 (owner end with several types counts once; child end types; a carrier already owned elsewhere), MQ4009 in part 1, MQ4115, MQ4116 (binding field, query field, query collection), MQ7105 (seed column, relation seed). Decision 2's test: a one-entity list is a one-type end; `[Part, PurchasedPart]` and a stereotype with one carrier are ends with several types. Today's models give the same findings and messages.
- **Expressions:** a guard through such an end reads a shared base's attribute; a stereotype's attribute is not offered through it, as through any navigation; a to-many end is refused as today; `in_state` valid with one governable type, MQ3116 with none; an owner at such an end; an actor path beyond it (MQ9604).
- **`in_state` answers, pinned:** a replay through an end with several types to an object whose type the lifecycle doesn't govern gives unknown, with no note; the same through a one-type end to a base object whose derived entity is governed, unchanged; `not in_state(…)` in a guard is false for both; a signature whose "Needed when" reads it is needed.
- **Processes and scenarios:** links that fit and don't (MQ9305 text); connect, disconnect and freeze through such an end; a connect through such an end with an object of another type (MQ9305), and through a one-type end with no new finding; rights and trigger point ends held through a covered type (MQ8102, MQ9903); today's replays unchanged.
- **Resolver:** `REnd` members for each form; the type order (file order, carriers by qualified name, a carrier derived from an entity listed later left out, any entity's roots); navigations on each type and on derived entities; `entity.relations`; `common_base`; records left out on one-type ends.
- **Stale output:** a carrier gaining a named stereotype renders again a unit that reads `entity.navigations`, a guard's resolved path through the end, and a relative actor's path through it; a new any-entity relation renders `entity.navigations` again; a relation edit that doesn't touch such ends renders nothing new; a model without such ends keeps its keys; the `et` case of `ImpactKeys` with its no-change test. Each stale test is checked to fail without its fix.
- **Stale findings:** MQ4115 appears and goes when a mapping places a carrier, when a database's `byConvention` or packages change, and when the settings change.
- **Index:** the new row fields, `e14`, `covers` filled by `WithSubjectCoverage` in order and refreshed when carriers change, `IndexRow` equality with the new lists.
- **Deletes and operations:** an entity entry removed under both choices; one entity entry left; the last entry (relation deleted, or refused); a stereotype deleted; `get_references` pointers; map-by-name, materialize-attributes and resync-binding skip such ends; seed columns leave them out.
- **Packs:** a golden model with such a relation for each pack: `sql-ddl` and `csharp-dapper` unchanged, `process-docs` pages, the TypeScript sample's union. The existing goldens pass untouched.
- **Editor:** unit tests for the end helper, the form's conversions, which relations show, the starting look, the toggle and positions, the References tab pointers, the seed grid; one end-to-end test that makes an end several types, collapses it and reloads.
- **Acceptance,** on an invented model: comments attached to parts, assemblies or controlled documents, an approval lifecycle over `approvable` with a right on the link's end, and a scenario that links and replays.

**Part 2,** for the chosen path
- **`RelationTypeStorageTests` (new):** each way to store in each cardinality decision 25 allows, over TPH, TPT and TPC placements; MQ4009 misfits; MQ4115 as a warning; MQ4117 cases; MQ4118 for each case; codes from `discriminatorValue` and names, sorted; a type not placed left out; a TPH-derived holder forced nullable as today; on path A the check texts byte for byte; on path B the named keys and columns, the binding fields and drift.
- **Joins:** the per-type paths for each storage, records, and `BuildJoins` from an end with several types.
- **Uniques:** one-to-one in each storage, in all five dialects, with the SQL Server filtered index.
- **Stale output:** a carrier added renders its table again; a `discriminatorValue` changed renders the check again; a model with no `typeStorage` keeps its keys.
- **Migrations:** a type added and removed, in each way to store; a code changed.
- **Packs:** `sql-ddl` goldens, create and migrate, in five dialects; `csharp-dapper` build with warnings as errors; `seed-data` refusal.
- **Editor:** the Storage tab and the mock's physical model.
- **No change** for a project that uses part 1 only, beyond MQ4115's severity.

## 18. Tasks and the build plan

### Tasks

The sizes are days for a person who knows the code, with tests and a review.

**Part 1**

| Wave | Group | What | Size |
| --- | --- | --- | --- |
| 1 | Contract and model | `relation.json`, `diagram.json` texts, `openapi.yaml`, rule texts; `RelationEnd` with `Types` and `AnyEntity`, the entry record; the end coverage helper; `et:ends`; compiling every reader with today's behavior for one-type ends | A day and a quarter |
| 1 | Editor, part one | The relation form, the end helper, the explorer, related lists, labels, the References tab and the seed grid, quick fixes; the mock's validation, references, cascade, store and index | Two days |
| 2 | Rules, expressions and processes | `CheckRelation` and the composition index, MQ3041 to MQ3043, MQ2003, MQ2020, MQ9211, MQ4009, MQ4115, MQ4116, MQ7105; `NavigationsOf`, `AddNavigations`, `in_state` typing; `RelativeActors`, `ScenarioObjects`, `VerbStepFields`; incremental validation, mapping and settings included | Two days |
| 2 | Resolver, index, deletes and records | `BuildRelation`, navigations, `entity.relations`, every dependency key of decision 20 and `ImpactKeys`; `ResolvedRecords`; the index, `covers` and `e14`; the cascade, field candidates and seed columns; tool texts; the TypeScript sample; pack goldens | Two days |
| 2 | Editor, part two | The canvas looks, the toggle, positions, which relations show, export; the pickers; the mock's rights, trigger and stepper rules | Two days |
| 3 | Docs | User guide (relations, and what unknown means for `in_state`), a new erratum, releases, modeling skill, conventions resource, `next.md`, section 11 of the lifecycle note | Half a day |

That comes to about nine and three quarter days for a person.

**Part 2,** on either path

| Wave | Group | What | Size |
| --- | --- | --- | --- |
| 1 | Contract and engine | `mapping.json` and the path's keys, `openapi.yaml`, rule texts; the four ways to store; on path B the naming keys, binding fields, validation and drift, on path A the resolver's columns, keys and checks; codes; `MappingRules` | Two and a half days |
| 1 | Editor | The Storage tab, the database views, the mock's physical model and bindings | A day and a half |
| 2 | Joins, uniques, readers, tools and migrations | `BuildJoins` and the join member; the dialect uniques; `PhysicalRules`, `BindingViews`, `DatabaseTables`, `DatabaseViews`, `DatabaseReach`, `CodeHolders`, records; migration tests | A day and a half |
| 2 | Packs | `sql-ddl` goldens in five dialects, `csharp-dapper` build, READMEs | A day |
| 3 | Docs | User guide (storage), releases, `next.md` | A quarter day |

That comes to about six and three quarter days for a person.

**Checklist for every group.**
- **Every reader of `RelationEnd.Entity`,** from the compiler once it turns optional, the list in section 2, and a grep for what the compiler misses: `.Entity` beside `Ends`, `end.Entity`, `e.Entity`, `string.Equals(…Entity…)`, `==` and `ReferenceEquals` on an end's entity (`ResolveRun.Conceptual.cs:742`, `DatabaseRun.Finish.cs:524`), `.entity` in Scriban templates under `packs/` and `samples/`, and `.entity` on ends in `src/editor/src`. A reader that only reads end ids (rights, trigger points, freezing, traces) needs no change.
- **Every place that asks "does this end lead from or to this entity":** `NavigationsOf`, `NavigationHolders`, the composition index, `ScenarioObjects`, `VerbStepFields`, `SeedInput`, `Materializer.Bindings`, `ReferenceDataRules`, `DatabaseRun.Queries.FieldTarget`, the binding field checks, `BuildJoins` (part 2), and their twins in the editor and the mock. Each calls the end coverage helper.
- **Every dependency key of decision 20:** the relation, navigations, expression paths, relative actor paths, `entity.relations` and `entity.navigations`.
- **New members:** `Resolution/ResolvedConceptual.cs` (`REnd`, `RNavigation`, part 2's `RRelationMapping`), `Editor/ResolvedRecords.cs`, `docs/api/openapi.yaml`, `Model/Documents.cs` (`RelationEndSummary`, with value equality).
- **Keys:** `Planning/DependencyHasher.cs` (`et`), `Planning/ImpactKeys.cs`, `Generation/PlanExplainer.cs`.
- **Tools:** `src/Maquettiste.Cli/Mcp/ModelTools.cs`, `ProcessTools.cs`, `Agent/tools.json`, `src/Maquettiste.Functions/_functions/`, each owned by one agent per wave.
- **Contracts:** the schema copies, `openapi.yaml` and its generated files. `validation-rules.json` is written only by the fixed command.
- **The editor:** `npm run typecheck` (`tsc -b`) and `npm run lint` before each push.

### How it gets built

**What earlier items cost.** Sign-off part 1, estimated at about 10,000 to 11,000 lines, came to 1.21M output tokens, 4.30M new input and 247M cached re-reads in about 3.5 hours. Sign-off part 2, about 4,500 lines, came to 1.04M, 3.42M and 194M in about 3 hours, above its estimate of 0.75M, 2.6M and 170M. So a part costs about 150M cached before its size counts, and estimates have run low on smaller parts. The figures below follow the independent check. In every item, the review and the browser pass each found real bugs.

**This item's size,** with tests, without pack expected files.
- **Part 1, about 7,500 to 8,500 lines:** contract and model about 550; rules, expressions and processes about 1,000 and their tests about 1,150; resolver, index, deletes, records and keys about 850 and tests about 850; editor form, helper, labels and pickers about 1,150; canvas about 950; mock about 500; editor tests about 450; samples and pack goldens about 200; docs about 400. The main risks are the reader sweep (every picker that asks which ends an entity holds) and keeping replays unchanged.
- **Part 2, about 5,000 to 6,000 lines,** on either path: contract about 300; engine (path B's naming, bindings, validation and drift, or path A's resolver) about 1,200 and tests about 1,100; joins and uniques about 300 and tests about 250; readers, tools and records about 400 and tests about 300; editor and mock about 1,000 and tests about 250; pack tests about 300; docs about 300. The main risk is the four ways to store across inheritance placements. Part 2's final estimate follows the owner's answer to question 2.

**Agents.** Each is capped at about 100 steps or 200k context. Opus builds the engine and the pack output and runs every review. Sonnet builds the editor, the mock and the docs, and runs the browser passes. **At most three agents run at once.** Agents work in their own worktrees, fast-forwarded to the branch, and their work is merged by patch, as in earlier items.

**Part 1 agents**

| Wave | Agent | Model | Steps | Starts from |
| --- | --- | --- | --- | --- |
| 1 | Contract and model; **contract first**: the schemas, all `openapi.yaml` changes, rule texts, the entry record, the end coverage helper, `RelationEnd`, and every reader compiling with today's behavior | Opus | About 90 | `main` once sign-off part 2 is merged |
| 1 | Editor and mock, part one | Sonnet | About 100 | The contract commit |
| 2 | Rules, expressions, processes and scenarios. Keeps every change to `Expressions/` and `Validation/ExpressionRules.cs` | Opus | About 100, with a fresh agent for the rest if it runs over | The contract and model agent's finish |
| 2 | Resolver, index, deletes, records, keys, tool texts, the TypeScript sample and pack goldens. **Owns the tool files** | Opus | About 100 | The contract and model agent's finish, beside the rules agent |
| 2 | Editor, part two: the canvas, the toggle, the pickers, the mock's process rules | Sonnet | About 100 | Wave 1's editor agent, in the first free slot |
| 3 | Review of the pull request | Opus | About 60 | All builders' finish |
| 3 | Docs | Sonnet | About 40 | The builders' finish, beside the review |
| 3 | Browser pass, which shows the owner the junction node | Sonnet | About 50 | The builders' finish, beside the review |
| 4 | Fixers, split by area (engine and output; editor and mock; docs) | Opus or Sonnet | About 40 each | The review's and the browser pass's findings |
| 4 | A short second browser pass on the fixes | Sonnet | About 25 | The editor fixer's finish |

**Part 2 agents,** on either path

| Wave | Agent | Model | Steps | Starts from |
| --- | --- | --- | --- | --- |
| 1 | Contract and engine; contract first | Opus | About 100, with a fresh agent for the rest if it runs over | `main` once part 1 is merged and question 2 answered |
| 1 | Editor and mock | Sonnet | About 90 | The contract commit |
| 2 | Joins, uniques, readers, tools, records and migrations. Owns the tool files | Opus | About 90 | The contract and engine agent's finish |
| 2 | Pack tests and READMEs | Opus | About 70 | The contract and engine agent's finish, beside the joins agent |
| 3 | Review, docs, browser pass | Opus, Sonnet, Sonnet | About 60, 30, 40 | All builders' finish |
| 4 | Fixers, split by area | Opus or Sonnet | About 40 each | The findings |

- **One pull request per part, into `main`,** with the contract, engine, editor, output, docs and fix commits. Each is its own release. The owner merges part 1 first, and tags each.
- **Generated files** (the editor's API types, the recorded rule list, the sample app's copies) are written only by the fixed commands, after each merge into the branch. Pack expected files stay with the builder, so their changes are reviewed.
- **Fixers** are fresh agents with the exact findings. **A design gap found while building** stops the work, and the owner is asked.

**Estimate.**

**Part 1**

| | Wall time | Output | New input | Cached re-reads |
| --- | --- | --- | --- | --- |
| Wave 1 (contract and model, editor part one) | About 0.8 hours | 0.3M | 1.0M | 50M |
| Wave 2 (rules and processes, resolver and records, editor part two) | About 1.35 hours | 0.5M | 1.85M | 115M |
| Wave 3 (review, docs, browser pass) | About 0.65 hours | 0.22M | 0.8M | 45M |
| Wave 4 (fixes, second browser pass, CI) | About 0.7 hours | 0.23M | 0.75M | 45M |
| In all | **About 3.5 hours** | **About 1.25M** | **About 4.4M** | **About 255M** |

**Part 2,** on either path

| | Wall time | Output | New input | Cached re-reads |
| --- | --- | --- | --- | --- |
| Wave 1 (contract and engine, editor) | About 1.0 hours | 0.35M | 1.2M | 70M |
| Wave 2 (joins, uniques, readers and migrations, pack tests) | About 1.0 hours | 0.38M | 1.3M | 75M |
| Wave 3 (review, docs, browser pass) | About 0.6 hours | 0.22M | 0.8M | 45M |
| Wave 4 (fixes, CI) | About 0.6 hours | 0.2M | 0.7M | 40M |
| In all | **About 3.2 hours** | **About 1.15M** | **About 4.0M** | **About 230M** |

**Each part stays under the 400M cap without a further split.** Part 1's likely range is 230M to 290M; part 2's is 200M to 260M. If a wave 2 agent needs a fresh agent to finish, add about 30M. If part 1's wave 2 ends above 200M, the rest is tracked step by step and the owner is told before wave 4 starts.

**Both parts:** about 12,500 to 14,500 lines, about 6.7 hours, about 2.4M output, about 8.4M new input and about 485M cached re-reads.

## Sources

From the research for this design. Vendor and product names appear only here, as prior art.

- [1] UML associations and n-ary associations — https://sparxsystems.com/enterprise_architect_user_guide/15.0/model_domains/associate.html ; https://textbooks.cs.ksu.edu/cc410/i-oop/05-uml/06-associations/tele.html
- [2] UML {xor} constraint — https://uml-diagrams.org/constraint.html
- [3] SysML v2 and KerML, union types — https://sim4edu.com/reading/kerml-sysml/core-intro
- [4] Barker's notation, exclusion arc — https://en.wikipedia.org/wiki/Barker%27s_notation ; https://users.informatik.uni-halle.de/~brass/dd24/print/c1_ermod.pdf
- [5] GraphQL unions and interfaces — https://graphql.org/learn/schema/
- [6] Rails polymorphic associations — https://guides.rubyonrails.org/association_basics.html
- [7] Laravel polymorphic relations and the morph map — https://laravel.com/docs/12.x/eloquent-relationships
- [8] Django generic foreign keys — https://docs.djangoproject.com/en/stable/ref/contrib/contenttypes/
- [9] Hibernate `@Any` and `@ManyToAny` — https://docs.hibernate.org/orm/6.3/javadocs/org/hibernate/annotations/Any.html ; https://docs.hibernate.org/orm/7.2/javadocs/org/hibernate/annotations/ManyToAny.html
- [10] EF Core inheritance — https://learn.microsoft.com/ef/core/modeling/inheritance
- [11] EF Core shared base types (third-party posts) — https://concurrency.com/blog/polymorphic-associations-using-ef-core/ ; https://milanjovanovic.tech/blog/tph-vs-tpt-ef-core
- [12] Prisma polymorphic relations — https://www.prisma.io/blog/prisma-orm-8-closes-330-most-requested-issues ; https://github.com/prisma/prisma/discussions/3668
- [13] PostgreSQL `num_nonnulls` — https://www.postgresql.org/docs/current/functions-comparison.html
- [14] A check that exactly one column is set — https://pgrs.net/2023/11/14/two-exciting-postgresql-features-to-improve-null-handling
- [15] SQL Antipatterns, polymorphic associations — https://www.oreilly.com/library/view/sql-antipatterns/9781680500073/f_0048.html
- [16] Teamcenter relation rules — https://media.plm.automation.siemens.com/open/plm-xml/docs/v7.0.3/documentation/plmxml_RelationRuleType.html
- [17] Teamcenter GRM rules and subtypes — https://teamcenterrac.blogspot.com/2021/01/grm-rulesgeneric-relationship-manager.html
- [18] Windchill association rules — https://support.ptc.com/help/windchill/r13.0.0.0/en/Windchill_Help_Center/changemanagement/BusRulesAssocRulesTableRef.html
- [19] Aras relationship types (search snippets only) — https://www.aras.com/b/english/posts/adding-relationships-to-itemtypes
