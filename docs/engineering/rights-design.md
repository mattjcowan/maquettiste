# Rights: design

**Status (2026-11-06): a design for the owner, not built.** It designs the fifth item in the owner's order after 4.1: rights, with named rights, defaults, combining, freezing and refusal reasons in scenarios. [Who, what and when](lifecycle-actors-scenarios.md) section 7 sets its direction. [What lifecycle systems can do](lifecycle-capabilities.md) is the yardstick. [The actors design](actors-design.md) (decisions 22 and 23 were recorded for this item), [the verbs design](verbs-design.md) and [the object effects design](object-effects-design.md) give its parts. A file path without a project prefix is under `src/Maquettiste.Engine/`. The examples are invented. Each new name and format is held to the roadmap's test: it stays for good, it must help any team whether they use it or not, and it stays optional.

## 1. In short

Today "who may act" is only a closed list: an event's actors, a gate's signers, a human task's actors. Nothing says what may be done to an object in a state. Nothing can deny. Nothing can be frozen. A scenario can expect a refusal, but not why.

| Layer | What changes |
| --- | --- |
| The model | A lifecycle lists **rights**. A right **allows** or **denies** some **actors** some **verbs**, in some **states**, under an optional **condition**. Its description says why. A **named right** is its own element, which many lifecycles use: what lifecycle systems call a rule. Each lifecycle has a **default** for what no right mentions: allowed, unless it says denied. **Freeze** and **thaw** take effect: while an object or a link is frozen, it can't be modified, connected, disconnected or deleted. An event's actors, a task's actors, a gate's signers and an operation's required actors keep their meaning, and count as rights |
| Simulation and scenarios | Checks run in a fixed order: frozen, rights, guards, signatures. A new **verb step** lets a participant modify, freeze, delete or do any other verb to the object. A refused step names the element that refused it, and a scenario can expect that element |
| Code generation | Templates read each lifecycle's rights, its default and a worked-out "who can do what". `csharp-dapper` writes a rights table and one check, and leaves conditions to the team. `process-docs` adds a Rights section, a "who can do what" table and a page of named rights. Each happens only when the model uses rights |
| The team's system, or a lifecycle system | Nothing changes until the team uses the new output or builds from the model |

**A project that uses none of this** keeps its files, output, findings and tool answers. Section 11 lists everything.

## 2. Scope

| | What | Section |
| --- | --- | --- |
| **This item** | Rights on lifecycles; named rights; defaults and combining; the existing lists as rights; freezing; rights on links and on attributes; verb steps and refusal reasons in scenarios; `get_rights`; the resolved model, packs and the editor | 3 to 10 |
| **For the owner** | Named rights that apply to types directly, with no lifecycle | Decision 8, question 1 |
| **Left to trigger points** | Code that runs on a verb, as check, override or action, including on freeze and thaw; checks on automatic moves; override and set-state as scenario steps | 12 |
| **Left to richer sign-off** | Reject, ignore, withdraw, delegate and route on a gate, each with its rights; "no one holds both of these roles on one object" | 12 |
| **Left to scenarios across objects** | Verb steps on other objects; connect and disconnect changing the run's links | 12 |
| **Not in this item** | Rights passed along containment (decision 22). Events a state ignores or defers, which is a chart feature, not a right. Freezing the model's own elements (decision 18) | 12 |

## 3. Decisions

Each decision gives its confidence: **high**, **medium** or **low**.

### Where a right lives, and its shape

1. **Rights live on the lifecycle, in one list, `rights`. Each right names the states where it holds; none means every state.** Confidence: high.
   - **Why:** one right often spans several states ("no edits in Released or Obsolete"). Stored per state, it would be repeated in each. Lifecycle systems show access per state, and the editor shows that grid too (section 10). The grid is a view, the list is the file.
   - **The alternative:** `rights` on each state. It repeats a right in every state it spans, and a named right would need a use in each state. It is not chosen.
2. **Only lifecycles hold rights.** Confidence: medium-high. A right speaks of an object in its states. An orchestration is a flow, not an object's state, and a library is shared parts. Both keep their events' and tasks' actor lists. `rights` or `rightsDefault` elsewhere is MQ9901.
3. **A right has a `name`, an `effect`, `actors`, `verbs`, `states` and an optional `condition`.** Confidence: high for the shape, medium-high for the names (question 2).
   - `name` is required and is an identifier, as a guard's is. It lets a refusal name the right. `displayName` is optional.
   - `effect` is `allow` or `deny`, and required. One right is one row of the grid, and a reader sees "deny" first.
   - **The description says why.** It is shown with every refusal, in the grid and in the docs. No second text key: `description` is already on every element and part, and is localizable. No rule asks for one; the editor prompts for it.
   - Three optional keys narrow a right: `ends` (decision 20), `attributes` (decision 21) and `whileFrozen` (decision 18).
4. **Who: `actors`, any actor, as an event's list takes them. An empty list means everyone.** Confidence: high for any actor; medium-high for "empty means everyone".
   - People, roles, groups, organizations, external systems, the system actor, and actors from the object: the owner, the person a field names, the members of a role within a context. So "the owner may", "everyone may", and "reviewers on this project may" all fit, as lifecycle systems set rights for the owner, for everyone and for named people or roles.
   - A right to a role reaches its members at any depth, deny included (actors decisions 5 and 22).
   - **Why empty means everyone:** an event with no actors already accepts anyone. The editor shows "Everyone". A delete never empties the list by accident: when the last actor named goes, the right goes too (section 8).
   - **The alternative:** an explicit `everyone: true`. It is a second way to say who. It is not chosen.
5. **What: `verbs`, at least one, built-in or the project's own.** Confidence: medium-high. MQ9701 and MQ9704 apply here as at any use. The verb's group says what the right speaks of:
   - **Moves, signatures and tasks:** this lifecycle's moves, the signatures they need, and its human tasks.
   - **Objects, links, and the project's own verbs:** the object, whichever lifecycle is acting (decision 10).
6. **When: `states` and `condition`.** Confidence: high.
   - `states` names states of this lifecycle. A compound state counts when any state inside it is active, as `in_state` does.
   - `condition` is an expression at a new `Right` site. It reads the object under `subject` (to-one paths too), `actor()`, `in_state` and `now()`. It doesn't read the context or the payload: a right is also checked outside a run, such as an edit through an API, which has neither.
   - **A right holds only when its condition is true.** False or unknown means it says nothing, so a deny over a missing value doesn't fire.
   - **`create`** is checked as if the object were in the lifecycle's initial state, since that is where a new object starts. Confidence: medium-high.

