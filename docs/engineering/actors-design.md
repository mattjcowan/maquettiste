# Actors: design

**Status (2026-11-05): a design for the owner, not built.** It designs the item after 4.1 in the owner's order: actors with membership, actors worked out from the object, roles held within a context, and the system as an actor. [Who, what and when](lifecycle-actors-scenarios.md) section 4 sets its direction, and [what lifecycle systems can do](lifecycle-capabilities.md) is the yardstick. [The design of 4.1](lifecycle-design.md) gives its shape. A file path without a project prefix is under `src/Maquettiste.Engine/`. The examples are invented. Each new name and format is held to the roadmap's test: it stays for good, it must help any team whether they use it or not, and it stays optional.

## 1. In short

Today an actor is a person, a role or an external system, and each stands alone. Nothing says a person holds a role. There is no "the object's owner", no "a reviewer on this project", and no way to say "the system did this".

| Layer | What changes |
| --- | --- |
| The model | Two new actor types, **group** and **organization**, and a third, **system**, for the system itself (shown as "Automatic"). Any actor may be a **member of** a role, group or organization, and roles nest. An actor may be **worked out from the object**: its owner, the person a field names, or the members of a role within a context such as the object's project. A lifecycle's subject entry may name where the object's **owner** is held. A scenario names its **personas**, and a step says which persona acts |
| Code generation | Templates read memberships, actors from the object, and personas. `csharp-dapper` adds the new types and membership checks, `process-docs` lists members and personas, each only when a model uses them |
| The team's system, or a lifecycle system | Nothing changes until the team uses the new output or builds from the model |

Membership changes what an existing actor list means only for a model that adds memberships: a list that names a role then also accepts its members. The gate's own description already says "the actors whose members may sign".

**A project that uses none of this** keeps its files, output, findings and tool answers. Section 10 lists everything.

## 2. Scope

| | What | Section |
| --- | --- | --- |
| **This item** | The types `group`, `organization` and `system`; `memberOf`; actors from the object (`relative`); the subject entry's `owner`; scenario personas and the step's `persona`; the explorer's later steps | 3 to 10 |
| **Decided here, built as its own small item** | A stereotype as a set of types | Decision 19 |
| **Recorded here for the rights item** | How rights combine, and the default when nothing is said | Decisions 22 and 23 |
| **Later items** | Verbs; setting the object's data; reading another lifecycle's state; rights (with "everyone", rights per attribute and along containment); trigger points; richer sign-off (delegation, escalation, separation of duties, membership valid for a period) | 11 |

## 3. Decisions

Each decision gives its confidence: **high**, **medium** or **low**.

### Types

1. **Two new actor types: `group` and `organization`.** Confidence: high that it is no harder or riskier than roles alone; medium-high overall.
   - **Why:** lifecycle systems commonly tell a person, a role, a group and an organization apart, with membership and a hierarchy. The union is the yardstick. A role is a job someone does (Reviewer). A group is a set of actors (Night shift). An organization is a unit with a place in a hierarchy (Supplier, Plant 3).
   - **What the engine does with them:** the same as with a role. Role, group and organization are all things an actor can be a member of. The type tells readers, packs and lifecycle-system configuration which one it is. A pack writing a lifecycle system's setup needs that, and a stereotype would be a habit, not a meaning.
   - **The cost:** one schema value each, the C# enum, the index row, `csharp-dapper`'s enum (only when used, decision 26), the editor's labels and folders, and the mock. Membership costs the same whichever types may hold members.
   - **The alternative:** roles with membership, with "group" as a stereotype. It saves two enum values and loses the meaning. It is not chosen.
2. **The system itself is an actor of type `system`, labeled "Automatic".** Confidence: medium-high.
   - **Why a type:** rights and history must be able to say "only the system may release this" and "the system moved it". A reserved id that names no element would break the rule that every reference is an element's id.
   - **Why `system`:** beside `external-system` it reads as "this system, not another". The editor and docs label it **Automatic**, as the owner chose, so the two aren't confused. The file value never shows in the editor.
   - **Explicit, and at most one:** a project creates it if it wants it. Two actors of type `system` are an error (MQ9605), since "the system itself" is one thing. Other automated agents are external systems, or the system actor holds roles through `memberOf` (a Scheduler role).
   - **What it does now:** it is an ordinary actor in every list. When a project has one, simulation and replay name it as the actor of time steps, eventless moves and finished invokes. Without one, those moves have no actor, as today.

### Membership

3. **Membership is stated on the member, as `memberOf`, a list of actor ids.** Confidence: high.
   - "A person holds roles" reads from the person's file. A role's members are listed by the editor and the resolved model, not stored twice.
   - It is the same shape as every other actor list (`actors`, `signers`, `requiredActors`).
   - Many members of one role touch many small files, so teammates rarely conflict on one file.
