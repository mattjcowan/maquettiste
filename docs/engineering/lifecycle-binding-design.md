# A lifecycle bound to a reference type or a status column: design

**Status (2026-11-05): a draft for the owner. Nothing in this file is built.** It designs item 4.1 of `next.md`. Section 11 lists what the owner decides. A file path without a project
prefix is under `src/Maquettiste.Engine/`. Examples use the billing fixture. Each new name and format is held to the roadmap's test: it stays for good, it must help any team whether they
use it or not, and it stays optional. An independent check of the first draft against the code changed much of this version; section 12 keeps what it found about the parts left for later.

## 1. In short

Today a lifecycle can match its states to only one kind of status: an enum attribute, by name. A team whose status is a code from a reference type, or a plain text or number column, can't say
which stored value each state stands for. So the states repeat by hand in the chart, in code and in SQL, and nothing notices when they drift apart.

This design lets each root state say which stored value it stands for: `"code": "ISS"`.

| Layer | What changes |
| --- | --- |
| The model | A lifecycle may bind an attribute typed by a reference type or a plain string or integer. Each root state holds its `code`. Rules keep codes and states in step, and renaming a reference type's code follows into the state |
| Code generation | Templates read `state.code`. `csharp-dapper` writes the codes as constants and maps states to codes both ways. `process-docs` lists them. `sql-ddl` can write a check on a plain status column, behind a pack parameter that is off by default |
| The team's system | Nothing changes unless the team runs the new output. The team's database holds the column and enforces whatever it runs |

An enum binding works exactly as it does today.

**The first build is codes only.** Guards that read the subject, and several lifecycles on one entity, were part of the roadmap's scope. The check showed each costs more than it looked, no team has asked for either yet, and both can be
added later without taking anything back. Section 11 asks about them.

## 2. Today

- `Process.BoundAttribute` must be a single-valued, non-flags enum attribute of the subject (MQ9202, `Validation/ProcessRules.cs`, `CheckLifecycle`).
- The enum's members must equal the root states, by name and in order (MQ9203). `sync_enum_from_process`, the `sync-enum` batch operation and `maquettiste process sync-enum` write the members from the states.
- The bound attribute's default must be the initial state's name (MQ9205).
- `RState.BoundMember` is the enum member a root state stands for. `csharp-dapper`'s `process-states.scriban` writes `ToStatus` from it, only for an enum. `process-docs` lists the mapping.
- A new error refuses a save (`ModelStore.cs`, the loop over `report.Diagnostics`), except replay findings. So a process and what it depends on must become valid in one write.
- A reference type's code is a literal in an attribute's `default`, its `allowedValues` and seed cells. The index lists each as a use of the row (`Model/ModelIndexer.cs`, fields `default`, `allowedValues` and `values`), so deletes and `get_references` see them, and
  `ModelStore.ReferenceRefactors.cs` rewrites them when a code is renamed, merged or retired. Expression trees hold row ids, and scenario values are literals nothing rewrites.
- `sql-ddl` writes nothing from `allowedValues`. A column typed by a reference type gets its strategy's objects, which `seed.sql` reconciles on every run in every dialect and which are not in the schema snapshot (`packs/sql-ddl/_reference.scriban`). A column a binding maps to a
  reference-typed attribute, but which does not itself state the type, gets nothing.

## 3. Decisions

1. **What may be bound.** A single-valued attribute of the subject (own, inherited or from a stereotype) of one of three types: an enum, as today; a reference type; or a plain status, meaning `string`, `text`, `int16`, `int32` or `int64`, directly or through a scalar type.
2. **A "status column" is reached through its attribute.** The lifecycle binds the attribute, and the entity's binding maps the attribute to the column, as for every other attribute. `subject` stays an entity. A table with no entity gets one with `materialize-entities`. See section 11, question 1.
3. **The map lives on the state.** A root state gains an optional `code`: a string or an integer, in the same form as an attribute's `default`. A literal rather than a row id reads the same as every other place the model holds a code, and it works for a plain column, which has no rows. Two codes are compared by
   `ReferenceDataIndex.CodeKey`, so `1` and `"1"` are the same code. A string is refused where the type is an integer, as `CodeProblem` already does.
