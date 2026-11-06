# Rights: design

**Status (2026-11-06): designed, with the owner's answers applied; awaiting a second independent check; not built.** It designs the fifth item in the owner's order after 4.1: rights, with named rights, defaults, combining, freezing and refusal reasons in scenarios. It is built in two parts, each its own release (decision 32). [Who, what and when](lifecycle-actors-scenarios.md) section 7 sets its direction. [What lifecycle systems can do](lifecycle-capabilities.md) is the yardstick. [The actors design](actors-design.md) (decisions 22 and 23 were recorded for this item), [the verbs design](verbs-design.md) and [the object effects design](object-effects-design.md) give its parts. The first independent check found three parts that couldn't be built as written, nine points to fix and six minor ones; each is settled below, and the owner answered its questions. A file path without a project prefix is under `src/Maquettiste.Engine/`. The examples are invented. Each new name and format is held to the roadmap's test: it stays for good, it must help any team whether they use it or not, and it stays optional.

## 1. In short

Today "who may act" is only a closed list: an event's actors, a gate's signers, a human task's actors. Nothing says what may be done to an object in a state. Nothing can deny. Nothing can be frozen. A scenario can expect a refusal, but not why.

| Layer | What changes |
| --- | --- |
| The model | A lifecycle lists **rights**. A right **allows** or **denies** some **actors** some **verbs**, in some **states**, under an optional **condition**. It may list actors it does not apply to, with **`except`**. Its description says why. A **named right** is its own element, which many lifecycles use: what lifecycle systems call a rule. Each lifecycle has a **default** for what no right mentions: allowed, unless it says denied. **Each lifecycle answers on its own, and any "no" wins.** An event's actors, a task's actors and a gate's signers keep their meaning, and count as rights. In part 2, **freeze** and **thaw** take effect, and a right can name **attributes** |
| Simulation and scenarios | Checks run in a fixed order: rights, guards, signatures. Part 2 puts "frozen" first. A new **verb step** lets a participant modify, delete or do any other verb to the object. A refused step names the element that refused it, and a scenario can expect that element |
| Code generation | Templates read each lifecycle's rights, its default and a worked-out "who can do what". `csharp-dapper` writes a rights table and one check, where it already checks actors, and leaves conditions to the team. `process-docs` adds a Rights section, a "who can do what" table and a page of named rights. Each happens only when the model uses rights |
| The team's system, or a lifecycle system | Nothing changes until the team uses the new output or builds from the model |

**A project that uses none of this** keeps its files, output bytes, findings, tool answers, dependency keys and speed. Section 11 lists everything, for each part.

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
- **Verb steps** keep `verb`, `values`, `end` and `object`. Part 2 adds `attributes`, and allows `freeze` and `thaw` as verb steps. In part 1 those two are MQ9305, so no part 1 file holds a step whose outcome part 2 would change.
- **A right** keeps its keys. Part 2 adds `whileFrozen` and `attributes`, both optional. A right without them means the same in both parts.
- **The evaluator** takes an object's frozen flags from the start. In part 1 they are always false.
- **Generated code.** `Rights.Check` takes one request record, so part 2 adds the frozen flag as a member without breaking a call the team wrote. Part 2 adds `Refusals.Frozen`. Both are written only when a project uses freezing.
- **Tools.** `get_rights` keeps its rows. Part 2 adds an optional `frozen` input.

## 3. Decisions

Each decision gives its confidence: **high**, **medium-high**, **medium** or **low**. A decision marked **(part 2)** is built in part 2. The others are part 1.

### Where a right lives, and its shape

1. **Rights live on the lifecycle, in one list, `rights`. Each right names the states where it holds; none means every state.** Confidence: high.
   - **Why:** one right often spans several states ("no edits in Released or Obsolete"). Stored per state, it would be repeated in each. Lifecycle systems show access per state, and the editor shows that grid too (section 10). The grid is a view, the list is the file.
   - **The alternative:** `rights` on each state. It repeats a right in every state it spans, and a named right would need a use in each state. It is not chosen.
