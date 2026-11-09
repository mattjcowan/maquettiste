# Relations with several types at an end: design

**Status (2026-11-09): part 1 released in 0.21.0 (#70), part 2 released in 0.22.0 (#72). Part 2 designed and checked (section 19): storage in the team's designed and imported tables (path B), and several types or any entity at both ends, in one release (0.22.0). The owner answered its four questions as recommended on 2026-11-09 (section 15), and it is built, reviewed and checked in the browser in #72. The owner kept the collapsed both-ends box and confirmed the names on 2026-11-09, for 0.22.0. An independent check is applied to each part (sections 14 and 19.18). The owner answered part 1's six questions as recommended on 2026-11-08, and gave part 2's principles the same day (section 15).** It designs the item that [Who, what and when](lifecycle-actors-scenarios.md) section 11 describes: "an end lists several entities, all of them, or every entity carrying a stereotype". The owner's drawing of 2026-11-08 in that section is decided and is not reopened here. [Stereotype sets](stereotype-sets-design.md) (0.14.0) gives the meaning of a stereotype as a set of types, and named trigger points ([trigger points](trigger-points-design.md), part 2) give the list form it reuses. [Actors](actors-design.md), [rights](rights-design.md), [object effects](object-effects-design.md) and [lifecycles](lifecycle-design.md) give the readers that change. Every code fact here was checked against the code at 0.19.0; line numbers will move once sign-off part 2 is merged. A file path without a project prefix is under `src/Maquettiste.Engine/`. The examples are invented. Each new name and format is held to the roadmap's test: it stays for good, it must help any team whether they use it or not, and it stays optional.

## 1. In short

Today each end of a relation names one entity. A team that attaches comments to parts, assemblies and documents has to add a shared base entity, or make one relation per type. Neither says what they mean: "a comment is attached to one thing, which is a part, an assembly or a document".

| Layer | What changes |
| --- | --- |
| The model | One end of a two-ended relation may name its **types**: a list of entities and stereotypes, or **any entity**. The end then covers each listed entity, every entity that carries a listed stereotype, and every entity derived from them. Role, cardinality, navigation and delete rule stay one per end. Today's `entity` keeps its meaning and its bytes |
| Rules, processes and scenarios | The other end's navigation is generated on each type. Guard paths through such an end read what every type holds under one id. Rights, trigger points, freezing and scenario links work through it, since they already name ends by id. `in_state` through it is unknown for an object of a type the lifecycle doesn't govern, as it is today through a one-type end |
| Code generation | Part 1: templates see the types of each end, and no database stores such a relation. Part 2 (section 19): an optional relation mapping describes, per end, how the team's own tables hold each link: a key on each type's table, a column per type, a type and an id, an id unique across types, a key to a shared base table, a link table, or a table per type. The engine adds no column. Both ends may have several types |
| The editor | The relation's form picks one entity, several types or any entity. The canvas draws the agreed looks: bundled lines for two types, a junction node for three or more, one line to a box for a stereotype or any entity, and one toggle per relation that collapses its lines to one line and a list. The toggle and the node's position are saved in the diagram file |
| The team's system, or a lifecycle system | Nothing changes until the team uses the new output or builds from the model |

**A project that uses none of this** keeps its files, output bytes, findings and messages word for word, keys, replays and speed. Section 12 says how, and which tests pin it.

**Two parts, each its own release:** part 1 is the model, the rules, processes, scenarios, the editor and the resolved model, with no storage (0.21.0). Part 2 is storage in the team's tables, and several types or any entity at both ends (0.22.0, section 19; question 10 asks whether it stays one release).

## 2. Scope

| | What | Section |
| --- | --- | --- |
| **Part 1 (0.21.0)** | `types` and `anyEntity` on a relation end; what an end covers; the rules; navigations on each type; guard paths, `in_state`, owner and actor paths, rights, trigger points, freezing and scenario links through such an end; the resolved model and records; the index; deletes and references; the relation form, the canvas looks and the collapse toggle; tool texts, the API contract and the editor's mock server; refusals where one table is needed (binding fields, query fields and collections, seeds) | 4 to 11 |
| **Part 2 (0.22.0)** | An optional relation mapping that describes, per end, how the team's designed and imported tables hold each link (path B); type codes; what the database can't enforce, with the text to copy; joins per pair of types; entity binding fields for the link's columns; several types or any entity at both ends of a two-ended relation; the Storage tab, the canvas for both ends and the database views; `sql-ddl` and `csharp-dapper` tests | Section 19 |
| **Later, if teams ask** | Tables the engine makes for such relations (path A); materialize of such storage; seeds of such links; a query through such an end; several types on an n-ary relation; `exceptEntities` on an end; a stereotype's attributes through a navigation; narrowing a right or trigger point to one type; a database-wide storage default; value-map facts for a renamed type code; the storage forms of section 19.13 | 13 |
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
| One link table holds the instances of many relations, told apart by a relation code, with a from side and a to side, each an id or a type and an id, and attribute columns | Common in lifecycle and document systems (general knowledge, no single source) | A link table with `constants` (fixed values such as the relation code), per-end entries and attribute columns (section 19.4, ways 8 and 9) |
| Ids are unique across every table (UUIDs, or one shared sequence), so a link holds the id alone and the type comes from the row it points at | Common (general knowledge) | An id unique across types (way 4), allowed on an any-entity end (the owner, 2026-11-08) |
| A type column holds a number that points at a type-definition table, not a name | Common in lifecycle systems (general knowledge) | An integer `discriminatorValue` as the type code (decision 42) |
| A table carries its own surrogate id, and the business key is a separate unique key | Inheritance and key mapping in object-relational frameworks [10] | Links refer to a unique column of each type's table, never to the entity's key attributes (principle 3 of section 19.1) |
| Attribute values stored as rows (entity-attribute-value), value tables split by data type, table sets repeated per partition | Large lifecycle systems (general knowledge) | Not modeled: a table can't describe them on its own, so packs implement them (section 13) |

No source reviewed draws a junction node with a collapse toggle; that look is the owner's. The rows after the type-value row were added for part 2 (section 19) and describe patterns, not one product's schema.

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

8. **One end of a two-ended relation may have several types: an association, an aggregation or a composition, with any `min` and `max`.** Several types on both ends, or on an n-ary relation, are refused (MQ3041). Confidence: medium-high; question 3. **Part 2 lifts the both-ends limit** (the owner, 2026-11-08; decision 53, section 19.12); the n-ary limit stays.
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
24. **(part 2) Which path part 2 serves first is the owner's question 2. Part 2's final design follows the answer.** Confidence: high that it is the owner's call. **Settled:** path B first (section 15, answer 2); section 19 is its design.
    - **Path B, designed tables with explicit bindings (recommended):** the team's table files hold the columns, keys and checks, and the mapping only names them (decision 26). It invents nothing, and it fits the owner's standing rule: tables first, bindings explicit, no derived tables.
    - **Path A, tables the engine makes:** the engine adds the columns, keys and checks to the tables it makes from entity mappings (decision 27). One mapping line per relation, but the engine makes up columns and checks from conventions.
    - **Both paths share** decisions 25 and 28 to 32: the four ways to store, type codes, what the database can't enforce, joins, one-to-one uniques, migrations and the other packs.
    - The other path comes later (section 13). Part 1 doesn't depend on the answer.
25. **(part 2) The four ways to store.** **Replaced by section 19:** the mapping describes the storage per end and composes (decisions 37 to 39); the four ways below are four combinations of its entries (section 19.4); `typeStorage` is question 8; MQ4115's severity is question 7; the rules are worded on table columns (decision 41), so "key" below reads "a unique column of the type's table". Kept for the record: A relation with an end of several types is stored in a database only through a mapping of it there. Without one, nothing is stored, and MQ4115 becomes a warning. The mapping gains **`typeStorage`**. `shape` keeps its meaning and may be left out, as today. Confidence: high.

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
    - **An `anyEntity` end is stored only as `type-and-id`** (the owner, 2026-11-08). `column-per-type` and `table-per-type` are refused there (MQ4117), since a new entity would add a column or a table. An end that names a stereotype may use any of the ways, since the team named the set. **Widened the same day:** an id alone, when ids are unique across tables, is allowed too (section 15; decision 43).
    - **In each database, the end's types are the ones placed there.** A covered type with no placement is left out of that database's storage, with no finding; the Storage tab lists it, and says when a column comes from a carrier that gained the stereotype through a package default. When no type is placed, the relation isn't stored there, as today.
    - **`type-and-id`** needs the id column to refer to one unique column of each placed type's table (its primary key or a unique key, of a compatible type), never to the entity's key attributes (MQ4117 otherwise; decision 41). **`base-table`** needs a foreign key to one table that holds the rows of every placed type (MQ4117 otherwise); no base entity is needed.
26. **(part 2, path B) Designed and imported tables with explicit bindings: a sketch.** Confidence: medium-high; settled in part 2's design. **Settled in section 19:** decisions 37 to 47. The mapping extends `ends` with `foreignKeys`, `types`, `idColumn`, `typeColumn` and `referencesColumn`; bindings gain two field forms; the check is suggested as info (MQ4118).
    - The team's table files hold the columns, foreign keys and checks, written by hand or imported from a database.
    - The relation's mapping states `typeStorage` and names what implements it: one existing foreign key per type for `column-per-type` and for a key on each type's table; one junction per type, with its end foreign keys, for `table-per-type`; a type column and an id column for `type-and-id`; today's `foreignKey` for `base-table`. This extends today's `foreignKey`, `junctionTable` and `ends`, which already name what exists for one-type relations.
    - An entity's binding maps the end's columns in its `fields`, so its read and write statements (`binding_sql`) carry them, and MQ4047 (every column accounted for) counts them. Part 2 lifts MQ4116 for binding fields.
    - Validation checks that each named key leads from the right table to the right type, that the id column fits the types' keys and the type column holds text, and that the table has a check for the chosen storage (info when it doesn't, with the check text to copy). Drift (`Model/BindingDrift.cs`) follows the named columns.
    - The engine writes no column. `sql-ddl` writes the table files as it writes any designed table.
    - **Waits:** engine-made tables for such relations, and materialize, which writes a projection's shape into table files and so needs path A's shapes.
27. **(part 2, path A) Tables the engine makes: a sketch.** Confidence: medium-high; settled in part 2's design. **Waits** (section 13). Its check texts serve part 2's MQ4118 text to copy (decision 47). Its conventions and keys are not added.
    - The mapping's `typeStorage` alone decides. The engine adds the columns, foreign keys and checks to tables it makes from entity mappings.
    - **Checks** are plain SQL text, the same form in all five dialects, since expression-language checks are lowered to PostgreSQL only (MQ4056 elsewhere; `Resolution/ResolvedExpressions.cs:386-391`). Column per type, `min` 1: `(CASE WHEN "attached_to_part_id" IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN "attached_to_assembly_id" IS NOT NULL THEN 1 ELSE 0 END) = 1`, with `<= 1` for `min` 0 and, for a composite key, a clause that each set is all null or all set. Type and id, `min` 1: both columns not null and `"attached_to_type" IN ('Assembly', 'Part')`; `min` 0 adds "both null, or both set". Identifiers follow the database's quoting, codes are sorted ordinally, and the check is named by `checkName` with `{name}` set to the end's role.
    - **Names:** two new conventions, `typeKeyColumn` (default `{role}_{entity}_{key}`, such as `attached_to_part_id`) and `typeColumn` (default `{role}_type`); the id column uses `foreignKeyColumn` with `{key}` set to `id`; a junction per type uses `junctionTable` with each type in the end's place, such as `comment_part`.
    - **Keys** stay the same across renames: `<endId>.<entityId>.<keyAttributeId>`, `<endId>.type`, `<endId>.id`, `<relationId>.<entityId>@<databaseId>`.
    - **Refused for now (MQ4117):** a holding table that is designed, imported or bound; a reference-typed collection attribute beside `table-per-type`, since `sql-ddl` finds those through one junction. Materialize refuses an entity whose table holds such storage (MQ4055).
    - **Waits:** designed and imported tables, entity bindings and materialize.
28. **(part 2) A type code is the code of the object's own entity: the `discriminatorValue` of the entity's mapping in that database, else the entity's name.** This is the rule the TPH discriminator follows today (`DatabaseRun.Finish.cs:436`). Confidence: medium; question 5. **Settled in decision 42:** an entity binding may set `discriminatorValue` too, since a bound entity ignores its mapping element (MQ4054); a type column may be an integer column holding integer codes; collisions are checked among the entities a type column holds.
    - Every covered entity that can have objects (not abstract) and is placed in the database has a code, derived ones included, since an object's type column names its own entity.
    - Today `discriminatorValue` is refused on an entity outside an inheritance hierarchy (`Validation/MappingRules.cs:146`, MQ4009). It becomes allowed when a `type-and-id` storage in that database covers the entity.
    - Two covered entities with one code are refused (MQ4117), with the fix to set `discriminatorValue`.
    - The type column is text; an engine-made one has length 64, as the TPH discriminator (`DatabaseRun.cs:1026`). An integer code is written as its text.
29. **(part 2) What the database can't enforce is said once, as info MQ4118.** **Settled in decision 47**, with the text to copy for each missing check or unique. A `type-and-id` storage can't apply the end's delete rule, nor a composition's cascade, so the team's system applies it. A `base-table` storage can't refuse a link to a base row of a type the end doesn't list. Confidence: high.
30. **(part 2) Joins.** **Settled in decision 48:** paths between table columns, one per pair of targets, with `conditions` on a step, `from_type` and `to_type` on a path, and `type_joins` on navigations and entity mappings. Today `navigation.joins[db]` and an entity mapping's `joins` hold one path of equal columns (`RJoinPath`, `ResolvedConceptual.cs:535-558`), built by `BuildJoins`, which matches on `navigation.From.Entity` (`DatabaseRun.Finish.cs:504-544`, line 524). Confidence: medium-high; the member names come with part 2's design.
    - A navigation to or from an end with several types gets one path per type: for `column-per-type`, `table-per-type` and a key on each type's table, a path of equal columns; for `type-and-id`, the id equality plus "the type column equals the type's code", which `RJoinPath` can't say today, so part 2 adds one member for it; for `base-table`, today's single path to the base's table.
    - `BuildJoins` uses coverage in place of `From.Entity`. Records and tests cover each storage.
31. **(part 2) One-to-one uniques work in every dialect.** Confidence: high. **Settled for path B in decision 47:** a missing unique is info, with the text to copy (a filtered unique index on SQL Server); the engine-made index waits with path A.
    - Today a one-to-one adds a plain `UNIQUE` on the key columns (`DatabaseRun.Relations.cs:139-140`), and `sql-ddl` adds `NULLS NOT DISTINCT` on PostgreSQL only (`packs/sql-ddl/_sql.scriban:284`). PostgreSQL, MySQL, SQLite and Oracle let several rows leave the key empty under a unique key; SQL Server allows one such row.
    - With `column-per-type` every set is nullable by design, and so is `type-and-id` with `min` 0. Part 2 makes each of these a unique index filtered to rows where the key is set on SQL Server, and a plain unique key elsewhere. On path B, validation says when a designed table lacks it. A five-dialect golden pins it.
    - Today's optional one-to-one on SQL Server has the same limit; part 2 doesn't change it.
32. **(part 2) Migrations and the other packs follow the tables.** Confidence: high. **Settled for path B in decision 52 and section 19.9:** the team's table edits carry the facts; a changed type code is warning MQ4119.
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
    - An older engine refuses a file with `types` or `anyEntity` (part 1), or with part 2's new keys (section 19.6), or with several types at both ends (MQ3041 in 0.21.0). `formatVersion` stays 1.
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

