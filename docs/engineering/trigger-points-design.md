# Trigger points: design

**Status (2026-11-08): part 1 built (0.17.0); part 2 next. Designed on 2026-11-07. The fixes from one independent check are applied: five blocking items, and its should-fix, minor and name findings. The fixes from a second independent review are applied too: four blocking, twelve should-fix and eleven minor, then five small ones from its confirmation pass. The owner decided all six questions in section 14 as recommended.** It designs the sixth item in the owner's order after 4.1: trigger points "on every verb, as check, override and action, with the generated step methods". It is built in two parts, each its own release (decision 37). [Who, what and when](lifecycle-actors-scenarios.md) sections 3, 5, 8 and 10 set its direction. [What lifecycle systems can do](lifecycle-capabilities.md), part D, is the yardstick. [The verbs design](verbs-design.md), [the object effects design](object-effects-design.md), [the rights design](rights-design.md) and [the actors design](actors-design.md) give its parts. A study of 20 systems (sources at the end) and the owner's evaluation shaped it. Section 3 tests that evaluation point by point. The check found that signatures were counted, and automatic moves picked, before the checks ran, and that the generated code could write before a refusal; each is settled below. A file path without a project prefix is under `src/Maquettiste.Engine/`. The examples are invented. Each new name and format is held to the roadmap's test: it stays for good, it must help any team whether they use it or not, and it stays optional.

## 1. In short

Today only a move can carry logic. A guard says whether the move is offered. A transition's actions and a state's entry and exit actions run with it. Nothing runs when an object is created, modified, deleted, connected or frozen. Nothing can check a change and say why it is refused, warn without refusing, run after the change is saved, or replace a built-in effect such as deletion.