2. **Only lifecycles hold rights.** Confidence: medium-high. A right speaks of an object in its states. An orchestration is a flow, not an object's state, and a library is shared parts. Both keep their events' and tasks' actor lists. `rights` or `rightsDefault` elsewhere is MQ9901.
3. **A right has a `name`, an `access`, `actors`, `verbs`, `states` and an optional `condition`.** Confidence: high, for the shape and the names (the owner's answer 4).
   - `name` is required and is an identifier, as a guard's is. It lets a refusal name the right. `displayName` is optional.
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
   - **`create`** is checked as if the object were in the lifecycle's initial state, since that is where a new object starts. Confidence: medium-high.

### Named rights

7. **A named right is its own element, kind `right`, in `model/rights/`. A lifecycle uses it with an entry `{ id, right, states }`.** Confidence: medium-high.
   - **Why an element:** it is reused across lifecycles, has its own page, references, package and translations. An actor from the object is reused the same way (actors decision 8).
   - **Why not in a library process:** a library's members read the using process by name, but conditions name attributes by id, which MQ9020 already refuses in libraries.
   - **The use says only where:** its `states`. Who, what, when and the access belong to the named right. A variant is another right.
   - **Checked at each use:** its condition's paths, its `ends`, and the actors from the object in its `actors` and `except` must resolve on every subject of each lifecycle that uses it, as a library's gate is checked at each process (actors decision 11). Part 2 adds `attributes`. A failure is reported at the use.
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
    | 3 | Denies: every answering lifecycle's deny rights that hold; the acting lifecycle's first, in file order, then the others by name | `right` | The right: inline, or the named right |
    | 4 | Defaults: the first answering lifecycle, in the same order, where no allow holds and the default is deny | `default` | The lifecycle |
    | 5 | Guards, as today | `guard` | The guard |
    | 6 | Signatures: the gate's signers, then the lifecycle's denies on `sign`, then a repeat signer, then a missing reason | `gate-signer`, `right`, `gate-repeat`, `gate-reason` | The gate or gate policy, or the right |

    - Rows 3 and 4 together are decision 11: a lifecycle says no when a deny holds, or when nothing allows and its default is deny. An explicit deny is reported before any default, so the more telling reason wins.
    - Rights are checked on the event's verb before any guard picks a transition, as verbs decision 12 planned. `sign` is checked at the signatures step, since only then is it known that the occurrence is a signature (verbs decision 15).

### Who may is said once

13. **An event's `actors` stays a closed list, and counts as a right.** Confidence: high.
    - The listed actors get an allow for that event, in its own lifecycle. Everyone else is refused by the event, as today (`actor`).
    - Explicit rights narrow it, since a deny wins. They can't widen it: an allow on `promote` doesn't let a Manager send an event that lists only Clerks.
    - For an event that names an object, link or own verb, other lifecycles still answer (decision 10). The event's list is an allow only in its own lifecycle.
    - **An event with no verb** is outside rights that name verbs. Its actors and its lifecycle's default still apply. Under `deny`, an event with no verb and no actors can never be sent (MQ9905 warns).
    - Outcomes for today's models don't change.
14. **A human task's `actors` is the same, for `complete`. A gate's `signers` is the same, for `sign`, and for the gated event.** Confidence: high.
    - Rights on `complete` and `sign` narrow them.
    - **A gate's signers count as an allow for the gated event, as the event's actors do.** Every occurrence of a gated event is checked against the event's verb at rows 3 and 4 (verbs decision 15). Without this, under a deny default, a signer couldn't sign without also being allowed to promote. The signers that count are those of the gates on the transitions the event can take from the active states.
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
    - **The one change for a project that uses no rights.** An event may already name `freeze` (0.14.0), and does nothing in a run. From part 2 it freezes the subject, as verbs decision 16 fixed. A scenario changes only when it sends such an event and later one that names `modify`, `connect`, `disconnect` or `delete`. Part 2's release notes say so, and `verify_scenarios` names the steps.
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
19. **(Part 2) Where "frozen" is held is the team's system's business.** Confidence: medium-high. As with an owner without an owner field (actors decision 10), nothing in the model holds it. Generated code asks the host, through a hook written only when the project uses freezing. A subject's frozen field can come later, if teams ask, without changing a file.

### Rights on links and attributes

20. **`ends` narrows a right to links through those ends of the subject.** Confidence: medium-high.
    - With `ends`, `connect`, `disconnect`, `read` and `modify` speak of those links, and in part 2 `freeze` and `thaw` too. "Deny connect through `components` in Released."
    - Without `ends`, link verbs speak of every link, and `read` and `modify` (part 2: `freeze` and `thaw`) speak of the object.
    - Lifecycle systems also set connect and disconnect from each end. An end is one side of a relation, so this needs nothing more.
    - **Scenario links hold no values,** so a `modify` step on a link is checked and recorded only (decision 25).
21. **(Part 2) `attributes` narrows a right to reading or modifying those attributes.** Confidence: medium-high.
    - "Deny read of `salary`, except to HR staff." Only with `read` and `modify` (MQ9903).
    - A modify step that changes several attributes is checked per attribute, but only against rights that name attributes. One denied refuses the step. A right without `attributes` is checked once, as in part 1, so part 1 outcomes don't change.
    - **In "who can do what":** a `read` or `modify` cell that a right with `attributes` would change answers "depends", and names that right.
    - The direction placed this "later in the same item". It is part 2.
22. **Rights passed along containment are not in this item.** Confidence: medium-high. An actor from the object with `within` already says "the project's reviewers may change its items". Inheriting a container's rights, in the container's own state, needs a containment rule on relations and the container's state in every run. It can come later, if teams ask, as an optional key.

### Simulation and scenarios

23. **Rights check inputs an actor makes:** events of the process the scenario runs, human-task completions, signatures, and verb steps. Confidence: medium-high.
    - **Not checked:** automatic moves, timers and finished invokes that are not human tasks. The chart decides those, and a lifecycle that denies `promote` to everyone in a state shouldn't stop its own timer. Trigger points' check kind may stop them later.
    - **Not checked:** events of an invoked sub-process, which sees no object. Their own actor lists apply, as today.
24. **In a run, a right applies to a participant who satisfies any of its actors and none of its `except`, not only the actor the step acts as.** Confidence: medium-high.
    - A deny on `Owner` applies to Bea even when she acts as `Author`. An `except` that names `Owner` leaves Bea out even when she acts as `Author`. This follows actors decision 17 for what "satisfies" means.
    - A step with only an `actor` matches that actor and its memberships, and an actor from the object by exact match, as today. The same holds for `except`.
25. **A new step input, `verb`: a participant does a verb to the scenario's subject, or to one of its links.** Confidence: high that it is needed (verbs decision 18); medium-high for its shape.
    - **Who acts:** as for an event step, the step's `actor`, or the participant's when it names only a participant.
    - **Keys:** `verb`; `values` (attribute id to value) for `modify` on the subject; `end` and `object` for a link. Without `end`, the step acts on the subject. Part 2 adds `attributes` for `read`.
    - **Which verbs:** object verbs, link verbs and the project's own. Moves, signatures and task completion keep their own steps (an event, `invoke-done`). `override`, `set-state`, `reject`, `ignore`, `withdraw`, `delegate`, `route` and `claim` get steps with their own items (MQ9305 until then). In part 1, `freeze` and `thaw` steps are MQ9305 too, until part 2 gives them their effect.
    - **What the run does when accepted:**
      - `modify` on the subject applies its `values` to the object's row, so later guards and sets see them. They show in `valuesChanged` and `expect.values`, keyed by attribute id, as sets are (object effects decision 4). On a link, it is checked and recorded only, since scenario links hold no values.
      - `delete` removes the object. Later steps are refused (`no-transition`), with a note.
      - The rest (`read`, `create`, `copy`, `revise`, `lock`, `unlock`, `change-owner`, `change-type`, `change-lifecycle`, `connect`, `disconnect`, own verbs) are checked and recorded only. A note says the run didn't apply them. Scenarios across objects will apply `connect` and `disconnect`.
      - Part 2: `freeze` and `thaw` change the flag.
26. **A refused step names the element that refused it, and a scenario can expect it.** Confidence: high for the shape; medium-high for when recording writes it.
    - The trace gains `refusedBy`, the id from decision 12, and `rights`, the rights that answered, each with its access. Two refusal values are new: `right` and `default`. Part 2 adds `frozen`.
    - A step may expect `refusal` (the value) and `refusedBy` (the id). When either differs, it is MQ9308. Both need `accepted: false` (MQ9305).
    - `record_scenario` writes both for a refused step when the project uses rights (decision 28). Otherwise it writes what it writes today.
27. **Other lifecycles' states come from the scenario,** as `in_state` reads them (object effects decision 10): the object's `states`, else its bound value, else the initial state with a note. Confidence: high.

### Code generation

28. **Each output depends on what it needs, so a project pays only for what it uses.** Confidence: high.
    - **A project uses rights** when a lifecycle has `rights` or `rightsDefault`. Only then is `process.access` filled and keyed, are `Rights.cs` and the interpreter's checks written, and does recording write refusing ids. A named right acts only through a use.
    - **A named right** reaches `model.rights`, its index row and the docs' rights page, even before a lifecycle uses it.
    - **Scenario keys reach only their scenario.** A verb step, or an expected `refusal` or `refusedBy`, changes that scenario's records, page and test, and nothing else. A scenario that expects `guard` doesn't turn on `Rights.cs`.
    - Section 9 gives the test for each output. Otherwise every template sees empty lists, and the new trace and record members are left out.
29. **`csharp-dapper` writes one rights table and one check. Conditions, and other lifecycles' states, go to the team.** Confidence: medium-high.
    - Generated code can't read the object until the generated runtime (4.6), as 4.1 decided for guards and object effects for sets. So each condition is a partial method the team writes, and a new one fails the build until written.
    - **Every condition is a partial method of one `RightConditions` class,** whose team half is written once. A named right's condition is written once, however many lifecycles use it. An inline right's is named after its lifecycle and the right. A condition reads no context or payload (decision 6), so one method serves every lifecycle, and the check can reach the rights of any lifecycle that answers.
    - Actors from the object, in `actors` or `except`, go through the resolver the team already supplies (actors decision 27). The acting lifecycle's resolver also answers for those that another answering lifecycle's rights name, so one resolver serves the check.
    - **Other lifecycles' states come from a hook, written only when needed.** An event that names an object, link or own verb is answered by every lifecycle that governs the object (decision 10). Generated code can't read another lifecycle's state for the same object. So when another lifecycle could say no to such a verb, because it has a deny right naming the verb or it denies by default, the acting lifecycle's handlers gain one partial method per such lifecycle, `StatesIn<Lifecycle>(call)`, in the team's half. It returns the object's active states there; none means the initial state, as in a run (decision 27). A new one fails the build until written, as conditions do.
    - **Why a hook:** the check never skips a lifecycle that could refuse, and the build names what the team must supply. A project whose rights need no other lifecycle gets no hook. A lifecycle that allows by default and says nothing about the verb never needs one, since it can't say no.
    - **The alternative:** check only the acting lifecycle, and say so in the generated comments. Generated code would then accept what the model refuses, and nothing in the build would show it. It is not chosen.
    - **`Rights.Check` takes one request record:** the verb, the acting lifecycle (none outside a move), the envelope, and the object's states by lifecycle. A team's own call, such as from an edit endpoint, gives the states itself. Part 2 adds a member without breaking that call.
30. **Generated code checks rights where it already checks actors: events, human tasks and signatures.** Confidence: high.
    - Operation endpoints call their handler directly and carry no actor (`operation-endpoints.scriban`). The pipeline that checks actors carries process commands only (`CommandDescriptor` in `dispatch.scriban`). So generated code doesn't check an operation's `requires` or its verb. The first draft did, and it could not be built.
    - A team that wants an operation checked calls `Rights.Check` from its handler, with the operation's verb and the object's states.
    - Adopting rights changes the generated checks of events, tasks and signatures only, and only in a project that uses rights.

### Tools

31. **One new tool, `get_rights`: who can do what in a lifecycle. Each lifecycle answers on its own, and any "no" wins.** Confidence: medium-high.
    - Per state, actor and verb, it answers allowed, denied, or "depends", with the elements that decide.
    - **Within the lifecycle,** "depends" means a condition or an actor from the object decides, in `actors` or in `except`.
    - **Other lifecycles** answer only for object, link and own verbs (decision 10), and their state isn't known in a cell. When the lifecycle itself says no, the cell is denied. Otherwise, when another lifecycle that governs the object says no in every one of its states, the cell is denied, by that lifecycle's right or default. When one could say no only in some states, or under a condition, the cell depends, and `dependsOn` names those lifecycles. A lifecycle that allows by default and says nothing about the verb never appears.
    - **One row per subject type where they differ.** When the lifecycle's subjects are governed by different other lifecycles, and that changes a cell, the cell has one row per subject type, each with its `subject`. Otherwise one row covers them all.
    - The editor's "Who can do what" view, the mock, and the docs pack's table read the same computation. For a project that uses rights, they never disagree. For a project without rights, `process.access` is empty, so output doesn't change, while `get_rights` still answers from the closed lists and the default.

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

Also updated in each part: the sample app's schema copies, `openapi.yaml` by hand (the shapes, the trace, the simulate and record requests, the records, `get_rights`), and the editor files generated from it.

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

`DocumentOwner` is an actor from the object, `"relative": { "owner": true }`. The document's subject entry names no owner field, so the team's system holds the owner. The condition is written as text and stored as a tree of ids, as every expression is (MQ3108).

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
| MQ9903 | E | `ends` that aren't ends every subject holds, or beside no verb that acts on a link. For a named right, reported at the use. Part 2 adds: `attributes` that aren't attributes every subject holds, or beside a verb other than `read` and `modify` | Remove the entry |
| MQ9904 | W | An allow right in a lifecycle that allows by default: it can't change any answer (decisions 9 and 11) | Remove it; set the default to deny |
| MQ9905 | W | Under `deny`, an event or a human task no one can ever do: no actors, no gate signers on its transitions, and no allow right of its lifecycle names its verb (`complete` for a task); or an event with neither a verb nor actors nor signers | Add actors; add an allow right |
| MQ9906 | W | An `except` entry that the same right's `actors` also names, so the right never applies to it | Remove it from `except` |
| MQ9308 | E | A refused scenario step whose refusal or refusing element differs from `expect.refusal` or `expect.refusedBy` | Update the expectations from a replay (`refresh-scenario`) |

**Existing rules that gain a case.** No finding changes for a model that uses none of this: every new case needs a right, a default or a verb step. Nothing is checked on an operation's `requires` (decision 15).
- **MQ2001 and MQ2002:** an `except` entry that names nothing, or something other than an actor, as for any actor list.
- **MQ9701, MQ9704:** a verb named by a right.
- **MQ9604:** an actor from the object in a right's `actors` or `except`, at the lifecycle (at each use for a named right).
- **MQ9106:** an actor named by a right, in `actors` or `except`, counts as used.
- **MQ3104 and the typing rules:** `current_actor()`, `in_state` and `now()` read at `Right` sites. The context and the payload don't (MQ3102).
- **MQ9305:** a verb step's fields: a move, signature or task verb; in part 1, `freeze` or `thaw`; `values` beside a verb other than `modify`, or beside an `end`; an `end` that isn't the subject's, or an `object` not linked through it, except for `connect`; `delete` on a link. Also `refusal` or `refusedBy` beside `accepted: true`. Part 2 adds: `attributes` beside a verb other than `read`; `frozen` on a link of an object other than the subject.

The mock backend's rule list follows.

## 6. Resolved model and templates

```csharp
public sealed class RRight
{
    public string Id, Name, DisplayName, Description;            // as other parts and elements
    public string Access { get; internal set; } = "";            // "allow" or "deny"
    public RList<RActor> Actors { get; internal set; }           // file order; empty means everyone
    public bool Everyone { get; internal set; }                  // Actors is empty
    public RList<RActor> Except { get; internal set; }           // file order; empty when none
    public RList<RVerb> Verbs { get; internal set; }
    public RList<REnd> Ends { get; internal set; }               // empty when none
    public RExpression? Condition { get; internal set; }         // typed; JavaScript null when it uses current_actor() or in_state
    public bool Named { get; internal set; }                     // an element, not a part of a lifecycle
    public RPackage? Package { get; internal set; }              // named only
    public RList<RRightUse> Uses { get; internal set; }          // where it holds
    // Part 2 adds Attributes (RList<RAttribute>, empty when none) and WhileFrozen (bool).
}
public sealed class RRightUse { public string Id; public RRight Right; public RProcess Process; public RList<RState> States; } // States empty: every state
public sealed class RAccess                                      // one cell of "who can do what" (decision 31)
{
    public RState State; public RActor? Actor;                   // Actor null: everyone else
    public RVerb Verb; public REntity? Subject;                  // Subject null: the same for every subject type
    public string Answer;                                        // "allowed", "denied" or "depends"
    public bool ByDefault; public IReadOnlyList<RObject> By;     // the rights, lists or lifecycles that decide
    public RList<RProcess> DependsOn;                            // other lifecycles that may say no; empty otherwise
}
// RProcess gains Rights (RList<RRightUse>: inline rights as uses of themselves, then uses of named rights, in file order),
//   RightsDefault ("allow" or "deny") and Access (RList<RAccess>). ResolvedModel gains Rights: the named rights.
// RActor and RVerb gain Rights: the rights that name them directly (an actor in actors or except). RStep gains Verb, Values, End
//   and Object (part 2: Attributes). RExpectation gains Refusal and RefusedBy. Part 2: RScenarioObject and its links gain Frozen.
```

- **Templates read** `model.rights`, `process.rights`, `process.rights_default`, `process.access`, the right's members (`right.access`, `right.actors`, `right.except`, `right.verbs`, `right.ends`, `right.condition`), `use.right`, `use.states`, `actor.rights`, `verb.rights`, `step.verb`, `step.values`, `step.end`, `step.object`, `step.expect.refusal` and `step.expect.refused_by`. Part 2 adds `right.attributes`, `right.while_frozen`, `step.attributes` and `object.frozen`.
- **`access` keeps one meaning,** whether an actor may: a right states it (`allow` or `deny`), and `process.access` lists the answers.
- **`process.access`** lists, for each atomic state, each actor named by the lifecycle's rights (in `actors` or `except`), events, tasks and gates (plus "everyone else"), and each verb those name, the answer of decisions 10 to 12, with rows split by subject type as decision 31 says. A role's row follows membership. It is the same computation as `get_rights`. It is filled only in a project that uses rights (decision 28).
- **Dependency keys:** `process.rights` keys on the process file and each named right it uses. `model.rights` keys on `k:right`. `process.access` also keys on every actor file (membership), the verb vocabulary, and what decides which lifecycles govern its subjects: the process kind (`k:process`) and each such lifecycle's file, as an entity's lifecycles are keyed today. So a lifecycle added later over the same type, or one whose rights change, renders it again. These keys exist only in a project that uses rights, and only a template that reads them pays.
- **Records:** process records gain `rights` and `rightsDefault`, step records the verb step's keys, expectation records `refusal` and `refusedBy`, each only where present. Named rights get records of kind `right`. Part 2 adds `frozen` on object records. So `get_resolved_model` answers don't change for a project without them.

## 7. Simulation and replay

- **One evaluator.** `Processes/RightsEvaluator.cs` works out decisions 10 to 12 for a verb, an actor or participant, the object's states in each lifecycle, and its frozen flags (always false in part 1). Each answering lifecycle answers on its own, then any "no" wins. The interpreter, `get_rights`, `process.access` and the docs pack all use it.
- **Where the interpreter checks:** `HandleEvent` (`Processes/StatechartInterpreter.cs:184`) checks the event's list (as today), then denies and defaults, before `Select`. The gate signers that count as an allow (decision 14) come from the gates on the event's transitions from the active states. `HandleInvoke` (`:259`) does the same for a human task and `complete`. `CheckSigner` (`:823`) checks the lifecycle's denies on `sign` after the signer list. A new `HandleVerb` runs verb steps.
- **The object's row** (object effects section 7): `modify` writes its values there. A delete marks the row removed. Part 2 adds the frozen flags.
- **The trace** gains `refusedBy` and `rights` (each answering right with its access, in check order). Notes cover a verb the run didn't apply and a delete. All are left out when unused, so today's traces don't change: `refusedBy` is written in a project that uses rights, and on a step that expects it. Part 2 adds `frozen` (true while the subject is frozen), and notes for a repeated freeze or thaw and for sets on a frozen object.
- **Replay** compares `expect.refusal` and `expect.refusedBy` (MQ9308).
- **Replay keys** (`Resolution/ResolveRun.Processes.cs`) add the named rights a process uses. In a project that uses rights, a scenario whose steps check an object, link or own verb (a verb step, or an event that names one) also keys on the process kind and on every lifecycle that governs its subject, since another lifecycle may answer. A scenario in a project that uses no rights keeps today's keys.
- **Speed:** a lifecycle is evaluated only when it has rights or denies by default. Other lifecycles' states are worked out only when an object, link or own verb is checked, and only for lifecycles that could say no.

## 8. Tools

- **`get_rights` (new):** takes a lifecycle and, optionally, states, actors, verbs and a subject. Answers rows of state, actor, verb, `subject` (when answers differ by subject type), answer (`allowed`, `denied`, `depends`), whether it is the default, the ids that decide (`by`), and `dependsOn` (the other lifecycles that may say no), with the lifecycle's default. Pages with `cursor`, as `get_elements` does. Read-only. Part 2 adds an optional `frozen` input: without it, cells answer for a thawed object; with `frozen: true`, modify, connect, disconnect and delete are denied by freezing, and `whileFrozen` rights hold.
- **`simulate_process`, `record_scenario`, `verify_scenarios`:** verb steps, the trace's new members, and the expectations of decision 26. Part 2 adds objects' and links' `frozen`.
- **`preview_expression`:** previews a right's condition at its pointer, and for a named right against a lifecycle that uses it.
- **`get_references`, `referenced_by`:** a right's actors (in `actors` and `except`), own verbs, states, ends and condition paths; a use's named right; a verb step's end and object. Part 2 adds a right's and a step's attributes.
- **Deletes:**
  - A named right in use: refused, unless `remove-references` or `delete-dependents`, which both remove the uses.
  - An actor, state or end a right names in `actors`, `states` or `ends`: `remove-references` drops it from the list. **When the last one goes, the right goes too,** since an empty list would widen it to everyone, every state or the whole object. A holding's `within` goes with its object for the same reason (actors section 7). Part 2 treats `attributes` the same way.
  - An actor in `except`: `remove-references` drops it, and the right stays. An empty `except` means no exception, which is all that is left of an actor that no longer exists.
  - `rename-verb` and `remove-verb` walk rights too. A right whose last verb is removed goes.
- **`get_model_index`:** process rows gain `rights`, the count, only when above zero, and their `verbs` also list the verbs their rights name. Named rights get rows with `access` and `verbs`. So the Verbs tab counts rights among a verb's uses.
- **`export_process`, `import_process`:** rights travel in `meta.maquettiste`.
- **`validate`:** the rules of section 5.
- **The CLI** follows the same tools. **The rule list, the schemas, the conventions resource, the modeling skill and the tool texts** describe all of this.

## 9. Packs

Output changes only for a model that uses what each output needs (decision 28).

**`csharp-dapper`, part 1**
- **`Rights.cs`,** in the processes namespace, written only when a lifecycle has `rights` or `rightsDefault`: `RightAccess`, `RightDefinition` (its name, access, actors, except, verbs, ends, whether it has a condition, and its description), `RightRequest` (decision 29), `RightDecision` (allowed, the refusal and the refusing id), and `Rights.Check`, which follows decisions 10 to 12. A `Can<Verb>` method for each verb a right names calls it.
- **Process definitions:** each lifecycle's entry gains its rights with their states and its default, through an optional parameter written only when the project uses rights, as verbs did.
- **Conditions:** one partial method per right with a condition, in a `RightConditions` class whose team half is written once: `<Right>(call)` for a named right, once however many lifecycles use it, and `<Lifecycle><Right>(call)` for an inline right. Each is documented with the condition's text, and the check calls it.
- **Actors from the object:** the acting lifecycle's resolver (actors decision 27) also answers for those that another answering lifecycle's rights name.
- **Other lifecycles' states:** `StatesIn<Lifecycle>(call)`, written only as decision 29 says.
- **The interpreter:** checks rights and defaults before guards for events and human tasks, with gate signers as an allow for the gated event, and `sign` at gates. `Refusals` gains `Right` and `Default`, and results gain `RefusedBy`, only when `Rights.cs` is written.
- **Operations:** no change (decision 30).
- **Scenario tests:** a refused step asserts the refusing id when it has `expect.refusedBy`. When `Rights.cs` is written, a verb step calls `Rights.Check` with the scenario objects' states and asserts its answer. Otherwise nothing in the model can refuse it, and it is written as a comment.
- **Nothing goes in `helpers.js`.**

**`csharp-dapper`, part 2:** `RightDefinition` gains `WhileFrozen` and `Attributes`, `RightRequest` gains the frozen flag and the attributes, and `Refusals` gains `Frozen`, each only when the project uses freezing or attribute rights. Whether an instance's object or link is frozen comes from a host hook, a partial method written only when the project uses freezing.

**`process-docs`, part 1**
- **The lifecycle page,** when it has `rights` or `rightsDefault`: a Rights section with the default in a sentence ("Anything not stated here is denied."), a table of rights (name, allows or denies, who, except, does what, in which states, when, why), and a "Who can do what" table per state from `process.access`. A cell that depends on another lifecycle says so ("Depends on HR").
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
- **A toggle, Rights or Who can do what.** The second shows one state at a time (a state picker), actors as rows and verbs as columns. Each cell says Allowed, Denied or Depends, greyed when it is the default. A cell that depends on another lifecycle says which ("Depends on HR"). A subject picker shows when answers differ by subject type. Clicking a cell lists what decides it, each a link. It reads `get_rights`. Part 2 adds a **Frozen** switch.

**Named rights:** a **Rights** folder in the Processes explorer, after Actors. The right editor has the same fields without states, Except included, and a **Used by** list. Search finds them.

**Scenario editor:**
- **Steps:** the input picker gains **Do a verb**. Its row has a verb picker (object, link and own verbs first), and cells for values and the link (end and object) as the verb needs. Part 2 adds an attributes cell for `read`.
- **Expectations:** **Refusal** and **Refused by** cells for a refused step, filled by "Update from replay".
- **Objects grid (part 2):** a **Frozen** box, and one per link.

**Simulation panel:** a **Do…** menu beside the event buttons runs a verb step as the chosen participant. A refusal shows its reason and links to the element. Each step lists the rights that answered. Part 2 adds freeze and thaw to the menu, and a badge on a frozen object.

**Elsewhere:** the actor editor lists the rights that name the actor, in Who or Except. The Verbs tab counts rights among a verb's uses.

**Quick fixes:** MQ9901, MQ9902 and MQ9903 remove the entry. MQ9904 removes the right or sets the default to deny. MQ9905 opens the event, or adds an allow right. MQ9906 removes the entry from Except. MQ9308 updates the expectations from a replay.

**Generated and mock files:** the editor's API types come from `openapi.yaml` by the fixed command. The kind's entries in `model/model.ts`, `model/labels.ts` and `app/navigation.ts` are added by hand. The mock backend gains `rightRules.ts` beside `effectRules.ts`, and its `stepper.ts` (1,491 lines today) gains the rights check, verb steps and the `get_rights` computation, a large part of the editor work. `validate.ts`, `refs.ts`, `store.ts`, `cascade.ts`, `modelIndex.ts`, `processHandlers.ts` and a `get_rights` handler follow. Part 2 adds the frozen flags and attribute checks there.

## 11. A project that uses none of this

**After part 1:**
- **Files:** nothing rewrites a file, so saves, `format`, `format --check` and snapshot import behave as today. Recording a scenario writes what it writes today (decision 26).
- **Findings:** the same. Every new rule and case needs a right, a default or a verb step. Nothing new is checked on an operation's `requires` (decision 15). MQ9105 stays as it is.
- **Output:** the same bytes. Each pack change depends on what its output needs (decision 28). Generated operation code doesn't change (decision 30).
- **Simulation and replay:** the same outcomes and traces. An event's and a task's actor lists are checked as today, with the same refusal values. New trace members are left out.
- **Tool answers about the model:** the same. New record and index members are left out when unused, and `model.rights` and `process.access` are empty. `get_rights` is new. The rule list, the schemas and the tool texts describe the additions.
- **Dependency keys:** the same. The process kind and other lifecycles join `process.access` and replay keys only in a project that uses rights.
- **Capabilities:** once built, these rows of `lifecycle-capabilities.md` become Yes: who may read, change or delete per state; the long list of rights per state; rights for the owner, everyone and named actors; a rule over the object's data and the current user; a named rule reused; combining; an access rule for every state; and what may be done in this state. A named rule reused across types without a lifecycle stays No until its own item (decision 8). Rights along containment stays No.
- **Speed:** the same. Rights are evaluated only where they exist.
- **Once, as on any upgrade:**
  - Every unit renders again and writes the same bytes, since the engine version is part of each unit's key.
  - The editor refreshes the `.schema` copies, or `validate` warns MQ1008 until refreshed.
  - The index cache is read in full once.
- **A team on mixed engine versions:** an older engine refuses the new kind and keys (decision 32).

**After part 2:**
- **A project that uses part 1 but neither freezing nor attribute rights** keeps its files, output bytes, findings, keys and outcomes. The new members and hooks are written only when used.
- **A project that uses neither part** keeps everything listed above, with one exception: an event that names `freeze` now freezes the subject in a run (decision 16). Only a scenario that then sends an event naming `modify`, `connect`, `disconnect` or `delete` changes, and the release notes say so.
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
1. **Confirm three new names before part 1's release.** They came from fixing the first check's findings, and names are permanent once released:
   - the part kind **`process-right`**, for each entry of a lifecycle's `rights` (decision 3). Tools report it, and qualified names use it. The bare word `right` is the named right's kind.
   - **`subject`** and **`dependsOn`** in `get_rights` rows (decision 31): the subject type a row is for, and the other lifecycles a "depends" cell waits on.

   Recommended: as listed. Confidence: medium-high. The build proceeds with them, and the release waits for the owner's word.

## 14. Tests

**Part 1**
- **`RightRuleTests` (new):** failing and passing cases for MQ9901 to MQ9906 and MQ9308, and the new cases of MQ2001, MQ2002, MQ9701, MQ9704, MQ9604, MQ9106, MQ3104 and MQ9305. Today's models give the same findings, including one whose operations list `requires`.
- **`RightsEvaluatorTests` (new):**
  - One lifecycle: deny, then allow, then the default.
  - Across lifecycles: any "no" wins. HR denies by default and Projects allows project managers to `modify`: the manager is refused by HR's default. A lifecycle that allows by default and is silent answers yes. Which lifecycles answer for each verb group.
  - `except`: a listed actor, a member of a listed role, the object's owner with and without an owner field; the default answers for an excepted actor, under allow and under deny; `except` never grants.
  - Empty actors as everyone; membership at any depth, deny included; actors from the object; a participant who satisfies an actor other than the one it acts as.
  - States, compound states and conditions, unknown included; `create` in the initial state; `ends`.
  - The closed lists as allows that can't be widened; gate signers as an allow for the gated event under a deny default.
  - The fixed order and the refusing id for each row of decision 12, an explicit deny before a default.
- **`RightsReplayTests` (new):**
  - Verb steps: `modify` values seen by a later guard; `modify` on a link checked and recorded only; delete then later steps; verbs recorded but not applied, with notes; a `freeze` or `thaw` step is MQ9305.
  - Refusals at each check, `expect.refusal` and `expect.refusedBy`, MQ9308; recording with and without rights.
  - Other lifecycles' states from `states`, a bound value and the initial state.
  - Replay keys: adding a lifecycle over the same subject replays a scenario with an object verb; a scenario in a project without rights keeps today's keys.
  - Unchanged traces and replays for a model without rights.
- **`NamedRightTests` (new):** loading, references, `get_references`, localized texts; checks at each use; deletes under each choice, including the last actor, state or end, and an actor in `except`; `rename-verb` and `remove-verb` across rights; index rows, by patch and by full build; the `process-right` part kind in the name index.
- **`ResolvedRightsTests` (new):** `process.rights`; `process.access` with membership, `except`, "depends" for a condition, an actor from the object and another lifecycle (with `dependsOn`), and one row per subject type where answers differ; `model.rights`, `actor.rights`, `verb.rights`, records; dependency keys, with the process kind only in a project that uses rights.
- **`GetRightsTests` (new):** the tool's rows, filters and pages agree with `process.access`; a project without rights gets answers from the closed lists and the default while `process.access` is empty.
- **No change:** a model without rights gives the same output, files, `format --check`, findings, replay, traces, keys and tool answers. The pack golden tests pass untouched.
- **Pack tests:** a new case per pack. `csharp-dapper`'s `Rights.cs`, condition partials (a named right used by two lifecycles has one), the `StatesIn` hook (written only when another lifecycle could say no), interpreter checks and scenario tests build with warnings as errors and run. A model whose scenarios only expect `guard` writes no `Rights.cs`. `process-docs`' Rights section, table, "Depends on" cells, rights page and refusal lines.
- **Editor:** unit tests for the grid's columns, the cell answers and the verb step's cells per verb. Browser tests (in the browser pass) for adding rights with Except, using a named right, setting the default, reading Who can do what with a "Depends on" cell, a verb step refused by a right, and updating expectations from a replay.
- **Acceptance,** on an invented model:
  - A document lifecycle that denies by default, with the rights of section 4, and the scenario there, with no owner field.
  - An employee governed by an HR lifecycle that denies by default and lets HR staff `modify`, and by a project lifecycle that lets project managers `modify`. A project manager is refused by HR's default.
  - A gated approval under a deny default: signers sign without an allow on `promote`, and a right denies `sign` to the submitter (`subject.submittedBy = current_actor()`), refused at the signatures step.

**Part 2**
- **Freezing:** freeze and thaw by a verb step and by an event; repeated; frozen links; "frozen" refused before any right; `whileFrozen`; a move's sets on a frozen object apply, with a note; an event that names `freeze`, then one that names `modify`, is refused.
- **Attributes:** a modify step checked per attribute against attribute rights only; `read` with `attributes`; MQ9903's attribute case; deletes of a named attribute; "depends" cells for attribute rights.
- **`get_rights`** with `frozen: true`.
- **Packs:** the frozen hook and the new members written only when used; `Refusals.Frozen`.
- **No change:** a project that uses part 1 only keeps its output bytes, findings and outcomes.
- **Acceptance:** the part 2 steps of section 4; an HR lifecycle where `salary` can't be read or modified except by HR staff.

Each stale-output test is checked to fail without its fix.

## 15. Tasks and the build plan

### Tasks

The sizes are days for a person who knows the code, with tests and a review. They show how the groups compare.

**Part 1**

| Wave | Group | What | Size |
| --- | --- | --- | --- |
| 1 | Contract and model | The schemas, `openapi.yaml`, rule texts; the trace and refusal members and the step expectation in the engine; the `right` kind and the `process-right` part; `rights`, `rightsDefault` and `except`; the `Right` site kind; MQ9901 to MQ9906 and the changed rules | Two days |
| 1 | References, deletes and the evaluator | References, deletes and their widening guard, `except`, `rename-verb` and `remove-verb`, index rows, localization; `RightsEvaluator` | A day and a half |
| 1 | Editor, part one | The Rights tab and grid, named rights in the explorer and their editor, quick fixes, the mock's validation and references | A day and a half |
| 2 | Simulation and scenarios | The interpreter's checks, verb steps, the trace, MQ9308, recording, replay keys | A day and a quarter |
| 2 | Resolved model, tool and docs output | `RRight`, `process.access` with `dependsOn` and subject rows, records, `get_rights`, tool texts; `process-docs` | A day and a quarter |
| 2 | `csharp-dapper` | `Rights.cs` and the request record, condition partials, the `StatesIn` hook, the interpreter's checks, scenario tests | A day |
| 2 | Editor, part two | Who can do what, scenario verb steps and expectations, the simulation panel, the mock's stepper and `get_rights` | A day and a half |
| 3 | Docs | User guide (processes, scenarios, rights, expressions), releases, modeling skill, capabilities rows, `next.md` | Half a day |

That comes to about ten and a half days for a person.

**Part 2**

| Wave | Group | What | Size |
| --- | --- | --- | --- |
| 1 | Contract and engine | The schemas, `openapi.yaml`, rule texts; `whileFrozen`, `attributes`, the frozen keys, the `frozen` refusal and trace member; MQ9903 and MQ9305 cases; references and deletes for attributes; the evaluator's frozen row and attribute checks | A day and a half |
| 1 | Editor | The new columns, Frozen boxes, freeze and thaw in Do…, the attributes cell, the Frozen switch, the mock | A day |
| 2 | Simulation | Freeze and thaw take effect, from steps and events; frozen flags; per-attribute modify; sets on a frozen object; replay keys | A day |
| 2 | Output | `RRight`'s new members, records, `get_rights`' `frozen`; both packs and the frozen hook | A day |
| 3 | Docs | User guide, releases, modeling skill, capabilities rows, `next.md` | Half a day |

That comes to about five days for a person.

**Checklist for every group.** Anything that reads or writes a lifecycle, an actor list, a verb, a scenario step or a refusal must be found and handled:
- **A new element kind and part kind:** `Model/ElementKind.cs`, `Model/KindInfo.cs` (folder `model/rights`), `Model/ElementBase.cs`, `get_model_kinds`, the localizable node kinds in `Localization/LocaleChains.cs`; for `process-right`, `Model/ModelIndexer.cs` (the part kind map and `SubElementKinds`), `Model/PropertySchemas.cs` (`TranslatedHolderKinds`), `Loading/NameIndex.cs` (a case in `Holders`) and the list-to-kind map in `Loading/ChangePlanner.Cascade.cs`.
- **Expressions:** the `Right` site in `Expressions/ExprType.cs` and every site switch the `Set` kind needed (object effects decision 3), and `Validation/ExpressionRules.cs` (`ExprSites.Of`).
- **Processes:** `Model/Processes.cs`, `Processes/StatechartInterpreter.cs`, `Processes/ScenarioReplayer.cs`, `Processes/StepTrace.cs` (`Refusals`), `Processes/ScenarioObjects.cs`, `Processes/Participants.cs`, `Processes/ProcessApi.cs`, and the exporter and importer.
- **Resolver and records:** `Resolution/ResolvedProcesses.cs`, `Resolution/ResolveRun.Processes.cs`, `Editor/ResolvedRecords.cs`.
- **Contracts:** the schema copies, `openapi.yaml` and its generated files. `validation-rules.json` is written only by the fixed command.
- **Test harness:** `RenderKit.WithSettings` and `ResolvedCoverageTests`.

### How it gets built

**What earlier items cost.** 4.1 took about 3.75 hours for about 11,000 lines: about 155 output tokens, 425 new input and 40,000 cached re-reads per line. Verbs, about 5,000 lines, took about 2.5 hours. Object effects, about 6,000 lines written by hand (10,500 in the diff with pack expected files), took about 3.5 hours, slower per line because of the typer and the simulator. Engine builders cost about 20M cached per 100 steps. In every item, the review and the browser pass each found real bugs.

**This item's size.** The first check put the whole item at about 10,000 to 11,000 lines with tests, without pack expected files. The first draft's 9,000 left the editor's mock simulator (`stepper.ts`) and the rights check light. `except` adds about 300 lines to part 1. Rights on types with no lifecycle, about 500 lines, leave part 2.
- **Part 1, about 6,800 lines:** contract, rules and references about 1,200 and their tests about 700; the evaluator about 500 and its tests about 400; simulation and scenarios about 500 and their tests about 500; resolved model, tool and packs about 900 and tests about 300; editor and mock about 1,500; docs about 300.
- **Part 2, about 3,000 lines:** contract, rules and references about 450 and tests about 350; freezing and attributes in the evaluator and the simulator about 450 and tests about 450; resolved model and packs about 400 and tests about 150; editor and mock about 600; docs about 150.

**Agents.** Each is capped at about 100 steps or 200k context. Opus 5.5 builds the engine and pack output and runs every review. Sonnet 5.5 builds the editor, the mock and the docs.

**Part 1 agents**

| Wave | Agent | Model | Steps | Starts from |
| --- | --- | --- | --- | --- |
| 1 | Contract and model; **contract first**: the schemas, all `openapi.yaml` changes including the trace, the refusal values, records and `get_rights` rows; the trace and refusal members in `Processes/StepTrace.cs` and the step expectation, so later agents share them; rule texts. Keeps every change to `Validation/ExpressionRules.cs` | Opus | About 100 | `origin/main` (0.14.0) |
| 1 | References, deletes and `RightsEvaluator`. Owns `ProcessTools.cs` | Opus | About 100 | The contract commit |
| 1 | Editor and mock, part one | Sonnet | About 100 | The contract commit |
| 2 | Simulation and scenarios | Opus | About 90 | Wave 1's engine agents |
| 2 | Resolved model, records, `process.access`, `get_rights`, tool texts, replay keys, and `process-docs` | Opus | About 90 | Wave 1's engine agents, beside the simulation agent |
| 2 | `csharp-dapper` output | Opus | About 80 | The resolved model agent's first commit (`RRight` and `process.rights` filled), in the first free slot |
| 2 | Editor and mock, part two | Sonnet | About 100 | Wave 1's editor agent |
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
| 2 | Resolved model, records, `get_rights`' `frozen`, both packs and the frozen hook | Opus | About 70 | The same, beside the simulation agent |
| 3 | Review of the pull request | Opus | About 50 | All builders' finish |
| 3 | Docs | Sonnet | About 30 | The builders' finish, beside the review |
| 3 | Browser pass | Sonnet | About 30 | The builders' finish, beside the review |
| 4 | Fixers, split by area | Opus or Sonnet | About 30 each | The findings |

- **At most three agents run at once.** In part 1, the evaluator comes in wave 1, so the wave 2 engine agents use it without waiting on each other. The simulation agent works in `Processes/` and the process API. The resolved model agent works in `Resolution/`, `Editor/ResolvedRecords.cs`, the tool and `process-docs`. The `csharp-dapper` agent works only in its pack. Wave 2 has four agents, so the `csharp-dapper` agent takes the first free slot.
- **One pull request per part, into `main`,** with the contract, engine, editor, output, docs and fix commits. Each is its own release. The owner merges part 1 first.
- **Generated files** (the editor's API types, the recorded rule list, the sample app's copies) are written only by the fixed commands, after each merge into the branch. Pack expected files stay with the builder, so their changes are reviewed.
- **Fixers** are fresh agents with the exact findings. **A design gap found while building** stops the work, and the owner is asked.

**Estimate.**

**Part 1**

| | Wall time | Output | New input | Cached re-reads |
| --- | --- | --- | --- | --- |
| Wave 1 (contract, model, references, evaluator, editor part one) | About 1.3 hours | 0.3M | 0.8M | 65M |
| Wave 2 (simulation, resolved model and docs output, `csharp-dapper`, editor part two) | About 1.3 hours | 0.3M | 0.85M | 75M |
| Wave 3 (review, docs, browser pass) | About 0.6 hours | 0.15M | 0.4M | 35M |
| Wave 4 (fixes, second browser pass, CI) | About 0.6 hours | 0.1M | 0.25M | 40M |
| In all | **About 3.5 to 4 hours** | **About 0.85M** | **About 2.3M** | **About 200 to 225M** |

**Part 2**

| | Wall time | Output | New input | Cached re-reads |
| --- | --- | --- | --- | --- |
| Wave 1 (contract and engine, editor) | About 0.7 hours | 0.15M | 0.4M | 35M |
| Wave 2 (simulation, output) | About 0.6 hours | 0.15M | 0.4M | 35M |
| Wave 3 (review, docs, browser pass) | About 0.4 hours | 0.1M | 0.25M | 25M |
| Wave 4 (fixes, CI) | About 0.4 hours | 0.05M | 0.15M | 30M |
| In all | **About 2 to 2.5 hours** | **About 0.45M** | **About 1.2M** | **About 120 to 135M** |

**Both parts:** about 5.5 to 6.5 hours, about 1.3M output, about 3.5M new input and about 320 to 360M cached re-reads. The first check estimated the whole item, built in one go, at 5 to 6.5 hours, 1.3M output, 3.5M new and 280 to 320M cached. Splitting adds about 0.5 hours and 40M cached in all: a second pull request, review, browser pass, CI run and release. `except` adds about 300 lines and rights on types with no lifecycle take out about 500, so the size stays near the check's. The output and new-input figures follow 4.1's cost per line, a little under, as object effects did. The cached figures follow the check's reading, which sits between a count by steps (about 20M per 100 Opus steps and 15M per 100 Sonnet steps, plus coordination) and 4.1's cost per line.
