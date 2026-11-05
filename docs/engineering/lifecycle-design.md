# Lifecycles any implementation can follow: design

**Status (2026-11-05): a draft for the owner. Nothing in this file is built.** It designs item 4.1 of `next.md`. [What lifecycle systems can do](lifecycle-capabilities.md) is the yardstick, and [Who, what and when](lifecycle-actors-scenarios.md) sets the direction this item starts. Section 13 lists what the owner decides. A file path without a project prefix is under `src/Maquettiste.Engine/`. Examples use the billing fixture. Each new name and format is held to the roadmap's test: it stays for good, it must help any team whether they use it or not, and it stays optional.

**How this version came about.** The first draft assumed a single status column. The second assumed the opposite, but two independent checks of it, one against the code and one on what it is worth to the four uses, found that it would have changed things for the team already using lifecycles, and that several parts couldn't work as written. This version answers both checks, and the decisions say where.

## 1. In short

A lifecycle in Maquettiste is a process with `use: lifecycle`. It is a model, and Maquettiste runs nothing. A team may generate code from it with packs, build from it by hand, or implement it in a lifecycle system. Either way, the model must not decide how the state is stored, or which and how many types follow it.

Today it decides too much. A lifecycle has one entity as its subject, the entity names one lifecycle, and the state can only be held in one enum attribute matched by name.

| Layer | What changes |
| --- | --- |
| The model | A state may carry a `code`, its stable stored value, however the state is stored. A lifecycle may govern several entities with a new `subjects` list, while the one-subject form stays valid as it is. Each subject may bind an attribute that holds the state: an enum, a reference type, or a plain string or integer. Guard conditions read the object. A scenario names its objects |
| Code generation | Templates read codes and subjects. `csharp-dapper` writes the codes, maps states to codes and back, and lists every move from code to code. `sql-ddl`'s lifecycle tables can hold the current code, and it can write a check on a status column |
| The team's system, or a lifecycle system | Nothing changes until the team uses the new output or builds from the model |

**A project that uses none of this sees no change at all:** the same findings, the same output, the same files. Section 11 shows how.

## 2. What the owner said

- **Maquettiste is a modeling tool first.** Some teams only model, so a rule that limits what can be said is a barrier. A hand-written stub, such as an empty partial method, is a fine result.
- **A lifecycle's state usually lives in its own tables, not a single status column.** So a state's code can't depend on a bound attribute.
- **One lifecycle can govern many types, and one object can follow many lifecycles**, such as an employee's HR lifecycle and project lifecycle. Several instances of one lifecycle per object, such as one per project, are best modeled on a detail entity like a project assignment.
- **Triggers exist for every operation**, as check, override and action, and a pack may turn them into partial methods or one pipeline. They are the subject of their own item (section 12).
- **Relations can allow several types at each end.** That is its own item too.

## 3. Scope

| | What | Section |
| --- | --- | --- |
| **4.1, this design** | State codes; `subjects`; a state attribute per subject; guard conditions that read the object; a scenario's objects; output for codes and moves | 4 to 11 |
| Next, a design each | The order in [Who, what and when](lifecycle-actors-scenarios.md) section 10: actors, verbs, setting the object's data and reading another lifecycle's state, rights, trigger points, richer sign-off. Relations with several types, revisions, and lifecycles on custom kinds and tables come alongside | 12 |

## 4. Decisions

**State codes**

1. **Any state may carry a `code`:** a string or an integer, its stable stored value. It is optional, it needs no binding, and it can sit on a nested state. History and choice states can't have one, since nothing rests in them. Codes are unique within a lifecycle, and they are all strings or all integers. They are compared by `ReferenceDataIndex.CodeKey`, so `1` and `"1"` are the same code.
2. **A literal, not a row id.** It reads like every other place the model holds a code, such as a default or `allowedValues`, and it works where there are no rows.
3. **One code per state, for every subject.** A type that needs another encoding gets its own lifecycle, or a pack maps the values.
4. **Codes that cover the lifecycle.** A lifecycle's codes "cover" it when every atomic state it can reach has exactly one coded state among itself and its ancestors, and no coded state sits inside a parallel region. Then any configuration has exactly one code. The resolved model says whether they cover it (`process.codes_cover`), because mapping states to codes is only defined when they do (section 9). A lifecycle with a few codes, or codes in parallel regions, is still valid. It just gets constants and no mapping.

