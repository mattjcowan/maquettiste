# Next

The one list of what is not built yet, and of every item on the roadmap of 2026-10-30 with where it stands. It
changes with every change that builds, moves or drops an item. The record of what is built stays in `status.md`, the
design documents and `docs/releases.md`.

How to read the tables:

- **#** is the roadmap's item number. The designs cite these numbers ("scope 3.3").
- **Requested** is "yes" when a team that uses Maquettiste asked for the item, whatever its status here. Where the team
  asked for more than the item builds, or for a different form, the notes say so and "Not doing" lists what was
  declined.
- **Status** is "built" with the version, "build next" or "later", "covered" by other items, or "not doing".
- **Build next** and **later** are the owner's decisions of 2026-11-02, taken after an independent review of every item
  not built. *Build next*: most teams need it soon. *Later*: kept on the roadmap with no date; picked when what it
  depends on exists, or when it becomes the most needed. Nothing on the roadmap is dropped, except what "Not doing"
  lists.

Every roadmap item not built yet was requested, except projections (3.12), views (3.13) and declared environments
(6.9). The older open work at the end came from the owner's reviews.

The build-next order of 2026-11-02, one pull request each, now all built (pull requests #3 to #10): operations, first cut (3.6);
project-defined kinds (3.7); the small fixes marked below, validation speed first; library packs with exports (5.1,
5.3); tests for packs (8.2), whose design builds on libraries; calculated values (5.2); declarative rules and
suppressions (8.3); the model diff with impact before apply (7.3, 7.2).

## Build next

The owner's decisions of 2026-11-05, taken after asking the team that requested 3.10. In this order, one pull request
each. An item with no number is new to the roadmap. Each new name and format is held to one test: it stays for good, it
must help any team whether they use it or not, and it stays optional.

| # | Item | Requested | Status | Notes |
| --- | --- | --- | --- | --- |
| | A template helper that lists what links to an element (`referenced_by`) | yes | Built, 0.12.2 | Each row says the holder's kind and status and the field that made the link, and reaches parts such as a gate. A unit that reads it renders again when a link comes or goes, or a holder changes. |
| | Declared rules that select one custom type | yes | Built, 0.12.2 | `custom:<type>` in a rule's `select`; `custom` still judges every custom type. |
| | Create or update by name in a batch | yes | Built, 0.12.2 | The operation `upsert`: one step for a script that refreshes elements from another source. An update replaces the whole document. It never moves an element or takes over another, an element that exists must send the ids of the parts it keeps, and seeds are left out. |
| | Declared rules that leave retired linking elements out of a count | yes | Built, 0.12.2 | `excludeRetired` on `references` and `referencedBy`. Without it a retired process or scenario still counts. A general filter was not added: a condition on the holder cannot see that a part sits in a retired element. |
| 3.10 | Requirements as a project's own custom kind, with typed links and declared rules, and a guide that shows how | yes | Built, 0.12.2 | [Requirements as a kind of your own](../guides/requirements-as-your-own-kind.md). Replaces the built-in kind of [design 4.3](next-phase-2-design.md#43-requirements), which is in "Not doing". |
| | Custom properties and stereotypes on guards, actions, invokes and gates, and custom properties on checks and constraints | yes | Built, 0.12.2 | [Design 4.4](next-phase-2-design.md#44-shared-guards-actions-and-gate-policies), decision 9, plus checks and constraints. An operation's guards hold them as well. Properties on a check never reach its SQL. |
| 4.2 | Shared process guards, actions and gate policies | yes | Built, 0.12.2 | [Design 4.4](next-phase-2-design.md#44-shared-guards-actions-and-gate-policies). A library's guard holds an expression, not a condition, since a condition names one process's context. The editor edits a library through the JSON view. |
| 4.1 | Lifecycles any implementation can follow: a stable code per state, `subjects` for one lifecycle on many types (the one-subject form deprecated with a warning), the state held in an attribute as one option, guard conditions that read the object, and a scenario's objects | yes | Built, 0.13.0 | [The design](lifecycle-design.md), with the owner's decisions of 2026-11-05, checked four times. Built in #36 to #39, each reviewed independently, in about four hours by agents. [What lifecycle systems can do](lifecycle-capabilities.md) is its yardstick, and [Who, what and when](lifecycle-actors-scenarios.md) sets the order of the items that follow. |

## Lifecycles after 4.1 (the owner, 2026-11-05)

The owner's order for what follows 4.1, from [Who, what and when](lifecycle-actors-scenarios.md#10-order) and
[what comes next](lifecycle-design.md#12-what-comes-next). Each needs its own design first. None has a roadmap number.
The model keeps two keys on a subject entry for the last items, but does not accept them yet: `when` and `owner`.

| # | Item | Requested | Status | Notes |
| --- | --- | --- | --- | --- |
| | Actors: membership, relative actors starting with the owner, roles within a context, the system, and participants | yes (the owner) | Built, 0.14.0 (#47, #49) | [Design](actors-design.md). Also brings the explorer's later steps: a role lists its members, participants sit with their scenarios, and the system actor shows as **Automatic**. The owner kept the word "participants". |
| | A stereotype as a set of types: a lifecycle's subject naming a stereotype covers every entity that carries it, and guards and owner fields read its attributes | yes (the owner) | Built, 0.14.0 (#50) | [Design](stereotype-sets-design.md). Comes right after actors ([actors decision 19](actors-design.md)). |
| | Verbs: a built-in set and the project's own, with events and operations naming theirs | yes (the owner) | Built, 0.14.0 (#51) | [Design](verbs-design.md), from [section 5](lifecycle-actors-scenarios.md#5-what-verbs). 27 built-in verbs with fixed meanings, the project's own in `verbs.json`, and a verb on events, automatic moves and operations. The owner kept all 27. |
| | Setting the object's data from a move, and reading another lifecycle's state | yes (the owner) | Built, 0.14.0 (#52) | [Design](object-effects-design.md), from [section 6](lifecycle-actors-scenarios.md#6-two-prerequisites-setting-the-objects-data-and-reading-another-lifecycles-state). Rights need both. An action lists `sets` (an attribute and a value in the expression language), which run in simulation; the language gains `current_actor()` and `in_state(object, state, …)` for the subject or a to-one link; scenario objects give their `states`. The owner kept the names, with `current_actor()` in place of the working `actor()`. |
| | Rights: named rights, defaults, combining, freezing, and refusal reasons in scenarios | yes (the owner) | Built: part 1, 0.15.0 (#55); part 2, 0.16.0 (#57) | [Design](rights-design.md), from [section 7](lifecycle-actors-scenarios.md#7-when-rights-with-rules-and-freezing-folded-in). Built in two parts, each its own release. Part 1: a lifecycle lists rights (allow or deny, who, who not with `except`, which verbs, in which states, under a condition) and a default; a named right is its own element, reused by many lifecycles; each lifecycle answers on its own, and any "no" wins; a scenario step can do an object verb, a refused step names what refused it, and "who can do what" shows the answers. Part 2: freezing (while frozen, an object or link can't be modified, connected, disconnected or deleted) and rights on one attribute. The owner answered on 2026-11-06: `access` in place of `effect`, `except` added, rights on types with no lifecycle later. Left for the owner: confirm three new names before part 1's release, and before part 2 that naming `freeze` is the opt-in to freezing. About 4 to 4.5 hours for part 1, and 2 to 2.5 hours for part 2. |
| | Trigger points on every verb, as check, override and action, with generated step methods | yes (the owner) | Built: part 1, 0.17.0 (#60); part 2, 0.18.0 (#63). Part 2 holds named trigger points for types with no lifecycle or shared by many types (covers, except, "Never skipped", "Runs for"), and in `csharp-dapper` the opt-in bulk-load bypass and dry run | [Design](trigger-points-design.md), from [section 10](lifecycle-actors-scenarios.md#10-order), step 6. Part 1 holds trigger points on lifecycles for every verb, the fixed order, `old.`, check outcomes in scenario `assume`, "What happens when" in the editor, and `csharp-dapper` step methods with a per-type runner and an after-commit relay. A lifecycle lists `triggerPoints`: a check (refuse with a message, or only warn), an action (before the checks, with the change, or after commit), or a replace of a verb's built-in effect on delete, disconnect, copy and revise only. Optional keys say where: states, into states, moves, types, attributes, link ends. Every verb runs in one fixed order after frozen, rights, guards and signatures; a move whose check refuses is treated like one whose guard is false. `csharp-dapper` writes step methods per verb, writes nothing before the checks pass, and hands after-commit work to its outbox. The fixes of two independent reviews are applied. Built in two parts: lifecycles first, then named trigger points for types with no lifecycle, with the bulk-load bypass and the dry run. The owner decided all six questions as recommended: the names stay, part 1 ships alone as 0.17.0 and part 2 as 0.18.0, a check that refuses the signature completing a gate leaves it uncounted and audited as refused, recording writes the full list of trigger points only on request, and the bypass and dry run come in part 2. About 4.5 to 5 hours for part 1, and about 3 hours for part 2. |
| | Richer sign-off: several named signatures, conditions on who must sign, delegation, escalation, separation of duties | yes (the owner) | Released: part 1 in 0.19.0 (#65), part 2 in 0.20.0 (#67). Part 1 holds approvals with one stage: named signatures with their own signers and counts, "Needed when" and "Not by", and the responses sign, reject, ignore and withdraw, with scenarios, simulation and both packs. Part 2 holds several stages in rounds, "Skip unless" on a stage, delegation, reminders and escalation that the running system sends, and signing again with a password | [Design](signoff-design.md), from [section 10](lifecycle-actors-scenarios.md#10-order), step 7. Comes with approval stages (4.3). A gate may hold stages of named signatures, each with its own signers and count, an optional condition on whether it is needed, and who it may not be signed by. Signers answer with sign, reject (a veto that moves nothing), ignore or withdraw; a move on rejection is an ordinary transition whose verb is `reject`. A gate with today's fields keeps today's meaning. Built in two parts, on the owner's answers of 2026-11-08: part 1 (0.19.0) holds one stage; part 2 (0.20.0) adds rounds in series and side by side, a stage skipped by a condition, delegation, reminders and escalation, and signing again with a password. About 3 hours for part 1, and about 2.5 hours for part 2. Checked independently before part 1. The owner confirmed part 2's names on 2026-11-08. |
| | Relations with several types at an end | yes (the owner) | Later | [Section 11](lifecycle-actors-scenarios.md#11-relations-with-several-types-at-an-end). Waits on the stereotype-set question in actors and on an agreed drawing. |
| | Revisions: a revision sequence, revising to a chosen name, and release moving the revision on | yes (the owner) | Later | [Capabilities, part G](lifecycle-capabilities.md#g-revisions). Not the same as sealing migration facts (6.2). |
| | Lifecycles on custom kinds and tables, not only entities | yes (the owner) | Later | [Design, section 12](lifecycle-design.md#12-what-comes-next). Needs the `when` key. |
| | One of several lifecycles per object | yes (the owner) | Later | [Design, section 2 and decision on subjects](lifecycle-design.md#2-what-the-owner-said). Adds a `when` condition to the subject entry; an entry without one keeps its meaning. |

## Phase 1: built in 0.11.0

Designed in `next-phase-1-design.md`. 0.11.0 was published only as previews; its items are released in 0.12.0.

| # | Item | Requested | Status | Notes |
| --- | --- | --- | --- | --- |
| 3.1 | Typed references inside extension properties (`x-ref`), stored as ids, with names converted to ids | yes | Built, 0.11.0 | The editor's forms do not show the new keys yet; the JSON tab edits them. |
| 3.2 | Stereotype groups, `requires`, `conflicts`, `implies` and typed parameters | yes | Built, 0.11.0 | |
| 3.4 | Package membership for every kind, and `dependsOn` layering | yes | Built, 0.11.0 | A package owning database schemas, and model import by package, are in "Not doing". |
| 3.9 | Translatable text in extension properties (`x-translatable`), and a refactor from tags to a typed property | yes | Built, 0.11.0 | Typed `key:value` tags are in "Not doing". |
| 3.11 | `rationale`, `decision` and `status` on every element | yes | Built, 0.11.0 | Templates do not see them on enum members, stereotypes and categories yet. |
| 5.4 | Seeds by name in templates (`seed_named`, `seed_row`) | yes | Built, 0.11.0 | |
| 5.7 | Copy units and closing block markers | yes | Built, 0.11.0 | |
| 5.9 | Template helpers and the null contract of the resolved model | yes | Built, 0.11.0 | |
| 5.10 | Generation by unit or element; baselines from a model snapshot or a folder | yes | Built, 0.11.0 | The trace map is phase 4. Generation at a git ref is in "Not doing". |
| 6.2 | Migration facts in a ledger, `validate --against`, fold, and the ledger table contract (version 1, frozen 2026-11-01) | yes | Built, 0.11.0 | Left: sealing facts into a revision and squashing (deferred), and facts for dialects other than PostgreSQL (MQ4056 until a team needs one). |
| 6.5 | Index and constraint name rules per dialect; literal defaults checked against their type | yes | Built, 0.11.0 | The first part of 6.5; `db check` is phase 3. |
| 6.8 | Index `nulls`, `range` and `multirange` types, SQL objects with environments and run-once | yes | Built, 0.11.0 | |
| 7.1 | Batches by name, with ids minted by the engine | yes | Built, 0.11.0 | |
| 7.5 | MCP answers that fit an agent's context | yes | Built, 0.11.0 | |
| 8.4 | Script rules that scale | yes | Built, 0.11.0 | |

## Phase 2: built in 0.12.0

Milestone D is designed in section 3 of `next-phase-2-design.md` and was merged in pull request #1. The first cut of
operations (section 4.1) was merged in pull request #3, project-defined kinds (4.2) in #4, the small fixes in #5, and
library packs with exports (5.2, 5.3) in #6, tests for packs (5.4) in #7, calculated values (5.5) in #8, declarative
rules (5.6) in #9, and the model diff and impact before apply (6.1, 6.2) in #10. 0.12.0 was released on 2026-11-04.

| # | Item | Requested | Status | Design |
| --- | --- | --- | --- | --- |
| 3.3 | Stereotypes that contribute columns, keys, indexes and checks, inherited by id; redefinition by facet; schema and package default stereotypes; fold into a stereotype | yes | Built, 0.12.0 | [3.1](next-phase-2-design.md#31-stereotypes-that-contribute-structure) |
| 6.4 | Columns typed by an enum, a reference type or a scalar type next to their physical type; `sensitive` on columns | yes | Built, 0.12.0 | [3.2](next-phase-2-design.md#32-columns-typed-by-reference-types-enums-and-scalar-types) |
| 3.5 | Bindings that record their provenance; a drift rule and re-sync; materialize types attributes by reference type | yes | Built, 0.12.0 | [3.3](next-phase-2-design.md#33-bindings-that-record-their-provenance-and-drift) |
| 6.6 | Seed foreign keys by natural key, stable derived ids, rows computed by a script | yes | Built, 0.12.0 | [3.4](next-phase-2-design.md#34-seeds-by-natural-key-stable-ids-and-scripted-rows) |
| 6.7 | Rename, retire and merge reference-type codes and types, each recording a value map | yes | Built, 0.12.0 | [3.5](next-phase-2-design.md#35-reference-type-refactors) |
| 3.6 | Operations, first cut: the kind with `subject`, `level` and `effect`; input members by type or key; outcomes with `completes` and `returns`; errors with `code` and `input`; `fires` and `requires`; `each operation`; csharp-dapper's `operation-contracts` unit | yes | Built, 0.12.0 | [4.1](next-phase-2-design.md#41-operations) |
| 3.6 | Operations, second cut: errors shared from stereotypes (`redefines`, `excludes`, MQ3029), file members and returns, paging, csharp-dapper's `operation-endpoints` unit | yes | Built, 0.12.0 | [4.1](next-phase-2-design.md#41-operations) |
| 3.7 | Project-defined kinds: the `custom` kind with project-declared types in kind files, members that have ids, content validation, translations, `each custom:<type>` and the tools for kind files and rows | yes | Built, 0.12.0 | [4.2](next-phase-2-design.md#42-project-defined-kinds) |
| 5.1 | Library packs: `uses` with version ranges, shared partials and namespaced helpers, libraries in project rule scripts, `pack new --library` | yes | Built, 0.12.0 | [5.2](next-phase-2-design.md#52-library-packs) |
| 5.3 | Packs that read each other's exports: unit `exports`, `exported`, incremental keys that follow them, `pack exports` | yes | Built, 0.12.0 | [5.3](next-phase-2-design.md#53-packs-that-read-each-others-exports) |
| 8.2 | Tests for packs: cases compared byte for byte, helper tests in the sandbox, `pack test` and `test_pack`, a case for each shipped pack | yes | Built, 0.12.0 | [5.4](next-phase-2-design.md#54-tests-for-packs) |
| 5.2 | Values calculated once by project scripts: calculation files per kind, `calculated` in templates, rules and agents, checks after resolution; nothing when a project declares none | yes | Built, 0.12.0 | [5.5](next-phase-2-design.md#55-values-calculated-by-project-scripts) |
| 8.3 | Declarative rules (selector, condition, severity, message) on the resolved model, and per-element suppressions with a required reason, for built-in rules too | yes | Built, 0.12.0 | [5.6](next-phase-2-design.md#56-declarative-rules) |
| 7.2 | Impact before apply: a batch's dry run lists referrers, diagnostics added and removed, reached databases and, per pack, the touched units and files | yes | Built, 0.12.0 | [6.1](next-phase-2-design.md#61-impact-before-apply) |
| 7.3 | A model diff between snapshots, folders and the working model, graded and grouped by package, with a Markdown form | yes | Built, 0.12.0 | [6.2](next-phase-2-design.md#62-a-model-diff-between-two-models) |

## Phase 3: built in 0.12.0

The design, `next-phase-3-design.md`, was approved on 2026-11-03 and merged in pull request #14; tasks H1 to H14 build
it. 0.12.0 was released on 2026-11-04.

| # | Item | Requested | Status | Notes |
| --- | --- | --- | --- | --- |
| 3.8 | One expression language for checks, validations, derived values and guards, lowered to SQL and JavaScript | yes | Built, 0.12.0 | [3.1](next-phase-3-design.md#31-one-expression-language-for-checks-validations-derived-values-and-guards). PostgreSQL only (MQ4056 elsewhere); actions stay JavaScript until operations replace them (4.6). The editor's text box for expressions is with the other screens below. |

## Left over from built items

| Item | Requested | Status | Notes |
| --- | --- | --- | --- |
| Editor screens for the items above | yes | Later | Screens for operations and project-defined kinds come first, since the JSON form handles their nested members worst. For custom kinds, in this order: an explorer section per kind, one form built from a kind's schema with a link picker, then custom properties on process nodes. Each item ships in the engine, the CLI and MCP first; the generic JSON form edits what has no screen yet. Each design section lists its screens under "Editor follow-ups". |
| Sign-off part 2: the simulation panel's Start row clips one select, and the Verbs tab doesn't count `delegate`'s uses | no | Later | Found in 0.20.0's browser passes. The owner said on 2026-11-08 they can wait; neither changes a name. |
| Sealing facts into a revision, and squashing (6.2) | yes | Later | Built with the live planner (6.1): squashing must check each environment's ledger, which only 6.1 reads. Facts stay open until then. |
| Rationale, decision and status in templates for enum members, stereotypes and categories (3.11) | yes | Built, 0.12.0 | |
| Expression checks on a table an entity's mapping projects, naming the entity's columns (3.8) | yes | Built, 0.12.1 | An overlay's tree check names the entity's attributes and value object members by attribute name, since validation cannot know the column names the conventions give them; resolution lowers it through the projected table's columns, and an attribute the table does not store is MQ4008 at the check. |

## Phase 2: not built yet

Designed in sections 4 to 6 of `next-phase-2-design.md`. Every *build next* item of phase 2 is built; what follows is
*later*.

| # | Item | Requested | Status | Tasks | Design | Notes |
| --- | --- | --- | --- | --- | --- | --- |
| 5.8 | DDL as structured steps from the engine | yes | Later | 18 | [5.1](next-phase-2-design.md#51-ddl-as-structured-steps-from-the-engine) | The same output; built with the live planner (6.1), the only item it pays back, never on its own. |
| 6.9 | Declared environments | no | Later | 4 | [6.3](next-phase-2-design.md#63-declared-environments) | Added by the roadmap's review; mostly preparation for reading live databases. |

## Phase 3: not designed yet

| # | Item | Requested | Status | Notes |
| --- | --- | --- | --- | --- |
| 3.12 | Projections | no | Later | Added by the roadmap's review; operation payloads and scenarios use them. Needs 3.8. |
| 5.5 | The query kind for hand-written SQL, and CTEs in the structured query | yes | Later | For database-first teams. Written tables and result columns are declared, not read from the SQL text. Includes raw SQL as a binding's read source (from the bindings work below). |
| 6.1 | A live PostgreSQL planner: `db plan`, `db apply`, `db adopt` | yes | Later | Large, PostgreSQL only. Built when more than one team manages databases with Maquettiste. |
| 6.5 | `db check`: generated DDL run against a scratch database | yes | Later | The second part of 6.5. A SQL parser is decided after `db check` shows how much is left. |
| 6.3 | SQL objects that revert | yes | Later | Built with the live planner (6.1), which runs the revert statements. |
| 6.3 | Typed triggers and row-level security policies | yes | Later | SQL objects cover these today. Typed grants are in "Not doing". |
| 7.6 | Conventions tied to the rules that check them; where each resolved value came from | yes | Later | Helps agents explain answers. |
| 4.3 | Approval gates with stages | yes | Built, 0.20.0 (#67) | Built with richer sign-off part 2: [design](signoff-design.md) (rounds in series and side by side, and a stage skipped by a condition). |
| 4.4 | Timers from elements, task contracts, editable fields per state | yes | Later | Close to a team's workflow engine; after the lifecycle (4.1) and shared guards (4.2). |
| 7.4 | A merge driver for model files | yes | Later | An optional integration for teams that keep models in git; the product never needs it. The model diff (7.3) covers review. |

## Phase 4: not designed yet

| # | Item | Requested | Status | Notes |
| --- | --- | --- | --- | --- |
| 3.13 | Views of the model: saved criteria that select elements | no | Later | Added by the roadmap's review. It was to be part of the documentation pack (5.6), which is not being built for now. |
| 5.10 | The trace map from generated lines to element ids | yes | Later | The second part of 5.10. |
| 4.6 | Charts joined to operations; the process runtime generated from them | yes | Later | Needs operations (3.6) and the expression language (3.8). The engine runs no process; packs generate the runtime. |
| 4.5 | A structure hash per process, and records in flight when a process changes | yes | Later | Needs the lifecycle (4.1); the state map is a step in the migration ledger (6.2). |
| 8.1 | Scenarios run against the running system | yes | Later | Needs operations (3.6) and projections (3.12); the team writes the test template. |
| 8.5 | Upgrade tests from history | yes | Covered | No separate feature: build the previous version from a folder or snapshot, migrate, run the suite. The guide page "Upgrade tests" walks through it. |

## Not doing

Decided on 2026-10-30 unless a row says otherwise, each with what to use instead.

| Not doing | Instead | Requested |
| --- | --- | --- |
| A separate trait (mixin) kind | Stereotypes that contribute structure, inherited by id (3.3) | yes |
| A stored by-name binding mode | Map by name as an authoring action, with provenance, a drift rule and re-sync (3.5) | yes |
| Relations inferred from foreign keys when the model resolves | Materialize writes them; bindings stay explicit (3.5) | yes |
| A package owning database schemas | Tables in packages; the database routes packages to schemas (3.4) | yes |
| Model import by package now | Cross-model imports, with phase C of the model service (below) | yes |
| New engine kinds per project concept | One `custom` kind with a project schema per type (3.7) | yes |
| A built-in message kind | A custom kind with translatable text (3.7, 3.9) | yes |
| Typed `key:value` tags | Properties through extension schemas, and the tags-to-property refactor (3.9) | yes |
| Open issues stored on elements | A tracker link in `decision` (3.11) | yes |
| Engine emitters for C# or other languages | The engine lowers expressions to SQL and JavaScript; packs render other languages from the typed tree (3.8) | yes |
| Connectors to outside requirement tools | A batch written by a script (3.10) | no |
| A built-in requirement kind, with `fulfills`, `verifies` and a coverage report (3.10, design 4.3) | A project's own custom kind, typed links, declared rules and a pack. Decided 2026-11-05; it comes back only if a team needs what a custom kind cannot give | yes |
| CSV import for custom kinds | A batch written by a script. Decided 2026-11-05 | no |
| Business calendars and escalation chains in the engine | Custom kinds (3.7); an escalation is a chain of timers | yes |
| A version element for processes | A structure hash per process, and a state map in the ledger (4.5) | yes |
| The engine running processes | Packs generate the runtime from the chart (4.6) | no |
| Documentation or SysML v2 export in the engine | A team's own pack, written from the resolved model | yes |
| Model documentation as an example pack (5.6), for now | A team's own pack; the process-docs pack covers process pages. Decided 2026-11-04 | yes |
| SysML v2 export as an example pack (9), for now | A team's own pack, written from the resolved model. Decided 2026-11-04 | yes |
| SysML v2 import | None planned | yes |
| Migration facts stored on elements | One fact file each in the ledger, projected onto resolved elements (6.2) | yes |
| Element ids in database object comments | The ledger table (6.1, 6.2) | yes |
| Typed grants; roles as product elements | Grants as SQL objects that revert; roles as names or a custom kind (6.3) | yes |
| Seed rows stored by column name | Rows keyed by column id; names accepted on every input (6.6) | yes |
| Reading version control: a git provider, `generate --at <ref>`, baselines and diffs from git refs | A model snapshot or a folder; Maquettiste also runs hosted, with no repository (5.10, 7.3) | yes |
| A physical snapshot committed with each revision | One hash per object; baselines from a model snapshot or a folder (6.2). The committed physical snapshot stays as an option. | no |
| Prompting for migration facts | One classifier records facts on every write; only intent is left pending (6.2) | no |
| Redefinition by restating a whole member | Redefinition by facet, keeping the inherited id (3.3) | no |
| JavaScript guards on operations | Operation guards in the expression language (3.8) | no |

## Other open work

Queued at 0.7.1, before the roadmap, from the owner's reviews; none of it was requested by a team that uses
Maquettiste. The old item "Migrations as recorded facts" became the migration ledger (6.2) and is built.

### Validation speed (fixed in 0.12.0)

The gate 3 bench line "MQ90xx-MQ92xx, whole model, added to validate" (400 ms budget) ran between 400 and 850 ms since
0.6.0. The cause: validation compiled every process expression into a shared cache of 512 entries only to find parse
errors, so with 1,000 processes the cache cleared itself on every run and its locks serialized the validation threads.
Validation now parses without compiling; the line measures 0 to 63 ms.

Scenario replay compiled through the same cache; the compiled expressions now travel with the process object, so a
model of any size compiles each process once (replay on the bench: 2,377 ms to 2,105 ms, 3,000 ms budget). Left: check
the editor's validation on the 5,000-entity scale dataset (`explorer-redesign.md` §4.5).

### Bindings, the rest (`binding` phases B and C)

Built in 0.6.0: an entity binds to a table, view or query of a database, with constant columns, a field map, write
columns and delete by key or soft delete; bulk auto-map, create tables, remove bindings, entities from tables.
Milestone D added provenance, drift and re-sync (3.5).

Left:

- **Raw SQL as a read source.** A binding reads from a hand-written SQL statement, not only a table, view or query.
  Moved to the query kind (5.5).
- **Split.** One entity read from and written to several tables.
- **Routine bindings.** Read, write and delete through stored routines.
- **Converters** as named, reusable elements on a field map.
- **The unmapped-attribute rule in the engine (built).** MQ4058 (a stored attribute of a bound entity that no field
  maps) is a binding rule of the engine, as the editor's field map reports it.

Decided: the database knows no entities; entities own their bindings; writes of attribute-per-row (EAV) tables are out.

### Model service: persistence providers, snapshots, a model library (snapshots built in 0.8.0)

The model stops being only "the JSON files in this checkout" and becomes something an instance serves; generation stays
something a checkout does. One central instance can hold many models (title, entity and table counts, last updated),
each with snapshots (name, date) that can be opened read-only, restored, compared, exported and imported.

**Built (2026-10-05, phase A, engine, server and editor).** Snapshots as deterministic zip archives under
`.maquettiste/model-snapshots/`, with take, list, rename and publish, delete, open read-only, compare, restore, export
and import, over the API, the CLI, MCP and the editor. `snapshots.md` and erratum E45 record them.

Left in phase A: instance capabilities (`model: read`, `generation: off`, `packs: read|off`); generating from a snapshot
with its packs.

**Snapshots in object storage (asked 2026-10-05, on hold).** Keep snapshot archives in an S3-compatible bucket instead
of the model folder. The credentials never go in `maquettiste.json` (it is in the repository): they are host site
variables, the secret key marked secret (encrypted by the host, never sent back), and environment variables for the CLI.
Settings › Snapshots shows where snapshots go and a connection test, never the key. Every snapshot operation works the
same; opening one downloads its archive once into the cache; compare reads the two small index entries first. Proposed,
to confirm when it is picked up: the bucket is the only store when configured, with a one-time "Upload local snapshots";
configured per instance (fits the central service), not per project.

Phases B (an http provider, multi-model service mode, the library picker, `generate --model <url>`) and C (a database
provider with versions as history, accounts and single sign-on, per-model permissions, cross-model imports) are
unchanged.

Decided: one central service, many models, one URL; working models are visible, and a snapshot can be marked published;
packs travel with a snapshot only when asked; compare is in phase A; snapshots are zip files on the file provider.

Open questions:

1. Do developers need a local copy of the model, or is `generate --model <url>` enough (and what about offline work)?
2. Auth for the first cut: is one shared token per instance enough until accounts arrive?
3. Should cross-model imports (shared vocabularies) be designed now, since they shape the archive format (it can carry
   an `imports` list later)?
4. Should anything in a working model be hidden from a reviewer?
5. Should a project keep its snapshots out of version control (the product writes no ignore file), or are they model
   history to keep?

### The assistant, next steps

Built in 0.7.0: the sidebar reads the whole model and proposes model changes as reviewed diffs.

- Let it propose pack and extension file edits, reviewed the same way.
- Let it run a plan and show the diff (apply stays a user action).
- Drafting a template from an example output (the spec's last assist task) waits on pack writes.

### Smaller items

- **The store rescans every file per request.** Reading the model snapshot stats every model file each time (about
  150 ms on 40,000 files); a file watcher or a cheaper change check would remove most of a warm preview's time.
- **Oracle DDL against a server.** Every other dialect ran against a real server in 0.6.0; Oracle did not.
- **Other lifecycle capabilities that may come after the items above.** Per-state access, required fields on entry and
  exit (partly 4.4), a reject path, observers and acknowledgers. Listed as directions to confirm in
  [lifecycle-capabilities.md](lifecycle-capabilities.md#what-this-changes-for-item-41).
- **Lock files.** The project-reference ranges in `packages.lock.json` still read `1.0.0-alpha.1` (harmless metadata
  from the first restore). Refreshing them would mean a release script that re-evaluates the locks at every version
  bump.

### Editor approachability (waits on a walkthrough with the owner)

- A reorganization for non-experts: onboarding, naming, empty states, a clearer first screen. Collect confusion points
  in a live walkthrough first.
- **Found by a browser walkthrough (2026-11-06), older than 4.1:**
  - Large charts open at about 5% zoom on first view.
  - Pickers, favorites and some messages show internal names or raw ids where display names belong; a reference error names
    an id rather than the element.
  - A Problems row reveals its element only with Enter, not with a click or double-click.
  - Clicking a scenario row in the process editor doesn't select it in the inspector.
  - The dark theme was not checked properly; check it in the walkthrough.
- **The Processes explorer is too flat (the owner, 2026-11-05). Built for 0.13.1 in #41.** Today each domain lists every process in one list
  (lifecycles, orchestrations and libraries mixed), and Actors is one flat list after the domains. Proposed:
  - In each domain, processes grouped by use: **Lifecycles**, **Orchestrations** and **Libraries**. A lifecycle shows
    the entities it governs beside its name.
  - Under a process: **States**, **Events**, **Guards and actions** and **Gates**, then **Scenarios**, failed ones
    first, each with its status.
  - **Actors** grouped by type: **People**, **Roles** and **Systems**. "Systems" is the folder's label only: the
    actor type stays `external-system` in files, and its label elsewhere stays "external system". The system itself,
    which a later item makes the actor of automatic moves, is labeled **Automatic**, so the two aren't confused. Once
    actors have membership, a role lists its members, and scenario personas sit with their scenarios.
  - In the Domain model explorer, an entity lists the lifecycles that govern it. An actor's row says where it acts.
  - **Built right after 4.1 (the owner, 2026-11-05).**