### Named rights

7. **A named right is its own element, kind `right`, in `model/rights/`. A lifecycle uses it with an entry `{ id, right, states }`.** Confidence: medium-high.
   - **Why an element:** it is reused across lifecycles, has its own page, references, package and translations. An actor from the object is reused the same way (actors decision 8).
   - **Why not in a library process:** a library's members read the using process by name, but conditions name attributes by id, which MQ9020 already refuses in libraries.
   - **The use says only where:** its `states`. Who, what, when and the effect belong to the named right. A variant is another right.
   - **Checked at each use:** its condition's paths, `ends`, `attributes` and actors from the object must resolve on every subject of each lifecycle that uses it, as a library's gate is checked at each process (actors decision 11). A failure is reported at the use.
   - **It covers rules.** "Forbid modify, delete and change owner when the item is secret, unless the actor is its owner" is one named right (section 4).
8. **A named right may name `subjects`, entities or stereotypes, and then applies to every object of those types, with or without a lifecycle.** Owner question 1. Recommended: yes. Confidence: medium.
   - **Why:** the owner's rule example doesn't depend on any lifecycle. Without this, a type with no lifecycle can have no rights, and a team states one rule once per lifecycle.
   - **How it fits:** it answers for object, link and own verbs only, since moves belong to a lifecycle (MQ9903). It takes `subjects` in the same form as a lifecycle, stereotypes included. Such a right isn't also used by a lifecycle (MQ9901). It joins the combining of decision 11, after every lifecycle.
   - **Cost:** about 500 lines, about 20 minutes. The build does it last, so the owner can leave it out.

### Defaults and combining

9. **Each lifecycle has a default, `rightsDefault`: `allow` (when absent) or `deny`.** Confidence: high (actors decision 23); medium-high for one value rather than one per verb.
   - "Denied" by default would change every existing lifecycle's simulation.
   - A lifecycle system that denies by default is stated with `deny` and allow rights ("Everyone may read").
   - MQ9904 warns about an allow right that can't change any answer, as the direction asks. A team writing requirements may keep such rights for the record, and can lower the warning in Settings › Validation.
10. **Which lifecycles answer depends on the verb's group.** Confidence: medium-high.
    - **Moves, signatures and tasks:** only the lifecycle where the move, signature or task is. Promoting in HR is not promoting in Projects.
    - **Objects, links and own verbs:** every lifecycle that governs the object, each in the object's state there. In a run, these states come from the scenario (decision 27). Then named rights with `subjects`, if question 1 is yes.
    - A team that wants HR to stop a move in Projects writes a right in the Projects lifecycle with `in_state(subject, HR.Suspended)`.
11. **Combining: any deny wins, then any allow, then the defaults. If any answering lifecycle denies by default, the answer is denied.** Confidence: high for the first two (actors decision 22, the attribute-based access control standard's rule); medium-high for the defaults.
    - **Why deny wins among defaults too:** a strict lifecycle stays strict when a looser one also governs the object. A project lifecycle silent about changing a salary leaves a strict HR lifecycle to deny it.
    - A satisfied closed list (decisions 13 to 15) counts as an allow.
12. **The checks run in a fixed order, and the first that fails names the refusal.** Confidence: high (the direction, section 9). So two scenarios can't disagree about the reason.

    | Order | Check | Refusal | Refused by |
    | --- | --- | --- | --- |
    | 1 | Frozen (decision 17) | `frozen` | Nothing: freezing is built in |
    | 2 | Closed lists: an event's or a human task's actors, an operation's required actors | `actor` (today's) | The event, the invoke or the operation |
    | 3 | Denies: the acting lifecycle's rights in file order, then other answering lifecycles by name, then named rights with `subjects` by name | `right` | The right: inline, or the named right |
    | 4 | The default, when nothing allowed: the acting lifecycle's if it denies, else the first other by name | `default` | The lifecycle |
    | 5 | Guards, as today | `guard` | The guard |
    | 6 | Signatures: the gate's signers, then denies on `sign`, then a repeat signer, then a missing reason | `gate-signer`, `right`, `gate-repeat`, `gate-reason` | The gate or gate policy, or the right |

    Rights are checked on the event's verb before any guard picks a transition, as verbs decision 12 planned. `sign` is checked at the signatures step, since only then is it known that the occurrence is a signature (verbs decision 15).

### Who may is said once

13. **An event's `actors` stays a closed list, and counts as a right.** Confidence: high.
    - The listed actors get an allow for that event. Everyone else is refused by the event, as today (`actor`).
    - Explicit rights narrow it, since a deny wins. They can't widen it: an allow on `promote` doesn't let a Manager send an event that lists only Clerks.
    - **An event with no verb** is outside rights that name verbs. Its actors and the defaults still apply. Under `deny`, an event with no verb and no actors can never be sent (MQ9905 warns).
    - Outcomes for today's models don't change.
14. **A human task's `actors` is the same, for `complete`. A gate's `signers` is the same, for `sign`.** Confidence: high.
    - Rights on `complete` and `sign` narrow them.
    - MQ9105 stays. A signature is still sent as the gated event, so the event's list is checked first.
15. **An operation's `requires`: its actor entries become the operation's closed list, with no state.** Confidence: medium-high.
    - "Only these actors may run it." Other entries, such as a permission code or a custom element, keep no meaning in the engine.
    - The operation's verb is answered by the lifecycles of its subject, as any verb is.
    - The simulator doesn't run operations. This reaches `get_rights`, the docs and generated code, and only when the project uses rights (decision 28).

### Freezing

16. **Freeze and thaw take effect. An object or a link is frozen or not.** Confidence: high.
    - It starts thawed. A scenario may start an object or a link frozen (`frozen: true`).
    - An accepted `freeze` freezes it, and an accepted `thaw` thaws it, whether a verb step or an event that names the verb does it. So rights and, later, triggers apply to freezing with nothing extra (the direction, section 7.5).
    - Freezing what is frozen, or thawing what isn't, changes nothing and adds a note.
