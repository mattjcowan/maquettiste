# Processes linked to operations: design

**Status (2026-11-11): built, unreleased. Designed, approved by the owner (section 20), built, reviewed on Fable and fixed.** It designs roadmap row 4.6, renamed from "Charts joined to operations" to **"Processes linked to operations"** (`next.md`). The owner trimmed it on 2026-11-10: Maquettiste models processes and runs none, so no pack generates a process runtime. The original notes are `tmp/scope-next-capabilities.md` section 4.6; their "Done when" (a generated endpoint and status change) is out. [Object effects](object-effects-design.md) and [revisions](revisions-design.md) give the format. Every code fact was checked against the code at 0.24.0 (6688026b). A file path without a project prefix is under `src/Maquettiste.Engine/`. The examples are invented. Each new name and format is held to the roadmap's test: it stays for good, it must help any team whether they use it or not, and it stays optional.

## 1. In short

Today an operation lists the events it `fires` (`Model/Operations.cs:70-72`), and a resolved event lists the operations that fire it (`REvent.FiredBy`, `Resolution/ResolvedProcesses.cs:393`, filled at `Resolution/ResolveRun.Operations.cs:63`). Nothing else joins the two. The only check on `fires` is MQ3030, a read operation that fires an event (`Validation/OperationRules.cs:91-98`). An action can't say it calls an operation, and a service step can't say which operation does its work. The published contract's `eventRecord` has no "fired by" (`Editor/ResolvedRecords.cs:911`). The editor edits `fires` as raw JSON (`src/editor/src/editors/operation/OperationEditor.tsx:1-3`).

| Layer | What changes |
| --- | --- |
| The model | An **action** may have a **`call`**: one operation it calls, and a typed value for any of its **`inputs`**, written in the expression language. A **service step** (an invoke of type `service`) may name its **`operation`**. `fires` keeps its shape; its meaning is now written down exactly (decision 1) |
| Checks | Five new rules: an operation and a process it is linked to are about different subjects (MQ3032); an operation fires an event whose payload its input can't supply (MQ3044); an event that no operation fires (MQ9024, off unless a project turns it on); a call or service step that can't be read (MQ9025); a call that leaves a required input empty (MQ9026). Input values are typed like a set's value (MQ3102, MQ3103) |
| Simulation | A call runs nothing. The trace records it, with the input values worked out. Nothing moves because of it |
| Resolved model and contract | Both directions: `eventRecord.firedBy`, `actionRecord.call`, `invokeRecord.operation`, `operationRecord.calledBy` and `operationRecord.invokedBy`. Templates read the same through `event.fired_by` (exists), `action.call`, `invoke.operation`, `operation.called_by` and `operation.invoked_by` |
| Editor | A **Fires** picker on the operation screen (events grouped by process). A **Fired by** column on the Events tab, and a **Fired by** line in a selected transition's side panel. A **Calls** column and dialog in the Actions grid, with an input grid beside the Sets pattern. An **Operation** column in the Invokes grid. New findings in Problems. The canvas is unchanged |
| Example packs | `process-docs` lists the operations on each arrow and step. `csharp-dapper` output does not change, even for a model that uses all of this |

**Each part is optional on its own.** A team can use the checks on `fires` without calls, calls without service-step links, and service-step links without either. No part reads another's keys (section 4).

**A project that uses none of this** keeps its files, output bytes, findings, keys and speed. Section 15 says how, and which tests pin it.

## 2. Scope

| | What | Section |
| --- | --- | --- |
| **This item** | The exact meaning of `fires`; checks MQ3032, MQ3044, MQ9024, MQ9025, MQ9026; `call` on actions with typed `inputs`; `operation` on service steps; trace of calls; both directions in the resolved model, the contract and the template context; the editor screens; `process-docs`; docs | 4 to 14 |
| **Later, if teams ask** | Scenarios that expect calls (`expect.calls`); a call to an operation on a linked object (a to-one path, like `in_state`'s `of`); calls in a library's actions; inputs on a service step; which outcome fires which event; a resolved map of which input supplies which payload attribute; a quick fix that adds input members for a payload | 16 |
| **Left to 4.4** | Due times, timers from model values, human tasks and their contracts, editable fields per state. 4.4 can give a service step `inputs` in the shape of a call's, and map its done and error transitions to outcomes and errors (section 16) | 16 |
| **Not in this item** | Any generated runtime. The engine running an operation, or an operation moving a process during simulation. Synthesized operations (create, update, delete) | 4 |

## 3. What a team can rely on, and what is theirs

Teams will inventory what Maquettiste can say, and some will build to match it. So this section says, for each link, what the model states and what it leaves alone. **Nothing below has a runtime meaning unless this section gives it one.**

| Link | The model states | Left to the team's system |
| --- | --- | --- |
| `fires` | When the operation's work is done, the team's system sends each listed event to a process instance. When the operation has a record-level subject that the event's lifecycle governs, that instance is the record's own | On which outcome an event is sent (or whether a failure sends none); the order when several are listed; whether before or after the data change; in which transaction; retries; which instance gets the event when the subjects don't line up, or for an orchestration |
| `call` | When the action runs, the team's system asks for the operation to be run, with the listed inputs set to the listed values. Each value is worked out from the object and the context as they were when the action started (as a set's value is). For a record-level operation whose subject the process governs, the record is the process's object | When the call happens relative to the move (before, during, after commit); whether the move waits for it; retries; what each outcome or error means for the process; inputs not listed; which record, when the subjects don't line up |
| Service step `operation` | The service step's work is done by running that operation | How the step starts it and with what inputs (until 4.4); how its outcomes and errors map to the step's done and error moves; timeouts |
| Checks | The five rules of section 8, with the severities given | Whether to raise, lower or turn off any of them (`validation.rules`), or suppress one finding |

**A loop is not a model error.** An action on the `Issue` move may call an operation that fires `Issue`. The model doesn't forbid it, and the simulator doesn't follow it, since a call runs nothing.

## 4. Decisions

Each decision gives its confidence: **high**, **medium** or **low**.

### What `fires` means

