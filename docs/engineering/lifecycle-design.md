# Lifecycles any implementation can follow: design

**Status (2026-11-05): a draft for the owner. Nothing in this file is built.** It designs item 4.1 of `next.md`, rewritten after the owner's points of 2026-11-05. [What lifecycle systems can do](lifecycle-capabilities.md) is the yardstick, and section 13 lists what the owner decides. A file path without a project prefix is under `src/Maquettiste.Engine/`. Examples use the billing fixture. Each new name and format is held to the roadmap's test: it stays for good, it must help any team whether they use it or not, and it stays optional.

## 1. In short

A lifecycle in Maquettiste is a process with `use: lifecycle`. It is a model: Maquettiste runs nothing. A team may generate code from it with packs, or a product team may build it in a lifecycle system. Either way the model must not decide how the state is stored, or which and how many types follow it.

Today it decides too much. A lifecycle has one entity as its subject, the entity names one lifecycle, and the state can only be held in one enum attribute matched by name. This design removes those limits.

| Layer | What changes |
| --- | --- |
| The model | A state may carry a `code`, its stable stored value, however the state is stored. A lifecycle lists the types it governs in `subjects`, and an entity can be governed by several. Each subject may bind an attribute that holds the state, typed by an enum, a reference type or a plain string or integer. Guards read the object |
| Code generation | Templates read the codes and the subjects. `csharp-dapper` writes the codes, a table of moves from code to code, and do-nothing methods at each step of a move for a team to fill in. `sql-ddl`'s lifecycle tables hold the current code, and it can write a check on a status column |
| The team's system, or a lifecycle system | Nothing changes until the team uses the new output or builds from the model. The state may live in a column, in the lifecycle's own tables, or in another system |

The rest of what lifecycle systems do (triggers on every operation, richer sign-off, rights per state, rules, freezing, relations with several types) follows as items of their own. Section 12 gives each one's shape, so nothing here blocks them.

## 2. What the owner said, and what it changes

- **Maquettiste is a modeling tool first.** Some teams only model. A rule that limits what can be said, just because generated code can't run it yet, is a barrier. A hand-written stub, such as an empty partial method, is a fine result.
- **A lifecycle's state usually lives in its own tables, not a single status column.** So a state's code can't depend on a bound attribute, and a status column is one option among several.
- **One lifecycle can govern many types, and one object can follow many lifecycles**, such as an employee's HR lifecycle and project lifecycle. Several instances of one lifecycle on one object, such as one per project, are best modeled on a detail entity like a project assignment.
- **Triggers exist for every operation, as check, override and action.** That includes moves, signatures, freezing, locking, connecting and disconnecting. A pack can turn them into partial methods, or into a table of from-to codes with one pipeline that runs custom logic.
- **Relations can allow several types at each end.**

The first draft of this design assumed the opposite on the first three points. An independent check of that draft against the code found six serious problems, and the decisions below keep the answers to them (section 4, decisions 13 to 16).

## 3. Scope

| | What | Section |
| --- | --- | --- |
| **4.1, this design** | State codes; `subjects`; a state attribute per subject; guards that read the object; output that turns all this into codes, moves and empty step methods | 4 to 11 |
| Next, a design each | Trigger points on every operation; richer sign-off; rights per state, rules and freezing; a type that follows one of several lifecycles, and moving objects between them; relations with several types per end; revisions; lifecycles on custom kinds and tables | 12 |

## 4. Decisions

**State codes**

1. **Any state may carry a `code`:** a string or an integer, its stable stored value. It is optional, it doesn't depend on a binding, and it can sit on a nested state as well as a root one. Codes are unique within a lifecycle, and they are all strings or all integers. They are compared by `ReferenceDataIndex.CodeKey`, so `1` and `"1"` are the same code.
2. **A literal, not a row id.** It reads like every other place the model holds a code (a default, `allowedValues`), and it works where there are no rows.
3. **One code per state, for every subject.** A lifecycle stores the same value for a state whatever type follows it. A type that needs another encoding gets its own lifecycle, or a pack maps the values.

**Subjects**

