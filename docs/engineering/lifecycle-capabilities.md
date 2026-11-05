# What lifecycle systems can do: the union, and where Maquettiste stands

**Status (2026-11-05): a first capture, for the owner to correct.** It lists what product lifecycle systems offer for lifecycles and triggers, as one list with duplicates removed, and marks what Maquettiste can state today. It decides nothing. The design of roadmap item 4.1 will be reworked from it.

**Why this list exists.** Maquettiste is a modeling tool first. Some teams will only model, and a product team may then implement the model in a lifecycle system. Others will generate code with packs. A model must leave room for all of these, so this list is the yardstick for what a lifecycle model should be able to say. A row here is not a promise to build it: some belong in the model, and others can be custom properties, stereotypes or pack output.

## Prior art

Each row's "Seen in" uses these letters. A letter means this session's reading of that system's documentation or community material showed it.

| Letter | System | How much was read |
| --- | --- | --- |
| E | ENOVIA / 3DEXPERIENCE (eMatrix) | The full MQL command syntax, from a reference the owner supplied (an older Matrix release), and a third-party copy of the newer help |
| W | PTC Windchill | Help Center on state changes, phases, and basic and advanced templates |
| T | Siemens Teamcenter | Thin: API notes, a training page and a guide on signoffs. Access rules were not found |
| A | Aras Innovator | Life cycle map guide. Workflow voting is thin |
| O | Oracle Agile PLM | Administrator guide on workflows and event handlers, the most complete |
| F | Autodesk Fusion Manage | Scripting on transitions only. Approvals were not found |
| S | SOLIDWORKS PDM | Transition options |
| V | Autodesk Vault | Lifecycle definitions, states and transitions |
| P | SAP status management | General status management, not its PLM areas |

"Own" marks what the owner asked for directly. Not read: Arena, Propel, Infor and others. The syntax shows what can be stated, not always what it does, so a row from E's syntax can read more into a keyword than the system means; the owner can correct those.

In the "Today" column, **Yes** means the model can say it, **Part** means some of it, **No** means it can't, and **Pack** means a pack can write it from what the model holds. A team can record almost anything in a custom property today; that alone doesn't count as Yes, because nothing gives it meaning.

## A. Defining and assigning lifecycles