| Layer | What changes |
| --- | --- |
| The model | A lifecycle lists **trigger points**. Each says what runs when one of its **verbs** happens: a **check** (refuse with a message, or only **warn**), an **action** (**before** the checks, **with the change**, or **after commit**), or a **replace** of the verb's built-in effect, which only a few verbs allow. Optional keys say **where**: in some states, into some states, on some moves, for some types, for some attributes, or for links through some ends. A **`when`** condition and the body read the object as it will be, and its values before the verb through **`old.`**. A trigger point may say it **touches outside systems**. In part 2, a **named trigger point** covers types with no lifecycle, and a bulk load may skip the points not marked **`noBypass`** |
| Simulation and scenarios | Every verb runs in one fixed order: frozen, rights, guards, signatures, then the trigger points: before, checks, the effect or its replacement, with the change, after commit. A move whose check refuses is treated like a move whose guard is false. A refusing check names itself. A step can expect check warnings and the trigger points that ran. A check the team writes in code takes the outcome the step assumes, in the `assume` map guards already use |
| Code generation | Templates read each lifecycle's trigger points and the order they run in. `csharp-dapper` writes step methods per verb and one static method per trigger point for the team, runs them inside the command's transaction (for the per-type runner, the caller's) without writing before the checks pass, and hands after-commit work to the existing outbox. `process-docs` adds "What happens when" per verb |
| The team's system, or a lifecycle system | Nothing changes until the team uses the new output or builds from the model |

**A project that uses none of this** keeps its files, output bytes, findings, replays, tool answers, dependency keys and speed. Existing guards, guard assumptions and actions work as they do today. Section 12 lists everything.

## 2. Scope

| | What | Section |
| --- | --- | --- |
| **Part 1 (release 1)** | Trigger points on lifecycles, for every verb a lifecycle answers for; checks with a severity; actions with a timing; replace on a short list of verbs; the narrowing keys; `when`; `old.`; `outside`; the fixed order, inside the interpreter's selection of moves; simulation, scenarios (check outcomes in `assume`, `expect.checkWarnings`, `expect.triggerPoints`), the trace; `get_trigger_sequence`; the resolved model, both packs and the editor | 4 to 11 |
| **Part 2 (release 2)** | Named trigger points with `subjects` and `exceptEntities`: types with no lifecycle, and one trigger point reused by every type it covers. `noBypass`, the bulk-load bypass and the dry run in generated code | Decisions 20, 33 to 35; sections 5 to 16 |
| **Later, if teams ask** | Queued work as its own timing, and compensation (decision 8); a trigger point that sends an event or does a verb on another object; batches of objects (decision 23); `set-state` and `override` as scenario steps | 13 |
| **Not in this item** | Who may bypass trigger points (the host's permission, decision 20); where data is stored, which bindings and packs decide (decision 17); enforcing `lock` | 13 |

**What part 1 leaves fixed, so part 2 adds only an element kind and the bypass.** Part 1's keys, order, trace and generated step methods stay as they are. Part 2's named trigger point uses the same body and narrowing keys, and its points join the same order (decision 34). The generated per-type runner of part 1 (decision 30) takes part 2's points, and `TriggerPointCall`'s init properties take the bypass and dry-run members, with no change to their shape.

## 3. The evaluation, tested

The owner's evaluation was tested against the study and the code. Most of it holds. Where it doesn't, the evidence is given.

| Point | Verdict | Evidence | Decision |
| --- | --- | --- | --- |
| Check, override and action is the right core: block before, react after | **Agree** | Every one of the 20 systems has a veto before and an action after (research matrix) | 1, 5 |
| A prepare moment before the checks | **Agree**, shaped as the action's **`before`** timing, not a fourth type | Dataverse: "If you want to change any values … you should do it here [PreOperation]" [S19]. Kubernetes runs mutating webhooks first, and "webhooks that need to … see the final state of the object … should use a validating admission webhook" [S37]. Salesforce, Rails and Dataverse register one kind of handler at several stages; they don't make a separate kind for preparing | 7 |
| Check severity: error or warning | **Agree** | Kubernetes: "Warnings can be sent with allowed or rejected admission responses" [S37]. No PLM system studied has one. ENOVIA's "soft block" blocks without an error and keeps the check program's changes [S2] | 6 |
| Action timing: with the change, or after commit | **Agree** | ENOVIA: actions run immediately or deferred until "the outermost transaction" commits [S7]. Aras in-scope and out-of-scope post-methods [S15]. Rails `after_save` and `after_commit` [S33] | 7 |
| Queued work as a third timing | **Disagree** for the model | An outbox writes the message in the transaction, and a relay publishes it later, "if and only if the database transaction commits", possibly "more than once" [S39]. That is the after-commit contract: can't undo the change, may run late, may run twice. Whether a host runs it on the request's thread or a queue can't be seen in the model | 8 |
| Compensation in the first release | **Later** | It needs steps that undo each other across objects (a saga [S40]). The model has no such steps yet, and handling a failed after-commit call is the running system's job (the direction, section 12) | 8 |
| A context contract per verb | **Agree**, and it needs one new thing in the expression language: `old.` | Dataverse images, Salesforce `Trigger.old`, SQL `OLD`/`NEW`, Kubernetes `oldObject` (research 3.2) | 13, 14 |
| Ordering | **Agree**. The list's order in the file is the order; no number | A list in a file is already explicit and has no ties. Camunda 8 runs listeners in model order [S25], and Jira runs post functions in their listed order [S23] | 19 |
| Inheritance through types and stereotype sets, with an opt-out | **Agree** on inheritance. The opt-out sits **on the trigger point**, not on the type | A lifecycle's subject entry already covers derived entities and a stereotype's carriers (stereotype sets). Narrowing in the trigger point keeps "where does this run" in one place, and never changes a type's file. JPA's opt-out sits on the subclass but drops every inherited listener at once [S36] | 12, 33 |
| A bulk-load bypass that needs a right and is logged | **Partly**, and in part 2. `noBypass` is in the model. Who may bypass is the host's permission, not a right | A right speaks of a verb on an object in a state; a bypass is neither. Dataverse's bypass is a system privilege, and its own logic is never bypassed [S21]. Aras events with `is_required = 1` "cannot be ignored" [S14] | 20, 35 |
| A recursion limit | **Agree** for generated code. The model removes the recursion instead | Dataverse: "Avoid applying changes to an entity included in the message because this will trigger a new Update event" [S19]. Salesforce runs flows "only one time per entity, per transaction" [S18] | 21 |
| A declared "touches outside systems" flag | **Agree** | Kubernetes `sideEffects` and `dryRun` [S37]. Simulation never runs the team's code, so the flag matters most for generated tests, dry runs and timing | 22 |
| Per object or per set | **Disagree** for the model: per object, and a pack may batch | Windchill splits a multi-object event for a single-object listener, and wraps a single one for a multi-object listener [S10]. So batching is a property of the runtime, not of the rule | 23 |
| Override should not be a general kind; a narrow replace | **Agree** | PostgreSQL allows `INSTEAD OF` only on views [S31]; SQL Server one per action [S32]; Dataverse's main stage is "for internal use only" [S19]; Aras replace events skip their own before and after events [S14]; ENOVIA's override skips "the normal system event code and history mechanism" [S7] | 16 |
| Replace on create, delete, revise, connect, disconnect and data held outside | **Partly.** Recommended: delete, disconnect, copy and revise | Numbering is a `before` action, as Dataverse and Salesforce do it; ENOVIA needed its separate `precreate` because it had no prepare [S4]. Dataverse's exception is for virtual table *data providers*, a whole data source: in Maquettiste that is bindings and packs. Connect has no removal to soften | 17, owner question 2 |
| Never on governance verbs | **Agree**, and the list grows: also `read`, `modify`, owner, type and lifecycle changes, lock and unlock | Each either changes who may act or is what history and rights rely on | 16 |
| The kind can't be named `override` | **Agree**: `replace` | The verb `override` already means "make a move although its conditions or signatures are not met" (verbs decision 1) | 16 |
| Reuse guards, rights, actions, `sets`, the refusal order and the trace | **Agree**, with one limit: a trigger point's body is inline, not a reference to a guard or an action | Guards and actions read the process's context and event. Most verbs have neither, and are also done outside a run. Rights decision 6 chose an inline condition for the same reason | 3, 4 |

**One departure from the direction.** The direction says automatic moves, timers and deferred triggers act as the system (who, what and when, section 3). Automatic moves and timers still do. After-commit work here carries the original actor instead (decision 13): it finishes a change that actor made, so the team's code can tell who made it, and history names them.

## 4. Decisions

Each decision gives its confidence: **high**, **medium-high**, **medium** or **low**. A decision marked **(part 2)** is built in part 2. The others are part 1.

### Shape and home

1. **A trigger point says what runs when a verb happens at a place.** Confidence: high.
   - It has a `name` (an identifier, unique among the lifecycle's trigger points, MQ3001), an optional `displayName`, a `type`, at least one verb in `verbs`, optional narrowing keys (decision 10), an optional `when`, and a body (decision 3).
   - **The description says why.** A check's description is the message shown when it refuses or warns, as a right's description is shown with every refusal. No second text key. It is localizable. With no description, the message is the display name, else the name, and the editor prompts for one.
   - Each entry of `triggerPoints` is a part of kind **`process-trigger-point`**, as an entry of `rights` is a `process-right`. Part 2's named element is kind **`trigger-point`**.
2. **In part 1, trigger points live on the lifecycle, in one list, `triggerPoints`, after `rights`.** Confidence: medium-high.
   - **Why the lifecycle:** it already holds the moves, states and signatures, and the rights, whose narrowing keys trigger points reuse. Its subjects already name the types, with their derived entities and a stereotype's carriers. One editor tab and one file show what runs.
   - **Why `triggerPoints`, not `triggers`:** a transition already has a `trigger` key (`event`, `after`, `always` …), and databases have triggers of their own in SQL objects.
   - **Only lifecycles hold them** (MQ8101). An orchestration is a flow, not an object's state, and a library is shared parts. An orchestration's event that names an object, link or own verb on its subject still runs the subject's lifecycles' trigger points (decision 11).
   - **Types with no lifecycle** come in part 2, as named trigger points with `subjects` (decision 33). Rights left the same widening to a later item (rights decision 8).
   - **The alternative:** trigger points on entity and stereotype files. Every project's entity file and editor would change, and a move's points would still need the lifecycle. It is not chosen.
3. **The body is inline. A check has a `condition`. An action or a replace has `sets`. With neither, the body is the team's code.** Confidence: medium-high.
   - `condition` is an expression; only true passes. `sets` is the list object effects defined (`{ id, attribute, value }`), with the same rules (MQ9802, MQ9803). Which attributes a set may name follows decision 12.
   - **Why not name a guard or an action:** guards and actions read the process's context and the event. A modify done through an edit screen has neither, so a check that named a guard reading the context could not be run there. Rights decision 6 made the same choice for a right's condition.
   - **What is reused:** the expression language and its typing, the `ProcessSet` shape, the editor's value box and sets editor, and the generated pattern of one team method per body.
   - A trigger point's actions don't change the process's context and don't raise events. Moves keep doing that through their own actions.
4. **What exists keeps its job.** Confidence: high.
   - **Guards** still answer "is this move offered?" They pick the transition. A check answers "is this change acceptable?" once the guard holds, and gives a message. Jira draws the same line: a failed condition hides the move, a failed validator refuses it [S23]. A move whose check refuses is then treated like a move whose guard is false (decision 18).
   - **Rights** still answer "may this actor do this verb here?"
   - **A transition's actions and a state's entry and exit actions** are the with-the-change actions of moves, and run as today. A trigger point adds what they can't say: before and after commit, checks, other verbs, and one point for every move of a verb.
   - **The refusal order** gains one row, and **the trace** two members (decision 28).
   - **A move with no verb runs no trigger points:** an event that names none, and a transition without an event that names none. Trigger points speak of verbs.
   - **A move verb** is a built-in verb of group `move`. An event that names any other verb (an own verb, or `delete`) runs that verb's group rule for which lifecycles answer (decision 11) and that verb's narrowing keys. `targets`, `transitions` and the payload are allowed on a point only when every verb it names is a move verb or an own verb that an event of this lifecycle names (and `targets` and `transitions` beside `sign`, decision 10). On such an own verb, the point matches only when one of those events sends it, so only events run it, never a verb step or the per-type runner.
   - **Operations.** An operation's guards check its input before its handler runs, and raise its own errors. A trigger point's check speaks of the verb on the object, wherever it is done. A team's operation handler that does an object verb through the generated runner (decision 30) gets both: the operation's guards first, then the verb's order. An operation that fires an event runs that move's trigger points once, in the interpreter. The per-type runner refuses move, signature and task verbs, since those go through the process's commands, so they never run twice.

### Types and timings

5. **Three types: `check`, `action` and `replace`.** Confidence: high for check and action; medium-high for replace as a type.
   - They keep the owner's idiom, with `replace` in place of `override` (decision 16).
   - **The key is `type`,** as a state's and an invoke's subtype are, so a part holds no second `kind`.
6. **A check has a `severity`: `error` (the default) or `warning`.** Confidence: high.
   - An error refuses, with refusal `check` and the trigger point as `refusedBy`.
   - A warning lets the step through. The trace and the generated result list it in `checkWarnings`, with its message.
   - There is no "block without an error". A check passes, warns, or refuses with its message.
7. **An action has a `timing`: `before`, `with-change` or `after-commit`.** Confidence: high for the three moments; medium-high for "before" as a timing.
   - **`before`** prepares the change: defaults, numbers, derived values. Its sets fill the proposed values (decision 25), so every check sees the final data. Nothing is written until the checks pass. It can't refuse; refusing is a check's job.
   - **`with-change`** runs in the same unit of work, after the effect. If it fails in the team's system, the verb is undone.
   - **`after-commit`** runs once the change is saved. It can't undo the change, may run late, and may run more than once, so the team's code must be safe to repeat.
   - **Why a timing, not a fourth type:** the systems studied register one kind of handler at several stages (Dataverse stages, Salesforce before and after triggers, Rails callbacks). A fourth type would split one idea in two.
   - `timing` is required on an action and allowed nowhere else (MQ8101).
8. **No queued timing and no compensation in the first release.** Confidence: medium-high.
   - Queued work has the after-commit contract (section 3). A pack delivers after-commit work through an outbox, which is a queue (decision 31).
   - Compensation can come later as an optional key, such as an action that undoes another. It changes no file.
9. **An after-commit action sets nothing** (MQ8105). Confidence: medium-high.
   - Once the change is saved, setting the object is a new change by the system, which would run modify's trigger points again. Setting data belongs before or with the change.
   - A team whose after-commit code writes back, such as an outside system's number, does a new modify in its own code, which runs the trigger points again under the depth guard. The after-commit message carries the depth it was queued at, and the relay starts the count there (decision 21).

### Places

10. **Optional keys narrow where a trigger point runs. With none, it runs wherever its verbs happen to the lifecycle's subjects.** Confidence: high for `states`, `attributes` and `ends` (rights uses them); medium-high for `targets`, `transitions` and `entities`.

    | Key | Place | Runs when | With verbs |
    | --- | --- | --- | --- |
    | `states` | State | The object is in one of these states of this lifecycle (for a move: its source). A compound state counts when any state inside it is active | Any |
    | `targets` | Entering a state | The move enters one of these states. A compound state counts when the move enters it or any state inside it, default states included | Move verbs; `sign`; own verbs sent by this lifecycle's events (decision 4) |
    | `transitions` | Move, signature | The move is one of these transitions; for `sign`, a signature on their gates | Move verbs; `sign`; own verbs sent by this lifecycle's events (decision 4) |
    | `entities` | Type | The object's entity is one of these, or derived from one | Any |
    | `attributes` | Attribute | The step reads or modifies one of these attributes | `read`, `modify` |
    | `ends` | Relation end | The link goes through one of these ends of the subject | `connect`, `disconnect`, `read`, `modify`, `freeze`, `thaw` |

    - **Leaving a state** is `states` on move verbs. **Entering** is `targets`. A state's entry and exit actions stay as they are.
    - **For a move, `states` matches** its source, the active states inside the source, and the compound states that contain the source. Nothing else: another region of a parallel state never counts.
    - **A signature place** is `sign` with `transitions`, since a gate sits on a transition. Reject, ignore, withdraw and delegate join with richer sign-off.
    - **`attributes` matches the attributes the step itself reads or changes,** the actor's own change. Values that before actions add don't make other points match. So matching never depends on the order of preparation, and needs no repeat. Confidence: medium-high.
    - Keys that don't fit their verbs are MQ8102. `attributes` beside `ends` is MQ8102, as for rights.
    - **When the step doesn't say which attributes or end** (an event that names `modify` or `connect`, a `read` step without `attributes`, a per-type runner call without them), a point narrowed by `attributes` or `ends` runs if it is a check, as rights fail closed (rights decisions 20 and 21), and doesn't run if it is an action or a replace. A note in the trace says it assumed so.
11. **Which lifecycles' trigger points run depends on the verb's group, as for rights.** Confidence: medium-high.
    - **Moves, signatures and tasks:** only the lifecycle where the move, signature or task is. Move verbs are as decision 4 defines them.
    - **Objects, links and own verbs:** every lifecycle that governs the object, each reading the object's state there. So a second lifecycle can't get around another's checks, for the reason rights decision 10 gives. This holds when an event names the verb too; only the acting lifecycle's points can then use the move's `targets`, `transitions` and payload (decision 4).
    - Other lifecycles' states come from the scenario, as for rights (rights decision 27).
12. **Types and inheritance.** Confidence: medium-high.
    - A lifecycle's trigger point runs for every type its subjects cover: an entity and its derived entities, or every entity carrying a stereotype and their derived entities (stereotype sets).
    - **`entities` narrows it** to some of those types, each with its derived entities. An entity the lifecycle doesn't govern is MQ8102.
    - **`entities` also widens what the point may read and set.** Without it, `subject.`, `old.`, `attributes` and sets name attributes every subject of the lifecycle holds under one id, as object effects decision 2 has it. With it, they name attributes every entity the point covers holds under one id. So a point for drawings only may set a drawing's own attribute.
    - **The opt-out is narrowing at the trigger point.** A type never lists the trigger points it skips. Part 2 adds `exceptEntities` to a named trigger point, whose `subjects` can be broad (decision 33).

### What each moment reads

13. **Each verb group passes a fixed set of things, the same in simulation, in templates and in generated code.** Confidence: medium-high.

    | Verb group | `subject.` reads | `old.` reads | Also |
    | --- | --- | --- | --- |
    | Move verbs (decision 4): `promote`, `demote` | The object | The object before the move | The source states (`in_state`, `states`), the states it enters (`targets`), the transition, the event's payload |
    | Signatures: `sign` | The object | The same | The transition and its gate; the signer is `current_actor()` |
    | Tasks: `complete`, `claim` | The object | The same | The task's state |
    | `create` | The new object's proposed values | Nothing (null) | |
    | `copy`, `revise` | The new object's proposed values | The object copied | |
    | `modify` | The proposed values | The values before | The attributes changed |
    | `read` | The object | The same | The attributes read |
    | `change-owner`, `change-type`, `change-lifecycle` | The proposed values | The values before | |
    | `connect`, `disconnect` | The object | The same | The end; generated code also passes the other object's key |
    | `delete`, `freeze`, `thaw`, `lock`, `unlock`, own verbs | The object | The same | An own verb sent by an event of this lifecycle: in this lifecycle, the move's states, transition and payload, as for a move (decision 4) |

    - **Before an effect** (before actions, checks, replace), `subject.` is the proposed values, and `in_state` reads the states before the move. **After it** (with the change, after commit), `subject.` is the object after the verb, and `in_state` reads the states after the move.
    - **States on `create`, `copy` and `revise`.** `create` reads the lifecycle's initial state, as rights do, in `states` and `in_state`; the per-type runner answers it without asking `StatesIn<Lifecycle>`. `copy` and `revise` read the source object's states, and `old.` is the source.
    - **The payload** is read only when every verb the point names is a move verb or an own verb an event of this lifecycle names (decision 4), and every event the point can run on declares that attribute, as an action's sets read it today. Otherwise MQ3102.
    - **The context is never read** (MQ3102, as at a right's condition). A trigger point must run wherever the verb is done, and an edit outside a run has no context.
    - Every expression reads `current_actor()`, `now()` and `in_state`, as a guard does.
    - **Who it runs as.** A trigger point runs as part of the verb. `current_actor()` is the acting participant, or the system actor for an automatic move, as for a move's sets (object effects decision 5). Its sets are part of the verb, like a move's sets: attribute rights and freezing are checked on the actor's own step, not on what a trigger point sets. After-commit work carries the original envelope: the actor, the signer and the held actors (section 3 notes where this departs from the direction).
14. **`old.` reads the object's values before the verb.** Confidence: high that it is needed; medium-high for its shape.
    - In text, `old.price`. In the tree, a path with `"old": true`. To-one paths follow the same rule as `subject.`.
    - **It is special only at trigger point sites.** Elsewhere, text that starts with `old` keeps the meaning it has today, and the tree's `old` flag is refused there (MQ3104).
    - "The price may not drop by more than a tenth" is `coalesce(old.price, 0) = 0 or subject.price >= old.price * 0.9`. Without the first part, an empty old price would read unknown and fail.
15. **Two new site kinds: `Trigger` (a `when` or a check's condition, judged as a guard) and `TriggerSet` (a set's value, typed against its attribute).** Confidence: high.
    - Each reads what decision 13 says. They are handled explicitly at every place the `Set` kind needed (object effects decision 3), plus the new `old` node. SQL refuses both.
    - **`when` holds only when true.** False or unknown means the trigger point doesn't run, and the trace says it was skipped.

### Replace

16. **A replace takes the place of a verb's built-in effect. Everything around it still runs.** Confidence: high.
    - Rights, guards, before actions and checks run before it. With-the-change and after-commit actions run after it.
    - **History is still recorded.** The trace records the verb as done. In generated code, the per-type runner writes a `<Entity>VerbDone` record to the outbox, on the built-in path and the replace path alike, for the team's history (decision 31). Today generated code writes history only for moves (`<P>Transitioned`). ENOVIA's override skips "the normal system event code and history mechanism" [S7], so a team must add history back by hand. This design doesn't copy that.
    - **At most one replace can apply to one verb on one entity,** in one lifecycle or across the lifecycles that govern it, whatever their `states` and `when` (MQ8104).
    - **Never on governance verbs,** which history, rights and signatures rely on: every move, signature and task verb, `freeze`, `thaw`, `lock`, `unlock`, `change-owner`, `change-type`, `change-lifecycle`, `read` and `modify`. Own verbs have no built-in effect to replace. A replace on any of these is MQ8103.
    - **A replace never takes the place of a move.** An event may name any verb (verbs decision 14), so an event can name `delete`. Its effect is still its move. The event runs the verb's other trigger points, but no replace.
    - **The body states the replaced effect:** `sets` the run applies, such as `archivedOn` set to `now()`, or the team's code.
    - **With-the-change sets on `delete` need a replace.** After a built-in delete there is no object to set. So a with-change action with `sets` on `delete` is MQ8101 unless a replace on `delete` covers every entity the point covers. On a verb step where that replace doesn't run (its `when` or places don't match), the action is skipped, traced `skipped`, and one note says so.
    - **An event that names `delete` still does its move** (owner's answer). The replace stands in only for a verb step's effect, so it never runs there, and the object stays. `delete`'s checks and its other actions still run, with-change sets included, traced `ran`.
    - **A replace on `copy` or `revise` sets the new object.** The run doesn't hold it, so the trace shows its sets and nothing is written (decision 25).
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

18. **Every verb runs in one fixed order. For a move, the trigger points run while the move is picked, right after its guard, so a refusing check works like a false guard.** Confidence: high for the order; medium-high for where it runs. Rows 1 to 7 are rights decision 12, unchanged.

    | Order | Step | Refusal |
    | --- | --- | --- |
    | 1 to 5 | Frozen, closed lists, denies, defaults, no move | As today |
    | 6 | Guards, as today, for each candidate move | `guard` |
    | 7 | Signatures: who may sign, as today, on a gated move | As today |
    | 8 | **Before actions**, which fill the proposed values. During selection they work on a scratch copy | None |
    | 9 | **Checks**: errors refuse the candidate; warnings are listed | `check` (new) |
    | 10 | **The effect**: the built-in one (for a move, its exit, transition and entry actions, as today; for `sign`, counting the signature), or the replace. The proposed values are written first | None |
    | 11 | **With-the-change actions** | None |
    | 12 | **After-commit actions**, once the step's whole change is done | None |

    - **Moves, during selection.** For each candidate transition, `Select` and `SelectAt` test the guard, then, on a gated move, who signs, then run rows 8 and 9 on a scratch copy of the object's values. A refusing check leaves the candidate out, exactly as a false guard does, and the next candidate is tried. So an `always` move whose check refuses leaves no candidate, and `RunToCompletion` can't loop. A refused candidate is tried again only when `RunToCompletion` selects again after another microstep, as a false guard is, so its check may run again; decision 28 says what the trace lists. When no candidate is left, the event is refused with the most telling reason: a check outranks a gate, which outranks a guard. Timers fire through `HandleTime`, which tests the guard itself; it runs rows 8 and 9 there too.
    - **On an automatic move** (`always`, `after`, `done`, `invoke-done`, `invoke-error`), a refusing check means the move isn't taken. The trace names the check. The step that led to it is still accepted. Rights left this to trigger points (rights decision 23). An `invoke-done` or `invoke-error` step is the one case where the move is the step's own: with no transition left, the step is refused, as it is today when the guard is false, and the task stays pending (section 8).
    - **A gated move.** Today a signature is counted when the move survives conflict removal (`Sign`, called from `RemoveConflicts`). The trigger points of `sign` run between the signer check (`CheckSigner`) and the count. For the occurrence that would complete the gate, selection first works out that it would complete, without counting, and also runs the move's before actions and checks. Only when all pass is the signature counted and the move made. When a check refuses, the signature is not counted, and it is audited as `refused`, an outcome the gate audit already has. (Owner question 4.) **`sign`'s own points run as a verb of their own around the count,** on every signature, whether or not the gate completes. Its before actions and checks run during selection. When the occurrence is admitted, its before values are written. Then the count is made, `sign`'s with-change points run, and its after-commit points are queued. When the gate completes, the move's rows 10 to 12 follow in `Microstep`. **On the completing occurrence, `sign`'s before actions and checks run first, then the move's,** on the same scratch copy, so the move's checks see what `sign` prepared. `sign`'s points run where the count is made today, in the admit. In the rare case where conflict removal later drops a transition it had admitted (a transition from a state inside its source), the signature stays counted, as it does today, and `sign`'s points have run with it.
    - **A point runs at most once per microstep.** When it matches several selected transitions (parallel regions), its checks must pass for each, its before sets are applied once, from the first of them in selection order, and the trace lists it once. A point that runs in two microsteps of one step is listed twice, in order. When the before actions of two selected transitions set one attribute, the later transition in selection order wins, as a later action's set does today. No rule notes it: MQ9506 speaks of two transitions of one source, which are never selected together.
    - **An event that takes several transitions** is refused only when no transition is left, as today. A transition whose check refuses is simply not taken.
    - **Verbs that aren't moves** (verb steps, and the per-type runner) have no selection: rows 8 to 12 run in order after rights.
19. **Within one candidate (a transition being picked, or a verb step), all lifecycles' points for a moment run before the next moment. Within a moment, the acting lifecycle comes first, then the others by name, each in its list's order.** Confidence: high.
    - So every check sees every lifecycle's preparation for that candidate. When parallel regions select several candidates, each runs its own before actions and checks in turn.
    - **A completing signature is two verbs in one candidate.** `sign`'s before actions and checks run first, then the move's, each in this decision's order (decision 18).
    - No `order` number. The list's order is explicit, has no ties, and the editor reorders by dragging.

### Operational controls

20. **(Part 2) `noBypass: true` means no bulk load ever skips the trigger point. Who may bypass the others is the host's permission.** Confidence: medium.
    - The model states only what must always run. Simulation has no bypass: every scenario runs every trigger point.
    - **Why part 2:** the key means something only once generated code can bypass (decision 35). A key with no effect would invite the question "what does it do?".
    - **Why not `required`:** a gate's `required` is its count of signatures, and an attribute's means "not empty".
    - **Why not a right:** a right is a verb on an object in a state. A bypass is a permission over how the running system writes, like who is signed in (the direction, section 12). Adding a built-in verb for it would be permanent (verbs decision 5).
21. **The model has no trigger recursion. Generated code guards against the team's own.** Confidence: medium-high.
    - A trigger point's sets are part of the verb that ran it. They never run `modify`'s trigger points, as a move's sets don't.
    - A trigger point raises no events and does no verb on another object in this item. So nothing in the model can call a trigger point from a trigger point. Moves keep the existing bound on microsteps (MQ9507), and a refusing check never counts as a microstep, since it leaves the move out (decision 18).
    - Generated code counts nested runs. When the team's code inside a trigger point runs verbs more than 8 deep, the command fails, so the change is undone. Each after-commit message carries the depth it was queued at, and `TriggerPointRelay` starts the count there, so a chain from after commit to a modify to after commit stops too.
22. **`outside: true` says the team's code reaches outside systems.** Confidence: medium-high.
    - Simulation never runs the team's code, so it touches nothing. The trace marks such points with `outside: true` (decision 28). Only the team's code can reach outside, so `outside` on a point with no team code is MQ8101.
    - Generated scenario tests record outside points instead of calling them (decision 31). Part 2's dry run does the same (decision 35).
    - **A with-the-change action that touches outside systems is warned** (MQ8106): a rollback can't undo the outside call, so it usually belongs after commit. A `before` one is not warned, since asking an outside system for a number is a common way to prepare.
23. **A trigger point runs once per object. A pack may batch.** Confidence: medium-high.
    - Scenarios act on one object, and the model's meaning is per object. Windchill shows the runtime can convert between the two [S10].
    - A key for per-set rules can come later, if teams ask. It changes no file.

### Simulation and scenarios

24. **Trigger points run in simulation and replay, for every input:** events, automatic moves, timers, signatures, task completions and verb steps. Confidence: medium-high.
    - They run after the existing checks (decision 18), in a project with or without rights.
    - In a scenario with no objects, the lifecycles that govern every subject of its process run theirs, each in its initial state, as rights do. A point narrowed by `entities` runs there only when its `entities` cover every subject entity of the process. Otherwise it doesn't run, and a note says so, as rights don't ask a lifecycle that governs only some subjects.
    - An invoked sub-process sees no object, so it runs no trigger points.
25. **Before actions fill the proposed values. They are written only when the step is accepted.** Confidence: high.
    - The proposed values start as the object's row, with a `modify` step's values or a `create` step's values on top. Each before action's sets apply to them in turn. Checks read them.
    - On refusal they are dropped. On acceptance they are written before the effect, so a move's own actions see them.
    - **On `copy` and `revise`,** the proposed values are the new object's, which the run doesn't hold. The trace shows them, and nothing is written.
    - Within one trigger point, every value is worked out first, then all are applied, as for an action's sets (object effects decision 4).
    - **A trigger point's sets are part of its verb, so freezing never stops them,** on a move or any other verb (a with-change set on a `freeze` step too). A note is added when the object is frozen, as for a move's sets (rights decision 17).
26. **The step's existing `assume` map also takes check outcomes.** Confidence: high.
    - Today `assume` maps a guard with no expression to `true` or `false` (`schemas/v1/scenario.json`, `Model/Processes.cs`, `step.assume`). That stays exactly as it is.
    - It now also maps a check with no `condition`, a check the team writes in code, to `pass`, `warning` or `error`. Each value is a boolean (a guard) or one of those three words (a check).
    - **Boolean entries stay exactly as today:** their keys are guard ids and references to their guards, MQ2001 flags one that names nothing, and deletes treat them as they do now. A boolean keyed to a check means nothing and is ignored. **Only a word entry is new.** MQ9305 covers a word whose key is not a team-code check of a lifecycle the step reaches, and a word for a check that has a `condition`. Word keys are trigger point ids and references to their trigger point, as boolean keys are to guards, so when the check is deleted, `remove-references` drops the entry, and one that names nothing is MQ2001.
    - **Names stay unambiguous.** The resolved `step.assume` is keyed by name, as today. MQ3001 makes names unique across a process's guards and trigger points together, so a guard and a check never share a key. Its new case needs a trigger point, so only a project that uses trigger points can get it. A word entry for another lifecycle's check is keyed by both names, such as `HR.SalaryBand`, so checks of two lifecycles never collide either. Process names are unique only within a package, so when two lifecycles the step reaches share a name, each one's checks are keyed by the lifecycle's id in place of its name.
    - Without an entry, such a check passes, and the trace shows it as `passed` with `assumed: true`. An action written as the team's code changes nothing in a run, and the trace shows it as `ran` with no sets.
    - This is the stub the evaluation asked for. It lets a scenario prove the path where the team's check refuses. The generated test then requires the team's code to give that outcome, so the assumption becomes an acceptance test.
27. **An accepted `create` step writes its proposed values to the subject's row. A `create` step may give `values`.** Confidence: medium-high.
    - The scenario's subject stands for the object created. So a number or default prepared on create is seen by later steps.
    - Today `create` takes no values and writes nothing, so its outcomes don't change. A refused create still leaves the subject in place, as today.
28. **The trace and the expectations.** Confidence: high for the shape; medium-high for comparing in order.
    - The trace gains **`triggerPoints`**: each trigger point whose places matched, in run order, with its lifecycle, verb, type, timing, outcome (one of `passed`, `warned`, `refused`, `ran`, `replaced`, `queued`, or `skipped` when `when` was false, or for a with-change action whose sets have no object after a verb step's built-in `delete`, decision 16), two flags, and the sets it applied. The flags are `assumed: true`, for a team-code check whose outcome came from `assume` or its default, and `outside: true`. An assumed warning is `warned` with `assumed: true`, and it is in `checkWarnings`. An outside after-commit point is `queued` with `outside: true`. A team-code action is `ran` with no sets. It gains **`checkWarnings`**, the ids of checks that warned, each once per step, in the order they first warned. The name keeps clear of a verify result's `warnings`, which holds findings.
    - A refused step has refusal **`check`** and the trigger point's id as `refusedBy` (MQ9308). `refusedBy` is written today only in a project that uses rights; it is now written in a project that uses rights or trigger points. Everything that reads "uses rights" for refusals today reads "uses rights or trigger points": in the engine, `ScenarioReplay.UsesRights` (`Processes/ScenarioReplayer.cs`), which the trace's `refusal` and `refusedBy` and recording read; in `csharp-dapper`, the members section 10 lists.
    - A step may expect **`checkWarnings`** (exactly those ids) and **`triggerPoints`** (exactly the ids that ran, in order, repeats included, `skipped` ones left out). A difference is MQ9310.
    - **Which points `triggerPoints` lists.** Per microstep, the points of the transitions that microstep takes, in decision 19's order; for a verb step, its points in run order. When a step is refused by a check, it adds the refusing check once. Checks run on candidates that weren't taken are not listed, and their warnings are dropped. A refused check that a later loop passes is listed with the microstep that took its move. `sign`'s points, which run in the admit rather than in a microstep, and a task's `complete` points are listed where section 8 runs them, in run order. On a completing signature that is `sign`'s before actions and checks, the move's before actions and checks, `sign`'s with-change points, then the move's microstep points. On any other signature it is `sign`'s points alone. After-commit points come last, as queued. Generated code follows the same text.
    - **What recording writes** (owner question 5): `record_scenario` writes `refusal`, `refusedBy` and `checkWarnings` in a project that uses trigger points, each only when not empty. It writes `triggerPoints` only when asked, since a new trigger point would otherwise break every recorded scenario.

### Code generation

29. **Each output depends on what it needs, so a project pays only for what it uses.** Confidence: high.
    - **A project uses trigger points** when a lifecycle has `triggerPoints` (part 2: or a named trigger point exists). Only then are step methods, the runner, the new refusal and result members written, and only then does recording write the new expectations.
    - **One naming key, `tn:trigger-points`, hashes every lifecycle that has a trigger point:** its id, every verb its points name, and its replaces with their verbs and entities (section 7). Its hash never changes in a project without trigger points, as `rn:rights` doesn't without rights.
    - **`model.trigger_lifecycles`** lists those lifecycles, by name then id, and keys only on `tn:trigger-points`. The shared generated units (the interpreter, the dispatcher) ask it whether to write `Refusals.Check`, `CheckWarnings` and the refusal members of section 10, as they ask `model.rights_lifecycles` about rights (`interpreter.scriban`). So no shared unit reads every process file.
30. **`csharp-dapper` writes step methods per verb, and one static method per trigger point for the team.** Confidence: medium-high.
    - **Step methods,** in a generated class `<Lifecycle>TriggerSteps`: `Before<Verb>`, `Check<Verb>`, `With<Verb>` and `After<Verb>` for each verb that has points, and `Perform<Verb>` where a replace is. Each calls the matching points in order (decision 19), testing their places and `when`.
    - **The team's methods are static, and take a `TriggerPointCall`,** like a right's condition. Each is named after its trigger point, in one of three static partial classes per lifecycle: `<Lifecycle>TriggerPoints` (the body), `<Lifecycle>TriggerConditions` (the `when`) and `<Lifecycle>TriggerRecords` (writing a before action's values once the checks pass). A trigger point's name is unique in its lifecycle (MQ3001), and the step methods live in another class, so no two methods collide. A point named like the class its method goes in, such as `DocumentTriggerPoints` in lifecycle `Document`, would give a method named like its class, which C# refuses. The pack's existing rule for a member named like its class (`member_name` in `_process.scriban`) then adds `Value` to the method's name. No engine rule knows the pack's class names. The per-type runner calls them without creating handler objects.
    - **Which methods a point gets,** by type and timing:

    | Type and timing | Body method | When method | Records method |
    | --- | --- | --- | --- |
    | Check | yes | only with `when` | no |
    | Action, `before` | yes | only with `when` | yes |
    | Action, `with-change` | yes | only with `when` | no |
    | Action, `after-commit` | yes | only with `when` | no |
    | Replace | yes | only with `when` | no |

    Every point has a body method, since generated code can't read the object until 4.6; with a `condition` or `sets`, the method's comment carries their text, and the team writes it from that. A when method exists only where the point has a `when`. Only a before action has a records method: nothing may be written until every check passes, so its body only fills `Proposed`, and its records method writes those values afterwards. The other moments run after the checks, so one method does the work. Whether a point has `sets` changes no cell: a before action's records method writes whatever its body filled, from `sets` or the team's code.
    - **Synchronous, but for after commit.** The methods the generated interpreter calls (`when`, checks, before, records, with the change, replace) are synchronous, like guards and right conditions today, and the interpreter stays synchronous. Only the after-commit method returns a `Task`, and `TriggerPointRelay` calls it, after the command. A check that needs an outside system calls it synchronously from its method, as a guard's code does today.
    - **How the interpreter reaches them.** The interpreter is one shared runtime. `<P>Definition` (`process-definition.scriban`) passes a `<Lifecycle>TriggerSteps` instance as `StatechartDefinition.TriggerSteps` (null without points), the same way it passes `ProcessRights` as `StatechartDefinition.Rights`. `<Lifecycle>TriggerSteps` implements `ITriggerPointSteps`, which the shared runtime declares, so the interpreter calls a verb's step methods without knowing the lifecycle.
    - **Bodies and `when` go to the team,** documented with their text, since generated code can't read the object until the generated runtime (4.6). A new one fails the build until written, as a right's condition does.
    - **Moves, signatures and tasks** run them inside the generated interpreter, in the order of decision 18.
    - **Object, link and own verbs done outside an event** go through a per-type runner, `<Entity>Verbs`, written for each entity whose governing lifecycles have such points, whatever their `entities`; the runner tests `entities` at run time. It takes the `IProcessOutbox` and writes its after-commit messages and `<Entity>VerbDone` itself. The caller must call it inside its own transaction, and the generated comment says so. The team calls it from its own handlers, as rights decision 30 has the team call `Rights.Check`. It runs freezing and rights when those are written, then every governing lifecycle's steps, asking `StatesIn<Lifecycle>` for other lifecycles' states, as rights' hook does. It refuses move, signature and task verbs (decision 4).
31. **Timing maps onto the pipeline the pack already writes. Nothing is written before the checks pass.** Confidence: high.
    - Generated commands already run in a unit of work (`TransactionBehaviour`) that ends by writing the result's events to `IProcessOutbox`, inside the same transaction (`OutboxBehaviour`). It commits whatever the handler returns, a refused result too. So a refused command must have written nothing of the change. It saves only what a refused command saves today: the snapshot and its audit, when the step audits something (`SaveAsync` in `process-handlers.scriban`), now including a signature audited as `refused`.
    - **The per-type runner has no such pipeline:** generated operation handlers have neither a transaction nor an outbox. So `<Entity>Verbs` takes the `IProcessOutbox`, writes its after-commit messages and `<Entity>VerbDone` itself, and is called inside the caller's own transaction (decision 30). The three promises below rest on `TransactionBehaviour` and `OutboxBehaviour` for process commands, and on that transaction for the per-type runner.
    - **Before actions fill `TriggerPointCall.Proposed`,** a set of values by attribute, and write nothing. Checks read `Proposed`. Only when every check passes does generated code call each before point's `<Lifecycle>TriggerRecords` method, which writes its values, and then the effect. The interpreter and the runner keep that order alike.
    - **The effect, or the replace, and with the change** run in the same unit of work. When the team's code throws, the command fails, `TransactionBehaviour` doesn't commit, and the change is undone. In the per-type runner, the exception passes through `RunAsync` to the caller, whose transaction rolls back.
    - **After commit** becomes one `TriggerPointMessage` per due trigger point, added to the result's events (the per-type runner writes them to the outbox itself). So the outbox stores it only if the change commits. The team's relay delivers it later, through a generated `TriggerPointRelay`, to the team's method. The message holds no team objects, only what the call needs: the trigger point, the verb, the lifecycle, the entity, the object's key, the instance (none outside a run), the envelope (the actor, the signer and the held actors), the transition and the states entered (moves), the event and its payload (moves), the end and the other object's key (links), the attributes changed, the proposed values as written, the depth it was queued at (decision 21), the `CommandId` (the per-type runner) and its `MessageId`. The relay rebuilds a `TriggerPointCall` from them, with `Old` and `New` null. Each message has a stable `MessageId`: in a process command, the instance, the version the command saves (unique per saved command, by the store's version check), the trigger point and its place in the command; in the per-type runner, a `CommandId` the caller gives, the object's key, the trigger point and its place. The team can drop a repeat by it.
    - **History of object verbs:** `<Entity>Verbs` writes a `<Entity>VerbDone` record (the entity, the key, the verb, the actor, the time, and the replace that ran, if any) to the outbox, on both paths (decision 16).
    - **No bypass and no dry run in part 1** (owner question 6). They come in part 2 (decision 35).
32. **`process-docs` writes "What happens when" per verb.** Confidence: high. Section 10.

### Types with no lifecycle, and the bypass (part 2)

33. **(Part 2) A named trigger point is its own element, kind `trigger-point`, in `model/trigger-points/`. Its `subjects` name entities and stereotypes; its `exceptEntities` name entities it skips.** Confidence: medium-high.
    - It runs for every object whose entity a subject covers (an entity and its derived entities; a stereotype's carriers and theirs), with or without a lifecycle. That is the type place for types such as a customer, which have no lifecycle and still need a number on create or an archive on delete.
    - Its keys are an inline point's, but for `states`, `targets` and `transitions`, which belong to one lifecycle (MQ8102). A state elsewhere is read with `in_state` in `when`. What it may read and set is what every covered entity holds under one id (decision 12).
    - **`exceptEntities` is the opt-out,** written where the rule is, so "every approvable type but memos" is one file. It is not `excludes`, which operations use for the stereotype errors they don't inherit.
    - `subjects` is the key rights reserved for named rights on types (rights decision 8), so a later item can give both one control in the editor.
34. **(Part 2) Type points run before lifecycle points, at each moment.** Confidence: medium.
    - Among type points: the one whose subject is the farthest base runs first, as JPA runs superclass listeners first [S36]. At one depth, those brought by an entity come before those brought by a stereotype, then by name.
    - So the order runs from the outside in: type, then lifecycle, then the list's order within it.
35. **(Part 2) Generated code gains the bulk-load bypass and a dry run.** Confidence: medium.
    - **Bypass:** `TriggerPointCall.SkipOptional` is honoured only when the host's `ITriggerBypass` hook allows it. Its team-owned stub, written once, refuses. Each skip is logged through the hook. Points with `noBypass` always run (decision 20).
    - **Dry run:** `TriggerPointCall.DryRun` runs freezing, rights, before actions and checks, stops before the effect, and rolls its unit of work back, so a screen can ask "would this be accepted, and with which warnings?". Outside points are recorded, not called.

### Tools

36. **One new tool, `get_trigger_sequence`: what runs, in order, when a verb happens in a lifecycle.** Confidence: medium-high.
    - It takes a lifecycle and a verb, and optionally a state, an entity, attributes, an end and a transition. It answers the steps of decision 18, each trigger point with its lifecycle, moment and places, and the built-in rows that apply.
    - It reads the snapshot and never resolves the whole model, as `get_rights` does. The editor's "What happens when" view and the docs pack's lists use the same evaluator, so they never disagree.

### Release

37. **Two parts, each a minor release, and a team upgrades together.** Confidence: high, by the owner's 4.1 decision 6.
    - Part 1 holds everything a lifecycle needs and is the part a review can hold. Part 2 adds the element kind, its editor, its place in the runner, and the bypass and dry run.
    - An older engine refuses a file with `triggerPoints`, an `old` path, a text value in `assume`, `values` on `create`, the new expectations, or a `trigger-point` element. `formatVersion` stays 1.

## 5. Schema and files

**Part 1**

| File | Key | Type | Notes |
| --- | --- | --- | --- |
| `common.json` | `$defs/triggerBody` (new) | `type`, `verbs`, `timing`, `severity`, `when`, `condition`, `sets`, `outside`, `entities`, `attributes`, `ends` | `type`: `check`, `action` or `replace`. `timing`: `before`, `with-change`, `after-commit`. `severity`: `error` or `warning`. `when` and `condition`: `$defs/expr`. `sets`: as an action's. `outside` defaults to false |
| `common.json` | `$defs/expr` path gains `old` | boolean | Decision 14; only at trigger point sites |
| `process.json` | `triggerPoints` (after `rights`) | list of `{ id, name, displayName?, description?, …triggerBody, states?, targets?, transitions? }` | Decisions 1 to 12. Part kind `process-trigger-point` |
| `scenario.json` | step `assume`: each value is a boolean or `pass`, `warning` or `error` | | Decision 26. Booleans for guards, as today; words for checks |
| `scenario.json` | step `values` allowed with `create` | | Decision 27 |
| `scenario.json` | step `expect.checkWarnings`, `expect.triggerPoints`; `expect.refusal` gains `check` | id lists | Decision 28 |

**Part 2**

| File | Key | Type | Notes |
| --- | --- | --- | --- |
| `trigger-point.json` (new kind) | `kind`, `id`, `name`, `displayName`, `package`, `description`, `subjects`, `exceptEntities`, and `triggerBody` | `subjects`: a non-empty list of `{ entity }` or `{ stereotype }`. `exceptEntities`: entity ids | Decision 33. Folder `model/trigger-points/` |
| `common.json` | `triggerBody` gains `noBypass` | boolean, default false | Decision 20 |

Also updated in each part: the sample app's schema copies, `openapi.yaml` by hand (the shapes, the trace, the simulate and record requests, the records, `get_trigger_sequence`), and the editor files generated from it.

```json
"triggerPoints": [
  { "id": "…", "name": "AssignNumber", "type": "action", "timing": "before", "verbs": ["create"],
    "description": "Gives the document its number, such as DOC-000123." },
  { "id": "…", "name": "TitleBeforeReview", "type": "check", "verbs": ["promote"], "targets": ["<id of Review>"],
    "condition": { "text": "coalesce(subject.title, '') <> ''" },
    "description": "A document needs a title before it goes to review." },
  { "id": "…", "name": "LargePriceDrop", "type": "check", "severity": "warning", "verbs": ["modify"],
    "attributes": ["<id of price>"],
    "condition": { "text": "coalesce(old.price, 0) = 0 or subject.price >= old.price * 0.9" },
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

As text, conditions and values are stored as trees of ids. `AssignNumber` and `PublishToPortal` are the team's code. `CreditLimit` is a check the team writes, so a scenario assumes its outcome, in the same map that already assumes guards:

```json
"steps": [
  { "id": "…", "input": "verb", "verb": "create", "participant": "<p1>", "values": { "<id of title>": "" } },
  { "id": "…", "input": "event", "event": "<id of Submit>", "participant": "<p1>",
    "expect": { "accepted": false, "refusal": "check", "refusedBy": "<id of TitleBeforeReview>" } },
  { "id": "…", "input": "verb", "verb": "modify", "participant": "<p1>", "values": { "<id of price>": 80 },
    "expect": { "values": { "<id of price>": 80 }, "checkWarnings": ["<id of LargePriceDrop>"] } },
  { "id": "…", "input": "verb", "verb": "connect", "participant": "<p1>", "end": "<id of end orders>", "object": "<o2>",
    "assume": { "<id of CreditLimit>": "error", "<id of a guard with no expression>": true },
    "expect": { "accepted": false, "refusal": "check", "refusedBy": "<id of CreditLimit>" } },
  { "id": "…", "input": "verb", "verb": "delete", "participant": "<p1>",
    "expect": { "triggerPoints": ["<id of Archive>"], "values": { "<id of archivedOn>": "2026-11-07T09:00:00Z" } } }
]
```

The scenario object's price is 100, so the drop to 80 warns. The archived document stays in the run, so later steps still reach it. The last step's `triggerPoints` was written on request (decision 28).

Part 2:

```json
{ "kind": "trigger-point", "id": "…", "name": "CustomerNumber", "type": "action", "timing": "before", "verbs": ["create"],
  "subjects": [ { "entity": "<id of Customer>" } ], "noBypass": true,
  "description": "Gives each customer its number, such as C-00042." }
```

## 6. Rules

The process families MQ90xx to MQ99xx are full. Trigger points take the next free family, **MQ81xx**, labelled "Trigger points".

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ8101 | E | A trigger point's shape: `timing` missing on an action or beside another type; `severity` beside a type other than check; `condition` on an action or a replace; `sets` on a check; `outside` on a point with no team code (a check with a `condition`, or an action or replace with `sets`); a with-change action with `sets` on `delete` that no replace on `delete` covers (decision 16); `triggerPoints` on an orchestration or a library | Remove the key |
| MQ8102 | E | A narrowing key that doesn't fit: `states` or `targets` that aren't this lifecycle's states; `transitions` that aren't its transitions; `targets` or `transitions` beside a verb that isn't a move verb, `sign` or an own verb an event of this lifecycle names (decision 4); `attributes` beside a verb other than `read` and `modify`, or beside `ends`; `ends` beside a verb that can't act on a link; `attributes`, `ends` or set attributes not held under one id by every entity the point covers (decision 12); `entities` the lifecycle doesn't govern. Part 2 adds: `states`, `targets` or `transitions` on a named trigger point; `exceptEntities` that no subject covers | Remove the entry |
| MQ8103 | E | A replace on a verb that doesn't allow it (decisions 16 and 17) | Make it an action |
| MQ8104 | E | Two replaces that can apply to one verb on one entity, in one lifecycle or across the lifecycles that govern it (part 2: or named trigger points). `states` and `when` are not considered, so two `delete` replaces in different states are refused too. This is on purpose: whether two conditions can both hold can't be known before a run, so the rule never guesses | Narrow one with `entities` |
| MQ8105 | E | An after-commit action with `sets` | Make it a with-change action |
| MQ8106 | W | A with-change action that touches outside systems: a rollback can't undo the outside call | Make it after commit |
| MQ8107 | W | A trigger point that can never run: a move verb that no event or transition of the lifecycle names, or `transitions` none of whose moves has the verb; or verbs that are all verbs no input can do yet (`override`, `set-state`, `claim`, `reject`, `ignore`, `withdraw`, `delegate`, `route`), unless an event names one | Remove it |
| MQ9310 | E | A scenario step whose check warnings or trigger points differ from `expect.checkWarnings` or `expect.triggerPoints` | Update the expectations from a replay |

**Existing rules that gain a case.** No finding changes for a model that uses none of this: every new case needs a trigger point, an `old` path at a trigger site, a text value in `assume` or `values` on `create`. Boolean entries in `assume` stay unchecked, as today.
- **MQ3001:** two trigger points of one lifecycle with one name, or a trigger point named like a guard of its process (its libraries' guards included), so the resolved `step.assume` stays unambiguous (decision 26). Each case needs a trigger point.
- **MQ3101, MQ3102, MQ3108 and the typing rules:** `when`, `condition` and set values; MQ3102 for the context read at a trigger point site, as at a right's condition, for a payload attribute some event lacks, or for a payload read by a point whose verbs aren't all move verbs or own verbs an event of its lifecycle names (decision 4).
- **MQ3104:** the tree's `old` flag at any site other than a trigger point's.
- **MQ9701, MQ9704:** a verb named by a trigger point.
- **MQ9802, MQ9803:** a trigger point's sets, as an action's.
- **MQ9305:** in `assume`, a word whose key is not a team-code check of a lifecycle the step reaches (a guard's key included), or a word for a check that has a `condition`; `values` beside `create` that a set couldn't set. Boolean entries stay unchecked.
- **MQ9308:** the refusal `check`.

The mock backend's rule list follows.

## 7. Resolved model and templates

```csharp
public sealed class RTriggerPoint : RObject                     // Kind "process-trigger-point"; part 2: "trigger-point" for a named one
{
    public string Name, DisplayName, Description;
    public string Message { get; internal set; } = "";          // the description, else the display name, else the name
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
    public bool Outside, TeamCode;                              // TeamCode: no condition (check) or no sets (action, replace)
    public RList<REntity> Covers { get; internal set; }         // the entities it runs for, in name order
    public RProcess? Process { get; internal set; }             // null for a named one (part 2)
    // Part 2 adds NoBypass, Subjects and ExceptEntities (named only) and Package.
}
public sealed class RTriggerVerb                                // one verb's points in this lifecycle, by moment, in run order
{
    public RVerb Verb;
    public RList<RTriggerPoint> Before, Checks, Replace, WithChange, AfterCommit;
}
// RProcess gains TriggerPoints (file order) and TriggerVerbs (one entry per verb that has points, in the verb list's order).
// ResolvedModel gains TriggerLifecycles: the lifecycles with trigger points, by name then id (part 2: and TriggerPoints, the named ones).
// REntity gains TriggerLifecycles: the lifecycles governing it with points on object, link or own verbs, whatever those points'
//   entities (the runner tests them at run time), by name (part 2: and
//   TriggerPoints, the named trigger points that cover it, in decision 34's order). RVerb gains TriggerPoints.
// RStep's Assume holds booleans for guards and words for checks, keyed by name (another lifecycle's check as Lifecycle.Name, or LifecycleId.Name when two reached lifecycles share a name); RExpectation gains CheckWarnings and TriggerPoints; the step's trace
//   gains TriggerPoints and CheckWarnings.
```

- **Templates read** `model.trigger_lifecycles`, `process.trigger_points`, `process.trigger_verbs`, the trigger point's members (`trigger_point.type`, `trigger_point.timing`, `trigger_point.severity`, `trigger_point.message`, `trigger_point.verbs`, `trigger_point.states`, `trigger_point.targets`, `trigger_point.transitions`, `trigger_point.entities`, `trigger_point.attributes`, `trigger_point.ends`, `trigger_point.when`, `trigger_point.condition`, `trigger_point.sets`, `trigger_point.outside`, `trigger_point.team_code`, `trigger_point.covers`), `entity.trigger_lifecycles`, `verb.trigger_points`, `step.assume`, `step.expect.check_warnings`, `step.expect.trigger_points` and `step.trace.trigger_points`. Part 2 adds `model.trigger_points`, `entity.trigger_points`, `trigger_point.no_bypass`, `trigger_point.subjects` and `trigger_point.except_entities`.
- **`process.trigger_verbs`** lists only this lifecycle's points. Other lifecycles join at run time, as decision 11 says, and `entity.trigger_lifecycles` names them for a per-type runner.

**Dependency keys.**
- **The trigger points naming key, `tn:trigger-points`.** Its hash counts, for every lifecycle with `triggerPoints`, by id: every verb its points name, and its replaces with their verbs and entities. Part 2 adds each named trigger point's id, verbs, subjects and `exceptEntities`. It changes when a lifecycle gets its first trigger point or loses its last, when the verbs its points name change, or when a replace changes; not when a check's condition, a point's states or its sets change. It is worked out once per snapshot from the index rows, as `rn:rights` is: a process row carries those verbs and replaces only where present. `Planning/DependencyHasher.cs` gains the `tn` prefix, and the plan explainer words it "The trigger points the lifecycles state".
- **`model.trigger_lifecycles`** keys only on `tn:trigger-points`, as `model.rights_lifecycles` keys only on `rn:rights`.
- **`process.trigger_points` and `process.trigger_verbs`** key on the process file.
- **`trigger_point.covers`** keys on the process file and on the coverage keys `entity.lifecycles` uses (`ResolveRun.Processes.cs`): the referrers of each subject entity and its bases, `sn:subjects` and the carrier-set keys `cs:<key>` of the stereotypes involved. So a new derived entity or a new carrier renders it again.
- **`entity.trigger_lifecycles`** keys on `tn:trigger-points`, and in a project that uses trigger points on the entity's governing keys (rights section 6) and each lifecycle it lists. Its members ignore `entities`, so narrowing or widening a point's `entities` never changes which lifecycles an entity lists, and no key is missed.
- **`verb.trigger_points`** keys on `tn:trigger-points`, and once its hash holds anything, on the file of each lifecycle with trigger points. A lifecycle's first `promote` point changes the hash, so the verbs page renders again.
- So a template that reads none of these pays nothing, and in a project without trigger points the one new key never changes.

**Records:** process records gain `triggerPoints`, expectation records `checkWarnings` and `triggerPoints`, trace records `triggerPoints` and `checkWarnings`, each only where present. Step records' `assume` carries words only where a check is assumed. Part 2: named trigger points get records of kind `trigger-point`. So `get_resolved_model` answers don't change for a project without them.

## 8. Simulation and replay

- **One evaluator.** `Processes/TriggerSequence.cs` works out, for a verb, the acting lifecycle, the object's entity and states, and the place (transition, targets, attributes, end), which points run at each moment and in what order (decisions 11, 18, 19 and, in part 2, 34). The interpreter, `get_trigger_sequence`, `process.trigger_verbs` and both packs use it. It is cached per snapshot, per lifecycle and verb.
- **Where the interpreter runs them:**
  - **`Select` and `SelectAt`** (`Processes/StatechartInterpreter.cs:660`, `:743`): after `Guard` holds and, on a gated move, after `CheckSigner`, they run the candidate's before actions on a scratch copy of the object's values and then its checks. A refusing check calls the existing `Fail` with a new level above the gate's, and the candidate is skipped. `RunToCompletion` (`:606`) tries it again only when it selects again after another microstep, as for a false guard. Before actions and checks on candidates not taken leave no trace entry and no warning (decision 28). A passing candidate keeps its proposed values and its warnings with it.
  - **`HandleTime`** (`:275`): the timer's guard is tested directly; rows 8 and 9 run right after it, and a refusing check skips the timer's move.
  - **Signatures:** `sign`'s before actions and checks run inside selection, between `CheckSigner` and the count. For an occurrence that would complete the gate, selection works out completion without counting, as `Sign` (`:1105`) does, and runs the move's rows 8 and 9 too. `Sign` runs in `RemoveConflicts`' admit only for a candidate that passed. When a check refuses, `Audit` records the attempt as `refused` and nothing is counted. On an admitted signature, `sign`'s own points then run around the count: its before values are written, the count is made, its with-change points run and its after-commit points are queued. A signature that doesn't complete the gate makes no microstep, so this happens in the admit itself, for every signature. When the gate completes, the move's rows 10 to 12 follow in `Microstep`.
  - **`Microstep`** (`:1131`): before exit actions, it writes the selected transitions' proposed values (once per point, decision 18), in selection order, so the later wins when two set one attribute. After entry actions, it runs with-the-change points. After-commit points are queued.
  - `HandleInvoke` (`:315`): `complete`'s points for a human task of the root, on `invoke-done` and `invoke-error` alike, as rights check `complete` for both. After rights, `complete`'s before actions and checks run; a refusing check refuses the step, and the task stays pending. Then the transition is selected, running its own verb's rows 8 and 9 as for any move; with no transition left, the step is refused, as today. Then, as a verb of its own around the task's end, `complete`'s before values are written, the task ends, its with-change points run and its after-commit points are queued. The transition's rows 10 to 12 follow in `Microstep`.
  - `HandleVerb` (`:373`): after rights and freezing, rows 8 to 12 for the step's verb. A replace takes the place of the effect in its `switch`: a replaced `delete` doesn't set `_deleted`. `create` writes its proposed values (decision 27).
  - **At the end of the step,** queued after-commit points are listed in the trace as `queued`, in order.
- **Other lifecycles' points** for object, link and own verbs read the object's states in those lifecycles as `RunRights` does (`Processes/RunRights.cs`), and their conditions read the running chart for the acting lifecycle (rights decision 6).
- **`assume`** answers a team-code check's outcome, from the same map as guards (decision 26).
- **The trace** gains `triggerPoints` and `checkWarnings` (decision 28), left out when empty, so today's traces don't change.
- **Replay** compares `expect.checkWarnings` and `expect.triggerPoints` (MQ9310) and the refusal `check` (MQ9308).
- **Recording and the trace's refusals** read "uses rights or trigger points", not rights alone: `ScenarioReplay.UsesRights` (`Processes/ScenarioReplayer.cs:248`) widens to both, so `refusal` and `refusedBy` are written for a check in a project with no rights.
- **Replay keys** mirror `RightsReplayKeys` (`Resolution/ResolveRun.Rights.cs:400`). A scenario whose process has trigger points keys on its file, as today. A scenario that does an object, link or own verb (a verb step, or an event that names one) also keys on `tn:trigger-points`. In a project that uses trigger points it also keys on the governing keys of its subject's entity (of every subject entity of its process when it has no objects), and on each governing lifecycle with points on a verb its steps do, with that lifecycle's file and the keys of what its conditions and sets read. So it replays when another lifecycle starts governing the subject or gains a check on `modify`. A project without trigger points gains only `tn:trigger-points` on such scenarios, and its hash never changes there. The running lifecycle's own points reach its keys through `TraceInputs`: `ExprSites.ConditionIds`, `SetsData` and `SubjectDepth` (`Validation/ExpressionRules.cs:1198-1230`), which walk only guards and action sets today, also count trigger point sites. A `TriggerConditionKeys`, modelled on `RightConditionKeys` (`Resolution/ResolveRun.Rights.cs:472`), keys what other lifecycles' points read.
- **Speed:** a lifecycle without trigger points costs nothing: selection skips the new rows. Other lifecycles are asked only for object, link and own verbs, and only when they have points on that verb.

## 9. Tools

- **`get_trigger_sequence` (new):** decision 36. Read-only. Answers in one page, since one verb's sequence is short. Its web route follows the rights route in `src/Maquettiste.Functions/_functions/ProcessEndpoints.cs`: `GET /api/processes/{id}/trigger-sequence?verb=…&state=…&entity=…&attributes=…&end=…&transition=…`. `attributes` is comma-separated ids, as `states` is for rights; the others are one id or key each. There is no cursor.
- **`simulate_process`, `verify_scenarios`:** the trace's new members, check outcomes in `assume`, `values` on `create`, and the expectations of decision 28. Each enabled trigger in the answer gains **`checksUnknown`**, beside today's `guardUnknown`: the ids of the team-code checks its candidates reach, whose outcome the input takes from `assume`. It is left out when empty, so today's answers don't change. The simulation panel and the scenario editor's Assume cell read it.
- **`record_scenario`:** writes `refusal`, `refusedBy` and `checkWarnings` in a project that uses trigger points. A new optional input, `triggerPoints: true`, also writes `expect.triggerPoints`.
- **`preview_expression`:** previews a `when`, a condition or a set value at its pointer, with `old.`.
- **`save_element`, `apply_batch`:** accept `{ "text": … }` for `when`, `condition` and set values, parsed in the point's scope (decision 12).
- **`get_references`, `referenced_by`:** a trigger point's verbs (own verbs), states, targets, transitions, entities, attributes, ends, set attributes and expression paths; a step's `assume` word keys, as references to their trigger points. Boolean keys stay references to their guards, as today, so answers about guards don't change.
- **Deletes:** a state, transition, entity, attribute or end a trigger point names: `remove-references` drops it from the list. **When the last one goes, the trigger point goes too,** since an empty list would widen it to everywhere, as for rights. A set's attribute goes with its set. When a check is deleted, `remove-references` drops its word entries from `assume`, as it drops a guard's boolean entries today. `rename-verb` and `remove-verb` walk trigger points; one whose last verb is removed goes. Part 2: a named trigger point's last subject goes the same way.
- **`get_model_index`:** process rows gain `triggerPoints`, the count, only when above zero, the verbs their points name, and their replaces, each only when present. Their `verbs` also list the verbs their trigger points name. Part 2: named trigger points get rows with `type`, `verbs` and `subjects`.
- **`export_process`, `import_process`:** trigger points travel in `meta.maquettiste`.
- **`validate`:** the rules of section 6.
- **The CLI** follows the same tools. **The rule list, the schemas, the conventions resource, the modeling skill and the tool texts** (`src/Maquettiste.Cli/Mcp/ProcessTools.cs` and `Agent/tools.json`) describe all of this.

## 10. Packs

Output changes only for a model that uses what each output needs (decision 29).

**`csharp-dapper`, part 1**
- **`TriggerPoints.cs`,** in the processes namespace, only when `model.trigger_lifecycles` isn't empty:
  - `TriggerPointCall`, a record with init properties, so later members don't break a team's call: the verb, the lifecycle, the entity, the instance (none outside a run), the envelope, the held actors, the source states, the states entered and the transition (moves), the event (moves, with its payload), the end and the other object's key (links), the attributes changed (`modify`), `Old` and `New` (the team's objects, given by the caller; none inside the interpreter until 4.6), `Proposed` (the values before actions fill), and `CommandId` (given by the caller of the per-type runner).
  - `TriggerPointOutcome` (passed, the check warnings with their messages, and the refusing point with its message), `TriggerPointMessage` (decision 31's members, one per due after-commit point), `ITriggerPointSteps` (decision 30), and `<Entity>VerbDone` for each per-type runner.
  - A `TriggerPointRelay` that rebuilds the call from an after-commit message, with `Old` and `New` null, and awaits the team's method; and a depth counter that fails the command past 8 nested runs, which the relay starts at the message's depth (decision 21).
- **`<Lifecycle>TriggerSteps`,** generated: the step methods of decision 30, implementing `ITriggerPointSteps`.
- **`process-definition.scriban`:** `<P>Definition` passes a `<Lifecycle>TriggerSteps` instance as `StatechartDefinition.TriggerSteps`, beside `ProcessRights`, only for a lifecycle with points; otherwise it stays null (decision 30).
- **The team's half:** the static partial classes `<Lifecycle>TriggerPoints`, `<Lifecycle>TriggerConditions` and `<Lifecycle>TriggerRecords`, each method named after its trigger point (with `Value` added when that would match its class, decision 30) and documented with its text. A before point's method fills `call.Proposed` and must write nothing; its comment says so. After-commit methods carry a comment: they may run more than once. Every method is synchronous but the after-commit one, which returns a `Task` (decision 30).
- **The interpreter:** runs the rows of decision 18 inside its own selection for events, automatic moves, timers and signatures, so a refusing check works as a false guard there too. For a human task, `complete`'s before actions and checks run before the transition is picked, and a refusing check refuses the step, as section 8 says. `Refusals` gains `Check`, and results gain `CheckWarnings`, only when `model.trigger_lifecycles` isn't empty. Proposed values are written through `<Lifecycle>TriggerRecords` only after the checks pass. After-commit messages join the result's events, so `OutboxBehaviour` writes them in the command's transaction (decision 31). `sign`'s points run around the count on every signature, and the move's rows 10 to 12 follow only when the gate completes (decision 18). Everything written today under `rights` for refusals is written under `rights || triggers`, where `triggers` is `model.trigger_lifecycles.size > 0`: `Fail`'s `by` and the guard and gate ids passed to it, `StepResult.RefusedBy`, `CommandResult.RefusedBy` (`process-handlers.scriban`) and the scenario test's `refusedBy` assertion (`scenario-tests.scriban`).
- **Per-type runners:** `<Entity>Verbs`, for each entity whose governing lifecycles have points on object, link or own verbs (`entity.trigger_lifecycles`), whatever the points' `entities`, which it tests at run time. `RunAsync(verb, call, perform)` refuses move, signature and task verbs, runs freezing and `Rights.Check` when written, then before actions and checks across the lifecycles, then the records, then the team's `perform` or the replace, then with the change. It takes the `IProcessOutbox` and writes the after-commit messages and `<Entity>VerbDone` to it itself, then returns accepted or refused and the check warnings. The team calls it from its operation handlers inside its own transaction, as its generated comment says (decision 31).
- **Scenario tests:** a generated `ScenarioTriggers` records outside points and after-commit messages instead of calling them, and answers outside team-code checks from the step's `assume`. A team-code check that isn't outside runs the team's code, and the test asserts it gives the assumed outcome. A step with `expect.checkWarnings` or `expect.refusedBy` asserts them.
- **Nothing goes in `helpers.js`.**

**`csharp-dapper`, part 2:** named trigger points' methods in `TriggerPoints`, `TriggerConditions` and `TriggerRecords` classes per package, written once however many types they cover, and their place in each `<Entity>Verbs` runner, including entities with no lifecycle. `ITriggerBypass` with its team-owned stub `Dispatch/TriggerBypass.cs`, `SkipOptional` and `DryRun` (decision 35).

**`process-docs`, part 1**
- **The lifecycle page,** when it has `triggerPoints`: a "Trigger points" table (name, type, verbs, where, when, what it does, severity, outside, why), and for each verb a "What happens when someone promotes a Document" list in the order of decision 18: the built-in rows that apply, then each trigger point with its sentence. A team-code body says "the team's code: <why>". An outside one says so.
- **The scenario page:** per step, the trigger points that ran (when recorded), check warnings, assumed outcomes, and refusals by a check with its message ("refused by TitleBeforeReview: a document needs a title before it goes to review").
- **The verbs page:** the trigger points per verb.

**`process-docs`, part 2:** a trigger points page with each named trigger point, the types it covers and skips, and a "Trigger points" line on each covered type's lifecycle page. "Never skipped by a bulk load" on points with `noBypass`.

**`sql-ddl`, `seed-data` and the TypeScript sample:** no change.

## 11. Editor

**Process editor, a Trigger points tab** (lifecycles only), after Rights:
- **The grid:** one row per trigger point, in run order, reordered by dragging. Columns: Name, **Does** (the shared verb picker, several at once), **Type** (Check, Action, Replace), **When it runs** (Before checks, With the change, After commit; actions only), **Severity** (Error, Warning; checks only), **Where** (chips for states, into states, moves, types, attributes and links, edited in one popover), **Only if** (the value box, for `when`), **What** (the value box for a check's condition, or the sets editor from object effects; empty shows "The team's code"), **Outside systems**, and **Why** (for a check, "Message shown"). Replace is offered only for the verbs that allow it.
- **Add trigger point** at the foot. Icon buttons carry tooltips.
- **A toggle, Trigger points or What happens when.** The second takes a verb, and optionally a state and a type. It shows the steps in order, each a link, from `get_trigger_sequence`. Other lifecycles' points appear with their lifecycle's name.

**Scenario editor:**
- **Steps:** a `create` step gets a values cell. The existing **Assume** cell, which lists guards with no expression as true or false, also lists the team-code checks the step can reach, each with Pass, Warning or Error.
- **Expectations:** **Check warnings** and **Trigger points** cells. "Update from replay" fills Refusal, Refused by and Check warnings, and Trigger points when its box "Also record trigger points" is ticked. The Refusal cell offers **Check**.

**Simulation panel:** each step lists the trigger points that ran, with their outcome as a badge, and check warnings in amber. A refusal by a check shows its message and links to it. An automatic move left out by a check says so.

**Named trigger points (part 2):** a **Trigger points** folder in the Processes explorer, after Rights. The editor has the same fields without states, targets and moves, plus **Covers** (entities and stereotypes), **Except** (entities), **Never skipped by a bulk load** (`noBypass`), and a list of the types it runs for. Search finds them.

**Elsewhere:** the Verbs tab counts trigger points among a verb's uses. Quick fixes: MQ8101 and MQ8102 remove the key or entry, MQ8103 makes the point an action, MQ8105 and MQ8106 change its timing, MQ8107 removes it, MQ9310 updates the expectations from a replay.

**Generated and mock files:** the editor's API types come from `openapi.yaml` by the fixed command. The part kind needs no entry in `model/model.ts` or `model/labels.ts`, as `process-right` has none. By hand, as for rights: `model/labels.ts` gains the Trigger points tab's texts, `model/model.ts` counts a lifecycle's trigger points and their verbs in its index row, and a new `model/triggerPoints.ts` beside `model/rights.ts` holds the grid's rows and edits. In `src/editor/src/mocks/model/`, the mock gains `triggerRules.ts` beside `rightRules.ts`, and `stepper.ts` (1,768 lines today) gains rows 8 to 12 in its selection and verb steps. `validate.ts`, `refs.ts`, `store.ts`, `cascade.ts` and `modelIndex.ts` follow. The mock stepper runs other lifecycles' points for object, link and own verbs, as `rightsCheck.ts` asks other lifecycles, so the HR and Projects acceptance case gives the same trace in the mock. `src/editor/src/mocks/processHandlers.ts` gains a `get_trigger_sequence` handler, **simpler than the engine's:** this lifecycle's points only, with no other lifecycles. Other lifecycles are tried against the engine in the browser pass. The simpler answer stays in the mock: the editor never works out a sequence itself, and What happens when always shows the answer `get_trigger_sequence` returns.

## 12. A project that uses none of this

**After part 1:**
- **Files:** nothing rewrites a file, so saves, `format`, `format --check` and snapshot import behave as today. Recording a scenario writes what it writes today.
- **Existing guards, assumptions and actions:** unchanged. Guards still pick transitions, and selection works as today when no trigger point exists. `assume` with booleans for guards reads, records, references and deletes as today. A transition's actions and a state's entry and exit actions run in the same order, with the same sets and context.
- **Findings:** the same. Every new rule and case needs a trigger point, an `old` path at a trigger site, a word in `assume` or `values` on `create`.
- **Output:** the same bytes. Each pack change depends on `model.trigger_lifecycles`, `process.trigger_points`, `entity.trigger_lifecycles` or a new scenario key.
- **Simulation and replay:** the same outcomes and traces. A `create` step without values writes nothing new. Signatures are counted where they are today. `refusedBy` is written where it is today. The new trace members are left out.
- **Tool answers about the model:** the same. New record and index members are left out when unused, and `model.trigger_lifecycles`, `process.trigger_points`, `process.trigger_verbs` and `entity.trigger_lifecycles` are empty. `get_trigger_sequence` is new, and answers the built-in rows alone. `get_references` and `referenced_by` answer as today, since boolean `assume` keys stay references to their guards, as they are now. The rule list, the schemas and the tool texts describe the additions.
- **Dependency keys:** the same, but for one key. A scenario that does an object, link or own verb, and a template that reads `model.trigger_lifecycles`, `entity.trigger_lifecycles` or `verb.trigger_points`, also keys on `tn:trigger-points`. Its hash never changes in a project without trigger points, so nothing renders or replays again because of it.
- **Speed:** the same. Selection skips the new rows when a lifecycle has no trigger points, and the sequence is worked out only for lifecycles that have them.
- **Capabilities:** once built, these rows of `lifecycle-capabilities.md` part D become Yes: named points before and after; an override that replaces the normal behaviour (as a narrow replace); a failure that does or doesn't roll back; runs in line or later; order when several run; the same kinds at every point; points on sign-off (sign only; the rest with richer sign-off); points on other state events; points on every operation on an object; points on a relationship's operations; points on an attribute's change; messages back when refused. "A point just before commit" becomes Part (with the change). "Triggers switched off for a session" stays No until part 2, then becomes Part (`noBypass`, and the host's bypass). "An action on one object that moves another" stays No.
- **Once, as on any upgrade:** every unit renders again and writes the same bytes, since the engine version is part of each unit's key; the editor refreshes the `.schema` copies, or `validate` warns MQ1008 until refreshed; the index cache is read in full once.
- **A team on mixed engine versions:** an older engine refuses the new keys (decision 37).

**After part 2:**
- **A project that uses part 1 but no named trigger points, `noBypass`, bypass or dry run** keeps its files, output bytes, findings, keys and outcomes.
- **A project that uses neither part** keeps everything listed above.

## 13. What comes next

| Item | What it takes from trigger points |
| --- | --- |
| Richer sign-off | Points on `reject`, `ignore`, `withdraw`, `delegate` and `route`, accepted and warned (MQ8107) until then, start to run at a gate, with `transitions` as the place |
| Scenarios across objects | Connect and disconnect run the other object's points too. A trigger point that sends an event or does a verb on another object ("a change order releasing its items"), with a depth bound in the model |
| `set-state` and `override` as scenario steps | The rights design planned them here (rights section 12). Until they get steps, their points are accepted and warned (MQ8107), and the docs list them. The steps need their own meaning for entry and exit actions and for gates, so they come as a small item of their own |
| The generated runtime (4.6) | Generated code reads the object, so `when`, conditions and sets no longer go to the team |
| Revisions | `revise` gets its behaviour. A replace on it already works |
| Rights on types with no lifecycle | Shares `subjects` with part 2's named trigger points (rights decision 8) |
| If teams ask | Queued work as its own timing; compensation; per-set trigger points; `old.` across to-many links; `outside` on guards and actions themselves. Each adds an optional key and changes no file |

## 14. The owner's decisions

**Decided on 2026-11-07: all six as recommended.** The owner's reasons are kept beside each one. The owner also asked that four points already settled above stay visible once built: an event that names `delete` still does its move, and the replace stands in only for a verb step's effect (decision 16); a check that refuses an automatic move leaves that move untaken, and the step that led to it stays accepted (decision 18); what a trigger point sets is part of the verb, and matching uses the actor's own change (decisions 10 and 13); the editor and the docs pack show the engine's sequence (decision 36 and section 11).

1. **Names, permanent once released.** Decided: keep the set below. Each stays clear of a name already in use, as listed. `when` is also the word reserved for a later condition on a subject entry; the two sit on different objects, and both mean "this applies only when the condition holds". `old.` is the one new expression form. The check outcomes belong in the existing `assume` map, since a second map would split one step's assumptions.
   - the list `triggerPoints` (shown as "Trigger points"), not `triggers`, which a transition's `trigger` key and database triggers already use; the part kind `process-trigger-point`, and in part 2 the element kind `trigger-point` with `subjects` and `exceptEntities` (not `excludes`, which operations use);
   - `noBypass` (part 2), not `required`, which a gate and an attribute already use;
   - `checkWarnings` in the trace and in `expect.checkWarnings`, not `warnings`, which a verify result already uses; `expect.triggerPoints` and the trace's `triggerPoints`, whose entries carry an `outcome` and the flags `assumed` and `outside`; the refusal `check`; the words `pass`, `warning` and `error` in the existing `assume` map;
   - `type` with `check`, `action` and `replace` (not `override`, which is a verb); `timing` with `before`, `with-change` and `after-commit`; `severity` with `error` and `warning`; `when`, `condition`, `outside`, `targets`, `transitions` and `entities`;
   - `old.` in the expression language;
   - the tool `get_trigger_sequence`, its web route `GET /api/processes/{id}/trigger-sequence`, and its `record_scenario` input `triggerPoints`; an enabled trigger's `checksUnknown` in the simulation answer; the template members of section 7;
   - in `csharp-dapper`: `TriggerPointCall` with `Proposed` and `CommandId`, `TriggerPointOutcome`, `TriggerPointMessage`, `TriggerPointRelay` and `ITriggerPointSteps` (not `Trigger…` alone, which the processes namespace's `TriggerKind` already uses), `StatechartDefinition.TriggerSteps`, `<Lifecycle>TriggerSteps`, `<Lifecycle>TriggerPoints`, `<Lifecycle>TriggerConditions`, `<Lifecycle>TriggerRecords`, `<Entity>Verbs`, `<Entity>VerbDone`, `Refusals.Check`, `CheckWarnings`; in part 2 `ITriggerBypass`, `SkipOptional` and `DryRun`.
   - **Added while building, for the owner to confirm** (part 1 names the design did not list). In `csharp-dapper`: `ChartDefinition.TriggerSteps` (the design said `StatechartDefinition`; the pack's class is `ChartDefinition`) and `ChartDefinition.Id`, `TriggerPointWarning`, `TriggerPointDepth`, `StepResult.TriggerPoints`, `CommandResult.TriggerPoints`, `StepResult.AfterCommit`, `TriggerPointCall.InState`, `.Enters`, `.MovedBy` and `.Is`, `ITriggerPointSteps.Speaks` and `.Subject`, `TriggerPointOutcome.Ran`, `.Replaced` and `.Messages`, `TriggerPointStandIn` (`Current`, `Check`, `Records`) and `ScenarioTriggers` (`Assumed`, `Recorded`). In the engine and its contract, chosen where the design left a choice: the template members of section 7 plus `trigger_point.team_code`, `trigger_point.covers`, `trigger_point.process` and `verb.trigger_points`, and the CLI verb `process trigger-sequence`.
2. **Which verbs allow a replace.** Decided: only `delete`, `disconnect`, `copy` and `revise` (decision 17). Governance verbs stay out. The other choice was the evaluation's list, which adds `create` and `connect`. Numbering is better as a before action, and storage elsewhere is a binding. Adding a verb later breaks nothing; taking one away breaks files.
3. **What is in the first release.** Decided: part 1 alone, as 0.17.0: trigger points on lifecycles for every verb, all three types and timings, and check outcomes in `assume`. Then part 2 (named trigger points for types with no lifecycle, `noBypass`, the bypass and the dry run) as 0.18.0. Queued work, compensation and per-set rules wait for a team to ask (decisions 8 and 23). Part 1 changes how the interpreter picks a move; that ships and settles before the host's permission hook and a rollback path are added. The other choice was both parts in one release.
4. **A check that refuses the signature that would complete a gate.** Decided: that signature is not counted, and it is audited as `refused` (decision 18). The signer signs again once the object is fixed. Counting it would keep a signature given for a change the model refused.
5. **What recording writes.** Decided: `record_scenario` writes `refusal`, `refusedBy` and `checkWarnings` when the project uses trigger points, and `expect.triggerPoints` only on request (decision 28). Writing it always would make every new trigger point break every recorded scenario.
6. **The dry run and the bulk-load bypass.** Decided: both come in part 2, with `noBypass` (decisions 20 and 35). Part 1 already changes the interpreter's selection, its riskiest part; the bypass needs the host's permission hook, and the dry run needs a rollback.

## 15. Tests

**Part 1**
- **`TriggerRuleTests` (new):** failing and passing cases for MQ8101 to MQ8107 and MQ9310, and the new cases of MQ3001, MQ3101, MQ3102, MQ3104, MQ3108, MQ9701, MQ9704, MQ9802, MQ9803, MQ9305 and MQ9308. MQ8104 across two lifecycles that govern one entity. MQ8102 with `entities` widening what sets may name. MQ3001 for a check named like a guard; MQ8101 for `outside` without team code and for with-change sets on `delete` with no replace; MQ8107 for a point on `override` only. Today's models, including guard assumptions, give the same findings: a model with a stale boolean `assume` key gives the same findings and the same `get_references` answer. A deleted check drops its word entry from `assume`.
- **`TriggerSequenceTests` (new):** the order of decision 18 for each verb group; moments before lifecycles (decision 19); the acting lifecycle first; each narrowing key, compound states in `states` and `targets`, `entities` with derived entities and a stereotype's carriers; `attributes` matching the step's own change only; which lifecycles answer per group; an orchestration with a subject; a scenario with no objects; a move with no verb runs none.
- **`TriggerReplayTests` (new):**
  - Before actions seen by checks; a refused step drops the proposed values; an accepted one is seen by the move's own actions; `copy` shows the new object without writing it.
  - Selection: a refusing check on one candidate lets the next be taken; an event refused with `check` as the most telling reason; an `always` move whose check refuses ends the step without looping and without MQ9507; a timer whose check refuses; a point matching two parallel transitions runs once per microstep, and twice across two microsteps; two parallel transitions setting one attribute, the later winning; an `always` move refused in one loop and taken in a later one lists its check once, with the microstep that took it; a preempted candidate lists nothing and its warning is dropped.
  - Signatures: `sign`'s checks before counting; a check refusing the completing occurrence leaves the count unchanged, audits `refused` and makes no move; the next valid signature completes it; a `with-change` set on `sign` applies on a signature that doesn't complete the gate. A human task's `complete` check refuses the step, on `invoke-done` and on `invoke-error`.
  - Checks: error and warning; `assume` with words for checks beside booleans for guards; a team-code check assumed to pass.
  - Replace: a replaced `delete` keeps the object and later steps reach it; its sets apply; history in the trace; an event that names `delete` runs no replace.
  - With-the-change sets after the effect; after-commit points queued in order; `outside` marked; outcomes with the `assumed` and `outside` flags; `when` false shown as skipped; a check narrowed by `attributes` runs on an event that names `modify`, and an action narrowed so doesn't; `create` matching `states` on the initial state; `entities` in a scenario with no objects; with-change sets on a replaced `delete`; `old.` on `modify`, on a move and on `create` (null).
  - Trigger point sets don't run `modify`'s points, and aren't checked against attribute rights. A frozen object during a move: sets apply, with a note.
  - `create` with values written to the row; a `create` step without values unchanged.
  - Expectations, MQ9310; recording writes `refusedBy` and `checkWarnings`, also in a project with trigger points and no rights, and `triggerPoints` only on request; nothing new without trigger points.
  - Replay keys: lifecycle B gains a check on `modify`, or starts governing the subject through a base or a stereotype, and a scenario of lifecycle A with a `modify` step replays; a project without trigger points keeps `tn:trigger-points` at one hash.
  - Unchanged traces, signatures, audits and replays for a model without trigger points, including models that use rights, freezing and guard assumptions.
- **`TriggerExpressionTests` (new):** `old.` parsing, typing and printing at trigger sites; `old` keeping today's meaning elsewhere; the two site kinds; the payload rule; previews.
- **`ResolvedTriggerTests` (new):** `model.trigger_lifecycles`, `process.trigger_points`, `process.trigger_verbs`, `trigger_point.covers` and `trigger_point.message`, `entity.trigger_lifecycles`, `verb.trigger_points`, records; dependency keys: a lifecycle's first `promote` point renders the verbs page and the shared interpreter unit again; a new derived entity renders `covers` again; a check's condition change doesn't change `tn:trigger-points`; a lifecycle whose only object-verb point is narrowed to Drawing is still listed by Document's `entity.trigger_lifecycles`, and removing the narrowing leaves nothing stale. Each stale-output test is checked to fail without its fix.
- **`GetTriggerSequenceTests` (new):** agrees with the interpreter's order; answers without resolving the model; the built-in rows alone in a project without trigger points.
- **No change:** a model without trigger points gives the same output, files, `format --check`, findings, replay, traces and tool answers, and its keys gain only `tn:trigger-points`. The pack golden tests pass untouched.
- **Pack tests:** a new case per pack. `csharp-dapper`'s `TriggerPoints.cs`, step methods, the three static classes (a point named `CheckModify`, a point named `XWhen` beside `X`, and a point named `DocumentTriggerPoints` in lifecycle `Document` all build), interpreter steps, `<Entity>Verbs` and `<Entity>VerbDone`, outbox messages with stable ids, the depth guard and scenario tests build with warnings as errors and run. A refused check leaves no write from a before action, though the refused result commits. A with-the-change body that throws rolls the command back. An after-commit message is written only when the command commits. `<Entity>Verbs` refuses `promote`, writes its messages and `<Entity>VerbDone` to the outbox, and leaves none stored when the caller's transaction rolls back. The relay awaits the after-commit method, rebuilds the call with `Old` and `New` null, and starts the depth count at the message's depth. Trigger points with no rights: a refused check sets `RefusedBy` on the result, and the scenario test asserts it. `process-docs`' table, "What happens when" lists and scenario lines.
- **Editor:** unit tests for the grid's columns per type, the replace filter, the Where popover, and the Assume cell's two kinds of value. Browser tests (in the browser pass) for adding each type, reordering, What happens when with another lifecycle, assuming a check's outcome, and updating expectations from a replay with and without trigger points.
- **Acceptance,** on an invented model: the document lifecycle and scenario of section 5; an employee governed by HR and Projects, where HR's warning on `modify` of `salary` runs during a Projects verb step; a gated approval whose check refuses the last signature.

**Part 2**
- Named trigger points: loading, references, deletes (the last subject), `exceptEntities`, MQ8102 and MQ8104 cases; the order of decision 34; a customer with no lifecycle whose `create` step runs `CustomerNumber`; packs' per-package classes and runners for entities with no lifecycle; `noBypass`, the bypass stub refusing, a permitted skip logged and `noBypass` points still run; a dry run that rolls back and records outside points; no change for a project that uses part 1 only.

## 16. Tasks and the build plan

### Tasks

The sizes are days for a person who knows the code, with tests and a review. They show how the groups compare.

**Part 1**

| Wave | Group | What | Size |
| --- | --- | --- | --- |
| 1 | Contract and model | The schemas (with `assume`'s two kinds of value), `openapi.yaml`, rule texts; the trace, refusal and expectation members; the `process-trigger-point` part; the `Trigger` and `TriggerSet` sites and the `old` node; MQ8101 to MQ8107 and the changed rules | Two days |
| 1 | References, deletes and the sequence | References (`assume`'s word keys only), deletes and their widening guard, `rename-verb` and `remove-verb`, index rows, localization; `TriggerSequence` | A day and a half |
| 1 | Editor, part one | The Trigger points tab and grid, the Where popover, quick fixes, the mock's validation and references | A day and a half |
| 2 | Simulation and scenarios | The new rows in `Select`, `SelectAt` and `HandleTime`, with scratch copies; signatures counted after the checks, with the completing occurrence; verb steps; replace; `assume` for checks; `create` values; the trace; MQ9310; recording; replay keys | Two days and a half |
| 2 | Resolved model, keys, tools and docs output | `RTriggerPoint`, `model.trigger_lifecycles`, `process.trigger_verbs`, `entity.trigger_lifecycles`, `tn:trigger-points` and the coverage keys, records, `get_trigger_sequence`, tool texts; `process-docs` | A day and a half |
| 2 | `csharp-dapper` | `TriggerPoints.cs`, the step and static classes, the interpreter's selection, `Proposed` and the records after checks, `<Entity>Verbs` and `<Entity>VerbDone`, outbox messages and the relay, the depth guard, scenario tests; the definition hook, the runner's outbox and the refusal members under `rights || triggers` | Two days and a half |
| 2 | Editor, part two | What happens when, scenario cells, the simulation panel, the mock's stepper and a simple `get_trigger_sequence` | A day and a half |
| 3 | Docs | User guide (processes, scenarios, trigger points, expressions), releases, modeling skill, capabilities rows, `next.md`. The guide says two things plainly: until 4.6 the team's static methods load the object themselves, as right conditions do; and a team-code action fills nothing in a replay, so a check that reads its values needs them given on the step | Half a day |

That comes to about thirteen and a half days for a person.

**Part 2**

| Wave | Group | What | Size |
| --- | --- | --- | --- |
| 1 | Contract and engine | The `trigger-point` kind, `subjects`, `exceptEntities` and `noBypass`, rules, references, deletes, the sequence's type points | A day and a half |
| 1 | Editor | The explorer folder and editor, the mock | A day |
| 2 | Simulation and output | Type points in runs and replay keys; resolved members; both packs, including runners for entities with no lifecycle, the bypass hook and the dry run | Two days |
| 3 | Docs | User guide, releases, `next.md` | A quarter day |

That comes to about four and three quarter days for a person.

**Checklist for every group.** Anything that reads or writes a lifecycle's parts, an expression site, a verb, a scenario step, a guard assumption, a signature or the trace must be found and handled:
- **A new part kind (and in part 2 an element kind):** `Model/ModelIndexer.cs` (the part kind map and `SubElementKinds`), `Model/PropertySchemas.cs` (`TranslatedHolderKinds`), `Loading/NameIndex.cs`, the list-to-kind map and `PartNoun` (a noun for the part kind) in `Loading/ChangePlanner.Cascade.cs`; part 2 also `Model/ElementKind.cs`, `Model/KindInfo.cs` (folder `model/trigger-points`), `Model/ElementBase.cs`, `get_model_kinds` and `Localization/LocaleChains.cs`.
- **Expressions:** the two sites in `Expressions/ExprType.cs` and every site switch the `Set` and `Right` kinds needed (`Expressions/ExprJs.cs`, `Expressions/ExprTyper.cs`, `Expressions/ExprScope.cs`, `Expressions/ExprSql.cs`, `Rendering/BuiltinHelpers.cs`, `ModelStore.ExpressionPreview.cs`), the `old` node in each, `Expressions/ExprText.cs` (parses and prints `old.`), `Expressions/ExprLowering.cs`, `Expressions/ExprEvaluator.cs` and `Expressions/expr-runtime.js` (the `old` node), and `Validation/ExpressionRules.cs` (`ExprSites.Of`).
- **Processes:** `Model/Processes.cs` (`ScenarioStep.Assume` takes both kinds of value; only word keys are indexed, as references to trigger points), `Processes/StatechartInterpreter.cs` (`Select`, `SelectAt`, `HandleTime`, `Sign`, `Microstep`, `HandleVerb`), `Processes/ScenarioReplayer.cs` (`UsesRights` widened to trigger points), `Processes/StepTrace.cs` (`Refusals`), `Processes/ProcessExpressions.cs` (it lowers the `Set` and `Right` sites into the session, and its `EvaluateSet` and `EvaluateCondition` take the old row), `Processes/RunRights.cs` (other lifecycles' states), `Processes/VerbStepFields.cs`, `Processes/ProcessApi.cs` (`ResolveNames` maps check names, another lifecycle's as `Lifecycle.Name`, and `EnabledTrigger` gains a counterpart to `GuardUnknown`, left out when false, for the reachable team-code checks the Assume cell lists), `Processes/ProcessLibraries.cs` (a library holds no trigger points), and the exporter and importer.
- **Resolver, keys and records:** `Resolution/ResolvedProcesses.cs` (`step.assume`), `Resolution/ResolveRun.Processes.cs`, `Resolution/ResolveRun.Rights.cs` (the replay keys to mirror), the `ExprSiteKind.Set` and `Right` switches in both, `Editor/ResolvedRecords.cs`, `Planning/DependencyHasher.cs` (`tn`), `Generation/PlanExplainer.cs`.
- **Tools:** `src/Maquettiste.Cli/Mcp/ProcessTools.cs`, `Agent/tools.json` and the route in `src/Maquettiste.Functions/_functions/ProcessEndpoints.cs`, each owned by one agent per wave.
- **Packs:** in `packs/csharp-dapper/`, `process-definition.scriban` (the hook), `interpreter.scriban`, `process-handlers.scriban` and `scenario-tests.scriban` (the refusal members under `rights || triggers`).
- **Contracts:** the schema copies, `openapi.yaml` and its generated files. `validation-rules.json` is written only by the fixed command.
- **Test harness:** `RenderKit.WithSettings` and `ResolvedCoverageTests`.

### How it gets built

**What earlier items cost.** Rights part 1, about 7,500 to 8,500 lines, cost about 1.46M output tokens, 5.17M new input and 372M cached re-reads over about 3.1 hours. Its estimate had said 1.0M, 2.6M and 230 to 260M over 4 to 4.5 hours. Part 2, about 3,000 lines, cost about 0.71M, 2.25M and 143M over about 3 hours, against an estimate of 0.45M, 1.2M and 120 to 135M over 2 to 2.5 hours. So the earlier per-line figures ran low on output (by about half) and new input (about double). The figures below use rights' actual cost per line, and the check's figures for part 1. In every item, the review and the browser pass each found real bugs.

**This item's size,** with tests, without pack expected files.
- **Part 1, about 10,000 to 11,000 lines** (the check's figure, with its fixes, plus about 500 for the second review's): contract, rules and references about 1,500 and their tests about 1,000; the sequence and simulation, with selection and signatures, about 1,400 and their tests about 1,000; resolved model, keys, tools and packs about 1,800 and tests about 650; editor and mock about 2,000; docs about 400. The main risks are the change to how the interpreter picks moves, and the generated interpreter in `csharp-dapper`.
- **Part 2, about 3,700 lines:** contract and engine about 750 and tests about 550; simulation about 200 and tests about 250; resolved model, packs, the bypass and the dry run about 850 and tests about 200; editor and mock about 750; docs about 150.

**Agents.** Each is capped at about 100 steps or 200k context. Opus 5.5 builds the engine and pack output and runs every review. Sonnet 5.5 builds the editor, the mock and the docs.

**Part 1 agents**

| Wave | Agent | Model | Steps | Starts from |
| --- | --- | --- | --- | --- |
| 1 | Contract and model; **contract first**: the schemas, all `openapi.yaml` changes including the trace, refusal, expectations, `assume`, records and `get_trigger_sequence`; the members in `Processes/StepTrace.cs`; rule texts. Keeps every change to `Expressions/` and `Validation/ExpressionRules.cs` | Opus | About 100 | `origin/main` (0.16.0) |
| 1 | References, deletes and `TriggerSequence`, through the engine's planner, not the tool files | Opus | About 100 | The contract commit |
| 1 | Editor and mock, part one | Sonnet | About 100 | The contract commit |
| 2 | Simulation and scenarios: selection, signatures, timers and verb steps, through `Processes/ProcessApi.cs` | Opus | About 100, with a fresh agent for the rest if it runs over | Wave 1's engine agents |
| 2 | Resolved model, keys, records, `get_trigger_sequence` with its route, replay keys and `process-docs`. **Owns `ProcessTools.cs`, `tools.json` and `ProcessEndpoints.cs`** | Opus | About 90 | Wave 1's engine agents, beside the simulation agent |
| 2 | `csharp-dapper` output | Opus | About 100 | The resolved model agent's first commit (`RTriggerPoint`, `model.trigger_lifecycles` and `process.trigger_verbs` filled), in the first free slot |
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
| 2 | Simulation, resolved model and both packs, with the bypass and the dry run. Owns the tool files | Opus | About 100 | The contract and engine agent's finish |
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
| Wave 1 (contract, model, references, sequence, editor part one) | About 1.4 hours | 0.5M | 1.8M | 125M |
| Wave 2 (simulation, resolved model and docs output, `csharp-dapper`, editor part two) | About 2.1 hours | 0.8M | 2.6M | 185M |
| Wave 3 (review, docs, browser pass) | About 0.6 hours | 0.3M | 1.0M | 75M |
| Wave 4 (fixes, second browser pass, CI) | About 0.6 hours | 0.3M | 1.1M | 80M |
| In all | **About 4.5 to 5 hours** | **About 1.9M** | **About 6.5M** | **About 465M** |

The second review's fixes add about 500 lines, mostly in `csharp-dapper` (the definition hook, the after-commit message, the runner's outbox and the refusal members) and in simulation (`sign` and `complete` around their effects). So wave 2 rises a little; the other waves don't change.

**Part 2**

| | Wall time | Output | New input | Cached re-reads |
| --- | --- | --- | --- | --- |
| Wave 1 (contract and engine, editor) | About 1 hour | 0.3M | 1.0M | 60M |
| Wave 2 (simulation and output, bypass, dry run) | About 0.9 hours | 0.3M | 1.0M | 60M |
| Wave 3 (review, docs, browser pass) | About 0.6 hours | 0.15M | 0.45M | 30M |
| Wave 4 (fixes, CI) | About 0.5 hours | 0.1M | 0.35M | 30M |
| In all | **About 3 hours** | **About 0.85M** | **About 2.8M** | **About 180M** |

**Both parts:** about 13,700 to 14,700 lines, about 7.5 to 8 hours, about 2.75M output, about 9.3M new input and about 645M cached re-reads. The total is over four hours, so it is split. The split costs about 0.5 hours and 40M cached, as it did for rights: a second pull request, review, browser pass, CI run and release.

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