1. **`fires` keeps its shape. Its meaning is written down: the operation's work, when done, sends these events.** Confidence: high.
   - Section 3 says exactly what is stated and what is the team's. The user guide, the schema text (`schemas/v1/operation.json`) and the record's XML doc say the same.
   - **Edge cases, all allowed:** an operation with no subject fires any event (a nightly job). Several operations fire one event: all are listed in `firedBy`, in the resolved model's order of operations (`ResolveRun.Operations.cs:42-44`). One operation fires events of several processes, or several events of one process: all are listed, in file order, and the order carries no runtime meaning. An event of an orchestration may be fired like a lifecycle's. A repeated id in `fires` appears once in the resolved list, as today (`ResolveRun.Operations.cs:183-185`).
   - A read operation that fires an event stays MQ3030, unchanged.

### Checks on the operation and event link

2. **MQ3032, a warning: an operation and a process it is linked to can't be about the same objects.** Confidence: high for the rule; medium-high for the matching rule.
   - **When it runs:** the operation has a subject `S`, and the process has subjects. With no subject on either side there is nothing to compare, so no finding.
   - **The match:** the process governs `S` (`Processes/SubjectCoverage.cs:213`, `Governs`, which covers derived entities and stereotype entries), or it governs some entity derived from `S`. So an operation on `Part` may fire an event of the lifecycle of `PurchasedPart`; it applies to some of its records.
   - **One rule for three links.** It is reported on `fires` (the operation's file, at `/fires/<i>`), on a call (the process's file, at `/actions/<i>/call/operation`) and on a service step (the process's file, at the invoke's `operation`). For a call and a service step it runs only when the operation is record-level, since only then does it act on one record.
   - The level doesn't matter for `fires`: a collection operation such as "create invoice" may fire a lifecycle event of the record it makes.
   - **Why a warning:** a team may route an event through its own lookup. A warning tells most teams of a slip, and the rest can turn it off.
