# Verbs: design

**Status (2026-11-06): built for 0.14.0 (#51).** It designs the third item in the owner's order after 4.1: a built-in set of verbs with fixed meanings, the project's own verbs, and events, automatic moves and operations naming theirs. [Who, what and when](lifecycle-actors-scenarios.md) section 5 sets its direction. [What lifecycle systems can do](lifecycle-capabilities.md) is the yardstick. [The actors design](actors-design.md) gives its shape. A file path without a project prefix is under `src/Maquettiste.Engine/`. The examples are invented. Each new name and format is held to the roadmap's test: it stays for good, it must help any team whether they use it or not, and it stays optional.

## 1. In short

Today nothing says what an event or an operation does. An event has a free name, such as `Submit` or `Approve`. An operation has an effect, read or change, and nothing more. Rights and trigger points need one shared word for "what is done": who may promote, and what runs when something is frozen.

| Layer | What changes |
| --- | --- |
| The model | A fixed set of 27 **built-in verbs** in five groups: moves, signatures, tasks, objects and links. Each has one meaning that never changes. A project adds **its own verbs** in one file, `model/vocabularies/verbs.json`, and may **hide** built-in verbs it doesn't use. An **event** names its verb. A **transition without an event** names the verb of its automatic move. An **operation** names its verb. A verb is written as its key, as a stereotype is. Renaming one of the project's verbs rewrites every use |
| Code generation | Templates read `model.verbs` and the verb of each event, transition and operation. `csharp-dapper` writes a `Verb` enum and each event's and operation's verb. `process-docs` adds a Verb column and a page of verbs. Each happens only when the model uses verbs |
| The team's system, or a lifecycle system | Nothing changes until the team uses the new output or builds from the model |

Verbs carry no behaviour in this item. Who may do a verb comes with rights. What runs when it happens comes with trigger points. This item gives both a fixed list of names to build on.

**A project that uses none of this** keeps its files, output, findings and tool answers. Section 10 lists everything.

## 2. Scope

| | What | Section |
| --- | --- | --- |
| **This item** | The built-in verbs and their meanings; the verb vocabulary file; `verb` on events, on transitions without an event and on operations; rules; renaming and removing a verb; the resolved model, packs and the editor | 3 to 10 |
| **Fixed meanings decided here, used later** | Every occurrence of a gated event is a signature; what freezing stops | Decisions 15 and 16 |
| **Left to rights** | Who may do a verb, in which states; enforcing freezing; whether freezing stops the project's own verbs; an event's `actors` and an operation's `requires` as shorthand rights | 11 |
| **Left to trigger points** | Places, the check, override and action kinds, and generated step methods per verb | 11 |
| **Left to richer sign-off** | Reject, ignore, withdraw, delegate and route on a gate | 11 |
| **Left to scenarios** | Steps that do any verb to any object | 11 |

## 3. Decisions

Each decision gives its confidence: **high**, **medium** or **low**.

### The built-in verbs

1. **27 built-in verbs in five groups, each with one fixed meaning.** Confidence: high for the shape and for 23 of the verbs; medium-high for four (`set-state`, `route`, `claim`, `complete`). The set is owner question 1.

   | Group | Key | Display name | Meaning |
   | --- | --- | --- | --- |
   | Moves | `promote` | Promote | Move the object forward to a next state of its lifecycle |
   | | `demote` | Demote | Move the object back to an earlier state of its lifecycle |
   | | `override` | Override | Make a move although its conditions or signatures are not met |
   | | `set-state` | Set state | Put the object in a chosen state directly, without a declared move |
   | Signatures | `sign` | Sign | Give a signature that a move needs |
   | | `reject` | Reject | Refuse a signature, so the move does not happen |
   | | `ignore` | Ignore | Waive a signature, so the move no longer waits for it |
   | | `withdraw` | Withdraw | Take back a signature already given |
   | | `delegate` | Delegate | Hand a signature or a task to another actor |
   | | `route` | Route | Send the object to actors for review, signature or a task |
   | Tasks | `claim` | Claim | Take a task offered to several actors, so only you hold it |
   | | `complete` | Complete | Finish a task |
   | Objects | `create` | Create | Make a new object |
   | | `read` | Read | See an object or a link, and its data |
   | | `modify` | Modify | Change the values of an object or a link |
   | | `delete` | Delete | Remove an object |
   | | `copy` | Copy | Make a new object from an existing one |
   | | `revise` | Revise | Make the next revision of an object |
   | | `lock` | Lock | Reserve an object, so only the actor holding the lock may change it |
   | | `unlock` | Unlock | Release a lock |
   | | `change-owner` | Change owner | Give an object another owner |
   | | `change-type` | Change type | Make an object an instance of another type |
   | | `change-lifecycle` | Change lifecycle | Put an object under another lifecycle |
   | | `freeze` | Freeze | Make an object or a link read-only until it is thawed. While frozen it can't be modified, connected, disconnected or deleted (decision 16) |
   | | `thaw` | Thaw | Undo a freeze |
   | Links | `connect` | Connect | Link two objects through a relation |
   | | `disconnect` | Disconnect | Remove a link between two objects |

   - **Why these:** each appears in several systems or standards in the yardstick (parts B to E and G of the capabilities list). Each means the same thing in all of them, so a pack or a lifecycle system knows how to implement it.
   - **Left to projects:** check-out and check-in (their meanings vary too much between systems), schedule, enable, disable, grant, revoke, suspend, resume, reassign, start, stop, cancel, archive, publish and notify. Their meaning differs between systems, or they belong to the running system. A project adds any of them as its own (decision 7). In the editor, "Add a verb…" offers them as suggestions (decision 7).
   - **Why "verb":** "operation" already names an element kind. Using it for this list would give one word two meanings (the direction, section 13).
2. **A built-in verb's meaning never changes, and a project can't redefine it.** Confidence: high. A project may hide it (decision 8). The meanings live in one table in the engine, `Model/BuiltInVerbs.cs`. The schema, the editor's list and the user guide are copies made from it, and a test checks they agree.
3. **Keys are short kebab-case English words.** Confidence: high that they are kebab-case, like stereotype keys; medium-high for the exact spellings (question 1). Display names are fixed labels, such as "Change owner".
4. **A group says what a verb acts on.** Confidence: high.
   - **Moves:** the object's state in a lifecycle.
   - **Signatures:** a signature a move needs.
   - **Tasks:** a human task.
   - **Objects:** an object. `read`, `modify`, `freeze` and `thaw` also act on a link.
   - **Links:** a link between two objects through a relation.

   Groups order the editor's pickers and the docs. They don't limit where a verb may be named (decision 14).
5. **Built-in verbs added later.** Confidence: high. A later release may add a built-in verb. If a project already has its own verb with that key, MQ9702 asks it to choose. `rename-verb` to the same key adopts the built-in in one step (decision 9). The built-in set grows only when a later item gives the new verb behaviour. The release notes then name the quick fix (`rename-verb` to the same key) for a project whose own verb has that key.

### The project's own verbs

6. **One verb vocabulary per project: `model/vocabularies/verbs.json`, kind `verb-vocabulary`.** Confidence: high for the file; medium-high for "global only".
   - Like `tags.json`, it has a fixed file name. It is created with the first own verb or the first hidden built-in.
   - **Global only in this item.** A verb is a project-wide meaning, and a pack writes one enum. An optional `package` key could add a domain's own verbs later without changing any file, if teams ask.
7. **An own verb has an id, a key, a display name and a description.** Confidence: medium-high.
   - **The key** is how every use names it.
   - **The id** makes it a sub-element, as a category is. The index records each use of its key as a reference to that id, as it does for stereotype keys. So `get_references` lists its uses, deleting the vocabulary follows the usual choices, and its display name and description are localizable.
   - **The description** is the meaning a pack or an implementer fills in. No rule asks for one.
   - **Built-ins can be suggested.** In the editor, "Add a verb…" offers the verbs left to projects (decision 1) as suggestions. A suggestion only pre-fills an own verb's key, display name and description. It changes no file format and fixes no name. Confidence: medium-high.
   - **Common fields:** `verbs.json` accepts the common element fields (`description`, tags and so on), as `tags.json` does.
   - **Not translated:** built-in display names and meanings are not translated in this item. Own verbs' display names and descriptions are localizable.
   - **Nothing else in this item:** no custom properties, no "refines a built-in", and no group. Each could be added later without changing a file (section 11).
8. **A project may hide built-in verbs, with `hidden`.** Confidence: high that hiding exists, as the direction says; medium-high for what it does.
   - The editor doesn't offer hidden verbs. They are left out of `model.verbs` and so out of generated enums.
   - Naming one anyway is a warning at the use (MQ9704). It then stays in `model.verbs`, so the use still resolves.
9. **Renaming and removing go through two batch operations.** Confidence: medium-high.
   - **`rename-verb`** renames an own verb and rewrites every use, all or nothing. When `to` is another own verb or a built-in, the two merge: the uses move to it and the definition goes. It drops the removed verb's translations, as `retire-code` does, and takes `expectedHashes`, as `retag` and `rename-code` do. An own verb whose key is a built-in's (MQ9702) has no uses, since they name the built-in: renaming or removing it writes only the vocabulary, so adopting the built-in (`to` the same key) leaves every process and operation alone.
   - **`remove-verb`** removes an own verb and clears `verb` from every use. It takes `expectedHashes` and drops the verb's translations in the same way.
   - **A plain save** that changes the key of an own verb in use is refused (MQ9707), pointing to `rename-verb`, as a stereotype's key change is refused (MQ3020). An unused verb's key may change freely.
   - Built-in verbs can't be renamed or removed.
10. **A verb no one declares is an error at the use (MQ9701).** Confidence: high. A tag that isn't declared is only a note, but a verb will drive rights and triggers. A typo would make a verb that no right covers. The quick fix adds it to the project's verbs.

### Where verbs are named

11. **An event names its verb, with `verb`.** Confidence: high.
    - It says what the actor does by sending the event. `Submit` promotes, `SendBack` demotes, and `Escalate` does the project's own `escalate`.
    - Any process's events may name one: lifecycles and orchestrations. Libraries hold no events (MQ9020).
    - It is optional. An event with no verb means what it means today.
12. **A transition names a verb only when it has no event.** Confidence: medium-high.
    - An automatic move (`after`, `always`, `done`, `invoke-done`, `invoke-error`) has no event, but it is still a promotion or a demotion. Trigger points will run on it, so the transition says which.
    - A transition with an event takes its event's verb. A verb of its own there is an error (MQ9705). Rights are checked on the event before any guard picks a transition (the direction, section 9), so the verb must be known from the event alone.
    - **The alternative:** a verb on each transition, overriding its event's. One event could then promote from one state and demote from another, depending on a guard, and the right to check would depend on the guard. A team that needs this names two events, or uses its own verb, such as `decide`. It is not chosen.
13. **An operation names its verb, with `verb` after `effect`.** Confidence: high that operations name one; medium-high that nothing ties it to `effect`.
    - "Submit" on an order names `promote` and fires the lifecycle's `Submit` event. "Assign reviewer" names `connect`.
    - `effect` says whether the operation changes data. The verb says what is done. They can differ honestly: a read that counts each view changes data. So no rule ties them.
   - **The operation's verb is separate from the verbs of the events it `fires`.** "Submit" names `promote` for the operation, and the `Submit` event it fires has its own verb. Rights decide which of them are checked. This item fixes none.
14. **Any verb may be named in any of the three places.** Confidence: medium-high. A team may model an edit as an event that names `modify`, or a command as an operation that names `promote`. The editor offers the likely group first (section 9). One guideline, not a rule: a gated event names the move's verb, not `sign` (decision 15).

### Fixed meanings used by later items

15. **Every occurrence of a gated event that a gate counts is a signature, `sign`. The occurrence that completes the gate also makes the move, under the event's verb.** Confidence: medium-high. Today a gate collects signatures through its event's occurrences, counted per source and event (MQ9103). This keeps "who may sign" (the gate's signers) apart from "who may promote" (the event's actors), as the direction's section 7 needs. No key changes.
16. **Freezing's meaning is fixed now. Enforcing it comes with rights.** Confidence: medium-high. While an object or a link is frozen, it can't be modified, connected, disconnected or deleted, until it is thawed (the direction, section 7.5). Whether freezing also stops a project's own verbs is for the rights item. So are `change-owner`, `change-type`, `change-lifecycle` and `revise`, which freezing does not stop in this item.
17. **Relations name no verb in this item.** Confidence: high. `connect` and `disconnect` are reserved with fixed meanings. Rights and trigger points will place them on a relation. No relation file changes now.
18. **Scenario steps name no verb in this item.** Confidence: high. A step that sends an event already implies its verb. Steps that do any verb to any object come with rights, when refusals need reasons.

### Code generation

19. **`model.verbs` is empty unless the project uses verbs.** Confidence: high.
    - A project uses verbs when it has a verb vocabulary or names a verb anywhere.
    - Then `model.verbs` lists the built-in verbs that aren't hidden, in the table's order, then the project's own in file order, then any hidden verb that is still named.
20. **Packs write verbs only when the model uses them.** Confidence: high; medium-high that an enum lists every visible verb.
    - `csharp-dapper` writes a `Verb` enum of `model.verbs`, with each verb's key, which is what to store.
    - It writes an event's or an operation's verb only where one is named.
    - The enum lists every visible verb, not only the named ones. So it doesn't change as events change, and later rights and triggers can name any verb. Hiding is how a team trims it.
21. **Generated code doesn't act on verbs yet.** Confidence: high. The generated interpreter checks nothing new. Trigger points add step methods per verb (section 11).

### Release

22. **A minor release, and a team upgrades together.** Confidence: high, by the owner's 4.1 decision 6. An older engine refuses `verbs.json` and any file with `verb`. `formatVersion` stays 1.

## 4. Schema and files

| File | Key | Type | Notes |
| --- | --- | --- | --- |
| `verb-vocabulary.json` (new) | `kind`, `id`, `name`, `description`, `hidden`, `verbs` | `hidden`: a unique list of built-in keys. `verbs`: a list of `{ id, key, displayName?, description? }` | Decisions 6 to 8. Fixed path `model/vocabularies/verbs.json`, name `verbs` |
| `common.json` | `$defs/builtInVerb` (new) | `oneOf` of `{ const, description }`, one per built-in verb | Made from `Model/BuiltInVerbs.cs`. Gives tools and JSON editors the list with meanings |
| `common.json` | `$defs/verbRef` (new) | `anyOf`: `builtInVerb`, or a kebab-case key | Used by every `verb` |
| `process.json` | event `verb` (after `displayName`) | `verbRef` | Decision 11 |
| `process.json` | transition `verb` (after `trigger`) | `verbRef` | Decision 12; only without an event (MQ9705) |
| `operation.json` | `verb` (after `effect`) | `verbRef` | Decision 13 |
| `batch.json` | ops `rename-verb` and `remove-verb`; a new member `verb` (the key acted on), the existing `to`, and `expectedHashes` | | Decision 9 |

Also updated: the sample app's schema copies, `openapi.yaml` by hand (the index rows, the record shapes and the batch operations), and the editor files generated from it.

```json
{
  "$schema": "../../.schema/v1/verb-vocabulary.json",
  "kind": "verb-vocabulary",
  "id": "…",
  "name": "verbs",
  "hidden": ["lock", "unlock"],
  "verbs": [
    { "id": "…", "key": "escalate", "description": "Raise the request to the next level of support." },
    { "id": "…", "key": "quarantine", "displayName": "Put in quarantine", "description": "Set the batch aside until it is inspected." }
  ]
}
```

```json
"events": [
  { "id": "…", "name": "Submit", "verb": "promote", "actors": ["<id of Clerk>"] },
  { "id": "…", "name": "SendBack", "verb": "demote" },
  { "id": "…", "name": "Escalate", "verb": "escalate" }
],
"transitions": [
  { "id": "…", "source": "<id of Approved>", "trigger": "always", "verb": "promote", "targets": ["<id of Published>"] }
]
```

```json
{ "kind": "operation", "name": "AssignReviewer", "subject": "<id of Document>", "effect": "change", "verb": "connect", "outcomes": [ … ] }
```

```json
{ "op": "rename-verb", "verb": "escalate", "to": "raise" }
{ "op": "remove-verb", "verb": "quarantine" }
```

## 5. Rules

The next free family is MQ97xx.

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ9701 | E | A `verb` that is neither a built-in verb nor one of the project's own. Reported at the event, transition or operation | Add it to the project's verbs; clear it |
| MQ9702 | E | An own verb whose key is a built-in verb's, or two own verbs with one key, also once hyphens are dropped (`x-1` and `x1` would be one enum member in generated code) | Use the built-in (`rename-verb` to it) |
| MQ9703 | E | `hidden` names a key that isn't a built-in verb | Remove the entry |
| MQ9704 | W | A hidden built-in verb is named. Reported at the use | Show the verb again |
| MQ9705 | E | A transition triggered by an event names a verb of its own | Move it to the event, when the event names none; clear it |
| MQ9706 | W | An own verb's key equals a built-in key once hyphens are dropped, such as `setstate` beside `set-state` | Use the built-in |
| MQ9707 | E | A save changes the key of an own verb that is in use. Refused at save, like MQ3020 | (Use `rename-verb`) |

Duplicates in `hidden` and bad key shapes are schema errors. One existing rule changes: MQ1009 (more than one vocabulary of a kind) gains this case, a second verb vocabulary file, as it covers tags and categories (`Model/ModelIndexer.cs:219-234`). Its text names verbs. Nothing else changes. The mock backend's rule list follows.

## 6. Resolved model and templates

```csharp
public sealed class RVerb
{
    public string Key { get; internal set; } = "";            // "promote", "escalate"
    public string DisplayName { get; internal set; } = "";    // the fixed label, or the own verb's displayName (else from the key: "Escalate")
    public string Description { get; internal set; } = "";    // the fixed meaning, or the own verb's description ("" when none)
    public bool BuiltIn { get; internal set; }
    public string Group { get; internal set; } = "";          // "move", "signature", "task", "object", "link", or "project"
    public string? Id { get; internal set; }                  // the own verb's id; null for a built-in
    public bool Hidden { get; internal set; }                 // a hidden built-in that is still named
    public RList<REvent> Events { get; internal set; }        // events that name it, in model order
    public RList<RTransition> Transitions { get; internal set; } // transitions whose move has it, its own or its event's
    public RList<ROperation> Operations { get; internal set; }   // operations that name it
    public bool Used { get; internal set; }                   // named anywhere
}
public sealed class ResolvedModel { public RList<RVerb> Verbs { get; internal init; } }   // empty unless the project uses verbs (decision 19)
// REvent gains Verb; RTransition gains Verb (its own, else its event's); ROperation gains Verb. Each is an RVerb or null.
```

- **Templates read** `model.verbs`, `verb.key`, `verb.display_name`, `verb.description`, `verb.built_in`, `verb.group`, `verb.hidden`, `verb.used`, `verb.events`, `verb.transitions`, `verb.operations`, `event.verb`, `transition.verb` and `operation.verb`.
- **Dependency keys:** `model.verbs` and each verb's own fields key on the vocabulary file and on `vn:verbs`, a key whose hash is the vocabulary plus the sorted set of verb keys named anywhere. That set decides whether the list is empty, which hidden verbs it shows and which verbs are used. So editing a process or an operation renders again the units that read `model.verbs` only when it starts or stops naming a key. In a project without verbs the key never changes. `verb.events`, `verb.transitions` and `verb.operations` key on every process and operation file; they matter only when the list is not empty. `event.verb`, `transition.verb` and `operation.verb` key on their own file and on the vocabulary file. Only a template that reads them pays.
- **Records:** event, transition and operation records gain `verb`, the key, only where named. A transition's record `verb` is its own, else its event's. Verb records appear only when the project uses verbs. So `get_resolved_model` answers don't change for a project without them.

## 7. Tools

- **`get_schema`:** the new `verb-vocabulary` schema. `builtInVerb` lists each built-in key with its meaning, so an agent can read the list before a project uses any verb.
- **`get_references`, `referenced_by`:** a use of an own verb is a reference to its id. Uses of built-in verbs are not references, as an unknown stereotype key is none.
- **Deleting `verbs.json`:** refused while own verbs are named, unless `remove-references` (it clears those `verb` keys) or `delete-dependents` (the same, since nothing needs a verb to exist). Uses of built-in verbs stay.
- **`apply_batch`:** `rename-verb` and `remove-verb`. Both take `dryRun`, and both are refused (MQ1002) for a built-in key or a key that isn't an own verb.
- **`get_model_index`:** operation rows gain `verb`, and process rows gain `verbs`, the distinct keys its events and transitions name, sorted. Each appears only when set, so projects without verbs keep the same rows.
- **`validate`:** the rules of section 5.
- **No new tool.** The editor's Verbs tab counts uses from the index rows. The conventions resource and the modeling skill explain verbs and point to the schema for the list.
- **The CLI** follows the same operations. **The rule list, the schemas and the tool texts** describe all of this.

## 8. Packs

Output changes only for a model that uses verbs.

**`csharp-dapper`**
- **`Verbs.cs`,** in the base namespace, only when `model.verbs` isn't empty: an enum `Verb` with one member per verb, each documented with its meaning, and a map between members and keys, since the key is what to store. The builder checks that the file compiles beside `using System;` with warnings as errors.
- **Process definitions:** an event's entry gains its verb only where named, and so does an automatic move's. The entry type's optional parameter is written only when the model uses verbs, so processes without verbs give the same bytes.
- **Operation contracts:** a `Verb` constant only on an operation that names one.
- **Nothing goes in `helpers.js`,** since changing it renders every unit of the pack again.

**`process-docs`**
- **The process page:** the Events table gains a Verb column when an event of the process names one. The Transitions table gains one when a transition's move has a verb.
- **A verbs page,** only when the model uses verbs: each verb, its meaning, and the events, transitions and operations that name it. The index links to it.

**`sql-ddl`, `seed-data` and the TypeScript sample:** no change.

## 9. Editor

**Settings, a new Verbs tab** (beside Tags, Categories and Stereotypes):
- **Built-in verbs:** a table by group, with the verb, its meaning, how many processes and operations name it, and a **Hide** box. Hiding a verb in use is allowed and shows its warning.
- **Your verbs:** a table with key, display name, description and uses, and **Add**, **Rename…** and **Remove…**. Rename and Remove show what will change (a dry run), then run one batch, so Undo reverts it in one step.
- The tab creates `verbs.json` with the first own verb or the first hidden verb.

**A verb picker, shared by every place:**
- **None** first, then the groups, most likely first:
  - an event in a lifecycle, or a transition without an event: Moves, then your verbs, then the rest;
  - an event in an orchestration: your verbs, Tasks, then the rest;
  - an operation: Objects and Links, then your verbs, then the rest.
- Each row shows the display name, and the meaning as secondary text. Typing filters. Hidden verbs aren't listed.
- **Add a verb…** at the foot creates an own verb and picks it. It offers the verbs left to projects as suggestions, which only pre-fill the own verb (decision 7).

**Process editor:**
- **Events:** a **Verb** column.
- **Transition inspector:** **Verb** for a transition without an event. For one with an event, it shows the event's verb, greyed, with a link to the event.
- **The canvas** doesn't change.

**Operation editor:** **Verb** beside **Effect**.

**Elsewhere:** search finds an own verb and opens the Verbs tab. Problems shows **Go to** for MQ9702, MQ9703 and MQ9706. In the simulation panel, an event button's tooltip names its verb.

**Quick fixes:** MQ9701 adds the verb or clears it, MQ9702 and MQ9706 use the built-in, MQ9703 removes the entry, MQ9704 shows the verb again, MQ9705 moves the verb to the event or clears it.

**Generated and mock files:** `src/editor/src/model/verbs.ts`, the built-in list, is made from the engine's table by a fixed command. The kind's entries in `model/model.ts`, `model/labels.ts` and `app/navigation.ts` are added by hand. The mock backend's `validate.ts`, `refs.ts` and `store.ts`, its `mocks/model/cascade.ts` and `modelIndex.ts`, and its batch handling follow the new keys, rules and operations.

## 10. A project that uses none of this

- **Files:** nothing rewrites a file, so saves, `format`, `format --check` and snapshot import behave as today.
- **Findings:** the same. Every new rule needs a verb vocabulary or a `verb` key. No existing rule changes.
- **Output:** the same bytes. Every pack change depends on `model.verbs` or a named verb.
- **Simulation and replay:** the same outcomes. Verbs carry no behaviour in this item.
- **Tool answers about the model:** the same. New index and record members are left out when unused, and `model.verbs` is empty. The rule list, the schemas and the tool texts describe the additions.
- **Capabilities:** the row "Kinds of move: promote, demote, revise, change" in `lifecycle-capabilities.md` becomes Yes once built.
- **Speed:** the same. Verb lists are built only when the project uses verbs.
- **Once, as on any upgrade:**
  - Every unit renders again and writes the same bytes, since the engine version is part of each unit's key.
  - The editor refreshes the `.schema` copies, or `validate` warns MQ1008 until refreshed.
  - The index cache is read in full once.
- **A team on mixed engine versions:** an older engine refuses `verbs.json` and files with `verb` (decision 22).

## 11. What comes next

| Item | What it takes from verbs |
| --- | --- |
| A stereotype as a set of types (#50) | Nothing. It comes first, as the actors design decided. It also changes `process.json`, `openapi.yaml`, `Resolution/ResolvedProcesses.cs`, `Editor/ResolvedRecords.cs`, `Model/ModelIndexer.cs`, `Validation/RuleCatalog.cs`, `validation-rules.json` and the mock's `store.ts` and `validate.ts`, so this item is built on top of it, never side by side |
| Setting the object's data, and reading another lifecycle's state | Nothing new |
| Rights | A right names verbs. An event's `actors` become a right to its verb in its source states. An operation's `requires` becomes a right to its verb with no state. Freezing (decision 16) is enforced. It decides whether freezing stops an own verb, perhaps with an optional key on the own verb |
| Trigger points | The grid of verb, place and kind (check, override, action). Places include lifecycle, state, move, signature, type and relation. An automatic move's verb comes from its transition (decision 12). Generated step methods per verb, keyed on the `Verb` enum, such as one method per verb and kind on each lifecycle |
| Richer sign-off | `reject`, `ignore`, `withdraw`, `delegate` and `route` gain a place on gates. Decision 15 stays |
| Scenarios across objects | A step names a verb and an object: modify a value, connect two objects, freeze one |
| Revisions | `revise` gets its behaviour. A project's own `check-out` and `check-in` may become built-in then (decision 5) |
| Lifecycles on custom kinds | Nothing new. Verbs aren't tied to entities |
| If teams ask | A domain's own verbs (an optional `package`); custom properties on own verbs, such as a lifecycle system's command name; an own verb that refines a built-in. Each adds an optional key and changes no file |

## 12. Decisions the owner must make

1. **Confirm the built-in set and its names before release.** Built-in verbs are permanent, and each one added later may clash with a project's own key (decision 5). The 27 in decision 1 are each in several systems or standards in the yardstick. Four are less certain: `set-state` (an administrator's correction), and `route`, `claim` and `complete` (named by the direction for tasks). `check-out` and `check-in` are left to projects, since their meaning varies most between systems. The ones left to projects are listed in decision 1. Recommended: the 27 as listed. Confidence: high. **Decided (2026-11-06): the owner kept all 27.**

## 13. Tests

- **`VerbRuleTests` (new):** failing and passing cases for MQ9701 to MQ9707. Today's models give the same findings.
- **`VerbVocabularyTests` (new):**
  - Loading at the fixed path; own verbs as sub-elements, with references from each use, `get_references`, and localized display names and descriptions.
  - Deleting `verbs.json` under each delete choice.
  - `rename-verb`: a plain rename; a merge into another own verb; a merge into a built-in; adopting a built-in of the same key. `remove-verb`. Refusals for a built-in or an unknown key. All or nothing, and dry runs.
  - The index rows' `verb` and `verbs`, by patch and by full build; an edit to `verbs.json` makes the patch fall back to a full build.
  - MQ1009 for a second verb vocabulary file.
- **`BuiltInVerbsTests` (new):** the engine's table, the schema's `builtInVerb`, the editor's `verbs.ts` and the user guide's built-in table agree. The engine agent writes that table in wave 1, and the docs agent keeps it. Keys are unique and kebab-case, and their Pascal names are unique.
- **`ResolvedVerbTests` (new):**
  - `model.verbs` is empty without verbs; its order; hidden verbs left out unless named.
  - A transition's verb, its own or its event's; each verb's events, transitions and operations.
  - Dependency keys: editing `verbs.json` renders again only units that read verbs; naming a verb on one event renders again the units that read `model.verbs`.
- **No change:** a model without verbs gives the same output, files, `format --check`, findings, replay and tool answers. The pack golden tests pass untouched.
- **Pack tests:** a new test case with verbs for each pack. `csharp-dapper`'s `Verbs.cs`, event entries and operation constants build with warnings as errors. `process-docs`' Verb columns and verbs page.
- **Editor:** unit tests for the picker's order per place and its hidden filter. Browser tests (run as a browser pass in wave 2) for adding an own verb, hiding a built-in, renaming with a dry run and Undo, naming a verb on an event, a transition and an operation, and MQ9705's quick fix.
- **Acceptance**, on an invented model:
  - A document lifecycle where `Submit` promotes, `SendBack` demotes, an `always` move from Approved to Published promotes, and `Escalate` does the project's own `escalate`.
  - An operation `AssignReviewer` that connects.
  - `lock` and `unlock` hidden, and an own `check-out` added from the suggestions.
  - Renaming `escalate` to `raise` rewrites every use in one step. The packs' output names each verb.

Each stale-output test is checked to fail without its fix.

## 14. Tasks and the build plan

### Tasks

The sizes are days for a person who knows the code, with tests and a review. They show how the groups compare.

| Wave | Group | What | Size |
| --- | --- | --- | --- |
| 1 | Contract and engine | The schemas, the built-in table, the `verb-vocabulary` kind, `verb` on events, transitions and operations, MQ9701 to MQ9707, references, deletes, localization of own verbs, `rename-verb` and `remove-verb`, index rows | A day and a half |
| 1 | Editor | The Verbs tab, the verb picker in three editors, quick fixes, the generated list, the mock | A day |
| 2 | Resolved model and output | `RVerb`, `model.verbs`, records, tool texts; `csharp-dapper` and `process-docs` | A day |
| 3 | Docs | User guide (its built-in table is written by the engine agent in wave 1, and the docs agent keeps it), processes guide, releases, modeling skill, capabilities row, `next.md` | Half a day |

That comes to about four days for a person.

**Checklist for every group.** Anything that reads or writes a vocabulary, an event, a transition or an operation must be found and handled:
- **A new fixed-path kind:** `Model/ElementKind.cs`, `Model/KindInfo.cs`, `Model/ElementBase.cs` (the derived types), `Loading/ModelPaths.cs`, `Model/ModelSnapshot.cs` (one accessor, as `Tags` has), `get_model_kinds`, and the localizable node kinds in `Localization/LocaleChains.cs`.
- **Index patch:** `Model/ModelIndexer.cs` falls back to a full build when `verbs.json` changes, as for stereotypes, tag vocabularies and category trees (`ModelIndexer.cs:328-336`). MQ1009's one-vocabulary check (`ModelIndexer.cs:219-234`) covers verbs.
- **Key references:** a `[VerbKey]` attribute read by `Model/ModelIndexer.cs` beside `[StereotypeKeys]`; `Loading/NameIndex.cs` for the `verb` sub-element kind; `Loading/ChangePlanner.Cascade.cs` for deletes.
- **Save refusals:** MQ9707 is refused in two places: `Validation/SaveRules.cs:24`, beside MQ3020, and `Loading/ChangePlanner.cs:291`.
- **Batch operations:** `ModelStore.cs` (`BatchOp`), with `ModelStore.Tags.cs`' `retag` as the model for a walk over every use.
- **Resolver and records:** `Resolution/ResolvedProcesses.cs`, `Resolution/ResolvedOperations.cs`, `Editor/ResolvedRecords.cs`.
- **Contracts:** the schema copies, `openapi.yaml` and its generated files, `src/editor/src/model/verbs.ts`. `validation-rules.json` and `verbs.ts` are written only by the fixed commands.
- **Test harness:** `RenderKit.WithSettings` and `ResolvedCoverageTests`.

### How it gets built

**What earlier items cost.** 4.1 took about 3.75 hours, 1.71M output tokens, 4.67M new input and 436M cached re-reads, for about 11,000 lines. That is about 155 output tokens, 425 new and 40,000 cached per line. Engine builders cost about 20M cached per 100 steps. Actors, about 7,000 lines, took about 3.5 hours of agent time with reviews.

**This item's size.** About 4,500 to 5,000 lines with tests: engine about 1,500, its tests about 1,100, packs about 450 with expected files, editor and mock about 1,350, docs about 350. That is about 65 to 70% of actors. It has no simulator change, no SQL and no binding work, so few slow test loops.

**Agents.** Each is capped at about 100 steps or 200k context. Opus 5.5 builds the engine and pack output and runs every review. Sonnet 5.5 builds the editor, the mock and the docs.

| Wave | Agent | Model | Steps | Starts from |
| --- | --- | --- | --- | --- |
| 1 | Engine; **contract first**: the schemas, `Model/BuiltInVerbs.cs`, all `openapi.yaml` changes including the record shapes, rule texts. It also writes the user guide's built-in table | Opus | About 100 | `origin/stereotype-sets` (#50) |
| 1 | Editor and mock | Sonnet | About 90 | The contract commit |
| 2 | Resolved model, records, tool texts, and output for both packs | Opus | About 80 | The engine agent's finish |
| 2 | Review of the pull request | Opus | About 50 | Both wave 1 agents' and the output agent's finish |
| 2 | Docs | Sonnet | About 50 | The output agent's finish, beside the review |
| 2 | Browser pass: the editor flows, on the built branch | Sonnet | About 40 | The output agent's finish, beside the review |
| 3 | Fixers, split by area (engine and output; editor and mock; docs) | Opus or Sonnet | About 40 each | The review's and the browser pass's findings |

- **At most three agents run at once.** The output agent starts after the engine agent, since both touch `Resolution/`, `Editor/ResolvedRecords.cs` and the tool texts.
- **One pull request, stacked on #50** (stereotype sets), with the contract, engine, editor, output, docs and fix commits. The owner merges #50 first, then this one.
- **Generated files** (the editor's API types and verb list, the recorded rule list `validation-rules.json`, the sample app's copies) are written only by the fixed commands, run after each merge into the branch. No agent edits `validation-rules.json` or `verbs.ts` by hand. All `openapi.yaml` changes are in the contract commit. Pack expected files stay with the builder, so their changes are reviewed.
- **Fixers** are fresh agents with the exact findings.
- **A design gap found while building** stops the work, and the owner is asked. Builders don't decide scope.

**Estimate.**

| | Wall time | Output | New input | Cached re-reads |
| --- | --- | --- | --- | --- |
| Wave 1 (contract, engine, editor) | About 1.1 hours | 0.35M | 0.9M | 75M |
| Wave 2 (output, review, docs, browser pass) | About 1 hour | 0.25M | 0.7M | 60M |
| Wave 3 (fixes and CI) | About 0.6 hours | 0.1M | 0.3M | 35M |
| In all | **About 2.5 to 3 hours** | **About 0.7M** | **About 1.9M** | **About 170M** |

The output and new-input figures follow 4.1's cost per line. The cached figure sits between two readings: by steps, about 85M for the agents (about 20M per 100 Opus steps and 15M per 100 Sonnet steps, fixers included) plus about 40M for coordinating one pull request, so about 125M; by 4.1's cost per line, about 190M. The wall time is about 70 to 85% of the actors' 3.5 hours, with the browser pass and the stack on #50 added.
