# Who, what and when: actors, verbs, rights and scenarios across lifecycles

**Status (2026-11-05): a direction for the owner, not a design to build from.** It shows how actors and scenarios tie lifecycles, states, rights, signatures and triggers together. Each part becomes an item with its own design. [What lifecycle systems can do](lifecycle-capabilities.md) is the yardstick, and [the design of 4.1](lifecycle-design.md) is the first item. The shapes here are sketches, and names and keys are not settled. An independent check of the first version shaped this one; section 13 says what changed.

## 1. Why this matters

The owner wants one model to serve four uses:

| Use | What the model must give |
| --- | --- |
| **A manual model**, to understand and agree on how things work | A readable account of who does what, when, and why something is allowed or refused |
| **Requirements for building an external system** | Statements precise enough to build from, and acceptance criteria to test against |
| **Requirements for implementing in a lifecycle system** | What to configure (lifecycles, states, rights, signatures, triggers), and a test plan that proves the configuration |
| **The system that generates the code** for an enterprise system or SaaS | All of the above, in a form packs can generate from, with generated tests |

All four depend on the same three questions, asked of every part of a lifecycle: **who** may act, **what** they may do, and **when** (in which state, under which condition). They also need scenarios that prove the answers. Today each feature answers these its own way, or not at all.

## 2. Today

- **Actors** are a person, a role or an external system. Nothing says a person holds a role, there is no "the object's owner", and there is no "a reviewer on this project".
- **Who may act** is stated in four separate places, each with a plain list of actors:
  - an event's `actors`;
  - a gate's `signers` and `requiredActors`;
  - a human task's `actors`;
  - an operation's `requires`, which the engine gives no meaning.
- **Nothing can set the object's data.** A process's actions change its context, not the object, and an operation has outcomes but no effects. So nothing can record who submitted something, which "the approver is not the submitter" needs.
- **Nothing can read another lifecycle's state** unless that state happens to sit in a column.
- **Scenarios run one process.** A step is an event, time passing, or an invoke finishing. The signer is free text. A step can expect a refusal, but not why. There are no other operations, no data changes and no second object.
- **Rights per state, rules, freezing and trigger points** don't exist.

## 3. The idea: one vocabulary of who, what and when

| Question | One concept | Used by |
| --- | --- | --- |
| Who | **Actors**: people, roles, groups and external systems, with membership; actors relative to the object, such as its owner; roles held within a context, such as a project's reviewer; and the system itself | Moves, signatures, tasks, rights, triggers, scenarios |
| What | **Verbs**: what can be done to an object. A built-in set with fixed meanings, and the project's own | Moves, operations, rights, triggers, scenarios |
| When | **States and conditions**: the object's state in each of its lifecycles, and conditions over the object's data and the acting actor | Rights, signatures, triggers, guards |

Each feature then says the same thing in the same terms:
- **A right:** who may do which verbs, in which states, under which condition, allowed or denied, and why.
- **A signature:** who may sign this move, under which condition.
- **A trigger:** what runs when a verb happens here, as check, override or action.
- **A scenario step:** this persona does this verb to this object, and expects this outcome, for this reason.

## 4. Who: actors

1. **Membership.** Any actor may be a member of others, so a person holds roles, and roles nest. A right given to a role reaches its members. This is the most basic thing missing. Groups and organizations can be roles at first (question 1).
2. **Relative actors.** An actor defined from the object, such as its owner, its creator, the person in an attribute, or the requester's manager through a to-one path. The owner comes first: a subject entry in 4.1 keeps the key `owner` for it, naming the attribute or to-one end that holds the object's owner.
3. **Roles within a context.** A role that holds only within a context object, such as "reviewer on this project", reached along a to-one path from the object. This is how a lifecycle system's team roles and security contexts are stated.
4. **The system.** Automatic moves, timers and deferred triggers act as the system, so rights and history can say so.
5. **Separation of duties.** "No one holds both of these roles on one object", and "the approver is not the submitter". The second needs the object to record who submitted, which comes in section 6.
6. **A set of types named by a stereotype.** "Every entity carrying this stereotype" as one way to name several types at once. Lifecycle subjects need it, reusable rights over shared attributes need it, and so do relations with several types per end. Decide it once, in this item.

## 5. What: verbs