3. **MQ3044, a warning: an operation fires an event whose payload its input can't supply.** Confidence: medium-high.
   - **The matching rule, by name and type.** For each payload attribute of the event, an input member of the operation **with exactly the same name** (ordinal) supplies it when:
     - both are typed values whose types are the same: the same built-in keyword, or the same `ref` id (enum, value object, scalar or reference type);
     - or the member is a record key (`keyOf`) and the attribute's type is the type of that entity's one key attribute;
     - and both are collections, or neither is.
   - **The finding, per attribute:** a payload attribute that is required, with neither `default` nor `defaultExpression`, and no member of the same name; or a member of the same name that doesn't fit (another type, a collection on one side only, a file, or a record key of an entity with a composite key). An optional attribute with no member of its name gives no finding.
   - **Not counted:** a record-level operation's implicit record key. It says which instance gets the event; it is not payload. Length, precision, scale and validation are not compared.
   - **Why by name:** names are how a reader matches them by eye, how the editor's grids show them, and how templates will. A per-event mapping list would be a new format to keep in step for every event. It comes later only if teams ask (section 16).
   - **Why a warning:** a handler may fill an attribute itself (`issuedOn` is today's date). Teams that do so lower the rule, or suppress the one finding.
4. **MQ9024, information, off unless `validation.rules` gives it a severity: an event that no operation fires.** Confidence: high that it is opt-in.
   - **Which events:** an event of a lifecycle or orchestration that some event transition uses, that no action of the process `raises`, and that no operation fires. An internal event (one an action raises) is never expected to come from an operation.
   - **Reported** on the process's file, at `/events/<i>`.
   - **Opt-in, like MQ7205** (`Validation/LocalizationRules.cs:114`). Many events come from people, not commands, so by default it would nag every team. A team that wants "every move has a command" turns it on as a warning or an error.
   - **Speed:** while it is off, the validator neither runs it nor adds the peers it needs (section 8).

### Actions that call an operation

5. **An action may have one `call`: the operation, and a typed value for any of its inputs.** Confidence: high for the shape; medium-high for one call per action.
   - `"call": { "operation": <operation id>, "inputs": [ { "id", "member": <input member id>, "value": <expression> } ] }`. `operation` is required inside `call`. `inputs` defaults to empty.
   - **Beside `expression` and `sets`.** An action may have any mix of `expression` (context changes, JavaScript), `sets` (the object's data) and `call`. Each is optional. With none, it is a stub, as today.
   - **One call per action.** An action is already a named, reusable effect, and a move lists several actions. "Issue and notify" is two actions, each readable by name. A list of calls per action would make a pack choose an order the model never states.
   - **`isStub` keeps its meaning:** neither an expression nor sets (`Resolution/ResolvedProcesses.cs:443-444`). A call is something the team's system carries out, like a stub. So `csharp-dapper` treats a call-only action exactly as it treats a stub today, and its output can't change.
   - **The published wording (owner, 2026-11-10, from the external review).** Teams read `isStub` as part of the contract, so its description in the record's XML doc, `ResolvedProcesses.cs` and `docs/resolved-model.md` says: "Whether the action has neither an expression nor sets; the host implements it. A call is that work, recorded on `call`; a call-only action is still a stub."
   - **The alternative:** `calls` as a list beside `sets`. It is not chosen, for the reason above. It could be added later without changing a file, which `call` as a list now could not undo.
6. **An input's value is typed like a set's value, against the member's type.** Confidence: high.
   - The site is the existing `Set` kind (`Expressions/ExprType.cs:23-27`) with the member's type as its value type, built like `SetSite` (`Validation/ExpressionRules.cs:582-592`). So it reads what a set's value reads: the context, the object under `subject`, the payload of the events whose moves run the action, `now()`, `current_actor()` and `in_state`. An entry or exit action reads no payload (MQ3102, as for sets).
   - The value must fit (MQ3103). A record key member types as its entity's key attribute. Null fits any member, required or not, as for a set's value, and the input counts as given (no MQ9026).
   - **Not givable** (MQ9025): a file member and a collection member (the language has no list values), a record key of an entity with a composite key or no key, and a member with no type the expression language has values of (binary, float, a value object; `ExprSites.MemberType` gives none), including a record key whose key attribute has such a type. The team's system supplies those. A type or entity that doesn't resolve is MQ2001 or MQ2002, not MQ9025.
   - **Why the `Set` kind, not a new kind:** the reads and the typing are the same. A new kind would repeat six switch sites (object-effects decision 3) for no difference.
7. **Inputs not listed are the team's.** Confidence: high.
   - A required member with no `default` that the call doesn't list is MQ9026, a warning per member. Its message says the team's system must supply it. Members that aren't givable (decision 6) are skipped, since the call can't give them.
   - An optional member, or one with a default, may be left out with no finding. The model says nothing about its value: the default applies where the team's system applies defaults.
8. **Which record the call acts on.** Confidence: medium-high.
   - A record-level operation whose subject the process governs (decision 2's match) acts on the process's object.
   - Its subject is another entity (an `Invoice` process calling `notify` on `Customer`): MQ3032 warns, and the record is not stated by the model.
   - A collection-level operation, or one with no subject, acts on no one record. Its inputs say everything.
   - The process has no subjects (an orchestration): no finding, and the record is the team's.
   - **Kept out, perhaps later:** a to-one path from the subject (`subject.customer`), in the shape of `in_state`'s `of`. It would let the model say which customer. It adds a key, so it waits for a team to ask (section 16).
9. **A library's action may not have a call** (MQ9020 gains the case). Confidence: medium-high. A library's members name the using process's parts by name, while a call's values name context and attributes by id, which MQ9020 already refuses for sets. Allowing `operation` alone in a library is possible later.
10. **Outcomes and errors stay out.** Confidence: high. What an outcome or an error means for the process is the team's (section 3). A later item could let a move name the outcome it waits for, through 4.4's service steps.

### Service steps

11. **A service step may name its `operation`.** Confidence: high.
    - `"operation": <operation id>` on an invoke of type `service`. On a `process` or `human-task` invoke it is MQ9025. Human tasks belong to 4.4.
    - It has no inputs in this item. **The seam for 4.4:** 4.4 may add `inputs` to an invoke in exactly the shape of a call's (`{ id, member, value }`), typed the same way, and may map the step's `invoke-done` and `invoke-error` moves to outcomes and errors. Nothing here blocks either.
    - MQ3032 applies when the operation is record-level.
    - The simulator is unchanged: a service step is still completed from outside by a step of type `invoke` (`Model/Processes.cs:1059`).

### Simulation and scenarios

12. **A call runs nothing. The trace records it.** Confidence: high.
    - Each `ActionRun` gains `call`: the operation id and the input values worked out, by member id. It is left out when the action has no call.
    - The values are worked out like a set's (from the object and context as they were when the action started), with the same lowering. A value that fails to work out is recorded as null with a note. A call never stops a run, since it has no effect.
    - **No effect:** the operation's guards don't run, its events are not sent, no outcome is chosen, and the context and the object don't change. The trace note says "recorded, not run".
    - **The action's source** stays `stub` for an action with only a call (`Processes/StepTrace.cs:159-169`). The call is in its own member, so no existing trace value changes meaning.
    - **Scenarios:** no new expectation in this item. Replays give the same outcomes and keep their keys, since no expectation reads a call. `expect.calls` is later, if teams ask.
13. **A service step's operation shows in the simulation panel** beside the pending step ("Waiting for ChargeCard: runs chargeCard"). No trace change.

### Code generation and release

14. **The provided packs are examples.** Confidence: high. `process-docs` shows how a template reads these links (section 13). `csharp-dapper` reads none of them, so its output is byte-identical for every model. Templates read every member, so any pack can write a handler that sends the event with its payload, an API page that says which move a command makes, or a workflow step that calls the service.
15. **A minor release, and a team upgrades together.** Confidence: high, by the owner's 4.1 decision 6. An older engine refuses a file with `call` or an invoke's `operation`. `formatVersion` stays 1.

## 5. Capability inventory

The same rows join the user guide (section 14). "Reads it" names who acts on the capability: checks (C), simulator (S), contract records (R), template context (T), editor (E).

| Capability | Written in | What it means | Reads it | Left to the team |
| --- | --- | --- | --- | --- |
| An operation fires events | operation `fires` (exists) | Its work, when done, sends these events (section 3) | C (MQ3030, MQ3032, MQ3044), R `operationRecord.fires`, T `operation.fires`, E Fires picker | Which outcome, order, timing, transaction, retries, target instance when subjects don't line up |
| An event knows what fires it | worked out from `fires` | The operations that list the event | C (MQ9024), R `eventRecord.firedBy` (new), T `event.fired_by` (exists), E Fired by column and line | Nothing more |
| Subjects line up | worked out | The operation's subject is one the process governs, or a base of one | C (MQ3032) | Routing when they don't |
| Payload is supplied | worked out, by name and type | Each required payload attribute has an input of its name and type | C (MQ3044) | Filling attributes the handler works out itself |
| Every used event has a command | worked out | Opt-in check for projects that want it | C (MQ9024, off by default) | Turning it on |
| An action calls an operation | action `call.operation` (new) | When the action runs, the team's system asks for the operation | C (MQ3032, MQ9020, MQ9025), S (trace), R `actionRecord.call`, T `action.call`, E Calls column and dialog | When, waiting, retries, outcomes and errors |
| Input values for a call | action `call.inputs` (new) | Each listed input is set to its value, worked out when the action starts | C (MQ3102, MQ3103, MQ9025, MQ9026), S (values in the trace), R, T, E input grid | Inputs not listed; file and collection inputs |
| A service step names its operation | invoke `operation` (new) | The step's work is done by that operation | C (MQ3032, MQ9025), R `invokeRecord.operation`, T `invoke.operation`, E Operation column, simulation panel | Inputs (until 4.4), outcome and error mapping, timeouts |
| An operation knows its callers | worked out | The actions that call it and the service steps that name it | R `operationRecord.calledBy`, `invokedBy` (new), T `operation.called_by`, `invoked_by`, E References tab (automatic) | Nothing more |
| Process docs show the links | `process-docs` | Fired by on arrows and events; Calls on actions; Operation on steps | The pack | A team's own pack decides its own output |

## 6. Schema and files

| File | Key | Type | Notes |
| --- | --- | --- | --- |
| `process.json` | action `call` (after `sets`) | `{ operation, inputs }` | `operation`: an id, required. `inputs`: a list of `{ id, member, value }`, default empty. `member` is an operation member id. `value` is `common.json#/$defs/expr`; text is accepted on writes and stored as a tree (MQ3108), as for sets. Not in a library (MQ9020) |
| `process.json` | invoke `operation` (after `process`) | id | Only on `service` (MQ9025) |
| `operation.json` | `fires` | unchanged | Its description gains decision 1's meaning |

No schema default is added, so no file gains a key. Also updated: the sample app's schema copies, `docs/api/openapi.yaml` by hand (the process shapes, the trace's `ActionRun.call`, the preview request's `input` site, the five records), and the editor files generated from it.

**The invoice example.** An `Invoice` lifecycle: `Draft` → `Issued` → `Paid`. Its `Issue` event carries `dueDays` (`int32`, required) and `issuedOn` (`date`, required).

```json
{ "kind": "operation", "id": "…OP1", "name": "issue", "displayName": "Issue invoice",
  "subject": "<id of Invoice>", "effect": "change", "verb": "promote",
  "input": [ { "id": "…M1", "name": "dueDays", "type": "int32", "required": true } ],
  "outcomes": [ { "id": "…", "name": "issued" } ],
  "fires": [ "<id of InvoiceLifecycle.Issue>" ] }
```

This gives MQ3044 at `/fires/0`: "`Issue` needs `issuedOn` (date, required); operation `issue` has no input of that name." The team adds the input, or lowers the rule if its handler uses today's date.

An action in an `InvoiceRun` orchestration whose subject is `Invoice`:

```json
"actions": [
  { "id": "…A1", "name": "RequestIssue",
    "call": { "operation": "…OP1",
      "inputs": [ { "id": "…", "member": "…M1", "value": { "path": ["<id of context.termsDays>"] } } ] } }
]
```

A service step in the `Issued` state of a payment orchestration:

```json
"invoke": [ { "id": "…", "name": "ChargeCard", "type": "service", "operation": "<id of chargeCard>" } ]
```

## 7. The editor's words

Labels are plain: **Fires**, **Add event**, **Fired by**, **Calls**, **Operation**, **Inputs**, **Input**, **Type**, **Required**, **Value**, **Not given**. Tooltips say "Runs nothing in the simulator; recorded in the trace" on Calls, and "Done by running this operation" on a step's Operation. No vendor names.

## 8. Rules

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ3032 | W | An operation and a process it is linked to can't be about the same objects: the operation's subject is neither governed by the process nor a base of an entity it governs. Reported on `fires`, on a call and on a service step (for these two only when the operation is record-level); not when either side has no subject | None |
| MQ3044 | W | An operation fires an event whose payload its input can't supply: a required payload attribute without a default has no input member of its name, or a member of its name has another type or form | None |
| MQ9024 | I, off unless `validation.rules` gives it a severity | An event that some event transition uses, that no action raises and that no operation fires | None |
| MQ9025 | E | A call or service step that can't be read: an input names a member that isn't an input of the called operation, names one member twice, or names a member that isn't givable (decision 6: a file member, a collection member, a record key of an entity with a composite key or no key, a member with no type the expression language has values of); or `operation` on an invoke that isn't a service step | Remove the input, or the invoke's `operation` |
| MQ9026 | W | A call leaves a required input member without a default ungiven (members that aren't givable aside) | Add the input with an empty value box (editor only) |

**Free ids, checked:** MQ3032 is the id phase 2 left free for operations follow-ups (`next-phase-2-design.md:263`, `:4008`), and no code uses it. MQ3044 is the next free id in MQ30xx: MQ3034 to MQ3036 stay reserved for custom-kind follow-ups and MQ3037 to MQ3040 were planned for the dropped built-in requirements, so this design avoids them. MQ9024 to MQ9026 are unused anywhere; MQ9023 stays reserved (`next-phase-2-design.md:263`). Checked with a search of `src`, `docs`, `schemas`, `tests` and `packs` on 2026-11-10.

**Existing rules that gain a case.** No finding changes for a model that uses none of this.
- **MQ3102, MQ3103:** an input's value, as for a set's value.
- **MQ9020:** a library action with a `call`.
- **MQ2001, MQ2002, MQ2020, MQ2022:** a call's `operation` and `member`, and an invoke's `operation`, are references like any other. They need no code beyond the `[ElementRef]` attributes.

**Where they run.** MQ3032 (on `fires`) and MQ3044 go in `Validation/OperationRules.cs`, beside MQ3030. MQ3032 (on calls and steps), MQ9024, MQ9025 and MQ9026 go in `Validation/ProcessRules.cs`. Input value typing goes in `Validation/ExpressionRules.cs` beside `AddSets` (`:238`).

**Incremental validation.** These rules read across files, so the dependents (`Validation/ModelValidator.cs`, `ProcessPeers` at `:1328`) gain cases:
- A process file changes: the operations that fire its events are referrers of those events and come back already, when referrers are included. The builder confirms it with a stale test.
- An operation changes: the processes whose actions or invokes name it are its referrers. **When MQ9024 is on**, the processes whose events it fires, before and after the change, are added (the store's "former" pattern, as `FormerActorPeers`). While it is off, nothing is added.
- An entity changes (its base, its key attribute): its operations are referrers already; the processes it is a subject of come through today's subject peers. Beyond them (`OperationLinkPeers`), for each operation whose subject is the entity or an entity derived from it, or that has a record key member (`keyOf`) of one: the processes that call it or run it, and the operation itself when it fires. A base that changes above an operation's subject (MQ3032 on its `fires`), and a key attribute's type, a key that turns composite or goes, or a base a key comes from (MQ3044, MQ3103, MQ9025), reach files that reference only the derived entity, so referrers alone miss them. Stale tests pin both.

The mock backend's rule list and `validate.ts` follow.

## 9. Resolved model, template context and contract

```csharp
public sealed class RCall : RObject                     // Kind "call"; Id is "<action id>/call", synthetic, since a call is no element or sub-element
{
    public ROperation Operation { get; internal set; } = null!;
    public RList<RCallInput> Inputs { get; internal set; } = RList<RCallInput>.Empty;   // file order; one that doesn't resolve or type is left out
    public RAction? Action { get; internal set; }
}
public sealed class RCallInput : RObject                // Kind "call-input"
{
    public ROperationMember Member { get; internal set; } = null!;
    public RExpression Value { get; internal set; } = null!;   // typed against the member; its root carries the JavaScript, as a set's value does
    public RCall Call { get; internal set; } = null!;          // the call that holds it
}
// RAction gains Call (RCall?, null when none). IsStub is unchanged: neither an expression nor sets.
// RInvoke gains Operation (ROperation?, null when none).
// ROperation gains CalledBy (RList<RAction>) and InvokedBy (RList<RInvoke>), in Processes order then file order.
// REvent.FiredBy is unchanged in shape; its keys narrow (below).
```

**Template members:** `action.call` (`id`, `operation`, `inputs`, `action`), `input.member`, `input.value` (with `.text`), `input.call`, `invoke.operation`, `operation.called_by`, `operation.invoked_by`. `event.fired_by` exists. `get_template_context` lists them, since its member lists come from reflection.

**Dependency keys.**
- `action.call` keys on the process file and on the operation's file, whose name and members it prints. `invoke.operation` keys on the operation's file.
- `operation.called_by` and `operation.invoked_by` key on the operation's referrers (`r:<operation>`), as `entity.operations` keys on the entity's.
- **`event.fired_by` narrows.** Today it keys on `r:<event>` and `k:operation` (`ResolveRun.Operations.cs:51-58`). When the list is empty, it keys on `r:<event>` only: an operation that starts firing the event changes `r:<event>`, so `k:operation` adds nothing but renders when an unrelated operation is added. When the list is not empty, it keeps both. A stale test proves an operation that starts firing the event renders the page again. Confidence: medium-high; the builder checks it with that test before relying on it.

**Records** (`Editor/ResolvedRecords.cs`), each left out of the JSON when unset, so answers don't change for a project without them:

| Record | New field | Type | Meaning |
| --- | --- | --- | --- |
| `eventRecord` | `firedBy` | list of operation ids | The operations whose `fires` names the event, in the operations' order. Left out when none |
| `actionRecord` | `call` | `callRecord` | The operation the action calls and the inputs it gives. Left out when none |
| `callRecord` (new) | `operation`, `inputs` | id; list of `callInputRecord` | |
| `callInputRecord` (new) | `id`, `member`, `value` | id; operation member id; `expressionRecord` | The value: its text printed from current names, its type and its tree, as `setRecord.value` |
| `invokeRecord` | `operation` | operation id | The operation a service step runs. Left out when none |
| `operationRecord` | `calledBy`, `invokedBy` | lists of action ids and invoke ids | Left out when none |

`invokeRecord.type`'s XML doc also gets fixed: it says "`service` or `process`" (`ResolvedRecords.cs:957`) but `human-task` is a value too.

**Regenerating the contract.** After the record changes, one command writes `schemas/v1/resolved-model.json`, `docs/resolved-model.md` and their two copies in `samples/reference-app/.maquettiste/.schema/v1/` (the dev skill, `.claude/skills/maquettiste-dev/SKILL.md:43-48`):
`MAQUETTISTE_UPDATE_GOLDEN=1 dotnet test tests/Maquettiste.Engine.Tests -c Release --filter FullyQualifiedName~ResolvedContractTests.The_checked_in_schema_and_reference_match_the_record_types`. The "Version 0.25.0" commit runs it again after the version bump. `openapi.yaml`'s `x-engine-record` entries are edited by hand, and the Functions contract tests check them against real answers.

## 10. Simulation and scenarios

- **The interpreter** (`Processes/StatechartInterpreter.cs`) works out a call's input values where it works out an action's sets, from the same snapshot of the object and context, then records them in `ActionRun.Call`. Nothing else runs.
- **Lowering** (`Processes/ProcessExpressions.cs`): call inputs are lowered beside sets. The defaults row for the subject is written when a call input reads `subject`, as for a set (`:170`).
- **The trace** (`Processes/StepTrace.cs:264`): `ActionRun` gains `Call` (`{ operation, inputs }`, inputs by member id), left out when null. A value that fails gives null and a note in the trace's notes.
- **Replay** compares nothing new, so replay keys stay as they are (`Resolution/ResolveRun.Processes.cs`). `record_scenario` writes nothing new.
- **Sub-processes:** a call in an invoked sub-process is recorded the same way. A value that reads `subject` reads null there, as sets see no object (object-effects decision 4), with a note.

## 11. Tools and API

- **No new tool.** Tools take documents and answer with records.
- `get_schema process` and `get_schema operation` show the keys and texts. `get_resolved_model`, `get_template_context` and `model export --resolved` show the new members.
- `apply_batch`, `save_element` and `patch_element` take qualified names in `call.operation`, `member` and an invoke's `operation`, and `{ "text": … }` for a value, stored as a tree.
- `simulate_process` answers the trace's `call`.
- `preview_expression` (and `POST` preview in the API) gains the site `input`, with `member` naming the operation member, for a value not yet saved. A saved input previews by its id, as a saved set does (`ModelStore.ExpressionPreview.cs:87-89`).
- `get_references` lists a call's `operation` and `member` and an invoke's `operation`.
- **Deletes:** deleting an operation that a call names is refused, unless `remove-references` (removes the call) or `delete-dependents` (removes the call too). Deleting an input member that an input names: either choice removes the input. An invoke's `operation` is cleared by either choice.
- `export_process` and `import_process`: an action's `call` and an invoke's `operation` travel in `meta.maquettiste` (`Processes/XStateExporter.cs`), so a round trip keeps them.
- **Index:** no new row or member. The format stays `maquettiste-index/e14` (`Editor/ModelReads.cs:23`).
- **Tool texts:** `src/Maquettiste.Cli/Mcp/ProcessTools.cs`, `ModelTools.cs`, `Agent/tools.json`, the conventions resource (`src/Maquettiste.Cli/Mcp/McpServerSetup.cs`), the modeling skill (`skills/maquettiste-modeling/SKILL.md`) and `Agent/AssistantPrompt.cs` explain `call`, the step's `operation`, and section 3's line between the model and the team. As built:
  - **Changed:** `ProcessTools.cs` (`simulate_process` answers the trace's `call`; `export_process` carries an action's call and a step's operation under `meta.maquettiste`); `Agent/tools.json` (`preview_expression`'s `input` site and `member`, and a member the called operation doesn't have is bad-request); `ModelTools.cs` (`save_element`, `create_element` and `apply_batch` list a set's value and a call input's value among the places that take `{ "text": … }`); the modeling skill (the `call` shape, its checks, a step's `operation`, and that the team's system runs both).
  - **No change, and why:** the conventions resource in `McpServerSetup.cs` serves the embedded modeling skill as it is, so the skill's update reaches it with no code change. `AssistantPrompt.cs` embeds the same skill as its conventions and names no kind's keys in its own text, so it needs none either.

## 12. Editor

**The operation screen** (`editors/operation/OperationEditor.tsx`) gains a **Fires** section on its General tab.
- Each fired event shows as one row: "Invoice lifecycle › Issue", the event's payload in small text, a remove button (tooltip "Stop firing Issue"), and any MQ3032 or MQ3044 finding on its row.
- **Add event** opens a two-step picker. First a process, from the index (no documents read): processes that govern the operation's subject come first under "On Invoice", the rest under "Other processes", with a filter box. Then that process's events, from its one document. This keeps the picker from reading every process, since models are large.
- The JSON tab still shows `fires`, and edits there still work.

**The process editor.**
- **Events tab** (`editors/process/EventsTab.tsx`): a read-only **Fired by** column, after Used by. It lists operation names; a click opens the operation. The editor asks `GET /api/model/references/{id}` for each event while the tab is open (cached, one small call per event) and keeps the rows whose `field` is `fires`; names come from the index.
- **Transition side panel** (`inspector/ProcessSections.tsx`, `TransitionSection` at `:237`): for an event trigger, a **Fired by** line under Event, with the same links. Other triggers show nothing.
- **The canvas is unchanged.** No label, badge or icon is added.
- **Actions grid** (`editors/process/TransitionsTab.tsx`, `ExpressionGrid` at `:311`): a **Calls** column after Sets. The cell reads "issue(dueDays = context.termsDays)". Its pencil button (tooltip "Edit the call of RequestIssue") opens a **Calls** dialog built on `SetsDialog`'s pattern (`editors/process/SetsEditor.tsx`):
  - An **Operation** picker from the index, with operations on the process's subjects first, and **None**.
  - Once picked, an **Inputs** grid from that operation's one document: one row per input member, with **Input**, **Type**, **Required** and **Value**. The Value box is the shared `ValueBox` (`editors/ValueBox.tsx`), which shows the type, the printed form and any finding. Leaving a value empty means "not given"; a required one shows MQ9026's text in the row. File and collection members show "Supplied by your system" and can't be edited.
  - Apply writes the call as one change. A refusal keeps the dialog open, as the Sets dialog does.
- **Invokes grid** (`editors/process/StatesTab.tsx`, `InvokesGrid` at `:276`): an **Operation** column, locked unless the type is `service` (as Process is locked unless `process`, `:308`), with a link to the operation.
- **Simulation panel:** an action that called an operation shows "called issue (dueDays = 30), recorded, not run". A pending service step shows its operation.
- **Problems:** the new findings show with their quick fixes (MQ9025 removes the input or clears the step's operation; MQ9026 adds the input row in the dialog).

**Generated and mock files:** the editor's API types come from `openapi.yaml` by the fixed command. The mock backend follows: `mocks/model/validate.ts` (the five rules), `refs.ts` and `cascade.ts` (references and deletes), `stepper.ts` and `mocks/processHandlers.ts` (the trace's `call`), `expressionPreview.ts` (the `input` site), and the recorded rule list.

## 13. Packs

| Pack | Change |
| --- | --- |
| `process-docs` | `process-page.scriban`, and `_docs.scriban`, which gains a `call_cell` helper beside `sets_cell` that prints an action's call as one code span, "issue (dueDays = context.termsDays)". **Transitions** (`:114`): a **Fired by** column, when some event of the process is fired by an operation, listing `t.event.fired_by` names. **Events** (`:219`): a **Fired by** column under the same condition. **Actions** (`:348`): a **Calls** column when some action calls, printing "issue (dueDays = context.termsDays)". **Invokes** (`:362`): an **Operation** column when some step names one. The diagram is unchanged. Short comments in the template name the member each part reads |
| `csharp-dapper` | **No change.** It reads none of the new members, and a call-only action stays a stub (decision 5). Its operation contract's summary keeps naming the fired events (`operation-contracts.scriban:116`) |
| `sql-ddl`, `seed-data`, `samples/typescript-pack` | No change |

**Keys of the process page.** It reads `e.fired_by` for each event to decide on the columns, which adds `r:<event>` keys. In a project with no `fires`, those hashes change only when a scenario naming the event changes (the only other files that reference an event, besides the process itself, are scenarios and operations: `[ElementRef(IndexKinds = ["event"])]` at `Model/Operations.cs:71` and `Model/Processes.cs:282`, `:447`, `:1054`). The page already keys on every scenario of its process through `p.scenarios` (`r:<process>`, `ResolveRun.Processes.cs:1563`). So no page renders more often, and section 9's narrowing keeps `k:operation` out.

## 14. Docs and guides to update

- **User guide** (`docs/user-guide.md`):
  - "Fires and requires" (`:3695`): decision 1's meaning, and section 3's table for `fires`.
  - A new subsection after it, **"Operations and processes: what the model says, and what it leaves to you"**, holding section 5's capability rows as the user-facing inventory.
  - "The rules for operations" (`:3706`) gains MQ3032 and MQ3044. "The rules for processes" (`:3355`) gains MQ9024 to MQ9026 and the MQ9020 case.
  - "Actions that set the object's data" (`:2579`) gains a sibling, **"Actions that call an operation"**. The service step's `operation` joins the invokes text. "Generating code from operations" (`:3723`) names `called_by` and `invoked_by`.
- **Processes guide** (`docs/guides/processes.md`): a short section 17, "Link a move to the command that makes it", on the invoice example.
- **Engineering:** `docs/engineering/lifecycle-capabilities.md` part F rows "Automated steps that run a program" (add "and name the operation they run") and part C "Actions, in order" (add "and the operation an action calls"); `next.md` row 4.6 renamed with its status and the 3.8 row's "actions stay JavaScript until operations replace them (4.6)" corrected (actions stay; they may call an operation); `ProcessAction`'s comment "Its name is the name phase 4 gives the operation it becomes" (`Model/Processes.cs:425`) corrected the same way.
- `docs/releases.md`, the modeling skill, and `docs/resolved-model.md` (generated).

**A user-facing inventory of every capability doesn't exist today.** `lifecycle-capabilities.md` is an engineering yardstick, and `docs/resolved-model.md` lists fields, not capabilities. These rows go into the user guide now. If the owner wants one inventory page for all features, they move there unchanged (open question 7).

## 15. A project that uses none of this

- **Files:** nothing rewrites a file. No new key has a schema default. Saves, `format`, `format --check` and snapshot import behave as today.
- **Findings and messages:** the same. MQ3032 and MQ3044 need `fires`, a call or a step's operation, with subjects or a payload to compare. MQ9024 is off by default. MQ9025 and MQ9026 need a call or a step's operation. **Pinned by** the existing rule tests and a test that validates the shared billing fixture before and after.
  - **One honest exception:** a project that already uses `fires` gets MQ3032 or MQ3044 warnings where its links don't line up. That is the check doing its job; the release notes say so, and both can be turned off.
- **Output:** the same bytes. `csharp-dapper` reads none of it. `process-docs` adds columns only when a link exists. **Pinned by** the pack goldens, which pass untouched, and a no-change golden with operations that fire nothing.
- **Dependency keys:** the same for `csharp-dapper`. The process page gains `r:<event>` keys that, without `fires`, change only when a scenario the page already keys on changes (section 13). `event.fired_by` drops `k:operation` when empty. **Pinned by** a key test: adding an unrelated operation renders no process page.
- **Simulation and replay:** the same outcomes, traces and keys. `ActionRun.call` is left out when absent.
- **Tool answers about the model:** the same. New record fields are left out when unset. Answers that change for every project, as in earlier items: `list_validation_rules` (five rules), `get_schema` for processes and operations, `tools/list` texts, the conventions resource, and `get_template_context`.
- **Speed:** the same. MQ3044 runs per fired event, MQ3032 per link. MQ9024 and its peers run only when turned on. The bench and its baseline (`bench/baseline.json`) pin it.
- **Once, as on any upgrade:** every unit renders again with the same bytes, since the engine version is part of each unit's key; the `.schema` copies refresh (MQ1008 until then).

## 16. What comes next

Each item waits on purpose. Each adds an optional key later without changing a file.

| Item | Why it waits |
| --- | --- |
| `expect.calls` on a scenario step | A test of "this move calls that command with these values". Needs a compare rule and recording. No team has asked |
| A call on a linked object (`of`, a to-one path) | Says which customer `notify` acts on. One key, the same path rule as `in_state` |
| Calls in a library's actions | Needs input values that read by name, like a library guard's expression |
| A service step's `inputs`, and its done and error moves mapped to outcomes and errors | 4.4's ground: due times and human tasks shape the same object |
| Which outcome fires which event | `fires` on an outcome. Today a team says it in its handler |
| A resolved map from inputs to payload attributes | MQ3044's rule as data, for packs. A template can apply the rule today |
| A quick fix that adds the missing inputs | Needs a new batch operation |

## 17. The three questions

**How does the UI change?** Five small places change, and the canvas does not.
- The operation screen gets a Fires list with an Add event picker. You pick a process, then its event. You no longer type ids in JSON.
- The Events tab gets a Fired by column. A transition's side panel gets a Fired by line. Both link to the operation.
- The Actions grid gets a Calls column. Its button opens a dialog: pick an operation, then fill its inputs in a grid, like the Sets dialog.
- The Invokes grid gets an Operation column for service steps.
- New warnings show in Problems, on the row they are about.

**Will it be intuitive?** Mostly yes. Every change reuses a pattern people already know: a grid column, a dialog like Sets, a picker like the verb and process pickers. Labels are plain words, and icon buttons have tooltips.
- **The riskiest is the Calls dialog.** Sets and Calls sit side by side but behave differently. A set changes the object in the simulator. A call changes nothing there; it says what your system will ask for. A user may expect a call to move the process or fire its events. To prevent that, the column's tooltip and the dialog's header say "Runs nothing in the simulator; recorded in the trace", and the simulation panel prints "recorded, not run" after each call. The browser pass checks that a first-time user reads it that way.
- The two-step Fires picker is the second risk. It is needed because models are large, and it shows the processes on the operation's own subject first, which is almost always the right one.

**How does it help modeling and code generation?**
- **Modeling:** one place answers "which command makes this move?", in both directions. The checks catch the usual slips: a command on the wrong object, a payload the command can't fill, an action that forgets a required input. A team that wants every move to have a command can turn that on.
- **Code generation:** templates read both directions with typed values. A pack can write a command handler that sends the right event with its payload filled from the request; an API reference that says "this endpoint moves an invoice from Draft to Issued"; a workflow step that calls the right service with typed arguments; or test stubs that expect those calls. The model stays a description, so a team's own runtime, or a lifecycle system, can implement it in its own way.

## 18. Tests

- **`OperationLinkRuleTests` (new):** failing and passing cases for MQ3032 (each link; no subject on either side; a derived entity; a stereotype entry; a base of a governed entity), MQ3044 (missing required attribute; one with a default; type, collection, file and record key mismatches; an optional attribute; the implicit record key not counted), MQ9024 (off by default; on as a warning; an internal event; an unused event), MQ9025 (each case), MQ9026 (required, default, file and collection members), and the MQ9020 case. Today's models give the same findings.
- **Expression tests:** an input's value typed against typed, record key and nullable members; MQ3102 for payload in an entry action; text to tree and back; renames reprint the text.
- **Incremental validation:** stale tests for a process edit reaching the operations that fire its events, an operation edit reaching the processes that call it, and MQ9024's peers only when on. Each is checked to fail without its fix.
- **Simulation:** a call's values in the trace, worked out from before the action's sets apply; a failed value giving null and a note; nothing else changing; a call in a sub-process; the action's source staying `stub`.
- **Resolver and keys:** each member and record, left out when unset; `called_by` and `invoked_by` order; `event.fired_by` narrowing (an unrelated operation renders no process page; an operation that starts firing renders it); `export_process` then `import_process` keeps `call` and `operation`.
- **Contract:** `ResolvedContractTests` after the regenerate command; `ResolvedCoverageTests` covers the new members.
- **Packs:** a `process-docs` golden on the invoice example with all four columns; a no-change golden for both packs with operations that fire nothing; a `csharp-dapper` golden with calls and step operations that equals the golden without them.
- **Editor:** unit tests for the Fires picker's ordering, the Fired by filter by `field`, and the Calls dialog's conversions (given, empty, not givable). Browser tests: add a fired event and see MQ3044, open an operation from Fired by, add a call with a typed value and see its type, simulate and see "recorded, not run".
- **Acceptance**, on the invoice example: `issue` fires `Issue` and gets MQ3044 until `issuedOn` is added; `RequestIssue` calls `issue` with `dueDays = context.termsDays`, and a simulation records it; `SendReminder` calls `notify` on `Customer` and gets MQ3032; `ChargeCard` names `chargeCard`; with MQ9024 on as a warning, `Pay` is reported until an operation fires it.

## 19. Tasks, build plan and estimate

### Size

With tests, without pack expected files: **about 5,000 lines (4,500 to 5,800).**

| Group | Lines |
| --- | --- |
| Contract: schemas, `openapi.yaml`, rule texts | 250 |
| Model, loading, references, deletes, XState meta, with tests | 450 |
| Rules, input value sites, preview site, peers, with tests | 1,150 |
| Simulation and trace, with tests | 450 |
| Resolver, records, keys, tool texts, with tests | 650 |
| `process-docs` and the no-change goldens | 250 |
| Editor: Fires picker, Fired by, Calls dialog, Operation column, mock, tests | 1,500 |
| Docs | 350 |

### How it gets built

Lean, as the owner asked: one pull request, one Fable review, one browser pass. Each agent is capped at about 100 steps or 200k context. Opus builds the engine and the pack page. Sonnet builds the editor, the mock and the docs, and runs the browser pass. At most three agents run at once, in worktrees fast-forwarded to the branch `operations-linked`, merged by patch.

| Wave | Agent | Model | Steps | Starts from |
| --- | --- | --- | --- | --- |
| 1 | **Contract first** (schemas, all `openapi.yaml` changes, rule texts), then the model keys, references, deletes and XState meta | Opus | About 70 | `main` at 0.24.0 |
| 1 | Editor part 1: Fires picker, Fired by column and line, Operation column | Sonnet | About 70 | The contract commit |
| 2 | Rules, input value sites, the preview site, incremental peers | Opus | About 80 | Wave 1's engine agent |
| 2 | Resolver, records, keys, simulation trace, tool texts, `process-docs`, goldens, the contract regenerate command. **Owns the tool files** | Opus | About 90 | Wave 1's engine agent |
| 2 | Editor part 2: Calls dialog, simulation panel, mock rules and trace | Sonnet | About 80 | Editor part 1, in the first free slot |
| 3 | Review of the whole branch | Fable | About 60 | Every builder's finish |
| 3 | Docs; browser pass | Sonnet | About 40 each | Beside the review |
| 4 | Fixers by area; the "Version 0.25.0" commit; generated files; one push; the pull request; CI | Opus, Sonnet | About 30 each | The findings |

A design gap found while building stops the work, and the owner is asked.

### Estimate

**Calibration:** earlier items cost about 155 output tokens, 425 new input tokens and 40,000 cached re-reads per line built; cached re-reads follow about 70M before size plus about 21M per thousand lines. This item has no canvas work.

| | Wall time | Output | New input | Cached re-reads |
| --- | --- | --- | --- | --- |
| Wave 1 | About 0.7 hour | 0.18M | 0.5M | 40M |
| Wave 2 | About 1.1 hours | 0.4M | 1.0M | 75M |
| Wave 3 (Opus and Sonnet) | About 0.5 hour | 0.08M | 0.3M | 22M |
| Wave 4 | About 0.6 hour | 0.12M | 0.35M | 33M |
| **In all, Opus and Sonnet** | **About 2.9 hours (2.5 to 3.3)** | **About 0.78M** | **About 2.15M** | **About 170M (150M to 200M)** |
| The review on Fable | Within wave 3 | About 0.05M | About 0.15M | About 13M |
| **Everything** | | **About 0.83M** | **About 2.3M** | **About 183M** |

## 20. Open questions for the owner

**Answered (owner, 2026-11-10, with an external review agreeing): all seven as recommended below.** The names stand; MQ3044 is a warning; MQ9024 is off by default; `expect.calls` waits; a call on another object warns (MQ3032) and the record stays the team's; no calls in a library's actions (MQ9020); the capability rows go in the user guide's Operations section, and a single inventory page of every feature is a later item. Plus the `isStub` wording in decision 5.

1. **Confirm the new names before release.** `call` with `operation`, `inputs`, `member` and `value` on an action; `operation` on a service step; records `firedBy`, `call` (`callRecord`, `callInputRecord`), `invokeRecord.operation`, `calledBy`, `invokedBy`; template `action.call`, `invoke.operation`, `operation.called_by`, `operation.invoked_by`; the preview site `input`. **Recommendation: as listed.** `call` is singular because an action calls one operation; `calls` would suggest a list.
2. **MQ3044 (payload not supplied) as a warning, not information.** **Recommendation: warning.** It is usually a slip, and a handler that fills an attribute itself is one suppression away.
3. **MQ9024 (an event no operation fires) off by default.** **Recommendation: off, opt-in like MQ7205.** Many events come from people, not commands.
4. **Scenarios that expect calls (`expect.calls`).** **Recommendation: later, if a team asks.** The trace already shows each call.
5. **A call to an operation on another object.** **Recommendation: warn (MQ3032) and leave the record to the team now.** Add a to-one path (`of`) only when a team asks.
6. **Calls in a library's actions.** **Recommendation: refuse now (MQ9020).** Allow them later with name-based values, if asked.
7. **Where the capability rows live for users.** No user-facing inventory exists. **Recommendation: put them in the user guide's Operations section now.** If you want one inventory page for every feature, that is a separate decision, and these rows move there unchanged.