**Part 2** is in section 19.6: `mapping.json`'s `ends` entries gain `foreignKeys`, `types`, `idColumn`, `typeColumn` and `referencesColumn`, the mapping gains `constants`, `attributes[].column` serves relation mappings, an entity binding gains `discriminatorValue` and two field forms, and `openapi.yaml` gains the storage members of `RelationMappingRecord`, `BindingFieldRecord` and the database view. `typeStorage` is question 8. Path A's conventions are not added.

Also updated in both parts: the sample app's schema copies, the editor files generated from `openapi.yaml`, and the recorded rule list, each by its fixed command.

## 6. Rules

| Rule | Sev | Part | Change |
| --- | --- | --- | --- |
| MQ3041 | E | 1, 2 | **New.** An end with several types where it can't be: on an n-ary relation, on both ends of a relation (part 1 only; part 2 drops this case, section 19.12), or `anyEntity` on a composition's child end. "Relation 'Link' is n-ary, so its end 'target' can't have several types; give it one entity." "Relation 'Link' has several types on both ends; keep them on one end." "End 'item' is the part in composition 'Holds', so it can't be any entity; list the types it may be." |
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
| MQ4009 | E | 1, 2 | Part 1: a mapping of a relation with an end of several types sets only `ignore`. Part 2: the cases of section 19.7 |
| MQ4115 | I (answer 7) | 1, 2 | **New.** "Relation 'AttachedTo' has an end with several types, so database 'main' stores nothing for it." Reported when every end has a covered type placed in the database. Part 2 also skips a database whose mapping stores the relation (decision 49) |
| MQ4116 | E | 1 | **New.** A binding field, a query field or a query collection names an end with several types, or a navigation to one, which leads to more than one table (`schemas/v1/query.json`, `collections`). "Field 3 of the binding of 'Comment' names end 'attachedTo', which leads to several types; a field can't map it." Also, in part 1, a field or collection that names another end of such a relation, as a navigation from one of its types does, since nothing is stored: "Collection 0 of query 'PartNotes' names end 'comment' of relation 'AttachedTo', which has an end with several types; a collection can't fill it." A field of a collection's select list says "Field 0 of collection 1 of query …". Part 2 lifts it for binding fields, which resolve or get MQ4045 (decision 46), and keeps it for queries |
| MQ4117 | E | 2 | **New.** What a mapping or a binding names doesn't fit together: section 19.7 |
| MQ4118 | I | 2 | **New.** What the database can't enforce for an end, with the text to copy: section 19.7 |
| MQ4119 | W | 2 | **New.** A type code changed since the baseline: section 19.7 |
| MQ4120 | W | 2 | **New.** A mapping states storage but leaves a placed type out: decision 49 |
| MQ4011, MQ4045, MQ4051 | E, E, W | 2 | Section 19.7 |
| MQ4055 | E | later | Path A, which waits: also an entity whose table holds the storage of a relation with an end of several types |

Every new rule and new case needs an end with several types or one of part 2's new keys. The catalog descriptions of MQ2003, MQ2020, MQ3016, MQ3116, MQ4009, MQ4055, MQ7105, MQ9211, MQ9305 and MQ9604 grow, so `list_validation_rules` answers differently for every project, as in earlier items.

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

