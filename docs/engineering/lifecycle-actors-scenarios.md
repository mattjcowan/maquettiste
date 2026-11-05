# Who, what and when: actors, operations and scenarios across lifecycles

**Status (2026-11-05): a direction for the owner, not a design to build from.** It shows how actors and scenarios should tie lifecycles, states, rights, signatures and triggers together. Each part becomes an item with its own design. [What lifecycle systems can do](lifecycle-capabilities.md) is the yardstick, and [the design of 4.1](lifecycle-design.md) is the first item. The shapes here are sketches: names and keys are not settled.

## 1. Why this matters

The owner wants one model to serve four uses:

| Use | What the model must give |
| --- | --- |
| **A manual model**, to understand and agree on how things work | A readable account of who does what, when, and why it is allowed or refused |
| **Requirements for building an external system** | Statements precise enough to build from, and acceptance criteria to test against |
| **Requirements for implementing in a lifecycle system** | What to configure: lifecycles, states, rights, signatures, rules, triggers. Also a test plan to prove the configuration |
| **The system that generates the code** for an enterprise system or SaaS | All of the above, structured so packs can generate it, plus generated tests |

All four depend on the same three questions, asked of every part of a lifecycle: **who** may act, **what** they may do, and **when**, meaning in which state and under which condition. They also depend on scenarios that prove the answers. Today each feature answers these its own way, or doesn't answer them at all.

## 2. Today

- **Actors** are a person, a role or an external system. Nothing says a person holds a role, or a role belongs to a group. There is no "the object's owner" or "a reviewer on this project".
- **Who may act** is stated in four separate places, each with a plain list of actors:
  - an event's `actors` (who may cause a move);
  - a gate's `signers` and `requiredActors`;
  - a human task's `actors`;
  - an operation's `requires`, which the engine gives no meaning.
- **Scenarios** run one process.
  - A step is an event, time passing, or an invoke finishing or failing.
  - The step names its actor, but the signer is free text.
  - A step can expect to be refused, but not why.
  - There are no other operations, no data changes, and no second object.
- **Rights per state, conditional rules, freezing and trigger points** don't exist. The capability list shows how much lifecycle systems offer here.

## 3. The idea: one vocabulary of who, what and when

| Question | One concept | Used by |
| --- | --- | --- |
| Who | **Actors**: people, roles, groups, external systems and the system itself, plus actors relative to the object (its owner) and roles held within a context (a project's reviewer) | Events, signatures, tasks, rights, rules, triggers, scenarios |
| What | **Operations**: an open vocabulary of operation names. It includes the lifecycle's own moves and sign-off, an object's operations (create, modify, delete, connect, freeze, lock, revise and more), and the project's own operations | Rights, rules, triggers, scenarios |
| When | **States and conditions**: the object's state in each of its lifecycles, and expressions over the object and the acting actor | Rights, rules, signatures, triggers, guards |

Every feature then says the same thing in the same terms. A signature is "who may sign this move, under which condition". A right is "who may do which operations in this state, under which condition". A trigger is "what runs when this operation happens here, as check, override or action". A scenario step is "this person does this operation on this object, and expects this outcome, for this reason".

## 4. Who: actors

1. **Membership.** Any actor may be a member of others, so a person holds roles, and roles and groups nest. A right given to a role reaches its members. This is the most basic thing missing today.
2. **Groups and organizations.** These may be new actor types, or just roles. Question 1 in section 12.
3. **Relative actors.** An actor defined by a path from the object, such as its owner, its creator, the assignee in an attribute, or the requester's manager. Rules ("unless you own it"), routing ("to the requester's manager") and ownership rights all need these.
4. **Roles within a context.** A role that holds only within a context object, such as "reviewer on this project" or "approver in this organization", together with how the context is reached from the object. This is how a lifecycle system's team roles or security contexts are stated.
5. **The system.** Automatic moves, timers and deferred triggers act as the system, so rights and history can say so.
6. **Personas.** Person actors that hold roles become the cast of scenarios, such as "Ann, finance controller". Signers in scenarios become personas instead of free text.

## 5. What: operations

1. **An open vocabulary, not a fixed list.** The project owns the list of operation names, as it owns its tag vocabularies. Maquettiste ships a starter set, which a project extends or narrows:
   - the lifecycle's own: promote, demote, override, sign, reject, ignore, withdraw, enable, disable;
   - an object's: create, read, modify, delete, copy, connect, disconnect, freeze, thaw, lock, unlock, revise, and change owner, type or lifecycle.

   This answers the owner's question: nothing is hard-coded. A lifecycle system with hundreds of trigger names has them because they combine operations, places and kinds, and that grid is built from this vocabulary.
2. **A lifecycle's events are operations.** A move is caused by an event, and each event can say which operation it is (promote, demote, or the project's own), so rights and triggers can name it either way.
3. **Operation elements fit in.** Maquettiste already has operations: commands on an entity with inputs, outcomes and errors, which fire lifecycle events and list what they `require`. An operation names which vocabulary entry it is. Its `requires` becomes a real right, stated with the right's terms in section 6.

## 6. When: states and conditions

1. **Rights per state.** For each state, which actors may do which operations, allowed or denied. There is a default for every state, and separate entries for the owner, for everyone, and for named actors. An object in two lifecycles is governed by both, and question 3 in section 12 asks how they combine.
2. **Rules.** Named, reusable rules that allow or forbid operations when a condition holds, over the object's data and the acting actor. For example: forbid modify, delete and change owner when the item is classified secret and the actor is not its owner. They apply to types, states or the whole model.
3. **Freezing.** A frozen item, whether an object or a relation's instance, refuses changes until it is thawed. Freezing and thawing are operations, so rights and triggers apply to them too.
4. **The acting actor in expressions.** Conditions in guards, signature filters, rules and triggers can read `actor` and test membership, such as whether the actor is a member of Finance or is the object's owner.

