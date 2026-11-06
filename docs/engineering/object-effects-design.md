# Setting the object's data, and reading another lifecycle's state: design

**Status (2026-11-06): built for 0.14.0 (#52).** It designs the fourth item in the owner's order after 4.1: a move that sets the object's data, and a condition that reads the object's state in another lifecycle. [Who, what and when](lifecycle-actors-scenarios.md) section 6 sets its direction, and calls both prerequisites for rights. [What lifecycle systems can do](lifecycle-capabilities.md) is the yardstick. [The verbs design](verbs-design.md) and [the actors design](actors-design.md) give its shape. A file path without a project prefix is under `src/Maquettiste.Engine/`. The examples are invented. Each new name and format is held to the roadmap's test: it stays for good, it must help any team whether they use it or not, and it stays optional.

## 1. In short

Today nothing can set the object's data. A process's actions change its context, and an operation's outcomes change nothing. So nothing can record who submitted a request, which "the approver is not the submitter" needs. Nothing can read an object's state in another lifecycle either, unless that state happens to sit in a column. Rights need both.

| Layer | What changes |
| --- | --- |
| The model | An **action** may list **`sets`**: an attribute of the object and a value in the expression language, such as `submittedBy` set to `current_actor()` and `submittedOn` set to `now()`. Where the action runs says when: on a move, on entering a state or on leaving it. The expression language gains **`current_actor()`**, the identity of whoever acts, and **`in_state(object, state, …)`**, which asks whether an object is in a state of any lifecycle, however the state is stored. The object is the subject or a to-one link from it. A scenario object may give its **`states`** in other lifecycles, and a step may expect the **`values`** it changed |
| Simulation and replay | Sets run. The scenario object's values change, and later guards and sets see the new values. `in_state` reads the running chart for the process's own states, and a scenario object's states for other lifecycles. A step's `expect.values` and a trace's sets are keyed by attribute id; resolved records use names |
| Code generation | Templates read each action's sets and the new expression nodes. `csharp-dapper` writes one method per action that sets data, for the team to fill in (an action with only sets stays the team's stub, like any action the pack can't translate, so an existing team file still builds), and leaves a guard that uses the new nodes to a person, as today for any guard it can't translate. `process-docs` adds a Sets column and shows the scenario objects' states and expected values. Each happens only when the model uses them |
| The team's system, or a lifecycle system | Nothing changes until the team uses the new output or builds from the model |

**A project that uses none of this** keeps its files, output, findings and tool answers. Section 11 lists everything.

## 2. Scope

| | What | Section |
| --- | --- | --- |
| **This item** | `sets` on actions; `current_actor()` and `in_state` in guards and sets; the states of scenario objects; expected values on steps; rules; simulation and replay; the resolved model, packs, tools and the editor | 3 to 10 |
| **Kept out, perhaps later** | `sets` on an operation's outcomes (the direction covers moves and actions only; a trigger point may let an outcome name an action); setting a linked object's data or a link; setting context values in the expression language; reading through to-many links ("every affected item is Released"); a library action that sets data; a scenario that starts part way through its own lifecycle | 12 |
| **Left to trigger points** | An action that moves another object in its lifecycle, including by setting the attribute that holds its state | 12 |
| **Left to rights** | Conditions on rights, which read the same things as guards | 12 |
| **Left to the generated runtime (4.6)** | Generated code that applies sets and reads other lifecycles itself | 12 |

## 3. Decisions

Each decision gives its confidence: **high**, **medium** or **low**.

### Setting the object's data

1. **An action may list `sets`. Each set names one attribute of the object and a value in the expression language.** Confidence: high.
   - `{ "id", "attribute": <attribute id>, "value": <expression> }`. The value is a tree, and text is accepted on writes and stored as a tree, as for every expression (MQ3108).
   - **Why on the action:** actions already run in three places: on a move (a transition's `actions`), on entering a state (`entry`) and on leaving it (`exit`). So the question "when" needs no new key. An action is also already named and reused, packs already write one method per action, and a trigger's "action" kind will name actions.
   - **The alternative:** `sets` on transitions and on states' entry and exit. That repeats one shape in three places and skips the place packs already handle. It is not chosen.
   - An action may have an `expression` (context changes, in JavaScript), `sets`, both, or neither. An action with neither is a stub, as today.
2. **A set's attribute is one every subject of the process holds under one id.** Confidence: high.
   - The same scope as a guard's `subject.x` (4.1 decision 20, stereotype sets decision 7): own and inherited attributes for one subject, a shared base entity's for several, and a stereotype's attributes when a subject names a stereotype.
   - A process with no subjects has nothing to set (MQ9801).
   - **Not settable** (MQ9802): a derived attribute, which is worked out from others, and an attribute that holds a lifecycle's state. The process's own bound attribute belongs to the chart. Another lifecycle's bound attribute would move that lifecycle silently, which is the trigger-points item's job.
   - One action sets an attribute once (MQ9803).
   - **Kept out, perhaps later:** a member of a value object (`amount.value`), a collection, a to-one end (setting a link), and an attribute of a linked object. Each would widen what `attribute` accepts and change no file.
3. **A set's value reads what a guard reads.** Confidence: high.
   - It reads the context, the object under `subject` (to-one paths too), `now()`, `current_actor()` and `in_state`.
   - It reads the payload of the events whose transitions list the action, as a guard reads the payload of the events whose transitions use it. An entry or exit action reads no payload, since the event that enters or leaves a state isn't fixed (MQ3102, as today for a payload some event lacks).
   - It must fit the attribute's type, as a derived value must (MQ3103). A value that may be null is allowed: setting null clears the attribute.
   - **The site kind:** a new `Set` site kind, not the `Value` kind with scope flags. A `Value` site is a derived value or a default, which must hold for any row and can't read a lifecycle or an actor, so reusing it with flags would let those rules leak into set sites. The `Set` kind is handled explicitly where a default would mis-treat it: `ExprJs.cs:23` (the JavaScript entry), `ExprTyper.cs:63` (the site rules), `ExprScope.cs:195-196` (what a site may read), `ExprSql.cs` (refuses), `BuiltinHelpers.cs:719` (the helper's site switch) and `ModelStore.ExpressionPreview.cs:87-89` (previews). It types as a value site does and reads like a guard site.
4. **Sets run in simulation and replay. They are not only a statement.** Confidence: high.
   - "The approver is not the submitter" can only be proved if the run records the submitter. A modeling-only `sets` would leave every guard that reads it untested.
   - **Order:** actions run in the order the interpreter runs them today: exit actions, then the transition's actions, then entry actions, each list in its order. Within one action, every value is worked out from the object and the context as they were when the action started, then all are applied together. So `a` set to `b` and `b` set to `a` swap them. This matches `Record<Action>(call)` in the generated code, which gets the old context. The next action sees the new values. Guards that pick the move see the values from before it.
   - **Actors taken from the object read the run's current values** (actors decision 27). The replayer and `simulate_process` merge the subject's sets, by attribute id, into the objects passed to each step, so a later step's actor lookup sees what an earlier step set. Today both pass fixed objects (`ScenarioReplayer.cs:195`, `ProcessApi.cs:594`); each changes to pass the run's current objects. Confidence: high.
   - **An `in_state` on the running chart inside an entry or exit action's set** reads the states active at that moment: for an exit action, the state still active; for an entry action, the state just entered.
   - **An action's JavaScript `expression`** still reads only the context and the event. It doesn't read the object (4.1 decision 20).
   - **No objects in the scenario:** sets write into the object's row, which starts with each attribute's default, as guards read it today (4.1 decision 22). So a later guard still sees the value set.
   - **An invoked sub-process** sees no object (4.1 decision 22). Its sets change nothing in a run, and the trace says so.
   - **The trace** lists each action's sets and the values the step changed. **A scenario step** may expect them in `expect.values`, keyed by attribute id, as `expect.context` and an object's `values` are in files, compared as `expect.context` is: exactly the attributes the step changed (MQ9307). `record_scenario` writes `expect.values` only when a step changed one.
   - **Why `values`:** a scenario object already lists its `values`. When steps later name the object they act on (the direction, section 9), `expect.values` keeps its meaning: the values of the step's object, which today is always the scenario's subject.
5. **`current_actor()` is the identity of whoever acts.** Confidence: high that it is needed; medium-high for how it types.
   - In a run, it is the step's `signer` when given, else its participant's identity (or the participant's name when it has none, actors decision 14), else the name of the actor the step acts as, else null. An automatic move acts as the system actor when the project has one (actors decision 2), so `current_actor()` is its name. Otherwise it is null.
   - **It reads at guard sites too,** so "the approver is not the submitter" is one guard today: `subject.submittedBy <> current_actor()`. It reads at process guards, operation guards and sets, and nowhere else (MQ3104).
   - **How it types:**
     - When it meets a comparison, `in`, `coalesce`, a `case` branch or a set's attribute, it takes the other side's type, as a null literal fits any type. The typer records that type on the `actor` node.
     - Anywhere else it is a string.
     - **Lowering** reads the identity text through that type's reader (a number, a uuid, a string). When the text doesn't convert, the value is null, with a note in the trace. A team that stores people by number isn't blocked.
   - **The alternative:** a fixed string type. It would refuse a team whose people are known by a number or a uuid. It is not chosen.
   - **Why `current_actor()`:** the model already says "actor" for who may act. Action JavaScript's `event.actor` is the name of the actor element, not the person, and a step's `actor` also means the element. The owner chose `current_actor()` over the working name `actor()`, so the name itself says it is the person acting now (owner question 1).
6. **`now()` at a set** is the run's clock, as at a guard. Confidence: high. A date attribute takes `date_of(now())`.

### Reading another lifecycle's state

7. **`in_state(object, state, …)` is a predicate: true when the object is in any of the states named.** Confidence: high.
   - **A predicate, not a state value.** An object can be in several states of one lifecycle at once (parallel regions, and a state inside another). "Is it in Active?" has one answer either way, and "what is its state?" doesn't. Standards do the same: `In()` in the state machine XML standard, and `oclIsInState` in the object constraint language.
   - **The alternative:** `subject.<lifecycle>.state` compared with `=`. It needs a state type in the language, a new kind of path segment, and an equality that ignores nested and parallel states. It is not chosen.
   - **In the JSON tree:** `{ "inState": [<state id>, …], "of": [<relation end id>, …] }`. `of` is absent for the object itself. In text: `in_state(subject, Employment.Active)` and `in_state(subject.employee, Employment.Active, Employment.OnLeave)`. A state is named by its lifecycle, then its path: `Employment.Employed.Active`.
   - **One lifecycle per call.** The states listed belong to one lifecycle (MQ3116). "In Active in HR or Assigned in Projects" is two calls joined by `or`.
   - **A compound state counts when any state inside it is active,** as the chart says today.
8. **The object is the subject, or one reached from it through to-one ends.** Confidence: high.
   - The path follows the same rule as a guard's `subject.x` paths: to-one ends that every subject holds under one id.
   - **To-many is kept out.** "Every affected item is Released" needs a quantifier the language doesn't have. It comes later, with quantifiers, if teams ask. The capabilities row "conditions on related objects" becomes Part.
   - **The lifecycle must be able to govern the object** (MQ3116): the entity reached, a base of it or a stereotype it carries is one of the lifecycle's subjects, or one of its derived entities is. When it governs only some derived entities, an object of another type has no state there, and the predicate reads null.
   - **The process's own lifecycle is allowed.** On the subject, it reads the running chart. This is how one parallel region waits for another, which `In()` exists for in the state machine XML standard. On a linked object, it reads that object's given state, like any other lifecycle.
9. **Where it reads:** process guards, operation guards and sets. Not at checks, derived values or rule conditions (MQ3104), since those must hold however the object moves, and a database check can't see a lifecycle. Confidence: high.
10. **In a run, a linked object's state, or the subject's state in another lifecycle, comes from the scenario.** Confidence: high for the order; medium-high for the last step.
    1. The object's **`states`**: a list of state ids, at most one active configuration per lifecycle (MQ9305). A compound state enters its default states, as restoring from a code does in 4.1. A parallel state with no region named enters each region's initial state.
    2. Else, when the lifecycle binds an attribute of that object's entity and the object gives a value for it, the state that value maps to: the enum member's state, or the state with that code when the codes form a map (4.1 decisions 4 and 14).
    3. Else, **the lifecycle's initial state.** An object that has never moved is where a new object starts. When the lifecycle binds an attribute, the bound attribute's default is used before the initial state: a default that maps to a state gives that state. This matches 4.1's rule for values: a value not given reads as its default. The trace notes each state it assumed, so a reader sees it.
    - **A `states` that disagrees with a bound value is MQ9305.** When the object gives both, and the state in `states` isn't the one the value maps to, the scenario is refused.
    - **No object at the end of the path** (a link not given): `in_state` reads null, so a guard over it fails, as a path to a missing object does today.
    - **With no `of`, `in_state` on the process's own lifecycle reads the running chart** even when the process has no subjects, and in an invoked sub-process, as the standard `In()` does. Only a path through an object needs a subject.
    - **The subject's state in the process the scenario runs** comes from the run. Giving it in `states` is MQ9305. It may later mean "start here", which is a separate item.
    - **The alternative for step 3:** read null when no state is given. Then `not in_state(subject.employee, Employment.Left)` fails for every scenario that doesn't mention the employee's state, which surprises more than it protects. It is not chosen.
    - Other objects keep their states for the whole run. Steps that move a second object come with scenarios across objects.
11. **Code generation reads the predicate as a node, with what a pack needs to write it however the state is stored.** Confidence: high.
    - The node gives its lifecycle, its states, the path, and, when the reached entity's state is held in a bound attribute, that attribute, its enum, and the codes or enum members (not their names, as 4.1 decision 14 has it) of the states named and every state inside them.
    - So a pack writes `employee.Status in (…)` when the state is in a column, a call to a lifecycle system when it isn't, or leaves the guard to a person.
    - There is no SQL form. These nodes appear only at guard and set sites, which no database check holds, and `preview_expression` gives no SQL for them.

### Code generation

12. **Generated code leaves sets to the team for now, in one method per action.** Confidence: medium-high.
    - Generated code has no object to write to until the generated runtime (4.6), as 4.1 decided for guards that read the object (decision 21) and actors decided for actors from the object (decision 27).
    - `csharp-dapper` writes `Record<Action>(call)` for each action that sets data, a partial method the team's half implements, documented with each set in text form. The generated action calls it before returning the context. So a translated context change stays generated, and only the object part goes to the team. A new one fails the build until written, as a new stub does today.
    - **An existing team file still builds.** Translated actions are static today (`process-handlers.scriban:67`). Adding `Record` makes the actions that call it instance methods, so only an action with sets changes shape; the rest stay static. An action with only sets (no JavaScript `expression`) stays the team's stub, like any action the pack can't translate: the generated half declares it, and the dispatch calls `Run<Action>`, which calls `Record<Action>`, then the team's action. An action with an expression the pack can't translate works the same way. A team that has already written the action keeps it. The only method the team must add is a new `Record<Action>`, and the build names it.
    - **The alternative:** the pack works out each value it can translate and passes the values in. Values that read the object or another lifecycle can't be translated, so the method's shape would depend on what each value reads. It is not chosen now. With 4.6, the pack can write the assignments itself.
    - Templates read every set's attribute and typed value, so any pack with an object to write to can write assignments today.
13. **A guard that uses `current_actor()` or `in_state` is left to a person in `csharp-dapper`,** as any guard it can't translate. Confidence: high. Nothing changes in `helpers.js`, whose renderer already gives up on a node it doesn't know, so no unit of the pack renders again for it.
14. **Packs write the new output only when the model uses it.** Confidence: high.

### Release

15. **A minor release, and a team upgrades together.** Confidence: high, by the owner's 4.1 decision 6. An older engine refuses a file with `sets`, `actor`, `inState`, an object's `states` or a step's `expect.values`. `formatVersion` stays 1.

## 4. Schema and files

| File | Key | Type | Notes |
| --- | --- | --- | --- |
| `process.json` | action `sets` (after `expression`) | list of `{ id, attribute, value }` | Decisions 1 to 3. `attribute` is a keyed id, as a path's ids are. `value` is `common.json#/$defs/expr` |
| `common.json` | `$defs/expr` gains `actor` and `inState` with `of` | `actor`: `true`. `inState`: a non-empty unique id list. `of`: an id list, only with `inState` | Decisions 5, 7 and 8. Still exactly one form per node |
| `scenario.json` | object `states` (after `values`) | id list | Decision 10 |
| `scenario.json` | step `expect.values` (after `context`) | attribute id → value, as `context` and an object's `values` | Decision 4 |

Also updated: the sample app's schema copies, `openapi.yaml` by hand (the process and scenario shapes, the trace, the simulate and record requests, the records), and the editor files generated from it.

```json
"actions": [
  { "id": "…", "name": "RecordSubmission",
    "sets": [
      { "id": "…", "attribute": "<id of PurchaseRequest.submittedBy>", "value": { "currentActor": true } },
      { "id": "…", "attribute": "<id of PurchaseRequest.submittedOn>", "value": { "now": true } }
    ] }
],
"guards": [
  { "id": "…", "name": "NotTheSubmitter",
    "condition": { "op": "ne", "args": [ { "path": ["<id of submittedBy>"] }, { "currentActor": true } ] } }
]
```

```json
{ "id": "…", "name": "EmployeeIsActive",
  "condition": { "inState": ["<id of Employment.Active>", "<id of Employment.OnLeave>"], "of": ["<id of the end Assignment.employee>"] } }
```

```json
"objects": [
  { "id": "…", "name": "ann", "entity": "<id of Employee>", "states": ["<id of Employment.Active>"] },
  { "id": "…", "name": "a1", "entity": "<id of Assignment>", "links": [ { "end": "<id of Assignment.employee>", "object": "<id of ann>" } ] }
],
"steps": [
  { "id": "…", "event": "<id of Submit>", "participant": "<id of bob>",
    "expect": { "states": ["<id of Review>"], "values": { "<id of submittedBy>": "bob" } } }
]
```

## 5. Rules

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ9801 | E | A set's attribute isn't one every subject of the process holds under one id: an unknown id, another entity's attribute, a to-one end, a collection or a value object's member; or the process has no subjects; or a context attribute named `subject` hides the object | Remove the set |
| MQ9802 | E | A set's attribute is derived, or holds a lifecycle's state (a bound attribute of this or any lifecycle that governs the subject) | Remove the set |
| MQ9803 | E | One action sets one attribute twice | Remove the second |
| MQ3116 | E | An `in_state` names states of two lifecycles, a state that isn't a state (a history or choice state), or a lifecycle that can't govern the object reached; or its `of` isn't a to-one path every subject holds | Remove the call |
| MQ9307 | E | The values a scenario step changed differ from `expect.values` (keyed by attribute id) | Update the expectations from a replay (`refresh-scenario`) |

**Existing rules that gain a case.** No finding changes for a model that uses none of this.
- **MQ3103:** a set's value that doesn't fit its attribute, as for a derived value.
- **MQ3104:** `current_actor()` or `in_state` at a check, a value or a rule condition. `now()` becomes allowed at a set, as at a guard.
- **MQ9020:** a library action with `sets`. A library's members read the using process by name, and sets name attributes by id, as conditions do, which MQ9020 already refuses on library guards.
- **MQ9305:** an object's `states` naming a state of a lifecycle that can't govern it, two states of one lifecycle that can't be active together, the state of the process the scenario runs on its subject, or a state that disagrees with a bound attribute's value the object gives (decision 10).

The next free family is MQ98xx. The mock backend's rule list follows.

## 6. Resolved model and templates

```csharp
public sealed class RSet
{
    public string Id { get; internal set; } = "";
    public RAttribute Attribute { get; internal set; } = null!;   // the declaring attribute
    public RExpression Value { get; internal set; } = null!;     // typed; its root carries the JavaScript, as a guard's condition does
    public RAction? Action { get; internal set; }                // the action that holds it, or null
}
// RAction gains Sets (RList<RSet>, empty when none). IsStub is true only with neither an expression nor sets.
// RExpression: Node "actor"; Node "in-state" with Process (RProcess), States (RList<RState>), Through (RList<REnd>),
//   BoundAttribute (RAttribute?, the attribute the reached entity declares that binds the lifecycle), Enum (the enum of an enum binding),
//   and Codes (the codes, or the enum MEMBERS rather than their names, per 4.1 decision 14, of the states named and every state
//   inside them, when all have one; else empty).
// The actor node carries the type the typer recorded (decision 5).
// RExpression.JavaScript (ResolvedExpressions.cs:72) is null when a tree uses actor or in-state: the engine lowers these at start-up (section 7), so no pack sees their JavaScript.
// RScenarioObject gains States (RList<RState>, as given). RExpectation gains Values (attribute NAME to value, as Context and an object's Values are).
```

- **Templates read** `action.sets`, `set.attribute`, `set.value` (with `.text`), the `actor` and `in-state` nodes (`node.process`, `node.states`, `node.through`, `node.bound_attribute`, `node.enum`, `node.codes`), `object.states` and `step.expect.values`.
- **Dependency keys:** `action.sets` keys on the process file and on the entities that declare the attributes set, whose names it prints. An `in-state` node also keys on the lifecycle it reads, on the enum when the binding is an enum, and on the entity that declares the bound attribute. Only a template that reads them pays.
- **Records:** action records gain `sets`, scenario object records gain `states`, and step records gain `values`, each only where present. So `get_resolved_model` answers don't change for a project without them.

## 7. Simulation and replay

- **The object's row becomes the run's.** `StatechartInterpreter` holds the subject's row for the root instance and applies each action's sets to it. Today the row is read-only (`_subject`).
- **States in the row.** Each object in the row carries the states of the lifecycles a condition reads, under a member no attribute can be named, filled from decision 10. For the running process on the subject, the member is the current configuration, filled at each evaluation. The lowered JavaScript of `in_state` reads that member. A process with no `in_state` keeps today's row. The subject row (`ScenarioObjects.cs:32`) must exist for sets, even when a scenario has no objects, and `ProcessExpressions.cs:170` writes no defaults row unless a condition reads `subject`, so a set must count as a read too.
- **Where the helpers live.** The `actor` and `in_state` helpers go in the engine-only start-up code beside `mqAt` (`ExprLowering.cs:154`), not in `expr-runtime.js`, whose bytes `BuiltinHelpers.cs:194` writes into output. So no generated file changes for a project that doesn't use them. `RExpression.JavaScript` is null for a tree that uses these nodes, so packs never see engine-only calls.
- **`current_actor()`** is passed with each evaluation, from decision 5.
- **The trace:** each action entry gains `sets` (attribute id to new value), and each step gains `valuesChanged` (by attribute id too), both only when something was set. Resolved records (`step.expect.values`) use attribute names, as they do for the context. A trace also gains `notes`, left out when empty, which holds the "initial state assumed" note (`StepTrace.cs:123`) and the notes below. An action that has only sets has no JavaScript, so its action source (`StepTrace.cs:46`) is its sets in text form (`submittedBy = current_actor()`), not an empty expression. Notes say when a state was assumed (decision 10.3), when an identity didn't convert (decision 5), and when a sub-process's sets did nothing (decision 4).
- **Replay** compares `expect.values` (MQ9307). A scenario's replay also depends on the lifecycles its conditions read through `in_state` (their states, initial states, codes, bindings, and the enum of an enum binding), and on the attributes its process sets.
- **Replay keys** (`ResolveRun.Processes.cs`):
  - A process that reads `current_actor()` keys every step on the actor kind (`:980`), since the identity comes from the step's actor and participant.
  - The replay inputs (`:1158`) also count processes that set data, not only those whose conditions read the object.
  - The enum of an enum binding is added to the keys. So editing one of them replays it, and a scenario that uses none of this keeps today's keys.
- **Speed:** the row is copied only when an action with sets runs. States are worked out only for lifecycles a condition reads.

## 8. Tools

- **`simulate_process`, `record_scenario`, `verify_scenarios`:** objects take `states`. The trace gains `sets`, `valuesChanged` and `notes`. Each run passes the run's current objects to the actor lookup (decision 4). Recording writes `expect.values`.
- **`preview_expression`:** previews a set's value at its pointer, and text written for a set, given the action and the attribute. `current_actor()` and `in_state` preview at guard and set sites, with no SQL form.
- **`get_references`, `referenced_by`:** a set's attribute, an `in_state`'s states and ends, and an object's `states` are references.
- **Deletes:** deleting an attribute that an action sets is refused, unless `remove-references` (it removes the set) or `delete-dependents` (the same). A state or end read by `in_state` is treated as a path in a condition is today. An object's `states` entry goes with `remove-references`.
- **`export_process`, `import_process`:** an action's `sets` travel in its `meta.maquettiste`, so a round trip keeps them.
- **`apply_batch`, `save_element`:** accept `{ "text": … }` for a set's value and store a tree.
- **No new tool.** The rule list, the schemas, the conventions resource, the modeling skill and the tool texts describe all of this.

## 9. Packs

Output changes only for a model that uses sets, the new nodes, objects' states or expected values.

**`csharp-dapper`**
- **Handlers (`process-handlers.scriban`):** for each action with sets, a partial method `Record<Action>(call)` in the generated half, documented with each set in text form, such as `submittedBy = current_actor()`. The generated `Action<Name>` calls it, then returns the context it returns today. The team's half (`process-handlers.partial.scriban`) gains a stub for it in new projects. In an existing project, the build names the missing method, as for a new stub action today.
- **Guards** that use `current_actor()` or `in_state` become partial methods, with the condition in the comment (decision 13).
- **Nothing goes in `helpers.js` or `expr-runtime.js`,** since changing either renders every unit of the pack again (section 7).

**`process-docs`**
- **The process page** (`process-page.scriban:200`): the Actions table gains a Sets column when an action of the process sets data, such as "submittedBy ← current_actor()". An action that has only sets shows its sets in the expression cell, not an empty expression. Guard conditions already print their text, so `in_state(…)` and `current_actor()` show with no change.
- **The scenario page:** the objects table gains a States column when an object gives states. A step's expected values join its "then" line when given.

**`sql-ddl`, `seed-data` and the TypeScript sample:** no change.

## 10. Editor

**Process editor, Actions grid:**
- A **Sets** column summarizes each action's sets ("submittedBy ← current_actor(), submittedOn ← now()").
- Its cell opens a small editor: one row per set, with an **Attribute** picker (the attributes every subject holds, without the ones MQ9802 refuses) and a **Value** box. **Add set** and **Remove** buttons carry tooltips.
- **The Value box** takes text. On commit, it saves `{ "text": … }`, which the server stores as a tree. It shows the printed form, the type and any finding, through the preview endpoint. It is one shared component, written so the guard and check inspectors can use it later (the phase 3 follow-up); this item uses it only for sets.

**Scenario editor, Objects grid:** a **States** cell. It offers the states of each lifecycle that can govern the object's entity, grouped by lifecycle, one per region. For the subject, the process the scenario runs isn't offered.

**Scenario steps:** an **Expected values** cell, read-only, filled by "Update from replay".

**Simulation panel:** objects carry their states. After each step, a **Values** list shows what the step changed, and each action in the trace shows its sets. Assumed states show as notes.

**Quick fixes:** MQ9801, MQ9802 and MQ3116 remove the set or the call. MQ9803 removes the second set. MQ9307 updates the expectations from a replay.

**Generated and mock files:** the editor's API types come from `openapi.yaml` by the fixed command. The mock backend's `validate.ts`, `refs.ts` and `store.ts`, `processHandlers.ts` (simulation and recording), `mocks/model/cascade.ts`, and the recorded rule list follow the new keys and rules.

## 11. A project that uses none of this

- **Files:** nothing rewrites a file, so saves, `format`, `format --check` and snapshot import behave as today.
- **Findings:** the same. Every new rule needs `sets`, `actor`, `inState`, an object's `states` or `expect.values`. The four rules that gain a case give no new finding without them.
- **Output:** the same bytes. Every pack change depends on a set, a new node, an object's states or expected values. The new helpers live in engine-only start-up code, not in `expr-runtime.js`, and `RExpression.JavaScript` is null only for a tree that uses the new nodes.
- **Simulation and replay:** the same outcomes. The subject's row, guard rows and replay keys are what they were when no action sets data and no condition uses `in_state`.
- **Tool answers about the model:** the same. New record and trace members are left out when unused. The rule list, the schemas and the tool texts describe the additions.
- **Capabilities:** once built, "A move or action records data on the object" and "A condition reads the object's state in another lifecycle" become Yes, and "Conditions on related objects" becomes Part (to-one only).
- **Speed:** the same. Rows are copied and states worked out only where used.
- **Once, as on any upgrade:**
  - Every unit renders again and writes the same bytes, since the engine version is part of each unit's key.
  - The editor refreshes the `.schema` copies, or `validate` warns MQ1008 until refreshed.
  - The index cache is read in full once.
- **A team on mixed engine versions:** an older engine refuses the new keys (decision 15).

## 12. What comes next

| Item | What it takes from this one |
| --- | --- |
| Rights | A right's condition is an expression at a guard-like site, so it reads the object, `current_actor()` and `in_state` with no new syntax. "Forbid modify when the item is secret, unless the actor is its owner" is `subject.classification = 'secret' and subject.ownerLogin <> current_actor()`. A lifecycle that answers only in some states uses `in_state` for another lifecycle's. Combining across an object's lifecycles needs each one's state, which scenarios now give. Refusal reasons name the right, as they name a guard today |
| Separation of duties (richer sign-off) | "The approver is not the submitter" is a guard today: a move sets `submittedBy`, and the next move's guard reads it. Richer sign-off can name it as a rule over signatures |
| Trigger points | A trigger's action kind names an action, with its sets. "An action that moves another object" comes there: a target on a linked object, or a move sent to it. That is also when setting another lifecycle's bound attribute can become allowed. Generated step methods take `Record<Action>` into the pipeline |
| Scenarios across objects | Steps that act on any object, and `expect.values` on that object. Steps that move a second object change its states during the run |
| The generated runtime (4.6) | Generated code gets an object: the pack writes each set as an assignment where the value translates, and `in_state` as a read of the stored state |
| A scenario that starts part way through | The subject's `states` for its own process could become its starting state. It is MQ9305 until then |
| If teams ask | Setting a link or a linked object's data; a value object's members; context changes in the expression language; library actions that set data, by stereotype attributes; quantifiers over to-many links for "every affected item". Each adds an optional key or node and changes no file |

## 13. Decisions the owner must make

1. **Confirm the new names before release: `sets`, `current_actor()`, `in_state`, an object's `states`, and a step's `expect.values`.** Names are permanent once released.
   - `sets`, `in_state`, an object's `states` and a step's `expect.values`: confidence high. `in_state` is short and reads as the question it asks. `values` matches the scenario object's own list.
   - The working name `actor()`: confidence medium-high. The model's word for who may act is "actor", but action JavaScript's `event.actor` and a step's `actor` mean the actor element (the role), not the person. A reader may confuse the two.
   - **Decided (2026-11-06): the owner kept `sets`, `in_state`, `states` and `expect.values`, and renamed `actor()` to `current_actor()`.** Its stored node is `{ "currentActor": true }`, and the resolved node is `current-actor`.

## 14. Tests

- **`ObjectEffectsRuleTests` (new):** failing and passing cases for MQ9801 to MQ9803, MQ3116 and MQ9307, and the new cases of MQ3103, MQ3104, MQ9020 and MQ9305. Today's models give the same findings.
- **`ExprActorInStateTests` (new):**
  - Text to tree and back for `current_actor()` and `in_state`, with nested state paths and several states; renaming a state or a lifecycle reprints the text.
  - Typing: `current_actor()` beside a string, an integer and a uuid, and alone; `in_state` over the subject, a to-one path, a stereotype subject and the process's own lifecycle; refusals at checks, values and rule conditions.
  - The JavaScript lowering, and no SQL form.
- **`ObjectEffectsReplayTests` (new):**
  - Sets in order: exit, transition and entry actions; values from before the action within one action (the swap); a later guard sees a new value; no objects; an invoked sub-process sets nothing.
  - `current_actor()` from the signer, the participant's identity, its name, the acting actor and the system actor; an identity that doesn't convert.
  - `in_state` from `states`, from a bound value (enum and code), and from the initial state, with the trace note; a missing link reads null; parallel regions of the running chart.
  - `expect.values`: recording, MQ9307, and replay keys that change when a read lifecycle or a set attribute changes and stay the same otherwise.
- **`ResolvedObjectEffectsTests` (new):** `action.sets`, the `in-state` node's codes under an enum binding, a code binding and no binding, `object.states`, `step.expect.values`, and dependency keys.
- **No change:** a model without any of this gives the same output, files, `format --check`, findings, replay, traces and tool answers. The pack golden tests pass untouched.
- **Pack tests:** a new test case per pack. `csharp-dapper`'s `Record<Action>` and guard partials build with warnings as errors. `process-docs`' Sets column, objects' States column and expected values.
- **Editor:** unit tests for the attribute picker's filter and the states picker's grouping. Browser tests (in the browser pass) for adding a set with text and seeing its type, a set that doesn't type, giving an object's states, and updating expected values from a replay.
- **Acceptance**, on an invented model:
  - A purchase request lifecycle: `Submit` runs `RecordSubmission`, which sets `submittedBy` to `current_actor()` and `submittedOn` to `now()`. `Approve` is guarded by `subject.submittedBy <> current_actor()`. A scenario where Bob submits and Bob is refused approval, then Carol approves.
  - An employee in an `Employment` lifecycle, and an `Assignment` lifecycle whose `Start` move needs `in_state(subject.employee, Employment.Active)`. One scenario gives Ann as Active and passes. One gives no state and is refused, with the note that the initial state was assumed.
  - A `Training` lifecycle on the same employee, reading `Employment` on the subject itself.

Each stale-output test is checked to fail without its fix.

## 15. Tasks and the build plan

### Tasks

The sizes are days for a person who knows the code, with tests and a review. They show how the groups compare.

| Wave | Group | What | Size |
| --- | --- | --- | --- |
| 1 | Contract and language | The schemas, `openapi.yaml`, rule texts; `Expr` gains `Actor`, `InState` and `Of`; the `Set` site kind; text parse and print; typing, lowering; `ExprSites` for set sites; MQ9801 to MQ9803, MQ3116 and the four changed rules; references and deletes (these two go to a references agent, below) | Two days |
| 1 | Editor | The Sets editor and the Value box, the objects' States cell, expected values, the simulation panel, quick fixes, the mock | A day and a half |
| 2 | Simulation and scenarios | The interpreter's own row and sets; states in rows; `current_actor()`; trace members and notes; MQ9307; replay keys; simulate, record and verify; the exporter's meta | A day and a half |
| 2 | Resolved model and output | `RSet`, the new nodes in `RExpression`, records, tool texts; `csharp-dapper` and `process-docs` | A day |
| 3 | Docs | User guide (processes, expressions, scenarios), releases, modeling skill, capabilities rows, `next.md` | Half a day |

That comes to about six and a half days for a person.

**Checklist for every group.** Anything that reads or writes an action, an expression node, a scenario object or a step's expectation must be found and handled:
- **The expression language:** `Model/Expressions.cs`, `Expressions/ExprText.cs` (parse beside `now`, print), `Expressions/ExprTyper.cs` (site rules beside `AllowsNow`), `Expressions/ExprLowering.cs` and `ExprJs.cs`, `Expressions/ExprSql.cs` (refuse), `Expressions/ExprScope.cs`, the expression runtime's `mq` functions, and `Validation/ExpressionRules.cs` (`ExprSites.Of` for set sites, `ReadsSubject` and `SubjectRowDepth` counting `of` paths).
- **Processes:** `Model/Processes.cs` (`ProcessAction`, `ScenarioObject`, the step expectation), `Processes/ProcessExpressions.cs` (lowered sets beside lowered conditions, the row with states and `current_actor()`; `:146` skips lowering when no guard has a condition, so it must lower for sets too; `:170` writes no defaults row unless a condition reads `subject`, so sets must count), `Processes/StatechartInterpreter.cs` (the row for the root instance, now written), `Processes/ScenarioObjects.cs` (the subject row at `:32` must exist for sets), `Processes/ScenarioReplayer.cs`, `Processes/StepTrace.cs`, `Processes/ProcessLibraries.cs` and `Validation/ProcessRules.cs` (MQ9020), `Processes/XStateExporter.cs` and `XStateImporter.cs`.
- **Resolver and records:** `Resolution/ResolvedProcesses.cs`, `Resolution/ResolvedExpressions.cs`, `Editor/ResolvedRecords.cs`.
- **The `Set` site kind:** `ExprJs.cs:23`, `ExprTyper.cs:63`, `ExprScope.cs:195-196`, `ExprSql.cs`, `BuiltinHelpers.cs:719`, `ModelStore.ExpressionPreview.cs:87-89` (decision 3).
- **Contracts:** the schema copies, `openapi.yaml` and its generated files. `validation-rules.json` is written only by the fixed command.
- **Test harness:** `RenderKit.WithSettings` and `ResolvedCoverageTests`.

### How it gets built

**What earlier items cost.** 4.1 took about 3.75 hours, 1.71M output tokens, 4.67M new input and 436M cached re-reads, for about 11,000 lines. That is about 155 output tokens, 425 new and 40,000 cached per line. Verbs, about 5,000 lines, took about 2.5 hours. Engine builders cost about 20M cached per 100 steps.

**This item's size.** About 6,000 lines with tests: language and rules about 1,300 and their tests about 900; simulation and scenarios about 800 and their tests about 700; resolved model and packs about 600 with expected files; editor and mock about 1,400; docs about 350. That is about 1.2 times verbs. Unlike verbs, it changes the typer and the simulator, whose test loops are slower.

**Agents.** Each is capped at about 100 steps or 200k context. The language work is about 2,200 lines with the contract, which doesn't fit one agent at 100 steps, so references and deletes move to their own agent. The alternative is to let the language agent run about 130 steps. Opus 5.5 builds the engine and pack output and runs every review. Sonnet 5.5 builds the editor, the mock and the docs.

| Wave | Agent | Model | Steps | Starts from |
| --- | --- | --- | --- | --- |
| 1 | Language and rules; **contract first**: the schemas, all `openapi.yaml` changes including the trace and record shapes, rule texts. Keeps every change to `Validation/ExpressionRules.cs` | Opus | About 100 | `origin/verbs` |
| 1 | References and deletes: `get_references`, `referenced_by`, cascades and the exporter's meta. Owns `ProcessTools.cs`, which no other agent edits | Opus | About 50 | The contract commit |
| 1 | Editor and mock | Sonnet | About 100 | The contract commit |
| 2 | Simulation and scenarios | Opus | About 90 | The language agent's finish |
| 2 | Resolved model, records, tool texts, replay keys in `ResolveRun.Processes.cs`, and output for both packs | Opus | About 90 | The language agent's finish, beside the simulation agent |
| 3 | Review of the pull request | Opus | About 50 | All builders' finish |
| 3 | Docs | Sonnet | About 50 | The builders' finish, beside the review |
| 3 | Browser pass: the editor flows, on the built branch | Sonnet | About 40 | The builders' finish, beside the review |
| 4 | Fixers, split by area (engine and output; editor and mock; docs) | Opus or Sonnet | About 40 each | The review's and the browser pass's findings |

- **At most three agents run at once.** The two wave 2 agents touch different folders: the simulation agent works in `Processes/` and the process API, and the output agent in `Resolution/` (including the replay keys in `ResolveRun.Processes.cs`), `Editor/ResolvedRecords.cs` and the packs. The contract commit has already fixed the shapes they share.
- **One pull request, stacked on verbs,** with the contract, engine, editor, output, docs and fix commits. The owner merges verbs first, then this one.
- **Generated files** (the editor's API types, the recorded rule list, the sample app's copies) are written only by the fixed commands, run after each merge into the branch. All `openapi.yaml` changes are in the contract commit. Pack expected files stay with the builder, so their changes are reviewed.
- **Fixers** are fresh agents with the exact findings.
- **A design gap found while building** stops the work, and the owner is asked. Builders don't decide scope.

**Estimate.**

| | Wall time | Output | New input | Cached re-reads |
| --- | --- | --- | --- | --- |
| Wave 1 (contract, language, references, editor) | About 1.4 hours | 0.35M | 0.95M | 80M |
| Wave 2 (simulation, output) | About 1 hour | 0.25M | 0.7M | 55M |
| Wave 3 (review, docs, browser pass) | About 0.7 hours | 0.15M | 0.45M | 35M |
| Wave 4 (fixes and CI) | About 0.7 hours | 0.1M | 0.3M | 45M |
| In all | **About 3.5 to 4 hours** | **About 0.85M** | **About 2.4M** | **About 200 to 220M** |

The output and new-input figures follow 4.1's cost per line, a little under, since the language and simulator work reads more than it writes. The cached figure sits between two readings: by steps, about 120M for the agents (about 20M per 100 Opus steps and 15M per 100 Sonnet steps, fixers included) plus about 40M for coordinating one pull request, so about 170M with the extra agent; by 4.1's cost per line, about 240M. The wall time is verbs' 2.5 hours scaled by size, plus the slower test loops of the typer and the simulator.