4. **Only roles, groups and organizations have members.** Confidence: high.
   - A member may be any actor except one worked out from the object (decision 12).
   - A person, an external system and the system actor have no members: nothing they could contain would act differently.
   - Memberships form no cycle (MQ9602). An organization's hierarchy is membership too: a department is a member of its division.
5. **An actor in a list stands for itself and all its members, at any depth.** Confidence: high.
   - This holds for an event's `actors`, a gate's `signers` and `requiredActors`, and a human task's `actors`.
   - It is what "a right given to a role reaches its members" means, and the rights item inherits it, deny rights included.
   - Simulation and replay accept a step whose actor is a member of a listed actor. A required actor counts as signed when a signature's actor or persona is it or one of its members.
   - Without any `memberOf`, every actor stands only for itself, so outcomes don't change.
6. **Model rules that read actor lists follow membership.** Confidence: high.
   - **MQ9105:** a signer that is a member of one of the event's actors is no longer flagged.
   - **MQ9106:** an actor counts as used when a process names it, when it is a member of a used actor, when an actor from the object names it, or when a persona names it.
   - **MQ9101:** unchanged. It counts only when every signer is a person, and a person has no members.
7. **No validity periods, delegation or exclusive roles in this item.** Confidence: high.
   - A membership valid for a period is about real people, which the team's system holds.
   - Delegation is a verb on a signature or a task, with rules about who may receive it. It belongs to richer sign-off, which uses this item's actors to say who.
   - "No one holds both of these roles on one object" and "the approver is not the submitter" belong to sign-off and rights. The second needs the object to record who submitted, which comes with setting the object's data.

### Actors worked out from the object

8. **An actor worked out from the object is an actor element with a `relative` key.** Confidence: medium-high.
   - Every list that takes an actor takes it with no change to `process.json`: events, gates, human tasks, and later rights and triggers.
   - One definition, such as `ProjectReviewer`, is reused in many places, and its page says what it means.
   - **The alternative:** qualify each use in place, with an object instead of an id in each list. It would change three lists now and more later, mix ids and objects in one list, and repeat the same path at every use. It is not chosen.
9. **Three forms, all in this item.** This improves question 2's recommendation, since 4.1's guards already follow to-one paths and the same code serves.
   - **`owner: true`:** the object's owner (decision 10). Confidence: high.
   - **`path`:** the actor that a field of the object names, reached through to-one relation ends if needed. For example `requestedBy`, or the requester's manager through `requester` then `manager`. Confidence: high.
   - **`membersOf` with `within`:** the members of a role, group or organization within a context object, reached from the object by a to-one path. For example "reviewer on this item's project". An empty `within` means the object itself, as for a project's own lifecycle. This is how team roles and security contexts are stated. Confidence: medium-high.

   Exactly one form per actor. To-many paths ("anyone on the project's team") come later; each would add a form, and an actor without one keeps its meaning.
10. **A subject entry may name the object's `owner`.** This is the key 4.1 kept for this item. Confidence: medium-high.
    - It names one single-valued attribute or to-one end of the entry's entity: own, inherited or from a stereotype, as for a binding.
    - Each subject names its own owner field, so one `Owner` actor serves a lifecycle over several types that hold their owner differently.
    - **It is optional.** Without it, the owner is held by the team's system, as many lifecycle systems hold a built-in owner on every object. No finding asks for it. In a scenario, such an object names its owner persona directly (decision 16).
    - It is allowed on an orchestration's subjects too, since it is about the object, not the state.
11. **Paths follow 4.1's rule for guards (decision 20 there).** Confidence: high.
    - Ids must mean the same thing for every subject of the process that uses the actor: the subject's own and inherited members for one subject, a shared base entity's for several.
    - A path ends at a single-valued attribute or a to-one end. Nothing else is required of the attribute's type, since the model doesn't decide how the team stores who someone is.
    - A use where a path can't be worked out is an error at the use (MQ9604). A library's gate or human task is checked at each process that uses it.
12. **An actor from the object can't be a member, and has no members.** Confidence: high. It is worked out per object at run time, so "the owner is a Reviewer" can't be a fact of the model. Its `type` says what it yields: `person` for an owner, `role` for "reviewer on this project".
13. **Holding a role with no context counts within every context.** Confidence: medium. A persona who is a Reviewer everywhere is also a reviewer on project P. A holding within P counts only within P, never for a plain `Reviewer` in a list.

### Personas