4. **A lifecycle lists what it governs.** `subjects` is a list of entries, each `{ "entity": <id> }`, optionally with a `boundAttribute`. The entry is an object so that custom kinds and tables can be added later (section 12) without another format change. An orchestration may list subjects too, for what its flow is about, but binds nothing.
5. **An entity can be governed by several lifecycles.** Nothing on the entity names them: the lifecycles say so. Two lifecycles binding the same attribute of one entity is MQ9202, until a type that follows one of several lifecycles is designed (section 12).
6. **The old keys are read and rewritten, not refused.** `subject` and `boundAttribute` on a process and `lifecycle` on an entity are read as one `subjects` entry, and MQ9209 asks for the new form. A save through the engine and `maquettiste format` rewrite them. The schema keeps them as deprecated keys, so every model that loads today still loads. See section 13, question 1.
7. **`set-lifecycle` keeps its meaning:** this entity's only lifecycle is the target. It adds the entity to the target's `subjects`, and removes it from every other lifecycle's. With no target, it removes the entity from all of them. The editor's Use control goes on working through it.

**Holding the state in an attribute**

8. **A subject may bind the attribute that holds its state.** It is a single-valued attribute of that subject (own, inherited or from a stereotype), typed by an enum, as today; a reference type; or a plain status (`string`, `text`, `int16`, `int32` or `int64`, directly or through a scalar type). Each subject binds its own attribute, since different types keep their status in different columns.
9. **An enum binding works as today**, matching root states by name (MQ9203, MQ9204).
10. **A code binding needs codes that the column can hold one at a time.** Every atomic state the lifecycle can reach must have exactly one state with a code among itself and its ancestors, and no state with a code may sit inside a parallel region. Root states with codes is the usual case.
11. **The codes must fit the attribute (MQ9207).** For a reference type, they must be live codes of the type, and a former code is reported with its replacement. For a plain status, they must fit its type, length, pattern and `allowedValues`.
12. **Values with no state are a warning (MQ9208).** This is one finding per binding, listing the values. The values are the attribute's `allowedValues` when it has them, else the type's rows in the model. Rows a script fills are not judged.

**From the check of the first draft**

13. **Binding is one write.** A code binding with a reachable state lacking a code is an error, and a new error refuses a save. So `boundAttribute` and the codes arrive together. A save that sends both works, and the batch operation `sync-codes` (decision 17) does it in one step.
14. **Unbinding is never refused because of codes.** Codes stay meaningful with no binding, so removing a binding, a subject or the bound attribute leaves valid codes behind.
15. **The index stays right when the attribute lives in another file.** A code under a reference binding is listed as a use of the row, field `code`. So `get_references` and `referenced_by` see it, deleting the row is refused, and renaming, merging or retiring a code rewrites it. A merge or retire that would leave two states one code is refused. Because the index reads the bound attribute's type, its patch path falls back to a full build when a changed document holds attributes and the model holds a code binding, or when a changed process holds one. A model with no code binding never takes that path.
16. **The validator's reach.** A save of a reference type, seed or scalar type revisits the lifecycles with a code binding, and only those.

**Tools**

17. **`sync-codes` proposes codes and binds them.** It is a batch operation: `{ "op": "sync-codes", "id": <process>, "entity": <subject>, "attribute": <attribute>, "codes": { <state name or id>: <code> } }`.
    - A state keeps a code it already has.
    - Otherwise, from the type's rows or the `allowedValues`, it matches a code equal to the state's name (ignoring case, `_` and `-`), then a row whose label equals the state's display name or name.
    - An ambiguous match counts as unmatched, and `codes` supplies what can't be matched.
    - It adds the subject if it is missing, sets the binding, and creates no rows.
    - A dry run lists what it would write and what is unmatched. An applied run with anything unmatched is refused (MQ9019) and writes nothing.
    - It is reached through `apply_batch` and `maquettiste process sync-codes`. `sync-enum` and its answer don't change.

**Guards that read the object**

18. **A condition may read `subject.x`.** In scope are the attributes every subject has, with the same name and type (own, inherited or from a stereotype), so a lifecycle over several types reads what they share. A bound attribute is left out, since the chart already knows its state.
    - A context attribute named `subject` keeps its meaning, and the subject is then not in scope.
    - Only conditions read the subject. The expression form (JavaScript) does not: packs write it as it is, and today a guard expression that reads `subject.x` saves and fails at run time.
    - Library guards hold expressions only, so they don't read the subject for now.
19. **Simulation is given the object.** A scenario's and a simulation's `start` gains `subject`: `{ "entity": <id>, "values": { <attribute id>: <value> } }`.
    - A value not given reads as the attribute's default, then null, as context does.
    - A bad value is a replay finding, so it doesn't refuse a save.
    - An invoked sub-process reads defaults only.
    - `sync-enum`'s check of literal uses counts these values. Renaming a code does not rewrite them, the same as scenario values today.
20. **Generated code may leave such a guard to a person.** A guard that `csharp-dapper` can't translate becomes a partial method, as today, with its condition in the comment. Today the generated scenario tests have no way past such a guard: its hand-written stub throws until someone writes it. This design makes them pass the scenario's `assume` for a guard the pack couldn't translate, which helps every stub guard, not only these.