1. **Built-in verbs with fixed meaning, plus the project's own.**
   - The built-in set is the lifecycle's own (promote, demote, override, sign, reject, ignore, delegate, withdraw) and an object's (create, read, modify, delete, copy, connect, disconnect, freeze, thaw, lock, unlock, revise, and change owner, type or lifecycle).
   - A project can hide built-in verbs it doesn't use, but can't change what they mean, so a pack and a lifecycle system know how to implement each one.
   - A project's own verbs are stubs a pack or implementer fills in.

   Nothing is hard-coded beyond the built-in set. A lifecycle system with hundreds of trigger names combines verbs, places and kinds into that grid.
2. **Verbs are referenced like tag vocabulary keys.** Renaming one rewrites its users.
3. **Moves and operations name their verb.** A lifecycle's event says which verb it is (promote, demote, or the project's own). An operation element, a command on an entity with inputs and outcomes, names its verb too. "Verb" is a new word on purpose: "operation" already names that element, and using it again for the vocabulary would give one word two meanings.

## 6. Two prerequisites: setting the object's data, and reading another lifecycle's state

1. **A move or an action may set the object's attributes,** such as `submittedBy` set to the acting actor, or `releasedOn` set to now. Without this, the object's owner, its submitter and separation of duties have nothing to stand on. Packs and implementers get a plain statement of what each move records.
2. **A condition may ask an object's state in any of its lifecycles.** For example, "the employee is Active in HR". This needs a predicate that works however the state is stored, which the simulator evaluates and packs render. Without it, the employee in two lifecycles can't say how they relate, unless one state happens to sit in a column.

## 7. When: rights, with rules and freezing folded in

1. **One concept, a right.**
   - A right gives actors, verbs, states and an optional condition, allows or denies, and carries a sentence a person can read: why it holds.
   - A named right can be reused, which covers what lifecycle systems call rules. "Forbid modify, delete and change owner when the item is classified secret, unless the actor is its owner" is one named right with a condition.
   - Calling this "rule" would clash with the model's validation rules and declared rules, which check the model rather than the running system.
2. **Defaults per lifecycle.** Each lifecycle says whether a verb nothing mentions is allowed or denied (allowed for compatibility). A warning flags allow-rights that change nothing under "allowed".
3. **Combining.** Each lifecycle or right answers allow, deny or nothing. Any deny wins, then any allow, then the default. A project lifecycle silent about changing a salary leaves the HR lifecycle to decide. This is the settled way of combining in the attribute-based access control standard.
4. **Who may is said once.**
   - An event's `actors` becomes shorthand for a right to its verb in all its source states, and explicit rights narrow it.
   - An operation's `requires` becomes a right with no state.
   - A gate's signers follow the same pattern. Today MQ9105 patches the overlap between signers and an event's actors.
