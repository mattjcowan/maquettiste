# Rights: design

**Status (2026-11-06): designed; the owner's answers and the fixes from three independent checks are applied; not built.** It designs the fifth item in the owner's order after 4.1: rights, with named rights, defaults, combining, freezing and refusal reasons in scenarios. It is built in two parts, each its own release (decision 32). [Who, what and when](lifecycle-actors-scenarios.md) section 7 sets its direction. [What lifecycle systems can do](lifecycle-capabilities.md) is the yardstick. [The actors design](actors-design.md) (decisions 22 and 23 were recorded for this item), [the verbs design](verbs-design.md) and [the object effects design](object-effects-design.md) give its parts. Three independent checks found parts that couldn't be built as written (an operation's actors, other lifecycles' states and "who can do what" in the first; stale keys, a named right's condition scope and what the caller holds in generated code in the second; where generated code can learn what the caller holds, in the third), and smaller points. Each is settled below, and the owner answered the first check's questions. A file path without a project prefix is under `src/Maquettiste.Engine/`. The examples are invented. Each new name and format is held to the roadmap's test: it stays for good, it must help any team whether they use it or not, and it stays optional.

## 1. In short

Today "who may act" is only a closed list: an event's actors, a gate's signers, a human task's actors. Nothing says what may be done to an object in a state. Nothing can deny. Nothing can be frozen. A scenario can expect a refusal, but not why.

| Layer | What changes |
| --- | --- |
| The model | A lifecycle lists **rights**. A right **allows** or **denies** some **actors** some **verbs**, in some **states**, under an optional **condition**. It may list actors it does not apply to, with **`except`**. Its description says why. A **named right** is its own element, which many lifecycles use: what lifecycle systems call a rule. Each lifecycle has a **default** for what no right mentions: allowed, unless it says denied. **Each lifecycle answers on its own, and any "no" wins.** An event's actors, a task's actors and a gate's signers keep their meaning, and count as rights. In part 2, **freeze** and **thaw** take effect, and a right can name **attributes** |
| Simulation and scenarios | Checks run in a fixed order: rights, then guards, then signatures. Part 2 puts "frozen" first. A new **verb step** lets a participant modify, delete or do any other verb to the object. A refused step names the element that refused it, and a scenario can expect that element |
| Code generation | Templates read each lifecycle's rights, its default and a worked-out "who can do what". `csharp-dapper` writes a rights table and one check, where it already checks actors, and leaves conditions to the team. `process-docs` adds a Rights section, a "who can do what" table and a page of named rights. Each happens only when the model uses rights |
| The team's system, or a lifecycle system | Nothing changes until the team uses the new output or builds from the model |

**A project that uses none of this** keeps its files, output bytes, findings, tool answers and speed, and its dependency keys gain only one key whose hash never changes there. Section 11 lists everything, for each part.

## 2. Scope

| | What | Section |
| --- | --- | --- |
| **Part 1 (release 1)** | Rights on lifecycles, with `except`; named rights; defaults and combining; event, task and gate lists as rights; rights on links (`ends`); verb steps and refusal reasons in scenarios; `get_rights` and "who can do what"; the resolved model, both packs and the editor | 3 to 10 |
| **Part 2 (release 2)** | Freezing: `freeze` and `thaw` take effect, `frozen` on scenario objects and links, `whileFrozen`, the `frozen` refusal and its host hook. Rights on attributes (`attributes`) | Decisions marked "part 2"; sections 4 to 15 |
| **Later, its own item** | Rights on types with no lifecycle (decision 8) | 12 |
| **Left to trigger points** | Code that runs on a verb, as check, override or action, including on freeze and thaw; checks on automatic moves; override and set-state as scenario steps | 12 |
| **Left to richer sign-off** | Reject, ignore, withdraw, delegate and route on a gate, each with its rights; "no one holds both of these roles on one object" | 12 |
| **Left to scenarios across objects** | Verb steps on other objects; connect and disconnect changing the run's links | 12 |
| **Not in this item** | A meaning for an operation's `requires` (decision 15). Rights passed along containment (decision 22). Events a state ignores or defers, which is a chart feature, not a right. Freezing the model's own elements (decision 18). Enforcing `lock` (section 12) | 12 |

**What part 1 leaves fixed, so part 2 adds only a refusal value and keys.** Part 2 moves nothing that part 1 sets:
- **The check order** (decision 12) keeps row 1 for "frozen". In part 1 that row never refuses. Every other row keeps its place.
- **Refusals and the trace.** `expect.refusal` takes a value from the engine's list of refusals. Part 2 adds `frozen` to the list, and the trace's `frozen` member. `refusedBy` and `rights` keep their shape.
- **Verb steps** keep `verb`, `values`, `end` and `object`. Part 2 adds `attributes`, and allows `freeze` and `thaw` as verb steps. In part 1 those two are MQ9305, so no verb step changes outcome in part 2. **An event that names `freeze` does** (0.14.0 already allows one): from part 2 it freezes the subject. That is the one outcome change, and an owner confirmation for part 2 (decision 16).
- **A right** keeps its keys and their meaning. A right on `freeze` or `thaw` speaks of the object, or with `ends` of those links, in both parts (decision 20). Part 2 adds `whileFrozen` and `attributes`, both optional. A right without them means the same in both parts.
- **The evaluator** takes an object's frozen flags from the start. In part 1 they are always false.
- **Generated code.** `Rights.Check` takes one request record with init properties, so part 2 adds the frozen flag as a member without breaking a call the team wrote. Part 2 adds `Refusals.Frozen` and the frozen hook. Both are written only when a project uses freezing (decision 28).
- **Tools.** `get_rights` keeps its rows. Part 2 adds an optional `frozen` input.

## 3. Decisions

Each decision gives its confidence: **high**, **medium-high**, **medium** or **low**. A decision marked **(part 2)** is built in part 2. The others are part 1.

### Where a right lives, and its shape

1. **Rights live on the lifecycle, in one list, `rights`. Each right names the states where it holds; none means every state.** Confidence: high.
   - **Why:** one right often spans several states ("no edits in Released or Obsolete"). Stored per state, it would be repeated in each. Lifecycle systems show access per state, and the editor shows that grid too (section 10). The grid is a view, the list is the file.
   - **The alternative:** `rights` on each state. It repeats a right in every state it spans, and a named right would need a use in each state. It is not chosen.
2. **Only lifecycles hold rights.** Confidence: medium-high. A right speaks of an object in its states. An orchestration is a flow, not an object's state, and a library is shared parts. Both keep their events' and tasks' actor lists. An orchestration's event that names an object, link or own verb is still answered by the lifecycles of its subject (decision 10). `rights` or `rightsDefault` elsewhere is MQ9901.
3. **A right has a `name`, an `access`, `actors`, `verbs`, `states` and an optional `condition`.** Confidence: high, for the shape and the names (the owner's answer 4).
   - `name` is required and is an identifier, as a guard's is. Two inline rights of one lifecycle can't share a name (MQ3001). It lets a refusal name the right. `displayName` is optional.
   - `access` is `allow` or `deny`, and required. One right is one row of the grid, and a reader sees "deny" first.
   - **Why `access`, not `effect`:** an operation's `effect` already means read or change. One word keeps one meaning. `access: "deny"` is the field people will read (the owner's answer 4).
   - **The description says why.** It is shown with every refusal, in the grid and in the docs. No second text key: `description` is already on every element and part, and is localizable. No rule asks for one; the editor prompts for it.
   - Optional keys narrow a right: `except` (decision 4), `ends` (decision 20), and in part 2 `attributes` (decision 21) and `whileFrozen` (decision 18).
   - Each entry of `rights` is a part of kind **`process-right`**, an inline right or a use (decision 7). The bare word `right` is the named right's kind, as `enum-member` and `operation-member` name parts whose bare word is taken. The name needs the owner's confirmation (section 13).