14. **A scenario names its personas.** Confidence: medium-high.
    - Personas live in the scenario, not among the model's actors, so they never reach generated production code. `csharp-dapper` writes a constant per model actor.
    - A persona has a `name` and may give:
      - `identity`, the value the team's system would hold for them, such as a login or a number (the name when absent);
      - `actor`, the person, external system or system actor they stand for, whose memberships they then have;
      - `holds`, the roles, groups and organizations they hold, each optionally `within` one of the scenario's objects;
      - `object`, the scenario object that is their record, such as an employee.
    - Personas are parts of the scenario, with name forms (`<scenario>.<persona>`), as 4.1 made objects. Each holding has an id.
    - A persona may stand for an actor that carries a project's own persona stereotype, so the two uses of the word fit together (section 12, question 2).
15. **A step names its `persona`.** Confidence: high.
    - The step's `actor` keeps its meaning: the actor the step acts as.
    - With a persona and no actor, the step acts as the first entry of the list, in file order, that the persona satisfies. So two replays never disagree.
    - With both, the actor must be one the persona satisfies, or it is MQ9305.
    - The signer identity defaults to the persona's identity. A step with no persona behaves as today.
16. **A scenario object may name its `owner` persona,** only when its subject entry names no owner field. Otherwise the owner comes from the object's own values and links, and naming both is MQ9305. Confidence: medium.
17. **How a persona satisfies an actor.** Confidence: high.
    - **A plain actor A:** the persona's `actor` is A or a member of A, or the persona holds A, or a member of A, with no context.
    - **The owner:** the subject's owner field holds the persona's identity or links to the persona's object. Without an owner field, the object's `owner` names the persona.
    - **A path:** the path, followed through the scenario objects' links, ends at the persona's identity or at the persona's object.
    - **Members of R within a path:** the persona holds R, or a member of R, within the object the path reaches, or with no context.
    - **Values:** an attribute matches when its value's text equals the identity's text. A step with only an `actor` matches an actor from the object only when it names that same actor, as today's exact match.

### Scope

18. **The order inside this item:** types, membership and the system first, then actors from the object and personas, since personas are the only way to prove contexts in a scenario. Confidence: high.
19. **A stereotype as a set of types: decided here, built as its own small item right after this one.** Confidence: medium-high.
    - **Why apart:** it touches lifecycle subjects, guard scope and later relation ends, and no actor. Building it here would mix two reviews in one pull request. It can run beside this item: the two share only the rule catalog and `process.json`'s subject entry.
    - **Its meaning, decided once:** the entities that carry the stereotype after package and schema defaults, implied stereotypes and exclusions, as the resolver works them out today. It is named by the stereotype's key wherever a list of entity types is accepted.
    - **Its first uses:** a subject entry naming a stereotype in place of an entity, and guard conditions reading the stereotype's attributes, which 4.1 kept out. Relations with several types per end come later, once their drawing is agreed.
    - **Order kept:** it still comes before verbs. Only the pull request is separate (section 12, question 1).
20. **Delegation belongs to richer sign-off.** Confidence: high. See decision 7.
21. **Separation of duties belongs to sign-off and rights.** Confidence: high. The section 10 order of the direction puts it there.
22. **For the rights item: how rights combine.** Confidence: high for the rule, medium-high for the refinement.
    - Each right answers allow, deny or nothing. **Any deny wins, then any allow, then the default.**
    - **The refinement, for an object in several lifecycles:** a lifecycle's default applies only when no right in any of the object's lifecycles answers. Then the defaults combine, and a "denied" default wins. So a project lifecycle that says nothing about changing a salary leaves the HR lifecycle to decide, as the direction says. Its default doesn't override HR's explicit allow.
    - An object in no lifecycle is allowed everything, as today.
    - Actors rely on this only in one way: a deny to a role reaches its members, as an allow does (decision 5).
23. **For the rights item: the default when nothing is said.** Per lifecycle, allowed unless the lifecycle says otherwise. Confidence: high. "Denied" by default would change every existing lifecycle's simulation.
24. **This is 0.14.0, and a team upgrades together.** Confidence: high, by the owner's 4.1 decision 6. An older engine refuses a file with a new type, `memberOf`, `relative`, a subject's `owner` or personas. `formatVersion` stays 1.

### Code generation

25. **Generated code checks membership only when the model has memberships.** Confidence: high. `csharp-dapper` writes a member map and a membership-aware check. Without memberships, its output is unchanged byte for byte.
26. **New types reach generated enums only when used.** Confidence: high. `csharp-dapper`'s `ActorType` gains `Group`, `Organization` or `System` only when an actor has that type. Today's template writes `ActorType.{{ pascal a.type }}` for every actor, so a model with a group would not compile without this. It ships in the first pull request.
27. **Actors from the object are left to the team's code.** Confidence: medium-high. Generated code can't read the object until the generated runtime (4.6). `csharp-dapper` accepts an envelope acting as such an actor only through a resolver the host supplies, a delegate emitted only when the model has actors from the object. Without one it refuses, so nothing is let through by accident.