## 5. Schema and files

`process.json`, with its copies:

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| `subjects` (after `use` in `x-order`) | list of `{ entity, boundAttribute? }` | `[]` | Decisions 4 and 8 |
| state `code` (after `type`) | string or integer | absent | Decision 1 |
| `subject`, `boundAttribute` | as today | absent | Deprecated; read as one subject (MQ9209) |

`entity.json`: `lifecycle` is deprecated in the same way. `scenario.json`: `start.subject`, as in decision 19. `batch.json`: `sync-codes`.

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

Here an invoice keeps its state in a column, and a credit note keeps it wherever its implementation chooses, such as the lifecycle's own tables.

## 6. Rules

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ9201 | E | Changed: a lifecycle with no subjects, a subject that isn't an entity, or one listed twice. | Add a subject |
| MQ9202 | E | Changed: a subject's bound attribute isn't a single-valued enum, reference type or plain status attribute of that subject; a binding on an orchestration; or two lifecycles binding one attribute. | Bind another attribute |
| MQ9203, MQ9204 | | Unchanged, per enum binding. | |
| MQ9205 | W | Changed: per binding, the attribute's default is not the initial state's name (enum) or code. | Set the default |
| MQ9206 | E | Two states share a code, codes mix strings and integers, or a code binding can't hold the state one value at a time (decision 10). | `sync-codes` |
| MQ9207 | E | A code doesn't fit its binding (decision 11). | Use a fitting code |
| MQ9208 | W | Values a binding can hold that no state covers (decision 12). | Add a state, or narrow `allowedValues` |
| MQ9209 | W | The old one-subject keys: write them as `subjects`. | `maquettiste format` |

MQ9019 gains `sync-codes`' refusals. MQ3102 covers an unknown `subject.x`.

## 7. The resolved model and templates

```csharp
public sealed class RProcess
{
    public RList<RSubject> Subjects { get; internal set; }     // in file order
    // Subject, BoundAttribute and BoundEnum stay: the first subject's, so packs that read them keep working
}
public sealed class RSubject
{
    public REntity Entity { get; internal set; } = null!;
    public RAttribute? BoundAttribute { get; internal set; }
    public REnum? BoundEnum { get; internal set; }
    public RReferenceType? BoundReferenceType { get; internal set; }
}
public sealed class RState { public object? Code { get; internal set; } }   // a string or a long, or null
public sealed class REntity { public RList<RProcess> Lifecycles { get; internal set; } }   // every lifecycle that lists it, by name
```

`entity.lifecycle` stays, and it is set only when exactly one lifecycle governs the entity. A pack that reads it today, such as `csharp-dapper`'s operation code, keeps working for those models. `process.bound_states` and `state.bound_member` follow the first enum binding.

The records follow, left out when empty or null: `ProcessRecord.Subjects`, `StateRecord.Code`, `EntityRecord.Lifecycles`. A reference binding's nodes also depend on the reference type's file. `entity.lifecycles` depends on `k:process` and the entity's referrers.

## 8. Tools

- **`get_references`, `referenced_by`:** a code under a reference binding shows as a use with field `code`.
- **`export_process`, `import_process`:** `code` goes in `meta.maquettiste`, and the importer's kept state keys gain it.
- **`simulate_process`, `record_scenario`, `verify_scenarios`:** `start.subject`.
- **`get_model_index`:** a lifecycle's row lists its subjects.
- **Tool and verb texts** describe subjects and codes.

## 9. Packs

Output changes only for a model that uses codes or more than one subject.

- **`csharp-dapper`:**
  - **Codes:** one constant per state that has a code, named by the state. A uuid code is a `static readonly Guid`.
  - **States to codes:** `ToCode` and `FromCode` between a configuration and its code.
  - **Enum bindings:** `ToStatus` keeps its form for the first one. Each further one gets `To<Entity>Status`.
  - **Moves:** a table of every move as (from code, to code, event).
  - **Step methods:** a partial class per lifecycle, with an empty partial method at each step of a move: requested, checked, signed and moved. There is also one `OnMove` method that receives the step and both codes, for a team that prefers one pipeline. The generated machine calls them. An empty partial method compiles away, so a team fills in only what it needs.
  - Nothing goes in `helpers.js`, since changing it renders every unit of the pack again.