4. **Who: `actors`, and optionally `except`.** Confidence: high for any actor and for `except` (the owner's answer 2); medium-high for "an empty list means everyone".
   - **`actors`** takes any actor, as an event's list does: people, roles, groups, organizations, external systems, the system actor, and actors from the object (the owner, the person a field names, the members of a role within a context). So "the owner may", "everyone may", and "reviewers on this project may" all fit, as lifecycle systems set rights for the owner, for everyone and for named people or roles.
   - A right to a role reaches its members at any depth, deny included (actors decisions 5 and 22).
   - **An empty `actors` means everyone.** An event with no actors already accepts anyone. The editor shows "Everyone". A delete never empties the list by accident: when the last actor named goes, the right goes too (section 8). **The alternative:** an explicit `everyone: true`. It is a second way to say who. It is not chosen.
   - **`except` lists actors the right does not apply to.** It takes the same actors: people, roles, groups and organizations (their members follow, at any depth, as elsewhere), and actors from the object, such as "the object's owner".
   - **`except` never grants.** For an actor it lists, this right says nothing. Other rights and the lifecycle's default still answer. With the usual default of allow, "deny the change, except the owner" is the whole rule. On a lifecycle that denies by default, the owner still needs an allow.
   - **It works with no owner field.** "The object's owner" is an actor from the object (actors decisions 8 and 10). It is read from the subject's owner field when there is one. Otherwise a scenario names the owner with the object's `owner`, and generated code asks the team's resolver (actors decision 27).
   - **Its rules:** as for any actor list, an entry that names nothing is MQ2001, and one that isn't an actor is MQ2002. An actor from the object that can't be worked out on every subject is MQ9604. An entry that the same right's `actors` also names is MQ9906, a warning, since the right then never applies to it.
   - **The alternative:** a condition such as `subject.ownerLogin <> current_actor()`. It needs an owner field that every subject holds under one id. It still works, but it isn't the only way.
5. **What: `verbs`, at least one, built-in or the project's own.** Confidence: medium-high. MQ9701 and MQ9704 apply here as at any use. The verb's group says what the right speaks of:
   - **Moves, signatures and tasks:** this lifecycle's moves, the signatures they need, and its human tasks.
   - **Objects, links, and the project's own verbs:** the object, whichever lifecycle is acting (decision 10).
6. **When: `states` and `condition`.** Confidence: high.
   - `states` names states of this lifecycle. A compound state counts when any state inside it is active, as `in_state` does.
   - `condition` is an expression at a new `Right` site. It reads the object under `subject` (to-one paths too), `current_actor()`, `in_state` and `now()`. It doesn't read the context or the payload: a right is also checked outside a run, such as an edit through an API, which has neither.
   - **A right holds only when its condition is true.** False or unknown means it says nothing, so a deny over a missing value doesn't fire.
   - **`in_state` on the lifecycle that is running reads the running chart,** from any answering lifecycle's right, as object effects decision 8 has it for a process's own states. So an HR right that reads `in_state(subject, Projects.Active)`, checked during a Projects run, sees the live states, not the states the scenario object was given.
   - **`create`** is checked as if the object were in the lifecycle's initial state, since that is where a new object starts. Confidence: medium-high.

### Named rights

7. **A named right is its own element, kind `right`, in `model/rights/`. A lifecycle uses it with an entry `{ id, right, states }`.** Confidence: medium-high.
   - **Why an element:** it is reused across lifecycles, has its own page, references, package and translations. An actor from the object is reused the same way (actors decision 8).
   - **Why not in a library process:** a library's members read the using process by name, but conditions name attributes by id, which MQ9020 already refuses in libraries.
   - **The use says only where:** its `states`. Who, what, when and the access belong to the named right. A variant is another right.
   - **Its condition's scope is what every subject of every lifecycle that uses it holds under one id,** as a guard's `subject.x` is over several subjects (4.1 decision 20). Text written for the condition is turned into ids in that scope. Text that doesn't parse there is refused (MQ3101), as for any expression. The condition is then typed once per use, against that lifecycle's subjects.
   - **With no use, there is no scope,** so text written for the condition stays as text. Its only finding is MQ3108. Once a lifecycle uses the right, `text-to-trees` turns it into ids.
   - **Checked at each use:** its condition, its `ends`, and the actors from the object in its `actors` and `except` must resolve on every subject of each lifecycle that uses it, as a library's gate is checked at each process (actors decision 11). Part 2 adds `attributes`. A failure is reported at the use: a new use whose subjects don't hold what the condition reads is MQ3102 there.
   - `preview_expression` takes an optional lifecycle, to preview a named right's condition, or text for it, as if used there (section 8).
   - **It covers rules.** "Forbid modify, delete and change owner when the item is secret, unless the actor is its owner" is one named right: deny those verbs, except the object's owner, when `subject.classification = 'secret'` (section 4).
8. **Rights on types with no lifecycle come later, as their own item.** Confidence: high. Decided by the owner (answer 3).
   - Putting rights on types is a real widening. Doing it later leaves every file from part 1 readable.
   - The later item can add an optional `subjects` key to a named right, entities or stereotypes. Adding it changes no file. Part 1 doesn't have the key.
   - Until then, a rule shared by several lifecycles is one named right, used once per lifecycle. A type with no lifecycle has no rights.

### Defaults and combining

9. **Each lifecycle has a default, `rightsDefault`: `allow` (when absent) or `deny`.** Confidence: high (actors decision 23); medium-high for one value rather than one per verb.
   - "Denied" by default would change every existing lifecycle's simulation.
   - A lifecycle system that denies by default is stated with `deny` and allow rights ("Everyone may read").
   - **MQ9904 warns about an allow right in a lifecycle that allows by default.** Each lifecycle answers on its own (decision 11), so there the allow can't change any answer: a deny still wins over it, and without it the default allows. A team writing requirements may keep such rights for the record, and can lower the warning in Settings › Validation.
10. **Which lifecycles answer depends on the verb's group.** Confidence: medium-high.
    - **Moves, signatures and tasks:** only the lifecycle where the move, signature or task is. Promoting in HR is not promoting in Projects.
    - **Objects, links and own verbs:** every lifecycle that governs the object, each in the object's state there. In a run, these states come from the scenario (decision 27).
    - A team that wants HR to stop a move in Projects writes a right in the Projects lifecycle with `in_state(subject, HR.Suspended)`.
    - **An orchestration** holds no rights, but it may list subjects. Its event that names an object, link or own verb on its subject is answered by every lifecycle that governs the subject, as for any process, so a second process can't get around a lifecycle's rights. None of them is the acting lifecycle, so they answer in name order. The orchestration's own moves, signatures and tasks answer to their closed lists only, as today, and so does an orchestration with no subjects, which has no object. `get_rights` still takes a lifecycle. Confidence: medium-high. It reuses the machinery for other lifecycles that could say no, for about 150 more lines.
    - **A scenario with no objects** names no entity for its subject. The other lifecycles that govern every subject of its process answer, each in its initial state (decision 27). One that governs only some subjects isn't asked. A note says each.
11. **Combining: each answering lifecycle answers on its own, then any "no" wins.** Confidence: high. Decided by the owner (answer 1).
    - **One lifecycle's answer:** its denies, then its allows, then its default. If a deny right holds, it says no. Otherwise, if an allow right holds, it says yes. Otherwise its default answers. A satisfied closed list (decisions 13 and 14) counts as an allow of its own lifecycle. Within one lifecycle this is actors decision 22: any deny wins, then any allow, then the default, the rule of the attribute-based access control standard.
    - **Across lifecycles:** the verb is allowed only when every answering lifecycle says yes. One "no" decides it.
    - **A lifecycle that allows by default and says nothing about the verb answers yes,** so it never vetoes another. A project lifecycle silent about changing a salary leaves the HR lifecycle to decide.
    - **Why each answers on its own:** pooling every right first would let one lifecycle's allow beat another lifecycle's default. If HR denies by default and Projects allows project managers to `modify`, a pooled rule lets a manager change a salary, because HR's default never becomes a "no" of its own. Answered per lifecycle, HR says no and the change is refused.
    - **The alternative:** pool every right, then any deny, then any allow, then the defaults. The first draft chose it, with the reason "a strict lifecycle stays strict", which the case above disproves. It is not chosen.
12. **The checks run in a fixed order, and the first that fails names the refusal.** Confidence: high (the direction, section 9). So two scenarios can't disagree about the reason.

    | Order | Check | Refusal | Refused by |
    | --- | --- | --- | --- |
    | 1 | Frozen (part 2, decision 17). In part 1 this row never refuses, and the rows below keep their places | `frozen` | Nothing: freezing is built in |
    | 2 | Closed lists: an event's or a human task's actors | `actor` (today's) | The event or the invoke |
    | 3 | Denies: every answering lifecycle's deny rights that hold; the acting lifecycle's first (an orchestration has none), in file order, then the others by name | `right` | The right: inline, or the named right |
    | 4 | Defaults: the first answering lifecycle, in the same order, where no allow holds and the default is deny | `default` | The lifecycle |
    | 5 | No move: no transition takes the event from the active states | `no-transition` (today's) | Nothing: no element refused it, so `refusedBy` is left out |
    | 6 | Guards, as today | `guard` | The guard |
    | 7 | Signatures: the gate's signers, then the lifecycle's denies on `sign`, then a repeat signer, then a missing reason | `gate-signer`, `right`, `gate-repeat`, `gate-reason` | The gate or gate policy, or the right |

    - Rows 3 and 4 together are decision 11: a lifecycle says no when a deny holds, or when nothing allows and its default is deny. An explicit deny is reported before any default, so the more telling reason wins.
    - **Before row 1, as today:** a finished run, an event that isn't the process's, an invoke that isn't pending, and a step after a delete are `no-transition`, with no `refusedBy`.
    - Rights are checked on the event's verb before any guard picks a transition, as verbs decision 12 planned. `sign` is checked at the signatures step, since only then is it known that the occurrence is a signature (verbs decision 15).

### Who may is said once

13. **An event's `actors` stays a closed list, and counts as a right.** Confidence: high.
    - The listed actors get an allow for that event, in its own lifecycle. Everyone else is refused by the event, as today (`actor`).
    - Explicit rights narrow it, since a deny wins. They can't widen it: an allow on `promote` doesn't let a Manager send an event that lists only Clerks.
    - For an event that names an object, link or own verb, other lifecycles still answer (decision 10). The event's list is an allow only in its own lifecycle.
    - **An event with no verb** is outside rights that name verbs. Its actors and its lifecycle's default still apply. Under `deny`, an event with no verb and no actors can never be sent (MQ9905 warns).
    - Outcomes for today's models don't change.
14. **A human task's `actors` is the same, for `complete`. A gate's `signers` is the same, for `sign`, and for the gated event.** Confidence: high.
    - Rights on `complete` and `sign` narrow them. Reporting a human task's error (`invoke-error`) is checked as `complete` too, as its actors list is checked today.
    - **A gate's signers count as an allow for the gated event, as the event's actors do.** Every occurrence of a gated event is checked against the event's verb at rows 3 and 4 (verbs decision 15). Without this, under a deny default, a signer couldn't sign without also being allowed to promote. The signers that count are those of the gates on the transitions the event can take from the active states.
    - **So a deny on the gated event's verb also stops signing.** Every signature is sent as that event, and rows 3 and 4 come before row 7.
    - MQ9105 stays. A signature is still sent as the gated event, so the event's list is checked first.
15. **An operation's `requires` stays out of rights.** Confidence: high.
    - **Why:** the engine gives `requires` no meaning today (`Model/Operations.cs:74-77`). It holds any element, and "required" could mean all of them or any one. Giving it a meaning would be a permanent change to an existing key, for a check that nothing could run: the simulator doesn't run operations, and generated operation endpoints carry no actor (decision 30).
    - This departs from the direction (section 7.4), which planned "an operation's `requires` becomes a right with no state". It can come later, if teams ask, and changes no file.
    - **An operation's verb is still a verb.** Rights answer for it whoever does it. A team's operation handler can ask `Rights.Check` with that verb (decision 30).

### Freezing (part 2)

16. **(Part 2) Freeze and thaw take effect. An object or a link is frozen or not.** Confidence: high.
    - It starts thawed. A scenario may start an object or a link frozen (`frozen: true`).
    - An accepted `freeze` freezes it, and an accepted `thaw` thaws it, whether a verb step or an event that names the verb does it. So rights and, later, triggers apply to freezing with nothing extra (the direction, section 7.5).
    - Freezing what is frozen, or thawing what isn't, changes nothing and adds a note.
    - **Naming `freeze` is the opt-in.** An event may already name `freeze` (0.14.0), and does nothing in a run. From part 2 it freezes the subject, as verbs decision 16 fixed. A separate switch would be permanent, so there is none. A scenario changes only when it sends such an event and later one that names `modify`, `connect`, `disconnect` or `delete`. Part 2's release notes say that such events start freezing, and `verify_scenarios` names the steps. The generated frozen hook answers "not frozen" until the team writes it, so such a project keeps building (decision 19). This is an owner confirmation for part 2 (section 13). Confidence: medium-high.
17. **(Part 2) While frozen, modify, connect, disconnect and delete are refused before any right.** Confidence: high (verbs decision 16, the owner's own words).
    - It is an implicit deny that no allow can lift.
    - **A frozen object** can't be modified or deleted, and its links can't be added or removed.
    - **A frozen link** can't be disconnected or have its attributes modified. The objects at its ends are not frozen by it.
    - **A move that sets data on a frozen object: its sets apply, and the trace adds a note.** Confidence: medium-high. Freezing doesn't stop moves (decision 18), and a move's sets are part of the move, such as recording who made an item obsolete. Stopping the sets but not the move would leave the object half moved. A team that wants a move stopped while frozen writes a `whileFrozen` deny on its verb.
18. **(Part 2) Freezing stops nothing else. A right with `whileFrozen: true` holds only while the object (or, with `ends`, the link) is frozen.** Confidence: medium-high.
    - **Not stopped:** moves (a frozen released item may still become obsolete), `revise` (revising is how a frozen item changes), `change-owner`, `change-type`, `change-lifecycle`, `lock`, `unlock`, `copy`, `read`, and the project's own verbs.
    - **Why the small set:** a built-in meaning is permanent (verbs decision 2), and a deny can't be lifted by an allow. A larger fixed set could never be narrowed for a team that needs less. With `whileFrozen`, a team adds "no change of owner while frozen", or stops its own verb, in one right.
    - **The alternative:** an optional key on an own verb, saying freezing stops it. It covers only own verbs, and adds a second way to do one thing. It is not chosen.
    - **Freezing the model's own elements** (rules, processes) would lock editing the model, which is another feature. A pack that writes a lifecycle system's setup can freeze that system's objects itself.
19. **(Part 2) Where "frozen" is held is the team's system's business.** Confidence: medium-high. As with an owner without an owner field (actors decision 10), nothing in the model holds it. Generated code asks the host, through a hook written when the project uses freezing (decision 28). Until the team writes it, the hook answers "not frozen", so a project that already names `freeze` keeps building; the generated comment says that nothing is refused for freezing until then. A subject's frozen field can come later, if teams ask, without changing a file.

### Rights on links and attributes

20. **`ends` narrows a right to links through those ends of the subject.** Confidence: medium-high.
    - With `ends`, `connect`, `disconnect`, `read`, `modify`, `freeze` and `thaw` speak of those links, since verbs decision 4 says these act on links. This holds from part 1: a right on `freeze` with `ends` already answers for freezing a link, and part 2 only makes freezing take effect. "Deny connect through `components` in Released."
    - Without `ends`, link verbs speak of every link, and `read`, `modify`, `freeze` and `thaw` speak of the object.
    - `ends` beside a verb that can't act on a link, such as `delete` or `change-owner`, is MQ9903.
    - Lifecycle systems also set connect and disconnect from each end. An end is one side of a relation, so this needs nothing more.
    - **Scenario links hold no values,** so a `modify` step on a link is checked and recorded only (decision 25).
21. **(Part 2) `attributes` narrows a right to reading or modifying those attributes.** Confidence: medium-high.
    - "Deny read of `salary`, except to HR staff." Only with `read` and `modify` (MQ9903).
    - A modify step that changes several attributes is checked per attribute, but only against rights that name attributes. One denied refuses the step. A right without `attributes` is checked once, as in part 1, so part 1 outcomes don't change.
    - **In "who can do what":** a `read` or `modify` cell that a right with `attributes` would change answers "depends", and names that right.
    - The direction placed this "later in the same item". It is part 2.
22. **Rights passed along containment are not in this item.** Confidence: medium-high. An actor from the object with `within` already says "the project's reviewers may change its items". Inheriting a container's rights, in the container's own state, needs a containment rule on relations and the container's state in every run. It can come later, if teams ask, as an optional key.

### Simulation and scenarios

23. **Rights check inputs an actor makes:** events of the process the scenario runs, human-task completions (and errors), signatures, and verb steps. Confidence: medium-high.
    - **Not checked:** automatic moves, timers and finished invokes that are not human tasks. The chart decides those, and a lifecycle that denies `promote` to everyone in a state shouldn't stop its own timer. Trigger points' check kind may stop them later.
    - **Not checked:** events of an invoked sub-process, which sees no object. Their own actor lists apply, as today.
24. **In a run, a right applies to a participant who satisfies any of its actors and none of its `except`, not only the actor the step acts as.** Confidence: medium-high.
    - A deny on `Owner` applies to Bea even when she acts as `Author`. An `except` that names `Owner` leaves Bea out even when she acts as `Author`. This follows actors decision 17 for what "satisfies" means.
    - A step with only an `actor` matches that actor and its memberships, and an actor from the object by exact match, as today. The same holds for `except`.
    - **Generated code follows the same rule when it knows what the caller holds.** The envelope carries only the actor the caller acts as, so `RightRequest` also carries the caller's held actors, which a host hook gives (decision 29). Until the team fills that hook, a caller isn't checked against rights on roles it holds but isn't acting as. Confidence: medium, since it depends on the host.
25. **A new step input, `verb`: a participant does a verb to the scenario's subject, or to one of its links.** Confidence: high that it is needed (verbs decision 18); medium-high for its shape.
    - **Who acts:** as for an event step, the step's `actor`, or the participant's when it names only a participant.
    - **Keys:** `verb`; `values` (attribute id to value) for `modify` on the subject; `end` and `object` for a link. Without `end`, the step acts on the subject. Part 2 adds `attributes` for `read`.
    - **`values`** names attributes a set could set (object effects decision 2), with values that fit their types; otherwise MQ9305.
    - **Which verbs:** object verbs, link verbs and the project's own. Moves, signatures and task completion keep their own steps (an event, `invoke-done`). `override`, `set-state`, `reject`, `ignore`, `withdraw`, `delegate`, `route` and `claim` get steps with their own items (MQ9305 until then). In part 1, `freeze` and `thaw` steps are MQ9305 too, until part 2 gives them their effect.
    - **What the run does when accepted:**
      - `modify` on the subject applies its `values` to the object's row, so later guards and sets see them. They show in `valuesChanged` and `expect.values`, keyed by attribute id, as sets are (object effects decision 4). On a link, it is checked and recorded only, since scenario links hold no values.
      - `delete` removes the object, and the chart stops. Later event, invoke and verb steps are refused (`no-transition`, with no `refusedBy`). Timers no longer fire, and time steps only move the clock. The outcome is read from the chart as it stood at the delete. A note says so.
      - The rest (`read`, `create`, `copy`, `revise`, `lock`, `unlock`, `change-owner`, `change-type`, `change-lifecycle`, `connect`, `disconnect`, own verbs) are checked and recorded only. A note says the run didn't apply them. Scenarios across objects will apply `connect` and `disconnect`.
      - Part 2: `freeze` and `thaw` change the flag.
26. **A refused step names the element that refused it, and a scenario can expect it.** Confidence: high for the shape; medium-high for when recording writes it.
    - The trace gains `refusedBy`, the id from decision 12, and `rights`, the rights that answered, each with its access. Two refusal values are new: `right` and `default`. Part 2 adds `frozen`.
    - A step may expect `refusal` (the value) and `refusedBy` (the id). When either differs, it is MQ9308. Both need `accepted: false` (MQ9305).
    - `record_scenario` writes both for a refused step when the project uses rights (decision 28). Otherwise it writes what it writes today.
27. **Other lifecycles' states come from the scenario,** as `in_state` reads them (object effects decision 10): the object's `states`, else its bound value, else the initial state with a note. The lifecycle that is running is read from the run (decision 6). Confidence: high.

### Code generation

28. **Each output depends on what it needs, so a project pays only for what it uses.** Confidence: high.
    - **A project uses rights** when a lifecycle has `rights` or `rightsDefault`. Only then is `process.access_grid` filled, are `Rights.cs` and the interpreter's checks written, and does recording write refusing ids. A named right acts only through a use.
    - **A named right** reaches `model.rights`, its index row and the docs' rights page, even before a lifecycle uses it.
    - **Scenario keys reach only their scenario.** A verb step, or an expected `refusal` or `refusedBy`, changes that scenario's records, page and test, and nothing else. A scenario that expects `guard` doesn't turn on `Rights.cs`.
    - **One key says which rights exist, `rn:rights`** (section 6). Its hash never changes in a project without rights, as `sn:subjects` doesn't in a model with no stereotype entry. Every output whose answer could change when a lifecycle gets its first right keys on it, so that first right renders and replays them again.
    - **(Part 2) A project uses freezing** when an event, a transition or a right names `freeze` or `thaw`, or a right has `whileFrozen`. Only then are the interpreter's frozen check, the frozen hook, `Refusals.Frozen` and the request's frozen flag written. A scenario's `frozen` keys reach only that scenario's test. **A project uses attribute rights** when a right has `attributes`; only then do the request and the rights table carry them.
    - Section 9 gives the test for each output. Otherwise every template sees empty lists, and the new trace and record members are left out.
29. **`csharp-dapper` writes one rights table and one check. Conditions, other lifecycles' states and what the caller holds come from the team.** Confidence: medium-high; medium for the held actors, which depend on the host.
    - Generated code can't read the object until the generated runtime (4.6), as 4.1 decided for guards and object effects for sets. So each condition is a partial method the team writes, and a new one fails the build until written.
    - **Conditions take the request.** A right's condition reads no context or payload (decision 6), and a lifecycle's `StatechartCall<TContext>` differs per process, so each condition is a static partial method that takes the `RightRequest`. A named right's is written once, in a `RightConditions` class in its package's namespace, however many lifecycles use it. An inline right's is in `<Lifecycle>RightConditions`, beside its lifecycle's handlers. Each method is named after its right, which is unique in its class (MQ3001 for inline rights; a named right's name is unique in its package), so an inline and a named right never collide. The team's half of each class is written once.
    - Actors from the object, in `actors` or `except`, go through the resolver the team already supplies (actors decision 27). The acting process's resolver also answers for those that another answering lifecycle's rights name, so one resolver serves the check.
    - **What the caller holds comes from a host hook, never from the request.** The engine checks every actor a participant satisfies (decision 24), but the envelope carries one actor, the one the caller acts as. `ProcessEnvelope` and `CommandContext.Envelope` stay as they are, `Authorize` only answers yes or no, and `Pipeline.cs` is written once, so none of them can carry it. So, only in a project that uses rights:
      - an interface, `ICallerHoldings`, answers the actors a caller holds with no context, besides the one it acts as, for an envelope. The host reads the caller from its own principal, as `Authorize` does;
      - a team-owned stub, `Dispatch/CallerHoldings.cs`, is written once, like an action stub, when the project first uses rights. It returns no held actors. Its comment says what that means: the caller is checked only as the actor it acts as, so a deny on a role it holds but isn't acting as doesn't reach it, and neither does an allow;
      - each generated command handler asks it once per command and passes the list to the interpreter, which puts it in `RightRequest.Held`. So every sender is covered, the generated endpoint and a team's own sender through the dispatcher alike. A team that calls the interpreter directly passes the list itself;
      - `Held` is never read from the request body or the command, so a caller can't give itself roles. The actor it acts as still comes from the request, and `Authorize` still decides whether the caller may act as it;
      - a holding within an object is an actor from the object, which the resolver answers.
    - **The gap is visible, not silent.** The stub is a file in the team's half, and its comment names the consequence. A host that keeps it is checked on rights for its acting actor only, where the engine checks every actor the participant satisfies. Confidence: medium.
    - **Other lifecycles' states come from a hook, written only when needed.** An event that names an object, link or own verb is answered by every lifecycle that governs the object (decision 10). Generated code can't read another lifecycle's state for the same object. So for each governing lifecycle, other than the acting one, that could say no to such a verb, because it has a deny right naming the verb or it denies by default, the acting process's handlers (a lifecycle's, or an orchestration's with subjects) gain a partial method `StatesIn<Lifecycle>(call)` in the team's half. They are written from the entries of `process.may_refuse` (section 6) that an event's verb reaches, whose keys follow the first right of another lifecycle. The method returns the object's active states there; none means the initial state, as in a run (decision 27). A new one fails the build until written, as conditions do.
    - **Why a hook:** the check never skips a lifecycle that could refuse, and the build names what the team must supply. A project whose rights need no other lifecycle gets no hook. A lifecycle that allows by default and says nothing about the verb never needs one, since it can't say no.
    - **The alternative:** check only the acting lifecycle, and say so in the generated comments. Generated code would then accept what the model refuses, and nothing in the build would show it. It is not chosen.
    - **`RightRequest` has init properties,** so part 2 adds members without breaking a team's call: the verb, the acting lifecycle (none outside a move), the entity of the object (which decides the lifecycles that govern it), the envelope, the held actors, and the object's states by lifecycle. A lifecycle missing from the states reads as its initial state, as in a run (decision 27). A team's own call, such as from an edit endpoint, fills it itself.
30. **Generated code checks rights where it already checks actors: events, human tasks and signatures.** Confidence: high. An orchestration's events that name an object, link or own verb on its subject are checked too (decision 10).
    - Operation endpoints call their handler directly and carry no actor (`operation-endpoints.scriban`). The pipeline that checks actors carries process commands only (`CommandDescriptor` in `dispatch.scriban`). So generated code doesn't check an operation's `requires` or its verb. The first draft did, and it could not be built.
    - A team that wants an operation checked calls `Rights.Check` from its handler, with the operation's verb, its entity and the object's states.
    - Adopting rights changes the generated checks of events, tasks and signatures only, and only in a project that uses rights.

### Tools

31. **One new tool, `get_rights`: who can do what in a lifecycle. Each lifecycle answers on its own, and any "no" wins.** Confidence: medium-high.
    - Per state, actor and verb, it answers allowed, denied, or "depends", with the elements that decide.
    - **A row is one kind of caller:** one who satisfies that actor, and so every actor it is a member of, and no other actor the grid names. "Everyone else" satisfies none of them. So a right to a member role (Lead author) doesn't apply to its parent's row (Author): the member has its own row. A right to the parent applies to the member's row. A caller who holds two roles that aren't members of each other gets both rows' answers, and any "no" wins.
    - **Events that name the verb.** A cell counts the closed lists of the events that name its verb and can be sent from its state. When several do, with different actor lists, the caller passes when any one of them lets it through, and `by` names those events. A verb that no such event names is answered by rights and the default alone.
    - **Within the lifecycle,** "depends" means a condition or an actor from the object decides, in `actors` or in `except`.
    - **Other lifecycles** answer only for object, link and own verbs (decision 10), and their state isn't known in a cell. When the lifecycle itself says no, the cell is denied. Otherwise, when another lifecycle that governs the object says no in every one of its states, the cell is denied, by that lifecycle's right or default. When one could say no only in some states, or under a condition, the cell depends, and `dependsOn` names those lifecycles. A lifecycle that allows by default and says nothing about the verb never appears.
    - **One row per entity where they differ.** When the lifecycle's subjects are governed by different other lifecycles, and that changes a cell, the cell has one row per entity, each with its `entity`. Otherwise one row covers them all.
    - **It reads the snapshot, never the resolved model:** the lifecycle, the named rights it uses, the actors and their memberships, and the lifecycles that govern its subjects with their rights. So it answers from what the question touches, and never resolves the whole model. `process.access_grid` calls the same evaluator while resolving. For a project that uses rights, the editor's "Who can do what" view, the docs pack's table and the tool never disagree. For a project without rights, `process.access_grid` is empty, so output doesn't change, while `get_rights` still answers from the closed lists and the default.

### Release

32. **Two parts, each a minor release, and a team upgrades together.** Confidence: high, by the owner's answer 5 and the owner's 4.1 decision 6.
    - Part 1 is the part a review can hold: rights, named rights, verb steps, refusal reasons and the "who can do what" view. Part 2 adds freezing and rights on attributes. Freezing must say what a move that sets data does to a frozen object (decision 17), so it doesn't ride along.
    - An older engine refuses a `right` file and a file with the new keys of each part. `formatVersion` stays 1.

## 4. Schema and files

**Part 1**

| File | Key | Type | Notes |
| --- | --- | --- | --- |
| `common.json` | `$defs/rightBody` (new) | `access`, `actors`, `except`, `verbs`, `ends`, `condition` | Shared by both forms. `access` is `allow` or `deny`. `actors` and `except` are unique actor id lists. `verbs` is a non-empty unique list of `verbRef`. `condition` is `$defs/expr` |
| `process.json` | `rightsDefault` (after `boundAttribute`) | `allow` or `deny` | Decision 9; lifecycles only (MQ9901) |
| `process.json` | `rights` (after `gates`) | list; each entry is an inline right `{ id, name, displayName?, description?, …rightBody, states? }` or a use `{ id, right, states? }` | Decisions 1, 3 and 7; exactly one form per entry (MQ9901). Part kind `process-right` |
| `right.json` (new kind) | `kind`, `id`, `name`, `displayName`, `package`, `description`, and `rightBody` | | Decision 7. Folder `model/rights/`. No `subjects` (decision 8) |
| `scenario.json` | step `input` gains `verb`; step `verb`, `values`, `end`, `object` | `verbRef`; attribute id to value; end id; scenario object id | Decision 25 |
| `scenario.json` | step `expect.refusal`, `expect.refusedBy` | a refusal value; an id | Decision 26 |

**Part 2**

| File | Key | Type | Notes |
| --- | --- | --- | --- |
| `common.json` | `rightBody` gains `attributes` and `whileFrozen` | attribute id list; boolean, default false | Decisions 18 and 21 |
| `scenario.json` | step `attributes` | attribute id list | Decision 25, for `read` |
| `scenario.json` | object `frozen`, link `frozen` | boolean, default false | Decision 16 |
| `scenario.json` | `expect.refusal` gains the value `frozen` | | Decision 26 |

Also updated in each part: the sample app's schema copies, `openapi.yaml` by hand (the shapes, the trace, the simulate and record requests, the records, `get_rights`, `preview_expression`'s lifecycle), and the editor files generated from it.

```json
"rightsDefault": "deny",
"rights": [
  { "id": "…", "name": "EveryoneReads", "access": "allow", "verbs": ["read"] },
  { "id": "…", "name": "AuthorsEditDrafts", "access": "allow", "actors": ["<id of Author>"],
    "verbs": ["modify", "connect", "disconnect"], "states": ["<id of Draft>"] },
  { "id": "…", "name": "ReleasedIsReadOnly", "access": "deny", "verbs": ["modify", "delete"],
    "states": ["<id of Released>", "<id of Obsolete>"], "description": "A released document changes only by revising it." },
  { "id": "…", "name": "NoBomChangesInReview", "access": "deny", "verbs": ["connect", "disconnect"],
    "ends": ["<id of end components>"], "states": ["<id of Review>"] },
  { "id": "…", "right": "<id of SecretOnlyOwner>" }
]
```

```json
{
  "kind": "right", "id": "…", "name": "SecretOnlyOwner", "access": "deny",
  "except": ["<id of DocumentOwner>"],
  "verbs": ["modify", "delete", "change-owner"],
  "condition": { "text": "subject.classification = 'secret'" },
  "description": "Only its owner may change a secret item."
}
```

`DocumentOwner` is an actor from the object, `"relative": { "owner": true }`. The document's subject entry names no owner field, so the team's system holds the owner. The condition is written as text. Once a lifecycle uses the right, it is stored as a tree of ids, in the scope of every subject of its uses (decision 7).

```json
"objects": [ { "id": "<o1>", "name": "spec", "entity": "<id of Document>", "owner": "<p1>",
  "values": { "<id of classification>": "secret" } } ],
"participants": [ { "id": "<p1>", "name": "bea", "actor": "<id of Author>" },
                  { "id": "<p2>", "name": "carl", "actor": "<id of Author>" } ],
"steps": [
  { "id": "…", "input": "verb", "verb": "modify", "participant": "<p2>", "values": { "<id of title>": "Pump spec" },
    "expect": { "accepted": false, "refusal": "right", "refusedBy": "<id of SecretOnlyOwner>" } },
  { "id": "…", "input": "verb", "verb": "modify", "participant": "<p1>", "values": { "<id of title>": "Pump spec" },
    "expect": { "values": { "<id of title>": "Pump spec" } } }
]
```

The document starts in Draft. Carl is refused by the named right: a deny wins over `AuthorsEditDrafts`. Bea is the owner, so the named right says nothing for her, and `AuthorsEditDrafts` lets her edit. No owner field is needed: the scenario object's `owner` names Bea.

Part 2 adds, for example, these rights and steps:

```json
{ "id": "…", "name": "AdminsFreeze", "access": "allow", "actors": ["<id of Admin>"], "verbs": ["freeze", "thaw"] },
{ "id": "…", "name": "OwnerStaysWhileFrozen", "access": "deny", "verbs": ["change-owner"], "whileFrozen": true }
```

```json
{ "id": "…", "input": "verb", "verb": "freeze", "participant": "<id of ada, an Admin>" },
{ "id": "…", "input": "verb", "verb": "modify", "participant": "<p1>", "values": { "<id of title>": "Pump spec 2" },
  "expect": { "accepted": false, "refusal": "frozen" } }
```

Once Ada freezes the document, Bea is refused too.

## 5. Rules

The next free family is MQ99xx.

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ9901 | E | A `rights` entry that is both an inline right and a use, or neither; a use that names something other than a named right; `rights` or `rightsDefault` on an orchestration or a library | Remove the entry |
| MQ9902 | E | A right's `states` names something that isn't a state of its lifecycle (another process's state, a history or a choice) | Remove the state |
| MQ9903 | E | `ends` that aren't ends every subject holds; `ends` beside a verb that can't act on a link (any but `connect`, `disconnect`, `read`, `modify`, `freeze` and `thaw`). For a named right, reported at the use. Part 2 adds: `attributes` that aren't attributes every subject holds, or beside a verb other than `read` and `modify` | Remove the entry |
| MQ9904 | W | An allow right in a lifecycle that allows by default: it can't change any answer (decisions 9 and 11) | Remove it; set the default to deny |
| MQ9905 | W | Under `deny`, an event or a human task no one can ever do: no actors, no gate signers on its transitions, and no allow right of its lifecycle names its verb (`complete` for a task); or an event with neither a verb nor actors nor signers | Add actors; add an allow right |
| MQ9906 | W | An `except` entry that the same right's `actors` also names, so the right never applies to it | Remove it from `except` |
| MQ9308 | E | A refused scenario step whose refusal or refusing element differs from `expect.refusal` or `expect.refusedBy` | Update the expectations from a replay (`refresh-scenario`) |

**Existing rules that gain a case.** No finding changes for a model that uses none of this: every new case needs a right, a default or a verb step. Nothing is checked on an operation's `requires` (decision 15).
- **MQ3001:** two inline rights of one lifecycle with one name.
- **MQ2001 and MQ2002:** an `except` entry that names nothing, or something other than an actor, as for any actor list.
- **MQ9701, MQ9704:** a verb named by a right.
- **MQ9604:** an actor from the object in a right's `actors` or `except`, at the lifecycle (at each use for a named right).
- **MQ9106:** an actor named by a right, in `actors` or `except`, counts as used.
- **MQ3101, MQ3102, MQ3108 and the typing rules:** a right's condition. A named right's is parsed in the scope of its uses and typed at each use, where a failure is reported. With no use, its text is kept, and MQ3108 is its only finding (decision 7).
- **MQ3104:** `current_actor()`, `in_state` and `now()` read at `Right` sites. The context and the payload don't (MQ3102).
- **MQ9305:** a verb step's fields: a move, signature or task verb; in part 1, `freeze` or `thaw`; `values` beside a verb other than `modify`, or beside an `end`; a `values` attribute a set couldn't set (object effects decision 2), or a value that doesn't fit its type; an `end` that isn't the subject's, or an `object` not linked through it, except for `connect`; `delete` on a link. Also `refusal` or `refusedBy` beside `accepted: true`. Part 2 adds: `attributes` beside a verb other than `read`; `frozen` on a link of an object other than the subject.

The mock backend's rule list follows.

## 6. Resolved model and templates

```csharp
public sealed class RRight : RObject                             // Kind: "right" for a named right, "process-right" for an inline one
{
    public string Name, DisplayName, Description;               // as other parts and elements
    public string Access { get; internal set; } = "";            // "allow" or "deny"
    public RList<RActor> Actors { get; internal set; }           // file order; empty means everyone
    public bool Everyone { get; internal set; }                  // Actors is empty
    public RList<RActor> Except { get; internal set; }           // file order; empty when none
    public RList<RVerb> Verbs { get; internal set; }
    public RList<REnd> Ends { get; internal set; }               // empty when none
    public string? ConditionText { get; internal set; }          // printed from current names; the stored text for a named right with no use
    public bool Named { get; internal set; }                     // an element, not a part of a lifecycle
    public RPackage? Package { get; internal set; }              // named only
    public RList<RRightUse> Uses { get; internal set; }          // where it holds
    // Part 2 adds Attributes (RList<RAttribute>, empty when none) and WhileFrozen (bool).
}
public sealed class RRightUse : RObject                          // Kind "process-right": one entry of a lifecycle's rights
{
    public RRight Right; public RProcess Process;
    public RList<RState> States;                                 // empty: every state
    public RExpression? Condition;                               // the right's condition typed for this lifecycle's subjects;
}                                                                //   JavaScript null when it uses current_actor() or in_state
public sealed class RAccessCell                                  // one cell of "who can do what" (decision 31)
{
    public RState State; public RActor? Actor;                   // Actor null: everyone else
    public RVerb Verb; public REntity? Entity;                   // Entity null: the same for every entity the lifecycle governs
    public string Answer;                                        // "allowed", "denied" or "depends"
    public bool ByDefault; public IReadOnlyList<RObject> By;     // the rights, lists, events or lifecycles that decide
    public RList<RProcess> DependsOn;                            // other lifecycles that may say no; empty otherwise
}
public sealed class RMayRefuse                                   // a governing lifecycle, other than this process, that could say no
{                                                                //   to object, link or own verbs this process's events or rights name
    public RProcess Lifecycle; public RList<RVerb> Verbs;
    public bool ByEvents;                                        // an event of this process names one of those verbs
}
// RProcess gains Rights (RList<RRightUse>, in file order; an inline right's entry is its use, which shares its id, and only the right
//   is found by id), RightsDefault ("allow" or "deny"), AccessGrid (RList<RAccessCell>) and MayRefuse (RList<RMayRefuse>).
// ResolvedModel gains Rights: the named rights. RActor and RVerb gain Rights: the rights that name them directly (an actor in actors or
//   except). RStep gains Verb, Values, End and Object (part 2: Attributes). RExpectation gains Refusal and RefusedBy.
//   Part 2: RScenarioObject and its links gain Frozen.
```

- **Templates read** `model.rights`, `process.rights`, `process.rights_default`, `process.access_grid`, `process.may_refuse`, the right's members (`right.access`, `right.actors`, `right.except`, `right.verbs`, `right.ends`, `right.condition_text`), `use.right`, `use.states`, `use.condition`, `actor.rights`, `verb.rights`, `step.verb`, `step.values`, `step.end`, `step.object`, `step.expect.refusal` and `step.expect.refused_by`. Part 2 adds `right.attributes`, `right.while_frozen`, `step.attributes` and `object.frozen`.
- **`access` has one meaning,** a right's `allow` or `deny`. The worked-out grid is `access_grid`.
- **`process.access_grid`** lists, for each atomic state, each actor named by the lifecycle's rights (in `actors` or `except`), events, tasks and gates (plus "everyone else"), and each verb those name, the answer of decisions 10 to 12, read as decision 31 says. It is filled only in a project that uses rights (decision 28).
- **`process.may_refuse`** lists the lifecycles that govern the process's subjects, other than itself, and could say no to an object, link or own verb its events or its rights name: they have a deny right naming it, or deny by default. Each entry gives those verbs, and whether an event names one. It is filled for lifecycles and for orchestrations with subjects, only in a project that uses rights. `csharp-dapper` writes the `StatesIn` hooks from the entries an event's verb reaches, and the grid keys on every entry.

**Dependency keys.**
- **The rights naming key, `rn:rights`.** Its hash counts, for each lifecycle with `rights` or `rightsDefault`, by id: its id, its default, the verbs its deny rights name (directly or through a named right), and the named rights it uses; then the id of every named right. It changes when a lifecycle gets its first right or loses its last, changes its default, or changes which verbs it can deny; not when a right's states, actors or condition change. In a project without rights it never changes, as `sn:subjects` doesn't in a model with no stereotype entry. It is worked out once per snapshot from the index rows, as `vn:verbs` is: a process row carries its default, the verbs its deny rights name and the named rights it uses, and a named right's row its access and verbs, each only where present. `Planning/DependencyHasher.cs` gains the `rn` prefix, and the plan explainer words it "The rights the lifecycles state".
- **The keys an entity's lifecycles already use** (`ResolveRun.Processes.cs`): the process kind (`k:process`), the referrers of the entity and each of its bases, the stereotype naming key `sn:subjects` and the carrier-set keys `cs:<key>` of the stereotypes involved. Below, "the governing keys" of an entity means these.
- **`process.may_refuse`** keys on the process file and `rn:rights`; in a project that uses rights, also on the governing keys of each subject's entity.
- **`process.access_grid`** keys on the same, and in a project that uses rights also on each named right the lifecycle uses, every actor file (membership), the verb vocabulary, and every entry of `may_refuse`: each governing lifecycle that could say no to any verb of the grid, including a verb that only the lifecycle's rights name, with that lifecycle's file (so its states and conditions) and the named rights it uses.
- **`actor.rights`** keys on the actor's referrers (`r:<id>`), as the actor's events and gates already do: every right that names the actor refers to it. So an actor page gains no key.
- **`verb.rights`** keys on `rn:rights`, and, once its hash holds anything, on `k:right`, each named right's file and the file of each lifecycle with rights. A project that uses verbs but no rights gains only `rn:rights` on its verbs page, whose hash never changes there.
- **`process.rights`** keys on the process file and each named right it uses. **`model.rights`** keys on `k:right`.
- So when lifecycle B gets its first right, or a deny on a verb that only A's rights name, a template that reads A's `may_refuse` or `access_grid` renders again, and A's `StatesIn<B>` hook is written when an event's verb reaches B. A change to the states or condition of such a deny renders the grid again through B's file. Only a template that reads these pays.

**Records:** process records gain `rights` and `rightsDefault`, step records the verb step's keys, expectation records `refusal` and `refusedBy`, each only where present. Named rights get records of kind `right`. Part 2 adds `frozen` on object records. So `get_resolved_model` answers don't change for a project without them.

## 7. Simulation and replay

- **One evaluator.** `Processes/RightsEvaluator.cs` works out decisions 10 to 12 for a verb, an actor or participant, the object's states in each lifecycle, and its frozen flags (always false in part 1). Each answering lifecycle answers on its own, then any "no" wins. It reads model elements from the snapshot, so the interpreter, `get_rights`, `process.access_grid` and the docs pack all use it.
- **Where the interpreter checks:** `HandleEvent` (`Processes/StatechartInterpreter.cs:184`) checks the event's list (as today), then denies and defaults, before `Select`. The gate signers that count as an allow (decision 14) come from the gates on the event's transitions from the active states. `HandleInvoke` (`:259`) does the same for a human task's `complete`, done or error. `CheckSigner` (`:823`) checks the lifecycle's denies on `sign` after the signer list. A new `HandleVerb` runs verb steps. In an orchestration with a subject, `HandleEvent` asks the subject's lifecycles about an event's object, link or own verb in the same way (decision 10).
- **States of the running lifecycle.** A condition of any answering lifecycle's right that reads the running lifecycle through `in_state` reads the running chart (decision 6).
- **The object's row** (object effects section 7): `modify` writes its values there. A delete marks the row removed and stops the chart (decision 25). Part 2 adds the frozen flags.
- **The trace** gains `refusedBy` and `rights` (each answering right with its access, in check order). Notes cover a verb the run didn't apply, a delete, and other lifecycles assumed or not asked in a scenario with no objects. All are left out when unused, so today's traces don't change: `refusedBy` is written in a project that uses rights, and on a step that expects it. Part 2 adds `frozen` (true while the subject is frozen), and notes for a repeated freeze or thaw and for sets on a frozen object.
- **Replay** compares `expect.refusal` and `expect.refusedBy` (MQ9308).
- **Replay keys** (`Resolution/ResolveRun.Processes.cs`) add the named rights a process uses. A scenario that checks an object, link or own verb (a verb step, or an event that names one) also keys on `rn:rights`. In a project that uses rights, it also keys on the governing keys of its subject's entity (of every subject entity of its process when it has no objects), and on each governing lifecycle that could say no to a verb its steps check, its events' verbs and its verb steps' verbs alike, with that lifecycle's file and the named rights it uses. So the scenario replays when another lifecycle gets its first right, starts governing the subject, gains a deny on a verb a step checks, or changes the states or condition of such a deny. A project without rights gains only `rn:rights` on those scenarios, and its hash never changes there.
- **Speed:** a lifecycle is evaluated only when it has rights or denies by default. Other lifecycles' states are worked out only when an object, link or own verb is checked, and only for lifecycles that could say no.

## 8. Tools

- **`get_rights` (new):** takes a lifecycle and, optionally, states, actors, verbs and an entity. Answers rows of state, actor, verb, `entity` (when answers differ by entity), answer (`allowed`, `denied`, `depends`), whether it is the default, the ids that decide (`by`), and `dependsOn` (the other lifecycles that may say no), with the lifecycle's default. It reads the snapshot and never resolves the whole model (decision 31). Pages with `cursor`, as `get_elements` does. Read-only. Part 2 adds an optional `frozen` input: without it, cells answer for a thawed object; with `frozen: true`, modify, connect, disconnect and delete are denied by freezing, and `whileFrozen` rights hold.
- **`simulate_process`, `record_scenario`, `verify_scenarios`:** verb steps, the trace's new members, and the expectations of decision 26. Part 2 adds objects' and links' `frozen`.
- **`preview_expression`:** previews a right's condition at its pointer, or text written for one. It gains an optional `lifecycle`: a named right's condition, or text for it, is previewed as if used there. Without it, a named right's condition is previewed in the scope of all its uses. A named right with no use needs one.
- **`save_element`, `apply_batch`:** accept `{ "text": … }` for a right's condition. An inline right's is parsed in its lifecycle's scope, a named right's in the scope of its uses, and a named right with no use keeps the text (decision 7). `text-to-trees` converts a named right's text once it has a use.
- **`get_references`, `referenced_by`:** a right's actors (in `actors` and `except`), own verbs, states, ends and condition paths; a use's named right; a verb step's end and object. Part 2 adds a right's and a step's attributes.
- **Deletes:**
  - A named right in use: refused, unless `remove-references` or `delete-dependents`, which both remove the uses.
  - An actor, state or end a right names in `actors`, `states` or `ends`: `remove-references` drops it from the list. **When the last one goes, the right goes too,** since an empty list would widen it to everyone, every state or the whole object. A holding's `within` goes with its object for the same reason (actors section 7). Part 2 treats `attributes` the same way.
  - An actor in `except`: `remove-references` drops it, and the right stays. An empty `except` means no exception, which is all that is left of an actor that no longer exists.
  - `rename-verb` and `remove-verb` walk rights too. A right whose last verb is removed goes.
- **`get_model_index`:** process rows gain `rights`, the count, only when above zero, and their `verbs` also list the verbs their rights name. Named rights get rows with `access` and `verbs`. So the Verbs tab counts rights among a verb's uses.
- **`export_process`, `import_process`:** rights travel in `meta.maquettiste`.
- **`validate`:** the rules of section 5.
- **The CLI** follows the same tools. **The rule list, the schemas, the conventions resource, the modeling skill and the tool texts** (`src/Maquettiste.Cli/Mcp/ProcessTools.cs` and `Agent/tools.json`) describe all of this.

## 9. Packs

Output changes only for a model that uses what each output needs (decision 28).

**`csharp-dapper`, part 1**
- **`Rights.cs`,** in the processes namespace, written only when a lifecycle has `rights` or `rightsDefault`: `RightAccess`, `RightDefinition` (its name, access, actors, except, verbs, ends, whether it has a condition, and its description), `RightRequest` (decision 29), `RightDecision` (allowed, the refusal and the refusing id), and `Rights.Check`, which follows decisions 10 to 12. A `Can<Verb>` method for each verb a right names calls it.
- **Process definitions:** each lifecycle's entry gains its rights with their states and its default, through an optional parameter written only when the project uses rights, as verbs did.
- **Conditions:** a static partial method `<Right>(RightRequest request)` per right with a condition: in `RightConditions`, in the named right's package namespace, for a named right, once however many lifecycles use it; in `<Lifecycle>RightConditions`, beside its lifecycle's handlers, for an inline right. The team's half of each class is written once. Each method is documented with the condition's text, and the check calls it.
- **Actors from the object:** the acting process's resolver (actors decision 27) also answers for those that another answering lifecycle's rights name.
- **What the caller holds:** `ICallerHoldings`, the team-owned stub `Dispatch/CallerHoldings.cs` (written once; it returns none, with the comment of decision 29), and each command handler's call to it, only in a project that uses rights. The generated registry passes it to each command handler; a container may register another, as the scenario tests do. `ProcessEnvelope`, the event endpoints' request bodies and `Pipeline.cs` don't change.
- **Other lifecycles' states:** `StatesIn<Lifecycle>(call)` for each entry of `process.may_refuse` that an event's verb reaches, on a lifecycle's handlers or an orchestration's.
- **The interpreter:** checks rights and defaults before guards for events and human tasks, with gate signers as an allow for the gated event, and `sign` at gates. `Refusals` gains `Right` and `Default`, and results gain `RefusedBy`, only when `Rights.cs` is written.
- **Operations:** no change (decision 30).
- **Scenario tests:** a generated `ScenarioHoldings` answers each step's held actors from its participant: the participant's actor and its holdings with no context. The generated test sets `ScenarioHoldings.Current` to the step's participant before each send, and clears it after. `ScenarioHost` is the team's own file, so generated code can't register `ScenarioHoldings` there: the team registers it once, in place of its `CallerHoldings` stub, and the pack's README and the release notes say how. Until then, a test whose step depends on a held actor fails, and its message names the missing registration. A refused step asserts the refusing id when it has `expect.refusedBy`. When `Rights.cs` is written, a verb step calls `Rights.Check` with the subject object's entity and states, and asserts its answer. Otherwise nothing in the model can refuse it, and it is written as a comment.
- **Nothing goes in `helpers.js`.**

**`csharp-dapper`, part 2,** only when the project uses freezing or attribute rights (decision 28): the interpreter checks frozen before rights for events that name `modify`, `connect`, `disconnect` or `delete`; `Refusals` gains `Frozen`; `RightRequest` gains the frozen flag, and with attribute rights the attributes; `RightDefinition` gains `WhileFrozen` when a right has it, and `Attributes` when a right names them. Whether an object or link is frozen comes from a hook in the team's half, a `partial void` method with a `ref bool frozen` argument. Until the team writes it, the answer is "not frozen" (decision 19).

**`process-docs`, part 1**
- **The lifecycle page,** when it has `rights` or `rightsDefault`: a Rights section with the default in a sentence ("Anything not stated here is denied."), a table of rights (name, allows or denies, who, except, does what, in which states, when, why), and a "Who can do what" table per state from `process.access_grid`. A cell that depends on another lifecycle says so ("Depends on HR").
- **A rights page,** only when there are named rights: each one, its condition, why, and the lifecycles that use it.
- **The scenario page:** verb steps, and refusals with their reason ("refused by SecretOnlyOwner: only its owner may change a secret item"), on the steps that have them.
- **The actor and verbs pages:** the rights that name each actor and verb.

**`process-docs`, part 2:** "While frozen" and "Attributes" columns when a right uses them, and frozen objects on the scenario page.

**`sql-ddl`, `seed-data` and the TypeScript sample:** no change.

## 10. Editor

**Process editor, a Rights tab** (lifecycles only), after Gates:
- **"When nothing is said:"** Allowed or Denied, at the top.
- **The rights grid:** one row per right, with Name, **Allows or denies**, **Who** (actor chips, or "Everyone"), **Except** (actor chips, empty by default), **Does** (the shared verb picker, several at once), **In states** (state chips, or "All states"), **When** (the shared value box from object effects, with the condition's type and findings), and **Why**. A Links column shows only when a right uses it. Part 2 adds **While frozen** and **Attributes** columns, shown only when a right uses them.
- A use of a named right shows the right's fields read-only, with only In states editable, and an icon button to open it (tooltip "Open the named right").
- **Add right** and **Use a named right…** at the foot. Icon buttons carry tooltips.
- **A toggle, Rights or Who can do what.** The second shows one state at a time (a state picker), actors as rows and verbs as columns. Each cell says Allowed, Denied or Depends, greyed when it is the default. A cell that depends on another lifecycle says which ("Depends on HR"). An entity picker shows when answers differ by entity. Clicking a cell lists what decides it, each a link. It reads `get_rights`. Part 2 adds a **Frozen** switch.

**Named rights:** a **Rights** folder in the Processes explorer, after Actors. The right editor has the same fields without states, Except included, and a **Used by** list. Its When box previews against a lifecycle that uses it, picked beside the box; with no use, it keeps the text and says the right needs a use. Search finds them.

**Scenario editor:**
- **Steps:** the input picker gains **Do a verb**. Its row has a verb picker (object, link and own verbs first), and cells for values and the link (end and object) as the verb needs. Part 2 adds an attributes cell for `read`.
- **Expectations:** **Refusal** and **Refused by** cells for a refused step, filled by "Update from replay".
- **Objects grid (part 2):** a **Frozen** box, and one per link.

**Simulation panel:** a **Do…** menu beside the event buttons runs a verb step as the chosen participant. A refusal shows its reason and links to the element. Each step lists the rights that answered. Part 2 adds freeze and thaw to the menu, and a badge on a frozen object.

**Elsewhere:** the actor editor lists the rights that name the actor, in Who or Except. The Verbs tab counts rights among a verb's uses.

**Quick fixes:** MQ9901, MQ9902 and MQ9903 remove the entry. MQ9904 removes the right or sets the default to deny. MQ9905 opens the event, or adds an allow right. MQ9906 removes the entry from Except. MQ9308 updates the expectations from a replay.

**Generated and mock files:** the editor's API types come from `openapi.yaml` by the fixed command. The kind's entries in `model/model.ts`, `model/labels.ts` and `app/navigation.ts` are added by hand. In `src/editor/src/mocks/model/`, the mock gains `rightRules.ts` beside `effectRules.ts`, and `stepper.ts` (1,491 lines today) gains the rights check and verb steps, a large part of the editor work; `validate.ts`, `refs.ts`, `store.ts`, `cascade.ts` and `modelIndex.ts` follow. `src/editor/src/mocks/processHandlers.ts` gains the `get_rights` handler. **The mock's `get_rights` is simpler than the engine's:** one row per cell, with no entity rows, so the editor's second part stays within one agent. The entity picker is tried against the engine in the browser pass. Part 2 adds the frozen flags and attribute checks to the mock.

## 11. A project that uses none of this

**After part 1:**
- **Files:** nothing rewrites a file, so saves, `format`, `format --check` and snapshot import behave as today. Recording a scenario writes what it writes today (decision 26).
- **Findings:** the same. Every new rule and case needs a right, a default or a verb step. Nothing new is checked on an operation's `requires` (decision 15). MQ9105 stays as it is.
- **Output:** the same bytes. Each pack change depends on what its output needs (decision 28). Generated operation code doesn't change (decision 30).
- **Simulation and replay:** the same outcomes and traces. An event's and a task's actor lists are checked as today, with the same refusal values. New trace members are left out.
- **Tool answers about the model:** the same. New record and index members are left out when unused, and `model.rights`, `process.access_grid` and `process.may_refuse` are empty. `get_rights` is new. The rule list, the schemas and the tool texts describe the additions.
- **Dependency keys:** the same, but for one key. A scenario that checks an object, link or own verb, and a template that reads `process.may_refuse`, `process.access_grid`, `actor.rights` or `verb.rights`, also keys on `rn:rights`. Its hash never changes in a project without rights, as `sn:subjects` doesn't without stereotype entries, so nothing renders or replays again because of it. The governing keys and other lifecycles join only in a project that uses rights.
- **Capabilities:** once built, these rows of `lifecycle-capabilities.md` become Yes: who may read, change or delete per state; the long list of rights per state; rights for the owner, everyone and named actors; a rule over the object's data and the current user; a named rule reused; combining; an access rule for every state; and what may be done in this state. A named rule reused across types without a lifecycle stays No until its own item (decision 8). Rights along containment stays No.
- **Speed:** the same. Rights are evaluated only where they exist.
- **Once, as on any upgrade:**
  - Every unit renders again and writes the same bytes, since the engine version is part of each unit's key.
  - The editor refreshes the `.schema` copies, or `validate` warns MQ1008 until refreshed.
  - The index cache is read in full once.
- **A team on mixed engine versions:** an older engine refuses the new kind and keys (decision 32).

**After part 2:**
- **A project that uses part 1 but neither freezing nor attribute rights** keeps its files, output bytes, findings, keys and outcomes. The new members and hooks are written only when used.
- **A project that uses neither part** keeps everything listed above, with one exception, which needs the owner's confirmation (decision 16): an event that names `freeze` now freezes the subject in a run. Only a scenario that then sends an event naming `modify`, `connect`, `disconnect` or `delete` changes. The project's generated code gains the frozen check and hook, and keeps building, since the hook answers "not frozen" until written. The release notes say that such events start freezing.
- **Capabilities:** rights on one attribute, and freezing objects and links, become Yes.
- **Once, as on any upgrade,** and **mixed engine versions:** as for part 1.

## 12. What comes next

| Item | What it takes from rights |
| --- | --- |
| Rights on types with no lifecycle | Its own item, later (the owner's answer 3). A named right gains an optional `subjects` key, entities or stereotypes, for object, link and own verbs. It needs a place in "who can do what", a way to run such a type in a scenario, and a place in generated code to check it. Adding it changes no file |
| Trigger points | The check kind is a right with code behind it. Freeze and thaw get their points. Checks on automatic moves, and `override` and `set-state` as steps, come there. The evaluator is where a check point's answer joins the others |
| Richer sign-off | Rights for `reject`, `ignore`, `withdraw`, `delegate` and `route` at a gate, checked at the signatures step. "No one holds both roles on one object" as a rule over holdings |
| Scenarios across objects | Verb steps on any object. `connect` and `disconnect` change the run's links, and freezing applies across objects |
| The generated runtime (4.6) | Generated code reads the object, so conditions, other lifecycles' states and frozen flags no longer go to the team |
| Revisions | `revise` gets its behaviour. Rights on it already work |
| If teams ask | Rights along containment; a subject's frozen field; a default per verb; quantifiers over to-many links in conditions; events a state ignores or defers (a chart item); a meaning for an operation's `requires` (decision 15). Each adds an optional key or a meaning, and changes no file |

**`lock` and `unlock`** have a fixed meaning (verbs decision 1): a lock reserves an object, so only the actor holding it may change it. Nothing enforces it, in this item or before. Rights can allow or deny `lock` and `unlock` now, but no run holds a lock. Enforcing it would change outcomes the way freezing does, so it needs its own item, if teams ask.

## 13. Decisions the owner must make

**Decided by the owner (2026-11-06):**
1. **How rights combine across lifecycles (decision 11).** Each lifecycle answers on its own, using its own rights and its own default, and one "no" decides it. A lifecycle that allows by default and says nothing about the verb answers yes, so it does not veto another. This is also the rule for "who can do what" (decision 31).
2. **`except` (decision 4).** Added. It means the right does not apply to the listed actors; other rights and the lifecycle's default still do. It never grants. With the usual default of allow, "deny the change, except the owner" is the whole rule.
3. **Rights on types with no lifecycle (decision 8).** Later, as their own item. Part 1 leaves every file readable by that item.
4. **Names.** `access` replaces `effect`, since an operation's `effect` already means read or change. The other names stay: `rights`, `rightsDefault`, the kind `right`, `whileFrozen`, `frozen`, `ends`, `attributes`, `except`; the step input `verb` with `values`, `attributes`, `end` and `object`; `expect.refusal` and `expect.refusedBy`; the refusal values `right`, `default` and `frozen`; and the tool `get_rights`.
5. **Two parts (decision 32).** Part 1 holds rights, named rights, verb steps, refusal reasons and "who can do what". Part 2 holds freezing and rights on attributes.

**Still open:**
1. **Before part 1's release: confirm three new names.** They came from fixing the checks' findings, and names are permanent once released:
   - the part kind **`process-right`**, for each entry of a lifecycle's `rights` (decision 3). Tools report it, and qualified names use it. The bare word `right` is the named right's kind.
   - **`entity`** in `get_rights` rows (decision 31): the entity a row is for, when answers differ by entity. The second check chose it over `subject`, which is already a part kind, and which resolved subjects use for entry ids.
   - **`dependsOn`** in `get_rights` rows: the other lifecycles a "depends" cell waits on. Packages, routines and SQL objects use the word for order, but here it appears only in tool output.

   Recommended: as listed. Confidence: medium-high. The build proceeds with them, and the release waits for the owner's word.
2. **Before part 2 only: confirm that naming `freeze` is the opt-in (decision 16).** From part 2, an event that names `freeze`, which 0.14.0 already allows, freezes the subject in a run, with no separate switch. The generated frozen hook answers "not frozen" until the team writes it, so such a project keeps building, and the part 2 release notes say that these events start freezing. Recommended: yes. Confidence: medium-high.

## 14. Tests

**Part 1**
- **`RightRuleTests` (new):** failing and passing cases for MQ9901 to MQ9906 and MQ9308, and the new cases of MQ3001, MQ2001, MQ2002, MQ9701, MQ9704, MQ9604, MQ9106, MQ3101, MQ3102, MQ3108, MQ3104 and MQ9305, including `ends` beside `delete` and `values` that don't fit. Today's models give the same findings, including one whose operations list `requires`.
- **`RightsEvaluatorTests` (new):**
  - One lifecycle: deny, then allow, then the default.
  - Across lifecycles: any "no" wins. HR denies by default and Projects allows project managers to `modify`: the manager is refused by HR's default. A lifecycle that allows by default and is silent answers yes. Which lifecycles answer for each verb group; an orchestration with a subject whose event names `modify`, answered by the subject's lifecycles, and one with no subjects, closed lists only; a scenario with no objects.
  - `except`: a listed actor, a member of a listed role, the object's owner with and without an owner field; the default answers for an excepted actor, under allow and under deny; `except` never grants.
  - Empty actors as everyone; membership at any depth, deny included; actors from the object; a participant who satisfies an actor other than the one it acts as.
  - States, compound states and conditions, unknown included; `in_state` on the running lifecycle from another lifecycle's right reads the live states; `create` in the initial state; `ends`, with `freeze` and `thaw` speaking of links.
  - The closed lists as allows that can't be widened; gate signers as an allow for the gated event under a deny default; a deny on the gated event's verb stops signing; a human task's error checked as `complete`.
  - The fixed order and the refusing id for each row of decision 12, an explicit deny before a default, and `no-transition` after the defaults with no `refusedBy`.
- **`RightsReplayTests` (new):**
  - Verb steps: `modify` values seen by a later guard; `modify` on a link checked and recorded only; delete, then later event, invoke, verb and time steps, and the outcome; verbs recorded but not applied, with notes; a `freeze` or `thaw` step is MQ9305.
  - Refusals at each check, `expect.refusal` and `expect.refusedBy`, MQ9308; recording with and without rights.
  - Other lifecycles' states from `states`, a bound value and the initial state.
  - Replay keys: lifecycle B gets its first right, starts governing the subject through a base or a stereotype it names, or changes its default, and a scenario of lifecycle A with a `modify` event replays; B gains a deny on `delete`, or changes the condition of that deny, and a scenario whose verb step deletes replays; a project without rights keeps `rn:rights` at one hash.
  - Unchanged traces and replays for a model without rights.
- **`NamedRightTests` (new):** loading, references, `get_references`, localized texts; a condition with no use kept as text with MQ3108 only; two uses over different subjects, parsed in their common scope and typed per use; a new use whose subjects lack an attribute, MQ3102 at the use; `preview_expression` with and without a lifecycle; deletes under each choice, including the last actor, state or end, and an actor in `except`; `rename-verb` and `remove-verb` across rights; index rows, by patch and by full build; the `process-right` part kind in the name index.
- **`ResolvedRightsTests` (new):** `process.rights` and `use.condition`; `process.access_grid` with membership (a member role's right on the parent's row and the other way round), two events naming one verb with different lists, `except`, "depends" for a condition, an actor from the object and another lifecycle (with `dependsOn`), and one row per entity where answers differ; `process.may_refuse`; `model.rights`, `actor.rights`, `verb.rights`, records.
- **`RightsKeyTests` (new):** the `rn:rights` hash changes for a first right, a default and a new deny verb, and not for a right's states or condition; `process.may_refuse` and `process.access_grid` render again when another lifecycle gets its first right, starts governing a subject, or gains a deny on a verb only the lifecycle's rights name, and the grid when that deny's states change; the `rn:rights` hash read from index rows equals one worked out from the files; an actor page's keys stay the same; `verb.rights` keys in a project that uses verbs but no rights hold only `rn:rights`.
- **`GetRightsTests` (new):** the tool's rows, filters and pages agree with `process.access_grid`; it answers without resolving the model; a project without rights gets answers from the closed lists and the default while `process.access_grid` is empty.
- **No change:** a model without rights gives the same output, files, `format --check`, findings, replay, traces and tool answers, and its keys gain only `rn:rights`. The pack golden tests pass untouched.
- **Pack tests:** a new case per pack. `csharp-dapper`'s `Rights.cs`, condition partials (a named right used by two lifecycles has one; an inline right and a named right whose names would join into one name both build), the `StatesIn` hook (written only when another lifecycle could say no, on an orchestration's handlers too), the `CallerHoldings` stub and interpreter checks build with warnings as errors and run. With the stub, a caller is checked as its acting actor only; a request body that names held actors changes nothing. Scenario tests run with `ScenarioHoldings`: a participant acting as `Author` who holds a role a right denies is refused, as in the engine. A model whose scenarios only expect `guard` writes no `Rights.cs`. `process-docs`' Rights section, table, "Depends on" cells, rights page and refusal lines.
- **Editor:** unit tests for the grid's columns, the cell answers and the verb step's cells per verb. Browser tests (in the browser pass) for adding rights with Except, using a named right, setting the default, reading Who can do what with a "Depends on" cell and the entity picker, a verb step refused by a right, and updating expectations from a replay.
- **Acceptance,** on an invented model:
  - A document lifecycle that denies by default, with the rights of section 4, and the scenario there, with no owner field.
  - An employee governed by an HR lifecycle that denies by default and lets HR staff `modify`, and by a project lifecycle that lets project managers `modify`. A project manager is refused by HR's default.
  - A gated approval under a deny default: signers sign without an allow on `promote`, and a right denies `sign` to the submitter (`subject.submittedBy = current_actor()`), refused at the signatures step.

**Part 2**
- **Freezing:** freeze and thaw by a verb step and by an event; repeated; frozen links; "frozen" refused before any right; `whileFrozen`; a move's sets on a frozen object apply, with a note; an event that names `freeze`, then one that names `modify`, is refused.
- **Attributes:** a modify step checked per attribute against attribute rights only; `read` with `attributes`; MQ9903's attribute case; deletes of a named attribute; "depends" cells for attribute rights.
- **`get_rights`** with `frozen: true`.
- **Packs:** the frozen check, hook and new members written only when the project uses freezing or attribute rights; `Refusals.Frozen`; a 0.14.0 model whose events name `freeze` builds with the hook unwritten, and its check refuses nothing for freezing.
- **No change:** a project that uses part 1 only keeps its output bytes, findings and outcomes.
- **Acceptance:** the part 2 steps of section 4; an HR lifecycle where `salary` can't be read or modified except by HR staff.

Each stale-output test is checked to fail without its fix.

## 15. Tasks and the build plan

### Tasks

The sizes are days for a person who knows the code, with tests and a review. They show how the groups compare.

**Part 1**

| Wave | Group | What | Size |
| --- | --- | --- | --- |
| 1 | Contract and model | The schemas, `openapi.yaml`, rule texts; the trace and refusal members and the step expectation in the engine; the `right` kind and the `process-right` part; `rights`, `rightsDefault` and `except`; the `Right` site kind and a named right's condition scope; MQ9901 to MQ9906 and the changed rules | Two days |
| 1 | References, deletes and the evaluator | References, deletes and their widening guard, `except`, `rename-verb` and `remove-verb`, index rows, localization; `RightsEvaluator` | A day and a half |
| 1 | Editor, part one | The Rights tab and grid, named rights in the explorer and their editor, quick fixes, the mock's validation and references | A day and a half |
| 2 | Simulation and scenarios | The interpreter's checks, verb steps, the trace, MQ9308, recording, replay keys | A day and a quarter |
| 2 | Resolved model, keys, tools and docs output | `RRight`, `process.access_grid`, `process.may_refuse`, `rn:rights` and the other keys, records, `get_rights`, `preview_expression`'s lifecycle, tool texts; `process-docs` | A day and a half |
| 2 | `csharp-dapper` | `Rights.cs` and the request record, `ICallerHoldings` and its stub, condition partials, the `StatesIn` hook, the interpreter's checks, scenario tests | A day and a quarter |
| 2 | Editor, part two | Who can do what, scenario verb steps and expectations, the simulation panel, the mock's stepper and a simple `get_rights` | A day and a half |
| 3 | Docs | User guide (processes, scenarios, rights, expressions), releases, modeling skill, capabilities rows, `next.md` | Half a day |

That comes to about eleven days for a person.

**Part 2**

| Wave | Group | What | Size |
| --- | --- | --- | --- |
| 1 | Contract and engine | The schemas, `openapi.yaml`, rule texts; `whileFrozen`, `attributes`, the frozen keys, the `frozen` refusal and trace member; MQ9903 and MQ9305 cases; references and deletes for attributes; the evaluator's frozen row and attribute checks | A day and a half |
| 1 | Editor | The new columns, Frozen boxes, freeze and thaw in Do…, the attributes cell, the Frozen switch, the mock | A day |
| 2 | Simulation | Freeze and thaw take effect, from steps and events; frozen flags; per-attribute modify; sets on a frozen object; replay keys | A day |
| 2 | Output | `RRight`'s new members, records, `get_rights`' `frozen`; both packs, the frozen check and its hook | A day |
| 3 | Docs | User guide, releases (with the note that events naming `freeze` start freezing), modeling skill, capabilities rows, `next.md` | Half a day |

That comes to about five days for a person.

**Checklist for every group.** Anything that reads or writes a lifecycle, an actor list, a verb, a scenario step or a refusal must be found and handled:
- **A new element kind and part kind:** `Model/ElementKind.cs`, `Model/KindInfo.cs` (folder `model/rights`), `Model/ElementBase.cs`, `get_model_kinds`, the localizable node kinds in `Localization/LocaleChains.cs`; for `process-right`, `Model/ModelIndexer.cs` (the part kind map and `SubElementKinds`), `Model/PropertySchemas.cs` (`TranslatedHolderKinds`), `Loading/NameIndex.cs` (a case in `Holders`) and the list-to-kind map in `Loading/ChangePlanner.Cascade.cs`.
- **Expressions:** the `Right` site in `Expressions/ExprType.cs` and every site switch the `Set` kind needed (object effects decision 3), a named right's scope across its uses, and `Validation/ExpressionRules.cs` (`ExprSites.Of`).
- **Processes:** `Model/Processes.cs`, `Processes/StatechartInterpreter.cs`, `Processes/ScenarioReplayer.cs`, `Processes/StepTrace.cs` (`Refusals`), `Processes/ScenarioObjects.cs`, `Processes/ObjectStates.cs`, `Processes/Participants.cs`, `Processes/ProcessApi.cs`, `Processes/SubjectCoverage.cs`, and the exporter and importer.
- **Resolver, keys and records:** `Resolution/ResolvedProcesses.cs`, `Resolution/ResolveRun.Processes.cs`, `Editor/ResolvedRecords.cs`, `Planning/DependencyHasher.cs` (`rn`), `Generation/PlanExplainer.cs` (its words).
- **Tools:** `src/Maquettiste.Cli/Mcp/ProcessTools.cs` and `Agent/tools.json`, each owned by one agent per wave.
- **Contracts:** the schema copies, `openapi.yaml` and its generated files. `validation-rules.json` is written only by the fixed command.
- **Test harness:** `RenderKit.WithSettings` and `ResolvedCoverageTests`.

### How it gets built

**What earlier items cost.** 4.1 took about 3.75 hours for about 11,000 lines: about 155 output tokens, 425 new input and 40,000 cached re-reads per line. Verbs, about 5,000 lines, took about 2.5 hours. Object effects, about 6,000 lines written by hand (10,500 in the diff with pack expected files), took about 3.5 hours, slower per line because of the typer and the simulator. Engine builders cost about 20M cached per 100 steps. In every item, the review and the browser pass each found real bugs.

**This item's size,** with tests, without pack expected files. The second check found part 1 light: the pack output, the editor's mock and the wait for a free slot in wave 2. Its figures are used for part 1. The third check's fixes (the holdings hook, wider keys, and orchestration events answered by the subject's lifecycles) add about 300 lines, which stay within part 1's range.
- **Part 1, about 7,500 to 8,500 lines:** contract, rules and references about 1,350 and their tests about 800; the evaluator about 550 and its tests about 450; simulation and scenarios about 550 and their tests about 550; resolved model, keys, tools and packs about 1,200 and tests about 400; editor and mock about 1,800; docs about 350.
- **Part 2, about 3,000 lines:** contract, rules and references about 450 and tests about 350; freezing and attributes in the evaluator and the simulator about 450 and tests about 450; resolved model and packs about 400 and tests about 150; editor and mock about 600; docs about 150.

**Agents.** Each is capped at about 100 steps or 200k context. Opus 5.5 builds the engine and pack output and runs every review. Sonnet 5.5 builds the editor, the mock and the docs.

**Part 1 agents**

| Wave | Agent | Model | Steps | Starts from |
| --- | --- | --- | --- | --- |
| 1 | Contract and model; **contract first**: the schemas, all `openapi.yaml` changes including the trace, the refusal values, records, `get_rights` rows and `preview_expression`'s lifecycle; the trace and refusal members in `Processes/StepTrace.cs` and the step expectation, so later agents share them; rule texts. Keeps every change to `Validation/ExpressionRules.cs` | Opus | About 100 | `origin/main` (0.14.0) |
| 1 | References, deletes and `RightsEvaluator`, through the engine's planner, not the tool files | Opus | About 100 | The contract commit |
| 1 | Editor and mock, part one | Sonnet | About 100 | The contract commit |
| 2 | Simulation and scenarios, through `Processes/ProcessApi.cs`, leaving the tool files to the output agent | Opus | About 90 | Wave 1's engine agents |
| 2 | Resolved model, keys, records, `process.access_grid`, `process.may_refuse`, `get_rights`, replay keys, and `process-docs`. **Owns `ProcessTools.cs` and `tools.json`** | Opus | About 100 | Wave 1's engine agents, beside the simulation agent |
| 2 | `csharp-dapper` output | Opus | About 90 | The resolved model agent's first commit (`RRight`, `process.rights` and `process.may_refuse` filled), in the first free slot |
| 2 | Editor and mock, part two, with the simpler mock `get_rights` | Sonnet | About 100 | Wave 1's editor agent |
| 3 | Review of the pull request | Opus | About 60 | All builders' finish |
| 3 | Docs | Sonnet | About 50 | The builders' finish, beside the review |
| 3 | Browser pass: the editor flows, on the built branch | Sonnet | About 50 | The builders' finish, beside the review |
| 4 | Fixers, split by area (engine and output; editor and mock; docs) | Opus or Sonnet | About 40 each | The review's and the browser pass's findings |
| 4 | A short second browser pass on the fixes | Sonnet | About 25 | The editor fixer's finish |

**Part 2 agents**

| Wave | Agent | Model | Steps | Starts from |
| --- | --- | --- | --- | --- |
| 1 | Contract and engine; **contract first**: the schemas, all `openapi.yaml` changes, the `frozen` refusal and trace member, rule texts; then the evaluator's frozen row and attribute checks, and references and deletes for attributes | Opus | About 100 | `main` once part 1 is merged, or part 1's branch while it waits |
| 1 | Editor and mock | Sonnet | About 80 | The contract commit |
| 2 | Simulation: freeze and thaw take effect, frozen flags, per-attribute modify, sets on a frozen object, replay keys | Opus | About 70 | The contract and engine agent's finish |
| 2 | Resolved model, records, `get_rights`' `frozen`, both packs and the frozen check and hook. Owns `ProcessTools.cs` and `tools.json` | Opus | About 70 | The same, beside the simulation agent |
| 3 | Review of the pull request | Opus | About 50 | All builders' finish |
| 3 | Docs | Sonnet | About 30 | The builders' finish, beside the review |
| 3 | Browser pass | Sonnet | About 30 | The builders' finish, beside the review |
| 4 | Fixers, split by area | Opus or Sonnet | About 30 each | The findings |

- **At most three agents run at once.** In part 1, the evaluator comes in wave 1, so the wave 2 engine agents use it without waiting on each other. The simulation agent works in `Processes/` and the process API. The resolved model agent works in `Resolution/`, `Planning/`, `Editor/ResolvedRecords.cs`, the tool files and `process-docs`. The `csharp-dapper` agent works only in its pack. Wave 2 has four agents, so the `csharp-dapper` agent takes the first free slot, which makes wave 2 about 1.6 hours.
- **One pull request per part, into `main`,** with the contract, engine, editor, output, docs and fix commits. Each is its own release. The owner merges part 1 first.
- **Generated files** (the editor's API types, the recorded rule list, the sample app's copies) are written only by the fixed commands, after each merge into the branch. Pack expected files stay with the builder, so their changes are reviewed.
- **Fixers** are fresh agents with the exact findings. **A design gap found while building** stops the work, and the owner is asked.

**Estimate.**

**Part 1**

| | Wall time | Output | New input | Cached re-reads |
| --- | --- | --- | --- | --- |
| Wave 1 (contract, model, references, evaluator, editor part one) | About 1.4 hours | 0.35M | 0.9M | 75M |
| Wave 2 (simulation, resolved model and docs output, `csharp-dapper`, editor part two) | About 1.6 hours | 0.4M | 1.0M | 90M |
| Wave 3 (review, docs, browser pass) | About 0.6 hours | 0.15M | 0.4M | 35M |
| Wave 4 (fixes, second browser pass, CI) | About 0.6 hours | 0.1M | 0.3M | 45M |
| In all | **About 4 to 4.5 hours** | **About 1.0M** | **About 2.6M** | **About 230 to 260M** |

**Part 2**

| | Wall time | Output | New input | Cached re-reads |
| --- | --- | --- | --- | --- |
| Wave 1 (contract and engine, editor) | About 0.7 hours | 0.15M | 0.4M | 35M |
| Wave 2 (simulation, output) | About 0.6 hours | 0.15M | 0.4M | 35M |
| Wave 3 (review, docs, browser pass) | About 0.4 hours | 0.1M | 0.25M | 25M |
| Wave 4 (fixes, CI) | About 0.4 hours | 0.05M | 0.15M | 30M |
| In all | **About 2 to 2.5 hours** | **About 0.45M** | **About 1.2M** | **About 120 to 135M** |

**Both parts:** about 10,500 to 11,500 lines, about 6 to 7 hours, about 1.45M output, about 3.8M new input and about 350 to 395M cached re-reads. The split itself costs about 0.5 hours and 40M cached: a second pull request, review, browser pass, CI run and release. The output and new-input figures follow 4.1's cost per line, a little under, as object effects did. The cached figures follow the checks' reading, which sits between a count by steps (about 20M per 100 Opus steps and 15M per 100 Sonnet steps, plus coordination) and 4.1's cost per line.