**Subjects**

5. **A lifecycle may list what it governs.** `subjects` is a list of entries, each `{ "entity": <id>, "boundAttribute": <id>? }`. Listing an entity in several lifecycles means its objects follow all of them at once, like the employee in HR and in a project. A type whose objects follow *one* of several lifecycles is a later item (section 12). It will add a condition to the entry, so the meaning of an entry without one never changes.
6. **The one-subject form stays valid, silent and unchanged.** `subject` and `boundAttribute` on a process, and `lifecycle` on an entity, keep their meaning and rules (MQ9201 included). Nothing deprecates them, nothing warns, and nothing rewrites them.
   - `subjects` is used only for two subjects or more, or when a team chooses it. The schema refuses both forms in one file.
   - Internally, one list serves every reader: `Process.Subjects` is `subjects` if present, else the one-subject form.
   - **Why:** the second draft rewrote the old form. The check showed that would break `format --check` in a team's CI, stop teammates on an older engine from loading files the new engine had saved, and get old snapshots refused on import. Two forms are the price of changing nothing for teams that don't need more.
7. **`entity.lifecycle` keeps its meaning.** It names the entity's lifecycle in the one-subject form, and MQ9201 checks it as today. With `subjects`, an entity may still name one of the lifecycles that list it, and naming one that doesn't list it is MQ9201, as today.
8. **`set-lifecycle` and the editor's Use and Subject controls work on the one-subject form, exactly as today.** On a lifecycle using `subjects`, `set-lifecycle` is refused (MQ9019) with a message to edit `subjects`, and the editor shows the subjects read-only, editable in the JSON view.

**Holding the state in an attribute**

9. **A subject may bind the attribute that holds its state.** It is a single-valued attribute of that subject, own or inherited, typed by:
   - an enum, as today;
   - a reference type;
   - a plain status: `string`, `text`, `int16`, `int32` or `int64`, directly or through a scalar type.

   Each subject binds its own, since different types keep their status in different columns. One attribute is bound by at most one lifecycle (MQ9202). That covers a base and a derived entity, since an inherited attribute is the same attribute.
10. **An enum binding works as today**, matching root states by name (MQ9203). `sync-enum` gains an optional `entity` for which subject's binding to sync, defaulting to the one-subject form's. Subjects of one lifecycle may share an enum, which is the natural case, without MQ9204 warning about each other.
11. **A code binding needs codes that cover the lifecycle** (decision 4), so the column can hold the state as one value. Codes in parallel regions or on two levels of one path are MQ9206.
12. **A missing code under a binding is a warning, not an error (MQ9210).** It means the column has no value for that state. As an error, it would refuse ordinary editing: adding a state, then a transition into it, then typing its code takes three saves, and the second would be refused. A pack's mapping throws for that state until it has a code.
13. **The codes must fit the attribute (MQ9207).** A reference type's codes must be live codes of the type, and a former code is reported with its replacement. A plain status's codes must fit its type, length, pattern and `allowedValues`. Two bindings to different reference types must both accept every code, so renaming a code in one type is refused while the other doesn't have it. That follows from decision 3.
14. **Values with no state are a warning (MQ9208)**, once per binding: the attribute's `allowedValues` when it has them, else the type's rows in the model. Rows a script fills are not judged.
15. **A renamed code follows.** A code under a reference binding is listed as a use of the row, field `code`. So `get_references` and `referenced_by` see it, deleting the row is refused, and renaming, merging or retiring the code rewrites it. A merge or retire that would leave two states one code is refused.
16. **The index stays right when the attribute lives in another file.** The index reads the bound attribute's type, which can sit in another document. So its patch path falls back to a full build when a changed document holds an attribute that some process binds, judged both before and after the change, or when a changed process binds one. A model with no binding outside the enum form never takes this path.
17. **The validator's reach.** A save of a reference type, seed or scalar type revisits the lifecycles that bind one, and only those.
18. **`sync-codes` proposes codes and binds them.** It is a batch operation:
    ```json
    { "op": "sync-codes", "id": <process>, "entity": <subject>, "attribute": <attribute>, "stateCodes": { <state id>: <code> } }
    ```
    - It keys states by id, since names are unique only among siblings. Values are strings or integers, and `stateCodes` is its own field, since the batch's existing `codes` field takes strings only.
    - A state keeps a code it has. Otherwise it matches a row or allowed value whose code equals the state's name (ignoring case, `_` and `-`), then one whose label equals the state's display name or name.
    - An ambiguous match is unmatched. `stateCodes` gives the rest.
    - It adds the subject when missing, and sets the binding. It creates no rows.
    - A dry run lists what it would write and what is unmatched.
    - It is reached through `apply_batch` and `maquettiste process sync-codes`.