- **`sql-ddl`:**
  - **Lifecycle tables (`processTables`):** these gain the subject's type and id and the current state's code, for a lifecycle whose states have codes. Without codes they are unchanged.
  - **`lifecycleChecks`:** a new parameter, off by default. For each code binding to a plain status that the entity's binding maps to one column of its write table, `seed.sql` reconciles a check `ck_<table>_<column>_states`, written the way the pack writes a reference type's check today, in every dialect. It isn't in the schema snapshot, so a migration doesn't see it.
- **`process-docs`:** lists subjects, bindings and codes. Uses of a library's member stay limited to the page's own process.
- **`samples/typescript-pack`:** the same codes and moves. An integer code is written unquoted.

## 10. Editor

Agents do most of this editing, and screens for it are on the later list. But the existing screens must keep working on the new form.

- **Use and Subject:** these edit the first subject, through `set-lifecycle`. Further subjects are edited in the JSON view.
- **New process dialog:** writes `subjects`.
- **Entity side:** quick fixes and the explorer read lifecycles from the processes, not from the entity.
- **Bound attribute picker:** lists enum, reference and plain attributes. `enumId` and the drift column stay enum-only.
- **States tab:** gains a **Code** column, which writes a number for an integer code.
- **MQ9205 quick fix:** writes the code for a code binding.
- **Mock backend:** learns the new rules, the subjects and the code uses (`mocks/model/validate.ts`, `refs.ts`, `store.ts`), and the recorded rule list follows.

## 11. A project that uses none of this, and upgrading

- **Old-form files keep working.** A model with `subject`, `boundAttribute` and `entity.lifecycle` loads and validates as before, plus one MQ9209 warning per lifecycle. `maquettiste format` rewrites them, and so does any save of those files through the engine.
- **The same output for everyone.** Resolution reads the old form as one subject, and `process.subject`, `bound_attribute`, `bound_enum` and `entity.lifecycle` are unchanged, so every pack writes the same bytes. The golden tests prove it.
- **No new work.** The index fallback and the validator's new reach run only for a model with a code binding.
- **Once, after upgrading:**
  - The schema copies change, so `validate` warns MQ1008 until they are refreshed.
  - The new schema hash forces one full re-read of the index cache.
  - Changed templates render once more and write the same bytes.
- **What the team using lifecycles would see:** an MQ9209 warning on each lifecycle until they run `maquettiste format`.

## 12. What comes next, as items of their own

Each needs its own design. These shapes are here so the format above leaves room for them. The capability list has the detail.

| Item | Shape | Why it's separate |
| --- | --- | --- |
| **Trigger points on every operation** | A grid of operations and kinds. The operations are the lifecycle's own (promote, demote, override, sign, reject, ignore, withdraw, enable, disable) and an object's (create, copy, delete, connect, disconnect, freeze, thaw, lock, unlock, revise, and changing an attribute, owner, type or lifecycle). The kinds are check, override and action. Each point names a handler, which may be a stub, with arguments. Today's guards and actions are two cells of that grid, and decision 20's step methods are where the lifecycle's points land in generated code | Object operations go well beyond lifecycles, and the list must stay open |
| **Richer sign-off** | Several named signatures per state, each with lists of who may approve, reject or ignore. A condition or routine decides whether each is needed and from whom. A signature chooses the next state, and can be withdrawn. Also routing to a person or role, signing again with a password, delegation, reminders and escalation | Overlaps approval stages (4.3) |
| **Rights per state, rules and freezing** | Rights per state for the owner, everyone and named roles. Rules that allow or forbid operations when a condition over the object and the user holds. Freezing any item, an object or a relation's instance, until it is thawed | These run in the team's system; the model states them for packs and implementers |
| **One of several lifecycles per object, and moving between them** | A type's objects follow one of several lifecycles, chosen by a rule or a person, and can move to another lifecycle, with a right and triggers. Also new versions of a lifecycle (4.5) | Decision 5 refuses this for now, so allowing it later only loosens a rule |
| **Relations with several types per end** | An end lists several entities, or all | Every reader of relations changes |
| **Revisions** | A revision sequence, the right to revise in each state, and what a relation does when an end is revised or copied | New to the model |
| **Lifecycles on custom kinds and tables** | `{ "custom": <type> }` and `{ "table": <id> }` as further `subjects` entries | The entry is an object for this reason |
| **Several instances of one lifecycle per object** | For now, a detail entity, which the guide will show. A key on the subject entry could come later if a team needs more | See section 13 |

## 13. For the owner to decide

Each has a recommendation, and the sections above follow it.

