# Lifecycles any implementation can follow: design

**Status (2026-11-05): the owner's decisions are in section 13. Four independent checks have read this design, and this version answers the fourth. Nothing in this file is built.** It designs item 4.1 of `next.md`. [What lifecycle systems can do](lifecycle-capabilities.md) is the yardstick, and [Who, what and when](lifecycle-actors-scenarios.md) sets the direction this item starts. A file path without a project prefix is under `src/Maquettiste.Engine/`. The examples are invented: the billing fixture has no processes. Each new name and format is held to the roadmap's test: it stays for good, it must help any team whether they use it or not, and it stays optional.

**How this version came about.**
- **First draft:** assumed a single status column.
- **Second draft:** removed that assumption. A check against the code and a check of its value to the four uses found that it would have broken things for the team already using lifecycles, and that parts of it couldn't work.
- **Third draft:** fixed that. A third check, against the code, found two remaining changes for teams that don't use any of this, rules that contradicted each other, and decisions left open.
- **Fourth draft:** answered the third check. A fourth check found three more slips, the main one about how an enum is stored, and judged the build plan too small by about half.
- **This version:** answers all four checks, and each decision says what it settles.

## 1. In short

A lifecycle in Maquettiste is a process with `use: lifecycle`. It is a model: Maquettiste runs nothing. A team may generate code from it with packs, build from it by hand, or implement it in a lifecycle system. Either way, the model must not decide how the state is stored, or which and how many types follow it.

Today it decides too much. A lifecycle has one entity as its subject, the entity names one lifecycle, and the state can only be held in one enum attribute matched by name.

| Layer | What changes |
| --- | --- |
| The model | A state may carry a `code`, its stable stored value, however the state is stored. A lifecycle lists the entities it governs in a new `subjects` list, and the one-subject form stays valid with a deprecation warning. Each subject may bind an attribute that holds the state: an enum, a reference type, or a plain string or integer. Guard conditions read the object. A scenario names its objects |
| Code generation | Templates read codes and subjects. `csharp-dapper` writes the codes, maps states to codes and back, and lists every move from code to code. `sql-ddl` can add the subject and current code to its lifecycle tables, and can write a check on a status column, each behind its own parameter |
| The team's system, or a lifecycle system | Nothing changes until the team uses the new output or builds from the model |

**A project that uses none of this** keeps its output and files. It gets one deprecation warning per process written in the one-subject form, lifecycles and orchestrations alike, which the owner chose and which can be turned off, and the usual once-per-upgrade effects. Section 11 lists everything.

## 2. What the owner said

- **Maquettiste is a modeling tool first.** A rule that limits what can be said is a barrier, and a hand-written stub is a fine result.
- **A lifecycle's state usually lives in its own tables, not a single status column.**
- **One lifecycle can govern many types, and one object can follow many lifecycles.** Several instances of one lifecycle per object are a detail entity, such as a project assignment.
- **Triggers exist for every operation**, and relations can allow several types at an end. Each is its own item (section 12).
- **Both subject forms stay, and the old one is deprecated with a warning.**

## 3. Scope

| | What | Section |
| --- | --- | --- |
| **4.1, first half** | State codes; `subjects`; a state attribute per subject; output for codes and moves | 4 to 11 |
| **4.1, second half** | Guard conditions that read the object; a scenario's objects | 4 (decisions 20 to 23) |
| Next, a design each | Actors, verbs, setting the object's data and reading another lifecycle's state, rights, trigger points, richer sign-off. Relations with several types, revisions, and lifecycles on custom kinds and tables come alongside | 12 |

The halves can ship separately. The first is what teams asked for: codes, and lifecycles over several types.

## 4. Decisions

### State codes

1. **Any state may carry a `code`:** a string or an integer, its stable stored value.
   - It is optional, needs no binding, and can sit on a nested state.
   - History and choice states can't have one, since nothing rests in them.
   - Codes are unique within a lifecycle, and all strings or all integers.
   - They are compared by `ReferenceDataIndex.CodeKey`, so `1` and `"1"` are the same code.
2. **A literal, not a row id.** It reads like a default or `allowedValues` does, and it works where there are no rows.
3. **One code per state, for every subject.** A type that needs another encoding gets its own lifecycle, or a pack maps the values.
4. **A lifecycle's codes form a map, or they don't.**
   - **The rule:** they form a map when no two coded states sit on one path from the root, and no coded state sits inside a parallel region. Then any configuration has at most one code.
   - **What the map gives:** the resolved model says whether they do (`process.codes_map`), and output that maps states to codes depends only on this (section 9).
   - **States without a code:** a reachable state with no coded state on its path just has no code. Adding codes one at a time never switches output on and off. The mapping says "no code" for such a state, and a binding warns about it (decision 13).
   - **Breaking the rule:** codes that don't form a map are allowed with no binding, and get constants only. Under a binding they are an error (decision 12).