4. **Root states only.** Only the states an enum binding already binds take a code: the root's children that are not history or choice states. Allowing nested codes later would loosen a rule, so it stays open (section 11).
5. **An enum binding is untouched.** It keeps matching by name, and MQ9203 and MQ9204 stay enum rules.
6. **A code that does nothing is a warning, not an error (MQ9209).** That covers a `code` under an enum binding or no binding, and one on a nested, history or choice state. Because a new error refuses a save, an error here would refuse every way of unbinding: `set-lifecycle`, changing the subject, clearing the picker, `processCreate`, and
   deleting the bound attribute with `remove-references`. With a warning, none of those is refused, and binding again finds the codes still there. `state.code` reads as null in these cases.
7. **Binding is one write.** A bound state with no code is an error, so `boundAttribute` and the codes must arrive together. A `save_element` or batch `update` that sends both works. For the case where the codes can be matched, there is a new batch operation (decision 8).
   Changing an attribute's type from enum to a reference type has the same shape as MQ9202 has today: send the entity and the process in one batch.
8. **`sync-codes` proposes and binds.** `{ "op": "sync-codes", "id": <process>, "attribute": <attribute id>, "codes": { <state name or id>: <code> } }` sets `boundAttribute` and fills each bound state's `code` in one change. `attribute` is optional when the process already binds one, and `codes` gives what can't be matched.
   - A state keeps a code it has. Otherwise the operation looks for a code equal to the state's name, ignoring case, `_` and `-`, and then for a row whose label equals the state's display name or name. A plain status takes candidates from `allowedValues`.
   - Two rows that match one state, or two states that match one code, count as unmatched. A label is the row's own label, not a translation.
   - It creates no rows, since rows belong to seeds, which can live in CSV files.
   - A batch with `dryRun` lists `codes` and `unmatched` and writes nothing. Applied with any state unmatched, it is refused (MQ9019), and nothing is written.
   - It is reached through `apply_batch` and `maquettiste process sync-codes <process> [--attribute <name>] [--apply] [--check]`. No new MCP tool. `sync-enum` and `sync_enum_from_process` and their answers do not change. See section 11, question 3.
9. **Codes are checked against the attribute's type (MQ9207).** A reference type's code must be a live code of the type, and a former code is reported with the code that replaced it. A plain status's code must fit the attribute's type, length, pattern and `allowedValues`.
10. **Values with no state are a warning (MQ9208).** It is one finding per lifecycle, listing the values. A record holding one of them would be in no state. The values are the attribute's `allowedValues` when it has them, else the type's rows in the model. Rows a script fills are not judged, since the index does not hold them. A status type shared more widely can narrow
    with `allowedValues`, or the rule can be turned off.
11. **A renamed code follows.** The index lists a state's code as a use of the row, under field `code`. So `get_references` and `referenced_by` show it, deleting the row is refused while it is used, and rename, merge and retire rewrite it. If a merge or a retire would leave two states with one code, MQ9206 refuses the batch and says so. `remove-references` on a row clears the
    code, so it is refused the same way. A code in a plain status has no row, so nothing rewrites it.
12. **The index stays right when the attribute lives in another file.** To list a state's code, the index reads the bound attribute's type, which can sit in another document, and the index's patching path keeps that table when an attribute changes. So the patch falls back to a full build when a changed document holds attributes and the model holds any state code, or when a changed
    process holds a code. A model with no code never takes this path.
13. **The validator's reach.** A save of a reference type, seed or scalar type revisits the processes that hold a code, and only those. A project with none does no extra work.
14. **C# constants are named by the state, not the code.** A state's name is an identifier, and a code may be `1` or `in-review`. A uuid code is a `static readonly Guid`, since a Guid is no constant, as the pack's `reference-type.scriban` already says.

## 4. Schema and files

`process.json`, with its copies:

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| state `code` (after `type` in `x-order`) | string or integer | absent | The stored value of a root state |

`batch.json`, with its copy: the operation `sync-codes`, with `attribute` and `codes`.