1. **The new `subjects` list in place of `subject`, `boundAttribute` and `entity.lifecycle`.** Recommended: yes, reading the old keys forever and writing the new form (decision 6). The other choice is to keep the old keys as the first subject and add a second key for the rest. That keeps two ways of saying the same thing for good.
2. **Several instances of one lifecycle on one object.** Recommended: a detail entity, as you suggested, shown in the guide. Nothing new in the format for now.
3. **The step methods in `csharp-dapper` (decision 20 and section 9).** Recommended: yes, as the first form of trigger points in generated code. They are pack output, not model format, so they can change.
4. **A check on a plain status column.** Recommended: the `sql-ddl` parameter, off by default. It adds nothing to the model's format.
5. **The order after 4.1.** Recommended: trigger points first, since they are what you described for generated code and lifecycle systems alike. Then richer sign-off, then rights, rules and freezing. Relations with several types per end can move up if you need them sooner.

## 14. Tests

- `ProcessRuleTests`: failing and passing cases for MQ9201 and MQ9202 as changed, MQ9205, and MQ9206 to MQ9209.
- `LifecycleSubjectTests` (new):
  - The old form loads and resolves the same as one subject, and `format` rewrites it with the same result.
  - One lifecycle over two entities resolves. Two lifecycles over one entity resolve, and `entity.lifecycles` and `entity.lifecycle` hold what decision 7 and section 7 say.
  - `set-lifecycle` with and without a target.
  - Two lifecycles binding one attribute is refused.
- `LifecycleCodeTests` (new):
  - Codes on root and nested states, with no binding.
  - Every binding type: enum, reference, plain string, plain integer, inherited, from a stereotype, and through a scalar type.
  - The partition rule of decision 10, including parallel regions.
  - Unbinding through each path leaves the model valid.
  - Reference refactors rewrite a state's code, and a refactor that would leave two states one code is refused.
  - **The index, by patch and by full build alike:** an attribute retyped in another file, and an attribute added then bound. `ModelIndexerPatchTests` covers a model with a code binding.
- `SyncCodesTests` (new): matching by code and by label, ambiguous matches, `codes`, binding in one step, no rows created, a refused run writes nothing.
- `ProcessConditionTests`:
  - A guard reads `subject.total`, including on two subjects that share the attribute.
  - A bound attribute is out of scope, and a context attribute named `subject` keeps its meaning.
  - `start.subject` values and defaults.
- **No change:** the old form and an enum-bound fixture give the same findings, output and files, apart from MQ9209. The pack golden tests pass untouched, and a seed save in a model with no code binding revalidates no process.
- **Pack tests:**
  - `csharp-dapper`'s codes, moves and step methods build with warnings as errors, for string, integer and uuid codes. `ToCode` and `FromCode` round trip, and the generated scenario tests pass.
  - The same in the TypeScript sample.
  - `sql-ddl`'s lifecycle tables with codes, and its check, run twice cleanly.
- **Acceptance:** one lifecycle governs invoices, bound to a status reference type, and credit notes, with no binding. Its codes reach the generated constants and moves, and a guard reads `subject.total > 0`.
- **By hand, as for shared guards:** a copy of the larger process fixture moved to codes and two subjects validates, its scenarios pass, the packs generate, and the generated C# builds and its tests pass.

Each stale-output test is checked to fail without its fix.

## 15. Tasks

One pull request per group, each stacked on the one before. The sizes are rough, and include tests and a review.

| Group | What | Size |
| --- | --- | --- |
| Codes | State codes and their rules (decisions 1 to 3) | A few hours |
| Subjects | `subjects`, reading and rewriting the old keys, `set-lifecycle`, the resolved model, the editor's existing screens and the mock | About a day |
| Bindings | Enum, reference and plain bindings per subject, `sync-codes`, the index fallback, the validator's reach, reference refactors | About half a day |
| Guards and simulation | `subject` in conditions, `start.subject`, the generated scenario tests | About half a day |
| Output | `csharp-dapper` codes, moves and step methods; `sql-ddl` lifecycle tables and its check; `process-docs`; the TypeScript sample; the sample app's copies | About half a day |
| Docs | User guide, processes guide (including the detail entity), releases, modeling skill, `next.md` | An hour or two |

That comes to about three days at this project's pace.

**Checklist for every group, from what 4.2 taught.** Anything that reads or writes a lifecycle, a state or a subject must be found and handled:
- The importer's kept keys and the exporter.
- Every pack, including `samples/`, and the sample app's committed copies (`node tools/build-model.mjs`).
- The mock backend and the recorded rule list.
- The schema copies, and `openapi.yaml` with its generated files.
- `RenderKit.WithSettings` and `ResolvedCoverageTests`.
- Dependency keys on anything read across files, and a template that reads only one list.
- `csharp-dapper`'s operation code, which reads `entity.lifecycle`.
