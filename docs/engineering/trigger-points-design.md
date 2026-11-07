# Trigger points: design

**Status (2026-11-07): designed, not built.** It designs the sixth item in the owner's order after 4.1: trigger points "on every verb, as check, override and action, with the generated step methods". It is built in two parts, each its own release (decision 36). [Who, what and when](lifecycle-actors-scenarios.md) sections 3, 5, 8 and 10 set its direction. [What lifecycle systems can do](lifecycle-capabilities.md), part D, is the yardstick. [The verbs design](verbs-design.md), [the object effects design](object-effects-design.md), [the rights design](rights-design.md) and [the actors design](actors-design.md) give its parts. A study of 20 systems (sources at the end) and the owner's evaluation shaped it. Section 3 tests that evaluation point by point. A file path without a project prefix is under `src/Maquettiste.Engine/`. The examples are invented. Each new name and format is held to the roadmap's test: it stays for good, it must help any team whether they use it or not, and it stays optional.

## 1. In short

Today only a move can carry logic. A guard says whether the move is offered. A transition's actions and a state's entry and exit actions run with it. Nothing runs when an object is created, modified, deleted, connected or frozen. Nothing can check a change and say why it is refused, warn without refusing, run after the change is saved, or replace a built-in effect such as deletion.

| Layer | What changes |
| --- | --- |
| The model | A lifecycle lists **trigger points**. Each says what runs when one of its **verbs** happens: a **check** (refuse with a message, or only **warn**), an **action** (**before** the checks, **with the change**, or **after commit**), or a **replace** of the verb's built-in effect, which only a few verbs allow. Optional keys say **where**: in some states, into some states, on some moves, for some types, for some attributes, or for links through some ends. A **`when`** condition and the body read the object as it will be, and its values before the verb through **`old.`**. A trigger point may be **required** (a bulk load never skips it) and may say it **touches outside systems**. In part 2, a **named trigger point** covers types with no lifecycle |
| Simulation and scenarios | Every verb runs in one fixed order: frozen, rights, guards, signatures, then the trigger points: before, checks, the effect or its replacement, with the change, after commit. A check that fails refuses the step and names itself. A step can expect warnings and the trigger points that ran. A check the team writes in code takes the outcome the step assumes |
| Code generation | Templates read each lifecycle's trigger points and the order they run in. `csharp-dapper` writes step methods per verb, one partial method per trigger point for the team, runs them inside the command's transaction, and hands after-commit work to the existing outbox. `process-docs` adds "What happens when" per verb |
| The team's system, or a lifecycle system | Nothing changes until the team uses the new output or builds from the model |

**A project that uses none of this** keeps its files, output bytes, findings, replays, tool answers, dependency keys and speed. Existing guards and actions work as they do today. Section 12 lists everything.

## 2. Scope