```json
{
  "kind": "process",
  "name": "InvoiceLifecycle",
  "use": "lifecycle",
  "subject": "<id of Invoice>",
  "boundAttribute": "<id of Invoice.status, typed by the reference type InvoiceStatus>",
  "states": [
    { "id": "…", "name": "Draft", "code": "DRF" },
    { "id": "…", "name": "Issued", "code": "ISS" },
    { "id": "…", "name": "Paid", "code": "PD", "type": "final" }
  ]
}
```

## 5. Rules

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ9202 | E | Changed text: the bound attribute is not a single-valued enum, reference type or plain status attribute of the subject, or it is set on an orchestration. | Bind another attribute |
| MQ9205 | W | Changed text: the bound attribute's default is not the initial state's name, or its code for a code binding. Compared by `CodeKey`. | Set the default |
| MQ9206 | E | Under a code binding, a bound state has no code, or two bound states share one. | `sync-codes` |
| MQ9207 | E | A state's code does not fit: not a live code of the reference type (a former code names its replacement), or not of the plain attribute's type, length, pattern or allowed values. | Use a live code |
| MQ9208 | W | The status can hold values no state covers: rows of the reference type, or allowed values, listed once per lifecycle. | Add a state, or narrow `allowedValues` |
| MQ9209 | W | A `code` that does nothing: under an enum binding or no binding, or on a nested, history or choice state. | Remove it, or bind a reference type or plain status |

MQ9019 gains the `sync-codes` refusals. MQ9201 does not change.

## 6. The resolved model and templates

```csharp
public sealed class RProcess { public RReferenceType? BoundReferenceType { get; internal set; } }   // a reference binding; BoundEnum for an enum
public sealed class RState { public object? Code { get; internal set; } }                           // a string or a long; null where decision 6 says
```

`process.bound_states`, `process.bound_attribute` and `state.bound_member` do not change. A template that wants a row's label finds it through `process.bound_reference_type.rows`. A state has no row member, since building one here would force a type's scripted rows to load. The records follow, left out when null as `ResolvedRecords` already does for optional members:
`ProcessRecord.BoundReferenceType` and `StateRecord.Code`. Nodes of a lifecycle with a reference binding also depend on the reference type's file, so a code's change renders again.

## 7. Other tools

- **`export_process`, `import_process`.** `code` goes in `meta.maquettiste`, and the importer's list of kept state keys (`Descriptive`, used at `XStateImporter.cs` for a state) gains it, so an import over a process keeps it.
- **`get_references`, `referenced_by`.** A state's code shows as a use with field `code`.
- **`reference_type_usage`.** No change: it reads the resolved attributes, and the bound attribute is already listed.

## 8. Packs

Output changes only for a model that uses a code binding.

- **`csharp-dapper`, `process-states.scriban`.** For a code binding: one constant per state's code, named by the state (decision 14), `ToStatus` over the code's type, and `FromStatus` from a code to the state's path. An enum binding and no binding write what they write today. Nothing goes in `helpers.js`, since changing that file renders every unit of the pack again.
- **`process-docs`.** The bound-attribute section lists each state with its code and, for a reference type, its label.
- **`samples/typescript-pack`.** The same constants and mapping. An integer code is written unquoted.
- **`sql-ddl`.** A new parameter `lifecycleChecks`, default false. See section 11, question 2. When true, for each lifecycle bound to a plain status whose attribute the entity's binding maps to one column of its write table, `seed.sql` reconciles a check named `ck_<table>_<column>_states` that lists the codes. It is written the way the pack already writes a reference type's `check`: every dialect, triggers on SQLite, runs again safely, and not in the schema snapshot.
  A lifecycle bound to a reference type gets nothing new, since the type has its own strategies. Binding nothing, or an entity with no binding in that database, writes nothing.

## 9. Editor

Agents do most of this editing, and a generic screen is on the later list, but a code binding must not leave the existing screens in a bad state.