17. **While frozen, modify, connect, disconnect and delete are refused before any right.** Confidence: high (verbs decision 16, the owner's own words).
    - It is an implicit deny that no allow can lift.
    - **A frozen object** can't be modified or deleted, and its links can't be added or removed.
    - **A frozen link** can't be disconnected or have its attributes modified. The objects at its ends are not frozen by it.
18. **Freezing stops nothing else. A right with `whileFrozen: true` holds only while the object (or, with `ends`, the link) is frozen.** Confidence: medium-high.
    - **Not stopped:** moves (a frozen released item may still become obsolete), `revise` (revising is how a frozen item changes), `change-owner`, `change-type`, `change-lifecycle`, `lock`, `unlock`, `copy`, `read`, and the project's own verbs.
    - **Why the small set:** a built-in meaning is permanent (verbs decision 2), and a deny can't be lifted by an allow. A larger fixed set could never be narrowed for a team that needs less. With `whileFrozen`, a team adds "no change of owner while frozen", or stops its own verb, in one right.
    - **The alternative:** an optional key on an own verb, saying freezing stops it. It covers only own verbs, and adds a second way to do one thing. It is not chosen.
    - **Freezing the model's own elements** (rules, processes) would lock editing the model, which is another feature. A pack that writes a lifecycle system's setup can freeze that system's objects itself.
19. **Where "frozen" is held is the team's system's business.** Confidence: medium-high. As with an owner without an owner field (actors decision 10), nothing in the model holds it. Generated code asks the host. A subject's frozen field can come later, if teams ask, without changing a file.

### Rights on links and attributes

20. **`ends` narrows a right to links through those ends of the subject.** Confidence: medium-high.
    - With `ends`, `connect`, `disconnect`, `read`, `modify`, `freeze` and `thaw` speak of those links. "Deny connect through `components` in Released."
    - Without `ends`, link verbs speak of every link, and `read`, `modify`, `freeze` and `thaw` speak of the object.
    - Lifecycle systems also set connect and disconnect from each end. An end is one side of a relation, so this needs nothing more.
21. **`attributes` narrows a right to reading or modifying those attributes.** Confidence: medium-high.
    - "Deny read of `salary` to everyone outside HR." Only with `read` and `modify` (MQ9903).
    - A modify step that changes several attributes is checked per attribute. One denied refuses the step.
    - The direction placed this "later in the same item". It is the build's last engine step, so it can be cut without touching the rest.
22. **Rights passed along containment are not in this item.** Confidence: medium-high. An actor from the object with `within` already says "the project's reviewers may change its items". Inheriting a container's rights, in the container's own state, needs a containment rule on relations and the container's state in every run. It can come later, if teams ask, as an optional key.

### Simulation and scenarios

23. **Rights check inputs an actor makes:** events of the process the scenario runs, human-task completions, signatures, and verb steps. Confidence: medium-high.
    - **Not checked:** automatic moves, timers and finished invokes that are not human tasks. The chart decides those, and a lifecycle that denies `promote` to everyone in a state shouldn't stop its own timer. Trigger points' check kind may stop them later.
    - **Not checked:** events of an invoked sub-process, which sees no object. Their own actor lists apply, as today.
24. **In a run, a right applies to a participant who satisfies any of its actors, not only the actor the step acts as.** Confidence: medium-high.
    - A deny on `Owner` applies to Bea even when she acts as `Author`. This follows actors decision 17 for what "satisfies" means.
    - A step with only an `actor` matches that actor and its memberships, and an actor from the object by exact match, as today.
25. **A new step input, `verb`: a participant does a verb to the scenario's subject, or to one of its links.** Confidence: high that it is needed (verbs decision 18); medium-high for its shape.
    - **Keys:** `verb`; `values` (attribute id to value) for `modify`; `attributes` for `read`; `end` and `object` for a link. Without `end`, the step acts on the subject.
    - **Which verbs:** object verbs, link verbs and the project's own. Moves, signatures and task completion keep their own steps (an event, `invoke-done`). `override`, `set-state`, `reject`, `ignore`, `withdraw`, `delegate`, `route` and `claim` get steps with their own items (MQ9305 until then).
    - **What the run does when accepted:**
      - `modify` applies its `values` to the object's row, so later guards and sets see them. They show in `valuesChanged` and `expect.values`, as sets do (object effects decision 4).
      - `freeze` and `thaw` change the flag.
      - `delete` removes the object. Later steps are refused (`no-transition`), with a note.
      - The rest (`read`, `create`, `copy`, `revise`, `lock`, `unlock`, `change-owner`, `change-type`, `change-lifecycle`, `connect`, `disconnect`, own verbs) are checked and recorded only. A note says the run didn't apply them. Scenarios across objects will apply `connect` and `disconnect`.
26. **A refused step names the element that refused it, and a scenario can expect it.** Confidence: high for the shape; medium-high for when recording writes it.
    - The trace gains `refusedBy`, the id from decision 12, and `rights`, the rights that answered, each with its effect. Three refusal values are new: `right`, `default` and `frozen`.
    - A step may expect `refusal` (the value) and `refusedBy` (the id). When either differs, it is MQ9308. Both need `accepted: false` (MQ9305).
    - `record_scenario` writes both for a refused step when the project uses rights. Otherwise it writes what it writes today.
27. **Other lifecycles' states come from the scenario,** as `in_state` reads them (object effects decision 10): the object's `states`, else its bound value, else the initial state with a note. Confidence: high.

### Code generation

28. **Packs write rights only when the model uses them.** Confidence: high.
    - A project uses rights when it has a named right; a lifecycle with `rights` or `rightsDefault`; or a scenario with a verb step, a frozen object or link, or an expected `refusal` or `refusedBy`.
    - Otherwise every template sees empty lists, and the new trace and record members are left out.
29. **`csharp-dapper` writes one rights table and one check; conditions go to the team.** Confidence: medium-high.
    - Generated code can't read the object until the generated runtime (4.6), as 4.1 decided for guards and object effects for sets. So each right's condition is a partial method the team writes, and a new one fails the build until written.
    - Actors from the object go through the resolver the host already supplies (actors decision 27). Whether something is frozen is asked of the host.
30. **Once the model uses rights, generated checks cover the closed lists too.** Confidence: medium-high. Events' and tasks' actors are checked today. An operation's required actors start being checked in the generated pipeline then, so adopting rights is one visible change in the generated code, not a silent one elsewhere.

### Tools

31. **One new tool, `get_rights`: who can do what in a lifecycle.** Confidence: medium-high.
    - Per state, actor and verb, it answers allowed, denied, or "depends" (a condition or an actor from the object decides), with the elements that decide.
    - The editor's "Who can do what" view, the mock, and the docs pack's table read the same computation, so they never disagree.

### Release

32. **A minor release, and a team upgrades together.** Confidence: high, by the owner's 4.1 decision 6. An older engine refuses a `right` file and a file with the new keys. `formatVersion` stays 1.

## 4. Schema and files

| File | Key | Type | Notes |
| --- | --- | --- | --- |
| `common.json` | `$defs/rightBody` (new) | `effect`, `actors`, `verbs`, `ends`, `attributes`, `whileFrozen`, `condition` | Shared by both forms. `effect` is `allow` or `deny`. `verbs` is a non-empty unique list of `verbRef`. `condition` is `$defs/expr` |
| `process.json` | `rightsDefault` (after `boundAttribute`) | `allow` or `deny` | Decision 9; lifecycles only (MQ9901) |
| `process.json` | `rights` (after `gates`) | list; each entry is an inline right `{ id, name, displayName?, description?, …rightBody, states? }` or a use `{ id, right, states? }` | Decisions 1, 3 and 7; exactly one form per entry (MQ9901) |
| `right.json` (new kind) | `kind`, `id`, `name`, `displayName`, `package`, `description`, `subjects`, and `rightBody` | `subjects` as a lifecycle's entries, without `boundAttribute` or `owner` | Decisions 7 and 8. Folder `model/rights/`. `subjects` only if question 1 is yes |
| `scenario.json` | step `input` gains `verb`; step `verb`, `values`, `attributes`, `end`, `object` | `verbRef`; attribute id to value; id list; end id; scenario object id | Decision 25 |
| `scenario.json` | step `expect.refusal`, `expect.refusedBy` | a refusal value; an id | Decision 26 |
| `scenario.json` | object `frozen`, link `frozen` | boolean, default false | Decision 16 |

Also updated: the sample app's schema copies, `openapi.yaml` by hand (the shapes, the trace, the simulate and record requests, the records, `get_rights`), and the editor files generated from it.

```json
"rightsDefault": "deny",
"rights": [
  { "id": "…", "name": "EveryoneReads", "effect": "allow", "verbs": ["read"] },
  { "id": "…", "name": "AuthorsEditDrafts", "effect": "allow", "actors": ["<id of Author>"],
    "verbs": ["modify", "connect", "disconnect"], "states": ["<id of Draft>"] },
  { "id": "…", "name": "ReleasedIsReadOnly", "effect": "deny", "verbs": ["modify", "delete"],
    "states": ["<id of Released>", "<id of Obsolete>"], "description": "A released document changes only by revising it." },
  { "id": "…", "name": "NoBomChangesInReview", "effect": "deny", "verbs": ["connect", "disconnect"],
    "ends": ["<id of end components>"], "states": ["<id of Review>"] },
  { "id": "…", "name": "AdminsFreeze", "effect": "allow", "actors": ["<id of Admin>"], "verbs": ["freeze", "thaw"] },
  { "id": "…", "name": "OwnerStaysWhileFrozen", "effect": "deny", "verbs": ["change-owner"], "whileFrozen": true },
  { "id": "…", "right": "<id of SecretOnlyOwner>" }
]
```

```json
{
  "kind": "right", "id": "…", "name": "SecretOnlyOwner", "effect": "deny",
  "verbs": ["modify", "delete", "change-owner"],
  "condition": { "text": "subject.classification = 'secret' and subject.ownerLogin <> actor()" },
  "description": "Only its owner may change a secret item."
}
```

The condition is written as text and stored as a tree of ids, as every expression is (MQ3108).

```json
"objects": [ { "id": "<o1>", "name": "spec", "entity": "<id of Document>",
  "values": { "<id of classification>": "secret", "<id of ownerLogin>": "bea" } } ],
"participants": [ { "id": "<p1>", "name": "bea", "actor": "<id of Author>" },
                  { "id": "<p2>", "name": "carl", "actor": "<id of Author>" },
                  { "id": "<p3>", "name": "ada", "actor": "<id of Admin>" } ],
"steps": [
  { "id": "…", "input": "verb", "verb": "modify", "participant": "<p2>", "values": { "<id of title>": "Pump spec" },
    "expect": { "accepted": false, "refusal": "right", "refusedBy": "<id of SecretOnlyOwner>" } },
  { "id": "…", "input": "verb", "verb": "modify", "participant": "<p1>", "values": { "<id of title>": "Pump spec" },
    "expect": { "values": { "title": "Pump spec" } } },
  { "id": "…", "input": "verb", "verb": "freeze", "participant": "<p3>" },
  { "id": "…", "input": "verb", "verb": "modify", "participant": "<p1>", "values": { "<id of title>": "Pump spec 2" },
    "expect": { "accepted": false, "refusal": "frozen" } }
]
```

Carl is refused by the named right, since a deny wins over `AuthorsEditDrafts`. Bea, the owner, edits. Once Ada freezes the document, Bea is refused too.

## 5. Rules

The next free family is MQ99xx.

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ9901 | E | A `rights` entry that is both an inline right and a use, or neither; a use that names something other than a named right; `rights` or `rightsDefault` on an orchestration or a library; a named right with `subjects` used by a lifecycle | Remove the entry |
| MQ9902 | E | A right's `states` names something that isn't a state of its lifecycle (another process's state, a history or a choice) | Remove the state |
| MQ9903 | E | `ends` that aren't ends every subject holds, or beside no verb that acts on a link; `attributes` that aren't attributes every subject holds, or beside a verb other than `read` and `modify`; a named right with `subjects` that names a move, signature or task verb. For a named right used by a lifecycle, reported at the use | Remove the entry |
| MQ9904 | W | An allow right that can't change any answer: its lifecycle allows by default, and so does every other lifecycle that would answer for its verbs (decision 10) | Remove it; set the default to deny |
| MQ9905 | W | Under `deny`, an event or a human task no one can ever do: no actors, and no allow right of its lifecycle names its verb (`complete` for a task); or an event with neither a verb nor actors | Add actors; add an allow right |
| MQ9308 | E | A refused scenario step whose refusal or refusing element differs from `expect.refusal` or `expect.refusedBy` | Update the expectations from a replay (`refresh-scenario`) |

**Existing rules that gain a case.** No finding changes for a model that uses none of this.
- **MQ9701, MQ9704:** a verb named by a right.
- **MQ9604:** an actor from the object in a right, at the lifecycle (at each use for a named right); and in an operation's `requires`, against the operation's subject.
- **MQ9106:** an actor named by a right counts as used.
- **MQ3104 and the typing rules:** `actor()`, `in_state` and `now()` read at `Right` sites. The context and the payload don't (MQ3102).
- **MQ9305:** a verb step's fields (a move, signature or task verb; `values` beside a verb other than `modify`; `attributes` beside one other than `read`; an `end` that isn't the subject's, or an `object` not linked through it, except for `connect`; `delete` on a link); `refusal` or `refusedBy` beside `accepted: true`; `frozen` on a link of an object other than the subject.

The mock backend's rule list follows.

## 6. Resolved model and templates

```csharp
public sealed class RRight
{
    public string Id, Name, DisplayName, Description;            // as other parts and elements
    public string Effect { get; internal set; } = "";            // "allow" or "deny"
    public RList<RActor> Actors { get; internal set; }           // file order; empty means everyone
    public bool Everyone { get; internal set; }                  // Actors is empty
    public RList<RVerb> Verbs { get; internal set; }
    public RList<REnd> Ends { get; internal set; }               // empty when none
    public RList<RAttribute> Attributes { get; internal set; }   // empty when none
    public bool WhileFrozen { get; internal set; }
    public RExpression? Condition { get; internal set; }         // typed; JavaScript null when it uses actor() or in_state
    public bool Named { get; internal set; }                     // an element, not a part of a lifecycle
    public RPackage? Package { get; internal set; }              // named only
    public RList<REntity> Subjects { get; internal set; }        // named with subjects only (question 1)
    public RList<RRightUse> Uses { get; internal set; }          // where it holds
}
public sealed class RRightUse { public string Id; public RRight Right; public RProcess Process; public RList<RState> States; } // States empty: every state
public sealed class RAccess                                     // one cell of "who can do what" (decision 31)
{
    public RState State; public RActor? Actor;                  // Actor null: everyone else
    public RVerb Verb; public string Answer;                    // "allowed", "denied" or "depends"
    public bool ByDefault; public IReadOnlyList<RObject> By;    // the rights, lists or lifecycle that decide
}
// RProcess gains Rights (RList<RRightUse>: inline rights as uses of themselves, then uses of named rights, in file order),
//   RightsDefault ("allow" or "deny") and Access (RList<RAccess>). ResolvedModel gains Rights: the named rights, empty unless used.
// RActor and RVerb gain Rights: the rights that name them directly. RStep gains Verb, Values, Attributes, End and Object.
// RExpectation gains Refusal and RefusedBy. RScenarioObject and its links gain Frozen.
```

- **Templates read** `model.rights`, `process.rights`, `process.rights_default`, `process.access`, the right's members, `use.right`, `use.states`, `actor.rights`, `verb.rights`, `step.verb`, `step.values`, `step.end`, `step.object`, `step.expect.refusal`, `step.expect.refused_by` and `object.frozen`.
- **`process.access`** lists, for each atomic state, each actor named by the lifecycle's rights, events, tasks and gates (plus "everyone else"), and each verb those name, the answer of decisions 10 to 12. A role's row follows membership. It is the same computation as `get_rights`.
- **Dependency keys:** `process.rights` keys on the process file and each named right it uses. `model.rights` keys on `k:right`. `process.access` also keys on every actor file (membership), the verb vocabulary, and the other lifecycles that govern its subjects (decision 10). Only a template that reads them pays.
- **Records:** process records gain `rights` and `rightsDefault`, step records the verb step's keys, expectation records `refusal` and `refusedBy`, and object records `frozen`, each only where present. Named rights get records of kind `right`. So `get_resolved_model` answers don't change for a project without them.

## 7. Simulation and replay

- **One evaluator.** `Processes/RightsEvaluator.cs` works out decisions 10 to 12 for a verb, an actor or participant, an object's states in each lifecycle, and its frozen flags. The interpreter, `get_rights`, `process.access` and the docs pack all use it.
- **Where the interpreter checks:** `HandleEvent` (`Processes/StatechartInterpreter.cs:201`) checks frozen, the event's list (as today), denies and defaults before `Select`. `HandleInvoke` (`:273`) does the same for a human task and `complete`. `CheckSigner` (`:822`) checks denies on `sign` after the signer list. A new `HandleVerb` runs verb steps.
- **The object's row** (object effects section 7) carries the frozen flags, and `modify` writes its values there. A delete marks the row removed.
- **The trace** gains `refusedBy`, `rights` (each answering right with its effect, in check order) and `frozen` (true while the subject is frozen). Notes cover a verb the run didn't apply, a repeated freeze or thaw, and a delete. All are left out when unused, so today's traces don't change.
- **Replay** compares `expect.refusal` and `expect.refusedBy` (MQ9308). Replay keys (`ResolveRun.Processes.cs`) add the named rights a process uses and, for verb steps, the lifecycles that govern the subject. A scenario that uses none of this keeps today's keys.
- **Speed:** rights are evaluated only for lifecycles that have them, and the other lifecycles' states are worked out only when an object or own verb is checked.

## 8. Tools

- **`get_rights` (new):** takes a lifecycle and, optionally, states, actors and verbs. Answers rows of state, actor, verb, answer (`allowed`, `denied`, `depends`), whether it is the default, and the ids that decide, with the lifecycle's default. Pages with `cursor`, as `get_elements` does. Read-only.
- **`simulate_process`, `record_scenario`, `verify_scenarios`:** verb steps, objects' and links' `frozen`, the trace's new members, and the expectations of decision 26.
- **`preview_expression`:** previews a right's condition at its pointer, and for a named right against a lifecycle that uses it.
- **`get_references`, `referenced_by`:** a right's actors, own verbs, states, ends, attributes and condition paths; a use's named right; a verb step's attributes, end and object.
- **Deletes:**
  - A named right in use: refused, unless `remove-references` or `delete-dependents`, which both remove the uses.
  - An actor, state, end or attribute a right names: `remove-references` drops it from the list. **When the last one goes, the right goes too,** since an empty list would widen it to everyone, every state or the whole object. A holding's `within` goes with its object for the same reason (actors section 7).
  - `rename-verb` and `remove-verb` walk rights too. A right whose last verb is removed goes.
- **`get_model_index`:** process rows gain `rights`, the count, only when above zero, and their `verbs` also list the verbs their rights name. Named rights get rows with `effect` and `verbs`. So the Verbs tab counts rights among a verb's uses.
- **`export_process`, `import_process`:** rights travel in `meta.maquettiste`.
- **`validate`:** the rules of section 5.
- **The CLI** follows the same tools. **The rule list, the schemas, the conventions resource, the modeling skill and the tool texts** describe all of this.

## 9. Packs

Output changes only for a model that uses rights (decision 28).

**`csharp-dapper`**
- **`Rights.cs`,** in the processes namespace: `RightEffect`, `RightDefinition` (its name, effect, actors, verbs, ends, attributes, `WhileFrozen`, whether it has a condition, and its description), `RightDecision` (allowed, the refusal and the refusing id), and `Rights.Check`, which follows decisions 10 to 12. A `Can<Verb>` method for each verb a right names calls it.
- **Process definitions:** each lifecycle's entry gains its rights with their states and its default, through an optional parameter written only when the model uses rights, as verbs did.
- **Conditions:** a partial method `Right<Name>(call)` per right with a condition, in the team's half of each lifecycle's handlers, documented with the condition's text. The check calls it.
- **The interpreter:** checks frozen, rights and defaults before guards for events and human tasks, and `sign` at gates. `Refusals` gains `Right`, `Default` and `Frozen`. Whether an instance's object or link is frozen comes from an optional host hook, written only when the model uses freezing.
- **Operations:** the pipeline's `AuthorizationBehaviour` checks an operation's required actors, as it checks an event's. The operation's verb against its subject's lifecycles stays in the team's `Authorize` hook, which can call `Rights.Check` with the object's states.
- **Scenario tests:** a refused step asserts the refusing id. A verb step calls `Rights.Check`.
- **Nothing goes in `helpers.js`.**

**`process-docs`**
- **The lifecycle page:** a Rights section with the default in a sentence ("Anything not stated here is denied."), a table of rights (name, allows or denies, who, does what, in which states, when, why), and a "Who can do what" table per state from `process.access`.
- **A rights page,** only with named rights: each one, its condition, why, and the lifecycles that use it.
- **The scenario page:** verb steps, and refusals with their reason ("refused by SecretOnlyOwner: only its owner may change a secret item").
- **The actor and verbs pages:** the rights that name each actor and verb.

**`sql-ddl`, `seed-data` and the TypeScript sample:** no change.

## 10. Editor

**Process editor, a Rights tab** (lifecycles only), after Gates:
- **"When nothing is said:"** Allowed or Denied, at the top.
- **The rights grid:** one row per right, with Name, **Allows or denies**, **Who** (actor chips, or "Everyone"), **Does** (the shared verb picker, several at once), **In states** (state chips, or "All states"), **When** (the shared value box from object effects, with the condition's type and findings), **While frozen**, and **Why**. Links and Attributes columns show only when a right uses them.
- A use of a named right shows the right's fields read-only, with only In states editable, and an icon button to open it (tooltip "Open the named right").
- **Add right** and **Use a named right…** at the foot. Icon buttons carry tooltips.
- **A toggle, Rights or Who can do what.** The second shows one state at a time (a state picker), actors as rows and verbs as columns. Each cell says Allowed, Denied or Depends, greyed when it is the default. Clicking a cell lists what decides it, each a link. It reads `get_rights`.

**Named rights:** a **Rights** folder in the Processes explorer, after Actors. The right editor has the same fields without states, and a **Used by** list. Search finds them.

**Scenario editor:**
- **Steps:** the input picker gains **Do a verb**. Its row has a verb picker (object, link and own verbs first), and cells for values, attributes, and the link (end and object) as the verb needs.
- **Expectations:** **Refusal** and **Refused by** cells for a refused step, filled by "Update from replay".
- **Objects grid:** a **Frozen** box, and one per link.

**Simulation panel:** a **Do…** menu beside the event buttons runs a verb step as the chosen participant. A refusal shows its reason and links to the element. A frozen object shows a badge. Each step lists the rights that answered.

**Elsewhere:** the actor editor lists the rights that name the actor. The Verbs tab counts rights among a verb's uses.

**Quick fixes:** MQ9901, MQ9902 and MQ9903 remove the entry. MQ9904 removes the right or sets the default to deny. MQ9905 opens the event, or adds an allow right. MQ9308 updates the expectations from a replay.

**Generated and mock files:** the editor's API types come from `openapi.yaml` by the fixed command. The kind's entries in `model/model.ts`, `model/labels.ts` and `app/navigation.ts` are added by hand. The mock backend's `stepper.ts` (the checks, verb steps and frozen flags), `validate.ts`, `refs.ts`, `store.ts`, `cascade.ts`, `modelIndex.ts`, `processHandlers.ts` and a `get_rights` handler follow.

## 11. A project that uses none of this

- **Files:** nothing rewrites a file, so saves, `format`, `format --check` and snapshot import behave as today. Recording a scenario writes what it writes today (decision 26).
- **Findings:** the same. Every new rule needs a right, a default, a verb step or a new scenario key. The rules that gain a case give no new finding without them. MQ9105 stays as it is.
- **Output:** the same bytes. Every pack change depends on the model using rights. An operation's `requires` isn't checked by generated code until then (decision 30).
- **Simulation and replay:** the same outcomes and traces. An event's and a task's actor lists are checked as today, with the same refusal values. New trace members are left out.
- **Tool answers about the model:** the same. New record and index members are left out when unused, and `model.rights` is empty. `get_rights` is new. The rule list, the schemas and the tool texts describe the additions.
- **Capabilities:** once built, these rows of `lifecycle-capabilities.md` become Yes: who may read, change or delete per state; the long list of rights per state; rights for the owner, everyone and named actors; a rule over the object's data and the current user; a named rule reused; rights on one attribute; combining; an access rule for every state; what may be done in this state; and freezing objects and links. A named rule reused across types without a lifecycle is Yes only with question 1. Rights along containment stays No.
- **Speed:** the same. Rights are evaluated only where they exist.
- **Once, as on any upgrade:**
  - Every unit renders again and writes the same bytes, since the engine version is part of each unit's key.
  - The editor refreshes the `.schema` copies, or `validate` warns MQ1008 until refreshed.
  - The index cache is read in full once.
- **A team on mixed engine versions:** an older engine refuses the new kind and keys (decision 32).

## 12. What comes next

| Item | What it takes from rights |
| --- | --- |
| Trigger points | The check kind is a right with code behind it. Freeze and thaw get their points. Checks on automatic moves, and `override` and `set-state` as steps, come there. The evaluator is where a check point's answer joins the others |
| Richer sign-off | Rights for `reject`, `ignore`, `withdraw`, `delegate` and `route` at a gate, checked at the signatures step. "No one holds both roles on one object" as a rule over holdings |
| Scenarios across objects | Verb steps on any object. `connect` and `disconnect` change the run's links, and freezing applies across objects |
| The generated runtime (4.6) | Generated code reads the object, so conditions and frozen flags no longer go to the team |
| Revisions | `revise` gets its behaviour. Rights on it already work |
| If teams ask | Rights along containment; a subject's frozen field; a default per verb; quantifiers over to-many links in conditions; events a state ignores or defers (a chart item). Each adds an optional key and changes no file |

## 13. Decisions the owner must make

1. **Should a named right apply to types directly, with no lifecycle (decision 8)?** A named right would name `subjects`, entities or stereotypes, and apply to every object of those types, with or without a lifecycle, for object, link and own verbs. Without it, a type with no lifecycle can have no rights, and a rule like "only its owner may change a secret item" is used once per lifecycle. It is a permanent widening of rights from lifecycles to types. Recommended: yes. Confidence: medium. It costs about 500 lines and 20 minutes, built last.
2. **Confirm the new names before release.** Names are permanent once released: `rights`, `rightsDefault`, `effect` with `allow` and `deny`, the kind `right`, `whileFrozen`, `ends`, `attributes`; the step input `verb` with `values`, `attributes`, `end` and `object`; `expect.refusal` and `expect.refusedBy`; `frozen` on objects and links; the refusal values `right`, `default` and `frozen`; and the tool `get_rights`. Recommended: as listed. Confidence: medium-high. The build proceeds with them, and the release waits for the owner's word.

## 14. Tests

- **`RightRuleTests` (new):** failing and passing cases for MQ9901 to MQ9905 and MQ9308, and the new cases of MQ9701, MQ9704, MQ9604, MQ9106, MQ3104 and MQ9305. Today's models give the same findings.
- **`RightsEvaluatorTests` (new):**
  - Deny wins, then allow, then defaults; deny among defaults across lifecycles; which lifecycles answer for each verb group.
  - Empty actors as everyone; membership at any depth, deny included; actors from the object; a participant who satisfies an actor other than the one it acts as.
  - States, compound states and conditions, unknown included; `create` in the initial state; `ends`, `attributes` and `whileFrozen`.
  - The closed lists as allows that can't be widened; an operation's required actors.
  - The fixed order and the refusing id for each row of decision 12.
- **`RightsReplayTests` (new):**
  - Verb steps: `modify` values seen by a later guard; freeze, thaw, repeated freeze; frozen links; delete then later steps; verbs recorded but not applied, with notes.
  - Refusals at each check, `expect.refusal` and `expect.refusedBy`, MQ9308; recording with and without rights.
  - Other lifecycles' states from `states`, a bound value and the initial state.
  - Unchanged traces, replays and keys for a model without rights.
- **`NamedRightTests` (new):** loading, references, `get_references`, localized texts; checks at each use; deletes under each choice, including the last actor, state, end or attribute; `rename-verb` and `remove-verb` across rights; index rows, by patch and by full build.
- **`ResolvedRightsTests` (new):** `process.rights`, `process.access` with membership and "depends", `model.rights`, `actor.rights`, `verb.rights`, records, and dependency keys.
- **`GetRightsTests` (new):** the tool's rows, filters and pages agree with `process.access`.
- **No change:** a model without rights gives the same output, files, `format --check`, findings, replay, traces and tool answers. The pack golden tests pass untouched.
- **Pack tests:** a new case per pack. `csharp-dapper`'s `Rights.cs`, condition partials, interpreter checks and scenario tests build with warnings as errors and run. `process-docs`' Rights section, table, rights page and refusal lines.
- **Editor:** unit tests for the grid's columns, the cell answers and the verb step's cells per verb. Browser tests (in the browser pass) for adding rights, using a named right, setting the default, reading Who can do what, a verb step refused by a right and then by freezing, and updating expectations from a replay.
- **Acceptance,** on an invented model:
  - A document lifecycle that denies by default, with the rights of section 4, and the scenario there.
  - An employee in an HR lifecycle that denies changing `salary` to everyone outside HR, and a project lifecycle silent about it. A project manager is refused by the HR right.
  - A gated approval where a right denies `sign` to the submitter (`subject.submittedBy = actor()`), refused at the signatures step.

Each stale-output test is checked to fail without its fix.

## 15. Tasks and the build plan

### Tasks

The sizes are days for a person who knows the code, with tests and a review. They show how the groups compare.

| Wave | Group | What | Size |
| --- | --- | --- | --- |
| 1 | Contract and model | The schemas, `openapi.yaml`, rule texts; the `right` kind; `rights` and `rightsDefault`; the `Right` site kind; MQ9901 to MQ9905 and the changed rules | Two days |
| 1 | References, deletes and the evaluator | References, deletes and their widening guard, `rename-verb` and `remove-verb`, index rows, localization; `RightsEvaluator` | A day and a half |
| 1 | Editor, part one | The Rights tab and grid, named rights in the explorer and their editor, quick fixes, the mock's validation and references | A day and a half |
| 2 | Simulation and scenarios | The interpreter's checks, verb steps, frozen flags, the trace, MQ9308, recording, replay keys | A day and a half |
| 2 | Resolved model, the tool and output | `RRight`, `process.access`, records, `get_rights`, tool texts; `csharp-dapper` and `process-docs` | Two days |
| 2 | Editor, part two | Who can do what, scenario verb steps and expectations, the simulation panel, the mock's stepper and `get_rights` | A day and a half |
| 3 | Docs | User guide (processes, scenarios, rights, expressions), releases, modeling skill, capabilities rows, `next.md` | Half a day |

That comes to about ten and a half days for a person. Question 1 adds about half a day.

**Checklist for every group.** Anything that reads or writes a lifecycle, an actor list, a verb, a scenario step or a refusal must be found and handled:
- **A new element kind:** `Model/ElementKind.cs`, `Model/KindInfo.cs` (folder `model/rights`), `Model/ElementBase.cs`, `get_model_kinds`, the localizable node kinds in `Localization/LocaleChains.cs`, and `Loading/NameIndex.cs` for inline rights as parts.
- **Expressions:** the `Right` site in `Expressions/ExprType.cs` and every site switch the `Set` kind needed (object effects decision 3), and `Validation/ExpressionRules.cs` (`ExprSites.Of`).
- **Processes:** `Model/Processes.cs`, `Processes/StatechartInterpreter.cs`, `Processes/ScenarioReplayer.cs`, `Processes/StepTrace.cs` (`Refusals`), `Processes/ScenarioObjects.cs`, `Processes/Participants.cs`, `Processes/ProcessApi.cs`, and the exporter and importer.
- **Resolver and records:** `Resolution/ResolvedProcesses.cs`, `Resolution/ResolveRun.Processes.cs`, `Editor/ResolvedRecords.cs`.
- **Contracts:** the schema copies, `openapi.yaml` and its generated files. `validation-rules.json` is written only by the fixed command.
- **Test harness:** `RenderKit.WithSettings` and `ResolvedCoverageTests`.

### How it gets built

**What earlier items cost.** 4.1 took about 3.75 hours for about 11,000 lines: about 155 output tokens, 425 new input and 40,000 cached re-reads per line. Verbs, about 5,000 lines, took about 2.5 hours. Object effects, about 6,000 lines written by hand (10,500 in the diff with pack expected files), took about 3.5 hours, slower per line because of the typer and the simulator. Engine builders cost about 20M cached per 100 steps. In every item, the review and the browser pass each found real bugs.

**This item's size.** About 9,000 lines with tests, without pack expected files: engine contract, rules and references about 1,500 and their tests about 900; the evaluator about 450 and its tests about 400; simulation and scenarios about 750 and their tests about 700; resolved model, tool and packs about 900 and tests about 300; editor and mock about 2,300; docs about 400; question 1 about 500. That is about 1.5 times object effects. It touches the simulator and the editor most.

**Agents.** Each is capped at about 100 steps or 200k context. Opus 5.5 builds the engine and pack output and runs every review. Sonnet 5.5 builds the editor, the mock and the docs.

| Wave | Agent | Model | Steps | Starts from |
| --- | --- | --- | --- | --- |
| 1 | Contract and model; **contract first**: the schemas, all `openapi.yaml` changes including the trace, records and `get_rights`, rule texts. Keeps every change to `Validation/ExpressionRules.cs` | Opus | About 100 | `origin/object-effects` |
| 1 | References, deletes and `RightsEvaluator`. Owns `ProcessTools.cs` | Opus | About 100 | The contract commit |
| 1 | Editor and mock, part one | Sonnet | About 100 | The contract commit |
| 2 | Simulation and scenarios | Opus | About 100 | Wave 1's engine agents |
| 2 | Resolved model, records, `get_rights`, tool texts, replay keys, and output for both packs | Opus | About 100 | Wave 1's engine agents, beside the simulation agent |
| 2 | Editor and mock, part two | Sonnet | About 100 | Wave 1's editor agent |
| 3 | Review of the pull request | Opus | About 60 | All builders' finish |
| 3 | Docs | Sonnet | About 50 | The builders' finish, beside the review |
| 3 | Browser pass: the editor flows, on the built branch | Sonnet | About 50 | The builders' finish, beside the review |
| 4 | Fixers, split by area (engine and output; editor and mock; docs) | Opus or Sonnet | About 40 each | The review's and the browser pass's findings |
| 4 | A short second browser pass on the fixes | Sonnet | About 25 | The editor fixer's finish |

- **At most three agents run at once.** The evaluator comes in wave 1, so both wave 2 engine agents use it without waiting on each other. The simulation agent works in `Processes/` and the process API. The output agent works in `Resolution/`, `Editor/ResolvedRecords.cs`, the tool and the packs.
- **One pull request, stacked on object effects,** with the contract, engine, editor, output, docs and fix commits. The owner merges object effects first, then this one.
- **Generated files** (the editor's API types, the recorded rule list, the sample app's copies) are written only by the fixed commands, after each merge into the branch. Pack expected files stay with the builder, so their changes are reviewed.
- **Question 1** is the last step of the contract agent and the output agent. If the owner says no, it is left out, and nothing else changes.
- **Fixers** are fresh agents with the exact findings. **A design gap found while building** stops the work, and the owner is asked.

**Estimate.**

| | Wall time | Output | New input | Cached re-reads |
| --- | --- | --- | --- | --- |
| Wave 1 (contract, model, references, evaluator, editor part one) | About 1.5 hours | 0.4M | 1.1M | 85M |
| Wave 2 (simulation, output, editor part two) | About 1.4 hours | 0.4M | 1.0M | 80M |
| Wave 3 (review, docs, browser pass) | About 0.8 hours | 0.2M | 0.6M | 45M |
| Wave 4 (fixes, second browser pass, CI) | About 0.9 hours | 0.15M | 0.4M | 50M |
| In all | **About 4.5 to 5.5 hours** | **About 1.15M** | **About 3.1M** | **About 260M** |

The output and new-input figures follow 4.1's cost per line, a little under, as object effects did. The cached figure sits between two readings: by steps, about 165M for the agents (about 20M per 100 Opus steps and 15M per 100 Sonnet steps, fixers included) plus about 50M for coordinating one pull request, so about 215M; by 4.1's cost per line, about 360M. The wall time is object effects' 3.5 hours scaled by size, less a little for the simpler typer work. Without question 1, take off about 20 minutes and 15M cached.