## 4. Schema and files

| File | Key | Type | Notes |
| --- | --- | --- | --- |
| `actor.json` | `type` | adds `group`, `organization`, `system` | Decisions 1 and 2 |
| `actor.json` | `memberOf` (after `type`) | id list | Decisions 3 to 5 |
| `actor.json` | `relative` (after `memberOf`) | `{ owner: true }`, `{ path: [ids] }`, or `{ membersOf: id, within: [ids] }` | Decisions 8 to 12; exactly one form; `within` defaults to `[]` |
| `process.json` | subject entry `owner` (after `boundAttribute`) | id | Decision 10 |
| `scenario.json` | `personas` (after `objects`) | list of `{ id, name, displayName?, identity?, actor?, holds?: [{ id, actor, within? }], object? }` | Decision 14; `identity` is a string or an integer |
| `scenario.json` | step `persona` (before `actor`) | id of a persona of the scenario | Decision 15 |
| `scenario.json` | object `owner` (after `links`) | id of a persona of the scenario | Decision 16 |

Also updated: the sample app's schema copies, `openapi.yaml` by hand (the index row's `actorType` values, its new `memberOf` and `relative` members, and the simulation inputs), and the editor files generated from it.

```json
{ "kind": "actor", "name": "Reviewer", "type": "role" }
{ "kind": "actor", "name": "SeniorReviewer", "type": "role", "memberOf": ["<id of Reviewer>"] }
{ "kind": "actor", "name": "ChangeBoard", "type": "group" }
{ "kind": "actor", "name": "Clerk", "type": "person", "memberOf": ["<id of ChangeBoard>"] }
{ "kind": "actor", "name": "Automatic", "type": "system" }
{ "kind": "actor", "name": "Owner", "type": "person", "relative": { "owner": true } }
{ "kind": "actor", "name": "RequesterManager", "type": "person", "relative": { "path": ["<id of end requester>", "<id of end manager>"] } }
{ "kind": "actor", "name": "ProjectReviewer", "type": "role", "relative": { "membersOf": "<id of Reviewer>", "within": ["<id of end project>"] } }
```

```json
"subjects": [ { "id": "…", "entity": "<id of Item>", "owner": "<id of Item.ownerLogin>" } ]
```

```json
"objects": [
  { "id": "<o1>", "name": "item", "entity": "<id of Item>", "values": { "<id of Item.ownerLogin>": "bea" },
    "links": [ { "end": "<id of end project>", "object": "<o2>" } ] },
  { "id": "<o2>", "name": "apollo", "entity": "<id of Project>" }
],
"subject": "<o1>",
"personas": [
  { "id": "<p1>", "name": "ann", "holds": [ { "id": "…", "actor": "<id of Reviewer>", "within": "<o2>" } ] },
  { "id": "<p2>", "name": "bea", "actor": "<id of Clerk>" }
],
"steps": [ { "id": "…", "event": "<id of Submit>", "persona": "<p2>" }, { "id": "…", "event": "<id of Approve>", "persona": "<p1>" } ]
```

Here Bea may submit as the item's owner, and Ann may approve as a reviewer on project Apollo.

## 5. Rules

The next free family is MQ96xx.

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ9601 | E | A `memberOf` entry that isn't a role, group or organization, is an actor from the object, names the actor itself, or is listed twice | Remove the entry |
| MQ9602 | E | Memberships form a cycle; the message names the cycle | |
| MQ9603 | E | A `relative` with no form or more than one; `within` without `membersOf`; `membersOf` naming something other than a plain role, group or organization; `memberOf` on an actor from the object; or type `system` with `relative` | |
| MQ9604 | E | An actor from the object used where it can't be worked out: in a process with no subjects, or with a path that doesn't resolve on every subject by decision 11, or that ends at a many-valued attribute, a value object or a to-many end. Reported at the use | |
| MQ9605 | E | Two or more actors of type `system` | Change the type |
| MQ9606 | E | A subject entry's `owner` that isn't a single-valued attribute or to-one end of its entity (own, inherited or from a stereotype) | Clear the owner |

**Changed:**
- **MQ9105, MQ9106:** follow membership (decision 6). MQ9105 says nothing when the signer or an event actor is an actor from the object, since that can't be known from the model.
- **MQ9305:** gains the persona cases: a holding that isn't a role, group or organization; a `within` or `object` that isn't one of the scenario's objects; a persona's `actor` that is a role, group, organization or actor from the object; a step's `persona` that isn't the scenario's; a step's `actor` the persona doesn't satisfy; an object's `owner` beside an owner field.
- **MQ9301:** a refusal by actor names the persona and the list it didn't satisfy.

