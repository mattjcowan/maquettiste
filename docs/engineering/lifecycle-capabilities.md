# What lifecycle systems can do: the union, and where Maquettiste stands

**Status (2026-11-05): a first capture, for the owner to correct.** It lists what product lifecycle systems offer for lifecycles and triggers, as one list with duplicates removed, and marks what Maquettiste can state today. It decides nothing. [The design of 4.1](lifecycle-design.md) was rewritten from it.

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
| N | Standards | Role-based access control, attribute-based access control, the state machine part of UML, human-task and case-management models, from general knowledge |

"Own" marks what the owner asked for or explained directly. Not read: Arena, Propel, Infor and others. The owner has confirmed what E's keywords mean where the syntax alone left it open: freezing, automatic promotion, signature filters, routing and rules.

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
| An object's owner, stated explicitly | E N | **No** |
| A move or action records data on the object (who submitted, when released) | N | Yes: an action's `sets`, with `current_actor()` and `now()` (built for 0.14.0, #52). Simulation runs them; generated code leaves the write to the team through a `Record<Action>` hook |
| A condition reads the object's state in another lifecycle, however it is stored | Own N | Yes: `in_state(object, state, …)` for the object or a to-one link, with scenario objects giving the states (built for 0.14.0, #52) |
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
| An event a state can't handle now: refused, ignored, or deferred until a state can | N | Part: refused only |
| Entry and exit points on a reusable part of a chart | N | Part: sub-processes |
| Stages and milestones | N | Part: compound states |
| Who may read, change or delete per state, by role | E W A S V | Yes: a lifecycle's `rights` name who, which verbs and which states, with `except` (built for 0.15.0, part 1) |
| A long list of rights per state: promote, demote, override, revise, connect and disconnect from either end, lock, freeze, schedule, change owner, type or policy, and more (32 in E) | E | Part: rights name any of the 27 built-in verbs or your own, per state (built for 0.15.0, part 1). Which verbs do something beyond a check is left to trigger points |
| Rights set separately for the owner, for everyone and for named people or roles | E | Yes: `actors` takes the owner, everyone (empty) or named people, roles and groups (built for 0.15.0, part 1) |
| **A rule that allows or forbids operations when a condition holds,** over the object's data and the current user. For example: forbid changing, deleting or reassigning an item classified secret unless you own it | E Own | Yes: a right's `condition` reads the object and `current_actor()`, with `except` for the owner (built for 0.15.0, part 1). It checks scenarios and generated code, and stops nothing in a system the team does not build |
| A named access rule, reused across things | E | Part: a named right, used by many lifecycles (built for 0.15.0, part 1). Types with no lifecycle are a later item |
| Rights on one attribute in one state (hide a salary from everyone outside HR) | N | Yes: a right's `attributes`, each attribute checked on its own (built for 0.16.0, part 2) |
| Rights passed along containment (a project's rights reach its items) | N | **No** |
| Role membership valid for a period | N | **No** |
| Several rights combined: any deny wins, then any allow, then a default | N | Yes: each lifecycle answers on its own, its denies, then its allows, then its default, and any lifecycle's "no" wins (built for 0.15.0, part 1) |
| An access rule for every state | E | Yes: a right with no states holds in every state (built for 0.15.0, part 1) |
| The object can't be edited in this state | A | **No**: 4.4 |
| **Any item can be frozen, and can't be edited until it is thawed:** an object, a relation's instance (it can't be disconnected or have its attributes edited), and others such as rules and processes. Freezing and thawing are operations with their own triggers, so automation can run on them | E Own | Part: objects and links freeze and thaw, and while frozen they can't be modified, connected, disconnected or deleted (built for 0.16.0, part 2). Rules and processes can't be frozen, and triggers on freezing are later |
| Fields required to enter or to leave a state | O | **No**: 4.4 |
| What may be done with the object in this state (allowed and forbidden operations) | P | Part: an operation can `require` a state |
| How relationships behave in this state (fixed or floating) | A | No |
| Default revision scheme in this state | W | No |
| May be revised, or versioned, in this state | E | **No** |
| A planned date for each state, on each object | E | No |
| Moves out of a state switched off for one object | E | No |
| An approval routed to a named person or role (`route`) | E Own | Part: a gate's `signers` are actors, which can be roles |
| Notify people, or whoever signed, on entering, with a message | E A O | No |
| Entering starts a workflow | A W | Yes: `invoke` |
| A state's history format | A | No |

## C. Transitions

| Capability | Seen in | Today |
| --- | --- | --- |
| Allowed from-to pairs | E W A O S V | Yes |
| All pairs allowed by default | V | No: each is declared |
| Kinds of move: promote, demote, revise, change | E W | Yes: events, automatic moves and operations name a verb (`promote`, `demote`, `revise`, `change-owner` and others). Verbs carry no behaviour yet |
| A casual state change beside a reviewed one | W | No |
| Someone with the right may move past unmet conditions (`override`), or set any state | E W O | **No** |
| Who may run a transition, by role, in a given state | A F S V | Part: an event's `actors` hold in every source state alike |
| Moves on by itself once every check and signature passes, running its actions as always (`promote true`) | E O S V Own | Yes: `always`, `done`, `after`, and a gate's transition fires on the signature that completes it |
| When several next states are possible, the person promoting picks one, or a trigger picks one from the object's data | E Own | Part: guards and choice states pick from data; a person picks by sending a different event |
| Conditions on the object's data | O S V | Part: guards read the context and the event, **not the object**. 4.1 |
| Conditions on related objects (all affected items, a BOM audit) | O | Part: a guard reads to-one links and their state with `in_state`; to-many ("every affected item") is still **No** |
| A condition that hides a transition, apart from one that refuses it | F | No: one guard does both |
| Where a rejection goes | E O | Part: a rejection is a veto that moves nothing; a move on rejection is another transition whose verb is `reject` (0.19.0) |
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
| Named points before and after a move | A O F | Yes: trigger points: a before action, a check, an action with the change and an action after commit (0.17.0) |
| A check that can stop the move | E A F O | Yes: a guard, or a trigger point's check, which works like a false guard for a move |
| An override that replaces the normal behaviour | E | Part: a trigger point's replace, on `delete`, `disconnect`, `copy` and `revise` only (0.17.0) |
| An action after the move | E A F O S V | Yes |
| A point just before commit | E | Part: an action with the change, in the same unit of work |
| A failure that does or doesn't roll the move back | A | Yes: a with-the-change action is undone on failure, an after-commit action can't undo the change |
| Runs in line, or later (`execute immediate` or `deferred`, sync or async) | E O | Yes: with the change, or after commit (through the outbox) |
| Triggers switched off for a session, as for a bulk load | E | Part: in generated `csharp-dapper` code only, behind `bulkLoadBypass` (off), when the team's hook allows it; `noBypass` points always run, and a replace is never skipped. Simulation never skips (0.18.0) |
| Order when several run | O S | Yes: one fixed order for every verb, named points before a lifecycle's, then the list's order |
| Points on types with no lifecycle, or one rule for many types | E O | Yes: named trigger points, with covers and except (0.18.0) |
| The same three kinds at every point: check, override, action | E | Yes: check, action and a narrow replace |
| Points on each sign-off event: approve, reject, ignore, withdraw | E Own | Yes: at an approval (0.19.0); a plain gate takes `sign` only |
| Points on other state events: promote, demote, override, enable, disable, schedule | E | Part: promote and demote, and your own verbs an event names; `override` and `set-state` wait for scenario steps |
| **Points on every operation on an object, not a fixed few:** create, copy, delete, connect, disconnect, freeze, thaw, lock, unlock, revise, check in or out, remove a file, grant, revoke, and changing an attribute, description, name, owner, type, policy or vault | E O Own | Yes: any verb, built-in or your own, with places by state, type, attribute and link end |
| Points on a relationship's operations: create, delete, change an attribute, freeze, thaw | E Own | Yes: `connect`, `disconnect`, `freeze`, `thaw`, `read` and `modify` through an end |
| Points on an attribute's change | E | Yes: `attributes` beside `read` and `modify`, with `old.` for the value before |
| Points on a workflow and its activities: start, stop, finish, suspend, resume, reassign, activate, complete, override | E | Part: `invoke-done` and `invoke-error` |
| A program attached to a type, run on an object (`method`) | E | Yes: operations |
| Logic in any language or plugin | E A O F V | Yes: a stub carries no language |
| A reusable handler, named by many | E O T | Yes: libraries (#33) |
| Arguments passed to a handler (`input`) | E T | Part: custom properties (#32) |
| An action on one object that moves another in its lifecycle (a change order releasing the items it affects) | E O N | **No** |
| Messages back to the user when refused | F | Yes: a check's message, named in the refusal. A process guard still gives none |

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
| Approve, reject, ignore, with a separate list of who may do each | E O | Yes: an approval's signers count as allowed for sign, reject, ignore and withdraw, and rights narrow each verb (0.19.0) |
| Whether a signature is needed, and from whom, decided by a condition or a routine over the object's data (`filter`) | E Own | Yes: a named signature's `when` (0.19.0); in generated code the team writes it |
| Several named signatures on one state | E | Yes: an approval's named signatures (0.19.0) |
| A signature withdrawn (`unsign`) | E | Yes: the response `withdraw` (0.19.0) |
| Approvers, observers, acknowledgers | O | **No**: signers only |
| Reviewers set per lifecycle role, mapped to people | W O | Part: actors, with no mapping |
| Person, role, group and organization told apart, with membership | E W O N | **No**: planned in the actors design (0.14.0) |
| Reviewers added or removed at run time | O | No: the system's job |
| Signing again with a password | S O | **No** |
| Signing authority handed to someone else | E O N | **No** |
| Claiming a task from a group, and an escalation chain | N | **No** |
| Separation of duties: one person can't hold two roles on one object, or approve what they submitted | N | Part: yes for "approve what they submitted" (`notBy`, 0.19.0); no for one person never holding two roles on one object |
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