**Guards that read the object**

19. **A guard condition may read the object.** Conditions name attributes by id, not by name, so in scope is what every subject holds under one id:
    - the subjects' own attributes when there is one subject;
    - attributes from a shared base entity;
    - a stereotype's attributes, when every subject carries it (the stereotype's attribute id, read on each entity that carries it).

    To-one navigations follow the same rule, as an operation's guards already do (`subject.customer.customerSince`). A bound attribute is left out, since the chart already knows its state.
    - A context attribute named `subject` keeps its meaning, and then the subject is not in scope. MQ9211 warns about the clash, since a later item adds `actor` the same way.
    - Only conditions read the object. Expressions in JavaScript don't: packs write those as they are, and today an expression reading `subject.x` saves and fails when run. Library guards hold expressions only, so they don't read the object for now.
20. **Generated code leaves such a guard to a person**, as it does any guard it can't translate. `csharp-dapper` writes it as a partial method, with the condition in the generated half's comment. The generated runtime has no object to read, and giving it one belongs to the generated runtime (4.6). Generated scenario tests behave as they do today for any hand-written guard, so no existing output changes.
21. **A scenario names its objects.** A scenario gains `objects`, its cast, and `subject`, the object its process runs for:
    ```json
    "objects": [
      { "id": <id>, "name": "invoice", "entity": <entity id>, "values": { <attribute id>: <value> }, "links": { <relation end id>: <object id> } }
    ],
    "subject": <object id>
    ```
    - Objects have ids, like every other part, so `subject` points at one by id and never by list position. `subject` is required once there are objects.
    - `links` connects one object to another through a relation's end, so a project assignment can point at its employee and its project.
    - A value not given reads as the attribute's default, then null, as context does. A bad value is a replay finding, so it doesn't refuse a save.
    - `process` stays required for now. It becomes optional when steps name the object they act on ([Who, what and when](lifecycle-actors-scenarios.md) section 8), and `subject` then sets the default.
    - `simulate_process` and `record_scenario` take the same `objects` and `subject` as the scenario, not a second shape inside `start`.
    - `sync-enum`'s check of literal uses counts object values. Renaming a code doesn't rewrite them, the same as scenario values today.

## 5. Schema and files

| File | Key | Type | Notes |
| --- | --- | --- | --- |
| `process.json` | `subjects` (after `use`) | list of `{ entity, boundAttribute? }` | Decisions 5 and 9. Refused together with `subject` or `boundAttribute` |
| `process.json` | state `code` (after `type`) | string or integer | Decision 1 |
| `scenario.json` | `objects`, `subject` (after `process`) | as in decision 21 | Decision 21 |
| `batch.json` | `sync-codes`, with `entity`, `attribute`, `stateCodes`; `sync-enum` gains `entity` | | Decisions 10 and 18 |

The copies follow: the sample app's schemas, `openapi.yaml` and its generated editor files.

```json
{
  "kind": "process",
  "name": "ReleaseLifecycle",
  "use": "lifecycle",
  "subjects": [
    { "entity": "<id of Invoice>", "boundAttribute": "<id of Invoice.status, typed by the reference type ReleaseStatus>" },
    { "entity": "<id of CreditNote>" }
  ],
  "states": [
    { "id": "…", "name": "Draft", "code": "DRF" },
    { "id": "…", "name": "Review", "code": "REV", "states": [ { "id": "…", "name": "Finance" }, { "id": "…", "name": "Legal" } ] },
    { "id": "…", "name": "Released", "code": "REL", "type": "final" }
  ]
}
```

Here an invoice keeps its state in a column, and a credit note keeps it wherever its implementation chooses.