MQ2002 already covers ids of the wrong kind. The mock backend's rule list follows.

## 6. Resolved model and templates

```csharp
public sealed class RActor
{
    public RList<RActor> MemberOf { get; internal set; }      // direct, file order
    public RList<RActor> Members { get; internal set; }       // direct, by name
    public RList<RActor> AllMemberOf { get; internal set; }   // at any depth, by name, each once
    public RList<RActor> AllMembers { get; internal set; }    // at any depth, by name, each once
    public RRelative? Relative { get; internal set; }
}
public sealed class RRelative
{
    public string Form { get; internal set; } = "";           // "owner", "path" or "members-of"
    public IReadOnlyList<RObject> Path { get; internal set; } // REnd steps, then the last RAttribute or REnd; for "members-of", the within path (ends only)
    public RActor? MembersOf { get; internal set; }
}
public sealed class RSubject { public RAttribute? OwnerAttribute { get; internal set; } public REnd? OwnerEnd { get; internal set; } }
public sealed class ResolvedModel { public RActor? SystemActor { get; internal init; } }
public sealed class RScenario { public RList<RPersona> Personas { get; internal set; } }
public sealed class RPersona
{
    public string Id, Name; public string? DisplayName; public object Identity;   // a string or a long; the name when absent
    public RActor? Actor; public RList<RHolding> Holds; public RScenarioObject? Object;
}
public sealed class RHolding { public string Id; public RActor Actor; public RScenarioObject? Within; }
// RStep gains Persona; RScenarioObject gains Owner (an RPersona).
```

- **Templates read** `actor.member_of`, `actor.members`, `actor.all_member_of`, `actor.all_members`, `actor.relative`, `subject.owner_attribute`, `subject.owner_end`, `model.system_actor`, `scenario.personas`, `step.persona` and `object.owner`.
- **Dependency keys:** `members` and `all_members` key on `k:actor`, since any actor file can add a member. `member_of` keys on the actor's own file and `all_member_of` on each file along the chain. A relative path keys on the entities it passes through. Only a template that reads a member pays for its keys.
- **Records:** `memberOf`, `relative`, `owner`, `personas` and `persona` appear only where used, so `get_resolved_model` answers don't change for a project without them.
- **`RActor.Events`, `Gates` and `Processes`:** unchanged, listing direct uses. A template finds uses through membership with `all_member_of`.

## 7. Tools

- **`get_references`, `referenced_by`:** `memberOf`, a relative path's ids, `membersOf`, the subject's `owner`, and a persona's `actor`, holdings, `within` and `object` are references. Personas are parts, with name forms.
- **Deleting:**
  - An actor named in `memberOf` or a holding: `remove-references` drops the entry, the way an actor goes from a list today.
  - An attribute or end in a relative path, or a role named by `membersOf`: refused under `remove-references` unless the actor is deleted too, as a scenario's `subject` is in 4.1. `delete-dependents` deletes the actor.
  - A subject's `owner` field: `remove-references` clears the key, since it is optional.
  - A scenario object named by a holding's `within`: the holding goes with the object. Dropping only `within` would widen it to every context.
- **`get_model_index`:** actor rows gain `memberOf` and `relative` (the form), each only when set. `actorType` takes the new values.
- **`simulate_process`, `record_scenario`, `verify_scenarios`:** take `personas`, and a step's or input's `persona`. A recorded step keeps the persona the panel acted as.
- **No new batch operation.** Adding members to a role is a batch of patches to the members' files, which `apply_batch` already does all or nothing.
- **The CLI** follows the same verbs. **The rule list, the schemas and the tool texts** describe all of this.

## 8. Packs

Output changes only for a model with a new type, a membership, an actor from the object, a subject `owner`, or personas.

**`csharp-dapper`**
- **`Actors.cs`:** `ActorType` gains `Group`, `Organization` and `System` only for the types the model uses (decision 26). The builder checks that a member named `System` compiles beside `using System;` with warnings as errors.
- **Membership, when any actor has `memberOf`:** `Actors.MemberOf`, each actor's memberships at any depth, and `Actors.Allows(list, actor)`. The interpreter's actor checks for events, human tasks and signers call it. Without memberships the interpreter is written as today.
- **Actors from the object (decision 27):** each keeps its constant, with a comment saying how it is worked out. The interpreter gains a host-supplied resolver only when the model has one. Without a resolver such an actor is refused.
- **Scenario tests:** a step with a persona passes the actor it acts as, worked out by the engine when the tests are written, and its identity as the signer. Scenarios without personas give the same tests.
- **Nothing goes in `helpers.js`,** since changing it renders every unit of the pack again.