**Part 2: `RRelationMapping`** (`relation.mappings[db]`): settled in section 19.8 (`links`, `constants`, `attributes`, `left_out`, `not_stored`, `type_codes`), with the join members of decision 48 and the binding members `holds` and `key_of`. The tables, columns, foreign keys and checks are the team's physical objects, so a pack that writes DDL from tables needs nothing more. A pack that stores links its own way reads the part 1 members.

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
- **Part 2** (section 19.8): `get_database_view` gains `relationStorage` (holding tables, types left out or not stored, and what the database can't enforce with the text to copy); `plan` and `get_plan_diff` show the tables as usual; `get_binding_drift` names the new field forms.
- **`openapi.yaml`, part 1:** `EndRecord` and `NavigationRecord` as section 7. `RelationEndSummary` makes `entity` optional and gains `entities` (ids of entity entries), `stereotypes` (ids), `anyEntity` and `covers` (the carriers of its stereotypes, for the explorer), each left out when unused. The index format moves to `maquettiste-index/e14` (`Editor/ModelReads.cs:23`). `RelationEndSummary` gains list members, so its equality must compare them by value, since `IndexRow` equality relies on it (`Model/Documents.cs:242`).
- **`covers` is filled after the rows,** since each index row is built from its own file (`ModelIndexer.cs:1084-1086`). `WithSubjectCoverage` (`ModelIndexer.cs:991-1037`), which fills a process row's `covers` today, also fills each relation end's, in the same order: per entry, by qualified name, then id. Its early return (lines 994-995) also checks for relation ends that name a stereotype.
- **Part 2:** `RelationMappingRecord`, `BindingFieldRecord`, `EntityBindingRecord` and the database view as section 19.8. No settings change.
- **Tool texts:** `ModelTools.cs`, `ProcessTools.cs`, `Agent/tools.json` and the Functions endpoints, where they describe relation ends and mappings. The conventions resource (`McpServerSetup.cs`), the modeling skill (`skills/maquettiste-modeling/SKILL.md`) and the assistant's prompt describe `types`, `anyEntity` and, in part 2, the per-end storage entries, `constants`, attribute columns, type codes, the binding field forms and both ends (section 19.8).
- **The editor's mock server** (`src/editor/src/mocks/model/`) re-implements the rules and must change in step. Part 1: `validate.ts`, `refs.ts`, `cascade.ts`, `modelIndex.ts`, `store.ts` (its index rows, line 1631), `rightRules.ts`, `rightsCheck.ts`, `triggerRules.ts`, `triggerRun.ts`, `relativeRules.ts`, `stepper.ts`, `queries.ts` and `bindings.ts` (MQ4116), `seed.ts` and `processSeed.ts` (MQ7105), `physical.ts` (skip such relations, MQ4115). Part 2: section 19.10 (`endRules.ts`, `refs.ts`, `cascade.ts`, `physical.ts`, `bindings.ts` and the database view).

## 10. Packs

Every change depends on an end with several types or one of part 2's new keys, so today's output is the same bytes.

| Pack | Part 1 | Part 2 |
| --- | --- | --- |
| `sql-ddl` | Nothing to write: the relation has no table, column or key. Its one relation loop, `ref_collections` (`_reference.scriban:582`), reads a mapping's junction table, which such a relation lacks. Golden test: output unchanged | No template change: the team's designed tables are written as they are (section 19.9). Golden tests for ways 1 to 10 of section 19.4: all of them in the `postgresql` dialect, and in the other four only the ways whose output differs (question 10); a migration test for a type added and removed |
| `csharp-dapper` | No change: it generates no navigations (README, "Not covered"), and rights and trigger points print ends by name | No template change: a bound holder's new fields become entity properties and its repository writes them; a mapped holder's link columns come through `storage_columns` (section 19.9). A build test with warnings as errors |
| `process-docs` | No change: it prints ends by name (`_docs.scriban:87-96, 495-500`) and objects by entity. A golden test with a right on such an end and a scenario link through it | No change; a golden with several types at both ends |
| `seed-data` | Seeds of such links are refused (MQ7105) | The same |
| `samples/typescript-pack` | It reads `nav.target.name` (`entity.scriban:31, 55-61`; `schema.scriban:47`) and would write broken code for a null target. It writes a union type from `nav.targets` (`attachedTo?: Part \| Assembly`), `unknown` for any entity, and no `…Id` property for such a navigation | No change; a golden with both ends |

A pack outside this repository that reads `end.entity` or `navigation.target` should check `end.has_several_types`. The release notes and `packs/README.md` say so.

## 11. Editor

**The relation's form** (`inspector/fields.tsx`, `EndEditor` and the ends list)
- Each end has a **Type** choice: **One entity**, **Several types** or **Any entity**. Several types shows the entries as chips, in file order, with a picker that lists entities, then a **Stereotypes** group with those that apply to entities. A chip has a remove button with a tooltip.
- Once one end has several types, the other end's choice keeps only One entity, with the tooltip "Only one end may have several types." (part 1 only: part 2 drops this limit, section 19.12). An n-ary relation keeps only One entity: "An n-ary relation's ends name one entity each." A composition's child end leaves out Any entity.
- Going back to One entity with one entity entry writes `entity`. With more, the person picks one.
- MQ3042 and MQ3043 have quick fixes (`problems/quickFix.ts`): use entity, remove the entry.
- The **Storage** tab: part 1 says "No database stores a relation with an end of several types." Part 2's tab is in section 19.10: per database, **Type storage** as Held in and Linked by, the pickers, the types left out or not stored, and what the database can't enforce with the text to copy.

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
- **Several types at both ends** (part 2): one junction node with lines to both ends' types, and one collapsed box that lists each end's entries under its role (section 19.12).
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

**After part 2:** section 19.15.

## 13. What comes next

| Item | What it takes from this |
| --- | --- |
| Tables the engine makes for such relations (path A) | Decision 27's sketch: the engine adds the columns, keys, checks and filtered uniques that MQ4118 today only suggests. It reuses part 2's per-end entries for designed tables |
| Materialize of such storage | Writes path A's columns, keys and checks into table files, and part 2's per-end entries into the mapping |
| Seeds of such links | A seed column names the type and the key. Lifts MQ7105's new case |
| A query through such an end | A query names which type it joins, using part 2's joins. Lifts MQ4116 for queries |
| A stereotype's attributes through a navigation | The item stereotype sets left open ("by navigation, not yet"): nested rows in `RowOf`, the resolved path target, pack behavior. For every navigation at once, not only these ends |
| A link table per pair of types | When both ends have several types and each pair has its own table. A `types` item would name the pair; the keys of section 19.6 stay |
| Column pointers spread over several holding tables | When one end's types live in several tables and each holds a type and an id (or an id) pointing at the other end. An entry would name a column per table; `foreignKeys` already covers keys |
| An attribute column per holding table | When the links of a relation with attributes live in several tables. An `attributes` item would name a column per table |
| A relation binding | Read, write and delete statements for links, like an entity binding, over the tables part 2's mapping names. The mapping's entries already name the columns |
| A field form for a relation attribute in a bound row | An entity binding field that maps a relation attribute stored in the holding entity's row, so its repository writes it |
| Schemas a table can't model on its own | Attribute values stored as rows (entity-attribute-value), value tables split by data type, table sets repeated per partition, ends made of a partition and an id, ends that point at a shared id table, one table with document columns for every entity. Packs implement them, reading the model and the tables separately (section 19.1). Not designed or promised |
| Several types on an n-ary relation | The canvas first draws every end of an n-ary relation |
| `exceptEntities` on an end | "Any entity except Comment", as named trigger points have it. An optional key |
| A right or trigger point narrowed to one type of an end | A narrowing key. Until then they speak for every type |
| A database-wide storage default | A convention for the per-end entries, if teams find one mapping per relation too much |
| A value-map fact for a renamed type code | Code maps exist (`Migrations/CodeMap.cs`), so a rename could rewrite the type column's rows. Until then MQ4119 warns (decision 52) |
| The "Add to table" button | Writes the check or unique MQ4118 suggests into the designed table file, in one save and one undo step. Waits by question 10's recommendation; Copy and the texts ship in part 2 |
| `constants` and `attributes[].column` on one-type relations | Only if question 9 takes the alternative: the same keys and checks as on a relation with an end of several types |
| The both-ends editor work | Only if question 10's checkpoint moves it: the form without the one-end limit, the both-ends node and box, and path pickers through both ends (section 19.12) |

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
4. **No storage default: yes.** Part 1 stores nothing, and MQ4115 is info. In part 2 the same finding becomes a warning until a mapping states the storage or sets `ignore`, so a model that already uses part 1 gets that warning on the upgrade to 0.22.0. (Question 7, section 19.14, asks to revisit the warning in light of the principle that entities and tables may stay separate models.) When the end with several types holds the key, the mapping doesn't set `typeStorage`: it names one foreign key on each type's table (decision 25). That is the natural shape, not a fifth value, and nothing is stored until that mapping exists.
5. **Type codes: the name, with collisions refused.** The stored code is the `discriminatorValue` of the entity's mapping in that database, otherwise the entity's name, the rule a shared-table discriminator already follows. Two covered entities with one code are refused (MQ4117). No second code scheme, such as qualified names, is added.
6. **Releases: two.** Part 1 is 0.21.0, after sign-off part 2 is merged. Part 2 is 0.22.0.

**One constraint for part 2's design, from the owner:** `column-per-type` and `table-per-type` are refused on an `anyEntity` end (MQ4117), since a new entity would add a column or a table. `type-and-id` is the way such an end is stored. An end that names a stereotype may still use the other ways, since the team named the set.

**Part 2's principles, from the owner, 2026-11-08** (section 19.1):
- **An `anyEntity` end may also be stored as an id alone,** with no type column, when ids are unique across tables ("yes, allow it"). The reason stays: no form of storage may grow with the model. A type and an id, and an id alone, both fit; a key per type and a table per type stay refused there (MQ4117). The database can't check the type an id points at, which MQ4118 says once (info). Decision 43.
- **Entities and tables are separate models.** They don't always map clearly together, and a pack may be what puts them together. A relation's storage mapping is optional, always; part 2 supports the usual correlations a table can model, and leaves the rest (attribute values stored as rows, partitioned table sets, one table with document columns for every entity) to packs.
- **"Entities can have a different id system than tables."** A table may carry its own surrogate id that no attribute maps. A link points at a unique column or key of each type's table, never at the entity's key attributes, and joins are between table columns. Decision 41.
- **Every combination of end kinds,** "including a relation that allows any to any": one entity, several types or any entity at each end of a two-ended relation. Cardinality stays `min` and `max` per end. An n-ary relation keeps one entity per end (answer 3a). Part 2 lifts the one-end limit (decision 53, section 19.12).

**Part 2's questions, answered 2026-11-09.** The owner sent the answers from a review they asked for. Each is the recommendation of section 19.14. Two replace earlier answers, both before those names were released.

7. **MQ4115 stays info.** A mapping is optional, so an unmapped relation isn't a warning, and a model that uses part 1 gets nothing new on the upgrade. The Storage tab still says "Nothing is stored in this database." MQ4120 is the warning. It fires only where a mapping states storage and leaves a placed type out. This replaces the warning in answer 4.
8. **`typeStorage` is left out of the format.** Each end's entry says how that end is stored, and the two ends can differ. The editor keeps the choices of section 19.10: Held in, Linked by and their labels, such as "An id unique across types" and "A key". The four earlier names are among those labels. This replaces the file key in answer 1.
9. **`constants` and `attributes[].column` apply to every relation mapping with a designed `junctionTable`,** a one-type relation the same way. A mapping that writes neither keeps its bytes. An attribute column on the one table that holds the links, when that table is an end's table, stays as decision 45 says. A one-type foreign-key mapping doesn't gain that column in 0.22.0.
10. **One release, 0.22.0, with cuts 1 and 3 and the checkpoint.** Waves 1 and 2 are expected near 210M. If they pass 240M, the both-ends editor work moves to 0.23.0 and the owner is told. 0.22.0 then still opens, checks and stores a relation with several types at both ends, and the canvas draws the plain fallback. The collapsed box, which has no lines, is chosen in the first browser pass.

**Any entity, confirmed** (decision 43): a type and an id, an id alone, a key to one shared table, and a link table that holds those are allowed. None of them grows when an entity is added. A key per type and a table per type stay refused.

**The collapsed box and the names, answered 2026-11-09** (from the same review, each as recommended in #72):

- **The collapsed both-ends box stays.** Collapse lines turns the relation into one box with no lines. The box names the relation and lists each end's entries under its role, with its cardinality ("source *: Comment, Note, Folder"); neither side has one card to draw a line to. The expanded look (one node with a line to each type) stays the starting look, Expand lines brings it back, and the toggle is saved on the diagram member (section 19.12).
- **The 0.22.0 names stand** as `docs/releases.md` lists them. `typeStorage` stays out of the file. Two labels the list names: a column the mapping names and the table no longer has reads "*name* (missing)", or "(missing column)" when the name is gone too. The field map, Map by name and the drift report name the new fields `<navigation>Type`, `<navigation>Id` and `<navigation><Entity>Id`, else after the role (`attachedToType`, `attachedToId`, `attachedToPartId`); these are the binding fields' names (decision 46), not new keys.

## 16. New names

- **In the format, part 1:** the end keys `types` and `anyEntity`, and the end type entry `{ entity }` or `{ stereotype }` (schema title "End type").
- **In the format, part 2** (section 19.6): in a relation mapping's `ends` entry, `foreignKeys` (a list of foreign key ids), `types` (items `{ entity, foreignKey }` or `{ entity, referencesColumn }`, schema title "End type storage"), `idColumn`, `typeColumn` and `referencesColumn` (a column id or key); the mapping key `constants` (items `{ column, value }`, `column` a column id); `attributes[].column` newly allowed on a relation mapping; both apply to every relation mapping with a designed `junctionTable` (answer 9); the entity binding key `discriminatorValue`; the binding field forms `<endId>.type` and `<endId>.<entityId>`. **`typeStorage` and its values `type-and-id`, `column-per-type`, `table-per-type` and `base-table`** were confirmed but never released, and are left out of the format (answer 8); the four ways stay as editor labels. Path A's conventions `typeColumn` and `typeKeyColumn` and its physical keys are not added (path A waits).
- **Rules:** MQ3041, MQ3042, MQ3043, MQ4115, MQ4116; part 2: MQ4117, MQ4118, MQ4119, MQ4120.
- **Template members:** `end.types`, `end.has_several_types`, `end.stereotypes`, `end.any_entity`, `end.common_base`, `navigation.targets`; part 2 (section 19.8): on a relation mapping `links` (each with `table` and `ends`, each end with `end`, `type`, `is_holder`, `columns`, `referenced_table`, `referenced_columns`, `foreign_key`, `type_column`, `codes`), `constants` (`column`, `values`), `attributes` (`attribute`, `column`), `left_out` and `not_stored` (`end`, `entity`), `type_codes` (`entity`, `code`); `step.conditions` (`column`, `values`), `path.from_type`, `path.to_type`, `navigation.type_joins`, `entity_mapping.type_joins`; on a binding field `holds` and `key_of`; on a binding `discriminator_value`.
- **API:** `EndRecord.types`, `.hasSeveralTypes`, `.stereotypes`, `.anyEntity`, `.commonBase`; `NavigationRecord.targets`; `RelationEndSummary.entities`, `.stereotypes`, `.anyEntity`, `.covers`; the index format `e14`; part 2: `RelationMappingRecord.links` (each end with `isHolder`, `referencedTable`, `referencedColumns`, `foreignKey`, `typeColumn`, `codes`), `.constants`, `.attributes`, `.leftOut`, `.notStored`, `.typeCodes`; `BindingFieldRecord.holds` (`id`, `type`, `key`), `.keyOf`; `EntityBindingRecord.discriminatorValue`; `DatabaseView.relationStorage` (`relationId`, `relationName`, `mappingId`, `tables`, `leftOut`, `notStored`, `gaps` with `endId`, `kind`, `text`), where `kind` is `no-foreign-key`, `shared-table`, `id-only`, `type-check`, `count-check`, `one-to-one-unique`, `link-unique`, `per-type-duplicates` or `rows-not-told-apart`. The index format stays `e14`.
- **In the engine:** the naming key `et:ends`; the end coverage helper; the entry record mirroring `TriggerPointSubject`; part 2: `RelationEndTypeStorage`, `MappingConstant`, `RColumnCondition`, `RelationPhysical.TypeLinks`, and the storage run's `CollectTypeStorage` and `FinishTypeStorage` (section 19.8).
- **In the editor:** Type, One entity, Several types, Any entity, Stereotypes, Collapse lines, Expand lines, "+N not shown", "through *key* (stereotype)", "any entity"; part 2: Held in, The other end's table, Each type's table, A link table, A table per type, Not in this database, Linked by, A key, A column per type, A type and an id, An id unique across types, A key to the shared base table, As written, Refers to, Rows of this relation, Left out, Not stored, "Nothing is stored in this database.", What the database can't enforce, Copy, Move it to the end's entry, Keep the stored code, "+N more" (the both-ends fallback). "Add to table …" waits (question 10).
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

**Part 2:** section 19.16.

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

**Part 2:** section 19.17.

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
- **Part 2:** section 19.17 (about 10,500 to 11,500 lines with both ends, counted as part 1 was).

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

**Part 2 agents:** section 19.17.

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

**Part 2:** section 19.17: about 360M cached re-reads (320M to 410M) before cuts; with the two recommended cuts and both ends, about 4.3 hours, 1.4M output, 4.6M new input and 335M cached re-reads (question 10).

**Each part stays under the 400M cap.** Part 1's likely range was 230M to 290M (it came to 296M). Part 2 needs two cuts and a checkpoint to stay under it (section 19.17, question 10). If a wave 2 agent needs a fresh agent to finish, add about 30M. If part 1's wave 2 ends above 200M, the rest is tracked step by step and the owner is told before wave 4 starts.

**Both parts:** part 1's actuals (about 8,500 lines, 1.09M output, 3.32M new input, 296M cached) and part 2's estimate with the recommended cuts (about 10,300 to 11,300 lines, 1.4M, 4.6M, 335M).

## 19. Part 2: storage in the team's tables, and several types at both ends

**Status (2026-11-09): designed and checked; the owner answered questions 7 to 10 as recommended (section 15). Built, reviewed and checked in the browser in #72, and released in 0.22.0 (answer 10). The owner kept the collapsed both-ends box and confirmed the names on 2026-11-09 (section 15).** This section is part 2's final design. It settles decisions 24 to 32 for the team's designed and imported tables (path B, the owner's answer 2). It lifts part 1's limit of several types on one end only (decision 8), as the owner asked on 2026-11-08 (section 15). An independent check found 3 blocking, 13 should-fix and 12 minor problems; all are applied (19.18). The four questions of 19.14 are answered (section 15). Every code fact was checked on branch `several-types-2` at 0.21.0 (`597ecb30`), and again by the check (`debba022`); line numbers will move. A path without a project prefix is under `src/Maquettiste.Engine/`. The examples are invented.

### 19.1 Principles

Four principles from the owner, all of 2026-11-08 (section 15), shape everything below.

1. **Entities and tables are separate models.** They don't always map clearly to each other. A relation's storage mapping is **optional, always**: a team may never map it, and its packs may read the relation and the tables separately and put them together as the team's code does. Nothing in part 2 assumes a mapping exists. Without one, the engine stores nothing for the relation, makes no column and asks for nothing beyond part 1's MQ4115 (question 7).
2. **The usual correlations, where a table can model them.** Part 2 describes the ways a table holds a link that teams commonly use (19.4). It doesn't try to describe every schema. Attribute values stored as rows, value tables split by data type, partitioned table sets, ends made of a partition and an id, and ends that point at a shared id table are possible, and packs implement them; they are neither designed nor promised here (section 13).
3. **Entities can have a different id system than tables.** An entity's key attributes need not be the id its table uses: a table may carry its own surrogate id, such as an integer or a UUID column that no attribute maps. So a link points at a unique column or a unique key of each type's table, never at "the entity's key". Every rule and every join below is worded on table columns.
4. **Every combination of end kinds.** Each end of a two-ended relation may name one entity, several types or any entity, up to any entity at both ends. Cardinality stays `min` and `max` per end and is independent of this. An n-ary relation keeps one entity per end (the owner's answer 3a). Section 19.12 designs it.

The standing rules hold: the engine never makes up a table or a column; a database holds only what is mapped to it; generation is optional; previews never touch the whole model; determinism is byte-exact.

### 19.2 In short

| Layer | What changes |
| --- | --- |
| The model | Both ends of a two-ended relation may have several types or be any entity. MQ3041 keeps its n-ary and composition-part cases (19.12) |
| The relation mapping, optional | For a relation with an end of several types, it describes per end, in today's `ends` list, how the team's tables hold each link: one foreign key, keys in several tables, a key per type, or an id column with an optional type column. `junctionTable` names a link table, as today. Two small additions serve a relation mapping with a designed link table: `constants` (fixed values that pick this relation's rows in a shared table) and `attributes[].column` (a relation attribute's column). Question 9 asks whether one-type relations get them too |
| Entity bindings | `discriminatorValue` (the entity's type code in that database); two field forms that map a link's type column and a key column per type; MQ4116 leaves binding fields |
| Rules | New: MQ4117 (what is named doesn't fit together), MQ4118 (what the database can't enforce, info), MQ4119 (a type code changed since the baseline, warning), MQ4120 (a mapping states storage but leaves a placed type out, warning). Changed: MQ3041, MQ4009, MQ4011, MQ4045, MQ4051, MQ4115, MQ4116 |
| Resolved model | A relation mapping's storage members; join paths per pair of types, with conditions on table columns |
| Packs | No template change. `sql-ddl` goldens: every way in one dialect, and the ways whose output differs in the other four (question 10) |
| The editor | The relation form allows several types at both ends, and the canvas draws them. The Storage tab describes each database's storage with a few choices and pickers, lists the types left out, and shows what the database can't enforce with the text to copy |
| A project that uses none of this | Same files, output bytes, findings, keys, records, delete plans and replays (19.15) |

### 19.3 What changed since the sketches

- **No list of shapes.** Decisions 25 to 27 named four shapes (`typeStorage`). The owner then said storage takes many shapes, and later scaled that back to the usual correlations a table can model. So the mapping describes the team's tables per end, and the entries compose. The four shapes become four combinations of entries (19.4). Question 8 asks to leave `typeStorage` out of the format.
- **No relation binding yet.** A binding for links, with read, write and delete statements like an entity binding, was weighed. It waits (section 13): the engine has no statements for links today, packs build link access from the mapping, and the mapping already names designed junctions for one-type relations. Nothing in the format blocks it.
- **Table columns, not entity keys** (principle 3). Decision 25's "every placed type's key is one attribute of one built-in type and length" becomes "the id column refers to one unique column of each type's table, of a compatible type". Decision 25's "one entity every placed type is or derives from, placed with TPH or TPT" becomes "a table that holds the rows of every placed type".
- **Both ends** may have several types (principle 4).

### 19.4 The usual ways, and how a mapping writes each

The examples: a `Comment` attached to one `Part` or `Assembly` (end `attachedTo` has several types); a `Folder` that holds parts and assemblies (end `items` has several types, end `folder` names `Folder`); and, for both ends, a `Link` from a `Comment` or a `Note` to a `Part` or an `Assembly`. Table and column names are the team's; the mapping names ids, shown here by name.

| # | Way | The team's columns | The relation mapping writes | The database enforces |
| --- | --- | --- | --- | --- |
| 1 | A key on each type's table: the end with several types holds the key | `parts.folder_id`, `assemblies.folder_id` | `{ "end": folder, "foreignKeys": [ parts.fk_folder, assemblies.fk_folder ] }` | Each key |
| 2 | A column per type: a key per type in the other end's table | `comments.part_id`, `comments.assembly_id` | `{ "end": attachedTo, "types": [ { "entity": Part, "foreignKey": comments.fk_part }, { "entity": Assembly, "foreignKey": comments.fk_assembly } ] }` | Each key. A check on how many are set is the team's |
| 3 | A type and an id | `comments.attached_type`, `comments.attached_id` | `{ "end": attachedTo, "typeColumn": comments.attached_type, "idColumn": comments.attached_id }` | Nothing on its own. A check on the codes is the team's |
| 4 | An id unique across types | `comments.attached_id` | `{ "end": attachedTo, "idColumn": comments.attached_id }` | Nothing on its own |
| 5 | A key to a shared base table | `comments.item_id`, a key to `items` | `{ "end": attachedTo, "foreignKey": comments.fk_item }` | The key; not the type |
| 6 | A link table, with attribute columns | `comment_links (comment_id, target_type, target_id, note)` | `"junctionTable": comment_links`, `{ "end": comment, "foreignKey": comment_links.fk_comment }`, `{ "end": attachedTo, "typeColumn": …, "idColumn": … }`, `"attributes": [ { "attribute": note, "column": comment_links.note } ]` | The comment key |
| 7 | A table per type | `comment_parts`, `comment_assemblies` | `{ "end": comment, "foreignKeys": [ comment_parts.fk_comment, comment_assemblies.fk_comment ] }`, `{ "end": attachedTo, "types": [ { "entity": Part, "foreignKey": comment_parts.fk_part }, { "entity": Assembly, "foreignKey": comment_assemblies.fk_assembly } ] }` | Every key |
| 8 | One link table for several relations | `links (relation_code, …)` | Any way with a `junctionTable`, plus `"constants": [ { "column": links.relation_code, "value": "AttachedTo" } ]` | As that way. A unique that MQ4118 suggests includes the constants' columns |
| 9 | Both ends: a type and an id at each side | `links (from_type, from_id, to_type, to_id, note)` | `"junctionTable": links`, `{ "end": source, "typeColumn": links.from_type, "idColumn": links.from_id }`, `{ "end": target, "typeColumn": links.to_type, "idColumn": links.to_id }` | Nothing on its own |
| 10 | Both ends: ids unique across types at each side | `links (from_id, to_id)` | `"junctionTable": links`, two entries with `idColumn` only | Nothing on its own |

- **Part 1's names** map onto these: `column-per-type` is way 2, `type-and-id` way 3, `base-table` way 5 and `table-per-type` way 7. Way 1 is the case of the owner's answer 4, which needs no `typeStorage`. Way 4 is new; the owner allowed it on an any-entity end.
- **Entries combine.** A link table may hold a key at one side and a type and an id at the other (way 6). It may also hold a key per type at a side. The database then can't refuse duplicate links, and MQ4118 says so (decision 47). A one-type end in a link table may have a type column too; it holds that entity's code.
- **What the database can't enforce** is said once per end and database, with the check or unique to copy (decision 47).

### 19.5 Decisions

Numbering continues from section 4. Each gives its confidence.

37. **The relation mapping describes the storage, per end, in today's `ends` list. It is optional.** Confidence: medium-high.
    - Today a relation mapping names what exists for a one-type relation: `foreignKey`, `junctionTable` and `ends` (`Model/Mapping.cs:60-76`; `schemas/v1/mapping.json:163-189`). Part 2 extends `ends`, as decision 26 said.
    - Without a mapping in a database, the engine stores nothing for such a relation there, as in part 1 (`Resolution/DatabaseRun.Relations.cs:53`). Packs may still store links their own way from part 1's members (`end.types`, `navigation.targets`).
    - Part 1's rule that such a mapping sets only `ignore` (`Validation/MappingRules.cs:209-225`, MQ4009) gives way to the checks below.
    - No rule asks for a mapping. MQ4011, which today asks for a key between two bound tables (`MappingRules.cs:301-305, 343-364`), still skips a relation with an end of several types.

38. **An `ends` entry says how its pointer points at the end, in exactly one way.** Confidence: medium-high.
    - **The four ways:**
      - **`foreignKey`** (today's key): one existing foreign key. On a one-type end, as today. On an end with several types, a key to a table that holds the rows of every placed target (way 5).
      - **`foreignKeys`** (new): several existing foreign keys that point at the end, one per table that holds links (ways 1 and 7).
      - **`types` with `foreignKey` items** (new): a key per type of this end, `[ { "entity": <id>, "foreignKey": <id> } ]` (ways 2 and 7). `entity` is an entity the end covers, listed once.
      - **`idColumn`** (new): the column that holds the linked object's id (ways 3, 4, 6, 9 and 10). **`typeColumn`** (new), beside it, holds the linked object's type code.
    - **What an id refers to.** By default, the primary key of each target's table, when it has one column. **`referencesColumn`** (new) names another unique column. It goes on the entry when one table holds the rows of every placed target. It goes per type when they live in several tables: `types: [ { "entity": <id>, "referencesColumn": <id> } ]`. Such `types` items are part of the id way, not a way of their own. `typeColumn` and `referencesColumn` need `idColumn` (MQ4009).
    - **The schema loosens.** `ends` items require only `end` from now on (today `["end", "foreignKey"]`, `mapping.json:185`). The editor can then save an entry half filled, and the message says what is missing. One way per entry is MQ4009, not a schema `oneOf`.
    - **One-type relations keep today's behavior.** The loosened schema reaches them too, so part 2 adds three things:
      - MQ4009 on any relation mapping for "an `ends` entry with no way". On a one-type relation it keeps the words of a missing `foreignKey`. Such a file used to fail the schema (MQ1002); it now reports MQ4009.
      - Every reader of an entry's `foreignKey` handles a missing one. In the engine: `DatabaseRun.Relations.cs:219` and `Validation/MappingRules.cs:287`, whose dictionary lookups would throw on a null key today. In the mock: `refs.ts`, `physical.ts` and `validate.ts`. In the editor: `RelationStorage.tsx`, whose junction picker types each entry with a `foreignKey` (lines 40, 230, 261), and `relationStorage.ts`.
      - A delete rule that keeps today's delete plans (19.6).
    - **Why these four:** each is one thing a column or a key does. Together they cover every usual correlation (19.4), and the format has room for more without changing them.

39. **Where each link lives (its holding table) follows from what is named.** Confidence: medium-high.
    - **A target** is a covered entity that is placed in the database, has a table, and whose rows aren't in the table of a covered base. The rule is the same for every end. A one-type end usually has one target, its entity. Over a TPC hierarchy with an abstract root, which has no table (`Resolution/TableBuild.cs:190`), the targets are its concrete entities, as today's foreign key spreads over them (`DatabaseRun.cs:743-760`).
    - **Left out:** a covered entity with no placement isn't a target, and gets no finding (decision 25). That includes an entity whose binding only reads (write `none`, or a view source), since such a binding gives no placement (`DatabaseRun.Bindings.cs:92-99`). The user guide says so.
    - **Order.** An end's targets follow its type order (decision 5). Each type gives itself when it is a target, then its covered derived entities that are targets, by qualified name, then id. `links`, `left_out`, `not_stored` and `type_joins` follow this order, so output stays byte-exact.
    - **Pointers.** For each pair of targets, one per end, the pointer is the entry's one way (`foreignKey`, or `idColumn` with its `typeColumn`), or its `types` item for that target, or the item of `foreignKeys` that the holding table owns.
    - **With `junctionTable`,** the link table holds every link. Every end needs an entry, as for today's designed junction (MQ4011, `MappingRules.cs:291-299`), and every pointer is in that table.
    - **Without it, and one end has no entry,** that end's rows hold the link. The holding table is the table of that end's target, and the other end's pointer must be in it.
    - **Without it, and every end has an entry,** the end with `types` (with `foreignKey` items) gives each of its targets a link table of its own (way 7): the table that owns the target's key. The other end's pointer there is the item of `foreignKeys` that table owns.
    - **MQ4117 when the entries don't fit together:**
      - two `foreignKeys` items owned by one table;
      - every end has an entry, there is no `junctionTable`, and no end has `types` with `foreignKey` items, so no table holds the links (a one-to-one with entries at both ends is one case);
      - a pointer outside its pair's holding table;
      - one end's targets live in several tables and the other end's pointer is one column. It needs `foreignKeys`, one per table, or a key to a shared table. Column pointers spread over several tables wait (section 13);
      - both ends have `types` and there is no `junctionTable`. A link table per pair of types waits (section 13).

40. **A table row holds one link.** When an end's own rows hold the link, the other end must be to-one (`max` 1), as today's to-one key in a binding (`Resolution/DatabaseRun.Bindings.cs:506`); MQ4117 otherwise. In a one-to-one, either end may hold it: the entries say which, so `foreignKeyEnd` isn't used (MQ4009). A many-to-many needs a link table (MQ4117 otherwise). Confidence: high.

41. **Ids and keys are table columns** (principle 3). Confidence: high.
    - **A foreign key leads to the right table** when its referenced table holds the rows of the target it serves. That is the target's table, or the table of a base that holds the target's rows (TPH or TPT, or a base bound to the same table). A key at a one-type end leads to its target's table. A single key at an end with several types leads to a table that holds the rows of every placed target (way 5). MQ4117 otherwise.
    - That the referenced columns are a key of that table is MQ4059 already (`Validation/PhysicalRules.cs:779-823`), for designed tables.
    - **An id column refers to one column of each target's table:** `referencesColumn` when named, else the table's primary key when it has one column. A primary key of several columns, or no primary key, needs `referencesColumn` (MQ4117). `referencesColumn` on the entry while the targets live in several tables is MQ4117 too: name one per type.
    - **The referenced column must be unique:** the table's primary key, or a unique key (a unique constraint, or a unique index without a filter, and on Oracle only a constraint). MQ4059 has this test, but it checks table files only and returns early when no columns are named (`PhysicalRules.cs:784-786`). Part 2 writes the same test again over resolved tables, after `FinishTables`, so tables the engine makes are covered too.
    - **The id column's type must match the referenced column,** as a foreign key column must (MQ4005's comparison, `Resolution/DatabaseRun.Finish.cs:254-264`). MQ4117 otherwise.
    - Nothing compares a column with an entity's key attributes. A surrogate id that no attribute maps is fine.
    - **A target's table** is the table its placement holds rows in (`Placement.Table`, `Resolution/TableBuild.cs:191-192`): a bound table, a binding's write table (else its source table), or a table the engine makes.

42. **A type code is decision 28's, and an entity binding may set it.** Confidence: medium.
    - The code of an entity in a database is the `discriminatorValue` of its binding to that database. Without one, it is the `discriminatorValue` of its mapping element there. Without either, it is the entity's name.
    - The storage run reads these from the files, so it needs no resolved binding (19.8).
    - **Why a binding key:** an entity bound to a database ignores its mapping element there (MQ4054, `Resolution/DatabaseRun.cs:657-675`). So a mapping element can't set a bound entity's code. The binding is that entity's mapping in the database (erratum E43). The key has the mapping's name, type (`["string", "integer"]`, `mapping.json:125-127`) and meaning.
    - **A number that names a type in a type-definition table** is an integer `discriminatorValue`. A text type column holds it as text (`'17'`). An integer type column needs every code it holds to be an integer.
    - **A type column holds the codes** of the entities its end covers that can have objects (not abstract) and are placed in the database. Derived ones are included, since an object's code names its own entity. A one-type end's type column holds its entity's code and those of its derived entities.
    - **MQ4117:** two of them with one code, with the fix to set `discriminatorValue`. Also a code its column can't hold: a column that is neither text nor an integer, an integer column and a code that isn't an integer, or a text column shorter than a code.
    - **`discriminatorValue` outside an inheritance hierarchy** (MQ4009 today, `MappingRules.cs:147-148`) is allowed when an end with several types covers the entity. The binding's key follows the same rule. Incremental validation brings these files back when coverage changes (19.7).

43. **An any-entity end: no way that grows with the model** (the owner, 2026-11-08). A type and an id, an id alone, a key to a shared table and a link table holding those are allowed. A key per type pointing at it (`types` items with `foreignKey`, ways 2 and 7) and keys spread over its types' tables (way 1, where the any-entity end holds the key) are refused (MQ4117), since every new entity would need a new column or table. `types` items with `referencesColumn` are allowed: they add no column. Confidence: high.
    - **For the owner to see:** allowing a key to a shared table and a link table here follows the owner's reason ("no storage that grows with the model"). The owner named only a type and an id, and an id alone. The pull request says so.

44. **`constants`: fixed values on a link table.** `[ { "column": <id>, "value": … } ]`. Each is a filter on the link table's rows and the value an insert sets, such as a relation code in a table that holds several relations (way 8). Confidence: medium-high.
    - **A new record, `MappingConstant`.** The JSON shape is a binding's constant (`Model/Bindings.cs:122-131`), but `column` takes a column id only (`IndexKinds = ["column"]`), where a binding's constant also takes a physical name (`Keyed = true`). So a renamed column is followed (19.11). The link table is always designed, so a column id always exists.
    - It needs `junctionTable`, and each column must be one of that table's columns (MQ4009). A constant's column can't also be a pointer column (MQ4009). A value its column can't hold is MQ4051, as for a binding's constant.
    - **Which relation mappings take it:** every relation mapping with a designed `junctionTable`, one-type relations included, since a shared link table holds those too (answer 9).
    - Two mappings in one database whose links one table holds, with the same constants, would read each other's links: MQ4117. It is reported only when one of the two writes `constants` or describes a relation with several types, so today's models get nothing new.
    - A relation code is the team's value. There is no relation code scheme.
    - Deleting a constant's column follows a binding's constant: `delete-dependents` removes the constant, `remove-references` refuses.

45. **`attributes[].column`: a relation attribute's column, when one table holds every link.** Confidence: medium-high.
    - Today a relation mapping can't name an attribute's column: the check needs a bound `table` (`MappingRules.cs:186-192`), which a relation mapping lacks, and a designed junction maps its attribute columns to nothing (`DatabaseRun.Relations.cs:213-232`).
    - Part 2 allows `column` when the links live in one designed table: the link table, or the other end's table. The column must be in it (MQ4009 for a link table, MQ4117 for an end's table). With links in several tables, it is MQ4117: one column can't be in several tables. A form per table waits (section 13).
    - A value-object or collection relation attribute can't use `column` (MQ4009): one column holds one plain value. On a mapping of a relation with an end of several types, `storage` and `prefix` are MQ4009, since they shape engine-made columns (path A).
    - **Which relation mappings take it:** as for `constants` (answer 9). A one-type relation whose links live in an end's table doesn't take it in 0.22.0.
    - On a link table, the column gets the attribute, as an engine-made junction's column does. On an entity's table, the column keeps its own meaning, since packs read unmapped columns as storage columns (`packs/csharp-dapper/_dapper.scriban:101`); the pairing is recorded in the relation mapping only.

46. **Entity bindings map the link's columns** (settles decision 26). Confidence: medium-high.
    - Beside today's `<endId>` (a to-one end's key), two field forms: **`<endId>.type`**, the column holding the linked object's type code; and **`<endId>.<entityId>`**, the column that points at a linked object of that entity (a column per type). `<endId>` on an end with several types maps its id column. What a column points at in the type's table is the mapping's to say (decision 41), not the entity's key.
    - A field may name an end the binding's entity holds: the entity is covered by the other end, and the named end is to-one. The skips for several types (`DatabaseRun.Bindings.cs:443-445, 505-506`) go. The test `end.Opposite is { Entity: … }` (`:506`) reads coverage, so way 1's `<endId>` on each type's binding works.
    - Names: `<navigation>Type` and `<navigation><Entity>Id`, else the role's, as today's `<navigation>Id` (`:511`).
    - **Each field shape and its finding,** for the engine, the mock and the parity fixtures. "Holds the end" means the binding's entity is covered by the other end, and the end is to-one.

      | Field `attribute` | When | Outcome |
      | --- | --- | --- |
      | `<endId>` | The entity holds the end | Resolves. On an end with several types, it maps the id column (`holds` is `id`). On a one-type end, it maps the key column, as today (way 1; `holds` is null) |
      | `<endId>.type` | The entity holds the end, which has several types | Resolves; `holds` is `type` |
      | `<endId>.<entityId>` | The entity holds the end, which has several types and covers that entity | Resolves; `holds` is `key`, `key_of` is the entity |
      | Any of the three | The end is to-many, or the other end doesn't cover the entity. Folder's binding naming `items` is one | MQ4045, today's text, as for a one-type to-many end today |
      | `<endId>.type` or `<endId>.<entityId>` | A one-type end, or an entity the end doesn't cover | MQ4045, today's text |
      | `<endId>.<entityId>`, with a mapping in the database | The entity isn't a target there, or its pointer has several columns | MQ4117. A field maps one column; the binding's `columns` list accounts for a pointer of several columns (MQ4047 otherwise) |
      | Any form, with a mapping in the database | The field maps another column than the one the mapping names for it | MQ4117 |

    - With no mapping, the fields stand alone: the binding reads and writes the columns, and nothing more is checked.
    - **MQ4116 leaves bindings.** Every binding field now resolves or gets MQ4045. So `NamesSeveralTypesEnd` (`DatabaseRun.Bindings.cs:443-445`) no longer hides MQ4045 for a binding field, and the validation check at `Validation/RelationTypeRules.cs:204-221` goes. MQ4116 keeps its query cases.
    - **Where it is checked:** MQ4045 where fields resolve today. The MQ4117 rows where bindings resolve (`ResolveBindings`, inside `Publish`), once the storage run has filled the mapping's pointers (19.8).

47. **What the database can't enforce is info, once per end and database** (MQ4118; settles decisions 29 and 31). Confidence: high.
    - The finding lists the gaps over the end's holding tables. Each gap has a `kind`, which the database view carries too (19.8):

      | `kind` | The gap | Text to copy |
      | --- | --- | --- |
      | `no-foreign-key` | No foreign key on the pointer. The database can't refuse a link to a missing row, nor apply the end's delete rule or a composition's cascade | None |
      | `shared-table` | A key to a shared table. It can't refuse a row of a type the end doesn't cover | None |
      | `id-only` | An id alone. It can't check the type the id points at | None |
      | `type-check` | A type column, with no check found on its codes | A check |
      | `count-check` | Keys per type, with no check found on how many are set | A check |
      | `one-to-one-unique` | A one-to-one, with no unique found on the pointer | A unique |
      | `link-unique` | A link table for a relation that doesn't allow duplicates, with no unique found over both ends' pointers | A unique, with the constants' columns |
      | `per-type-duplicates` | Keys per type in a link table. A unique over nullable columns doesn't stop duplicate links in most databases (decision 25's reason) | None |
      | `rows-not-told-apart` | A target's table holds rows of other entities, and nothing tells them apart: no discriminator and no binding constants, as when two entities are bound to one designed table | None |

    - **The text to copy** is in the database's dialect and quoting. A check uses decision 27's texts, with codes sorted ordinally and an integer code unquoted, named by `checkName` with `{name}` set to the end's role. A unique is `UNIQUE (…)`, or on SQL Server a unique index filtered to rows where the pointer is set (decision 31). In a shared link table (way 8), the unique includes the constants' columns, so two relations that share the table don't collide on the same pair.
    - **Keys per type in a link table stay allowed.** Refusing them (MQ4117) was weighed and left out: decision 50 keeps refusals for contradictions, and this is only weaker integrity. So MQ4118 says duplicates can't be refused there, and offers no unique that wouldn't work.
    - **"Found" is a hint, not a proof.** A check counts when it is a column check on the column, or its condition reads the column, or its SQL text names the column as a word. A unique counts when it is a unique key, a unique index without a filter, or the primary key, over the pointer's columns. The message says "finds no check that names column …".
    - The engine writes none of it; that waits with path A.

48. **Joins are paths between table columns, one per pair of targets** (settles decision 30). Confidence: medium-high.
    - **`RJoinStep` gains `conditions`:** a list of a column and the values it holds (`RColumnCondition`). Today's paths have none. A step gets a condition:
      - through a holding table with a type column: "the type column holds the target's codes";
      - through a link table with `constants`: each constant;
      - into a target's table that holds rows of entities the target doesn't stand for. A target stands for itself and its covered derived entities whose rows are in its table. The condition is what picks the target's rows: its TPH discriminator (`REntityMapping.DiscriminatorColumn`, `Resolution/ResolvedConceptual.cs:560`), or its binding's constants. A TPH-derived target in its root's table is one case, even when it is the end's only target. When nothing picks the rows, the step has no condition, and MQ4118 reports `rows-not-told-apart`.
    - **A binding's constants come from its file.** `BuildJoins` runs before bindings resolve (19.8). So it reads the constants from the binding file, and resolves each column against the target's table by key or name, as a binding does. A constant whose column isn't there gives no condition; MQ4048 or MQ4049 already reports it.
    - **`RJoinPath` gains `from_type` and `to_type`:** the targets at the path's two ends, each set only when that end has several types; null on today's paths.
    - **`RNavigation` gains `type_joins`:** by database name, the path of every pair of targets, ordered by the target at the navigation's from end, then at its to end, each in target order (decision 39). **`joins[db]`** is set only when there is exactly one path: way 5, or a single pair of targets in that database. Otherwise only `type_joins` holds the paths.
    - **`REntityMapping`:** `joins[name]` holds the entity's own path when there is one (a navigation from a target, such as `Part.comments`). A new `type_joins[name]` holds the paths of a navigation that leads to an end with several types. It is keyed on the entity, as `joins` is today. A bound entity gets no `REntityMapping` (`DatabaseRun.Finish.cs:421-424`), so for a holder with a binding the paths are on the navigation only.
    - **`BuildJoins`** (`DatabaseRun.Finish.cs:505-544`) takes the pairs from `RelationPhysical.TypeLinks` instead of matching `navigation.From.Entity` (`:527`). It stays where it is in the run and keeps its parallel loop. `JoinFor` (`:552-591`) builds each step from the pointer's columns and the referenced columns; its `OneEntity` read (`:559`), which throws on an end with several types (`ResolvedConceptual.cs:426`), takes the target instead.
    - Join paths reach templates only; no record carries them today, so no record changes.

49. **MQ4115 stays one info rule. A new warning, MQ4120, reports a placed target that no pointer reaches.** Confidence: high. The severities are answer 7's.
    - A rule has one default severity (`Diagnostics/RuleCatalog.cs:255`), and `validation.rules` overrides it per rule id. So two cases that need different severities can't share one rule.
    - **MQ4115** keeps part 1's text and stays a validation rule (`Validation/RelationTypeRules.cs:166-191`). It reports a database where every end has a covered type placed and nothing stores the relation. Today it skips a database whose mapping sets `ignore` (`:178-179`). Part 2 also skips a database whose mapping stores the relation: it names a `junctionTable` or has an `ends` entry. A mapping with neither, and no `ignore`, stores nothing, so MQ4115 treats it as no mapping.
    - **MQ4120 (new, warning)** reports, from the database run, a placed target that a mapping's entries reach with no pointer: "Relation 'AttachedTo' covers entity 'Memo' in database 'main', but its mapping names nothing that points at it there, so links to Memo objects aren't stored." It fires only where a mapping in that database states storage, so a model that uses part 1 can't get it on the upgrade.
    - **Which rule fires.** A single `foreignKey` that misses a target's table is MQ4117 (the wrong table). A `foreignKeys` or `types` list that leaves a target out is MQ4120.

50. **Refusals: only where a mapping would contradict itself or mean nothing.** Confidence: high.

    | Refusal | Why it stays |
    | --- | --- |
    | MQ3041: several types on an n-ary relation's end; any entity as a composition's part | The owner's answer 3a; a composition owning every entity means nothing (decision 9) |
    | MQ4009: no way or two ways in one entry; `typeColumn` or `referencesColumn` without `idColumn`; `types` items of the wrong kind; two entries for one end; one column used twice in an entry | An entry that says two things, or nothing |
    | MQ4009: `foreignKeyEnd`, `promoted`, `promotedName`, `storage` and `prefix`, top-level `foreignKey` on such a relation | They decide engine-made shapes, or repeat what the entries say (one place per fact) |
    | MQ4117: a key that leads to the wrong table; a pointer outside its holding table; entries that give no holding table; a column that isn't unique or doesn't match; a primary key of several columns with no `referencesColumn`; a `types` item for an entity that isn't a target; two types with one code; a code its column can't hold; a row holding a link to a to-many end | Contradictions: the stored links would be wrong or couldn't be written |
    | MQ4117: a key per type on an any-entity end | The owner's rule: no storage may grow with the model |
    | MQ4117: a link table per pair of types; column pointers spread over several holding tables; one attribute column for links in several tables | The format can't say it yet (section 13). Named as waiting, not as wrong |
    | MQ4116: a query field or collection through such an end | A query names one column path; through such an end there is one per type, and the query language can't yet say which (section 13) |
    | MQ7105: seeds of such links | A seed column can't yet name the type (section 13) |

51. **The mapping's other keys, for a relation with an end of several types.** `shape` may be left out. When written, it must agree with the entries, else MQ4009: `junction` when the links live in link tables (a `junctionTable`, or a table per type as in way 7), `foreign-key` when they live in an end's tables. `ignore` keeps its meaning. Confidence: medium-high.

52. **Migrations follow the team's tables** (settles decision 32 for path B). Confidence: medium.
    - The team edits its table files, and migration facts follow those changes as today. A type added means a column, a key or a table the team adds; a type removed drops them, which needs a fact like any drop (MQ4102).
    - **MQ4119, a changed type code.** A type code that changes changes no table, so no fact asks for it, and stored links that hold the old code stop matching. `validate --against` reports it as a warning, beside MQ4112 (`Migrations/BaselineCheck.cs:472`, in the pass at `ModelStore.Migrations.cs:1286-1288`), for each database whose mapping has a type column that holds the code.
    - **Where the codes come from.** The baseline's codes are read from the baseline's files, with no resolution: the binding's `discriminatorValue`, then the mapping's, then the name. The working side's codes and type columns come from the run's `type_codes`.
    - **How it clears.** No fact settles it, so it repeats until the baseline moves past the change. A suppression works, as for any finding.
    - **Quick fix: "Keep the stored code".** The usual cause is an entity renamed with no `discriminatorValue`. The fix writes the old code as `discriminatorValue` on the entity's binding to that database, else on its mapping element there, which it creates when there is none. The stored links match again, with no migration.
    - A value-map fact for type codes stays in section 13.

53. **Several types or any entity at both ends** (lifts decision 8; the owner, 2026-11-08). Section 19.12. Confidence: high.

### 19.6 Format

| File | Key | Type | Notes |
| --- | --- | --- | --- |
| `mapping.json` | `ends[].foreignKeys` (after `foreignKey`) | list of foreign key ids, `minItems` 1, `uniqueItems`, `"default": []` | Decision 38 |
| `mapping.json` | `ends[].types` (after `foreignKeys`) | list of `{ entity, foreignKey }` or `{ entity, referencesColumn }`, `minItems` 1, `"default": []` | Title "End type storage". Each item has `entity` and exactly one of the two |
| `mapping.json` | `ends[].idColumn`, `ends[].typeColumn`, `ends[].referencesColumn` (after `types`, in that order) | column ids; `referencesColumn` a column id or key | Decision 38 |
| `mapping.json` | `ends[]` `required` | `["end"]` | Was `["end", "foreignKey"]` (line 185). A valid file of today stays valid. A file with an entry and no `foreignKey` used to fail with MQ1002; it now reports MQ4009 (decision 38) |
| `mapping.json` | `constants` (after `ends`) | list of `{ column, value }`, `"default": []` | Decision 44. `column` is a column id |
| `mapping.json` | `x-order` | | `constants` after `ends` in the mapping's order (`mapping.json:6-41`); the new entry keys in the `ends` items' order, as above |
| `mapping.json` | descriptions of `ends`, `attributes[].column` | | Decisions 38 and 45 |
| `entity.json` | binding `discriminatorValue` (after `delete`, also in the binding's `x-order`) | string or integer | Decision 42 |
| `entity.json` | description of a binding field's `attribute` | | The forms of decision 46 |
| `relation.json` | descriptions of `types` and `anyEntity` | | Both ends (19.12). No new key |
| `openapi.yaml` | `RelationMappingRecord`, `BindingFieldRecord`, `EntityBindingRecord.discriminatorValue`, `DatabaseView.relationStorage` | | 19.8 |

**Writer defaults.** The new lists have `"default": []`, as `attributes` and `ends` have, and the new columns have none. So the canonical writer leaves them out when unset, and `format` keeps today's bytes.

**In code.**
- `RelationEndMapping` (`Model/Mapping.cs:103-113`) makes `ForeignKey` optional and gains `ForeignKeys`, `Types` (a new record `RelationEndTypeStorage { Entity, ForeignKey?, ReferencesColumn? }`), `IdColumn`, `TypeColumn` and `ReferencesColumn`.
- `Mapping` gains `Constants` (`IReadOnlyList<MappingConstant>`, a new record `{ Column, Value }`; decision 44).
- `EntityBinding` (`Model/Bindings.cs:13-73`) gains `DiscriminatorValue` (`JsonElement?`).
- **References.** Keys carry `[ElementRef(IndexKinds = ["key"])]` and entities `ElementKind.Entity`, as `ForeignKey` does today. `idColumn`, `typeColumn` and a constant's `column` carry `["column"]`, as `AttributeMapping.Column` does: they are columns of designed tables only (MQ4009). `referencesColumn`, on the entry or in a `types` item, is `Keyed = true`, as a designed foreign key's `referencesColumns` is (`Model/Physical.cs:617`). The referenced table may be one the engine makes, whose columns have keys, not ids. References, renames, cascades and batch names then work through the generic walker.
- **Deletes keep today's plans.** The cascade reads the schema's `required` (`Loading/ChangePlanner.Cascade.cs:12-18, 801-803`), which no longer names `foreignKey`. So it gains a rule, as it has for a relation end's `types` (`:372-382`):
  - When a delete would leave an `ends` entry with no way, `delete-dependents` removes the whole entry and `remove-references` refuses. On a one-type relation the refusal keeps today's words. That is exactly what happens today while `foreignKey` is required.
  - `typeColumn` and `referencesColumn` belong to the id way: losing one would change what the id means, so they follow the same rule.
  - An item of `foreignKeys` or `types` follows the generic rule for a list item. The last one leaves the entry with no way, so the rule above applies.
  - Deleting an entity removes the `types` items that name it, under either choice, as decision 21 does for an end's entries.
  - The mock follows the same rule through its `required` flags (`refs.ts`, `cascade.ts`).

Way 9, both ends, a type and an id at each side, with an attribute column:

```json
{ "kind": "mapping", "name": "Link in main", "database": "<main>", "relation": "<Link>",
  "junctionTable": "<links>",
  "ends": [
    { "end": "<source>", "typeColumn": "<links.from_type>", "idColumn": "<links.from_id>" },
    { "end": "<target>", "typeColumn": "<links.to_type>", "idColumn": "<links.to_id>" }
  ],
  "attributes": [ { "attribute": "<note>", "column": "<links.note>" } ],
  "constants": [ { "column": "<links.relation_code>", "value": 17 } ] }
```

Way 7, a table per type. Each key refers to whatever key its foreign key names in the type's table, often a surrogate id no attribute maps:

```json
{ "kind": "mapping", "name": "AttachedTo in main", "database": "<main>", "relation": "<AttachedTo>",
  "ends": [
    { "end": "<comment>", "foreignKeys": [ "<comment_parts.fk_comment>", "<comment_assemblies.fk_comment>" ] },
    { "end": "<attachedTo>", "types": [
      { "entity": "<Part>", "foreignKey": "<comment_parts.fk_part>" },
      { "entity": "<Assembly>", "foreignKey": "<comment_assemblies.fk_assembly>" } ] }
  ] }
```

Way 4, ids unique across types, where the id refers to each type table's `uid` column, a unique key that isn't the primary key (decision 41):

```json
{ "kind": "mapping", "name": "AttachedTo in main", "database": "<main>", "relation": "<AttachedTo>",
  "ends": [
    { "end": "<attachedTo>", "idColumn": "<comments.attached_uid>", "types": [
      { "entity": "<Part>", "referencesColumn": "<parts.uid>" },
      { "entity": "<Assembly>", "referencesColumn": "<assemblies.uid>" } ] }
  ] }
```

An entity binding that holds way 3:

```json
{ "id": "…", "database": "<main>", "source": "<comments>", "discriminatorValue": 4,
  "fields": [
    { "attribute": "<Comment.id>", "column": "id" },
    { "attribute": "<attachedTo>.type", "column": "attached_type" },
    { "attribute": "<attachedTo>", "column": "attached_id" } ] }
```

### 19.7 Rules

| Rule | Sev | Change |
| --- | --- | --- |
| MQ3041 | E | Drops "several types on both ends" (`RelationTypeRules.cs:96-98`). Keeps the n-ary case and any entity as a composition's part |
| MQ4009 | E | The file cases below |
| MQ4011 | E | A link table of a relation with an end of several types needs an entry for every end, as a designed junction does. Its own words, since today's text asks for a foreign key (`MappingRules.cs:296`): "Link table 'comment_links' holds relation 'AttachedTo', so end 'attachedTo' needs an 'ends' entry that says how the table points at it." Today's text stays for one-type relations |
| MQ4045 | E | Same texts. It now covers every binding field shape that doesn't resolve, on relations with an end of several types too (decision 46). The catalog text names the two new forms |
| MQ4051 | W | Also a relation mapping's constant that its column can't hold |
| MQ4115 | I (answer 7) | Part 1's text. Also skips a database whose mapping stores the relation (decision 49) |
| MQ4116 | E | Leaves binding fields (decision 46). Kept for query fields and query collections, also through the one-type end of such a relation, even where one path exists (Part's `folder` in way 1). The catalog text drops "which no database stores yet" (`RuleCatalog.cs:256`): "A query field or a query collection names a relation end with several types, a navigation to one, or another end of a relation with such an end; a query can't yet say which type's table it joins." |
| MQ4117 | E | **New.** The run cases below. Examples: "Foreign key 'fk_part' of table 'comments' is named for type 'Part' of end 'attachedTo', but it refers to table 'assemblies', which doesn't hold Part rows." "Column 'attached_id' of table 'comments' refers to column 'code' of table 'parts', which is neither its primary key nor a unique key." "Types 'Part' and 'PurchasedPart' both have code 'P' in database 'main', so column 'attached_type' can't tell them apart; set discriminatorValue on one." "End 'attachedTo' is any entity, so it can't have a key per type: every new entity would need a column. Use a type and an id, or an id alone." |
| MQ4118 | I | **New.** What the database can't enforce for an end (decision 47). "Database 'main' holds end 'attachedTo' of relation 'AttachedTo' as a type and an id in table 'comments', so it can't refuse a link to a missing part or assembly, nor apply the delete rule 'cascade'; and it finds no check that names column 'attached_type'. To check the codes, add: CHECK (\"attached_type\" IN ('Assembly', 'Part'))" |
| MQ4119 | W | **New** (decision 52). "Since the baseline, the type code of 'Part' in database 'main' changed from 'Part' to 'P', and column 'attached_type' of table 'comments' holds codes of relation 'AttachedTo'; links stored with the old code no longer find their object. Update those rows, or keep the stored code." Quick fix: Keep the stored code |
| MQ4120 | W | **New** (decision 49). A placed target that a mapping's entries reach with no pointer |

**MQ4009, the file cases** (validation, `Validation/MappingRules.cs`).
- **On a mapping of a relation with an end of several types:**
  - an `ends` entry with no way, or with two. `types` counts as a way only when its items carry `foreignKey` (decision 38);
  - `typeColumn` or `referencesColumn` without `idColumn`;
  - `types` items of the wrong kind: items with `foreignKey` beside `idColumn`; items with `referencesColumn` without `idColumn`; one list that mixes the two kinds;
  - `referencesColumn` on the entry and in `types` items too;
  - two entries for one end (the schema doesn't stop it, and `MappingRules.cs:282-296` never checks it);
  - `typeColumn` and `idColumn` naming one column;
  - `foreignKeys` together with `junctionTable`: in a link table, an end is pointed at by one key, a key per type, or an id;
  - a key that isn't a foreign key of a designed or imported table of the database, or a column that isn't a column of one (today's texts);
  - `foreignKeyEnd`, shape `promoted`, `promotedName`, top-level `foreignKey`; `storage` or `prefix` in `attributes`; a `shape` that disagrees (decision 51);
  - `constants` without `junctionTable`; a constant column outside that table; a constant column that is also a pointer column;
  - `attributes[].column` outside the link table, when there is one; on a value-object or collection attribute.
- **On any relation mapping:**
  - an `ends` entry with no way. On a one-type relation it keeps the words of a missing key: "End entry 0 names no 'foreignKey'; junction table 'part_tools' is designed, so the entry needs the foreign key that references its end's table.";
  - `foreignKeys`, `types`, `idColumn`, `typeColumn` or `referencesColumn` on a relation whose ends each have one type: "…needs a relation with an end of several types";
  - `constants` and `attributes[].column` on a one-type relation, as question 9 settles: with the recommendation, the `constants` and `attributes[].column` cases above, against a designed `junctionTable`; with the alternative, each one "…needs a relation with an end of several types".
- **On an entity mapping:** `constants`. **On a binding:** `discriminatorValue` on an entity outside a hierarchy that no end with several types covers.
- **Relaxed:** `discriminatorValue` outside a hierarchy when such an end covers the entity; `ends` without `junctionTable` on such a relation (`MappingRules.cs:282-283`).

**MQ4117, the run cases** (the database run, with placements and resolved tables; decisions 39 to 46).
- **Holding tables:** a key that leads to the wrong table; a pointer outside its pair's holding table; two `foreignKeys` items owned by one table; every end with an entry, no `junctionTable` and no `types` with `foreignKey` items; both ends with `types` and no `junctionTable` (waits); one column pointer for targets in several tables (waits); a row holding a link to a to-many end; a many-to-many with no link table.
- **Ids:** no `referencesColumn`, and a target table whose primary key has several columns or that has none; `referencesColumn` on the entry while the targets live in several tables; a referenced column that is neither the primary key nor a unique key; an id column whose type doesn't match its referenced column.
- **Types and codes:** a `types` item for an entity the end doesn't cover, checked by the run since carriers change; a `types` item for a covered entity that isn't a target, because its rows are in a covered base's table; two types with one code; a code its column can't hold.
- **Any entity:** a key per type at an any-entity end, or keys spread over its types' tables.
- **Shared tables:** two mappings that read each other's links (decision 44); an attribute column that isn't in the end's table that holds the links; one attribute column for links in several tables (waits).
- **Bindings,** where bindings resolve: a field that maps another column than the mapping names; `<endId>.<entityId>` for an entity that isn't a target, or whose pointer has several columns (decision 46).

**Which rule fires,** written down for the parity fixtures: a single `foreignKey` that misses a target's table is MQ4117; a `foreignKeys` or `types` list that leaves a target out is MQ4120.

Every new rule and case needs an end with several types, or one of part 2's new keys. The one exception is MQ4009's entry with no way on a one-type relation, which was MQ1002 before. The catalog texts of MQ3041, MQ4009, MQ4011, MQ4045, MQ4051, MQ4115 and MQ4116 change, so `list_validation_rules` answers differently for every project.

**Incremental validation.**
- The new keys are references, so a save of a named table checks the mapping again through today's reference dependents (`Validation/ModelValidator.cs:721-731`).
- **The relaxed MQ4009 on `discriminatorValue`** depends on coverage. A relation, stereotype or entity save can move coverage, while the finding sits on a mapping file or an entity file. Today's dependents don't bring those files back (`ModelValidator.cs:720-790`; `SeveralTypesPeers`, `:803-840`, brings relations only). So, while a relation with an end of several types exists:
  - a save of such a relation brings back the mapping files and entity files of the entities it covers, before and after the save;
  - a save that moves carriers brings back those of the entities whose coverage moved.
- Without such relations nothing extra runs. Reporting this case from the database run instead was weighed and left out: it would split MQ4009 across two sources, and the run's findings are dropped while validation has errors (19.10).
- The run's checks (MQ4117, MQ4118, MQ4120) run with every validate that resolves, as today. Decision 12's dependents for MQ4115 stay.

### 19.8 Resolved model, records, views and tools

**`RRelationMapping`** (`Resolution/ResolvedConceptual.cs:489-508`; `relation.mappings[db]` in templates) gains these members, for a relation with an end of several types and for a mapping with `constants` or attribute columns. Each is empty or null otherwise.

| Member | What it holds |
| --- | --- |
| `links` | One entry per pair of targets, in target order (decision 39): `table` (the holding table) and `ends`, one per end in file order. Each end has `end`, `type` (the target at that end), `is_holder` (whether this end's rows hold the link), `columns` (the pointer's columns in the holding table), `referenced_table` and `referenced_columns` (named as on a foreign key, `ResolvedPhysical.cs:354-357`), `foreign_key` (when the pointer is a key), `type_column` and `codes` (when it has a type column) |
| `constants` | `column` and `values` (`RColumnCondition`) |
| `attributes` | `attribute` and `column` |
| `left_out` | `end` and `entity`: covered entities not placed in the database, by end in file order, then qualified name, then id. No finding (decision 25) |
| `not_stored` | `end` and `entity`: placed targets no pointer reaches (MQ4120), in the same order |
| `type_codes` | `entity` and `code`, for every entity a type column holds, by qualified name, then id |

`shape` is `junction` when the links live in link tables, else `foreign-key`. `junction_table` is set when one link table holds every link, and is null for a table per type (way 7). `foreign_key` stays null for such a relation. A pack that writes DDL from tables needs none of this: the tables are designed.

**Designed objects a mapping names** keep today's flags. A link table gets `is_junction` and `relation`, as a designed junction does (`DatabaseRun.Relations.cs:224-227`). `relation` names one relation: for a shared table, the first in relation order; the relation mappings list every use. Each named foreign key gets `relation` and `end`, as today's two shapes set them: a key in a link table carries the end it points at, and a key in an end's table carries that holding end. So `sql-ddl`'s "Junction table." comment (`table.scriban:4`) and `seed_sql`'s key lookup (`_reference.scriban:815, 822`) keep working.

**Joins:** `RColumnCondition`, `RJoinStep.conditions`, `RJoinPath.from_type` and `to_type`, `RNavigation.type_joins`, `REntityMapping.type_joins` (decision 48).

**Bindings:** `RBindingField` (`Resolution/ResolvedBindings.cs:102-163`) gains `holds` (`id`, `type` or `key`, for the forms of decision 46; null on today's fields) and `key_of` (the entity of a `<endId>.<entityId>` field). `REntityBinding` gains `discriminator_value`.

**The database run, in order.** Today's order is `ResolveRelation`, `FinishTables`, `FinishRelationMappings`, `BuildEntityMappings`, `BuildJoins`, then `Publish`, which resolves bindings (`DatabaseRun.cs:97-115`). The storage of a relation with an end of several types runs in four steps, each where its inputs exist:
1. **In `ResolveRelation`** (`DatabaseRun.Relations.cs:48-70`), which needs placements only. A relation with an end of several types is skipped, as today, when it has no mapping in the database or its mapping sets `ignore`; so is one whose mapping states no storage (decision 49). Otherwise a new `CollectTypeStorage` works out the targets, the holding tables and the pointer ids (decision 39). It finds designed keys through `_foreignKeysById` (`DatabaseRun.cs:36`), sets the flags on designed objects, and records `RelationPhysical.TypeLinks`.
2. **After `FinishTables`, beside `FinishRelationMappings`** (`DatabaseRun.Finish.cs:359-386`). Keys, primary keys, uniques and stereotype members are resolved by then (`AddInheritedMembers`, `Finish.cs:23, 41-90`). A new `FinishTypeStorage` resolves each pointer's referenced table and columns. It runs the MQ4117 checks of decisions 39 to 45, works out the codes from the files (decision 42), the MQ4118 gaps with their texts, MQ4051 for constants and MQ4120. It fills `RRelationMapping` and its dependency keys. `FinishRelationMappings` skips these relations: its `OneEntity` reads (`:366, 382`) throw on an end with several types.
3. **In `BuildJoins`**, unchanged in place and still parallel: the path of every pair from `TypeLinks`, with the conditions of decision 48. A binding's constants are read from its file.
4. **Where bindings resolve** (`ResolveBindings`, inside `Publish`, `DatabaseRun.Bindings.cs:101`): the field forms, and the MQ4117 checks of a field against the mapping (decision 46), which read the pointers step 2 filled.

Moving `BuildJoins` after binding resolution was weighed and left out. `Publish` resolves bindings and then collects the run's keys in one pass, so the move would split it.

**A one-type designed junction** keeps its branch (`DatabaseRun.Relations.cs:213-230`). The branch skips an entry with no `foreignKey`, which MQ4009 reports. If question 9 is answered as recommended, it also applies the mapping's constants and attribute columns in step 1, with their checks in step 2. It takes today's path, byte for byte, when the mapping writes neither.

**Other engine readers** that assume a one-type opposite end, beside those above: `DatabaseRun.Bindings.cs:505-506` (decision 46) and `Loading/Materializer.Bindings.cs:434` (19.11). `Loading/Materializer.cs:725` reads projected tables only and stays.

**Dependency keys.**
- The new members key on the mapping file, the files of the named tables, each target's key table (`KeyDeps`, `DatabaseRun.Relations.cs:190-202`), the file that sets each code (the entity's file for a binding, the mapping file), and the end's types key `ec:<end id>`, as `FinishRelationMappings` adds a one-type relation's (`DatabaseRun.Finish.cs:359-386`). Navigations add the same for `type_joins`.
- **Covered entities that aren't placed.** `left_out`, `not_stored`, `links` and `type_codes` also change when a covered entity changes placement while no key moves. A stereotype carrier or a derived entity gets a mapping, or its mapping's `ignore` or `table` changes; it gains or loses a binding; or its package moves under a by-package database. None of these moves `cs:`, `ec:` or a target's key (`ResolveRun.Conceptual.cs:700, 718-727`). So while a mapping of the relation stores something in a database, `relation.mappings` and `type_joins` also key on the `e:` key and the referrers of every entity each end with several types covers. For example, `Memo` carries `controlledDocument` and isn't placed; when it gets a mapping, `relation.mappings` renders again.
- **The database reach follows** (`Migrations/DatabaseReach.cs`): a reached relation with such an end reaches every entity it covers. So `FactRecordingTests.Database_reach_covers_every_resolved_footprint` (`tests/Maquettiste.Engine.Tests/Integration/FactRecordingTests.cs:804`) holds.
- A model with no such mapping records no new key.

**Records** (`get_resolved_model`). `RelationMappingRecord` (`Editor/ResolvedRecords.cs:331`, built at `:1734-1735`) gains `links`, `constants`, `attributes`, `leftOut`, `notStored` and `typeCodes`, each left out when empty. Their members are the template members in camel case (`isHolder`, `referencedTable`, `referencedColumns`, `foreignKey`, `typeColumn`): ids for ends and entities, keys for tables and columns, names for foreign keys (as today's `foreignKey`). `BindingFieldRecord` (`Editor/BindingViews.cs:53-54`) gains `holds` and `keyOf`, left out when null; `EntityBindingRecord` (`:34`) gains `discriminatorValue`, left out when unset.

**The database view** (`get_database_view`, `Editor/DatabaseViews.cs:40`) gains `relationStorage`, left out when empty. It has one item per relation with an end of several types placed in the database: `relationId`, `relationName`, `mappingId`, `tables` (the holding tables), `leftOut`, `notStored` and `gaps`. Each gap has `endId`, `kind` and `text` (the text to copy, left out when there is none). `kind` is one of decision 47's values: `no-foreign-key`, `shared-table`, `id-only`, `type-check`, `count-check`, `one-to-one-unique`, `link-unique`, `per-type-duplicates` or `rows-not-told-apart`. These are API values, so they stay.

**Tools.** No new tool. `get_resolved_model`, `get_template_context` and `get_database_view` show the members; `get_binding_drift` names the new fields. The descriptions in `src/Maquettiste.Cli/Mcp/ModelTools.cs`, `Agent/tools.json`, the Functions endpoints, the conventions resource (`McpServerSetup.cs`), the modeling skill and the assistant's prompt (`Agent/AssistantPrompt.cs`) describe the per-end entries, `constants`, attribute columns, codes, the field forms and both ends.

### 19.9 Packs

| Pack | Change |
| --- | --- |
| `sql-ddl` | **No template change.** It writes designed tables as they are: columns, keys, checks (`packs/sql-ddl/_table.scriban:42-44`), uniques and indexes, with an index filter on every dialect but MySQL and Oracle (`_sql.scriban:585-586`), and `NULLS NOT DISTINCT` on PostgreSQL (`:283-285`). The engine adds nothing. Golden tests prove the DDL is the tables' own, each with the checks and uniques MQ4118 suggests written into the tables by the team: every way of 19.4 in the `postgresql` dialect, create and migrate; in the other four dialects, only the ways whose output differs (uniques, the filtered unique index, checks) (question 10). A migration test adds a type's column and drops another's |
| `csharp-dapper` | **No template change.** A bound holder's new fields carry `end`, so `bind_key_fields` declares them as entity properties (`packs/csharp-dapper/_dapper.scriban:317-329`) and the repository reads and writes them (`_binding.scriban:31, 61`). A holder mapped to a designed `table` gets its link columns as storage columns (`_dapper.scriban:101`). The README says the team's code sets the type column, or the one key column per type, and the database's checks guard them. A build test with warnings as errors |
| `process-docs` | No change. A golden test with a relation that has several types at both ends |
| `seed-data` | Seeds of such links stay refused (MQ7105) |
| `samples/typescript-pack` | No change: it writes a union from `nav.targets` (section 10). A golden test with both ends |

### 19.10 The editor: storage

**The relation's Storage tab** (`editors/storage/RelationStorage.tsx`). The part 1 notice (lines 71-76) gives way to one card per database.
- **Type storage**, as two choices: **Held in** and **Linked by** (first drafted as "Points by", which read awkwardly). Each pair writes the entries in the table below, and the tab reads a mapping back into a pair the same way. A mapping that matches no pair shows "As written", with every end's entry editable.
- **Choices and entries.** Below, A is a one-type end and B an end with several types. "Linked by" is asked for the end that doesn't hold the link; in a link table, for each end.

  | Held in | Offered when | Linked by | Entries written |
  | --- | --- | --- | --- |
  | The other end's table (A's) | B is to-one | A column per type; A type and an id; An id unique across types; A key to the shared base table | B: `types` with `foreignKey` items; `typeColumn` and `idColumn`; `idColumn`; `foreignKey`. A: no entry |
  | Each type's table (B's) | A is to-one, and B isn't any entity | A key, in each type's table (no choice) | A: `foreignKeys`, one per type table. B: no entry |
  | A link table | Always. The only choice when both ends have several types | Per end: A key (on A) or A key to the shared base table (on B); A column per type; A type and an id; An id unique across types | `junctionTable`, and per end: `foreignKey`; `types` with `foreignKey` items; `typeColumn` and `idColumn`; `idColumn` |
  | A table per type | A has one type, and B isn't any entity | A key, in each table (no choice) | A: `foreignKeys`, one per link table. B: `types` with `foreignKey` items |
  | Not in this database | Always | | `ignore` |

  - A many-to-many offers only A link table, A table per type and Not in this database (decision 40).
  - An any-entity end never offers A column per type (decision 43).
  - **Refers to**, per type, shows with A type and an id, and An id unique across types (below).
- **Pickers** for what the choices need: keys of the holding table that refer to the right table; the type and id columns of the holding table, the id column filtered by type; **Refers to**, per type, shown when a type table's primary key isn't one column or the person opens it (default: the primary key); the link table, or one per type; **Rows of this relation** (constants), with the binding editor's constants grid (`editors/storage/StorageTab.tsx:771`); a column per relation attribute when one table holds every link.
- **Lists:** "Left out (not in this database): Tool"; "Not stored: Memo, no key or column points at it".
- **What the database can't enforce**, from the database view: each gap with its text and **Copy**. The **Add to table 'comments'** button, which would write the check into the designed table file, waits (question 10; section 13).
- **Problems.** The tab shows errors and warnings, not info (`RelationStorage.tsx:98-106`). So it shows the mapping's MQ4009, MQ4117 and MQ4120, and MQ4011 as today. MQ4115 (info) shows as the card's line "Nothing is stored in this database." MQ4118 (info) shows as the list of gaps above, read from the database view.
- The pickers read the table documents as today (`useElements`), and placements and gaps from the database view. `relationStorage.ts` gains the pure helpers (the table above, both ways; key and column filters), unit tested without React. The mock uses the same table.

**The entity's field map** (`editors/storage/fieldMap.ts:148-177`): the skip at lines 157-158 goes, and the binding offers the new forms with origin "relation": "attachedTo type", "attachedTo id", "attachedTo Part key". Map by name matches them. **The entity's Storage tab** lists its relations through `endSingleEntity` only (`editors/storage/useStorage.ts:33`), so Part's way-1 relation wouldn't show; it reads coverage instead.

**Quick fixes** (`problems/quickFix.ts`): MQ4115, "Not in this database" and "Open the Storage tab"; MQ4120, "Open the Storage tab"; MQ4118, "Copy the check"; MQ4119, "Keep the stored code" (decision 52); MQ4009, remove the key, or for a top-level `foreignKey` on such a relation, "Move it to the end's entry".

**The mock server** (`src/editor/src/mocks/model/`): `endRules.ts` (MQ3041, MQ4009, MQ4115, MQ4116); `refs.ts` and `cascade.ts` (the entry rule of 19.6); `physical.ts` (the skip at line 678 becomes the storage of decisions 39 to 49: targets, holding tables, codes, MQ4117, MQ4118 and its texts, MQ4120); `bindings.ts` (the field forms and decision 46's table); and the database view's `relationStorage`. Like the engine, the mock reports no finding of the run while validation has errors.

**Parity, in two fixtures.** A whole-model validation drops every finding of the run as soon as any error exists (`ModelStore.cs:545-547`, `ModelStore.ResolverFindings.cs:51`, `Generation/Outcomes.cs:61`). MQ4009, MQ4011 and MQ4116 are validation errors, while MQ4117, MQ4118, MQ4120 and MQ4051 come from the run. One fixture holding both would never show the run's findings. So there are two:
- `src/editor/tests/fixtures/several-types-storage-files/`: one misfit per MQ4009 case, MQ4011, MQ4116's query cases, and MQ4115 with its new skip.
- `src/editor/tests/fixtures/several-types-storage-run/`, with no validation error: every way of 19.4, one misfit per MQ4117 case, every MQ4118 gap, MQ4051 and MQ4120.
- Each has its own `diagnostics.json` and `Hit` list, recorded by a new engine test, `SeveralTypesStorageParityTests` (beside `tests/Maquettiste.Engine.Tests/Validation/SeveralTypesParityTests.cs`). `mock-several-types-storage-parity.test.ts` checks the mock against both, field by field.
- Part 1's fixture is recorded again where part 2 changes its answers: the MQ3041 both-ends case, the MQ4116 binding cases now gone, and the MQ4009 text on its mapping. Its `Hit` list drops those cases and asserts they are gone.

### 19.11 Drift and the by-name operations

- **The mapping names ids,** so a renamed column or key is followed with no drift, constants included (decision 44). `referencesColumn` names a key when its table is one the engine makes, as a designed foreign key's referenced columns do. A removed column or key is a missing reference, as for today's `foreignKey`. A retyped id column fails MQ4117's type check.
- **Binding drift** (`Model/BindingDrift.cs`): `AttributeName` (`:259`) names the new field forms (`attachedToType`, `attachedToPartId`), so MQ4087 and the drift report speak of them. Re-sync keeps them, since it follows columns by id.
- **Map by name** (`Loading/Materializer.Bindings.cs:427-436`) offers the new forms as candidates for a holding entity, and `<endId>` for each type when the end with several types holds the key. `materialize-tables` is unchanged: a projection has no such columns, so a team adds them by hand (materialize of such storage waits with path A).

### 19.12 Several types or any entity at both ends

The owner, 2026-11-08: every combination, "including a relation that allows any to any". Below, "one entity", "several types" and "any entity" say what an end may point at; cardinality stays `min` and `max`.

- **The model and the rules.** MQ3041 drops its both-ends case (`RelationTypeRules.cs:96-98`) and keeps the n-ary case (`:68-69`) and any entity as a composition's part (`:101-105`). A composition's owner end and its part end may both have several types; MQ3016 counts each covered entity of the part end, as in part 1.
- **Coverage and navigations** are per end already: `EndCoverage` (`Model/EndCoverage.cs`) answers per end, and `BuildNavigations` (`Resolution/ResolveRun.Conceptual.cs:894-927`) makes one navigation per pair of ends, whose `From.Entity` and `Target` are null when that end has several types. A navigation between two such ends is held by every type of its from end, is listed in `entity.navigations` of each, and lists the far end's types in `targets`. An entity covered by both ends holds both navigations; MQ3009 reports a name clash, as today.
- **Expressions.** A path through such a navigation reads what every entity the far end covers holds under one id (decision 13), from any holder. `in_state` follows decision 15 per far end (MQ3116).
- **Rights, trigger points, freezing and actors** name ends by id, and an end is held by every covered entity of the other end (decision 16), so MQ8102 and MQ9903 read the holders as they do.
- **Scenarios and connect steps.** A link fits when the object is covered by the end it holds and the target by the other end (`Processes/ScenarioObjects.cs:183-196`, `Processes/VerbStepFields.cs:84-87`), which read coverage for such ends already. Tests pin both ends, a replay included.
- **Keys:** `ec:<end id>` and `et:ends` are per end already.
- **Records and the index:** `EndRecord`, `NavigationRecord` and `RelationEndSummary` are per end, so their shapes don't change, and the index format stays `maquettiste-index/e14` (`Editor/ModelReads.cs:23`). A file with several types at both ends is refused by 0.21.0 (MQ3041), so an older engine still refuses what it can't read; `formatVersion` stays 1.
- **Storage:** the per-end entries cover both ends: ways 9 and 10, and a link table with any way at each side. A link table per pair of types, and column pointers spread over several holding tables, wait (decision 39).
- **Seeds and queries:** as in part 1 (MQ7105, MQ4116).
- **The relation's form** (`inspector/relationEnds.ts`): the limit "Only one end may have several types." (line 12) goes; the n-ary and composition limits stay.
- **The canvas** extends the agreed drawing (section 11) to both ends:
  - **Expanded:** one junction node at the relation member's `x` and `y`. One line runs from the node to each type card or box of either end, and each line shows its end's role and cardinality. A stereotype or any-entity box sits on its end's side of the node and moves with it. Each end says "+N not shown" for its types not on the canvas.
  - **Collapsed** by the one toggle: one box at `x` and `y` that lists each end's entries under its role ("source: Comment, Note"; "target: Part, Assembly"). The toggle is offered when either end has two or more entries.
  - **The owner kept it** (2026-11-09, section 15): the collapsed box has no lines, which goes beyond the drawing the owner agreed for one end (section 11), because neither side has one card to draw a line to.
  - Nothing new in the diagram file: the member's `collapsed`, `x` and `y`, as in part 1. A relation with one end of several types keeps part 1's looks.
  - Which relations show in a domain view (`canvas/model.ts:52-57`): each end has an entry that is a card, or names a stereotype or any entity. `canvas/severalTypes.ts` gains the both-ends plan beside its near and far plan (lines 1-40). Exports and auto-layout include the node and boxes.
  - The browser pass shows the owner the both-ends look.

  ```
  Both ends with several types: one node

  +---------+ source *                    target 1 +----------+
  | Comment |-----------+          +---------------| Part     |
  +---------+           |   Link   |               +----------+
                        +---(o)----+
  +---------+ source *  |          |    target 1   +----------+
  | Note    |-----------+          +---------------| Assembly |
  +---------+                                      +----------+

  Collapsed by the toggle: one box

  +-----------------------------+
  | Link                        |
  | source *: Comment, Note     |
  | target 1: Part, Assembly    |
  +-----------------------------+
  ```
- **Editor readers that assume one end has one type,** each made to work through both ends:
  - `model/objectFields.ts:126-127`, which skips a relation when both ends have several types, so guard, actor and owner paths through it don't show;
  - `canvas/model.ts:329-332` (`pairOf`);
  - `canvas/severalTypes.ts:92`, which assumes the near end has one type, and `:412` (`stereotypeCounter`), which reads the first end with several types only;
  - `canvas/TypesEdge.tsx`, which has one `farEndIndex`, and `workspaces/entities/EntitiesWorkspace.tsx:435`.
- **If the both-ends editor work moves to 0.23.0** (question 10), the engine still allows both ends in 0.22.0, and the editor still works with such a relation:
  - every reader above skips it without breaking, as `objectFields.ts` does today;
  - the form opens it, shows each end's entries and saves without loss. The one-end limit stays for new edits, so such a relation is made in the file, by a tool or by the assistant;
  - its findings show as usual, and the Storage tab offers "A link table" for it;
  - the canvas draws a plain fallback: one line between the first card of each end on the canvas, with each end's label saying "+N more" and a tooltip listing the entries. There is no node and no toggle, and nothing is saved in the diagram file.
- **The mock and parity:** `endRules.ts` drops the case; part 1's fixture keeps `both.json` as a valid relation, and the storage run fixture adds a both-ends relation with ways 9 and 10.

### 19.13 What waits, with the reason

Added to section 13: a link table per pair of types; column pointers spread over several holding tables; an attribute column form per holding table; a relation binding with statements for links; a value-map fact for type codes; an engine-made check, unique or column (path A) and materialize; attribute values stored as rows, value tables split by data type, partitioned table sets, ends made of a partition and an id, ends that point at a shared id table, and other schemas a table can't model on its own, which packs implement (principle 2). None of them needs a change to the keys above.

Waiting too, by answer 10: the "Add to table" button. If the checkpoint says so: the both-ends editor work.

### 19.14 Questions for the owner

**Answered on 2026-11-09, each as recommended (section 15).** The questions stay here as the record.

7. **MQ4115's severity.** Your answer 4 made it a warning in part 2 until a mapping states the storage or sets `ignore`. Principle 1 says entities and tables may stay separate models, and a warning on every unmapped relation would push teams to map what they keep apart on purpose.
   - **Recommendation:** keep MQ4115 as info, so a model that uses part 1 gets nothing new on the upgrade. It also skips a database where a mapping stores the relation. The case "a mapping states storage but leaves a placed type out" becomes its own warning, MQ4120, since one rule can't have two severities (decision 49).
   - *Alternative:* MQ4115 becomes a warning, as answer 4 said. Every model with such a relation placed and not mapped then gets it on the upgrade. A narrower form warns only when every end's covered types are bound to designed or imported tables in that database.
8. **`typeStorage` and its four values** (confirmed, never released). The per-end entries say everything it would say, and more: way 1 has no value, way 4 would need a fifth (`unique-id`), and `table-per-type` names where links live, not how an end is pointed at. Writing both could disagree.
   - **Recommendation:** leave `typeStorage` out of the format. Keep its four ways as the editor's choice labels and the guide's words (A column per type, A type and an id, A key to the shared base table, A table per type).
   - *Alternative:* keep it, as an optional summary checked against the entries (MQ4009 when they disagree), with the fifth value `unique-id` and no value for way 1.
9. **`constants` and `attributes[].column`: on every relation mapping, or only on relations with an end of several types?** Both are permanent format features.
   - **Recommendation:** every relation mapping with a designed `junctionTable`. Your own examples of the usual correlations, one shared link table for many relations and a detail table with attribute columns, fit one-type relations just as well. A rule that works for some relations and not others would be odd for teams.
   - **The extra cost:** about 400 to 500 lines with tests, about 10M, which 19.17 already counts. For one-type relations it touches these readers:
     - the designed junction's branch (`DatabaseRun.Relations.cs:213-230`), which applies the constants and attribute columns;
     - relation seeds into a designed junction (`ResolveRun.TableSeeds.cs:650-665`), which must write the constants into each row and now fill the attribute columns;
     - join steps through a shared link table, which gain the constants as conditions;
     - `sql-ddl`'s `ref_collections` (`_reference.scriban:568-592`), which reads a junction-mapped relation's `template_defined`. It must stay the same, and does, since a collection attribute can't take `column` (decision 45); a golden pins it;
     - the Storage tab's junction picker, which gains the constants grid and the attribute column pickers.
   - **What stays the same:** a one-type mapping that writes neither key keeps its bytes and behavior. The branch takes today's path, and the pinned test of 19.16 holds it over the showcase model's designed junction.
   - *Alternative:* only on relations with an end of several types. On a one-type relation each is MQ4009 ("…needs a relation with an end of several types"), and section 13 lists "on one-type relations". It can be widened later, never narrowed. It saves about 10M.
10. **The release plan.**
    - **Recommendation: one release, 0.22.0, with two cuts and a checkpoint.**
      - Cut 1: trim the `sql-ddl` golden matrix. Every way in the `postgresql` dialect; in the other four, only the ways whose output differs (uniques, the filtered unique index, checks). Saves about 10M to 15M.
      - Cut 3: the "Add to table" button and its end-to-end test wait (section 13). Copy and the texts stay. Saves about 10M.
      - The both-ends editor work (the form's limit, the canvas, the path pickers and the reader sweep of 19.12) is a separate agent that starts last.
      - **The checkpoint**, after wave 2: if waves 1 and 2 together have spent more than 240M cached re-reads (expected: about 210M), the both-ends editor agent doesn't start. That work moves to 0.23.0, and you are told. At that overrun, the rest of the plan with the agent would land near 385M; without it, near 330M.
      - **If it moves,** 0.22.0's editor still opens, shows and validates a relation with several types at both ends without breaking, with a plain fallback drawing (19.12).
      - The likely figure is about 335M (300M to 385M) with the editor work kept, and about 295M if it moves (19.17).
    - *Alternative: two releases.* 0.22.0 stores links, with both ends still refused (MQ3041): about 8,300 to 9,200 lines and about 285M (255M to 325M), with the same cuts. 0.23.0 adds both ends: about 2,000 to 2,300 lines and about 135M (120M to 155M). That is about 420M in all, since each release pays again for a contract, a review, a browser pass, fixes and CI.

### 19.15 A project that uses none of this

- **A project with no end of several types and no new key** keeps its files, output bytes, findings and messages, keys, records, index, delete plans and replays. New members are empty or left out; `type_joins` and `conditions` are empty; the database view leaves `relationStorage` out. **Pinned by** the pack goldens and replays, which pass untouched, and the pinned test of 19.16 over the billing and showcase models.
- **Answers that change for every project,** as in earlier items: `list_validation_rules` (seven catalog texts, four new rules); `get_schema` for mappings and entities; `get_template_context`, whose member lists come from reflection (`GenerationService.Authoring.cs:461-463`); and the `tools/list` descriptions and the conventions resource, which 19.8 rewrites.
- **A one-type relation** keeps today's behavior, its MQ4011 and foreign key checks, and its delete plans, unless its mapping writes `constants` or `attributes[].column` (question 9). One difference: an invalid file with an `ends` entry and no `foreignKey` reports MQ4009 instead of MQ1002 (decision 38).
- **A model that uses part 1** keeps everything, with these differences:
  - MQ4115 per question 7 (nothing new if answered as recommended);
  - a binding field that names such an end no longer gets MQ4116: it resolves, or gets MQ4045 when the entity can't hold the end in that form (decision 46);
  - MQ4009's text on a mapping of such a relation changes;
  - a model that had both ends with several types loses its MQ3041 error.
- **Speed:** the new checks run only for mappings of such relations. **Pinned by** the bench and its baseline.

### 19.16 Tests

- **`RelationTypeStorageTests` (new):**
  - each way of 19.4 in each cardinality its holding table allows, over TPH, TPT, TPC (a one-type end over an abstract TPC root included) and bound placements;
  - a type table whose primary key is its own surrogate id, with the entity's key a natural code, as the referenced column; `referencesColumn` to a unique column, to one that isn't unique, and to a column of a table the engine makes; a primary key of several columns, and none;
  - MQ4009, MQ4011 and MQ4117, each case of 19.7, and the "which rule fires" pairs;
  - MQ4118, each gap kind with its text in five dialects, a primary key counted as a unique, and the unique with the constants' columns for way 8;
  - codes from a binding, a mapping and a name; integer codes in text and integer columns; collisions;
  - MQ4115 with its new skip; MQ4120;
  - MQ4119 against a baseline, its quick fix, and its repeat until the baseline moves;
  - `constants` on a shared link table holding two relations, with MQ4117 only when part 2 keys are used; `attributes[].column` on a link table, on an end's table, and refused over several tables;
  - each row of decision 46's field table.
- **One-type relations stay safe:** an entry with no `foreignKey` gives MQ4009 with the missing-key words, and the engine, the mock and the editor don't throw on it. A delete dry run on a designed junction's foreign key gives today's plan under both choices: `remove-references` refuses with today's words, and `delete-dependents` removes the entry.
- **Unchanged, pinned:** a hash taken before part 2's resolver changes, over the billing and showcase models. The showcase has a designed junction (`tests/fixtures/models/showcase/.maquettiste/model/mappings/uses-in-main.json`). The hash covers relation mapping records, entity mapping and binding records, the database view's JSON, the template-visible join paths of navigations and entity mappings, and the dependency keys of relations, navigations and bindings. It sits beside part 1's `RelationTypesUnchangedTests` (`tests/Maquettiste.Engine.Tests/Resolution/RelationTypesUnchangedTests.cs`).
- **Joins:** the paths of every pair for each way; conditions from type codes, constants, a TPH discriminator (a TPH-derived target in its root's table included) and a binding's constants read from its file; `joins` set only with one path, and `type_joins` for way 5; entity mapping joins; `BuildJoins` from targets.
- **Records and views:** `RelationMappingRecord`'s members, left out when unused; `BindingFieldRecord`; the database view's `relationStorage`, with each gap kind.
- **Stale output:** a carrier added renders `relation.mappings` again; so does a changed `discriminatorValue`, and a named table's change. A covered carrier that isn't placed has its `ignore` toggled, or gains a binding, and `relation.mappings` renders again. The database reach covers every covered entity. A model with no such mapping keeps its keys. Each stale test is checked to fail without its fix.
- **Stale findings:** the relaxed MQ4009 on `discriminatorValue` appears and goes on the mapping file and on the entity file when a relation's coverage changes.
- **Both ends:** MQ3041's both-ends case gone and its other cases kept; navigations both ways; `entity.navigations` with an entity covered by both ends; a guard and `in_state` through both; rights and trigger points held by every covered entity; scenario links, connect steps and a replay; ways 9 and 10.
- **Packs:** `sql-ddl` goldens, every way in `postgresql`, create and migrate, and in the other four dialects the ways whose output differs; `csharp-dapper` build with warnings as errors, with a bound holder mapping the new fields; `process-docs` and the TypeScript sample with both ends.
- **Editor:** unit tests for the choices table both ways, the pickers' filters, the field forms, and the both-ends drawing, toggle, fallback and export; the two parity tests; one end-to-end test that describes a type and an id, sees the gap with its text to copy, and reloads.
- **No change:** the existing goldens and replays pass untouched; a part-1-only model keeps its findings except as 19.15 says.

### 19.17 Tasks, build plan and estimate

**Tasks,** in days for a person who knows the code, with tests and a review.

| Wave | Group | What | Size |
| --- | --- | --- | --- |
| 1 | Contract | `mapping.json`, `entity.json` and `relation.json` texts, defaults and `x-order`; `openapi.yaml`; rule texts with MQ4120; `RelationEndMapping`, `MappingConstant`, `EntityBinding.DiscriminatorValue`; MQ3041 change; every reader compiling with today's behavior, the null-safe readers of decision 38 included | Three quarters of a day |
| 1 | Editor, part one | The Storage tab's choices table and pickers; the field map's forms; the both-ends fallback of 19.12; the mock's file rules (MQ3041, MQ4009, MQ4115, MQ4116) and its entry delete rule | A day and a half |
| 2 | Storage engine | `MappingRules` (MQ4009, MQ4011, constants, attribute columns, codes' keys) and its new dependents; the cascade's entry rule; the four steps of the storage run: targets, holding tables, pointers, codes, MQ4117, MQ4118 and texts, MQ4120; binding field forms | Two and a quarter days |
| 2 | Joins, readers and goldens | Conditions, `BuildJoins` and `type_joins`; records, the database view, drift and map by name; dependency keys and the database reach; MQ4119 and its quick fix; tool texts; the pinned "unchanged" test; both-ends engine tests for rules, expressions, processes and scenarios; the trimmed pack goldens | Two days |
| 2 | Editor, part two | The Storage tab's lists, gaps and quick fixes; the mock's storage run and view; the two parity fixtures | A day and three quarters |
| 3 | Both-ends editor, after the checkpoint | The form without the one-end limit; the both-ends node, box, toggle and export in place of the fallback; path pickers through both ends; the reader sweep of 19.12 | A day |
| 3 | Docs | User guide (relations at both ends, storage, a read-only binding's entity left out), the releases, the modeling skill, the conventions resource, `next.md` | Half a day |

That comes to about nine and three quarter days for a person.

**What part 1's estimate missed,** from `git diff --numstat 4bf2bc8c~1 597ecb30`, the design excluded:

| Area | Estimated | Built |
| --- | --- | --- |
| Engine code | About 2,400 | 1,626 |
| Engine tests | About 2,000 | 2,014 |
| Editor and mock | About 2,600 | 2,777 |
| Editor test code, fixtures excluded | About 450 | 2,066 |

The editor's tests ran more than four times over. The first draft of this estimate repeated that, and gave the storage run and the mock too little.

**Size,** with tests, without pack expected files, counted as part 1 was counted: about **10,500 to 11,500 lines.**
- **Storage, about 8,500 to 9,400:** contract about 400; file rules, dependents and the cascade's entry rule about 550 and tests 600; the storage run about 1,300 and tests 1,300; joins about 300 and tests 300; readers, records, views, keys, drift, MQ4119 and tool texts about 500 and tests 450, the pinned test included; editor about 800; mock about 1,000; editor tests about 900, fixtures excluded; pack tests about 250; docs about 300.
- **Both ends, about 2,000 to 2,300:** engine rule and resolver changes about 150 and tests about 650; storage cases about 250; editor form about 60, canvas about 500, the reader sweep about 150 and tests about 400; docs about 150. The editor's share, about 1,100, is the work question 10 can move.

**Agents.** As in part 1: each is capped at about 100 steps or 200k context; Opus builds the engine and the pack output and runs the review; Sonnet builds the editor, the mock and the docs and runs the browser passes; at most three agents run at once, in their own worktrees fast-forwarded to the branch, merged by patch. The both-ends editor agent is the last builder, so the checkpoint comes before any of its spend.

| Wave | Agent | Model | Steps | Starts from |
| --- | --- | --- | --- | --- |
| 1 | Contract first: schemas, `openapi.yaml`, rule texts, model records, MQ3041, every reader compiling | Opus | About 90 | `main` with part 1 merged, once questions 7 to 10 are answered |
| 1 | Editor and mock, part one, with the both-ends fallback | Sonnet | About 100 | The contract commit |
| 2 | Storage engine: file rules, dependents, the cascade's entry rule, the storage run, codes, MQ4115, MQ4117, MQ4118, MQ4120, binding forms | Opus | About 100, then a fresh agent for the rest, which is likely | The contract agent's finish |
| 2 | Joins, records, views, keys, drift, MQ4119, tool texts, the pinned test, both-ends engine tests, pack goldens. **Owns the tool files** | Opus | About 100 | The contract agent's finish, beside the storage agent |
| 2 | Editor, part two: Storage lists and gaps, quick fixes, the mock's storage run and view, the two parity fixtures | Sonnet | About 100 | Wave 1's editor agent, in the first free slot |
| Checkpoint | Waves 1 and 2's cached re-reads against 240M (question 10) | | | Every wave 2 agent's finish |
| 3 | Both-ends editor: the form, the canvas, the path pickers, the reader sweep | Sonnet | About 80 | The checkpoint, when it passes |
| 3 | Docs | Sonnet | About 40 | The checkpoint, beside the both-ends editor |
| 4 | Review of the pull request | Opus | About 60 | Every builder's finish |
| 4 | Browser pass, which shows the owner the both-ends drawing (or the fallback) and the Storage tab | Sonnet | About 50 | Beside the review |
| 5 | Fixers, split by area | Opus or Sonnet | About 40 each | The findings |
| 5 | A short second browser pass | Sonnet | About 25 | The editor fixer's finish |

**What earlier items cost.** Part 1, about 8,500 lines, came to 1.09M output, 3.32M new input and 296M cached re-reads in about 3 hours, against an estimate of 255M. Sign-off part 2, about 4,500 lines, came to 194M. A straight line through the two gives about 80M before size counts and about 25M per thousand lines.

**Before the cuts.** 10,500 to 11,500 lines give about 345M to 370M by that line. The storage engine will likely need a second agent at 100 steps, which adds about 30M. So expect **about 360M (320M to 410M)**, with about 1.45M output, about 4.8M new input and about 4.5 hours. That is roughly a one-in-three chance of passing the 400M cap. The first draft said 325M (290M to 370M), and its fallback could save nothing: it moved the both-ends canvas after wave 2, the wave that built it.

**What each cut saves,** cheapest first:

| Cut | What | Cached re-reads saved |
| --- | --- | --- |
| 1 | Trim the `sql-ddl` golden matrix: every way in `postgresql`, the ways whose output differs in the other four dialects | About 10M to 15M |
| 2 | Move the both-ends editor work (about 1,100 lines) to 0.23.0, keeping the engine's both-ends lift in 0.22.0 | About 35M to 45M |
| 3 | Defer the "Add to table" button and its end-to-end test; keep Copy and the texts | About 10M |
| 4 | If still needed: defer `REntityMapping.type_joins` and MQ4119 | About 10M to 15M |

**The recommended plan** (question 10): cuts 1 and 3, with cut 2 decided at the checkpoint.

| | Wall time | Output | New input | Cached re-reads |
| --- | --- | --- | --- | --- |
| Wave 1 (contract, editor part one) | About 1.0 hour | 0.37M | 1.25M | 70M |
| Wave 2 (storage engine and its likely second agent, joins and readers, editor part two) | About 1.6 hours | 0.55M | 1.8M | 140M |
| Checkpoint | | | | About 210M so far; the threshold is 240M |
| Wave 3 (both-ends editor, docs) | About 0.5 hours | 0.18M | 0.6M | 50M |
| Wave 4 (review, browser pass) | About 0.6 hours | 0.16M | 0.5M | 40M |
| Wave 5 (fixes, second browser pass, CI) | About 0.6 hours | 0.14M | 0.45M | 37M |
| **In all, the editor work kept** | **About 4.3 hours** | **About 1.4M** | **About 4.6M** | **About 335M (300M to 385M)** |
| In all, the editor work moved at the checkpoint | About 3.9 hours | About 1.25M | About 4.1M | About 295M (265M to 340M) |

- **The threshold.** 240M is about 15% over the expected 210M. At that overrun, the rest with the both-ends editor (about 127M) becomes about 145M, and the release lands near 385M. Without it, the rest (about 80M) becomes about 90M, and the release lands near 330M.
- **Lines:** about 10,300 to 11,300 with the editor work kept, and about 9,200 to 10,200 if it moves. Cut 3 removes about 200 lines; cut 1 removes only expected files, which aren't counted.
- **Against the check's figure.** The check paired cuts 1 and 2 for about 310M to 320M. With cuts 1 and 3 and the editor work kept, the figure is about 335M; if the checkpoint also moves the editor work, about 295M.

**The alternative, two releases:**

| Release | Lines | Wall time | Output | New input | Cached re-reads |
| --- | --- | --- | --- | --- | --- |
| 0.22.0: storage, both ends still refused, cuts 1 and 3 | About 8,300 to 9,200 | About 3.6 hours | About 1.2M | About 3.9M | About 285M (255M to 325M) |
| 0.23.0: both ends | About 2,000 to 2,300 | About 1.7 hours | About 0.42M | About 1.4M | About 135M (120M to 155M) |
| Both | | About 5.3 hours | About 1.6M | About 5.3M | About 420M |

- **One pull request into `main`,** with the contract, engine, editor, output, docs and fix commits. The tag waits for the owner.
- **Generated files** (the editor's API types, the recorded rule list, the sample app's copies) are written only by the fixed commands, after each merge into the branch. Pack expected files stay with the builder.
- **Fixers** are fresh agents with the exact findings. **A design gap found while building** stops the work, and the owner is asked.

### 19.18 What the independent check changed

- **One-type mappings stay safe.** Loosening `ends[].required` would have let a one-type entry with no `foreignKey` load, throw in two readers and change delete plans. MQ4009 now reports it, every reader handles it, and the cascade keeps today's plans (decision 38, 19.6).
- **The storage run's order.** The first draft did everything in `ResolveRelation`, before keys, uniques and bindings exist. It now runs in four steps, each where its inputs exist. `BuildJoins` stays in place and reads a binding's constants from its file (19.8).
- **A scope choice for the owner.** `constants` and `attributes[].column` on one-type relations had been settled without the owner. They are now question 9.
- **Rule cases.** MQ4009 and MQ4117 gained the missing cases, such as two entries for one end, two keys in one table and a primary key of several columns. Which rule fires is written down (19.7).
- **MQ4115 split.** One rule can't have two severities, so its second text is a new warning, MQ4120. MQ4115 gains a skip for a database where a mapping stores the relation (decision 49).
- **Binding fields.** A table gives each field shape its finding, and MQ4116 leaves bindings (decision 46).
- **Parity.** One fixture couldn't show the run's findings, which are dropped while validation has errors, so there are two (19.10).
- **Stale output and findings.** Keys follow covered entities that aren't placed, the database reach follows, and the relaxed MQ4009 gets dependents (19.8, 19.7).
- **Format and names.** Constants take column ids; `referencesColumn` takes keys of tables the engine makes; link ends use `referenced_table`, `referenced_columns` and `is_holder`; the gap kinds are listed; way 7's shape is `junction`; the new lists have empty defaults and a place in the order (19.6, 19.8).
- **Targets and joins.** One target rule for every end, a condition whenever a table holds other entities' rows, a gap when nothing tells them apart, and a stated order (decisions 39, 47, 48).
- **A key per type in a link table** now comes with a gap that says duplicates can't be refused, not a unique that wouldn't work (decision 47).
- **The editor.** A table from choices to entries, the reader sweep for both ends, and which findings show where (19.10, 19.12).
- **The "unchanged" promise** is pinned by a hash over the billing and showcase models, and the list of answers that change for every project is complete (19.15, 19.16).
- **MQ4119** says where the codes come from and how it clears, and offers "Keep the stored code" (decision 52).
- **The estimate** ran low, mostly on editor tests, and its fallback came too late to save anything. It is rebuilt with cuts and a real checkpoint (19.17, question 10).
- **Smaller fixes:** UUID, not GUID; "Linked by", not "Points by"; `joins[db]` only with one path; where a bound holder's join paths live; MQ4011's own words; flags on designed objects; MQ4116's query wording; a read-only binding's entity is left out; the owner sees the both-ends collapsed look and the any-entity shared-table reading.

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