- The bound-attribute picker (`useBinding`) lists reference-typed and plain attributes as well as enums. `enumId` and `members` stay enum-only, or every state would show as drifted.
- The States tab shows a **Code** column for a code binding, so a state added on the chart can be given its code in the same draft. It writes a number when the type is an integer.
- The MQ9205 quick fix (`problems/quickFix.ts`) writes the state's code for a code binding.
- **Sync enum** stays for enums. `sync-codes` is for the CLI and agents until a screen follows.
- The mock backend learns MQ9206 and MQ9207, its MQ9205 reads codes (`mocks/model/validate.ts`), its references list the new use (`mocks/model/refs.ts`), and the recorded rule list follows.

## 10. A project that uses none of this

- A process with no `code` passes through every new line unchanged: no new finding, the same output, the same saved file. A test proves each.
- The index's full-build fallback and the validator's new reach only run for a model that holds a code (decisions 12 and 13).
- `sync-enum`'s answer does not change, and the new record members are left out when null.
- No existing resolved member changes type, so a pack keeps working with no change.
- **Once, after upgrading, with the same results:** the schema copies change, so `validate` warns MQ1008 until refreshed, and the new schema hash forces one full re-read of the index cache. Units of templates that changed render once more and write the same bytes.

## 11. For the owner to decide

Each has a recommendation, and the sections above follow it.

1. **A status column on a table with no entity.** Recommended: not in this version. `subject` is an entity across rules, templates and operations, so a table as subject touches much more, and `materialize-entities` gives a table an entity today. **Worth asking the team who asked for this: do the columns they have in mind sit on tables that have entities, and do they store a status for sub-states, below the root?** A yes to the second would make decision 4 the next thing to loosen.
2. **A check on a plain status column.** It answers the SQL half of the repeated literals, since today a plain column has no way to get one without writing the codes again. The first draft made this a permanent key on the binding, written by the engine. That turned out to be PostgreSQL only, unable to handle fixed-length columns, and it would have let deleting a listed process remove the entity's whole binding. Recommended: the `sql-ddl` parameter in section 8, off by default. It adds nothing to the model's format. The cost: the check is not in the schema snapshot, so a migration doesn't see it. `seed.sql` keeps it right instead, the same as today's reference type checks.
3. **`sync-codes` or a second form of `sync-enum`.** The roadmap's scope says `sync_enum_from_process` gains a second form. Recommended: a separate operation. The two run in opposite directions (`sync-enum` writes members from states, and this writes codes onto states), a second form would change every enum caller's answer, and only a separate operation can bind in one step.
4. **Guards that read the subject (`subject.total > 0`).** The roadmap's scope includes it. Recommended: hold it until the generated runtime (4.6), and leave it out of this build. No team has asked for it, and it is the only piece here that changes running behaviour. Today it would give a guard that simulation can run and generated code can't: it would become a hand-written stub that the generated scenario tests hit. Adding it later takes nothing back.
5. **Several lifecycles on one entity.** The roadmap's scope includes it. Recommended: hold it until a team asks. It looked cheap but needs answers for `set-lifecycle`, base and derived entities and the editor's Use control. Today this is an error, so allowing it later only loosens a rule.

## 12. What the check found about the parts left for later

Kept so whoever builds them doesn't have to find these again.