**`process-docs`**
- **The actor page:** "Member of", "Members", and for an actor from the object a sentence saying how it is worked out ("the item's owner", "members of Reviewer within the item's project"). Events, gates and tasks it acts in through membership are listed under their own heading.
- **The scenario page:** a personas table, and each step names its persona.
- **The process page:** each subject's owner field.

**`samples/typescript-pack`:** the same type values and membership check as `csharp-dapper`, when used.

**`sql-ddl`:** no change. Its gate audit table already records the actor as text.

## 9. Editor

**Processes explorer, Actors** (#41 built the folders):
- **Folders:** **People**, **Roles**, **Groups**, **Organizations**, **Systems** (external systems), **Automatic** (the system actor) and **From the object** (actors with `relative`), each shown only when it has actors. An actor from the object sits only in **From the object**, whatever its type.
- **A role, group or organization lists its members** as child rows that reveal the member's own row. A member that holds members can be expanded in turn. The tree stops at an actor already on the path, so a model with a cycle (MQ9602) still draws.
- **Row text:** an actor's secondary text adds "member of Reviewer, ChangeBoard", or for an actor from the object, its form ("owner", "from a field", "within a context").
- **Personas sit with their scenarios:** under a scenario, a **Personas** list, each row opening the scenario editor at that persona.

**Actor editor:**
- **Type:** person, role, group, organization, external system, and "Automatic (the system itself)". Choosing Automatic when the project already has one is refused with the reason.
- **Member of:** a picker of roles, groups and organizations, with each one's type. Hidden for an actor from the object.
- **Members:** read-only, direct members with links, plus a count at any depth. **Add members…** picks actors and writes their files in one batch, so Undo reverts it in one step.
- **From the object:** a section with the form (the object's owner; a field of the object; members of a role within a context) and a path picker, reused from the guard condition editor.

**Process editor:**
- **Subject control:** gains **Owner**, a picker of single-valued attributes and to-one ends, for a lifecycle with one subject. With several subjects it is read-only, editable in the JSON view, as 4.1 decided for subjects.
- **Events, Gates and human tasks:** actor pickers list actors from the object in their own group.

**Scenario editor:**
- **Personas:** a table with name, identity, stands for, holds (each with an optional "within" object) and record object.
- **Steps:** a **Persona** column beside **Actor**. The Actor column shows the actor worked out, greyed, when only a persona is given.

**Simulation panel:** **Act as** lists the scenario's personas, then the actors. A refusal names the persona and the list.

**Quick fixes:** MQ9601 removes the entry, MQ9605 changes the type, MQ9606 clears the owner.

**Mock backend:** `validate.ts`, `refs.ts`, `store.ts` and `processHandlers.ts` follow the new keys and rules. The recorded rule list follows.

## 10. A project that uses none of this

- **Files:** nothing rewrites a file, so saves, `format`, `format --check` and snapshot import behave as today.
- **Findings:** the same. MQ9105 and MQ9106 change only through memberships, actors from the object and personas. No new rule fires on a model without the new keys and types.
- **Output:** the same bytes. Every pack change depends on a new type, key or persona.
- **Simulation and replay:** the same outcomes. Without memberships an actor stands only for itself, and without a system actor automatic moves have no actor, as today.
- **Tool answers about the model:** the same, since new index and record members are left out when unused. The rule list, the schemas and the tool texts describe the additions.
- **Speed:** the same. Membership closures are computed only when an actor has `memberOf`.
- **Once, as on any upgrade:**
  - Every unit renders again and writes the same bytes, since the engine version is part of each unit's key.
  - The editor refreshes the `.schema` copies, or `validate` warns MQ1008 until refreshed.
  - The index cache is read in full once.
- **A team on mixed engine versions:** an older engine refuses a file using the new keys or types (decision 24).

## 11. What comes next

What each later item takes from this one:

| Item | What it needs from actors |
| --- | --- |
| A stereotype as a set of types (decision 19) | Nothing; it runs beside this item |
| Verbs | Nothing new. A verb's right is given to actors as designed here |
| Setting the object's data | Moves that record the acting actor, such as `submittedBy`; the actor of an automatic move is the system actor |
| Reading another lifecycle's state | Nothing new |
| Rights | Membership reaching members (decision 5), actors from the object for owner rights, combining and defaults (decisions 22 and 23). It adds "everyone", rights per attribute and rights along containment |
| Trigger points | A trigger runs as the acting actor or as the system actor |
| Richer sign-off | Signers and routing by membership and context; delegation; separation of duties; membership valid for a period, if the model should say it |
| Scenarios across objects | Personas already act on the scenario's subject; once steps name their object, actors from the object are worked out against that object |

## 12. Decisions the owner must make

1. **Build "a stereotype as a set of types" as its own small pull request, right after actors?** It keeps the owner's order and only separates the build, since it touches lifecycle subjects and guards, not actors (decision 19). Recommended: yes. Confidence: medium-high. It doesn't block this item.
2. **Keep the word "personas" for the people in a scenario?** Some projects already put a persona stereotype of their own on actors (the user guide shows one with goals). The product defines no such stereotype, and a scenario persona may stand for such an actor, so the two fit. "People" would be wrong for a persona standing for an external system, and "cast" already describes the scenario's objects. Recommended: keep "personas". Confidence: medium. This name is permanent, so the second wave's contract commit waits for it if the owner wants another word. The first wave doesn't.

## 13. Tests

- **`ActorRuleTests` (new):** failing and passing cases for MQ9601 to MQ9606, and for the changed MQ9105, MQ9106, MQ9301 and MQ9305. Today's models give the same findings.
- **`ActorMembershipTests` (new):**
  - Membership at any depth, by role, group and organization; closures ordered by name with no repeats.
  - An event, a gate's signers and required actors, and a human task accept a member in simulation and replay.
  - Deleting an actor named in `memberOf`, under each delete choice.
  - The index row's `memberOf`, by patch and by full build.
  - Dependency keys: adding a member renders a unit that reads `members` again, and only such a unit.
- **`RelativeActorTests` (new):**
  - Each form, on one subject and on two subjects with a shared base; the owner through each subject's own field.
  - A path through to-one ends; a path that fails MQ9604; use in a library gate, checked at each user.
  - Deleting an attribute on a path, under each delete choice.
- **`PersonaTests` (new):**
  - Personas as parts, with name forms, references and deletes, including a holding's `within` object.
  - Each satisfaction case of decision 17, the picked actor in file order, and the signer from identity.
  - An object's `owner` persona; both owner sources at once (MQ9305).
  - Simulating and recording with personas.
- **`SystemActorTests` (new):** time steps, eventless moves and finished invokes name the system actor when there is one, and nothing otherwise; two system actors give MQ9605.
- **No change:** a model with today's actors, gates and scenarios gives the same output, files, `format --check`, findings, replay and tool answers. The pack golden tests pass untouched.
- **Pack tests:**
  - `csharp-dapper` with a group, an organization and a system actor builds with warnings as errors.
  - Membership checks in the generated interpreter, and the resolver for an actor from the object, each built and tested.
  - Generated scenario tests with personas pass.
  - `process-docs` pages for members, actors from the object and personas.
  - The TypeScript sample's type values and membership check.
- **Editor:** unit tests for the folders, members and cycle stop; browser tests for the actor editor's Member of and Add members, the Owner picker, and the scenario editor's personas.
- **Acceptance**, on an invented model:
  - Item and Document derive from a base with `project`. One lifecycle governs both, each subject naming its owner field.
  - Clerk is a member of ChangeBoard. SeniorReviewer is a member of Reviewer.
  - A scenario where the owner submits, a reviewer on the item's project approves, a reviewer on another project is refused, and a timer moves the item as the system.

Each stale-output test is checked to fail without its fix.

## 14. Tasks and the build plan

### Tasks

The sizes are days for a person who knows the code, with tests and a review. They show how the groups compare.

| Wave | Group | What | Size |
| --- | --- | --- | --- |
| 1 | Types and membership | `group`, `organization`, `system`; `memberOf`, closures, MQ9601, MQ9602, MQ9605, MQ9105 and MQ9106; simulation by membership; the system actor in traces; the index row; references and deletes | A day and a half |
| 1 | Output for types and membership | `csharp-dapper`'s enum and membership check, `process-docs`' actor page, the TypeScript sample | Half a day |
| 1 | Editor for types and membership | Folders, members in the tree, Member of, Add members, the type picker, the mock | A day |
| 2 | Actors from the object | `relative`, the subject's `owner`, MQ9603, MQ9604, MQ9606, path checks, references and deletes | A day and a half |
| 2 | Personas | `personas`, the step's `persona`, the object's `owner`, satisfaction, MQ9305 and MQ9301, simulate and record | A day and a half |
| 2 | Output for the rest | `csharp-dapper`'s resolver and scenario tests, `process-docs`' personas and owners | Half a day |
| 2 | Editor for the rest | From the object, the Owner picker, the scenario editor's personas, the simulation panel, personas in the explorer, the mock | A day |
| 3 | Docs | User guide, processes guide, releases, modeling skill, `next.md` | Half a day |

That comes to about eight days for a person.

**Checklist for every group.** Anything that reads or writes an actor, a subject entry or a scenario must be found and handled:
- **Readers of actor types:** the index row's switch (`Model/ModelIndexer.cs`), `ResolutionValues.Kebab`, MQ9101 (`Validation/ProcessRules.cs`), `csharp-dapper/actors.scriban`, the editor's `ACTOR_TYPE_LABELS` and folder list (`explorer/tree.ts`), the actor editor and new-actor dialog, and the mock.
- **Actor checks in simulation:** `Processes/StatechartInterpreter.cs` (events, human tasks, signers and required actors), `Processes/ScenarioReplayer.cs`, `Processes/StepTrace.cs`.
- **Scenario parts:** `Loading/NameIndex.cs`, `Loading/ChangePlanner.Cascade.cs`, `Processes/ScenarioObjects.cs`, `Editor/ResolvedRecords.cs`, as 4.1 did for objects.
- **Contracts:** the schema copies, `openapi.yaml` and its generated files.
- **Test harness:** `RenderKit.WithSettings` and `ResolvedCoverageTests`.

### How it gets built

**What 4.1 cost.** About 3.75 hours, 1.71M output tokens, 4.67M new input and 436M cached re-reads, for about 11,000 lines. Engine builders cost about 20M cached per 100 steps.

**This item's size.** About 6,500 to 7,500 lines with tests: engine about 2,300, its tests about 1,800, packs about 500 with expected files, editor and mock about 2,000, docs about 500. That is about 60 to 65% of 4.1. It has no SQL dialects and no binding rework, so fewer slow test loops.

**Agents.** Each is capped at about 100 steps or 200k context. Opus 5.5 builds the engine and pack output and runs every review. Sonnet 5.5 builds the editor, the mock and the docs.

| Wave | Agent | Model | Steps | Starts from |
| --- | --- | --- | --- | --- |
| 1 | Types, membership and the system actor in the engine; **contract first**: `actor.json`, `openapi.yaml`, rule texts | Opus | About 100 | main |
| 1 | Editor and mock for wave 1 | Sonnet | About 90 | The contract commit |
| 1 | Output for wave 1 | Opus | About 60 | The engine agent's finish |
| 1 | Review of pull request 1 | Opus | About 50 | |
| 2 | Actors from the object in the engine; **contract first**: `actor.json`'s `relative`, `process.json`'s `owner`, `scenario.json`'s personas, `openapi.yaml`, rule texts | Opus | About 100 | Pull request 1 |
| 2 | Personas and satisfaction in the simulator and tools | Opus | About 100 | The contract commit |
| 2 | Editor and mock for wave 2 | Sonnet | About 100 | The contract commit |
| 2 | Output for wave 2 | Opus | About 60 | Both engine agents' finish |
| 2 | Review of pull request 2 | Opus | About 60 | |
| 3 | Docs | Sonnet | About 60 | Pull request 2 |

- **At most three agents run at once.** In wave 2 the two engine agents share only the rule catalog and the resolved scenario types. The contract commit writes both, so neither edits the other's files.
- **Pull requests:** 1 on main (types and membership); 2 on 1 (actors from the object, personas and the docs commit). The owner merges two pull requests in order.
- **Generated files** (the editor's API types, the recorded rule list, the sample app's copies) are regenerated by fixed commands after each merge. Pack expected files stay with the builder, so their changes are reviewed.
- **Fixers** are fresh agents with the exact findings, about 40 steps each; two are allowed for.
- **A design gap found while building** stops the work, and the owner is asked. Builders don't decide scope.

**Estimate.**

| | Wall time | Output | New input | Cached re-reads |
| --- | --- | --- | --- | --- |
| Wave 1 (pull request 1, with review, fixes and CI) | About 1.25 hours | 0.45M | 1.1M | 105M |
| Wave 2 (pull request 2, with review, fixes and CI) | About 1.25 hours | 0.5M | 1.3M | 120M |
| Wave 3 (docs, and CI) | About 0.5 hours | 0.1M | 0.2M | 15M |
| In all | **About 3 hours** | **About 1.05M** | **About 2.6M** | **About 240M** |

The cached figure is about 160M for the agents (roughly 20M per 100 Opus steps, 15M per 100 Sonnet steps, two fixers included) and about 80M for coordinating: launching, merging, regenerating, opening pull requests and waiting on CI. A stereotype as a set of types, built apart (decision 19), adds about 1 hour, 0.3M output, 0.8M new input and 70M cached.