## 7. Bringing it together

| Feature | Who | What | When |
| --- | --- | --- | --- |
| A move (event) | Actors who may cause it | Its operation, such as promote | From its source states, if its guard holds |
| A signature | Who may approve, reject or ignore; who it is routed to; delegation | sign, reject, ignore, withdraw | On a move, if its filter condition holds |
| A task (human-task invoke) | Its assignees, which may be relative or context roles | complete, reassign | While its state is active |
| A right | An actor, possibly relative or within a context | Operations, allowed or denied | In states, under a condition |
| A rule | A condition over the actor | Operations, allowed or forbidden | Under a condition over the object |
| A trigger | Runs as the actor, or as the system | An operation, as check, override or action | At a place (a lifecycle, state, move or signature, or a type), under a condition |
| A scenario step | A persona | An operation or an event | Given the objects' states and data, with an expected outcome and reason |

## 8. Scenarios: the proof, for all four uses

1. **A cast.** A scenario names its objects (an employee, a project and an assignment, each an entity with values) and its personas. Today a scenario runs one process for an unnamed object.
2. **Steps for any operation.** A step can be:
   - an event that causes a move;
   - a signature (sign, reject, ignore or withdraw);
   - any operation from the vocabulary (modify a value, connect two objects, freeze);
   - time passing, or a task finishing.

   Each step names the object it acts on and the persona acting.
3. **Expectations, with reasons.** A step expects to be accepted or refused, and **why**:
   - no right, or a rule forbade it;
   - a guard failed, or a signature is still missing;
   - the item is frozen.

   It also expects each object's states and values afterwards, the triggers that ran, and the tasks and signatures now waiting.
4. **Across lifecycles and objects.** One scenario can follow an employee through HR onboarding while a project assignment goes through its own lifecycle, because the steps name their objects.
5. **What each use gets from the same scenario.**
   - A manual model gets a readable story, in the docs pack's "given, when, then" form.
   - A requirements model gets acceptance criteria.
   - A lifecycle system gets a test plan to verify the configuration by hand or by its own tools.
   - The generating system gets generated tests, and later runs against the live system (8.1).
6. **Coverage.** A report of which states, moves, signatures, rights, rules and triggers no scenario exercises. It works the same for every use.
7. **Traceability.** A scenario can name the requirements it proves, through the project's own requirement kind (3.10).

**What this means for 4.1 now.** The first draft of 4.1 gave scenarios `start.subject` for one object. To avoid two ways of saying the same thing later, 4.1 should start the cast instead: a scenario's `objects`, each with a name, an entity and values, with the process running for the first one. The 4.1 design is updated to say so.

## 9. Relations with several types at an end

The owner wants these, and they open many modeling options. They also raise a question 4.1 does not: how to draw them.

- **In the model**, an end lists several entities, all entities, or every entity that carries a stereotype. Rights, rules and triggers on relation operations (connect, disconnect, freeze) then apply per relation, whatever the types.
- **In code generation**, a pack chooses the storage, such as a type-and-id pair, one column per type, or one table per type. The model states only the intent, as for every other relation.
- **In diagrams**, there are four options:

  | Option | How it looks | Good for |
  | --- | --- | --- |
  | A small junction node with one line to each allowed type | Like an association with more than two ends | Three or more types |
  | One line per type, bundled with one label | Close to how a relation looks today | Two types |
  | One line to a box listing the types | Compact | Many types, or "all" |
  | One line to a common base entity or stereotype | Simplest | Types that share one |

  Recommended: a junction node by default, bundled lines for two types, and a toggle to collapse the junction into one line to a list. Each line shows its end's role and cardinality.
- **Bindings** to tables need thought: one foreign key cannot point at several tables. That belongs to the relations item's own design.

## 10. Order

These build on one another:

1. **4.1, lifecycles any implementation can follow** (designed): codes, subjects, guards that read the object, and a scenario's cast.
2. **Actors: membership, groups, relative actors, roles within a context, personas.** The foundation for the rest.
3. **The operation vocabulary**, with events and operation elements naming their entries.
4. **Trigger points** on every operation, as check, override and action.
5. **Rights per state, rules and freezing.**
6. **Richer sign-off**, which takes approval stages (4.3) with it.
7. **Scenarios** grow with each item: personas with 2, operation steps with 3, expected triggers with 4, refusal reasons with 5, signature steps with 6. Coverage can come at any point.

**Relations with several types** can run alongside any of these, once their diagram is agreed.

## 11. What stays out of the model

The model states what is allowed, required and expected. Running it belongs to the team's system or a lifecycle system: who is signed in, delivering notifications, scheduling, storing history, enforcing access. Simulation and scenario replay act these out to check the model, and packs generate them for teams that want code.

## 12. Questions for the owner

1. **Groups and organizations:** new actor types, or roles with membership? Recommended: roles with membership first, since membership is what all of these need, and add types only if a team needs to tell them apart.
2. **Relative actors:** which come first? Recommended: the object's owner and an actor named in an attribute. Paths through relations come later.
3. **Rights in two lifecycles at once:** when an object follows two lifecycles and their rights disagree, does a deny win, or must both allow? Recommended: both must allow, and any deny wins. That is safe, and easy to explain.
4. **When nothing says:** is an operation allowed or denied? Recommended: allowed, so a model with no rights keeps working, and a project can switch it to denied.
5. **The order in section 10:** is this right, and should relations with several types come sooner?