## 6. Rules

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ9201 | E | Unchanged for the one-subject form. With `subjects`: an empty list, a subject that isn't an entity, or one listed twice | Add a subject |
| MQ9202 | E | Changed: a subject's bound attribute isn't a single-valued enum, reference type or plain status attribute of that subject; a binding on an orchestration; or one attribute bound by two lifecycles | Bind another attribute |
| MQ9203 | E | Unchanged, per enum binding | `sync-enum` |
| MQ9204 | W | Unchanged, but quiet about other subjects of the same lifecycle | |
| MQ9205 | W | Changed: per binding, the default is not the initial state's name (enum) or code | Set the default |
| MQ9206 | E | Two states share a code; codes mix strings and integers; a code on a history or choice state; or a code binding whose codes don't cover the lifecycle (decision 11) | `sync-codes` |
| MQ9207 | E | A code doesn't fit its binding (decision 13) | Use a fitting code |
| MQ9208 | W | Values a binding can hold that no state covers (decision 14) | Add a state, or narrow `allowedValues` |
| MQ9210 | W | Under a code binding, a state the column must hold has no code (decision 12) | `sync-codes` |
| MQ9211 | W | A context attribute named `subject` hides the object from conditions | Rename it |

MQ9209 is left free. MQ9019 gains the refusals of `sync-codes` and of `set-lifecycle` on the `subjects` form. MQ3102 covers a `subject.x` that is not in scope.

## 7. The resolved model and templates

```csharp
public sealed class RProcess
{
    public RList<RSubject> Subjects { get; internal set; }    // one entry for the one-subject form
    public bool CodesCover { get; internal set; }             // decision 4
    // Subject, BoundAttribute, BoundEnum: set only when there is exactly one subject, as today
}
public sealed class RSubject
{
    public REntity Entity { get; internal set; } = null!;
    public RAttribute? BoundAttribute { get; internal set; }
    public REnum? BoundEnum { get; internal set; }
    public RReferenceType? BoundReferenceType { get; internal set; }
}
public sealed class RState { public object? Code { get; internal set; } }   // a string or a long, or null
public sealed class REntity { public RList<RProcess> Lifecycles { get; internal set; } }   // every lifecycle that governs it, by name
```

- `entity.lifecycle` is unchanged: what the entity names, with the same keys as today.
- `entity.lifecycles` is new, keyed like `entity.operations` on `k:process` and the entity's referrers. Only a template that reads it pays for those keys.
- `process.bound_states` and `state.bound_member` keep their meaning for the one-subject form.
- A template walking a lifecycle with several subjects reads `process.subjects`.
- The records add `subjects`, `code` and `lifecycles` only where the new form or codes are used, so a model without them answers exactly as today.
- A reference binding's nodes also depend on the reference type's file.

## 8. Tools

- **`get_references`, `referenced_by`:** a code under a reference binding shows as a use with field `code`.
- **`export_process`, `import_process`:** `code` goes in `meta.maquettiste`, and the importer's kept state keys gain it. An import keeps writing the one-subject form, which stays valid.
- **`simulate_process`, `record_scenario`, `verify_scenarios`:** a scenario's `objects` and `subject`.
- **Tool and verb texts** describe subjects and codes.

## 9. Packs

Output changes only for a model that uses codes, the `subjects` form, or a binding beyond an enum.

- **`csharp-dapper`:**
  - **Constants:** one per state that has a code, named by the state. Where two coded states share a name, the constant uses the state's path. A uuid code is a `static readonly Guid`.
  - **Mapping, when codes cover the lifecycle:** `ToCode` from a configuration, and `FromCode` from a code to the coded state's path. Restoring into a compound state enters its default, as the chart does.
  - **Moves, when codes cover the lifecycle:** a table of every transition as (from code, to code, trigger, event). Timers, completions and eventless moves have no event, and a move within one coded state has the same code at both ends.
  - **Enum bindings:** `ToStatus` as today for the one-subject form, and `To<Entity>Status` for each enum binding with `subjects`.
  - Nothing goes in `helpers.js`, since changing it renders every unit of the pack again. The empty step methods proposed in the second draft move to the trigger-points item, because a check or an override needs a return value, and they would have meant changing the interpreter every project shares.