### Subjects

5. **A lifecycle may list what it governs.** `subjects` is a list of entries, each `{ "id": <id>, "entity": <id>, "boundAttribute": <id>? }`.
   - **Ids:** entries have ids, like every other part of a process. So a finding can be suppressed per subject, and later keys can point at an entry.
   - **Derived entities:** an entry covers its entity and the entities derived from it.
   - **Several lifecycles:** listing an entity in several lifecycles means its objects follow all of them at once.
   - **One of several:** a type whose objects follow one of several lifecycles is a later item. It will add a condition to the entry, so an entry without one keeps its meaning.
   - **Orchestrations:** an orchestration may list subjects too, for what its flow is about, but binds nothing.
   - **Deleting a listed entity:** its entry goes, under either delete choice, the way an entity's binding does. An emptied list is removed, so the lifecycle has no subject and gets MQ9201, as today when its one subject is deleted.
6. **The one-subject form stays valid, and is deprecated with a warning (the owner's decision).**
   - **Unchanged:** `subject` and `boundAttribute` on a process keep their meaning and rules, MQ9201 included. They accept the new binding types too, since a binding is the same thing in either form.
   - **The warning:** MQ9209 warns on any process using it, lifecycle or orchestration, and can be turned off.
   - **Converting on request:** the quick fix and the batch operation `use-subjects` (with `elementIds`, like the other conversions) convert on request. Nothing converts on its own: the second draft did, and broke `format --check`, teammates on older engines and snapshot import.
   - **Both at once:** the schema refuses both forms in one file. Internally, one list serves every reader: `Process.Subjects`.
7. **Tools never create the deprecated form from nothing, and never change a file's form unasked.**
   - **No subject yet:** a process that has no subject gets `subjects` from whatever gives it one: the new-process dialog, `set-lifecycle`, `sync-codes`, `import_process` and the Subject control.
   - **Old form already there:** a process already in the one-subject form keeps it through any edit, a re-import included. The importer drops carried keys that would change the form.
   - **The cost (section 13):** a lifecycle created this way can't be read by a teammate on an older engine, so a team upgrades together.
8. **`entity.lifecycle` keeps its meaning:** it names one of the entity's lifecycles.
   - In the one-subject form, MQ9201 checks that the entity names its lifecycle back, as today.
   - With `subjects` the entity need not name it, since several lifecycles may govern it. So converting drops that one check, and the findings change by it.
9. **`set-lifecycle` keeps today's behaviour for today's form, and stops forcing "only one".**
   - **Target in the one-subject form, or with no subject:** as today. The target becomes the entity's named lifecycle with that one subject, and the entity's previous lifecycle in the one-subject form becomes an orchestration. A target with no subject gets `subjects`.
   - **Target using `subjects`:** the entity is added if missing, and the target becomes the entity's named lifecycle. The entity's previous lifecycle in the one-subject form becomes an orchestration, as today, since it would otherwise fail MQ9201. Its other lifecycles using `subjects` are left alone, since several may govern it.
   - **No target:** the entity's named lifecycle is cleared. Lifecycles in the one-subject form for this entity become orchestrations, as today. The entity is removed from the `subjects` of the others, and one left with no subjects becomes an orchestration, which mirrors today.
   - **The new-process dialog** keeps today's default, making the new lifecycle the entity's only one. As when clearing, the entity is also removed from the `subjects` of its other lifecycles, and one left with none becomes an orchestration. A choice keeps the entity's other lifecycles that use `subjects`.
10. **The editor's Use and Subject controls work on a lifecycle with one subject, in either form,** editing the form the file has. With two or more subjects, the controls show them read-only, editable in the JSON view. Demoting a lifecycle to an orchestration clears its bindings and keeps its subjects, as today.

### Holding the state in an attribute

11. **A subject may bind the attribute that holds its state.** It is a single-valued attribute of that subject, own, inherited or from a stereotype, as today. It may be typed by:
    - an enum, as today;
    - a reference type;
    - a plain status: `string`, `text`, `int16`, `int32` or `int64`, directly or through a scalar type.

    **Two lifecycles binding one attribute:**
    - Each subject binds its own attribute.
    - Two lifecycles may not bind the same attribute for an entity that both of them list by name (MQ9202). Covering a derived entity doesn't count. This can only happen with `subjects`, since the one-subject form already lets an entity name only one lifecycle.
    - Two lifecycles binding one inherited or stereotype attribute for different entities stays allowed, as today. That includes a lifecycle on a base entity and another on an entity derived from it.
12. **Under a binding, the codes must form a map** (decision 4), so the column can hold the state as one value. Otherwise it is MQ9206.
13. **A missing code under a binding is a warning (MQ9210).** A three-save edit (add a state, add a transition into it, type its code) is never refused.
14. **An enum binding works as today, and its states' codes, when given, match how the enum is stored.**
    - It matches root states by name (MQ9203), and MQ9205 still compares the default with the initial state's name.
    - An enum is stored as an integer by default: the member's value, else its position. It can also be stored as text: the member's code, else its name.
    - A string code must equal its member's text form, and an integer code its member's integer form. Otherwise it is MQ9207.
    - So a code and the enum never disagree, however a database stores the enum, and a lifecycle can mix an enum binding with other bindings.
15. **`sync-enum` gains an optional `entity`.** It defaults to the lifecycle's one enum binding in either form, so the MQ9203 quick fix keeps working after converting. Subjects of one lifecycle may share an enum without MQ9204 warning about each other.
16. **The codes must fit the attribute (MQ9207).**
    - A reference type's codes must be live codes of the type, and a former code is reported with its replacement.
    - A plain status's codes must fit its type, length, pattern and `allowedValues`.
    - With two reference bindings to different types, a code is renamed in both types in one batch. This check runs when the batch ends, so that passes, and renaming it in one type alone is refused. That is new: today each code rename is checked inside its own operation.
17. **Values with no state are a warning (MQ9208),** once per binding: the attribute's `allowedValues` when it has them, else the type's rows in the model. Rows a script fills are not judged.
18. **A renamed code follows.**
    - A code under a reference binding is listed as a use of the row, field `code`. So `get_references` and `referenced_by` see it, deleting the row is refused, and renaming, merging or retiring the code rewrites it.
    - A merge or retire that would leave two states one code is refused.
    - **Keeping the index right:** the index reads a bound attribute's type, which can sit in another file, from a map that only a full build refreshes. So its patch path falls back to a full build when a changed process adds, removes or changes a binding, and when a changed document retypes or removes an attribute that a process binds. Both need a binding, so a project without one never pays.
    - **Checking the right lifecycles:** the validator keeps a map from bound attribute to lifecycles, so a save of a reference type, seed or scalar type revisits only the lifecycles that bind one, at no cost when none do.
19. **`sync-codes` proposes codes and binds them.** It is a batch operation:
    ```json
    { "op": "sync-codes", "id": <process>, "entity": <subject>, "attribute": <attribute>, "stateCodes": { <state id>: <code> } }
    ```
    - It keys states by id, since names are unique only among siblings. Values are strings or integers, in its own field.
    - A state keeps a code it has. Otherwise it matches a code equal to the state's name (ignoring case, `_` and `-`), then a row whose label equals the state's display name or name.
    - An ambiguous match is unmatched, and `stateCodes` gives the rest.
    - It sets the binding in the form the file has, or `subjects` when the process has no subject.
    - On a lifecycle in the one-subject form, `entity` must be its subject, and another entity is refused.
    - It creates no rows. A dry run lists what it would write and what is unmatched.

### Guards that read the object (second half)

20. **A guard condition may read the object.** Conditions name attributes by id, so in scope is what every subject holds under one id:
    - the subject's own and inherited attributes when there is one subject;
    - with several subjects, the attributes of a base entity they all derive from.

    **Also in scope:** to-one navigations, following the same rule, as an operation's guards already do. The path ends at the declaring attribute, the base's, so the resolved condition has one target.

    **Kept out:**
    - A stereotype's attributes come with the item that makes a stereotype a set of types ([Who, what and when](lifecycle-actors-scenarios.md) section 4), since nothing reads them in an entity's scope yet.
    - The bound attribute, since the chart already knows its state.
    - The object, when a context attribute is named `subject`. That attribute keeps its meaning, with no new warning.
    - JavaScript expressions. Packs write those as they are, and library guards hold only expressions, so they don't read the object for now.
21. **Generated code leaves such a guard to a person**, as for any guard it can't translate. `csharp-dapper` writes a partial method, with the condition in the generated half's comment. Giving generated code an object to read belongs to the generated runtime (4.6). Generated scenario tests behave as today, so no existing output changes.
22. **A scenario names its objects.** It gains `objects`, its cast, and `subject`, the object its process runs for:
    ```json
    "objects": [
      { "id": <id>, "name": "invoice", "entity": <entity id>, "values": { <attribute id>: <value> },
        "links": [ { "end": <relation end id>, "object": <object id> } ] }
    ],
    "subject": <object id>
    ```
    - **Objects are parts of the scenario:** they have an index kind and name forms (`<scenario>.<object>`). `subject` and each link's `object` are indexed references, so deletes and `get_references` see them.
    - **`subject`:** required once there are objects.
    - **Deleting an object's entity:** with `remove-references`, the object and its links go. A `subject` left pointing at nothing refuses the delete, unless the scenario is deleted too.
    - **Missing values:** a value not given reads as the attribute's default, then null, as context does. A bad value is a replay finding, so it doesn't refuse a save.
    - **No objects:** a guard reading the object sees defaults, then null, and only true passes.
    - **Sub-processes:** an invoked sub-process sees no object.
    - **`process`:** stays required for now. It becomes optional when steps name the object they act on, and `subject` then sets the default.
    - **Simulating and recording:** `simulate_process`, `record_scenario` and the editor's simulation panel take the same `objects` and `subject`.
    - **Replay:** a scenario's replay also depends on its process's subjects, its objects' entities, and their base entities, so a changed default replays it.
    - **Literal uses:** `sync-enum`'s check counts object values. Renaming a code doesn't rewrite them, as for scenario values today.
23. **The index row** of a process keeps `subject` when it has exactly one subject, in either form, and gains `subjects` when it has two or more.

## 5. Schema and files

| File | Key | Type | Notes |
| --- | --- | --- | --- |
| `process.json` | `subjects` (after `use`) | list of `{ id, entity, boundAttribute? }` | Decisions 5 and 11; refused with `subject` or `boundAttribute` |
| `process.json` | state `code` (after `type`) | string or integer | Decision 1 |
| `scenario.json` | `objects`, `subject` (after `process`) | as in decision 22 | Second half |
| `batch.json` | `sync-codes` with `entity`, `attribute`, `stateCodes`; `use-subjects` with `elementIds`; `sync-enum` gains `entity` | | Decisions 6, 15 and 19 |

Also updated: the sample app's schema copies, `openapi.yaml` by hand, and the editor files generated from it.

```json
{
  "kind": "process",
  "name": "ReleaseLifecycle",
  "use": "lifecycle",
  "subjects": [
    { "id": "…", "entity": "<id of Order>", "boundAttribute": "<id of Order.status, typed by the reference type ReleaseStatus>" },
    { "id": "…", "entity": "<id of ReturnNote>" }
  ],
  "states": [
    { "id": "…", "name": "Draft", "code": "DRF" },
    { "id": "…", "name": "Review", "code": "REV", "states": [ { "id": "…", "name": "Finance" }, { "id": "…", "name": "Legal" } ] },
    { "id": "…", "name": "Released", "code": "REL", "type": "final" }
  ]
}
```

## 6. Rules

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ9201 | E | Unchanged for the one-subject form. With `subjects`: an empty list on a lifecycle, or an entity listed twice | Add a subject |
| MQ9202 | E | Changed: a binding isn't a single-valued enum, reference type or plain status attribute of its subject (own, inherited or from a stereotype); a binding on an orchestration; or two lifecycles binding one attribute for an entity both list | Bind another attribute |
| MQ9203 | E | Unchanged, per enum binding | `sync-enum` |
| MQ9204 | W | Unchanged, but quiet about subjects of the same lifecycle | |
| MQ9205 | W | Changed: per binding. For an enum binding, unchanged: the default isn't the initial state's name. For other bindings, the default isn't the initial state's code | Set the default |
| MQ9206 | E | Two states share a code; codes mix strings and integers; a code on a history or choice state; or, under a binding, codes that don't form a map | `sync-codes` |
| MQ9207 | E | A code doesn't fit its binding (decisions 14 and 16) | Use a fitting code |
| MQ9208 | W | Values a binding can hold that no state covers | Add a state, or narrow `allowedValues` |
| MQ9209 | W | The deprecated one-subject form | `use-subjects` |
| MQ9210 | W | Under a binding, a reachable state with no code | `sync-codes` |

MQ2002 already covers a subject that isn't an entity. MQ9019 gains the refusals of `sync-codes`. MQ3102 covers a `subject.x` that isn't in scope. Quick fixes that act on one binding pass its subject's `entity`.

## 7. The resolved model and templates

```csharp
public sealed class RProcess
{
    public RList<RSubject> Subjects { get; internal set; }    // one entry for the one-subject form
    public bool CodesMap { get; internal set; }               // decision 4
    // Subject, BoundAttribute, BoundEnum: set only when there is exactly one subject, in either form
}
public sealed class RSubject
{
    public string Id { get; internal set; } = "";              // the entry's id; the process's own id for the one-subject form
    public REntity Entity { get; internal set; } = null!;
    public RAttribute? BoundAttribute { get; internal set; }
    public REnum? BoundEnum { get; internal set; }
    public RReferenceType? BoundReferenceType { get; internal set; }
}
public sealed class RState { public object? Code { get; internal set; } }   // a string or a long, or null
public sealed class REntity { public RList<RProcess> Lifecycles { get; internal set; } }   // lifecycles governing it or a base of it, by name
```

- **`entity.lifecycle`:** unchanged, with its keys.
- **`entity.lifecycles`:** keyed on `k:process` and on the referrers of the entity and of each of its base entities, since a lifecycle may list a base. Only a template that reads it pays for those keys.
- **`process.bound_states` and `state.bound_member`:** keep their meaning for a lifecycle with one subject.
- **Records:** they add `subjects`, `code` and `lifecycles` only where used. A reference binding's nodes also depend on the reference type's file.

## 8. Tools

- **`get_references`, `referenced_by`:** a code under a reference binding is a use with field `code`. Scenario objects are parts with references.
- **`export_process`, `import_process`:** `code` goes in `meta.maquettiste`, and the importer's kept state keys gain it. The form follows decision 7.
- **`simulate_process`, `record_scenario`, `verify_scenarios`:** `objects` and `subject`.
- **The rule list, the schemas and the tool and verb texts** describe all of this.

## 9. Packs

Output changes only for a model that uses state codes, the `subjects` form, more than one subject, or a binding beyond an enum, or that turns on a new parameter.

**`csharp-dapper`**
- **Constants:** one per state that has a code, named by the state, or by its path where two coded states share a name. They are `string` or `long`, by the codes' kind.
- **Mapping, when the codes form a map:**
  - `ToCode` from a configuration, which returns no code for a state without one.
  - `FromCode` from a code to the coded state's path. Restoring into a compound state enters its default, as the chart does.
- **Moves, when the codes form a map:** a table of every transition as (from code, to code, trigger, event).
  - A move into a history or choice state has no fixed "to" code; it is decided when the move runs.
  - Timers, completions and eventless moves have no event.
  - A move within one coded state has the same code at both ends.
- **Enum bindings:** the template reads `p.subjects`. A lifecycle with one subject, in either form, keeps today's output: `ToStatus` for an enum binding, else the per-state enum and `Parse`. A lifecycle with several subjects gets the per-state enum and `Parse`, plus `To<Entity>Status` for each enum binding, qualified by package where two subjects share a name.
- **What stays out:** nothing goes in `helpers.js`, since changing it renders every unit of the pack again. Step methods for triggers come with the trigger-points item.

**`sql-ddl`**
- **`lifecycleColumns`:** a new parameter, off by default. It adds to the lifecycle tables (`processTables`) the subject's type, its id as text, and the current state's code when the codes form a map. Being a parameter, it never changes tables already created. A team that turns it on for existing tables adds the columns by hand, and the release note gives the statements.
- **`lifecycleChecks`:** a new parameter, off by default.
  - **What it checks:** for each binding to a plain status, it finds the attribute's column in each database, through the entity's binding or a mapping.
  - **What it writes:** `seed.sql` reconciles a check `ck_<table>_<column>_states` there, the way the pack writes a reference type's check today, in every dialect.
  - **The cost:** it isn't in the schema snapshot, so a migration doesn't see it.

**Docs and samples**
- **`process-docs`:** lists subjects, bindings and codes.
- **`samples/typescript-pack`:** the same constants, mapping and moves. An integer code is written unquoted.

## 10. Editor

- **Use and Subject:** work on a lifecycle with one subject, in either form. With two or more, the subjects are read-only, editable in the JSON view. Every new lifecycle is written as `subjects`.
- **Bound attribute picker:** lists enum, reference and plain attributes. `enumId` and the drift column stay enum-only.
- **States tab:** gains a **Code** column, which writes a number for an integer code.
- **Quick fixes:** MQ9205 writes the initial state's name for an enum binding, as today, and its code otherwise. MQ9209 converts. Quick fixes for one binding pass its subject.
- **Simulation panel:** carries a scenario's objects.
- **Readers of `subject` and `boundAttribute`, moved onto the one list:**
  - the explorer's row label, and its tree (`explorer/tree.ts`);
  - the process editor (`editors/process/ProcessEditor.tsx`);
  - the statechart canvas;
  - the inspector's process sections;
  - the MQ9201 and MQ9205 quick fixes, and the quick-fix list (`problems/quickFix.ts`);
  - the undo id list in `editors/process/api.ts`;
  - the mock backend's `validate.ts`, `refs.ts`, `store.ts` and `processHandlers.ts`. The recorded rule list follows.

## 11. A project that uses none of this

- **Files:** nothing rewrites a file unless the team asks, so saves, `format`, `format --check` and snapshot import behave as today.
- **Findings:** the same, plus one MQ9209 warning per process in the one-subject form, which can be turned off. Orchestrations count too, since the new-process dialog gives them a subject today. No changed rule gives a new error for today's models, and the shared-attribute cases in decision 11 stay allowed. One error goes away: a one-subject lifecycle that binds a plain attribute has MQ9202 today, and gets MQ9210 warnings instead.
- **Output:** the same bytes. Every pack change depends on new keys or a new parameter, and generated scenario tests don't change.
- **Tool answers about the model:** the same, since new record members are left out when unused. The rule list, the schemas and the tool texts describe the additions.
- **Speed:** the same. The index fallback, the validator's map and the new keys apply only where used.
- **Once, as on any upgrade:**
  - Every unit renders again and writes the same bytes, since the engine version is part of each unit's key.
  - The editor refreshes the `.schema` copies, or `validate` warns MQ1008 until refreshed.
  - The index cache is read in full once.
- **A team on mixed engine versions:** new lifecycles are written as `subjects`, which an older engine can't read (decision 7). So this is 0.13.0, and the release note says a team upgrades together. `formatVersion` stays 1, since this engine refuses any other value.

## 12. What comes next

[Who, what and when](lifecycle-actors-scenarios.md) section 10 gives the order and the reasons:
1. Actors.
2. Verbs.
3. Setting the object's data from a move, and reading another lifecycle's state.
4. Rights, with rules and freezing folded in.
5. Trigger points, including the generated step methods.
6. Richer sign-off.

Relations with several types per end, revisions, lifecycles on custom kinds and tables, and one of several lifecycles per object come alongside. Two keys on a subject entry are kept for those items, but not accepted yet:
- `when`, a condition for when the lifecycle applies;
- `owner`, the attribute or to-one end that holds the object's owner.

## 13. The owner's decisions (2026-11-05)

1. **Subject forms:** both stay. The one-subject form is deprecated with a warning and converted only on request (decision 6). This is 0.13.0.
2. **Several instances of one lifecycle per object:** a detail entity, shown in the guide, with nothing in the format.
3. **A missing code under a binding:** a warning (decision 13).
4. **A check on a plain status column:** the `sql-ddl` parameter, off by default.
5. **The order after 4.1:** as in [Who, what and when](lifecycle-actors-scenarios.md) section 10. The clearer Processes explorer comes right after 4.1.

**Two things for the owner to confirm, from the third check:**
- **Mixed engine versions.** New lifecycles are written as `subjects`, so a teammate on an older engine can't read one. That follows from deprecating the old form. The other choice is to keep writing the old form for a lifecycle with one subject until the team converts. It keeps mixed teams working, but Maquettiste would go on creating what it warns about.
- **Two halves.** The first half covers codes, subjects, bindings and output. The second covers guards that read the object, and scenario objects. They could ship as two releases, since the first half is what teams asked for.

## 14. Tests

- **`ProcessRuleTests`:** failing and passing cases for every new or changed rule. Today's models, including two lifecycles sharing an inherited or stereotype attribute for different entities, or on a base entity and a derived one, get the same findings plus MQ9209.
- **`LifecycleSubjectTests` (new):**
  - Both forms resolve to one list, and a file with both is refused.
  - Entries have ids, and a suppression per subject works.
  - Derived entities are covered.
  - One lifecycle over two entities, and two over one.
  - `entity.lifecycle` and `entity.lifecycles`.
  - `set-lifecycle` and the dialog in each case of decision 9.
  - `use-subjects` gives the same findings except MQ9209 and the back-reference check.
  - New processes come out as `subjects`, and an old-form file keeps its form through edits and re-import.
  - Deleting a listed entity under either delete choice, including the last one.
- **`LifecycleCodeTests` (new):**
  - Codes on root and nested states with no binding, and the map rule.
  - A state without a code keeps the mapping on.
  - Every binding type: enum (string codes against members' codes or names, integer codes against their values or positions), reference, plain string, plain integer, inherited, from a stereotype, and through a scalar type.
  - Renaming across two reference types in one batch.
  - Reference refactors.
  - **The index, by patch and by full build alike:** an attribute retyped in another file, an attribute added then bound, and an enum binding retyped to a reference type.
- **`SyncCodesTests` (new):** matching by code and by label, ambiguous matches, `stateCodes`, integer codes, both forms, and no rows created.
- **`ProcessConditionTests`:**
  - Reading the object's attributes, a shared base's, and a to-one navigation.
  - Attributes that only share a name, a stereotype's attributes and the bound attribute are out of scope.
  - A context attribute named `subject` keeps its meaning.
- **`ScenarioObjectTests` (new):**
  - Objects as parts, with references and name forms, and links.
  - `subject` by id.
  - Deleting an object's entity.
  - Defaults, and no objects.
  - A changed default replays the scenario.
  - Simulating and recording with objects.
- **No change:** a model with the one-subject form, enum bindings and stub guards gives the same output, files, `format --check` and tool answers, and the same findings plus MQ9209. The pack golden tests pass untouched.
- **Pack tests:**
  - `csharp-dapper`'s constants, mapping and moves build with warnings as errors, for string and integer codes. `ToCode` and `FromCode` round trip, including a state without a code and moves into history.
  - Codes that don't form a map get constants only.
  - The same in the TypeScript sample.
  - `sql-ddl`'s two parameters, through a binding and a mapping, run twice cleanly.
- **Acceptance**, on an invented model:
  - One lifecycle governs orders, bound to a status reference type, and return notes, with no binding. Both derive from a base entity with `total`.
  - Its codes reach the generated constants and moves.
  - A guard reads `subject.total > 0` in simulation.
  - A scenario with an order and its customer replays.
- **By hand, as for shared guards:** a copy of the larger process fixture moved to codes and two subjects validates, its scenarios pass, the packs generate, and the generated C# builds and its tests pass.

Each stale-output test is checked to fail without its fix.

## 15. Tasks

The sizes are days for a person who knows the code, and include tests and a review. They show how the groups compare. Section 16 gives hours and tokens for building with agents, and how the groups become pull requests.

| Half | Group | What | Size |
| --- | --- | --- | --- |
| First | Codes | State codes, their rules, and whether they form a map | Half a day |
| First | Subjects | `subjects` with ids beside the deprecated form, one list for every reader, MQ9209 and `use-subjects`, decisions 7 to 10, `entity.lifecycles`, the index row, and every editor and mock reader in section 10 | Two days |
| First | Bindings | Enum, reference and plain bindings in either form, `sync-enum`'s `entity`, `sync-codes`, the index fallback, the validator's map, reference refactors, the attribute picker, the Code column and the quick fixes | Two days |
| First | Output | `csharp-dapper`, `sql-ddl`'s two parameters in every dialect, `process-docs`, the TypeScript sample, the sample app's copies, and `code` in export and import | A day and a half |
| Second | Guards | Conditions over the object, through shared ids and to-one navigations | A day |
| Second | Scenario objects | Objects as parts, links, `subject`, the replayer, simulate and record, the editor's simulation panel, records | A day and a half |
| Both | Docs | User guide, processes guide (including the detail entity), releases, modeling skill, `next.md` | Half a day |

That comes to about six days for the first half and three for the second, nine in all, for a person.

**Checklist for every group, from what 4.2 taught and the three checks found.** Anything that reads or writes a lifecycle, a state, a subject or a scenario must be found and handled:
- **Readers of the subject:**
  - the validator's peers for entity edits;
  - the library rules;
  - the index row;
  - the XState importer;
  - `csharp-dapper`'s operation code (`entity.lifecycle`);
  - every editor and mock reader in section 10.
- **Import and export:** the importer's kept keys, the exporter.
- **Packs:** every pack, including `samples/`, and the sample app's committed copies (`node tools/build-model.mjs`).
- **Contracts:** the schema copies, `openapi.yaml` and its generated files.
- **Test harness:** `RenderKit.WithSettings` and `ResolvedCoverageTests`.
- **Dependency keys:** on anything read across files, and a template that reads only one list.

## 16. How it gets built

Agents build it, so the measure is hours and tokens, not days.

**What the last build took.** Custom properties and library processes (#32 and #33) added about 2,600 lines with tests, in 85 files. They took about 2 hours, including a review, its fixes and CI. They used 0.4M output tokens, 1.0M new input and 65M cached re-reads. The fourth check counts 4.1 at 130 to 150 files and 7,000 to 8,000 lines with tests, about three times that. Most of the difference is editor and mock work, five SQL dialects, and pack test cases with their expected files.

**Estimate** (raised by the fourth check).

| | Wall time | Output | New input | Cached re-reads |
| --- | --- | --- | --- | --- |
| First half | 5 to 6 hours | 1.5M | 3.5M | 160M |
| Second half | About 1.5 hours more, since most of it runs beside the first half's output | 0.7M | 1.5M | 75M |
| In all | 7 to 8 hours | About 2.2M | About 5M | About 235M |

The first half is three pull requests in a row, each with a build, a review, its fixes and about 20 minutes of CI. The owner's part is merging five pull requests in order.

**Where the tokens go.** Every step an agent takes reads its whole context again. Those cached re-reads are most of the tokens, and they grow with steps times context. About three quarters of what agents read is code. So the plan keeps each agent's context small, its steps few, and its reading aimed.

**How the tokens are kept down.**
1. **Agents build, and I coordinate.** My context holds this whole conversation, so each of my steps costs more than an agent's. I launch agents, merge their work and open pull requests. I read their summaries, not their diffs.
2. **One map of the code, written once.** The fourth check wrote it while it still had the code loaded: the files and functions each agent changes, the tests to run, and the traps it saw. It is kept with the build, outside the repository. Each builder starts from its part instead of searching.
3. **Short agents with exact briefs.** Each group gets a fresh agent. A long-lived agent's context keeps growing, and every step re-reads it.
4. **Resuming a finished agent isn't free.** The fourth check resumed the third checker, and its whole context had to be stored again: 0.5M new input. So a fixer is a fresh agent with the exact findings, unless the builder finished minutes ago.
5. **The right model for each part.**
   - **Opus 5.5** for the engine, the pack output and every review. The pack output must compile with warnings as errors and run twice cleanly in five SQL dialects. Its failures show only in slow tests, so a slip costs a long loop.
   - **Sonnet 5.5** for the editor and mock readers, and the docs, which follow a fixed shape and have fast tests.
6. **The contract first.** In each wave, the engine agent first writes the schema and `openapi.yaml` changes and commits them. The editor agent starts from that commit, so both work against the same shapes. Only that engine agent edits `openapi.yaml`, which is written by hand.
7. **Generated files by fixed commands.** After each merge, I regenerate the editor's API types, the recorded rule list and the sample app's copies with fixed commands, not agent work. Pack expected files stay with the builder, so their changes are reviewed.
8. **Tests where they cost least.** Builders run the tests for what they touch while working, and the full .NET suite once, quietly, before handing back. The database tests and the editor's browser tests run only in CI, which costs no tokens.
9. **One small review per pull request, run once.** The reviewer gets the diff, the decisions it builds and the map's traps. I make tiny fixes myself.
10. **No polling.** I wait for agents and CI in the background, which costs nothing.

**Order.** Running agents side by side saves time, not tokens. Each builder works in its own copy of the repository, and I merge by patch.

| Wave | Agents | Pull request |
| --- | --- | --- |
| 1 | Codes and subjects in the engine (Opus), contract first. Then the editor and mock readers (Sonnet), from the contract commit | 1, on main |
| 2 | Bindings in the engine (Opus), contract first. Then bindings in the editor (Sonnet) | 2, on 1 |
| 3 | Output (Opus). Beside it: scenario objects (Opus), then guards (a fresh Opus agent) | 3 on 2; 4 on 2; 5 on 4 |
| 4 | Docs for each half (Sonnet) | On 3 and on 5 |

- **At most three agents run at once.** The second half no longer builds beside the bindings: the fourth check found nineteen files that both would change. The output and the second half share none.
- **Merging:** the first half merges in order, 1 to 3. The second half then moves onto main, as in earlier stacks.
- **Questions first.** The owner's two answers in section 13 come before wave 1. A later "keep the old form" would rework decision 7, the dialog, the importer and `set-lifecycle`.
- **A design gap found while building** stops the work, and I ask the owner. Builders don't decide scope.