| Capability | Seen in | Today |
| --- | --- | --- |
| A lifecycle is defined once, apart from the types that use it | E W A O V | Yes |
| One lifecycle governs many types, or all of them | E V P | **No**: `subject` is one entity |
| A lifecycle governs any kind of object: files, folders, items, custom objects | V | **No**: entities only |
| A type has several possible lifecycles, chosen per object by a rule | O | **No** |
| A rule assigns the lifecycle when an object is created | W O | **No** |
| One object is in several different lifecycles at once (an employee's HR and project lifecycles) | Own | **No**: an entity names one |
| One object is in several instances of one lifecycle (often better as a detail entity such as a project assignment) | Own | **No** |
| Several statuses active at once on one object | P | Part: parallel states, inside one lifecycle |
| An object moves to another lifecycle, with a right and a trigger for it | E | **No** |
| A type opts out of lifecycles | V | Yes: no lifecycle is the default |
| A lifecycle is enabled, disabled or hidden | E O | Part: `status` on any element |
| Its own properties | E | Yes |
| Editing requires a lock, set per lifecycle (`enforce`) | E | No |
| Copying a lifecycle, and moving definitions between environments | E O | Part: the model is files, and packages can be shared |
| A new version of a lifecycle, and what happens to objects already in it | E W O | **No**: roadmap 4.5 |

## B. States

| Capability | Seen in | Today |
| --- | --- | --- |
| Name, label, translations, description | A | Yes |
| Icon | E A | No |
| One list of states shared across lifecycles | W | Part: an enum or reference type can be shared |
| A stable stored code per state | Own | **No**: 4.1 |
| States in an order | E O | Part: document order |
| Sub-states, parallel regions, history | | Yes: more than most of these systems |
| A phase distinct from its state | W | No |
| A state marked as released | A O | Part: custom property |
| A final state | O | Yes |
| States outside the normal path, reachable from anywhere (hold, cancel) | O | Part: as ordinary transitions |
| Who may read, change or delete per state, by role | E W A S V | **No**: custom property only |
| A long list of rights per state: promote, demote, override, revise, connect and disconnect from either end, lock, freeze, schedule, change owner, type or policy, and more (32 in E) | E | **No** |
| Rights set separately for the owner, for everyone and for named people or roles | E | **No** |
| A named access rule, reused across things | E | **No** |
| An access rule for every state | E | **No** |
| The object can't be edited in this state | A | **No**: 4.4 |
| Fields required to enter or to leave a state | O | **No**: 4.4 |
| What may be done with the object in this state (allowed and forbidden operations) | P | Part: an operation can `require` a state |
| How relationships behave in this state (fixed or floating) | A | No |
| Default revision scheme in this state | W | No |
| May be revised, or versioned, in this state | E | **No** |
| A planned date for each state, on each object | E | No |
| Moves out of a state switched off for one object | E | No |
| Handed to a named person on entering, with a message (`route`) | E | No |
| Notify people, or whoever signed, on entering, with a message | E A O | No |
| Entering starts a workflow | A W | Yes: `invoke` |
| A state's history format | A | No |

## C. Transitions

| Capability | Seen in | Today |
| --- | --- | --- |
| Allowed from-to pairs | E W A O S V | Yes |
| All pairs allowed by default | V | No: each is declared |
| Kinds of move: promote, demote, revise, change | E W | Part: events are free-named, with no kinds |
| A casual state change beside a reviewed one | W | No |
| Someone with the right may move past unmet conditions (`override`), or set any state | E W O | **No** |
| Who may run a transition, by role | A F S V | Yes: an event's `actors` |
| Runs by itself when conditions are met | E O S V | Yes: `always`, `done`, `after` |
| Conditions on the object's data | O S V | Part: guards read the context and the event, **not the object**. 4.1 |
| Conditions on related objects (all affected items, a BOM audit) | O | **No** |
| A condition that hides a transition, apart from one that refuses it | F | No: one guard does both |
| Where a rejection goes | E O | Part: as another transition |
| Which state comes next depends on which signature was met (`branch`) | E | Part: as separate transitions, but a source and event can carry only one gate |
| A move back that is always there (`demote`) | E W | Part: each is declared |
| The same person can't make two moves in a row | S | Part: a gate's `allowRepeatSigner` |
| Actions, in order | S V | Yes |
| Changing the revision on a move | S V | No |
| Comment allowed, required or prohibited on a move | O | Part: a gate's `reasonRequired` |
| Moving many objects together | W | No: the system's job |

## D. Triggers: custom logic at each point

| Capability | Seen in | Today |
| --- | --- | --- |
| Named points before and after a move | A O F | Part: a guard before, an action after |
| A check that can stop the move | E A F O | Yes: a guard |
| An override that replaces the normal behaviour | E | **No** |
| An action after the move | E A F O S V | Yes |
| A point just before commit | E | **No** |
| A failure that does or doesn't roll the move back | A | **No** |
| Runs in line, or later (`execute immediate` or `deferred`, sync or async) | E O | **No** |
| Triggers switched off for a session, as for a bulk load | E | No: the system's job |
| Order when several run | O S | Part: an action list's order |
| The same three kinds at every point: check, override, action | E | Part: check and action, no override |
| Points on each sign-off event: approve, reject, ignore, withdraw | E Own | **No** |
| Points on other state events: promote, demote, override, enable, disable, schedule | E | Part: promote only, as a guard or action |
| **Points on every operation on an object, not a fixed few:** create, copy, delete, connect, disconnect, freeze, thaw, lock, unlock, revise, check in or out, remove a file, grant, revoke, and changing an attribute, description, name, owner, type, policy or vault | E O Own | **No** |
| Points on a relationship's operations: create, delete, change an attribute, freeze, thaw | E Own | **No** |
| Points on an attribute's change | E | **No** |
| Points on a workflow and its activities: start, stop, finish, suspend, resume, reassign, activate, complete, override | E | Part: `invoke-done` and `invoke-error` |
| A program attached to a type, run on an object (`method`) | E | Yes: operations |
| Logic in any language or plugin | E A O F V | Yes: a stub carries no language |
| A reusable handler, named by many | E O T | Yes: libraries (#33) |
| Arguments passed to a handler (`input`) | E T | Part: custom properties (#32) |
| Messages back to the user when refused | F | Part: an operation guard's `error`, not a process guard's |

**How a pack can use this, with what the model holds today (Pack):**
- Partial classes per lifecycle, with a do-nothing partial method per guard and action. `csharp-dapper` already does this.
- A table of from-to codes, with one pipeline or middleware method that runs custom logic per pair. Templates can list `process.transitions`, but they can't yet write the codes (4.1) or name points around sign-off.
- Scripts for a lifecycle system's own language, from the same model.

## E. Sign-off

| Capability | Seen in | Today |
| --- | --- | --- |
| Signatures needed on a move | E W O S | Yes: a gate |
| How many must sign | S O | Yes: `required` |
| Everyone in a group, or anyone | O | Part: by count |
| Weighted votes | A | No |
| Approve, reject, ignore, with a separate list of who may do each | E O | Part: `meanings` and one list of signers; no reject path |
| A signature needed only when an expression holds (`filter`) | E | **No** |
| Several named signatures on one state | E | **No**: one gate per transition |
| A signature withdrawn (`unsign`) | E | **No** |
| Approvers, observers, acknowledgers | O | **No**: signers only |
| Reviewers set per lifecycle role, mapped to people | W O | Part: actors, with no mapping |
| Reviewers added or removed at run time | O | No: the system's job |
| Signing again with a password | S O | **No** |
| Signing authority handed to someone else | E O | **No** |
| Reminders and escalation | O | **No**: a timer could express it |
| Steps in series or parallel | T W A | **Part**: invokes and orchestrations; 4.3 |
| One sign-off policy shared by many moves | Own | Yes: gate policies (#33) |
| Signatures defined outside the model | Own | Yes: leave out the gate; custom logic decides |
| A record of each signature | E O | Yes: audit attributes |

## F. Workflows beside the lifecycle

| Capability | Seen in | Today |
| --- | --- | --- |
| A workflow on a state or phase | W A | Yes: `invoke` |
| A workflow on leaving a phase | W | Part: a sub-process before the move |
| A simple lifecycle with no workflows, and a full one | W | Yes: both can be modeled |
| Tasks with assignees | E T A | Yes: a `human-task` invoke |
| Automated steps that run a program, and sub-processes | E | Yes: `service` and `process` invokes |
| And and or nodes joining paths | E | Part: parallel states and choice states |
| A task's duration, priority, instructions, share of the day | E | No: custom property |
| Accepting, suspending, resuming and reassigning running work | E | No: the system's job |

## G. Revisions

| Capability | Seen in | Today |
| --- | --- | --- |
| A revision sequence, major and minor | E W S V | **No** |
| Revising to a chosen name, and reading the previous, next, first and last revision | E | **No** |
| What a relationship does when an end is revised or copied: nothing, follow, or duplicate | E A | **No** |
| Release moves the revision on, and the next edit starts over | A | **No** |
| Revising creates a new version | W | **No** |

## H. Where the state is held

| Capability | Seen in | Today |
| --- | --- | --- |
| The model says nothing about storage | Own | Yes |
| A single status column | Own | Part: an enum attribute only. 4.1 widens this |
| **The lifecycle's own tables: instances, current state, history, signatures. The owner expects this to be common, or the most common** | Own | Pack: `sql-ddl`'s `processTables`, off by default |
| A history of who moved what, when, with comments, added to by hand, and switched off for a session | E A | Pack: same tables |
| Current state and every state's detail readable on the object | E | Pack: templates read the definition; values are the system's |
| The system's own store, when a lifecycle system implements the model | Own | Yes: the model doesn't mind |

## I. The same lesson in relations

Not lifecycles, but the same barrier, and the owner raised it directly.

| Capability | Seen in | Today |
| --- | --- | --- |
| **A relation's end allows several types, or all of them** | E Own | **No**: each end names one entity. A base entity covers its derived ones |
| Meaning and cardinality per end | E | Yes |
| Duplicates prevented | E | Yes: `allowDuplicates` |
| Attributes on the relation | E | Yes |

A custom property's typed link (`x-ref`) can already name several kinds, so the idea isn't new to the model. Any relation is read by rules, templates, bindings and packs, so changing this is a large item of its own, and it isn't part of 4.1.

## What this changes for item 4.1

These are directions to confirm, not decisions.

1. **Storage must stay open.** A status column is one option among several, and likely not the main one. So `state.code` can't depend on a bound attribute: a state has a code whether the state lives in a column, its own tables or another system. The draft's "code that does nothing" warning and its root-states-only limit both came from assuming a column, and only the column binding should keep them.
2. **One lifecycle, many types, and one type, many lifecycles.** `subject` as a single entity and `entity.lifecycle` as a single process are the largest limits this list shows. Lifecycles may also need to govern more than entities, such as custom kinds.
3. **Guards read the object.** The draft held this back because generated code couldn't run it. That reasoning doesn't hold: a guard is a useful statement in a model with no pack at all, and a hand-written partial method is an acceptable result.
4. **Trigger points deserve a name.** E's syntax gives a clean shape: a grid of events (promote, demote, approve, reject and so on) by kinds (check, override, action), each naming a program with optional arguments. Today's guards and actions are two cells of that grid. A model could name the rest, and a template could read each point, with from-to pairs and their codes, whichever way a team implements it: partial methods, one pipeline, or a lifecycle system's own programs. The owner's point is that every operation has its points, so the list of events should be open rather than a fixed few. Points on an object's own operations (create, freeze, lock, connect) go wider than lifecycles.
5. **Rows that may be next after that:** per-state access, entry and exit required fields (partly 4.4), a reject path, observers and acknowledgers, and revision schemes.

## Sources

- ENOVIA: an MQL syntax reference the owner supplied (not public here); [Modify Policy](https://help-3dexperience.aesvietnam.com/English/MqlReferenceMap/mql-r-policy-command-modify.htm), [Trigger Clause](https://help-3dexperience.aesvietnam.com/English/MqlReferenceMap/mql-r-relationship-add-triggerclause.htm), [MQL cheat sheet](https://cheatography.com/deleted-98672/cheat-sheets/mql-syntax/), [route creation on maturity change](https://3dswym.3dexperience.3ds.com/post/enovia-user-community/automatic-route-creation-triggered-by-the-object-maturity-state-change_M8L8AA1lRSOl0Wmq9lTU8g)
- Windchill: [Planning Object State Change Policies](https://support.ptc.com/help/wnc/r12.1.0.0/en/Windchill_Help_Center/WCAdminOview/WCAdminOviewSetStateVsPromote.html), [Life Cycle Phases](https://support.ptc.com/help/windchill/cloud/r12.0.2.0/en/Windchill_Help_Center/LCPhasePropAbout.html), [Phase Workflow](https://support.ptc.com/help/windchill/cloud/r12.0.2.0/en/Windchill_Help_Center/LCWFProcessPhaseGate.html), [Basic and Advanced Life Cycles](https://support.ptc.com/help/windchill/cloud/r12.0.2.0/en/Windchill_Help_Center/LCChp_BasicAdvLC.html)
- Teamcenter: [ITK release status notes](https://support.industrysoftware.automation.siemens.com/docs/teamcenter/10.1/help/en_US/custom/ITKFunction/group___c_r.html), [managing product changes](https://docs.plm.automation.siemens.com/data_services/resources/rapid_start/11.6/rapid_start_help_pdf_412/tdoc/en_US/pdf/rs_managing_product_changes_aw.pdf), [configuration and release training](https://drwe.de/en/trainings/teamcenter/teamcenter-konfiguration-und-freigabeprozesse)
- Aras: [Creating a Life Cycle Map](https://docs.aras.com/creating-a-life-cycle-map/0000019f-d65e-d08f-a1df-df7e94070000), [Life Cycle Basics](https://community.aras.com/b/english/posts/life-cycle-basics)
- Agile PLM: [Workflows](https://docs.oracle.com/cd/E69230_15/otn/pdf/administration/html_agaah/output/chapter_11.htm), [Event Management](https://docs.oracle.com/cd/E69230_15/otn/pdf/administration/html_agaah/output/chapter_30.htm), [Process Extensions](https://docs.oracle.com/cd/E50306_29/otn/pdf/administration/html_agaah/output/chapter_8.htm)
- Fusion Manage: [condition and validation scripts](https://blog.autodesk.io/script-types-condition-and-validation-script/), [Scripting Basics](https://help.autodesk.com/cloudhelp/ENU/PLM-360-Scripting/files/DEV-SCRIPTING-BASICS.htm)
- SOLIDWORKS PDM: [Transition Properties](https://help.solidworks.com/2020/English/EnterprisePDM/Admin/IDD_TRANS_PROP.htm)
- Vault: [Lifecycle Definition Administration](https://help.autodesk.com/cloudhelp/2025/ENU/Vault-Admin/files/GUID-088F0236-B5B7-4E35-B218-BD2D253B87F6.htm), [Edit Lifecycle State Transitions](https://help.autodesk.com/cloudhelp/2025/ENU/Vault-Admin/files/GUID-B4443A39-A621-4E14-8A11-484EFBF3DB90.htm), [Lifecycles and States](https://help.autodesk.com/cloudhelp/Help/ENU/Vault/files/GUID-63CE799F-5E53-417C-9A6E-3F1987943D83.htm)
- SAP: [User Status Management](https://help.sap.com/saphelp_46c/helpdata/en/05/5d4c66b7ef11d195240000e82debf7/content.htm), [General SAP Status Management](https://help.sap.com/saphelp_46c/helpdata/EN/a9/ab748f414111d182b10000e829fbfe/content.htm)