| | What | Section |
| --- | --- | --- |
| **Part 1 (release 1)** | Trigger points on lifecycles, for every verb a lifecycle answers for; checks with a severity; actions with a timing; replace on a short list of verbs; the narrowing keys; `when`; `old.`; `required` and `outside`; the fixed order; simulation, scenarios (`assume`, `expect.warnings`, `expect.triggers`), the trace; `get_trigger_sequence`; the resolved model, both packs and the editor | 4 to 11 |
| **Part 2 (release 2)** | Named trigger points with `subjects` and `excludes`: types with no lifecycle, and one trigger point reused by every type it covers | Decisions 33 and 34; sections 5 to 16 |
| **Later, if teams ask** | Queued work as its own timing, and compensation (decision 8); a trigger point that sends an event or does a verb on another object; batches of objects (decision 23); `set-state` and `override` as scenario steps | 13 |
| **Not in this item** | Who may bypass trigger points (the host's permission, decision 20); where data is stored, which bindings and packs decide (decision 17); enforcing `lock` | 13 |

**What part 1 leaves fixed, so part 2 adds only an element kind.** Part 1's keys, order, trace and generated step methods stay as they are. Part 2's named trigger point uses the same body and narrowing keys, and its points join the same order (decision 34). The generated per-type runner of part 1 (decision 30) takes part 2's points with no change to its shape.

## 3. The evaluation, tested

The owner's evaluation was tested against the study and the code. Most of it holds. Where it doesn't, the evidence is given.

| Point | Verdict | Evidence | Decision |
| --- | --- | --- | --- |
| Check, override and action is the right core: block before, react after | **Agree** | Every one of the 20 systems has a veto before and an action after (research matrix) | 1, 5 |
| A prepare moment before the checks | **Agree**, shaped as the action's **`before`** timing, not a fourth type | Dataverse: "If you want to change any values … you should do it here [PreOperation]" [S19]. Kubernetes runs mutating webhooks first, and "webhooks that need to … see the final state of the object … should use a validating admission webhook" [S37]. Salesforce, Rails and Dataverse register one kind of handler at several stages; they don't make a separate kind for preparing | 7 |
| Check severity: error or warning | **Agree** | Kubernetes: "Warnings can be sent with allowed or rejected admission responses" [S37]. No PLM system studied has one; ENOVIA's "soft block" blocks without an error and keeps the check program's changes [S2] | 6 |
| Action timing: with the change, or after commit | **Agree** | ENOVIA: actions run immediately or deferred until "the outermost transaction" commits [S7]. Aras in-scope and out-of-scope post-methods [S15]. Rails `after_save` and `after_commit` [S33] | 7 |
| Queued work as a third timing | **Disagree** for the model | An outbox writes the message in the transaction, and a relay publishes it later, "if and only if the database transaction commits", possibly "more than once" [S39]. That is the after-commit contract: can't undo the change, may run late, may run twice. Whether a host runs it on the request's thread or a queue can't be seen in the model | 8 |
| Compensation in the first release | **Later** | It needs steps that undo each other across objects (a saga [S40]). The model has no such steps yet, and handling a failed after-commit call is the running system's job (the direction, section 12) | 8 |
| A context contract per verb | **Agree**, and it needs one new thing in the expression language: `old.` | Dataverse images, Salesforce `Trigger.old`, SQL `OLD`/`NEW`, Kubernetes `oldObject` (research 3.2) | 13, 14 |
| Ordering | **Agree**. The list's order in the file is the order; no number | A list in a file is already explicit and has no ties. Camunda 8 runs listeners in model order [S25], and Jira runs post functions in their listed order [S23] | 19 |
| Inheritance through types and stereotype sets, with an opt-out | **Agree** on inheritance. The opt-out sits **on the trigger point**, not on the type | A lifecycle's subject entry already covers derived entities and a stereotype's carriers (stereotype sets). Narrowing in the trigger point keeps "where does this run" in one place, and never changes a type's file. JPA's opt-out sits on the subclass but drops every inherited listener at once [S36] | 12, 33 |
| A bulk-load bypass that needs a right and is logged | **Partly.** `required` is in the model. Who may bypass is the host's permission, not a right | A right speaks of a verb on an object in a state; a bypass is neither. Dataverse's bypass is a system privilege, and its own logic is never bypassed [S21]. Aras events with `is_required = 1` "cannot be ignored" [S14] | 20 |
| A recursion limit | **Agree** for generated code. The model removes the recursion instead | Dataverse: "Avoid applying changes to an entity included in the message because this will trigger a new Update event" [S19]. Salesforce runs flows "only one time per entity, per transaction" [S18] | 21 |
| A declared "touches outside systems" flag | **Agree** | Kubernetes `sideEffects` and `dryRun` [S37]. Simulation never runs the team's code, so the flag matters most for generated tests, dry runs and timing | 22 |
| Per object or per set | **Disagree** for the model: per object, and a pack may batch | Windchill splits a multi-object event for a single-object listener, and wraps a single one for a multi-object listener [S10]. So batching is a property of the runtime, not of the rule | 23 |
| Override should not be a general kind; a narrow replace | **Agree** | PostgreSQL allows `INSTEAD OF` only on views [S31]; SQL Server one per action [S32]; Dataverse's main stage is "for internal use only" [S19]; Aras replace events skip their own before and after events [S14]; ENOVIA's override skips "the normal system event code and history mechanism" [S7] | 16 |
| Replace on create, delete, revise, connect, disconnect and data held outside | **Partly.** Recommended: delete, disconnect, copy and revise | Numbering is a `before` action, as Dataverse and Salesforce do it; ENOVIA needed its separate `precreate` because it had no prepare [S4]. Dataverse's exception is for virtual table *data providers*, a whole data source: in Maquettiste that is bindings and packs. Connect has no removal to soften | 17, owner question 2 |
| Never on governance verbs | **Agree**, and the list grows: also `read`, `modify`, owner, type and lifecycle changes, lock and unlock | Each either changes who may act or is what history and rights rely on | 16 |
| The kind can't be named `override` | **Agree**: `replace` | The verb `override` already means "make a move although its conditions or signatures are not met" (verbs decision 1) | 16 |
| Reuse guards, rights, actions, `sets`, the refusal order and the trace | **Agree**, with one limit: a trigger point's body is inline, not a reference to a guard or an action | Guards and actions read the process's context and event. Most verbs have neither, and are also done outside a run. Rights decision 6 chose an inline condition for the same reason | 3, 4 |

## 4. Decisions

Each decision gives its confidence: **high**, **medium-high**, **medium** or **low**. A decision marked **(part 2)** is built in part 2. The others are part 1.

### Shape and home

1. **A trigger point says what runs when a verb happens at a place.** Confidence: high.
   - It has a `name` (an identifier, unique among the lifecycle's trigger points, MQ3001), an optional `displayName`, a `type`, at least one verb in `verbs`, optional narrowing keys (decision 10), an optional `when`, and a body (decision 3).
   - **The description says why.** A check's description is the message shown when it refuses or warns, as a right's description is shown with every refusal. No second text key. It is localizable.
   - Each entry of `triggers` is a part of kind **`process-trigger`**, as `process-right` is.
2. **In part 1, trigger points live on the lifecycle, in one list, `triggers`, after `rights`.** Confidence: medium-high.
   - **Why the lifecycle:** it already holds the moves, states and signatures, and the rights, whose narrowing keys trigger points reuse. Its subjects already name the types, with their derived entities and a stereotype's carriers. One editor tab and one file show what runs.
   - **Only lifecycles hold them** (MQ8101). An orchestration is a flow, not an object's state, and a library is shared parts. An orchestration's event that names an object, link or own verb on its subject still runs the subject's lifecycles' trigger points (decision 11).
   - **Types with no lifecycle** come in part 2, as named trigger points with `subjects` (decision 33). Rights left the same widening to a later item (rights decision 8).
   - **The alternative:** trigger points on entity and stereotype files. Every project's entity file and editor would change, and a move's points would still need the lifecycle. It is not chosen.
3. **The body is inline. A check has a `condition`. An action or a replace has `sets`. With neither, the body is the team's code.** Confidence: medium-high.
   - `condition` is an expression; only true passes. `sets` is the list object effects defined (`{ id, attribute, value }`), with the same rules (MQ9802, MQ9803).
   - **Why not name a guard or an action:** guards and actions read the process's context and the event. A modify done through an edit screen has neither, so a check that named a guard reading the context could not be run there. Rights decision 6 made the same choice for a right's condition.
   - **What is reused:** the expression language and its typing, the `ProcessSet` shape, the editor's value box and sets editor, and the generated pattern of one partial method per body.
   - A trigger point's actions don't change the process's context and don't raise events. Moves keep doing that through their own actions.
4. **What exists keeps its job.** Confidence: high.
   - **Guards** still answer "is this move offered?" They pick the transition. A check answers "is this change acceptable?" after the move is picked, and gives a message. Jira draws the same line: a failed condition hides the move, a failed validator refuses it [S23].
   - **Rights** still answer "may this actor do this verb here?"
   - **A transition's actions and a state's entry and exit actions** are the with-the-change actions of moves, and run as today. A trigger point adds what they can't say: before and after commit, checks, other verbs, and one point for every move of a verb.
   - **The refusal order** gains one row, and **the trace** one member (decision 28).

### Types and timings

5. **Three types: `check`, `action` and `replace`.** Confidence: high for check and action; medium-high for replace as a type.
   - They keep the owner's idiom, with `replace` in place of `override` (decision 16).
   - **The key is `type`,** as a state's and an invoke's subtype are, so a part holds no second `kind`.
6. **A check has a `severity`: `error` (the default) or `warning`.** Confidence: high.
   - An error refuses the step, with refusal `check` and the trigger point as `refusedBy`.
   - A warning lets the step through. The trace and the generated result list it, with its message.
   - There is no "block without an error". A check passes, warns, or refuses with its message.
7. **An action has a `timing`: `before`, `with-change` or `after-commit`.** Confidence: high for the three moments; medium-high for "before" as a timing.
   - **`before`** prepares the change: defaults, numbers, derived values. Its sets change the proposed change (decision 25), so every check sees the final data. It can't refuse; refusing is a check's job.
   - **`with-change`** runs in the same unit of work, after the effect. If it fails in the team's system, the verb is undone.
   - **`after-commit`** runs once the change is saved. It can't undo the change, may run late, and may run more than once, so the team's code must be safe to repeat.
   - **Why a timing, not a fourth type:** the systems studied register one kind of handler at several stages (Dataverse stages, Salesforce before and after triggers, Rails callbacks). A fourth type would split one idea in two.
   - `timing` is required on an action and allowed nowhere else (MQ8101).
8. **No queued timing and no compensation in the first release.** Confidence: medium-high.
   - Queued work has the after-commit contract (section 3). A pack delivers after-commit work through an outbox, which is a queue (decision 31).
   - Compensation can come later as an optional key, such as an action that undoes another. It changes no file.
9. **An after-commit action sets nothing** (MQ8105). Confidence: medium-high.
   - Once the change is saved, setting the object is a new change by the system, which would run modify's trigger points again. Setting data belongs before or with the change.
   - A team whose after-commit code writes back, such as an outside system's number, does a new modify in its own code, which runs the trigger points again under the depth guard (decision 21).

### Places

10. **Optional keys narrow where a trigger point runs. With none, it runs wherever its verbs happen to the lifecycle's subjects.** Confidence: high for `states`, `attributes` and `ends` (rights uses them); medium-high for `targets`, `transitions` and `entities`.

    | Key | Place | Runs when | With verbs |
    | --- | --- | --- | --- |
    | `states` | State | The object is in one of these states of this lifecycle (for a move: its source). A compound state counts when any state inside it is active | Any |
    | `targets` | Entering a state | The move goes into one of these states | Moves; `sign` |
    | `transitions` | Move, signature | The move is one of these transitions; for `sign`, a signature on their gates | Moves; `sign` |
    | `entities` | Type | The object's entity is one of these, or derived from one | Any |
    | `attributes` | Attribute | The change reads or modifies one of these attributes | `read`, `modify` |
    | `ends` | Relation end | The link goes through one of these ends of the subject | `connect`, `disconnect`, `read`, `modify`, `freeze`, `thaw` |

    - **Leaving a state** is `states` on move verbs. **Entering** is `targets`. A state's entry and exit actions stay as they are.
    - **A signature place** is `sign` with `transitions`, since a gate sits on a transition. Reject, ignore, withdraw and delegate join with richer sign-off.
    - Keys that don't fit their verbs are MQ8102. `attributes` beside `ends` is MQ8102, as for rights.
11. **Which lifecycles' trigger points run depends on the verb's group, as for rights.** Confidence: medium-high.
    - **Moves, signatures and tasks:** only the lifecycle where the move, signature or task is.
    - **Objects, links and own verbs:** every lifecycle that governs the object, each reading the object's state there. So a second lifecycle can't get around another's checks, for the reason rights decision 10 gives.
    - Other lifecycles' states come from the scenario, as for rights (rights decision 27).
12. **Types and inheritance.** Confidence: medium-high.
    - A lifecycle's trigger point runs for every type its subjects cover: an entity and its derived entities, or every entity carrying a stereotype and their derived entities (stereotype sets).
    - **`entities` narrows it** to some of those types, each with its derived entities. An entity the lifecycle doesn't govern is MQ8102.
    - **The opt-out is narrowing at the trigger point.** A type never lists the trigger points it skips. Part 2 adds `excludes` to a named trigger point, whose `subjects` can be broad (decision 33).

### What each moment reads

13. **Each verb group passes a fixed set of things, the same in simulation, in templates and in generated code.** Confidence: medium-high.

    | Verb group | `subject.` reads | `old.` reads | Also |
    | --- | --- | --- | --- |
    | Moves: `promote`, `demote`, own verbs sent as events | The object | The object before the move | The source states (`in_state`, `states`), where it goes (`targets`), the transition, the event's payload |
    | Signatures: `sign` | The object | The same | The transition and its gate; the signer is `current_actor()` |
    | Tasks: `complete`, `claim` | The object | The same | The task's state |
    | `create` | The new object's proposed values | Nothing (null) | |
    | `copy`, `revise` | The new object's proposed values | The object copied | |
    | `modify` | The proposed values | The values before | The attributes changed |
    | `read` | The object | The same | The attributes read |
    | `change-owner`, `change-type`, `change-lifecycle` | The proposed values | The values before | |
    | `connect`, `disconnect` | The object | The same | The end; generated code also passes the other object's key |
    | `delete`, `freeze`, `thaw`, `lock`, `unlock`, own verbs | The object | The same | An event's payload when an event sends it |

    - **Before an effect** (before actions, checks, replace), `subject.` is the proposed change, and `in_state` reads the states before the move. **After it** (with the change, after commit), `subject.` is the object after the verb, and `in_state` reads the states after the move.
    - **The payload** is read only when every event the trigger point can run on declares that attribute, as an action's sets read it today. Otherwise MQ3102.
    - **The context is never read.** A trigger point must run wherever the verb is done, and an edit outside a run has no context.
    - Every expression reads `current_actor()`, `now()` and `in_state`, as a guard does.
14. **`old.` reads the object's values before the verb.** Confidence: high that it is needed; medium-high for its shape.
    - In text, `old.price`. In the tree, a path with `"old": true`. To-one paths follow the same rule as `subject.`.
    - Only at trigger point sites (MQ3104 elsewhere).
    - "The price may not drop by more than a tenth" is `subject.price >= old.price * 0.9`.
15. **Two new site kinds: `Trigger` (a `when` or a check's condition, judged as a guard) and `TriggerSet` (a set's value, typed against its attribute).** Confidence: high.
    - Each reads what decision 13 says. They are handled explicitly at every place the `Set` kind needed (object effects decision 3), plus the new `old` node. SQL refuses both.
    - **`when` holds only when true.** False or unknown means the trigger point doesn't run, and the trace says it was skipped.

### Replace

16. **A replace takes the place of a verb's built-in effect. Everything around it still runs.** Confidence: high.
    - Rights, guards, before actions and checks run before it. With-the-change and after-commit actions run after it.
    - **History is still recorded.** The trace records the verb as done, and generated code writes its history record as for the built-in effect. ENOVIA's override skips "the normal system event code and history mechanism" [S7], so a team must add history back by hand. This design doesn't copy that.
    - **At most one replace can apply to one verb on one entity,** in one lifecycle or across the lifecycles that govern it (MQ8104).
    - **Never on governance verbs,** which history, rights and signatures rely on: every move, signature and task verb, `freeze`, `thaw`, `lock`, `unlock`, `change-owner`, `change-type`, `change-lifecycle`, `read` and `modify`. Own verbs have no built-in effect to replace. A replace on any of these is MQ8103.
    - **The body states the replaced effect:** `sets` the run applies, such as `archivedOn` set to `now()`, or the team's code.
    - **Why `replace`:** the verb `override` already exists, with another meaning.
17. **The verbs that allow a replace: `delete`, `disconnect`, `copy` and `revise`.** Confidence: medium-high. Owner question 2.
    - **`delete`:** soft delete or archive. Nothing else can keep the object.
    - **`disconnect`:** end a link instead of removing it, as effectivity does.
    - **`copy` and `revise`:** the team's own rules for what carries over.
    - **Not `create`:** numbering and naming are a `before` action. Creating an object somewhere else is where its data is held, which bindings and packs decide.
    - **Not `connect`:** there is no removal to soften. A before action prepares the link, and a check guards it.
    - **Not "data held outside":** where data lives is a storage choice. Dataverse's exception is for virtual table data providers, a whole data source [S19]. In Maquettiste that is bindings.
    - Adding a verb to the list later breaks no file. Taking one away would.

### Order

18. **Every verb runs in one fixed order. The first check that fails names the refusal.** Confidence: high. Rows 1 to 7 are rights decision 12, unchanged.

    | Order | Step | Refusal |
    | --- | --- | --- |
    | 1 to 7 | Frozen, closed lists, denies, defaults, no move, guards, signatures | As today |
    | 8 | **Before actions**, which build the proposed change | None |
    | 9 | **Checks**: errors refuse; warnings are listed | `check` (new) |
    | 10 | **The effect**: the built-in one (for a move, its exit, transition and entry actions, as today), or the replace | None |
    | 11 | **With-the-change actions** | None |
    | 12 | **After-commit actions**, once the step's whole change is done | None |

    - A check that refuses stops everything after it, and the proposed change is dropped.
    - **An event that takes several transitions** (parallel regions) is refused when a check refuses any of its moves.
    - **On an automatic move** (`always`, `after`, `done`, `invoke-done`, `invoke-error`), a check that refuses means the move isn't taken, as a guard that turns false. The trace names the check. The step that led to it is still accepted. Rights left this to trigger points (rights decision 23).
    - **A gated event:** each occurrence that is a signature runs `sign`'s trigger points. The occurrence that completes the gate also runs the move's.
19. **Within one step, all lifecycles' points for a moment run before the next moment. Within a moment, the acting lifecycle comes first, then the others by name, each in its list's order.** Confidence: high.
    - So every check sees every lifecycle's preparation.
    - No `order` number. The list's order is explicit, has no ties, and the editor reorders by dragging.

### Operational controls

20. **`required: true` means no bulk load ever skips the trigger point. Who may bypass the others is the host's permission.** Confidence: medium.
    - The model states only what must always run. Simulation has no bypass: every scenario runs every trigger point.
    - Generated code asks the host whether a caller may skip the trigger points that are not required. It asks through a hook whose stub answers no, and it logs each skip (decision 31).
    - **Why not a right:** a right is a verb on an object in a state. A bypass is a permission over how the running system writes, like who is signed in (the direction, section 12). Adding a built-in verb for it would be permanent (verbs decision 5).
21. **The model has no trigger recursion. Generated code guards against the team's own.** Confidence: medium-high.
    - A trigger point's sets are part of the verb that ran it. They never run `modify`'s trigger points, as a move's sets don't.
    - A trigger point raises no events and does no verb on another object in this item. So nothing in the model can call a trigger point from a trigger point. Moves keep the existing bound on microsteps (MQ9507).
    - Generated code counts nested runs. When the team's code inside a trigger point runs verbs more than 8 deep, the command fails, so the change is undone.
22. **`outside: true` says the team's code reaches outside systems.** Confidence: medium-high.
    - Simulation never runs the team's code, so it touches nothing. The trace marks such points "outside". Their sets, which are the model's own statements, still apply.
    - Generated scenario tests and dry runs record outside points instead of calling them (decision 31).
    - **A with-the-change action that touches outside systems is warned** (MQ8106): a rollback can't undo the outside call, so it usually belongs after commit. A `before` one is not warned, since asking an outside system for a number is a common way to prepare.
23. **A trigger point runs once per object. A pack may batch.** Confidence: medium-high.
    - Scenarios act on one object, and the model's meaning is per object. Windchill shows the runtime can convert between the two [S10].
    - A key for per-set rules can come later, if teams ask. It changes no file.

### Simulation and scenarios

24. **Trigger points run in simulation and replay, for every input:** events, automatic moves, signatures, task completions and verb steps. Confidence: medium-high.
    - They run after the existing checks (decision 18), in a project with or without rights.
    - In a scenario with no objects, the lifecycles that govern every subject of its process run theirs, each in its initial state, as rights do.
    - An invoked sub-process sees no object, so it runs no trigger points.
25. **Before actions build a proposed change. It is applied only when the step is accepted.** Confidence: high.
    - It starts as the object's row, with a `modify` step's values or a `create` step's values on top. Each before action's sets apply to it in turn.
    - On refusal it is dropped. On acceptance it is written before the effect, so a move's own actions see it.
    - **On `copy` and `revise`,** the proposed change is the new object, which the run doesn't hold. The trace shows its values, and nothing is written.
    - Within one trigger point, every value is worked out first, then all are applied, as for an action's sets (object effects decision 4).
    - A trigger point's sets on a frozen object during a move apply, with a note, as a move's sets do (rights decision 17).
26. **A check written as the team's code passes, unless the step assumes another outcome.** Confidence: medium-high.
    - A step may list **`assume`**: a check's id and `pass`, `warning` or `error`. It applies only to a check without a `condition` (MQ9305 otherwise).
    - This is the stub the evaluation asked for. It lets a scenario prove the path where the team's check refuses. The generated test then requires the team's code to give that outcome, so the assumption becomes an acceptance test.
    - An action written as the team's code changes nothing in a run, and the trace says so.
27. **An accepted `create` step writes its proposed change to the subject's row. A `create` step may give `values`.** Confidence: medium-high.
    - The scenario's subject stands for the object created. So a number or default prepared on create is seen by later steps.
    - Today `create` takes no values and writes nothing, so its outcomes don't change. A refused create still leaves the subject in place, as today.
28. **The trace and the expectations.** Confidence: high for the shape; medium-high for comparing in order.
    - The trace gains **`triggers`**: each trigger point whose places matched, in run order, with its lifecycle, verb, type, timing, outcome (`passed`, `warned`, `refused`, `assumed`, `ran`, `replaced`, `queued`, `skipped` when `when` was false, or `outside`) and the sets it applied. It gains **`warnings`**, the ids of checks that warned.
    - A refused step has refusal **`check`** and the trigger point's id as `refusedBy` (MQ9308).
    - A step may expect **`warnings`** (exactly those ids) and **`triggers`** (exactly the ids that ran, in order, `skipped` ones left out). A difference is MQ9310.
    - `record_scenario` writes them only in a project that uses trigger points, and only when not empty.

### Code generation

29. **Each output depends on what it needs, so a project pays only for what it uses.** Confidence: high.
    - **A project uses trigger points** when a lifecycle has `triggers` (part 2: or a named trigger point exists). Only then are step methods, the runner, the hooks and the new result members written, and only then does recording write the new expectations.
    - **One key says which lifecycles have trigger points on object, link and own verbs: `tn:triggers`** (section 7). Its hash never changes in a project without trigger points, as `rn:rights` doesn't without rights.
30. **`csharp-dapper` writes step methods per verb, and one partial method per trigger point for the team.** Confidence: medium-high.
    - **Step methods,** on each lifecycle with trigger points: `Before<Verb>`, `Check<Verb>`, `With<Verb>` and `After<Verb>` for each verb that has points, and `Perform<Verb>` where a replace is. Each calls the matching points in order (decision 19), testing their places and `when`.
    - **Bodies and `when` are partial methods the team writes,** documented with their text, since generated code can't read the object until the generated runtime (4.6). A new one fails the build until written, as a right's condition does. A trigger point's sets go to a `Record<Name>` method, as an action's sets do.
    - **Moves, signatures and tasks** run them inside the generated interpreter, in the order of decision 18.
    - **Object, link and own verbs done outside an event** go through a per-type runner, `<Entity>Verbs`, written for each entity whose governing lifecycles have such points. The team calls it from its own handlers, as rights decision 30 has the team call `Rights.Check`. It runs freezing and rights when those are written, then every governing lifecycle's steps, asking `StatesIn<Lifecycle>` for other lifecycles' states, as rights' hook does.
31. **Timing maps onto the pipeline the pack already writes.** Confidence: high.
    - Generated commands already run in a unit of work (`TransactionBehaviour`) that ends by writing the result's events to `IProcessOutbox`, inside the same transaction (`OutboxBehaviour`).
    - **Before, checks, the effect or replace, and with the change** run inside that unit of work. When the team's code throws, the command fails and the transaction rolls back.
    - **After commit** becomes one message per due trigger point, added to the result's events. So the outbox stores it only if the change commits. The team's relay delivers it later, through a generated `TriggerRelay`, to the team's method. Each message carries a stable id, so the team can drop a repeat.
    - **Bypass:** `TriggerCall.SkipOptional` is honoured only when the host's `ITriggerBypass` hook allows it (its stub refuses), and each skip is logged. Required points always run.
    - **Dry runs:** `TriggerCall.DryRun` runs freezing, rights, before actions and checks, and stops before the effect, so a screen can ask "would this be accepted, and with which warnings?". Outside points are recorded, not called.
32. **`process-docs` writes "What happens when" per verb.** Confidence: high. Section 10.

### Types with no lifecycle (part 2)

33. **(Part 2) A named trigger point is its own element, kind `trigger`, in `model/triggers/`. Its `subjects` name entities and stereotypes; its `excludes` name entities it skips.** Confidence: medium-high.
    - It runs for every object whose entity a subject covers (an entity and its derived entities; a stereotype's carriers and theirs), with or without a lifecycle. That is the type place for types such as a customer, which have no lifecycle and still need a number on create or an archive on delete.
    - Its keys are an inline point's, but for `states`, `targets` and `transitions`, which belong to one lifecycle (MQ8102). A state elsewhere is read with `in_state` in `when`.
    - **`excludes` is the opt-out,** written where the rule is, so "every approvable type but memos" is one file.
    - `subjects` is the key rights reserved for named rights on types (rights decision 8), so a later item can give both one control in the editor.
34. **(Part 2) Type points run before lifecycle points, at each moment.** Confidence: medium.
    - Among type points: the one whose subject is the farthest base runs first, as JPA runs superclass listeners first [S36]. At one depth, those brought by an entity come before those brought by a stereotype, then by name.
    - So the order runs from the outside in: type, then lifecycle, then the list's order within it.

### Tools

35. **One new tool, `get_trigger_sequence`: what runs, in order, when a verb happens in a lifecycle.** Confidence: medium-high.
    - It takes a lifecycle and a verb, and optionally a state, an entity, attributes, an end and a transition. It answers the steps of decision 18, each trigger point with its lifecycle, moment and places, and the built-in rows that apply.
    - It reads the snapshot and never resolves the whole model, as `get_rights` does. The editor's "What happens when" view and the docs pack's lists use the same evaluator, so they never disagree.

### Release

36. **Two parts, each a minor release, and a team upgrades together.** Confidence: high, by the owner's 4.1 decision 6.
    - Part 1 holds everything a lifecycle needs and is the part a review can hold. Part 2 adds the element kind, its editor and its place in the runner.
    - An older engine refuses a file with `triggers`, `old`, `assume`, the new expectations, or a `trigger` element. `formatVersion` stays 1.

## 5. Schema and files

**Part 1**

| File | Key | Type | Notes |
| --- | --- | --- | --- |
| `common.json` | `$defs/triggerBody` (new) | `type`, `verbs`, `timing`, `severity`, `when`, `condition`, `sets`, `required`, `outside`, `entities`, `attributes`, `ends` | `type`: `check`, `action` or `replace`. `timing`: `before`, `with-change`, `after-commit`. `severity`: `error` or `warning`. `when` and `condition`: `$defs/expr`. `sets`: as an action's. Booleans default to false |
| `common.json` | `$defs/expr` path gains `old` | boolean | Decision 14 |
| `process.json` | `triggers` (after `rights`) | list of `{ id, name, displayName?, description?, …triggerBody, states?, targets?, transitions? }` | Decisions 1 to 12. Part kind `process-trigger` |
| `scenario.json` | step `assume` | trigger point id → `pass`, `warning` or `error` | Decision 26 |
| `scenario.json` | step `values` allowed with `create` | | Decision 27 |
| `scenario.json` | step `expect.warnings`, `expect.triggers`; `expect.refusal` gains `check` | id lists | Decision 28 |

**Part 2**

| File | Key | Type | Notes |
| --- | --- | --- | --- |
| `trigger.json` (new kind) | `kind`, `id`, `name`, `displayName`, `package`, `description`, `subjects`, `excludes`, and `triggerBody` | `subjects`: a non-empty list of `{ entity }` or `{ stereotype }`. `excludes`: entity ids | Decision 33. Folder `model/triggers/` |

Also updated in each part: the sample app's schema copies, `openapi.yaml` by hand (the shapes, the trace, the simulate and record requests, the records, `get_trigger_sequence`), and the editor files generated from it.

```json
"triggers": [
  { "id": "…", "name": "AssignNumber", "type": "action", "timing": "before", "verbs": ["create"], "required": true,
    "description": "Gives the document its number, such as DOC-000123." },
  { "id": "…", "name": "TitleBeforeReview", "type": "check", "verbs": ["promote"], "targets": ["<id of Review>"],
    "condition": { "text": "coalesce(subject.title, '') <> ''" },
    "description": "A document needs a title before it goes to review." },
  { "id": "…", "name": "LargePriceDrop", "type": "check", "severity": "warning", "verbs": ["modify"],
    "attributes": ["<id of price>"], "condition": { "text": "subject.price >= old.price * 0.9" },
    "description": "The price dropped by more than a tenth." },
  { "id": "…", "name": "StampRelease", "type": "action", "timing": "with-change", "verbs": ["promote"],
    "targets": ["<id of Released>"],
    "sets": [ { "id": "…", "attribute": "<id of releasedOn>", "value": { "text": "date_of(now())" } },
              { "id": "…", "attribute": "<id of releasedBy>", "value": { "text": "current_actor()" } } ] },
  { "id": "…", "name": "PublishToPortal", "type": "action", "timing": "after-commit", "verbs": ["promote"],
    "targets": ["<id of Released>"], "outside": true,
    "description": "Sends the released document to the supplier portal." },
  { "id": "…", "name": "Archive", "type": "replace", "verbs": ["delete"],
    "sets": [ { "id": "…", "attribute": "<id of archivedOn>", "value": { "text": "now()" } } ],
    "description": "Documents are archived, never removed." },
  { "id": "…", "name": "CreditLimit", "type": "check", "verbs": ["connect"], "ends": ["<id of end orders>"], "outside": true,
    "description": "The customer's credit limit, asked of the finance system." }
]
```

As text, conditions and values are stored as trees of ids. `AssignNumber` and `PublishToPortal` are the team's code. `CreditLimit` is a check the team writes, so a scenario assumes its outcome:

```json
"steps": [
  { "id": "…", "input": "verb", "verb": "create", "participant": "<p1>", "values": { "<id of title>": "" } },
  { "id": "…", "input": "event", "event": "<id of Submit>", "participant": "<p1>",
    "expect": { "accepted": false, "refusal": "check", "refusedBy": "<id of TitleBeforeReview>" } },
  { "id": "…", "input": "verb", "verb": "modify", "participant": "<p1>", "values": { "<id of price>": 80 },
    "expect": { "values": { "<id of price>": 80 }, "warnings": ["<id of LargePriceDrop>"] } },
  { "id": "…", "input": "verb", "verb": "connect", "participant": "<p1>", "end": "<id of end orders>", "object": "<o2>",
    "assume": { "<id of CreditLimit>": "error" },
    "expect": { "accepted": false, "refusal": "check", "refusedBy": "<id of CreditLimit>" } },
  { "id": "…", "input": "verb", "verb": "delete", "participant": "<p1>",
    "expect": { "triggers": ["<id of Archive>"], "values": { "<id of archivedOn>": "2026-11-07T09:00:00Z" } } }
]
```

The scenario object's price is 100, so the drop to 80 warns. The archived document stays in the run, so later steps still reach it.

Part 2:

```json
{ "kind": "trigger", "id": "…", "name": "CustomerNumber", "type": "action", "timing": "before", "verbs": ["create"],
  "subjects": [ { "entity": "<id of Customer>" } ], "required": true,
  "description": "Gives each customer its number, such as C-00042." }
```

## 6. Rules

The process families MQ90xx to MQ99xx are full. Trigger points take the next free family, **MQ81xx**, labelled "Trigger points".

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ8101 | E | A trigger point's shape: `timing` missing on an action or beside another type; `severity` beside a type other than check; `condition` on an action or a replace; `sets` on a check; `triggers` on an orchestration or a library | Remove the key |
| MQ8102 | E | A narrowing key that doesn't fit: `states` or `targets` that aren't this lifecycle's states; `transitions` that aren't its transitions; `targets` or `transitions` beside a verb that isn't a move or `sign`; `attributes` beside a verb other than `read` and `modify`, beside `ends`, or not held by every subject; `ends` beside a verb that can't act on a link, or not held by every subject; `entities` the lifecycle doesn't govern. Part 2 adds: `states`, `targets` or `transitions` on a named trigger point; `excludes` that no subject covers | Remove the entry |
| MQ8103 | E | A replace on a verb that doesn't allow it (decisions 16 and 17) | Make it an action |
| MQ8104 | E | Two replaces that can apply to one verb on one entity, in one lifecycle or across the lifecycles that govern it (part 2: or named trigger points) | Narrow one with `entities` |
| MQ8105 | E | An after-commit action with `sets` | Make it a with-change action |
| MQ8106 | W | A with-change action that touches outside systems: a rollback can't undo the outside call | Make it after commit |
| MQ8107 | W | A trigger point that can never run: a move verb that no event or transition of the lifecycle names, or `transitions` none of whose moves has the verb | Remove it |
| MQ9310 | E | A scenario step whose warnings or trigger points differ from `expect.warnings` or `expect.triggers` | Update the expectations from a replay |

**Existing rules that gain a case.** No finding changes for a model that uses none of this: every new case needs a trigger point, an `old.` path, `assume` or `values` on `create`.
- **MQ3001:** two trigger points of one lifecycle with one name.
- **MQ3101, MQ3102, MQ3108 and the typing rules:** `when`, `condition` and set values; MQ3102 for a payload attribute some event lacks.
- **MQ3104:** `old.` outside trigger point sites; the context read at one.
- **MQ9701, MQ9704:** a verb named by a trigger point.
- **MQ9802, MQ9803:** a trigger point's sets, as an action's.
- **MQ9305:** `assume` naming something other than a check without a condition, or a value other than the three; `values` beside `create` that a set couldn't set.
- **MQ9308:** the refusal `check`.

The mock backend's rule list follows.

## 7. Resolved model and templates

```csharp
public sealed class RTriggerPoint : RObject                     // Kind "process-trigger"; part 2: "trigger" for a named one
{
    public string Name, DisplayName, Description;
    public string Type { get; internal set; } = "";             // "check", "action" or "replace"
    public string? Timing { get; internal set; }                // actions: "before", "with-change" or "after-commit"
    public string? Severity { get; internal set; }              // checks: "error" or "warning"
    public RList<RVerb> Verbs { get; internal set; }
    public RList<RState> States, Targets;                       // empty when not narrowed
    public RList<RTransition> Transitions;
    public RList<REntity> Entities;                             // as written; Covers lists every entity it runs for
    public RList<RAttribute> Attributes; public RList<REnd> Ends;
    public RExpression? When, Condition;                        // typed at the Trigger site
    public RList<RSet> Sets { get; internal set; }              // typed at the TriggerSet site
    public bool Required, Outside, TeamCode;                    // TeamCode: no condition (check) or no sets (action, replace)
    public RList<REntity> Covers { get; internal set; }         // the entities it runs for, in name order
    public RProcess? Process { get; internal set; }             // null for a named one (part 2)
    // Part 2 adds Subjects and Excludes (named only) and Package.
}
public sealed class RTriggerVerb                                // one verb's points in this lifecycle, by moment, in run order
{
    public RVerb Verb;
    public RList<RTriggerPoint> Before, Checks, Replace, WithChange, AfterCommit;
}
// RProcess gains Triggers (file order) and TriggerVerbs (one entry per verb that has points, in the verb list's order).
// REntity gains TriggerLifecycles: the lifecycles governing it with points on object, link or own verbs, by name (part 2: and
//   Triggers, the named trigger points that cover it, in decision 34's order). RVerb gains Triggers.
// RStep gains Assume; RExpectation gains Warnings and Triggers; the step's trace gains Triggers and Warnings.
// ResolvedModel gains Triggers (part 2): the named trigger points.
```

- **Templates read** `process.triggers`, `process.trigger_verbs`, the trigger point's members (`trigger.type`, `trigger.timing`, `trigger.severity`, `trigger.verbs`, `trigger.states`, `trigger.targets`, `trigger.transitions`, `trigger.entities`, `trigger.attributes`, `trigger.ends`, `trigger.when`, `trigger.condition`, `trigger.sets`, `trigger.required`, `trigger.outside`, `trigger.team_code`, `trigger.covers`), `entity.trigger_lifecycles`, `verb.triggers`, `step.assume`, `step.expect.warnings`, `step.expect.triggers` and `step.trace.triggers`. Part 2 adds `model.triggers`, `entity.triggers`, `trigger.subjects` and `trigger.excludes`.
- **`process.trigger_verbs`** lists only this lifecycle's points. Other lifecycles join at run time, as decision 11 says, and `entity.trigger_lifecycles` names them for a per-type runner.

**Dependency keys.**
- **The trigger naming key, `tn:triggers`.** Its hash counts, for each lifecycle with `triggers`, by id: the object, link and own verbs its points name, and the verbs its replaces name with their entities. Part 2 adds each named trigger point's id, verbs and subjects. It changes when a lifecycle gets its first point on such a verb or loses its last, or a replace changes; not when a check's condition or a point's states change. It is worked out once per snapshot from the index rows, as `rn:rights` is: a process row carries those verbs only where present. `Planning/DependencyHasher.cs` gains the `tn` prefix, and the plan explainer words it "The trigger points on object verbs".
- **`process.triggers` and `process.trigger_verbs`** key on the process file.
- **`entity.trigger_lifecycles`** keys on `tn:triggers`, and in a project that uses trigger points on the entity's governing keys (rights section 6) and each lifecycle it lists.
- **`verb.triggers`** keys on `tn:triggers`, and once its hash holds anything, on the file of each lifecycle with trigger points.
- So a template that reads none of these pays nothing, and in a project without trigger points the one new key never changes.

**Records:** process records gain `triggers`, step records `assume`, expectation records `warnings` and `triggers`, trace records `triggers` and `warnings`, each only where present. Part 2: named trigger points get records of kind `trigger`. So `get_resolved_model` answers don't change for a project without them.

## 8. Simulation and replay

- **One evaluator.** `Processes/TriggerSequence.cs` works out, for a verb, the acting lifecycle, the object's entity and states, and the place (transition, targets, attributes, end), which points run at each moment and in what order (decisions 11, 18, 19 and, in part 2, 34). The interpreter, `get_trigger_sequence`, `process.trigger_verbs` and both packs use it. It is cached per snapshot, per lifecycle and verb.
- **Where the interpreter runs them:**
  - `HandleEvent` (`Processes/StatechartInterpreter.cs:206`): after rights and `Select`, before `Microstep`, it runs before actions on the proposed change, then checks. On acceptance it writes the proposed change, runs the microstep (the effect), then with-the-change points. After-commit points are queued.
  - **Automatic moves** in `RunToCompletion`: the same steps per microstep. A refusing check makes the move not taken, with a note (decision 18).
  - `CheckSigner` and `Sign` (`:1066`, `:1105`): `sign`'s points after the signature rows.
  - `HandleInvoke` (`:315`): `complete`'s points for a human task.
  - `HandleVerb` (`:373`): after rights and freezing, the points of the step's verb. A replace takes the place of the effect in its `switch`: a replaced `delete` doesn't set `_deleted`. `create` writes its proposed change (decision 27).
  - **At the end of the step,** queued after-commit points are listed in the trace as `queued`, in order.
- **Other lifecycles' points** for object, link and own verbs read the object's states in those lifecycles as `RunRights` does (`Processes/RunRights.cs`), and their conditions read the running chart for the acting lifecycle (rights decision 6).
- **`assume`** answers a team-code check's outcome. Without it, such a check passes and the trace says "assumed to pass".
- **The trace** gains `triggers` and `warnings` (decision 28), left out when empty, so today's traces don't change.
- **Replay** compares `expect.warnings` and `expect.triggers` (MQ9310) and the refusal `check` (MQ9308).
- **Replay keys** (`Resolution/ResolveRun.Processes.cs`): a scenario whose process has trigger points keys on its file, as today. A scenario that does an object, link or own verb (a verb step, or an event that names one) also keys on `tn:triggers`, and in a project that uses trigger points on each governing lifecycle with points on those verbs. So it replays when another lifecycle gains a check on `modify`. A project without trigger points gains only `tn:triggers` on such scenarios, and its hash never changes there.
- **Speed:** a lifecycle without trigger points costs nothing. Other lifecycles are asked only for object, link and own verbs, and only when they have points on that verb.

## 9. Tools

- **`get_trigger_sequence` (new):** decision 35. Read-only. Answers in one page, since one verb's sequence is short.
- **`simulate_process`, `record_scenario`, `verify_scenarios`:** the trace's new members, `assume`, `values` on `create`, and the expectations of decision 28.
- **`preview_expression`:** previews a `when`, a condition or a set value at its pointer, with `old.`.
- **`save_element`, `apply_batch`:** accept `{ "text": … }` for `when`, `condition` and set values, parsed in the lifecycle's scope.
- **`get_references`, `referenced_by`:** a trigger point's verbs (own verbs), states, targets, transitions, entities, attributes, ends, set attributes and expression paths; a step's `assume` ids.
- **Deletes:** a state, transition, entity, attribute or end a trigger point names: `remove-references` drops it from the list. **When the last one goes, the trigger point goes too,** since an empty list would widen it to everywhere, as for rights. A set's attribute goes with its set. `rename-verb` and `remove-verb` walk trigger points; one whose last verb is removed goes. Part 2: a named trigger point's last subject goes the same way.
- **`get_model_index`:** process rows gain `triggers`, the count, only when above zero, and their `verbs` also list the verbs their trigger points name. Part 2: named trigger points get rows with `type`, `verbs` and `subjects`.
- **`export_process`, `import_process`:** trigger points travel in `meta.maquettiste`.
- **`validate`:** the rules of section 6.
- **The CLI** follows the same tools. **The rule list, the schemas, the conventions resource, the modeling skill and the tool texts** (`src/Maquettiste.Cli/Mcp/ProcessTools.cs` and `Agent/tools.json`) describe all of this.

## 10. Packs

Output changes only for a model that uses what each output needs (decision 29).

**`csharp-dapper`, part 1**
- **`Triggers.cs`,** in the processes namespace, only when a lifecycle has `triggers`:
  - `TriggerCall`, a record with init properties, so later members don't break a team's call: the verb, the lifecycle, the entity, the instance (none outside a run), the envelope, the held actors, the source states, the targets and transition (moves), the event (moves, with its payload), the end and the other object's key (links), the attributes changed (`modify`), `Old` and `New` (the team's objects, given by the caller; none inside the interpreter until 4.6), `SkipOptional` and `DryRun`.
  - `TriggerOutcome` (passed, the warnings with their messages, and the refusing point with its message), and one message record per after-commit point, with a stable `MessageId` made of the instance or object key, the step and the point.
  - `ITriggerBypass`, with a team-owned stub `Dispatch/TriggerBypass.cs` written once, which answers that nobody may skip and logs each request. A `TriggerRelay` that dispatches an after-commit message to the team's method.
  - A depth counter that fails the command past 8 nested runs (decision 21).
- **Step methods,** on each lifecycle's handlers with trigger points: `Before<Verb>`, `Check<Verb>`, `With<Verb>`, `After<Verb>`, and `Perform<Verb>` where a replace is (decision 30).
- **The team's half:** one partial method per trigger point body (`<Name>`), per `when` (`<Name>When`), and per point with sets (`Record<Name>`), each documented with its text. A trigger point's name is unique in its lifecycle (MQ3001), so its methods never collide. After-commit methods carry a comment: they may run more than once.
- **The interpreter:** runs the steps of decision 18 for events, automatic moves, signatures and human tasks. `Refusals` gains `Check`, and results gain `Warnings`, only when `Triggers.cs` is written. After-commit messages join the result's events, so `OutboxBehaviour` writes them in the command's transaction (decision 31).
- **Per-type runners:** `<Entity>Verbs`, for each entity whose governing lifecycles have points on object, link or own verbs (`entity.trigger_lifecycles`). `RunAsync(verb, call, perform)` runs freezing and `Rights.Check` when written, then each lifecycle's steps, then the team's `perform` or the replace, and returns accepted or refused, warnings, and the events for the outbox. The team calls it from its operation handlers, inside its unit of work.
- **Scenario tests:** a generated `ScenarioTriggers` records outside points and after-commit messages instead of calling them, and answers outside team-code checks from the step's `assume`. A team-code check that isn't outside runs the team's code, and the test asserts it gives the assumed outcome. A step with `expect.warnings` or `expect.refusedBy` asserts them.
- **Nothing goes in `helpers.js`.**

**`csharp-dapper`, part 2:** named trigger points' bodies in a `Triggers` class per package, written once however many types they cover, and their place in each `<Entity>Verbs` runner, including entities with no lifecycle.

**`process-docs`, part 1**
- **The lifecycle page,** when it has `triggers`: a "Trigger points" table (name, type, verbs, where, when, what it does, severity, required, outside, why), and for each verb a "What happens when someone promotes a Document" list in the order of decision 18: the built-in rows that apply, then each trigger point with its sentence. A team-code body says "the team's code: <why>". An outside one says so.
- **The scenario page:** per step, the trigger points that ran, warnings, assumed outcomes, and refusals by a check with its message ("refused by TitleBeforeReview: a document needs a title before it goes to review").
- **The verbs page:** the trigger points per verb.

**`process-docs`, part 2:** a trigger points page with each named trigger point, the types it covers and excludes, and a "Trigger points" line on each covered type's lifecycle page.

**`sql-ddl`, `seed-data` and the TypeScript sample:** no change.

## 11. Editor

**Process editor, a Triggers tab** (lifecycles only), after Rights:
- **The grid:** one row per trigger point, in run order, reordered by dragging. Columns: Name, **Does** (the shared verb picker, several at once), **Type** (Check, Action, Replace), **When it runs** (Before checks, With the change, After commit; actions only), **Severity** (Error, Warning; checks only), **Where** (chips for states, into states, moves, types, attributes and links, edited in one popover), **Only if** (the value box, for `when`), **What** (the value box for a check's condition, or the sets editor from object effects; empty shows "The team's code"), **Required**, **Outside systems**, and **Why**. Replace is offered only for the verbs that allow it.
- **Add trigger point** at the foot. Icon buttons carry tooltips.
- **A toggle, Trigger points or What happens when.** The second takes a verb, and optionally a state and a type. It shows the steps in order, each a link, from `get_trigger_sequence`. Other lifecycles' points appear with their lifecycle's name.

**Scenario editor:**
- **Steps:** a `create` step gets a values cell. An **Assume** cell lists the team-code checks the step can reach, each with Pass, Warning or Error.
- **Expectations:** **Warnings** and **Trigger points** cells, filled by "Update from replay". The Refusal cell offers **Check**.

**Simulation panel:** each step lists the trigger points that ran, with their outcome as a badge, and warnings in amber. A refusal by a check shows its message and links to it.

**Named trigger points (part 2):** a **Trigger points** folder in the Processes explorer, after Rights. The editor has the same fields without states, targets and moves, plus **Covers** (entities and stereotypes) and **Except** (entities), and a list of the types it runs for. Search finds them.

**Elsewhere:** the Verbs tab counts trigger points among a verb's uses. Quick fixes: MQ8101 and MQ8102 remove the key or entry, MQ8103 makes the point an action, MQ8105 and MQ8106 change its timing, MQ8107 removes it, MQ9310 updates the expectations from a replay.

**Generated and mock files:** the editor's API types come from `openapi.yaml` by the fixed command. The part kind's entries in `model/model.ts` and `model/labels.ts` are added by hand. In `src/editor/src/mocks/model/`, the mock gains `triggerRules.ts` beside `rightRules.ts`, and `stepper.ts` (1,768 lines today) gains the steps of decision 18 for events and verb steps. `validate.ts`, `refs.ts`, `store.ts`, `cascade.ts` and `modelIndex.ts` follow. `src/editor/src/mocks/processHandlers.ts` gains a `get_trigger_sequence` handler, **simpler than the engine's:** this lifecycle's points only, with no other lifecycles. Other lifecycles are tried against the engine in the browser pass.

## 12. A project that uses none of this

**After part 1:**
- **Files:** nothing rewrites a file, so saves, `format`, `format --check` and snapshot import behave as today. Recording a scenario writes what it writes today.
- **Existing guards and actions:** unchanged. Guards still pick transitions. A transition's actions and a state's entry and exit actions run in the same order, with the same sets and context.
- **Findings:** the same. Every new rule and case needs a trigger point, `old.`, `assume` or `values` on `create`.
- **Output:** the same bytes. Each pack change depends on `process.triggers`, `entity.trigger_lifecycles` or a new scenario key.
- **Simulation and replay:** the same outcomes and traces. A `create` step without values writes nothing new. The new trace members are left out.
- **Tool answers about the model:** the same. New record and index members are left out when unused, and `process.triggers`, `process.trigger_verbs` and `entity.trigger_lifecycles` are empty. `get_trigger_sequence` is new, and answers the built-in rows alone. The rule list, the schemas and the tool texts describe the additions.
- **Dependency keys:** the same, but for one key. A scenario that does an object, link or own verb, and a template that reads `entity.trigger_lifecycles` or `verb.triggers`, also keys on `tn:triggers`. Its hash never changes in a project without trigger points, so nothing renders or replays again because of it.
- **Speed:** the same. The sequence is worked out only for lifecycles with trigger points.
- **Capabilities:** once built, these rows of `lifecycle-capabilities.md` part D become Yes: named points before and after; an override that replaces the normal behaviour (as a narrow replace); a failure that does or doesn't roll back; runs in line or later; order when several run; the same kinds at every point; points on sign-off (sign only; the rest with richer sign-off); points on other state events; points on every operation on an object; points on a relationship's operations; points on an attribute's change; messages back when refused. "A point just before commit" becomes Part (with the change). "Triggers switched off for a session" becomes Part (`required`, and the host's bypass). "An action on one object that moves another" stays No.
- **Once, as on any upgrade:** every unit renders again and writes the same bytes, since the engine version is part of each unit's key; the editor refreshes the `.schema` copies, or `validate` warns MQ1008 until refreshed; the index cache is read in full once.
- **A team on mixed engine versions:** an older engine refuses the new keys (decision 36).

**After part 2:**
- **A project that uses part 1 but no named trigger points** keeps its files, output bytes, findings, keys and outcomes.
- **A project that uses neither part** keeps everything listed above.

## 13. What comes next

| Item | What it takes from trigger points |
| --- | --- |
| Richer sign-off | `reject`, `ignore`, `withdraw`, `delegate` and `route` gain trigger points at a gate, with `transitions` as the place |
| Scenarios across objects | Connect and disconnect run the other object's points too. A trigger point that sends an event or does a verb on another object ("a change order releasing its items"), with a depth bound in the model |
| `set-state` and `override` as scenario steps | The rights design planned them here (rights section 12). Their points work in generated code and docs from part 1. The steps need their own meaning for entry and exit actions and for gates, so they come as a small item of their own |
| The generated runtime (4.6) | Generated code reads the object, so `when`, conditions and sets no longer go to the team |
| Revisions | `revise` gets its behaviour. A replace on it already works |
| Rights on types with no lifecycle | Shares `subjects` with part 2's named trigger points (rights decision 8) |
| If teams ask | Queued work as its own timing; compensation; per-set trigger points; `old.` across to-many links; `outside` on guards and actions themselves. Each adds an optional key and changes no file |

## 14. Decisions the owner must make

1. **Names, permanent once released.** Recommended, with confidence medium-high:
   - the list `triggers` (shown as "Trigger points"), the part kind `process-trigger`, and in part 2 the kind `trigger` with `subjects` and `excludes`;
   - `type` with `check`, `action` and `replace` (not `override`, which is a verb);
   - `timing` with `before`, `with-change` and `after-commit`;
   - `severity` with `error` and `warning`; `when`, `condition`, `required`, `outside`, `targets`, `transitions` and `entities`;
   - `old.` in the expression language;
   - the step's `assume` with `pass`, `warning` and `error`; `expect.warnings`, `expect.triggers` and the refusal `check`;
   - the tool `get_trigger_sequence`, and in `csharp-dapper` `TriggerCall`, `ITriggerBypass`, `TriggerRelay` and `<Entity>Verbs`.
2. **Which verbs allow a replace.** Recommended: `delete`, `disconnect`, `copy` and `revise` (decision 17). Confidence: medium-high. The other choice is the evaluation's list, which adds `create` and `connect`. Numbering is better as a before action, and storage elsewhere is a binding. Adding a verb later breaks nothing; taking one away breaks files.
3. **What is in the first release.** Recommended: part 1 alone, as 0.17.0. That is trigger points on lifecycles for every verb, all three types and timings, and the scenario stubs. Then part 2, named trigger points for types with no lifecycle, as 0.18.0. Queued work, compensation and per-set rules wait for a team to ask (decisions 8 and 23). Confidence: medium-high. The other choice is both parts in one release, about 6.5 hours in one larger pull request.

## 15. Tests

**Part 1**
- **`TriggerRuleTests` (new):** failing and passing cases for MQ8101 to MQ8107 and MQ9310, and the new cases of MQ3001, MQ3101, MQ3102, MQ3104, MQ3108, MQ9701, MQ9704, MQ9802, MQ9803, MQ9305 and MQ9308. MQ8104 across two lifecycles that govern one entity. Today's models give the same findings.
- **`TriggerSequenceTests` (new):** the order of decision 18 for each verb group; moments before lifecycles (decision 19); the acting lifecycle first; each narrowing key, compound states, `entities` with derived entities and a stereotype's carriers; which lifecycles answer per group; an orchestration with a subject; a scenario with no objects.
- **`TriggerReplayTests` (new):**
  - Before actions seen by checks; a refused step drops the proposed change; an accepted one is seen by the move's own actions; `copy` shows the new object without writing it.
  - Checks: error and warning; on an event with parallel transitions; on an automatic move (not taken, with a note); on `sign` and on the gate-completing occurrence; `assume` for each outcome; a team-code check assumed to pass.
  - Replace: a replaced `delete` keeps the object and later steps reach it; its sets apply; history in the trace.
  - With-the-change sets after the effect; after-commit points queued in order; `outside` marked and its sets applied; `when` false shown as skipped; `old.` on `modify`, on a move and on `create` (null).
  - Trigger point sets don't run `modify`'s points. A frozen object during a move: sets apply, with a note.
  - `create` with values written to the row; a `create` step without values unchanged.
  - Expectations, MQ9310, recording with and without trigger points.
  - Replay keys: lifecycle B gains a check on `modify`, and a scenario of lifecycle A with a `modify` step replays; a project without trigger points keeps `tn:triggers` at one hash.
  - Unchanged traces and replays for a model without trigger points, including models that use rights and freezing.
- **`TriggerExpressionTests` (new):** `old.` parsing, typing and printing; the two site kinds; the payload rule; previews.
- **`ResolvedTriggerTests` (new):** `process.triggers`, `process.trigger_verbs`, `trigger.covers`, `entity.trigger_lifecycles`, `verb.triggers`, records; dependency keys, with each stale-output test checked to fail without its fix.
- **`GetTriggerSequenceTests` (new):** agrees with the interpreter's order; answers without resolving the model; the built-in rows alone in a project without trigger points.
- **No change:** a model without trigger points gives the same output, files, `format --check`, findings, replay, traces and tool answers, and its keys gain only `tn:triggers`. The pack golden tests pass untouched.
- **Pack tests:** a new case per pack. `csharp-dapper`'s `Triggers.cs`, step methods, partials, interpreter steps, `<Entity>Verbs`, outbox messages with stable ids, the bypass stub, the depth guard and scenario tests build with warnings as errors and run. A with-the-change body that throws rolls the command back. An after-commit message is written only when the command commits. `process-docs`' table, "What happens when" lists and scenario lines.
- **Editor:** unit tests for the grid's columns per type, the replace filter, and the Where popover. Browser tests (in the browser pass) for adding each type, reordering, What happens when with another lifecycle, an Assume cell, and updating expectations from a replay.
- **Acceptance,** on an invented model: the document lifecycle and scenario of section 5; an employee governed by HR and Projects, where HR's warning on `modify` of `salary` runs during a Projects verb step.

**Part 2**
- Named trigger points: loading, references, deletes (the last subject), `excludes`, MQ8102 and MQ8104 cases; the order of decision 34; a customer with no lifecycle whose `create` step runs `CustomerNumber`; packs' per-package bodies and runners for entities with no lifecycle; no change for a project that uses part 1 only.

## 16. Tasks and the build plan

### Tasks

The sizes are days for a person who knows the code, with tests and a review. They show how the groups compare.

**Part 1**

| Wave | Group | What | Size |
| --- | --- | --- | --- |
| 1 | Contract and model | The schemas, `openapi.yaml`, rule texts; the trace, refusal and expectation members; the `process-trigger` part; the `Trigger` and `TriggerSet` sites and the `old` node; MQ8101 to MQ8107 and the changed rules | Two days |
| 1 | References, deletes and the sequence | References, deletes and their widening guard, `rename-verb` and `remove-verb`, index rows, localization; `TriggerSequence` | A day and a half |
| 1 | Editor, part one | The Triggers tab and grid, the Where popover, quick fixes, the mock's validation and references | A day and a half |
| 2 | Simulation and scenarios | The interpreter's steps for events, automatic moves, signatures, tasks and verb steps; the proposed change; replace; `assume`; `create` values; the trace; MQ9310; recording; replay keys | A day and a half |
| 2 | Resolved model, keys, tools and docs output | `RTriggerPoint`, `process.trigger_verbs`, `entity.trigger_lifecycles`, `tn:triggers`, records, `get_trigger_sequence`, tool texts; `process-docs` | A day and a quarter |
| 2 | `csharp-dapper` | `Triggers.cs`, step methods, partials, the interpreter, `<Entity>Verbs`, outbox messages and the relay, the bypass stub, the depth guard, scenario tests | A day and a half |
| 2 | Editor, part two | What happens when, scenario cells, the simulation panel, the mock's stepper and a simple `get_trigger_sequence` | A day and a half |
| 3 | Docs | User guide (processes, scenarios, trigger points, expressions), releases, modeling skill, capabilities rows, `next.md` | Half a day |

That comes to about eleven and a half days for a person.

**Part 2**

| Wave | Group | What | Size |
| --- | --- | --- | --- |
| 1 | Contract and engine | The `trigger` kind, `subjects` and `excludes`, rules, references, deletes, the sequence's type points | A day and a half |
| 1 | Editor | The explorer folder and editor, the mock | A day |
| 2 | Simulation and output | Type points in runs and replay keys; resolved members; both packs, including runners for entities with no lifecycle | A day and a half |
| 3 | Docs | User guide, releases, `next.md` | A quarter day |

That comes to about four and a quarter days for a person.

**Checklist for every group.** Anything that reads or writes a lifecycle's parts, an expression site, a verb, a scenario step or the trace must be found and handled:
- **A new part kind (and in part 2 an element kind):** `Model/ModelIndexer.cs` (the part kind map and `SubElementKinds`), `Model/PropertySchemas.cs` (`TranslatedHolderKinds`), `Loading/NameIndex.cs`, the list-to-kind map in `Loading/ChangePlanner.Cascade.cs`; part 2 also `Model/ElementKind.cs`, `Model/KindInfo.cs` (folder `model/triggers`), `Model/ElementBase.cs`, `get_model_kinds` and `Localization/LocaleChains.cs`.
- **Expressions:** the two sites in `Expressions/ExprType.cs` and every site switch the `Set` and `Right` kinds needed (`ExprJs.cs`, `ExprTyper.cs`, `ExprScope.cs`, `ExprSql.cs`, `BuiltinHelpers.cs`, `ModelStore.ExpressionPreview.cs`), the `old` node in each, and `Validation/ExpressionRules.cs` (`ExprSites.Of`).
- **Processes:** `Model/Processes.cs`, `Processes/StatechartInterpreter.cs`, `Processes/ScenarioReplayer.cs`, `Processes/StepTrace.cs` (`Refusals`), `Processes/RunRights.cs` (other lifecycles' states), `Processes/VerbStepFields.cs`, `Processes/ProcessApi.cs`, `Processes/ProcessLibraries.cs` (a library holds no trigger points), and the exporter and importer.
- **Resolver, keys and records:** `Resolution/ResolvedProcesses.cs`, `Resolution/ResolveRun.Processes.cs`, `Editor/ResolvedRecords.cs`, `Planning/DependencyHasher.cs` (`tn`), `Generation/PlanExplainer.cs`.
- **Tools:** `src/Maquettiste.Cli/Mcp/ProcessTools.cs` and `Agent/tools.json`, each owned by one agent per wave.
- **Contracts:** the schema copies, `openapi.yaml` and its generated files. `validation-rules.json` is written only by the fixed command.
- **Test harness:** `RenderKit.WithSettings` and `ResolvedCoverageTests`.

### How it gets built

**What earlier items cost.** Rights part 1, about 7,500 to 8,500 lines, cost about 1.46M output tokens, 5.17M new input and 372M cached re-reads over about 3.1 hours. Its estimate had said 1.0M, 2.6M and 230 to 260M over 4 to 4.5 hours. Part 2, about 3,000 lines, cost about 0.71M, 2.25M and 143M over about 3 hours, against an estimate of 0.45M, 1.2M and 120 to 135M over 2 to 2.5 hours. So the earlier per-line figures ran low on output (by about half) and new input (about double). The figures below use rights' actual cost per line: about 180 output, 650 new input and 46,000 cached per line for a large part, and about 240, 750 and 48,000 for a small one, whose fixed costs weigh more. In every item, the review and the browser pass each found real bugs.

**This item's size,** with tests, without pack expected files.
- **Part 1, about 8,000 to 9,000 lines:** contract, rules and references about 1,400 and their tests about 900; the sequence and simulation about 900 and their tests about 700; resolved model, keys, tools and packs about 1,150 and tests about 400; editor and mock about 1,900; docs about 400. It is a little larger than rights part 1, mostly in the interpreter and `csharp-dapper`.
- **Part 2, about 3,300 lines:** contract and engine about 700 and tests about 500; simulation about 200 and tests about 250; resolved model and packs about 650 and tests about 150; editor and mock about 700; docs about 150.

**Agents.** Each is capped at about 100 steps or 200k context. Opus 5.5 builds the engine and pack output and runs every review. Sonnet 5.5 builds the editor, the mock and the docs.

**Part 1 agents**

| Wave | Agent | Model | Steps | Starts from |
| --- | --- | --- | --- | --- |
| 1 | Contract and model; **contract first**: the schemas, all `openapi.yaml` changes including the trace, refusal, expectations, records and `get_trigger_sequence`; the members in `Processes/StepTrace.cs`; rule texts. Keeps every change to `Expressions/` and `Validation/ExpressionRules.cs` | Opus | About 100 | `origin/main` (0.16.0) |
| 1 | References, deletes and `TriggerSequence`, through the engine's planner, not the tool files | Opus | About 100 | The contract commit |
| 1 | Editor and mock, part one | Sonnet | About 100 | The contract commit |
| 2 | Simulation and scenarios, through `Processes/ProcessApi.cs` | Opus | About 100 | Wave 1's engine agents |
| 2 | Resolved model, keys, records, `get_trigger_sequence`, replay keys and `process-docs`. **Owns `ProcessTools.cs` and `tools.json`** | Opus | About 90 | Wave 1's engine agents, beside the simulation agent |
| 2 | `csharp-dapper` output | Opus | About 100 | The resolved model agent's first commit (`RTriggerPoint` and `process.trigger_verbs` filled), in the first free slot |
| 2 | Editor and mock, part two | Sonnet | About 100 | Wave 1's editor agent |
| 3 | Review of the pull request | Opus | About 60 | All builders' finish |
| 3 | Docs | Sonnet | About 50 | The builders' finish, beside the review |
| 3 | Browser pass | Sonnet | About 50 | The builders' finish, beside the review |
| 4 | Fixers, split by area (engine and output; editor and mock; docs) | Opus or Sonnet | About 40 each | The review's and the browser pass's findings |
| 4 | A short second browser pass on the fixes | Sonnet | About 25 | The editor fixer's finish |

**Part 2 agents**

| Wave | Agent | Model | Steps | Starts from |
| --- | --- | --- | --- | --- |
| 1 | Contract and engine; contract first | Opus | About 90 | `main` once part 1 is merged |
| 1 | Editor and mock | Sonnet | About 70 | The contract commit |
| 2 | Simulation, resolved model and both packs. Owns the tool files | Opus | About 90 | The contract and engine agent's finish |
| 3 | Review, docs, browser pass | Opus, Sonnet, Sonnet | About 50, 30, 30 | All builders' finish |
| 4 | Fixers, split by area | Opus or Sonnet | About 30 each | The findings |

- **At most three agents run at once.** The `csharp-dapper` agent takes the first free slot in wave 2, as in rights part 1.
- **One pull request per part, into `main`,** with the contract, engine, editor, output, docs and fix commits. Each is its own release. The owner merges part 1 first.
- **Generated files** (the editor's API types, the recorded rule list, the sample app's copies) are written only by the fixed commands, after each merge into the branch. Pack expected files stay with the builder, so their changes are reviewed.
- **Fixers** are fresh agents with the exact findings. **A design gap found while building** stops the work, and the owner is asked.

**Estimate.**

**Part 1**

| | Wall time | Output | New input | Cached re-reads |
| --- | --- | --- | --- | --- |
| Wave 1 (contract, model, references, sequence, editor part one) | About 1.3 hours | 0.45M | 1.6M | 110M |
| Wave 2 (simulation, resolved model and docs output, `csharp-dapper`, editor part two) | About 1.5 hours | 0.6M | 2.1M | 150M |
| Wave 3 (review, docs, browser pass) | About 0.5 hours | 0.25M | 0.9M | 65M |
| Wave 4 (fixes, second browser pass, CI) | About 0.5 hours | 0.25M | 0.9M | 70M |
| In all | **About 3.5 to 4 hours** | **About 1.55M** | **About 5.5M** | **About 395M** |

**Part 2**

| | Wall time | Output | New input | Cached re-reads |
| --- | --- | --- | --- | --- |
| Wave 1 (contract and engine, editor) | About 0.9 hours | 0.25M | 0.8M | 50M |
| Wave 2 (simulation and output) | About 0.8 hours | 0.25M | 0.8M | 50M |
| Wave 3 (review, docs, browser pass) | About 0.6 hours | 0.15M | 0.5M | 30M |
| Wave 4 (fixes, CI) | About 0.5 hours | 0.1M | 0.4M | 30M |
| In all | **About 2.5 to 3 hours** | **About 0.75M** | **About 2.5M** | **About 160M** |

**Both parts:** about 11,500 to 12,500 lines, about 6 to 7 hours, about 2.3M output, about 8M new input and about 555M cached re-reads. The total is over four hours, so it is split. The split costs about 0.5 hours and 40M cached, as it did for rights: a second pull request, review, browser pass, CI run and release.

## Sources

From the study of 20 systems, as the evaluation table cites them. Those marked "checked" were opened again for this design and say what is quoted.

- [S2] ENOVIA V6 docs (mirror), "Types of Triggers" (checked) — https://cjlr.srm.covisint.com/enovia/docv6/English/DteCustomizingMap/dte-c-trigger-design-types.htm
- [S4] 3DEXPERIENCE user assistance (mirror), "Trigger Clause" — https://help-3dexperience.aesvietnam.com/English/MqlReferenceMap/mql-r-relationship-add-triggerclause.htm
- [S7] ENOVIA V6 docs (mirror), "Types of Trigger Programs" (checked) — https://cjlr.srm.covisint.com/enovia/docv6/English/DteCustomizingMap/dte-c-trigger-programs.htm
- [S10] PTC Windchill 12.0.2, multi-object operations (checked) — https://support.ptc.com/help/wnc/r12.0.2.0/en/Windchill_Help_Center/WCCG_Serv_DevelopServerLogic_MultiObjOps.html
- [S14] Aras, "Server Events" (checked) — https://docs.aras.com/server-events-programming-37
- [S15] Aras, "Creating A Transition" — https://docs.aras.com/creating-a-transition-37
- [S18] Salesforce Ben, "Learn Salesforce Order of Execution" — https://www.salesforceben.com/learn-salesforce-order-of-execution/
- [S19] Microsoft Learn, "Event Framework in Microsoft Dataverse" (checked) — https://learn.microsoft.com/en-us/power-apps/developer/data-platform/event-framework
- [S21] Microsoft Learn, "Bypass custom Dataverse logic" — https://learn.microsoft.com/en-us/power-apps/developer/data-platform/bypass-custom-business-logic
- [S23] Atlassian, Jira Server "Advanced workflow configuration" — https://confluence.atlassian.com/adminjiraserver/advanced-workflow-configuration-938847443.html
- [S25] Camunda 8, "Execution listeners" — https://docs.camunda.io/docs/components/concepts/execution-listeners/
- [S31] PostgreSQL, "Overview of Trigger Behavior" — https://www.postgresql.org/docs/current/trigger-definition.html
- [S32] Microsoft Learn, "CREATE TRIGGER (Transact-SQL)" — https://learn.microsoft.com/en-us/sql/t-sql/statements/create-trigger-transact-sql
- [S33] Rails Guides, "Active Record Callbacks" — https://guides.rubyonrails.org/active_record_callbacks.html
- [S36] Jakarta Persistence 3.1, sections 3.5 and 4.10 — https://jakarta.ee/specifications/persistence/3.1/jakarta-persistence-spec-3.1.html
- [S37] Kubernetes, "Dynamic Admission Control" (checked) — https://kubernetes.io/docs/reference/access-authn-authz/extensible-admission-controllers/
- [S39] microservices.io, "Transactional outbox" — https://microservices.io/patterns/data/transactional-outbox.html
- [S40] microservices.io, "Saga" — https://microservices.io/patterns/data/saga.html