- **`sql-ddl`:**
  - **Lifecycle tables (`processTables`):** these gain the subject's type and id and the current state's code, for a lifecycle whose codes cover it. The tables aren't under migrations, so a team that already created them adds the columns by hand, and the release note gives the statements.
  - **`lifecycleChecks`:** a new parameter, off by default. For each code binding to a plain status, it finds the attribute's column through the entity's binding or a mapping in each database. Then `seed.sql` reconciles a check `ck_<table>_<column>_states` there, written the way the pack writes a reference type's check today, in every dialect. It isn't in the schema snapshot, so a migration doesn't see it.
- **`process-docs`:** lists subjects, bindings and codes.
- **`samples/typescript-pack`:** the same constants, mapping and moves. An integer code is written unquoted.

## 10. Editor

- **The one-subject form edits as today.** The `subjects` form shows its subjects read-only, editable in the JSON view.
- **New process dialog:** still writes the one-subject form.
- **Bound attribute picker:** lists enum, reference and plain attributes. `enumId` and the drift column stay enum-only.
- **States tab:** gains a **Code** column, which writes a number for an integer code.
- **MQ9205 quick fix:** writes the code for a code binding.
- **Mock backend:** learns the new rules, `subjects` and code uses (`mocks/model/validate.ts`, `refs.ts`, `store.ts`), and the recorded rule list follows.

## 11. A project that uses none of this

- **Files:** nothing rewrites, deprecates or warns about the one-subject form, so saves, `format` and `format --check` behave as today, and snapshots import as today.
- **Findings:** the same. The changed rules keep their old results for the one-subject form and an enum binding.
- **Output:** the same bytes. Every pack change is conditional on codes, `subjects` or a binding beyond an enum, and generated scenario tests don't change (decision 20). The golden tests prove it.
- **Tool answers:** the same, since new record members are left out when unused.
- **Speed:** the same. The index fallback, the validator's reach and the new dependency keys apply only to models and templates that use them. `entity.lifecycle` keeps its keys.
- **Once, after upgrading:**
  - The schema copies change, so `validate` warns MQ1008 until they are refreshed.
  - The schema hash forces one full re-read of the index cache.
  - Changed templates render once more and write the same bytes.
- **Upgrading.** A file using `subjects`, codes or the new bindings can't be read by an older engine, so this belongs in 0.13.0. `formatVersion` stays 1, since this engine refuses any other version and raising it would refuse every existing model.

## 12. What comes next

[Who, what and when](lifecycle-actors-scenarios.md) section 10 gives the order and the reasons. In short:
1. Actors.
2. Verbs.
3. Setting the object's data from a move, and reading another lifecycle's state.
4. Rights, with rules and freezing folded in.
5. Trigger points, including the generated step methods.
6. Richer sign-off.

Relations with several types per end, revisions, lifecycles on custom kinds and tables, and one of several lifecycles per object come alongside. Two keys on a subject entry are kept for those items, but not accepted yet:
- `when`, a condition for "this lifecycle applies when";
- `owner`, the attribute or to-one end that holds the object's owner.

## 13. For the owner to decide

Each has a recommendation, and the sections above follow it.

1. **Two forms for subjects.** The one-subject form stays valid and silent, and `subjects` is for two or more (decision 6). Recommended: yes. It is the only way to change nothing for the team already using lifecycles. With nothing breaking, 0.13.0 is still right, because of the new keys.
2. **Several instances of one lifecycle per object.** Recommended: a detail entity, as you suggested, shown in the guide. Nothing in the format for now.
3. **Missing codes under a binding as a warning, not an error (decision 12).** Recommended: yes, so editing a lifecycle never needs one big save.
4. **A check on a plain status column.** Recommended: the `sql-ddl` parameter, off by default. It adds nothing to the model's format.
5. **The order after 4.1.** Recommended: as in [Who, what and when](lifecycle-actors-scenarios.md) section 10.

## 14. Tests

- **`ProcessRuleTests`:** failing and passing cases for every new or changed rule. The one-subject form gets identical findings before and after.
- **`LifecycleSubjectTests` (new):**
  - Both forms resolve to the same subjects list, and a file with both is refused.
  - One lifecycle over two entities, and two lifecycles over one entity.
  - `entity.lifecycle` and `entity.lifecycles`.
  - `set-lifecycle` on each form.
  - One attribute bound twice, through a base and a derived entity.
  - `sync-enum` with `entity`, and MQ9204 for shared enums.