5. **Freezing is two built-in verbs and a fixed meaning.** Freeze and thaw are verbs. While an item is frozen, modify, connect, disconnect and delete are denied. Rights and triggers then apply to freezing with nothing extra.
6. **Finer rights for later in the same item:** rights on one attribute in one state (hide a salary from everyone outside HR), and rights passed along containment (a project's rights reach its items).

## 8. Bringing it together

| Feature | Who | What | When |
| --- | --- | --- | --- |
| A move (event) | Actors who may cause it (shorthand for a right) | Its verb, such as promote | From its source states, if its guard holds |
| A signature | Who may approve, reject or ignore; who it is routed to; who may stand in | sign, reject, ignore, withdraw, delegate | On a move, if its condition holds |
| A task (human-task invoke) | Assignees, possibly relative or within a context | complete, claim, delegate | While its state is active |
| A right, plain or named | Actors | Verbs, allowed or denied, with a reason | In states, under a condition |
| A trigger | Runs as the actor or as the system | A verb, as check, override or action | At a place (lifecycle, state, move, signature or type), under a condition |
| A scenario step | A persona | A verb or an event, on a named object | Given the objects' states and data, with an expected outcome and reason |

## 9. Scenarios: the proof, for all four uses

1. **A cast.** A scenario names its objects, each with an id, an entity, values and links to the others through relation ends. 4.1 starts this. It also names its personas, each a person with the roles they hold and where they hold them, such as "Ann is reviewer on project P".
   - Personas live in the scenario, not in the model's actors, so they never reach generated production code (`csharp-dapper` writes a constant per actor).
2. **Steps for any verb.** A step can be:
   - an event that causes a move;
   - a signature (sign, reject, ignore, withdraw, delegate);
   - any verb, such as modify a value, connect two objects through a relation, or freeze;
   - time passing, or a task finishing.

   Each step names the object it acts on and the persona acting.
3. **Expectations, with reasons.**
   - **Accepted or refused, and by what:** the element that refused it, such as a right, a guard or a gate, not just a category. Checks run in a fixed order (frozen, then rights, then guards, then signatures), so two scenarios can't disagree about which reason applies.
   - **What happened to an event:** refused, ignored or deferred. The state machine standard distinguishes these, and requirements need to.
   - **Afterwards:** each object's states and values, the triggers that ran, and the tasks and signatures now waiting.
4. **Across lifecycles and objects.** One scenario follows an employee through HR onboarding while a project assignment linked to them goes through its own lifecycle. Once steps name their objects, a scenario's single `process` becomes optional.
5. **What each use gets from one scenario.**
   - A manual model: a readable story, in the docs pack's "given, when, then" form.
   - A requirements model: acceptance criteria.
   - A lifecycle system: a test plan to verify the configuration.
   - The generating system: generated tests, and later runs against the live system (8.1).
6. **Coverage.** A report of which states, moves, signatures, rights and triggers no scenario exercises.
7. **Traceability.** A scenario names the requirements it proves, through the project's own requirement kind (3.10).

## 10. Order

1. **4.1, lifecycles any implementation can follow** (designed): codes, subjects, guard conditions that read the object, and a scenario's objects.
2. **Actors:** membership, relative actors starting with the owner, roles within a context, the system, and a stereotype as a set of types.
3. **Verbs:** the built-in set and the project's own, with events and operations naming theirs.
4. **Setting the object's data, and reading another lifecycle's state** (section 6). These are prerequisites for rights.
5. **Rights,** with named rights, defaults, combining, freezing and refusal reasons in scenarios.
6. **Trigger points,** on every verb, as check, override and action, with the generated step methods.
7. **Richer sign-off:** several named signatures, conditions on who must sign, delegation, escalation, separation of duties across steps. Approval stages (4.3) come with it.

Rights come before trigger points. They serve three of the four uses directly, the simulator can check them, and scenarios can prove refusals. Trigger points matter once a pack or a lifecycle system uses them. Scenarios grow with each item. **Relations with several types** can run alongside once the stereotype question in item 2 is settled and their drawing is agreed (section 11).

## 11. Relations with several types at an end

- **In the model:** an end lists several entities, all of them, or every entity carrying a stereotype (item 2).
- **In code generation:** a pack chooses the storage, such as a type and id pair, a column per type, or a table per type. One foreign key can't point at several tables, so bindings need their own design.
- **In diagrams:**

  | Option | How it looks | Good for |
  | --- | --- | --- |
  | A junction node with one line to each type | Like an association with more than two ends | Three or more types |
  | One line per type, bundled under one label | Close to today's look | Two types |
  | One line to a box listing the types | Compact | Many types, or "all" |
  | One line to a shared base entity or stereotype | Simplest | Types that share one |

  Recommended: a junction node by default, bundled lines for two, and a toggle to collapse to one line and a list. Each line shows its end's role and cardinality.

## 12. What stays out of the model

The model states what is allowed, required and expected. Running it belongs to the team's system or a lifecycle system: who is signed in, delivering notifications, scheduling, storing history and enforcing access. Simulation and scenario replay act these out to check the model, and packs generate them for teams that want code.

## 13. What the independent check changed

- **Two words.** "Operations" became **verbs**, because an operation element already exists. Rules became **named rights**, because rules already mean validation.
- **Defaults and combining.** The default became per lifecycle, with three-valued combining in place of "both must allow".
- **Prerequisites.** Setting the object's data and reading another lifecycle's state got their own item, before rights.
- **Order.** Rights now come before trigger points.
- **Scenarios.** The cast gained ids, links and an explicit subject. Personas moved into scenarios. Refusals name the element that refused, in a fixed order, and events can be refused, ignored or deferred.
- **Who may is said once.** Events' `actors` and operations' `requires` become shorthand for rights.
- **Missing pieces.** Separation of duties, rights per attribute and along containment, and membership validity were added, along with what the capability list lacked from standards (role-based and attribute-based access control, the state machine standard, human-task and case-management models).

## 14. Questions for the owner

1. **Groups and organizations:** new actor types, or roles with membership? Recommended: roles with membership first.
2. **Relative actors:** which come first? Recommended: the owner and an actor named in an attribute, then to-one paths.
3. **Combining rights across lifecycles:** recommended: any deny wins, then any allow, then the lifecycle's default (section 7).
4. **The default when nothing is said:** recommended: per lifecycle, allowed unless the lifecycle says otherwise.
5. **The order in section 10.**