**Guards that read the subject.**
- Conditions only. The expression form breaks any pack that writes expressions as they are (the TypeScript sample's third argument is already `states`), a guard expression reading `subject.x` saves today and would silently change meaning, and the scope says "through 3.8".
- `ExprSites.Operation` is a model for the typing only. It adds navigations, and it reads own and base attributes, not a stereotype's.
- The bound attribute should stay out of the guard's scope: the chart already knows its state, which avoids asking what it reads on a root choice state.
- A context attribute named `subject` must keep its meaning.
- `start.subject` needs a home in `ScenarioStart`, the resolved start and its record, `sync-enum`'s use check and the generated scenario tests. A value not given should read as the attribute's default, then null, as context does. A bad one needs a replay finding, so it doesn't refuse a save. Decide what an invoked sub-process sees.
- `ProcessExpressions.Row`, `Evaluate` and `StatechartInterpreter.Start` take no subject today, and the resolver's readable list (`ResolveRun.Processes.cs`) needs it too.
- The TypeScript sample already writes empty parentheses for a condition guard, since it is not a stub. Any model with one hits this.

**Several lifecycles.**
- MQ9201's new text must be "an entity with lifecycles names one, and an unnamed one binds an attribute", or a sole enum-bound lifecycle its entity forgot to name stops being reported.
- `set-lifecycle` with no target unbinds every lifecycle of the entity, and with a target it takes over the named slot and unbinds the current one.
- Say what happens when a base and a derived entity bind the same inherited attribute.

## 13. Not doing

- A table as a lifecycle's subject (question 1).
- Codes on nested states (decision 4).
- Moving existing enum bindings to codes. Both stay.
- Different codes per database. A binding's field map and a reference type's storage already handle where a value lives.
- Naming a state in the expression language (`InvoiceLifecycle.Issued`). A reference type's code can already be named there (`InvoiceStatus.ISS`).
- Enforcing transitions in the team's database, or generating the status update. That is the generated runtime (4.6).
- Records in flight when a lifecycle changes (4.5), which reads the codes this design adds.

## 14. Tests

- `ProcessRuleTests`: failing and passing cases for MQ9206 to MQ9209 and the changed MQ9202 and MQ9205.
- `LifecycleCodeTests` (new):
  - Reference, plain string, plain text and plain integer bindings validate and resolve, including inherited and stereotype attributes and a scalar type. `1` and `"1"` are one code.
  - Unbinding through `set-lifecycle`, changing the subject and `remove-references` on the attribute all succeed and leave MQ9209, and binding again is clean.
  - A former code names its replacement. `state.code` and `process.bound_reference_type` reach templates. A state's code shows in `get_references` and `referenced_by`.
  - Deleting a used row is refused. Rename, merge and retire rewrite a state's code, and a merge or a retire that would leave two states one code is refused.
  - **The index, by patch and by full build alike:** an attribute retyped in another file, and an attribute added then bound. `ModelIndexerPatchTests` covers a model holding codes.
- `SyncCodesTests` (new): proposals by code and by label; ambiguous matches are unmatched; `codes` fills the rest; binds in one step on a process not yet bound; no row is created; a refused apply writes nothing; `--check`; export then import over keeps codes.
- **No change:** an enum-bound and an unbound fixture give the same findings, output and saved files; the pack golden tests pass untouched; a seed save in a model with no code revalidates no process.
- Pack tests: `csharp-dapper` output builds with warnings as errors for string, integer and uuid codes, and `ToStatus` and `FromStatus` round trip every state; the same in the TypeScript sample; `sql-ddl`'s check runs twice cleanly on each dialect the pack tests cover.
- **Acceptance:** the invoice lifecycle bound to a status reference type drives its codes, and renaming a code follows into the state and the generated constants.
- By hand, as for shared guards: a copy of the larger process fixture moved to a code binding validates, its scenarios pass, the packs generate, the generated C# builds and its tests pass.

Each stale-output test is checked to fail without its fix.

## 15. Tasks

One pull request per group, each stacked on the one before. The sizes are rough, and include tests and a review.

| Group | Tasks | Size |
| --- | --- | --- |
| Codes in the model | **L1** schema, record, index and its fallback, rules, the validator's reach, the catalog. **L2** `sync-codes`, the CLI verb, reference refactors, import and export | About half a day |
| Codes in the output | **L3** resolved model, records, templates, `csharp-dapper`, `process-docs`, the TypeScript sample, the sample app's copies | A few hours |
| Editor | **L4** picker, Code column, quick fix, mock | A few hours |
| The check, if accepted | **L5** `lifecycleChecks` in `sql-ddl` | An hour or two |
| Docs | **L6** user guide, processes guide, releases, modeling skill, `next.md` | An hour |

**What 4.2 taught, as a checklist for every group.** Anything that reads or writes a state must be found and handled: the importer's kept keys, the exporter, every pack including `samples/` and the sample app's committed copies (`node tools/build-model.mjs`), the mock backend, the recorded rule list, schema copies, `openapi.yaml` and its generated files, `RenderKit.WithSettings`, `ResolvedCoverageTests`, dependency keys on anything read across files, and a template that reads only one list.