- **`LifecycleCodeTests` (new):**
  - Codes on root and nested states with no binding, and the cover rule with parallel regions.
  - Every binding type: enum, reference, plain string, plain integer, inherited, and through a scalar type.
  - Missing codes warn.
  - Reference refactors rewrite a code, and two reference bindings refuse a rename the other type doesn't share.
  - **The index, by patch and by full build alike:** an attribute retyped in another file, an attribute added then bound, and an enum binding retyped to a reference type.
- **`SyncCodesTests` (new):** matching by code and by label, ambiguous matches, `stateCodes`, integer codes, binding in one step, and no rows created.
- **`ProcessConditionTests`:**
  - A condition reads the object's attributes, a shared base's, a shared stereotype's, and a to-one navigation.
  - Attributes that only share a name are out of scope.
  - The bound attribute is out of scope.
  - A context attribute named `subject` keeps its meaning and warns.
- **`ScenarioObjectTests` (new):**
  - Objects with ids and links.
  - `subject` by id, and deleting the subject's entity reported, not silently swapped.
  - Defaults, simulation and recording with objects.
- **No change:** a model with the one-subject form, an enum binding and stub guards gives the same findings, output, files, `format --check` result and tool answers. The pack golden tests pass untouched.
- **Pack tests:**
  - `csharp-dapper`'s constants, mapping and moves build with warnings as errors, for string, integer and uuid codes. `ToCode` and `FromCode` round trip.
  - A lifecycle whose codes don't cover it gets constants only.
  - The same in the TypeScript sample.
  - The `sql-ddl` check, through a binding and through a mapping, runs twice cleanly.
- **Acceptance:** one lifecycle governs invoices, bound to a status reference type, and credit notes, with no binding. Its codes reach the generated constants and moves, a guard reads `subject.total > 0` in simulation, and a scenario with an invoice and its customer replays.
- **By hand, as for shared guards:** a copy of the larger process fixture moved to codes and two subjects validates, its scenarios pass, the packs generate, and the generated C# builds and its tests pass.

Each stale-output test is checked to fail without its fix.

## 15. Tasks

One pull request per group, each stacked on the one before. The sizes are rough, and include tests and a review. The code check judged the second draft's three days to be about half the real cost, and this version's estimate takes that into account.

| Group | What | Size |
| --- | --- | --- |
| Codes | State codes, their rules, and whether they cover the lifecycle | Half a day |
| Subjects | `subjects` beside the one-subject form, one list for every reader, `set-lifecycle`, `entity.lifecycles`, the editor's read-only view and the mock | About a day and a half |
| Bindings | Enum, reference and plain bindings per subject, `sync-enum`'s `entity`, `sync-codes`, the index fallback, the validator's reach, reference refactors | About a day |
| Guards | Conditions over the object, through shared ids and to-one navigations | Half a day to a day |
| Scenario objects | `objects`, `subject` and links, the replayer, simulate and record, the records | About a day |
| Output | `csharp-dapper`, `sql-ddl`, `process-docs`, the TypeScript sample, the sample app's copies | About a day |
| Docs | User guide, processes guide (including the detail entity), releases, modeling skill, `next.md` | A few hours |

That comes to about five to six days.

**Checklist for every group, from what 4.2 taught and both checks found.** Anything that reads or writes a lifecycle, a state, a subject or a scenario must be found and handled:
- **Readers of the subject:** `ModelValidator`'s peers for entity edits, `ProcessRules` for libraries, the index row, `XStateImporter`, and `csharp-dapper`'s operation code (`entity.lifecycle`).
- **Import and export:** the importer's kept keys and the exporter.
- **Packs:** every pack, including `samples/`, and the sample app's committed copies (`node tools/build-model.mjs`).
- **Mocks:** the mock backend and the recorded rule list.
- **Contracts:** the schema copies, and `openapi.yaml` with its generated files.
- **Test harness:** `RenderKit.WithSettings` and `ResolvedCoverageTests`.
- **Dependency keys:** on anything read across files, and a template that reads only one list.
