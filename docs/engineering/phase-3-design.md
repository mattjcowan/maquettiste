# Phase 3 design: processes, actors and scenarios

Phase 3 is SPEC.md Section 21's "3 Processes", closed by gate 3. This document answers `phase-3-brief.md` with the
owner's decisions of 2026-09-29 and specifies the model, the rules, the engine, import and export, the editor, the pack
units, the gate and the rounds. It follows the contracts of `engine-design.md` (schemas are the source of key order and
defaults, canonical form, the sandbox) and the house rules of the phase 2 documents: the engine models intent and
synthesizes no persistence, the product reserves no vocabulary, every rule has an MQ id, a catalog entry and a test, and
`docs/api/openapi.yaml` and `schemas/v1/` are the source of truth. Section 10 lists the SPEC amendments (E27 onward).

## 1. Scope and decisions

| # | Question (brief) | Decision (owner, 2026-09-29) | Consequence here |
| --- | --- | --- | --- |
| 1 | Runtime | The engine generates definitions and handler stubs only; the example packs ship a small generated interpreter as a template output | The engine's interpreter (§4) serves validation, simulation, scenarios and test derivation; packs emit their own (§7) |
| 2 | Gate 3 subjects | A sales-order lifecycle and a purchase-approval orchestration, test fixtures under `tests/fixtures/models`, both with parallel states and a gate | `tests/fixtures/models/processes/` (§8) |
| 3 | Simulation-derived tests | Event sequences recorded in the simulation panel, saved as scenarios beside the process, turned into test files by a pack; gate 3 in C# (`csharp-dapper`), the TypeScript pack second | Scenario element kind (§2.4), `each scenario` scope (§7.1) |
| 4 | Enum binding | The process owns the state list; a bound enum attribute that drifts is an error with a "Sync enum from process" quick fix (an operation) | MQ9203 and the `sync-enum` operation (§3, §4.4) |
| 5 | Gates and people | Signers are actors; the audit record shape is modelled; where it is stored is a template or mapping decision | §2.3 Gates; §7.2 audit units |
| 6 | Guard and action expressions | JavaScript in the existing sandbox, with a deadline, deterministic; query predicates share it later | §4.2 |
| 7 | Import | Unknown XState config kept as opaque extension data with a warning; inline functions become named stubs with a warning | §5, MQ9401, MQ9402 |
| 8 | Layout | A layered layout library in the editor; positions saved in the diagram file as the entity canvas does | §6.3, §6.5 |
| 9 | Events | Process-local in phase 3; domain events become an element kind in phase 4 | `events[]` inside the process (§2.3) |
| 10 | Leftovers | Phase 3 starts now; L2 to L5 run as a side track between rounds; the owner applies the SPEC errata | Not in the rounds of §9 |
| A | Actors | An element kind in phase 3: `type` person, role or external system; a persona is an actor with a stereotype carrying goals; processes bind events and gate signers to actors; phase 4 permissions bind to roles | `actor.json` (§2.5) |
| S | Scenarios | An element kind in phase 3: a recorded event sequence with the actor, the expected states after each step and the outcome; the source of simulation-derived tests and documentation walk-throughs | `scenario.json` (§2.4) |
| O | Operations | Stay phase 4; transition actions are named so they become operations later without renaming | §2.3 Actions |
| C | Custom code | Room for hand-written code in two shapes: user-code regions (`regions`) and partial classes or companion modules (`pair`) | §7.3 |
| D | Dispatch | Typed event and command contracts, a generated handler registry, pipeline behaviours, handler stubs in `pair`; cross-process delivery stays outside, with an outbox hand-off as the boundary | §7.4 |

Out of scope for phase 3: domain events as elements, operations, permissions, SCXML export, a runtime in the engine, and
any table the engine would create for process instances.

## 2. Model

### 2.1 Element kinds and files

`ElementKind` gains `Process`, `Actor` and `Scenario` (engine-design.md §2.2 table):

| `kind` | CLR type | Folder under `.maquettiste/` | Schema file | Package |
| --- | --- | --- | --- | --- |
| `process` | `Process` | `model/processes/` | `process.json` | optional (its domain) |
| `actor` | `Actor` | `model/actors/` | `actor.json` | none (SPEC §5: actors are not in a domain) |
| `scenario` | `Scenario` | `model/scenarios/<process stem>/` | `scenario.json` | its process's |

File names follow engine-design.md §2.2: the kebab-case name, suffixed on a collision. A scenario folder is named after
its process's file stem, as a seed folder is named after its target (suffixed `-<last 6 of the process id>` on a
collision). A scenario belongs to its process: deleting the process deletes its scenarios in the same save, as
reference-types-seeds-localization.md §2.7 does for seeds, and the delete check does not count that owning reference
(errata E16's rule). The entity's `lifecycle` field arrives (errata E3 retired): `entity.json` gains `lifecycle`, an
optional id of a process, placed after `base` in `x-order`.

Sub-elements carry ULIDs unique across the model, like attributes and members (engine-design.md §2.1). New index kinds
for `[ElementRef(IndexKinds = …)]`: `state`, `transition`, `event`, `guard`, `action`, `invoke`, `gate`, `meaning`,
`step`. Index rows: a process row gains `use`, `subject` and `stateCount` (all from its own file); an actor row gains
`actorType`; a scenario row gains `process` and `stepCount`. Counts derived from other files never go on a row (the RT
§3.8 rule). The index format constant moves to its next value because the row shape changes.

### 2.2 `process.json`

Top level, in `x-order`:

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| `$schema`, `kind`, `id`, `name`, `displayName`, `pluralName` | as `entity.json` | | `kind` is `const: "process"`; `name` an identifier |
| `package` | id → Package | none | the domain |
| `use` | `"lifecycle"` \| `"orchestration"` | `"orchestration"` | |
| `subject` | id → Entity | none | required when `use` is `lifecycle` (MQ9201) |
| `boundAttribute` | id → attribute | none | an enum-typed attribute of the subject; lifecycle only (MQ9202) |
| `description`, `stereotypes`, `tags`, `category` | common | | |
| `context` | attribute[] (`common.json#/$defs/attribute`), `x-sort: order` | `[]` | the running instance's data |
| `events` | event[] | `[]` | process-local (decision 9) |
| `guards` | guard[] | `[]` | |
| `actions` | action[] | `[]` | |
| `states` | state[] | | required, `minItems: 1`; the root's children, in document order |
| `initial` | id → state | first root child | a direct child of the root (MQ9001) |
| `transitions` | transition[] | `[]` | in priority order |
| `properties`, `generation`, `source` | common | | |

The root is implicit: the process is the root compound state and its `states` are the root's children. The root's
initial child is the process-level `initial`, the same key a nested compound state uses, so one rule (MQ9001) covers
both.

**State** (`states[]`, recursive):

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| `id` | id | | required |
| `name` | identifier | | required; unique among siblings |
| `displayName` | string | | localizable |
| `type` | `atomic` \| `compound` \| `parallel` \| `final` \| `history` \| `choice` | `atomic` | |
| `initial` | id → state | first child | compound only; a direct child (MQ9001) |
| `history` | `shallow` \| `deep` | `shallow` | history only |
| `defaultTarget` | id → state | parent's initial | history only: entered when no history is recorded |
| `entry`, `exit` | id list → action | `[]` | run in list order |
| `invoke` | invoke[] | `[]` | started on entry, cancelled on exit |
| `states` | state[] | `[]` | children in document order; regions of a parallel state |
| `description`, `stereotypes`, `properties` | common | | |

**Transition** (`transitions[]`):

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| `id` | id | | required |
| `displayName` | string | | the edge label when set ("changes requested") |
| `source` | id → state | | required |
| `trigger` | `event` \| `after` \| `done` \| `always` \| `invoke-done` \| `invoke-error` | `event` | `done`: the source compound reached a final child, or every region of the source parallel did (`onDone`); `always`: eventless |
| `event` | id → event | | required when `trigger` is `event` |
| `after` | ISO 8601 duration | | required when `trigger` is `after`; positive (MQ9008) |
| `invoke` | id → invoke | | required for `invoke-done` and `invoke-error`; an invoke of the source |
| `guard` | id → guard | none | unguarded when absent |
| `targets` | id list → state | `[]` | none: targetless (no exit or entry); several: one per orthogonal region |
| `actions` | id list → action | `[]` | |
| `external` | boolean | `false` | `true` exits and re-enters the source when a target is its descendant (statechart external transition) |
| `gate` | gate | none | §2.3 |
| `description`, `stereotypes`, `properties` | common | | |

Among transitions with one source and one trigger (and one event, duration or invoke), array order is priority: the
first whose guard holds is taken, which is also how a choice state's `else` works (the last, unguarded transition).

### 2.3 Events, guards, actions, invokes, gates, context

**Event**: `id`, `name` (identifier, unique in the process), `displayName`, `payload` (attribute[], `x-sort: order`,
the typed payload that generated commands and endpoints carry), `actors` (id list → Actor; empty means any actor),
`description`, `stereotypes`, `properties`. Events are process-local in phase 3; phase 4 promotes an event to an element
kind by moving it out of the process with its id unchanged, so references survive.

**Guard**: `id`, `name`, `displayName`, `expression` (optional JavaScript expression over `context` and `event`,
returning a boolean), `description`. **Action**: `id`, `name`, `displayName`, `expression` (optional JavaScript
expression returning an object of context updates), `raises` (id list → event: internal events queued after the action,
declared in the model so they are known without running code), `description`. A guard or action without an expression is
a named stub: packs emit a handler for it and the simulation asks for its result (§6.4). Action names are identifiers
unique in the process and are the names phase 4 gives the operations they become ("Promote to operation" keeps the name
and the id), so a pack that names handlers after actions keeps its file names across the promotion.

**Invoke** (on a state): `id`, `name`, `displayName`, `type` (`process` \| `service` \| `human-task`), `process` (id →
Process, for `process`), `actors` (id list → Actor, for `human-task`: who may complete it), `description`. A sub-process
runs to its final state and yields `invoke-done`; a service task and a human task wait for `invoke-done` or
`invoke-error` from outside (the generated service, or the assigned actor).

**Gate** (on a transition whose trigger is `event`): the transition fires only on the occurrence of its event that
completes the gate; earlier occurrences each record one signature and change no state.

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `id`, `name`, `displayName` | | | `name` an identifier unique in the process |
| `required` | integer ≥ 1 | `1` | N: signatures needed |
| `signers` | id list → Actor, `minItems: 1` | | M: the actors whose members may sign |
| `requiredActors` | id list → Actor | `[]` | at least one signature from each (a subset of `signers`, MQ9102) |
| `allowRepeatSigner` | boolean | `false` | whether one signer (the same person identity) may count twice |
| `reasonRequired` | boolean | `false` | whether each signature must carry a reason |
| `meanings` | meaning[], `minItems: 1` | | what a signature means: `id`, `name`, `displayName`, `description` ("Reviewed as finance controller") |
| `auditAttributes` | attribute[], `x-sort: order` | `[]` | extra fields recorded with each signature |
| `description` | common | | |

Signatures belong to the source state's active interval: exiting the source (another transition, a rejection) discards
them. **Audit record shape** (decision 5): the engine models one record per signature attempt and per gate outcome, and
the resolved model exposes it as `gate.audit` so packs render the same shape; where it is stored is a template or mapping
choice (§7.2):

| Field | Type | Meaning |
| --- | --- | --- |
| `instance` | string | the process instance's identity, supplied by the host |
| `process`, `gate`, `transition` | id | the model elements |
| `sequence` | int64 | order within the instance |
| `signer` | string | the signing person's identity, supplied by the host (never an actor id) |
| `actor` | id → Actor | the actor the signer signs as (one of `signers`) |
| `meaning` | id → meaning | |
| `reason` | text | required when `reasonRequired` |
| `at` | datetimeoffset | from the host's clock |
| `outcome` | `signed` \| `completed` \| `refused` \| `discarded` | `refused` records an attempt the gate did not count (not a signer, repeat signer, missing reason) |
| (auditAttributes) | as declared | |

**Context** uses the attribute model unchanged (types, enums, value objects, reference types, required, defaults), so
the resolved model, the attribute grid and packs' type maps apply as they do to entities.

**Lifecycle binding** (decision 4). The bound states are the root's children that are not `history` or `choice`, in
document order; a nested active state counts as its root-level ancestor. The bound attribute's enum must have exactly
those names as members, in that order; drift is MQ9203, fixed by the `sync-enum` operation (§4.4), which adds, removes and
reorders members and keeps the ids, codes and descriptions of members it keeps. The subject entity's `lifecycle` names
the process; both sides are checked (MQ9201).

### 2.4 `scenario.json`

`x-order`: `$schema`, `kind`, `id`, `name`, `displayName`, `process` (id → Process, required), `description`,
`stereotypes`, `tags`, `category`, `start`, `steps`, `outcome`, `properties`, `generation`, `source`.

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| `start.context` | map attribute id → value | `{}` | initial context values over the attribute defaults |
| `start.at` | ISO 8601 instant | `"2000-01-01T00:00:00Z"` | the simulated clock's start (the sandbox's fixed instant) |
| `steps` | step[], `minItems: 1` | | in order |
| `outcome` | `final` \| `active` | `active` | whether the root is final after the last step |

**Step**, in `x-order`: `id`, `input` (`event` \| `time` \| `invoke-done` \| `invoke-error`, default `event`), `event` (id
→ event), `invoke` (id → invoke), `after` (duration, for `time`: advance the clock), `actor` (id → Actor), `signer`
(string identity, for gated events), `meaning` (id → meaning), `reason` (string), `payload` (map attribute id → value),
`assume` (map guard id → boolean: results for guards without an expression), `expect`, `description`. `expect` has
`accepted` (boolean, default `true`; `false` records a refusal, so negative paths are tested), `states` (id list → state:
the active atomic states in document order) and `context` (map attribute id → value: only the attributes the step
changed). Payload and context maps are keyed by attribute id, as every reference is; the editor and the generated tests
show names. Maps sort ordinal as every free-form map does.

### 2.5 `actor.json`

`x-order`: `$schema`, `kind`, `id`, `name`, `displayName`, `pluralName`, `type` (`person` \| `role` \|
`external-system`, required), `description`, `stereotypes`, `tags`, `category`, `properties`, `generation`, `source`. A
persona is an actor (usually `person`) carrying a project-defined stereotype whose extension schema adds `goals` (an
array of strings) and whatever else the project wants: the product defines no persona stereotype, and the fixture
declares its own (§8.1). Phase 4 binds permissions to actors of type `role`.

### 2.6 Localization, classification, names, canonical form

- **Localizable nodes** (RT §3.1's rule, no list change needed): process, state, transition (`displayName` only, the edge
  label), event, guard, action, invoke, gate, gate meaning, actor, scenario, scenario step (`description` only), and
  attributes of context, payloads and audit fields. Shards: a process and its scenarios go to the process's package
  shard; an actor to `_root`.
- **Classification**: tags, category and stereotypes on processes, actors and scenarios; stereotypes and properties on
  states, transitions and events, so templates can branch on project marks. `extension.json`'s applicable kinds gain
  `process`, `actor`, `scenario`, `state`, `transition` and `event`.
- **Names**: states are unique among siblings and addressed by path (`Fulfilment.Shipping.Packed`); events, guards,
  actions, invokes and gates are unique within the process; actors across actors; scenarios within their process. MQ3001
  (duplicate name) and MQ3018 (invalid name) apply as for other kinds.
- **Canonical form**: arrays keep their order because order is meaning: `states` is document order (entry order,
  priority, the enum member order), `transitions` is priority, `events`, `guards`, `actions` and `invoke` are display
  order, `steps` is time. Attribute arrays (`context`, `payload`, `auditAttributes`) carry `x-sort: order`. Maps sort
  ordinal. Defaults are omitted (`type: atomic`, `trigger: event`, `external: false`, `required: 1`). Every object with
  `properties` has an `x-order` (`SchemaConsistencyTests`).

### 2.7 Process diagrams

`diagram.json` gains `process` (id → Process, after `package`). A diagram with `process` set is that process's
statechart: its `members[].element` are states of the process (MQ9016 otherwise), `x` and `y` are relative to the
parent state's container, and `width` and `height` are set on compound and parallel states. `collapsed` hides a
container's children. One diagram per process is created the first time the chart is laid out and saved; its name
defaults to the process name. Edge routes are not saved in phase 3: they are recomputed from the positions.

## 3. Validation

Processes take the MQ9xxx family. `RuleCatalog` gains six family labels: MQ90xx Processes, MQ91xx Actors and gates,
MQ92xx Lifecycles, MQ93xx Scenarios, MQ94xx Process import and export, MQ95xx Process expressions and simulation. Every
rule below gets a catalog entry, a positive and a negative test in `tests/Maquettiste.Engine.Tests/Processes/`, and a row
in the user guide. Reachability and dead ends are computed on the chart's structure, ignoring guards (a guard can only
remove paths). Existing rules apply unchanged: MQ2001 and MQ2002 for dangling or wrong-kind references (an event's actor
that is not an actor), MQ3001 and MQ3018 for names, MQ5001 for properties.

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ9001 | error | Missing or invalid initial: a compound state (or the root) whose `initial` is absent with no children, or not a direct child | Set initial to the first child |
| MQ9002 | error | A state's `type` contradicts its children: atomic, final, history or choice with children; compound or parallel without | |
| MQ9003 | warning | Unreachable state: no path from the initial configuration through any transition, history default or invoke | |
| MQ9004 | warning | Dead end: a non-final atomic state with no outgoing transition on itself or an ancestor and no invoke | |
| MQ9005 | info | No final state is reachable: the process never completes (normal for some lifecycles) | |
| MQ9006 | warning | Overlapping guards on one event: for one source and one trigger, two unguarded transitions, an unguarded transition before a guarded one (the guarded one never fires), or one guard used twice | |
| MQ9007 | error | Invalid targets: a state of another process, two targets in one region, or several targets not in orthogonal regions of one parallel ancestor | |
| MQ9008 | error | Trigger fields inconsistent: `event` without an event, `after` without a positive ISO 8601 duration, `done` on an atomic source, `invoke-done` or `invoke-error` naming no invoke of the source | |
| MQ9009 | error | An eventless cycle with no guard on it: the macrostep would not end | |
| MQ9010 | error | A choice state whose outgoing transitions are not all `always`, or whose last one is guarded (no default) | |
| MQ9011 | error | A history state outside a compound parent, or a `defaultTarget` that is not a descendant of that parent | |
| MQ9012 | error | A final state with outgoing transitions, invokes or children | |
| MQ9013 | warning | A declared event, guard, action or invoke that nothing uses | Remove it |
| MQ9014 | error | A reference to a sub-element of another process (a guard, action, event, invoke or state) | |
| MQ9015 | error | Invokes: a sub-process invoke cycle (a process invoking itself through others), a `process` invoke without `process`, a human task without actors | |
| MQ9016 | warning | A process diagram member that is not a state of the diagram's process | Remove the member |
| MQ9017 | warning | A parallel state with one region | |
| MQ9018 | warning | A `done` transition whose source has no reachable final descendant (it can never fire) | |
| MQ9101 | error | A gate's `required` above what its signers can give (only `person` actors listed and no repeat signing) | |
| MQ9102 | error | A gate's `requiredActors` not all in `signers` | Add them to signers |
| MQ9103 | error | A gate on a transition whose trigger is not `event`, or two gates on one source and event | |
| MQ9104 | error | A gate without meanings | |
| MQ9105 | warning | A gate signer that may not raise the gate's event (the event restricts `actors` and leaves the signer out) | Add the signer to the event's actors |
| MQ9106 | info | An actor no process references | |
| MQ9201 | error | Lifecycle inconsistent: `use: lifecycle` without `subject`; the subject's `lifecycle` does not name this process; an entity's `lifecycle` names an orchestration or a process whose subject is another entity | Set lifecycle on the subject |
| MQ9202 | error | `boundAttribute` not a single-valued, enum-typed attribute of the subject (own, inherited or stereotype-virtual), or set on an orchestration | |
| MQ9203 | error | Enum drift: the bound enum's members differ from the bound states (missing, extra or out of order) | Sync enum from process |
| MQ9204 | warning | The bound enum is used by other attributes or processes: syncing changes them too | |
| MQ9205 | warning | The bound attribute's `default` is not the initial root-level state | Set default |
| MQ9301 | error | A step is not a valid transition: refused (no transition, guard false, actor not allowed, gate refusal) while `expect.accepted` is true, or accepted while it is false | |
| MQ9302 | error | The active states after a step differ from `expect.states` | Update expectations from replay |
| MQ9303 | error | The context after a step differs from `expect.context` | Update expectations from replay |
| MQ9304 | error | The outcome after the last step differs from `outcome` | Update expectations from replay |
| MQ9305 | error | Step fields inconsistent: an unknown event, a `time` step without `after`, an invoke that is not pending, a payload value that does not fit the event's payload attribute | |
| MQ9306 | warning | A guard without an expression is evaluated and the step has no `assume` for it; replay stops at that step | |
| MQ9401 | warning | Unknown XState config kept as opaque extension data | |
| MQ9402 | warning | An inline function turned into a named stub | |
| MQ9403 | error | Import input is not a statechart config: invalid JSON, no `states`, or a shape the importer refuses | |
| MQ9404 | warning | An XState feature mapped approximately or dropped (§5.2) | |
| MQ9405 | error | Re-import conflict: an id carried in `meta` names an element of another kind or another process | |
| MQ9406 | info | Export wrote model data with no XState equivalent into `meta` | |
| MQ9501 | error | A guard or action expression does not parse | |
| MQ9502 | error | An expression threw during simulation, replay or verification | |
| MQ9503 | error | An expression exceeded the sandbox deadline or a limit | |
| MQ9504 | warning | A guard returned a value that is not a boolean (treated as false) | |
| MQ9505 | warning | An action's result names an unknown context attribute or a value of the wrong type (ignored) | |
| MQ9506 | warning | Two transitions enabled for one event from one source at run time (the first in priority order is taken) | |
| MQ9507 | error | A macrostep exceeded 1,000 microsteps | |

**As built (P1), where the table left a choice.** One finding per fact: a compound or parallel state without children, and
a final state with children, are MQ9002 (not MQ9001 or MQ9012); MQ9012 covers a final state's outgoing transitions and
invokes; MQ9001 also reports an `initial` set on a state that is not compound. A target in another process is MQ9007; every
other reference to another process's state, event, guard, action or invoke is MQ9014. MQ9003 reports only the topmost
unreachable state of a subtree (with the count below it) and includes pseudo-states: a history state no transition targets is
unreachable (the `process-basics` fixture's choice default now enters `Review` through its history state for that reason).
MQ9004 and MQ9018 look only at reachable states (an unreachable one already has MQ9003); `done` on a state that is not
compound or parallel is MQ9008. MQ9008 also reports a field of another trigger (`event`, `after` or `invoke` set with a
trigger that never reads it). A history state directly under the root is allowed (the root is compound). An invoke is "used"
(MQ9013) when an `invoke-done` or `invoke-error` transition names it. MQ9202 also refuses a flags enum (it holds several
members at once). MQ9204 counts every other attribute typed by the enum (entities, value objects, relations, stereotypes, and
process context, payload and audit attributes); another lifecycle bound to the enum is counted through its attribute. MQ9205
also reports a bound attribute without a default, and is skipped when the root's initial child is a choice or history state.
Scoped validation adds peers for these rules: a process change re-checks every actor (MQ9106) and the sub-processes it reaches
(MQ9015); a change to an enum or to any element holding attributes re-checks the processes that bind an attribute.

**Where they run.** MQ90xx to MQ92xx run in whole-model and scoped validation (`ValidationScope` with referrers, so an
enum change re-checks its bound processes). MQ93xx and MQ9502 to MQ9507 run by replaying scenarios in the engine
interpreter: `validate` replays every scenario of every process in scope, and the simulation endpoint reports them on its
trace. MQ9401 to MQ9406 are returned by import and export; the opaque data they describe stays visible in the process
inspector's Source section (§6.2).

**Quick fixes.** `RuleInfo` gains an optional `QuickFix` (an operation name), `GET /api/validation/rules` rows gain
`quickFix`, and the Problems panel offers the fix on a diagnostic of that rule, applied to the diagnostic's element as
one batch that undo reverts. The operations are batch operations in `batch.json`: `sync-enum` (`id`: the process),
`set-lifecycle` (`id`: the entity, `target`: the process), `set-initial` (`id`: the compound state or the process,
`target`: the child), `refresh-scenario` (`id`: the scenario: rewrite `expect` and `outcome` from a replay). The removals use the
existing `update`.

As built (P1), how each fix is derived from the diagnostic (P3 wires the button from this; `QuickFix` names only the
operation, so a fix whose arguments cannot be read from the diagnostic's element and pointer is not in the catalog):

| Rule | Operation | Arguments from the diagnostic | In the catalog |
| --- | --- | --- | --- |
| MQ9001 | `set-initial` | `id` = the diagnostic's element (the process, or the compound state); `target` = the first child in document order (the editor lets the user pick another) | yes |
| MQ9203 | `sync-enum` | `id` = the diagnostic's element (the process); nothing else | yes |
| MQ9201 | `set-lifecycle` | only two of its four findings are fixable: pointer `/subject` (`id` = the process's `subject`, `target` = the process) and an entity's `/lifecycle` naming an orchestration (`id` = the entity, `target` = its `lifecycle`). Pointer `/use` (no subject) has no entity to pass, and an entity naming another entity's lifecycle would silently unbind that entity | no: P3 either adds per-diagnostic fix arguments or builds these two cases in the editor |
| MQ9013, MQ9016, MQ9102, MQ9105, MQ9205 | plain `update` of the diagnostic's element: remove the node at the pointer (MQ9013, MQ9016), add the missing actors to `signers` or the event's `actors` (MQ9102, MQ9105), set `default` (MQ9205) | the element, its hash and the pointer | no: an update is not a named operation; P3 builds it in the editor and counts it as a quick fix |

## 4. Engine

### 4.1 The interpreter

`src/Maquettiste.Engine/Processes/` holds `StatechartModel` (the resolved chart: states with document order, depth,
ancestors and regions; transitions grouped by source and trigger), `StatechartInterpreter`, `ProcessAnalysis` (the
structural rules), `ScenarioReplayer`, `XStateProjection` (§5) and `ProcessOperations` (`sync-enum` and the other quick
fixes). One interpreter serves validation, the editor's simulation, scenario verification and recording, and it is the
reference the generated interpreters are tested against (§8). It is deterministic: no wall clock, no randomness beyond
the sandbox's seeded one, ordinal ordering everywhere.

Semantics follow XState v5 and statechart rules, stated here so both packs' interpreters can match them:

- **Configuration.** A set of active states that is always legal: the root is active; an active compound has exactly
  one active child; an active parallel has all its regions active. Reported as the active atomic states in document
  order.
- **Macrostep.** One external input (an event, a time advance's due timer, an invoke result) is one macrostep. It runs
  the microstep for that input, then repeats: eventless (`always`) transitions enabled in the new configuration, then
  internal events (from `raises` and from `done`) in queue order, until neither exists. Over 1,000 microsteps is MQ9507.
- **Selecting transitions.** For each active atomic state in document order, walk from the state up its ancestors and
  take the first transition, in priority order, whose trigger matches, whose actor rule holds and whose guard holds.
  Drop a selected transition whose exit set intersects one selected earlier (a descendant's transition wins, then
  document order). An event accepted by no transition is refused, with the reason (`no-transition`, `guard`, `actor`,
  `gate-signer`, `gate-repeat`, `gate-reason`).
- **Microstep.** Exit states in reverse document order (running `exit` actions, recording history for history states'
  parents, cancelling their timers and invokes); run transition actions in transition order; enter states in document
  order (running `entry`, descending through `initial`, restoring shallow or deep history, starting invokes and `after`
  timers). A transition is internal unless `external`: it does not exit its source when its targets are descendants.
  Targetless transitions exit and enter nothing.
- **Done events.** Entering a final child queues `done` for its parent; when every region of a parallel state is in a
  final state, `done` is queued for the parallel state (its `onDone`). The root reaching a final child ends the
  instance.
- **Delays.** Entering a state with `after` transitions schedules a timer at clock + duration; exiting cancels it. There
  is no wall clock: a `time` input advances the clock by a duration, and every timer due within it fires in due order
  (ties by document order), each as its own macrostep at its due instant.
- **Invokes.** A sub-process invoke runs the child process in the same interpreter as a nested instance; its final
  state queues `invoke-done` on the parent. Service and human tasks stay pending until an `invoke-done` or
  `invoke-error` input names them; a human task's input must come from one of its actors.
- **Gates.** An occurrence of a gated transition's event, from an actor in `signers`, adds a signature (refused with
  `gate-signer`, `gate-repeat` or `gate-reason` otherwise, and recorded as `refused`); the occurrence that makes the
  count reach `required` with every `requiredActors` entry covered fires the transition. The interpreter emits the audit
  records of §2.3 in its trace.
- **Actors.** An input names its actor; an event with `actors` accepts only those.

### 4.2 Guards and actions in the sandbox

Expressions run in the existing sandbox (engine-design.md §10) with its options unchanged: strict mode, no interop, no
`eval`, the fixed clock, the seeded `Math.random` (seed: process id and step index). `ProcessExpressions` compiles each
process's expressions once into prepared scripts of the form `(context, event) => (<expression>)` registered under the
guard or action id; `context` and `event` cross as frozen JSON-like data (`event` has `name`, `actor`, `payload`). A
guard returns a boolean (MQ9504 otherwise); an action returns an object whose members are context attribute names (MQ9505
for others). Limits: a per-expression deadline of 50 ms and 100,000 statements (MQ9503), under the pool's cancellation
token so a stuck expression stops within the host's one-second cancellation budget (host-contracts.md item 26). MQ9501
parses every expression at validation without running it. The same compiled form is what the later query predicates will
share (decision 6).

### 4.3 Resolved model for templates

`ResolvedModel` gains `processes`, `actors` and `scenarios`. Members, snake_case in templates: a process has `use`,
`subject`, `bound_attribute`, `bound_enum`, `context`, `events`, `guards`, `actions`, `states` (the tree), `all_states`
(document order), `atomic_states`, `bound_states`, `transitions`, `gates`, `invokes`, `actors` (every actor referenced),
`scenarios`, `initial`; a state has `name`, `path`, `type`, `parent`, `children`, `initial`, `history`, `entry`, `exit`,
`invoke`, `is_final`, `bound_member`, `transitions_out`; a transition has `source`, `targets`, `trigger`, `event`,
`after` (the duration text, `after_ms` and `after_ticks`), `guard` (and `guard_missing` for a guard that does not resolve), `actions`,
`external`, `gate`, `label`; an event has `name`,
`payload`, `actors`, `transitions`; a gate has `required`, `signers`, `required_actors`, `allow_repeat_signer`,
`reason_required`, `meanings`, `audit` (§2.3 fields plus `auditAttributes`); an actor has `type`, `processes`, `events`,
`gates`; a scenario has `process`, `start`, `steps` (each with names and paths resolved: event name, actor name, payload
by attribute name (an event payload attribute or a gate audit attribute), expected state paths, and `trace`, the engine
interpreter's replay of the step: accepted, refusal, audit outcomes, state paths, final) and `outcome`. Reads record `e:` dependency keys as today, so a scenario's unit
re-renders when its process changes.

### 4.4 API

Added to `docs/api/openapi.yaml` (and the generated editor client), in the style of the existing operations: element
documents go through `/api/model/elements`; these are the process-specific operations.

| Operation | Request | Response |
| --- | --- | --- |
| `POST /api/processes/{id}/simulate` | `{ document?, start?: { context?, at? }, scenario?, steps: SimInput[], from?: int }`; `document` simulates an unsaved draft (422 with diagnostics when it does not validate); `scenario` takes `start` and steps from a scenario | `{ processHash, trace: StepTrace[], configuration: [stateId], context, enabled: EnabledTrigger[], pending: [{ invoke, state }], timers: [{ transition, dueAt }], gates: [{ gate, transition, signatures: [{ signer, actor, meaning, reason }], satisfied }], final, clock, diagnostics }`; `trace` starts at index `from` |
| `POST /api/processes/{id}/scenarios` | `{ name, start?, steps: SimInput[], outcome? }` | 201 `{ element, hash }`: a scenario whose `expect` and `outcome` are filled from the engine's replay (refusals recorded as `accepted: false`) |
| `POST /api/processes/{id}/verify` | `{ scenarios?: [id] }` (all of the process when absent) | `{ results: [{ scenario, passed, steps, failure?: { step, rule, message, expected, actual } }] }` |
| `GET /api/processes/{id}/export?format=xstate` | | the XState config (§5), `application/json`, canonical key order; `X-Maquettiste-Diagnostics` counts MQ9406 |
| `POST /api/processes/import?format=xstate&dryRun=true` | `{ config, package?, name?, use?, subject?, into?, expectedHash? }`; `into` re-imports over a process | `{ document, diagnostics, created: [id] }`; with `dryRun=false` it saves as one batch (409 when `into` changed since `expectedHash`) |
| `POST /api/processes/{id}/sync-enum` | `{ dryRun?: bool, expectedHash? }` | `{ enum, added: [name], removed: [name], reordered: bool, refused: [{ member, referencedBy: [id] }] }`; a member still used by a default, `allowedValues`, a seed cell or a scenario value (`start.context`, a step's `payload` or `expect.context`, for attributes typed by the enum) is refused, not removed: there is no force flag, the user changes the uses first. In a batch, the plan reads the documents as the batch's other operations leave them, and a batch that also writes the process or the enum is refused |

`SimInput` is a step without `expect` (§2.4); `StepTrace` is `{ index, input, accepted, refusal?, microsteps: [{
transitions, exited, entered, actions: [{ action, source: "expression" | "stub", changed }] }], guards: [{ guard,
transition, result, source: "expression" | "assumed" | "missing" }], audit: [record], configuration, context, clock,
diagnostics }`. `EnabledTrigger` is `{ trigger, event?, invoke?, transitions: [id], actors, guardUnknown, gate?: { have,
need } }`.

**Simulation state lives in the client.** A session is the list of inputs the editor holds; every call replays it from
the start (or from a cached prefix keyed by `processHash` and the inputs' hash, per host). No server-side session exists
to expire, leak or diverge between two windows, the call is idempotent, and simulation, recording and verification are
one code path. `from` keeps responses small.

**MCP tools and CLI** (parity: every operation above has both):

| Operation | MCP tool (`src/Maquettiste.Cli/Mcp/ProcessTools.cs`) | CLI (`src/Maquettiste.Cli/Commands/ProcessCommand.cs`) |
| --- | --- | --- |
| simulate | `simulate_process` | `maquettiste process simulate <process> --inputs <file\|->` (trace as text or `--format json`) |
| record | `record_scenario` | `maquettiste process record <process> <name> --inputs <file>` (preview unless `--apply`) |
| verify | `verify_scenarios` | `maquettiste process verify [<process>…]` (exit 1 on a failure; `--format json`) |
| export | `export_process` | `maquettiste process export <process> [--format xstate] [--out <file>]` |
| import | `import_process` (dry run unless `apply: true`) | `maquettiste process import <file> --domain <package> [--name] [--use] [--subject] [--into <process>] [--apply]` |
| sync-enum | `sync_enum_from_process` (dry run unless `apply: true`) | `maquettiste process sync-enum <process> [--apply]` |

`<process>` is a name, a path or an id. Exit codes follow the existing verbs: 2 for `--check`-style "would change", 3 for
a refused write, 4 for an argument that names nothing.

### 4.5 Performance budgets

Measured by the bench (`bench/`) on the synthetic model extended with 1,000 processes (40 states each, 5 with 400) and
5,000 scenarios:

| Measure | Budget |
| --- | --- |
| One macrostep, 400-state chart, no expressions | ≤ 0.2 ms p95 |
| Guard or action evaluation in the sandbox (rented lease) | ≤ 0.05 ms p95 |
| `simulate` replay of 200 inputs, server time | ≤ 30 ms |
| MQ90xx to MQ92xx for one 400-state process | ≤ 10 ms; whole model ≤ 400 ms added to `validate` |
| Replaying every scenario (5,000 × 20 steps) | ≤ 3 s at `--jobs` = cores; scoped validation replays only the changed process's scenarios |
| Export or import of a 400-state process | ≤ 50 ms |
| Editor: layout of 400 nested states in the worker | ≤ 400 ms; first paint of a 400-state chart ≤ 250 ms |

## 5. Import and export

### 5.1 The projection

XState config is a projection of the process (SPEC §8): export writes it, import reads it, and a process exported then
imported is byte-identical after canonical writing (gate 3, §8.2). State keys are state names; references by name are
resolved to ids on import. Everything with no XState home goes into `meta` under one key, `maquettiste`, so round trips
keep ids, display names, actors, gates, payload types and context types.

| Process | XState config |
| --- | --- |
| process `name`, root `initial`, `states` | `id`, `initial`, `states` |
| `context` attributes | `context` with each attribute's default (or `null`); types in `meta.maquettiste.context` |
| state `type` | `type`: `parallel`, `final`, `history` (with `history: "shallow" \| "deep"`); compound and atomic implied; choice as an atomic state with only `always` transitions and `meta.maquettiste.type: "choice"` |
| `initial`, `defaultTarget` | `initial`, `target` on the history state |
| `entry`, `exit` | `entry`, `exit` as arrays of action names |
| transition, `trigger: event` | `on: { <event name>: [{ target, guard, actions, reenter }] }` on the source, in priority order |
| `trigger: after` | `after: { <milliseconds>: [...] }` (the ISO duration kept in `meta` so `P1M` survives) |
| `trigger: always` / `done` | `always: [...]` / `onDone: [...]` |
| `trigger: invoke-done` / `invoke-error` | `invoke: [{ id, src, onDone, onError }]` on the state |
| `targets` | `target`: `"#<state id>"` absolute references (always, so moves never break a target) |
| `external: true` | `reenter: true` |
| guard | `guard: "<name>"`; definitions in `meta.maquettiste.guards` with description and expression |
| actions, `raises` | `actions: ["<name>"]`; `raises` as `{ type: "raise", event }` entries in `meta` only |
| invoke `process` / `service` / `human-task` | `src: "<process name>"` / `"<invoke name>"` / `"<invoke name>"`, kind in `meta` |
| events, payloads, `actors` | event names in `on`; payloads and actors in `meta.maquettiste.events` |
| gate | `meta` on the transition (`meta.maquettiste.gate`) |
| ids of every node | `meta.maquettiste.id` on each state and transition entry |

Export sorts keys in one fixed order (the XState key order above, then `meta`) so the output is deterministic. The
editor's "Export XState" and the CLI write the same bytes.

### 5.2 Import rules

- Named guards and actions (strings, or `{ type }` objects with no function) map to guards and actions of that name,
  created when missing, with the expression from `meta` when present.
- **Inline functions** (a guard, action, delay or `src` given as function source text in the JSON, as tools that
  serialize configs write it) become named stubs: the name is the transition's event and target (`submit_to_review_guard`),
  made unique, and the function text is kept in the stub's `description` as a fenced block, not executed. Warning MQ9402.
- **Unknown config** (custom actor logic, `tags`, `output`, `systemId`, parameterized guards and actions, `spawn`,
  `sendTo`, unknown keys) is kept as opaque extension data in the process's `source` (`format: "xstate"`, the unknown
  subtrees by JSON pointer in `source.extensions`, a new optional member of `common.json#/$defs/source`) and written back
  on export. Warning MQ9401 per subtree.
- Guard combinators (`and`, `or`, `not`) become one named guard whose expression combines the parts when every part has
  one, else a stub; MQ9404. Delays given by name map to `after` with the named delay's value when `meta` or `delays` in
  the config has it, else a stub duration `PT0S` with MQ9404.
- Import never creates actors or entities: actor ids in `meta` that are not in the model are dropped with MQ9404, and
  the subject is an argument.
- Re-import (`into`) matches nodes by the ids in `meta`, then by path; unmatched model nodes are removed only when the
  import is applied, and the dry run lists them. MQ9405 refuses ids of the wrong kind.

## 6. Editor

### 6.1 The Processes explorer

The rail gains **Processes** after Domain model (explorer-redesign.md §1.0's order becomes Domain model, Processes,
Reference data, Databases, Diagrams, Generate), one explorer at a time as today. The explorer:

```
PROCESSES                                    2 processes · 8 actors · 13 scenarios
  ▾ ◇ Sales                                  1 process
    ▾ SalesOrderLifecycle                    lifecycle · SalesOrder.status · 14 states
      ▸ States                               14            (the state tree, nested)
      ▸ Events                               9
      ▸ Scenarios                            7             (✓ 7 passed)
  ▸ ◇ Purchasing                             1 process
  ▾ Actors                                   8
      BudgetHolder                           person · persona
      CreditManager                          role
      ProcurementSystem                      external system
```

Processes are grouped by domain (nested as in the Domain model explorer; domains without processes are hidden), then an
**Actors** folder, which also stays reachable from Domain model's People and access row (explorer-redesign.md §1.1).
The Domain model explorer's **Processes** kind folder (§1.2's planned order) lists the same process rows. A state, event
or scenario row opens the process editor on the matching tab with that node selected. The header's **+** and the domain
and folder menus offer:

- **New process…**: name, domain, and **Use**: *Lifecycle* (subject entity; bound attribute, a picker over the
  subject's enum-typed attributes, or "New status attribute and enum", created in the same batch; with an existing enum,
  "Start from its members" creates one root state per member) or *Orchestration* (optional subject). The process starts
  with one state, `Initial`, and its chart open.
- **New actor…**: name, type (person, role, external system), stereotypes (a project's persona stereotype shows its
  goals field).
- **New scenario…**: process, then **Record from simulation** (opens the chart with the simulation panel recording) or
  **Empty**.

Context menus follow explorer-redesign.md §1.8: Open, Open in new tab, Simulate, Verify scenarios, Export XState, Where
used, Rename, Delete; Import XState… on a domain.

### 6.2 The process editor

A document tab in the explorer-redesign.md §3.6 frame. Top controls: name, domain, use, subject, bound attribute (with
the MQ9203 badge and **Sync enum** beside it), stereotypes, tags and category chips; display name and description in the
header; Translations collapsed. Tabs:

| Tab | Content |
| --- | --- |
| **Chart** | The statechart canvas (§6.3) with the simulation panel (§6.4) docked below it; the editor opens on it (P4) |
| **States** | A tree grid: name, type, initial, history, entry, exit, invokes, bound member (read-only); keyboard entry as the attribute grid |
| **Transitions** | A grid: source (path), trigger, event or duration, guard, targets, actions, external, gate badge; below it the **Guards** and **Actions** grids (name, expression in a one-line code field with MQ9501 markers, description, used by) |
| **Events** | Events grid (name, actors, used by); the selected event's payload in the attribute grid |
| **Gates** | One form per gate: transition, required N of M (M computed from signers), signers and required actors (actor pickers), allow repeat signer, reason required, meanings grid, audit attributes grid |
| **Context** | The attribute grid (`inspector/AttributeGrid.tsx`) over `context` |
| **Scenarios** | The process's scenarios: name, steps, status (passed, failed at step n, not run), Replay, Open, Refresh expectations, Delete |

The inspector's sections for a canvas selection: **State** (general; entry and exit; invokes; bound member; description;
translations, deferred (P3 review): the editor's localization does not cover process nodes yet; properties), **Transition** (trigger; source and targets; guard; actions; external; gate; actors, read-only
from the event), **Process** when nothing is selected (the top controls, Code generation hints, and **Source**: import provenance and the
XState data kept opaque by §5.2). A scenario opens in
its own tab: header (process, outcome), a steps grid (input, event, actor, signer, meaning, reason, payload, assumptions,
expected states and context) and the replay status per step.

### 6.3 The statechart canvas

`src/editor/src/canvas/statechart/` on the canvas library the entity diagrams use. States are rounded boxes with a
24 px header row (`ROW_H`) and 12 px text; compound and parallel states are containers holding their children; a parallel
state's regions are separated by dashed lines, laid side by side; initial is a filled dot with an arrow, final a ringed
dot, history a circled H or H*, choice a diamond. Edges carry `event [guard] / actions`, `after 30d`, `done` or `always`;
a gated edge has a badge "2 of 3" with the signers in its tooltip. Bound root states show their enum member in muted text.
Validation badges (MQ9003, MQ9004, MQ9006) mark states and edges.

**Layout.** The layered layout library the entity canvas uses (phase2-design.md §4.9), in its worker, applied bottom-up:
each container's children are laid out first, the container is sized to fit, then its parent. Direction right, node
spacing 32, layer spacing 64, orthogonal edges, hierarchy-crossing edges handled by the library's hierarchical mode.
**Layout** (Ctrl/Cmd+L) lays out the whole chart or the selected container; positions are saved in the process diagram
(§2.7) as one `PUT` like the entity canvas; a new state is placed by an incremental layout of its container only.

**Selection and keyboard** (the editor's model: arrows, Enter, Escape, F2, Delete, F12, Shift+F12, Ctrl/Cmd+Z): arrows
move the selection to the nearest state in that direction within the container; Enter enters a container (selects its
initial child), Escape selects the parent; Tab and Shift+Tab walk the selected state's outgoing edges; `N` adds a
sibling state, `Shift+N` a child, `T` starts a transition from the selection (arrows or the mouse pick the target, Enter or
a click confirms; a dot on the right edge of every state drags onto the target for the same result);
F2 renames; Delete removes (a state with incoming transitions asks, listing them); F12 on an edge opens its event, guard
or gate. Multi-selection moves and deletes together. Everything is one undo step per gesture.

### 6.4 The simulation panel

Docked under the canvas, split into dense lists:

- **Start**: context values (the Context attributes as a form, defaults filled), or **From scenario…**.
- **Enabled**: every trigger enabled in the current configuration (`EnabledTrigger`), one row each: event, actor picker
  (limited to the event's actors), payload form (from the payload attributes), and for a gated event signer, meaning and
  reason; a stub guard shows a true/false toggle whose value is recorded as `assume`. **Raise** (Enter on the row).
  Number keys 1 to 9 raise the first nine rows with their current values.
- **Time**: next timer due and **Advance** by a duration (the next due timer by default).
- **Pending**: invokes waiting for a result, with **Done** and **Error**.
- **Configuration**: active states by path; the canvas highlights them, and the edges just taken flash once.
- **Last step**: guards evaluated (guard, transition, result, source: expression, assumed, missing), actions run
  (expression or stub, context changes), gate progress and audit records, refusals with their reason.
- **Trace**: the inputs so far; selecting one shows the state after it (a replay of the prefix); Delete removes the input
  and everything after it.
- **Record to scenario…** (name, outcome prefilled): `POST /api/processes/{id}/scenarios`. **Replay scenario…** steps
  through a scenario with pass or fail per step and stops at the first failure with expected and actual side by side.

Each change calls `simulate` with the input list, debounced 150 ms, aborting the previous call; the draft document is
sent when the process has unsaved edits.

### 6.5 Diagram file

The process diagram is an ordinary `model/diagrams/` file with `process` set (§2.7). It is listed under Diagrams in the
process's domain folder and opens the process editor's Chart tab. Members are states; positions are relative to the
parent container; containers carry `width` and `height`.

## 7. Generation

### 7.1 Scopes and variables

`pack.json`'s `for` pattern gains `each process`, `each actor` and `each scenario` (errata E32; SPEC §12 already names
`each process`). One unit per element, unit key the element id, scope aliases `process`, `actor` and `scenario` beside
`element`; `where` takes tags, stereotypes, categories and packages (a scenario's package is its process's), and
`database` and `abstract` are refused at pack load (MQ6001). The Units grid's scope help (generation-ui.md §3.1) gains a
row: "Runs once per process, actor or scenario; one file each." Variables are §4.3's members; two helpers join the
built-ins: `state_path <state>` and `iso_duration_ms <text>`. Expression translation is not a built-in: it is pack code
(below).

**Expressions in generated code.** A guard or action expression is JavaScript for the engine's sandbox. The TypeScript
pack emits it verbatim inside a typed function. The C# pack translates a documented subset in its `helpers.js`
(literals, `context.x` and `event.payload.x` member access, comparison, `&&`, `||`, `!`, arithmetic, `?:`, and an object
literal of context updates for actions); anything outside the subset becomes a stub in the handler companion, with the
expression as a comment. The subset lives in the pack, not in the engine, so another pack may choose differently.

### 7.2 Units of the example packs

`packs/csharp-dapper` (output under `Processes/<Process>/` unless shown), and a new example pack `packs/process-docs`
for documentation:

| Unit | Scope | Output | Mode | Why this mode |
| --- | --- | --- | --- | --- |
| `process-states` | each process | `<P>States.cs`: the state enum (the bound enum type when the process is a lifecycle, else one member per state path) and path constants | overwrite | Pure derivation; never edited |
| `process-definition` | each process | `<P>Definition.cs`: the transition table, state tree, timers, gates and meanings as static data the interpreter reads | overwrite | Pure derivation |
| `process-contracts` | each process | `<P>Contracts.cs`: one command record per event (typed payload plus the envelope: instance, actor, signer, meaning, reason), the `<P>Transitioned` event record, the gate audit record (§2.3) | overwrite | Contracts are shared with other processes and services and must match the model exactly |
| `process-handlers` | each process | `<P>Handlers.g.cs` + companion `<P>Handlers.cs` | pair | Guards and actions are hand-owned logic; the generated partial class declares a partial method per stub guard and action (a new one fails the build at the missing implementation) and implements the translated ones; the companion is created once with stub bodies |
| `process-services` | each process | `<P>Services.g.cs` (one interface per service task, one notification hook per human task) + companion `<P>Services.cs` | pair | Service implementations are the project's code; the interface changes with the model, the implementation does not get overwritten |
| `process-machine` | each process | `<P>Machine.g.cs` + companion `<P>Machine.cs` | pair | A typed facade (`Start`, `Restore`, one method per event) that teams extend with queries and conveniences in the partial partner |
| `process-store` | each process | `<P>Store.g.cs` (`I<P>Store`: load and save the instance snapshot, append history, append audit records) + companion `<P>Store.cs` | pair | Where instances, history and audit records live is a mapping or template decision (decision 5): the companion adapts the interface to whatever the project mapped |
| `process-endpoints` | each process | `Endpoints/<P>Endpoints.cs`: one endpoint per event, `POST <route>/{instance}/<event>`, mapping the request to the command and calling the dispatcher | regions | Many endpoints in one file whose set follows the events; per-endpoint request mapping and response shaping sit in one user-code region per endpoint and survive regeneration (committed roots only, MQ6015) |
| `dispatch` (+ `dispatch-companion`) | model | `Dispatch/Dispatch.g.cs` + companion `Dispatch/Pipeline.cs` | overwrite + once (as built: file blocks written only when the model has processes) | The generated half holds the contracts and dispatcher; the companion, created once, orders the pipeline and holds the project's own behaviours |
| `dispatch-registry` | model | `Dispatch/HandlerRegistry.g.cs` | overwrite | A complete, derived list; hand additions go in the `Pipeline.cs` partner |
| `dispatch-behaviours` | model | `Dispatch/Behaviours.g.cs` | overwrite | Default behaviours derived from the model; their policy hooks are partial methods implemented in `Pipeline.cs` |
| `interpreter` (+ `interpreter-companion`) | model | `Runtime/Statechart.g.cs` + companion `Runtime/ProcessHost.cs` | overwrite + once (as built: file blocks written only when the model has processes) | The small interpreter (§4.1 semantics) is generated and never edited; its extension points (clock, timer scheduler, invoke host) are interfaces the companion implements, created once with in-memory defaults |
| `actors` | model | `Processes/Actors.cs`: one constant per actor with its type, and each event's allowed actors | overwrite | Derived |
| `scenario-tests` | each scenario | `<test root>/<P>/<Scenario>Tests.cs`: one test per scenario asserting, after each step, accepted or refused, the active states and the changed context, and the outcome, through the dispatcher and the generated interpreter with a manual clock | overwrite | Derived from the model; a failing test is fixed in the model or in a handler, never in the test |
| `process-tables` (`sql-ddl`) | each process | `<db>/processes/<process>.sql`: instance, history and audit tables | overwrite | Off unless the parameter `processTables` is `true`: a template choice for projects that do not model these tables; the fixture maps modelled entities instead |
| `process-page` (`process-docs`) | each process | `processes/<process>.md`: description, a state diagram in diagram text, the transition table, gates and actors | overwrite | Documentation, derived |
| `actor-page` (`process-docs`) | each actor | `actors/<actor>.md`: type, stereotype properties (a persona's goals), the events it raises and the gates it signs | overwrite | Derived |
| `scenario-page` (`process-docs`) | each scenario | `processes/<process>/<scenario>.md`: the walk-through, one line per step with the actor, the event and the states after it | overwrite | Derived; the documentation walk-through of decision S |

No unit writes into a model folder, and nothing is created for a process that the project did not ask for: the process
units run for every process, but storage is an interface until the project's companion or a template choice says where.

### 7.3 Custom-code shapes

| Shape | Mode | C# | TypeScript | Used by |
| --- | --- | --- | --- | --- |
| User-code regions | `regions` | region markers inside a generated file (engine-design.md §13); the file is hashed with region bodies emptied, so edits inside regions are not hand edits | same markers in comments | `process-endpoints` |
| Partial pair | `pair` | a generated `*.g.cs` partial class plus a hand-owned partial partner created once; partial methods mark what the partner must implement | a generated `*.gen.ts` module plus a hand-owned companion created once; the generated module imports the companion through a declared interface (`import { handlers } from "./<p>.handlers.js"` checked with `satisfies <P>Handlers`) | handlers, services, machine, store, dispatch, interpreter host |

Regions fit a file with many small hand edits inside a generated structure; pairs fit hand-owned logic that the model
only declares. `once` alone is not used for code: a pair keeps the declaration side regenerating.

### 7.4 Dispatch

Generated code dispatches in-process and stays loosely coupled:

- **Contracts.** One command per process event (typed payload and envelope) and one event record per transition kind
  (`<P>Transitioned`: instance, from, to, event, at) plus the gate audit record. Contracts are plain types with no
  dependency on the dispatcher, so other services can share them.
- **Registry.** `HandlerRegistry` is a generated, typed list of registrations (command type, handler factory), built
  without reflection. The dispatcher reads it directly; a dependency-injection container can consume the same list
  through a small generated registration method, so neither the container nor reflection is required. In TypeScript the
  registry is a typed map from command name to handler, checked to cover every command.
- **Pipeline behaviours**, in this default order, each a generated class with partial-method hooks in `Pipeline.cs`:
  validation (payload attributes: required, length, range, allowed values), authorization by actor (the envelope's actor
  is one of the event's actors; the hook maps the host's principal to actors; gates stay the interpreter's, so a gated event
  without a signer is refused there with its audit record), logging
  (attributes marked `sensitive` are never logged), transaction (a unit-of-work hook around the handler and the store),
  outbox hand-off (the `<P>Transitioned` records and audit records are written to an outbox interface inside the same
  transaction).
- **Handler stubs** in `pair` mode, per process: the handler loads the instance through the store, runs the machine,
  and saves.
- **Boundary.** Cross-process delivery (message queues, durable function platforms, change capture) is outside the
  generated code: the contracts are shared, and the outbox hand-off is where generated code stops.

### 7.5 What the TypeScript pack mirrors

`samples/typescript-pack` gains the same units in TypeScript: a state union type, the definition as a `const` object,
command and event types, handlers and services as `pair` modules with companions, the typed registry map, behaviours as
composed functions, the interpreter module (`runtime/statechart.ts`, same semantics), endpoint stubs with regions, and
scenario tests on the platform's built-in test runner. It runs in CI after the C# pack and is not a gate 3 criterion.

## 8. Gate 3

### 8.1 Fixtures

`tests/fixtures/models/processes/` is one repo root with `.maquettiste/`, a C# solution under `src/` for the
hand-written partners, and `tools/gate3.sh`. Neither process comes from the owner's projects.

- **Domain Sales, `SalesOrderLifecycle`** (lifecycle; subject `SalesOrder`, bound attribute `SalesOrder.status` of enum
  `SalesOrderStatus`). `Draft` → `submit` → choice `CreditCheck` (`exceedsCreditLimit` → `CreditReview`, else →
  `Fulfilment`). `CreditReview` has a gate on `approveCredit`: 2 signatures from `CreditManager` and `FinanceDirector`,
  `CreditManager` required, no repeat signer, reason required, meaning "Credit approved"; `rejectCredit` loops back to
  `Draft`. `Fulfilment` is compound (as built, see the erratum in §10): its initial child `Processing` is parallel with region
  Payment (`AwaitingPayment` → `paymentReceived` → `Paid`, final; `after P30D` → `PaymentOverdue`) and region Shipping
  (`Picking` → `pick` → `Packing` → `ship` → `Shipped`, final), and its other child is the deep history state `Resume`
  (default `Processing`); `Processing`'s `done` → `Completed` (final). `hold` from `Fulfilment` → `OnHold`; `release` →
  `Resume`, the deep history of `Fulfilment`. `cancel` →
  `Cancelled` (final), guarded by `notShipped`. Context `total`, `creditLimit`; guards with expressions and one stub
  (`notShipped`). Scenarios: small order, credit approved with two signatures, credit rejected and resubmitted, hold and
  release (deep history), payment overdue by time, cancel refused after shipping, cancel before shipping (added by the P5
  review, so that `notShipped` is exercised both ways), repeat signer refused.
- **Domain Purchasing, `PurchaseApproval`** (orchestration; subject `PurchaseRequest`). Actors `Requester` (role),
  `Approver` (role), `FinanceController` (role), `ComplianceOfficer` (role), `ProcurementSystem` (external system),
  `BudgetHolder` (person, with the fixture's own `persona` stereotype and `goals`). `Drafting` → `submit` → `Review`,
  parallel: region Budget (`Checking` invokes service task `checkBudget`: done → `BudgetOk`, final; error →
  `BudgetRejected` → `requestChanges` → `Drafting`), region Compliance (`Pending` invokes human task
  `complianceReview` by `ComplianceOfficer` → `Cleared`, final). `onDone` → `Approval`: gate on `approve`, 2 of
  `Approver` and `FinanceController`, `FinanceController` required, audit attribute `costCentre`; `reject` → `Rejected`
  (final); `after P5D` a targetless transition with action `sendReminder`. Then `Ordering` invokes `createPurchaseOrder`
  (service, by `ProcurementSystem`) → `Ordered` (final). Scenarios: happy path, changes requested loop, budget rejected,
  compliance first then budget, reminder after five days, rejection with reason.

The model maps `SalesOrder` (with a `configuration` json attribute for the active states), `SalesOrderHistory`,
`PurchaseRequest`, `ProcessInstance` and `GateSignature` entities to a database explicitly, and the fixture's
`<P>Store.cs` companions use the pack's repositories for them: storage is modelled, not synthesized.

### 8.2 Pass criteria

| # | Criterion |
| --- | --- |
| 1 | `maquettiste validate` on the fixture: no error and no warning (MQ9005 infos allowed); every file canonical |
| 2 | `maquettiste process verify`: all 14 scenarios pass in the engine interpreter (13 until the P5 review added cancel before shipping) |
| 3 | At least one scenario of each process was recorded through the editor's simulation panel (the walk step below records one more and it must pass in 2 and 5) |
| 4 | `maquettiste generate` with `csharp-dapper` and `process-docs` into a copy, then the fixture's solution builds with `-warnaserror` (generated files plus the committed companions) |
| 5 | The generated scenario tests pass: one test per scenario, every step's acceptance, states and context and the outcome asserted through the dispatcher and the generated interpreter |
| 6 | Determinism: `generate` at `--jobs 1` and `--jobs N` byte-identical; `generate --check` clean after apply; export XState twice byte-identical |
| 7 | Round trip: each process exported to XState and imported with `--into` into a scratch copy gives byte-identical process files and an identical second export |
| 8 | Budgets of §4.5 met by the bench's process cases |

### 8.3 The gate script

`tests/fixtures/models/processes/tools/gate3.sh`, run the same way locally and by `.github/workflows/gate3.yml`, steps
in order, each printing its wall time: `prepare` (copy the fixture to `tmp/gate3`, start the image over it, wait for
`GET /api/health`), `walk` (`src/editor/tests/e2e/gate3.spec.ts`: open `PurchaseApproval`, simulate a path in the panel,
record it as a scenario, plan and apply), `validate` (criterion 1), `verify` (2, 3), `generate` (4, 6), `build` (4),
`test` (5), `roundtrip` (7), `bench` (8, `bench --filter processes`), `down`. Exit non-zero on the first failure.

## 9. Rounds

Each round ends with the build green (`dotnet build maquettiste.slnx -warnaserror`, `dotnet test`, and for editor
rounds `npm --prefix src/editor run lint`, `typecheck`, `test`, `e2e`), a reviewer who tries to refute the round's claims,
and a status line in this document.

| Round | Owned paths | Deliverables and tests | Reviewer refutes |
| --- | --- | --- | --- |
| **P1** Model, schemas, rules, CLI and MCP basics | `schemas/v1/{process,actor,scenario}.json`, `entity.json` (`lifecycle`), `diagram.json` (`process`), `pack.json` (scopes), `extension.json`, `batch.json`, `common.json` (`source.extensions`); `src/Maquettiste.Engine/Model/`, `Loading/`, `Validation/ProcessRules.cs`, `Processes/ProcessAnalysis.cs`, `Diagnostics/RuleCatalog.cs`; `tests/fixtures/models/processes/.maquettiste/` | Records and schemas with `x-order`; loader, index rows, localization of the new nodes; MQ9001–MQ9018, MQ9101–MQ9106, MQ9201–MQ9205 with catalog entries and tests; `sync-enum`, `set-lifecycle`, `set-initial` batch operations; the fixture's model; `SchemaConsistencyTests` and canonical round trip of every fixture file | A schema key without `x-order` or record property; a rule without a failing test; a sync that drops a referenced member; a fixture file not canonical |
| **P2** Interpreter, scenarios, import and export, API | `src/Maquettiste.Engine/Processes/`, `Scripting/` (expression compilation), `src/Maquettiste.Functions/_functions/ProcessEndpoints.cs`, `docs/api/openapi.yaml`, `src/Maquettiste.Cli/Commands/ProcessCommand.cs`, `src/Maquettiste.Cli/Mcp/ProcessTools.cs` | §4.1 semantics with a conformance suite (one test per bullet, plus the SPEC §8 example chart); MQ93xx, MQ94xx, MQ95xx; `simulate`, `scenarios`, `verify`, `export`, `import`, `sync-enum` endpoints, MCP tools and CLI verbs with parity tests; `validate` replays scenarios; the fixture's 13 scenarios pass; bench cases | Two runs of one input list giving different traces; an XState round trip that changes bytes; an endpoint without an MCP tool or CLI verb; a budget missed |
| **P3** Explorer, editors, dialogs | `src/editor/src/explorer/` (Processes rail and tree), `src/editor/src/editors/process/`, `actor/`, `scenario/`, `src/editor/src/model/labels.ts`, mocks | The Processes explorer (§6.1), New process, New actor and New scenario dialogs, the process editor's tabs except Chart, the actor and scenario editors, Problems quick fixes; component tests and mock e2e | A row whose kind is not stated by it or its folder; a dialog that saves in more than one change; a grid not at `ROW_H`; keyboard paths that need the mouse |
| **P4** Canvas, layout, simulation panel | `src/editor/src/canvas/statechart/`, `src/editor/src/canvas/layout.ts` (hierarchical use), `src/editor/src/editors/process/simulation/` | §6.3 canvas, nested bottom-up layout in the worker, positions in the process diagram, §6.4 panel with recording and replay; tests for layout sizing, keyboard model, debounce and abort; e2e: simulate and record | Layout that moves saved positions without Layout; a simulation result computed in the browser rather than by the engine; first paint or layout over budget |
| **P5** Packs, interpreter, dispatch, tests, gate 3 | `packs/csharp-dapper/` (process units, `helpers.js` translation), `packs/process-docs/`, `packs/sql-ddl/` (`process-tables`), `samples/typescript-pack/`, `tests/Maquettiste.Packs.Tests/ProcessTests.cs`, `tests/fixtures/models/processes/src/` and `tools/gate3.sh`, `.github/workflows/gate3.yml` | §7.2 units with golden files; the generated interpreter agrees with the engine on every fixture scenario; the dispatch registry, behaviours and pairs; the TypeScript mirror in CI; gate 3 criteria 1 to 8 pass | A generated file that differs between `--jobs` values; a companion overwritten on regeneration; reflection in the registry; a scenario test that passes against a broken handler |
| **P6** Docs, user guide, gate close | `docs/user-guide.md`, `docs/mcp.md`, pack READMEs, this document's status, `docs/engineering/spec-errata.md` (E27 onward), `README.md` | User guide sections for processes, actors, scenarios, simulation, import and export; MQ9xxx table in the guide; gate 3 run recorded; errata entered for the owner | A guide step that does not match the editor; an MQ id missing from the guide; a vendor name anywhere |

**Status P1, stage 1 (model), 2026-09-29:** built and verified. `process.json`, `actor.json`, `scenario.json` (with `$defs` for state, transition, event, guard, action, invoke, gate, meaning, step); `entity.json` `lifecycle`, `diagram.json` `process`, `pack.json` scopes `each process|actor|scenario`, `extension.json` and `stereotype.json` applicable kinds, `common.json` `source.extensions` (and `format: xstate`). Records in `Model/Processes.cs`, the nine index kinds, owning scenario deletion, `model/scenarios/<process stem>/`, index rows (`use`, `subject`, `stateCount`, `actorType`, `process`, `stepCount`, format `maquettiste-index/e8`), localizable nodes and shards, the six MQ9 family labels; fixture `tests/fixtures/models/process-basics/` and `Processes/ProcessModelTests.cs`. Scenarios carry `pluralName` like every element (the design's `x-order` omits it). Left for P1: MQ9001-MQ9018, MQ9101-MQ9106, MQ9201-MQ9205, the batch operations and the §8.1 fixture.

**Status P1, stage 2 (rules), 2026-09-29:** built and verified. `Processes/ProcessAnalysis.cs` (state tree, reachability through initial children, parallel regions, history defaults, transitions and `done`, completion, transition groups, eventless cycles); `Validation/ProcessRules.cs` with MQ9001-MQ9018, MQ9101-MQ9106 and MQ9201-MQ9205 run by `ModelValidator` (scoped peers for actors, sub-processes and bound enums); 29 catalog entries; `Processes/ProcessRuleTests.cs` with a failing and a passing case per rule. Choices the table left open are listed under section 3 ("As built"). Left for P1: MQ3001/MQ3018/MQ5001/MQ2003 on process sub-elements, the `sync-enum`, `set-lifecycle` and `set-initial` batch operations, and the section 8.1 fixture.

**Status P1, stage 3 (operations and fixture), 2026-09-29:** built and verified. Batch operations `sync-enum` (bound enum members become the bound states in document order, keeping ids, codes and descriptions; a removed member still used by a default, `allowedValues` or a seed cell refuses the operation (stage 4 dropped a `removeUsed` override); `ModelStore.PlanSyncEnumAsync` is the dry run: added, removed, reordered, refused), `set-lifecycle` (binds entity and process in one change and unbinds the previous partners, or clears the entity's lifecycle and turns the process back into an orchestration) and `set-initial` (process or compound state, a direct child) in `batch.json`, the engine's batch dispatcher (`ModelStore.Processes.cs`), `openapi.yaml`, `apply_batch` and `docs/mcp.md`; refusals are the new MQ9019 (catalog 146). `RuleInfo.QuickFix` and the rules endpoint's `quickFix` carry the fixes: MQ9001 `set-initial`, MQ9201 `set-lifecycle`, MQ9203 `sync-enum` (the Problems panel had no fix mechanism before; P3 wires it). The section 8.1 fixture `tests/fixtures/models/processes/.maquettiste/` (39 files, 13 scenarios, 48 steps) validates 0/0/0 and is canonical; `Fulfilment` is a compound state wrapping the parallel `Processing` and the deep history `Resume`, because MQ9011 (as built) puts a history state in a compound parent; `Processing`'s `done` goes to `Completed`. Tests: `Processes/ProcessOperationTests.cs`, `Processes/ProcessFixtureTests.cs`, Functions `ProcessBatchTests.cs`. Left for P1: MQ3001/MQ3018/MQ5001/MQ2003 on process sub-elements.

**Status P1, stage 4 (review fixes), 2026-09-29:** built and verified. MQ3001 now covers processes (per package), actors,
scenarios (per process), states among siblings, and events, guards, actions, invokes and gates within the process (one
namespace per kind) and gate meanings within their gate; MQ3018 checks process and node names as identifiers; MQ2003 and
MQ2004 run on the stereotypes of states, transitions and events, and extensions (MQ5001) apply to the kinds `state`,
`transition` and `event`. `removeUsed` is gone (§4.4 never had it): a used member is refused until its uses change, and
uses now include scenario values; `sync-enum` plans over the batch's own staged documents and is refused when the batch
also writes its process or enum. MQ3013 reports an enum attribute's `allowedValues` entry that names no member. MQ9201
lost its catalog quick fix (see the derivation table in §3). Refusal messages say what to do; the canonical round trip
test covers both process fixtures; §8.1 and §10 record the `Fulfilment` restructuring. P1 is complete.

**Status P2, stage (interpreter, expressions, replay), 2026-09-29:** built and verified. `Processes/StatechartModel.cs` (the resolved
chart, cached by process id and hash), `StatechartInterpreter.cs` (§4.1: configuration, macrostep and microstep, selection with
priority and SCXML conflict removal, exit and entry sets with the XState v5 transition domain, shallow and deep history, `done` for
compound and parallel states, a simulated clock with timers in due then document order, sub-process instances and pending service and
human tasks, gates with the §2.3 audit records, actors, refusal reasons), `StepTrace.cs` (the §4.4 trace), `ProcessExpressions.cs` (each
process's expressions parsed without running, MQ9501, and compiled once into one prepared script of `(context, event) => (expression)`
helpers; 50 ms and 100,000 statements under the pool's cancellation), `ScenarioReplayer.cs` (`ProcessRuntime` shares charts and one
expression pool per process per run; MQ9301 to MQ9306). `validate` replays every scenario in scope (`Validation/ScenarioRules.cs`; a
process change re-checks its scenarios and those of its callers, an entity change those of the processes it is the subject of, an enum,
value object or scalar change those of the processes whose attributes use it). The `refresh-scenario` batch operation (batch.json,
openapi, parser, quick fix of MQ9302 to MQ9304). All 13 fixture scenarios pass as written: no expectation needed a correction.
Measured on the fixture (in-test stopwatch, 960 macrosteps): macrostep p95 0.07 to 0.09 ms, guard evaluation p95 0.015 ms on a
rented lease (budgets 0.2 ms on a 400-state chart and 0.05 ms; the bench cases are P2c's). As built, where §3 and §4.1 left a choice:
only the first failing step of a scenario is reported, and the steps after it still run (their traces feed `refresh-scenario`) without
being compared; an empty `expect.states` is not checked, and `expect.context` must equal the set of attributes the step changed; a
`time` input is always accepted; MQ9506 compares guarded transitions only (an unguarded fallback, a choice's else, is intended); a
raised (internal) event never completes a gate (it has no signer); durations are fixed spans (a year 365 days, a month 30 days, as
delays in milliseconds are); an event input that names an event of a running sub-process goes to that instance; `event.actor` is the
actor's name. Replay findings (MQ93xx, MQ9502 to MQ9507) are reported on a save but do not refuse it (`RuleCatalog.IsReplayFinding`,
read by `ModelStore`), so a chart edit is saved and its scenarios are refreshed afterwards; `refresh-scenario` is refused (MQ9019) when
the batch also writes the scenario or its process, or when the replay stops before the last step. Left for P2: MQ9401 to MQ9406 and the
XState projection, the `simulate`, `scenarios`, `verify`, `export`, `import` and `sync-enum` endpoints with MCP tools and CLI verbs,
and the bench cases.

**Status P2, stage (XState projection), 2026-09-29:** built and verified. `Processes/XStateProjection.cs` (the engine methods the
`export` and `import` endpoints, MCP tools and CLI verbs call: `XStateProjection.Export(process, json, options)` returns the config text
and MQ9406/MQ9404; `XStateProjection.Import(text, json, options)` with `Model`, `Into`, `Package`, `Name`, `Use`, `Subject`, `Ids`
returns the canonical process file, the record, MQ9401 to MQ9405, `Created` and, with `into`, `Removed`), `XStateExporter.cs` and
`XStateImporter.cs`; catalog MQ9401 to MQ9406 (165 rules). Every fixture process (the two §8.1 processes and `process-basics`) exported
then imported gives a byte-identical canonical file and an identical second export, both standalone and `into` itself within the
model (no diagnostic, nothing created or removed); five exports of one process are byte-identical. Import samples in
`tests/fixtures/xstate/` (parallel, history, delays, invokes, choice with a combinator and a parameterized guard, inline functions,
unknown keys) are each stable after their first import. Tests: `Processes/XStateProjectionTests.cs` (39). As built, where §5 left a
choice: the global key order is `id, src, initial, states, context, type, history, target, entry, exit, on, after, always, onDone,
onError, invoke, guard, actions, reenter`, then kept opaque keys, then `meta` (every object follows the table's row order); a state's
model id is its XState `id`, so `"#<id>"` targets resolve in XState itself and a state's `meta.maquettiste` carries no `id`
(transitions and invokes carry theirs in `meta`); an invoke's XState `id` is its name; a compound state or root whose model names no
`initial` is exported with its first child as `initial` (XState v5 needs one) and `meta.maquettiste.initialImplied: true`; the root's
`meta.maquettiste.transitions` lists transition ids in priority order (per-state grouping loses the global order); `meta.maquettiste`
holds each node's canonical fields with no XState home, in canonical order (context attributes without `default`, whose value is in
`context`); `on`, `after`, `always`, `onDone` and invoke `onDone`/`onError` are always arrays, and opaque data is keyed by pointers in
that array form (a single transition object imports as `/0`), so it is written back where it was; a kept pointer with no place left is
dropped on export with MQ9404. Ids an import creates derive from one seed id drawn from `Ids` per import and the node's path in the
config (SHA-256 into the ULID's random part, the seed's time part): one path gets one id within an import, another import gets fresh
ids, and a deterministic generator makes the whole import deterministic. Without `meta`, `into` keeps ids and descriptive fields
(display names, descriptions, stereotypes, properties, gates, definitions with their expressions) of nodes matched by path (states),
name (events, guards, actions, invokes, context) or source, trigger and position (transitions), and keeps its order for them. MQ9405
also covers an id carried twice in one config and, without `into`, a carried process id already in the model; the node gets a derived
id so the dry run still shows a document. A `src` naming a model process imports as a `process` invoke; context values without `meta`
type as `string`, `bool`, `int32`/`int64`, `decimal` or `json` (null: no default); a root `type` other than compound, an unknown state
type, or a target or initial that does not resolve is MQ9403. Left for P2: the `simulate`, `scenarios`, `verify`, `export`, `import`
and `sync-enum` endpoints with MCP tools, CLI verbs and parity tests, and the bench cases.

**Status P2, stage (API, MCP, CLI, bench), 2026-09-29:** built and verified; P2 is complete. The six operations of §4.4 share one
engine call each (`ModelStore.ProcessApi.cs`: `SimulateProcessAsync`, `RecordScenarioAsync`, `VerifyScenariosAsync`,
`ExportProcessAsync`, `ImportProcessAsync`, `SyncEnumAsync`, returning `ProcessCall<T>`; request, response and simulation code in
`Processes/ProcessApi.cs`), wrapped by `_functions/ProcessEndpoints.cs`, the MCP tools `simulate_process`, `record_scenario`,
`verify_scenarios`, `export_process`, `import_process`, `sync_enum_from_process` (`Mcp/ProcessTools.cs`, 46 tools) and
`maquettiste process simulate|record|verify|export|import|sync-enum` (`Commands/ProcessCommand.cs`); `openapi.yaml` (tag
`processes`, 62 operations, problem code `conflict`) and the regenerated editor client and mocks. Parity tests:
`Functions.Tests/ProcessEndpointTests.cs` (6), `Cli.Tests/ProcessCommandTests.cs` (2 MCP, 2 CLI): verify passes all 13 fixture
scenarios, export then import `into` the process gives the same bytes, sync-enum plans and applies a drifted (reordered) enum.
As built, where §4.4 left a choice: a draft `document` is checked exactly as a save would check it (a new non-replay error is 422
with a `ValidationReport`); `scenario` runs its start and steps before the request's `steps`; the replay stops after an incomplete
trace; `from` defaults to -1 (the initial entry); `enabled` lists events of the root instance with candidate transitions (actors:
the event's, else the gate's signers), `invoke-done`/`invoke-error` per pending task, and `time` when timers exist; `gates` lists
the gated transitions of active states with their signatures; `record` accepts `?dryRun=true` (the CLI preview) and takes
`outcome` from the request when given, else from the replay; `verify` failures from a stop (MQ9305, MQ9306, MQ9507) have null
`expected` and `actual`; `sync-enum` checks `expectedHash` against the process (409) and a refused or non-lifecycle sync is 422
with MQ9019; `import` 409 comes back as a problem before planning and as the result body when the save conflicts; the CLI exits 0,
1 (failing scenario, invalid import, refused sync), 2 (`sync-enum --check` out of sync), 3 (changed while running), 4 (names
nothing). The functions' file budget of phase2-design.md §3.1 went from 24 to 25 files for `ProcessEndpoints.cs` (the host limit
is 50). No fixture expectation needed a correction. Bench (`Maquettiste.Bench time-processes`, a verb of its own so the default
bench does not move; 5,000 entities, 1,000 processes of 42 states with 5 of 400, 5,000 scenarios of 20 steps, 24 cores, jobs 24):
macrostep p95 0.005 ms (budget 0.2), guard evaluation p95 0.003 ms (0.05), simulate of 200 inputs 2.3 ms (30), whole-model
MQ90xx to MQ92xx 347 ms added to validate (400; base 143 ms, with processes 491 ms), replay of every scenario 2.3 s (3 s), export
4.3 ms and dry-run import into itself 6.9 ms of a 400-state process (50). **Known miss:** "one 400-state process ≤ 10 ms" measures
33 ms as a validate scoped to that process, but a validate scoped to one entity of the same model costs 43 ms: the figure is the
fixed cost of building a scoped validation context over the 5,000-entity model, not the process rules (5 ms on a 200-entity
model); the budget is kept and the scoped-validate fixed cost is left for a later round. Left: P3 onward (the editor wires these
operations and the quick fixes).

Status (P2 review fixes): the interpreter now queues `done` for a compound parent only; a parallel state's completion is a walk
from the final state's parent (or its region's parent) up through enclosing parallel states while every region is final, each
queued once per microstep, and a final state that is itself a region counts as final (a final region no longer completes its
parallel state early, and completion reaches a parallel state nested in another). A gate is checked during selection (refusals as
before) but signed only when its transition survives conflict removal; a preempted gated transition gets no signature and no audit,
and an incomplete one does not preempt others. The MQ90xx-MQ92xx budget row now measures what it names, `ProcessRules` for the
400-state process over a built validation context: **1.6 ms (10), pass**; the scoped validate (30 ms, with 41 ms for one entity)
is logged for reference as the fixed cost of a scoped run. Other figures of the same run: macrostep 0.005 ms, guard 0.003 ms,
simulate 2.2 ms, whole-model rules 319 ms added (base 122, with processes 441), replay 2.3 s, export 4.2 ms, import 7.0 ms; no
miss. Export diagnostics reach every surface: HTTP `X-Maquettiste-Warnings` counts MQ9404 (next to the MQ9406 count), MCP
`export_process` adds a second content block `{ diagnostics }`, the CLI prints them to stderr. An unparsable `start.at` is a bad
request on simulate and record (exit 4 on the CLI) and MQ9305 at `/start/at` in a replay. Simulation and recorded inputs accept
event, invoke, guard (`assume` keys) and actor names, resolved to ids when exactly one element has the name. MQ9502 to MQ9504
have passing rows, and MQ9503 has separate rows for the statement limit and the deadline (`ProcessExpressions.Open` takes limits).
No fixture expectation needed a correction; the 13 scenarios pass.

**Status P3, stage (explorer, dialogs, menus), 2026-09-30:** built and verified. The rail has **Processes** after Domain
model (`EXPLORERS`, `SIDEBAR_VIEWS`, the page-state and layout id lists, search place `processes`). The tree
(`explorer/tree.ts`, root `@processes`) groups processes by domain, nested, hiding domains without processes; a process
row (`@processes/p:<id>`, secondary "lifecycle · Invoice.status · 4 states": the attribute name once the row is expanded,
read from the subject's document) holds States (nested) and Events from its document and Scenarios from the index (steps
and status: passed, failed at step n, not run; the folder shows "✓ n passed" from the last verify, `explorer/processApi.ts`);
the Actors folder lists type and stereotypes. `scenario` is a new kind folder with placement `processes`. The Processes tree
is rebuilt rather than patched on a process, actor or scenario change. A state, event or scenario row opens the process
editor on its tab and records the node in `app/processFocus.ts`; until P3b a placeholder frame (`app/PendingEditor.tsx`,
wired in `editors/EditorTabs.tsx`; `EDITOR_KINDS` gains process, actor, scenario) shows the tabs and the node. Dialogs
(`explorer/processDialogs.tsx`, models in `explorer/processCreate.ts`) save one batch each: New process (a lifecycle
updates the subject's `lifecycle` and, for "New status attribute and enum", creates the enum and adds the attribute in the
same batch; "Start from its members" is on by default), New actor (the goals field when a chosen stereotype's extension
declares `goals`, from `GET /api/project` extensions), New scenario (Empty: one step on the first event; Record from
simulation disabled until P4), Import XState (dry run preview, then Apply), and Delete process (its scenarios in the same
batch). Menus: Open, Open in new tab, Simulate (disabled, with the note), Verify scenarios (Output panel), Export XState
(download), Where used, Move, Rename, Delete; New process and Import XState on a domain group. Mock: `mocks/model/processSeed.ts`
adds PurchaseApproval (the fixture's ids, three actors), InvoiceLifecycle (Invoice.status) and three scenarios; the
`drift` scenario (`?mock=drift`) adds Payment.status and PaymentLifecycle with MQ9203 (the mock validator's
`enumDrift`); `mocks/processHandlers.ts` answers the six operations (recordings `simulateProcess.budget-rejected.json`,
`exportProcess.json`, `verifyScenarios.*.json`, `importProcess.json`, `syncEnumFromProcess.json`, recorded by
`ProcessEndpointTests`; verify fails the scenarios `MOCK_FAILURES` lists). Left for P3b: the process, actor and scenario
editors (6.2) and the Problems quick fixes.

**Status P3, stage (process, actor and scenario editors), 2026-09-30:** built and verified. `editors/process/` replaces the
placeholder frame (`app/PendingEditor.tsx` removed): `ProcessEditor` in the 3.6 frame with the top controls
(`ProcessControls`: name, domain, use, subject, bound attribute with the MQ9203 badge and **Sync enum**: the dry run's
added, removed, reordered and refused in a confirm, then the apply, one undo step on the enum) and the tabs States (tree
grid; Ctrl+Enter a sibling, Ctrl+Shift+Enter a child, Ctrl+Delete refused while a transition enters the state),
Transitions (with the Guards and Actions grids; the expression cell carries an MQ9501 marker from a parse-only check and
from the engine's diagnostic; a gate is added or removed from its transition's row), Events (payload in the attribute
grid), Gates (one form per gate, N of M), Context and Scenarios (Replay = verify one, Open, Refresh expectations = the
`refresh-scenario` batch operation, Delete). One generic keyboard grid (`editors/process/Grid.tsx`, ROW_H, entered as
the attribute grid, a cell kind per row, row problems from the Problems store, cell markers) serves every grid; each
commit is one draft change and one undo step. The pure model is `model/process.ts`. The editor opens on States, or on the
tab and node the explorer asked for (`useProcessFocus`, then `clearProcessFocus`). The selection inside the editor
(`editors/process/selection.ts`) drives the inspector (`inspector/ProcessSections.tsx`): State, Transition, or Process
(use, subject, bound attribute, Code generation hints, Source as read-only JSON); Actor (type, used by) and Scenario
(process, outcome, steps, status). `editors/actor/ActorEditor.tsx` (name, type, chips, used by; Code generation; Where
used) and `editors/scenario/ScenarioEditor.tsx` (name, process, outcome; the steps grid with each step's replay status).
Mock: MQ9501 in `mocks/model/validate.ts`; `refresh-scenario` answered as a saved no-op in `mocks/model/store.ts` (it fell
to the delete branch before). A state's display name is translated in the process's Translations section (no per-node
section). Left: the Problems quick fixes (MQ9001 set-initial, MQ9203 sync-enum with the dry run, MQ9302-MQ9304
refresh-scenario) in `problems/`, and P4.

**Status P3, stage (Problems quick fixes, user guide), 2026-09-30:** built and verified; round P3 is complete. The Problems
panel shows a fix button beside a saved diagnostic whose rule has a catalog `quickFix` or an editor-built fix
(`problems/quickFix.ts`, pure: `hasQuickFix`, `fixDocuments`, `deriveQuickFix` per the derivation table;
`problems/applyQuickFix.ts`: flush the drafts, re-derive from the saved documents, one batch, one undo entry over every
document the fix may change, then the index and validation refresh; `problems/QuickFixButton.tsx`). MQ9001 **Set
initial** (`set-initial`, `id` the process or the compound state at the pointer, `target` the first child, a picker when
there are several; an initial on a non-compound state or with no children becomes **Remove initial**, a plain update);
MQ9203 **Sync enum** (the dry run's plan in a confirm, then the apply of 4.4); MQ9302-MQ9304 **Update expectations from
replay** (`refresh-scenario`); plain updates for MQ9013 (remove the node), MQ9016 (remove the member), MQ9102 (add to
signers), MQ9105 (add the signer to the event's actors), MQ9205 (the subject's own bound attribute's `default` = the
initial root state's name; none for an inherited or virtual attribute); MQ9201 **Set lifecycle on the subject**
(pointer `/subject`) and **Make it this entity's lifecycle** (an entity's `/lifecycle` naming an orchestration) as
`set-lifecycle`, built in the editor, so no per-diagnostic arguments were added to the catalog. As built: undoing the fix
of an error re-introduces that error, which the element-save rule refuses (engine and mock alike), so such an undo
reports "Cannot undo"; the undo of a warning fix restores the documents. The mock store answers `set-initial` and
`set-lifecycle` (it fell to the delete branch before). The user guide has "Processes, actors and scenarios".

**Status P3, review fixes, 2026-09-30:** built and verified. Grid rows are ROW_H (a 20 px `icon-row` button variant in
`components/ui/button.tsx` for row actions in the process grids and the attribute grid; e2e asserts every `*-grid-row`
height). Row keys: Ctrl+G adds or removes an event transition's gate, R replays and Shift+R refreshes a scenario; the
Scenarios hint shows (Grid renders the hint with `onKey` or `onRemove`). A select cell moved by the keyboard commits once
(Enter, Tab or blur); a pointer choice commits at once. F12 / Shift+F12 on reference cells (`GridColumn.definition`,
`useGridTargets` in `shared.ts`): elements open, process nodes select on their tab. The selected state's Invokes grid
(`StatesTab.tsx`: name, type, process, actors; a removal a transition waits on is refused). The gate's Required field is
a draft edit saved on blur or Enter. New process: always starts from the enum's members (the checkbox is gone), the new
status attribute defaults to `Initial` (MQ9205), and the subject's previous lifecycle is unbound in the same batch (a
plain update: the engine's `set-lifecycle` reads the pre-batch snapshot, so it cannot target a process the batch
creates). Use and Subject go through `set-lifecycle` (`editors/process/api.ts` `setLifecycle`, one undo entry over the
process, the entity and the previous partners); Lifecycle is offered only once a subject is set. New scenario… from a
process's row or folder presets that process (`processOfKey`). A collapsed lifecycle row reads Subject.attribute (the
explorer reads the process and subject documents once per forest). Mock: `mocks/model/validate.ts` reports MQ9001, MQ9201
(process side) and MQ9205; the seed binds Invoice to InvoiceLifecycle and the drift scenario binds Payment; the new
`?mock=lifecycle` scenario has one finding per quick-fix kind (e2e `processReview.mock-only.spec.ts`); the contract suite
calls every process operation and the process batch ops. Deferred with reason: per-state Translations (above).

**Status P4, stage (canvas, layout, process diagrams), 2026-09-30:** built and verified. `src/editor/src/canvas/statechart/`
on the canvas library renders the Chart tab (the process editor now opens on it; the simulation panel is docked below):
`chartModel.ts` (states, regions, pseudo-states, one edge per target with `event [guard] / actions`, `after 5d`, `done`,
`always`, `done: <invoke>`; targetless transitions as rows inside their state; the gate badge "N of M" with the signers
in its tooltip; the bound enum member of a lifecycle's root states; MQ9003, MQ9004 and MQ9006 badges by pointer; a
collapsed container as one box with the count it hides), `chartLayout.ts`, `diagram.ts`, `keyboard.ts`, `edits.ts`
(pure, unit-tested), the nodes, the edges and `StatechartCanvas.tsx`. Layout (toolbar, Ctrl/Cmd+L; the whole chart or
the selected container) is one call of the layered layout library in its worker in hierarchical mode
(`INCLUDE_CHILDREN`, direction right, node spacing 32, layer spacing 64, orthogonal, edge labels sized so the layers
make room for them, `MODEL_ORDER` cycle breaking on the top graph only, because the library's hierarchical mode fails
when a container sets an ordering strategy); `fitTree` then sizes each container bottom-up to its children plus padding
and a parallel state's regions are packed side by side; when the worker fails, a bottom-up per-container layout with the
fallback library stands in. State sizes are computed from their text, so nothing waits for measuring. Positions live in
the process diagram (2.7): members with `x`, `y` relative to the parent state, `width` and `height` on containers,
`collapsed`; one diagram per process, named after it in its domain, created as one undoable step by the first drag,
Layout, pan or zoom, or collapse (a chart without one is drawn from a layout held in memory); moves save through the
diagram's draft, the viewport as the entity canvas saves it. A state without a place (N, Shift+N, the States grid) is
placed by `placeMissing` inside its container only; its containers only grow; the placement joins the add's undo step;
saved positions move only by Layout. As built, where the design left a choice: index rows of process diagrams carry
`process` (`maquettiste-index/e9`), which the Diagrams explorer shows as "statechart of <process>" and `openDiagram`
routes to the process editor's Chart tab (the Domain model picker leaves process diagrams out); deleting states that the
diagram holds is one batch of the process and the diagram (also from the States grid; Delete process deletes the diagram
with it); the delete dialog lists the transitions entering the states and deletes them with the states; F12 on an edge
opens its gate, else its guard, else its event; with nothing selected an arrow selects the initial state, Escape on a
transition selects its source; in T mode the arrows pick among every state drawn. After P6, on the owner's request (the
chart had no visible way to connect states): a handle on the right edge of every state (`chart-link-handle`, a filled dot
that grows under the pointer)
starts the drag through the library's connection machinery, the drop lands on whichever state is under the pointer
(`elementsFromPoint`, so an edge's hit path over the state does not hide it), and in T mode a click on a state or
hovering it picks the target; because the chart draws its edges above the open containers they cross (z 1000 plus the
nesting depth), leaf states sit above every edge (`z-index: 2000` on a node holding `[data-leaf]`), which is also what
lets the handle take the pointer where an outgoing edge starts. The owner's second look (the dot was invisible on a
chart squeezed under the header fields and the Problems panel) made a process start with its details folded
(`mq.editor.details.process` defaults to hidden; the inspector carries the same fields) and the dot always visible. The
owner's third look (a chain laid out in one row hid its skip transitions, drawn straight through the states between):
`routes.ts` lifts a transition whose run would cross a drawn state over the states it crosses (a backward one below),
in lanes from the inside out by the length of the stretch (clearance 20, lane gap 24), derived from the boxes on every
render since routes are not saved; the layout itself is unchanged (a chain is one row, the lifted edges show the
branches). Unit tests `statechart-routes`. After 0.5.0 two CI failures: the chart's first-paint budget missed on the
hosted runner (391 ms against 313; 224 ms locally, no regression), so `editor.yml` sets `MQ_BUDGETS_ADVISORY` on a
hosted runner and the budget spec then records a miss on the report instead of failing, as gate3.yml does for the bench;
and on the macOS runner the three pack tests that restore and build the generated solution failed (`nuget.g.props
already exists`, then the test host could not load `Processes.Data`), the temp folder there lying under `/var`, a link
to `/private/var`, so `TempRepo` now resolves its root's real path before any tool runs in it. Budgets
(`statechart-budget.mock-only.spec.ts`, `?mock=chart400`: 400 states nested three deep with parallel regions and 321
transitions; run as the `mock` project's teardown, alone): first paint 228 to 236 ms (budget 250), layout in the worker
265 to 270 ms on the first Layout and 160 to 177 ms after (budget 400). Tests: unit `statechart-model`,
`statechart-layout` (bottom-up sizing over a fake engine and the real library), `statechart-keyboard`,
`statechart-diagram`, `statechart-edits`; Playwright `statechart.mock-only.spec.ts`. Left: the MQ9013 quick fix and an
XState import `into` a process do not drop the diagram's members of removed states (the save is refused with MQ2001 on
the diagram until they batch the diagram as `changeWithDiagram` does); a grown container may overlap a sibling until
Layout.

**Status P4, stage (simulation panel), 2026-09-30:** built and verified. `editors/process/simulation/` holds
`session.ts` (the pure client session: start, inputs, selection, the last answer; `addInput`, `removeFrom`,
`selectEntry`, `resetSession`, `restart`, `loadScenario`, `requestOf`, `viewOf`), `useSimulation.ts`
(`SimulationRunner`: simulate is called 150 ms after the last change with the whole input list, the call in flight is
aborted, and the draft is sent as `document` while the process has unsaved edits; a 422 shows its diagnostics in the
panel), `model.ts` (the pure rows: enabled rows with their default input, pending, the last step's lines, the trace, a
replay), `requests.ts` (the explorer's requests) and `SimulationPanel.tsx` (Start, Enabled, Time, Pending,
Configuration, Last step, Trace, Record to scenario… and Replay scenario…, in `ROW_H` rows with collapsible sections).
After every answer and every selection the panel publishes `setSimulationView` (`simulation/view.ts`: the shown entry's
configuration, the transitions it took, the step) and the canvas highlights the active states and flashes the taken
edges once. Every configuration, guard result, refusal, gate progress and verdict shown comes from simulate or verify;
the browser computes none of them. Entry points: the Processes explorer's **Simulate** opens the process editor pinned
on Chart with the panel shown; New scenario… **Record from simulation** creates nothing: it opens the Chart with the
panel recording under the given name, prefilled in Record to scenario…. **Record to scenario…** is `POST scenarios`;
the new scenario's index row and validation follow, and one undo step deletes it. **Replay scenario…** takes each
step's verdict from verify and the states after it from simulate with `scenario`, stops at the first failure and shows
expected and actual side by side; Configuration and the canvas follow the selected replay step. As built, where 6.4
left a choice: a start edit restarts the session (its inputs are dropped); raising while an earlier trace entry is
selected drops the inputs after it; while an earlier entry is selected, Enabled, Time and Pending wait for the last
entry (the answer describes only the last one; no second call is made); a row's default input is the first allowed
actor (none when any actor may), the payload defaults, a fresh signer `signer<n>`, the gate's first meaning, every stub
guard assumed true, and for time the delay to the next timer; the number keys work while the focus is in the panel;
sessions are kept per process for the page's life; record flushes the process draft first (the operation replays the
saved process); the panel starts folded to its toolbar (the chart keeps its room: at a 900 px window the editor's header
and the bottom panel leave the tab about 220 px) and expands on a Simulate or Record request or by its chevron, the
choice kept per browser (`mq.simulation.open`); the element editor's title row gained a chevron that folds the display
names, description and top controls away (`mq.editor.details.<kind>`), since the inspector shows the same fields. Mock: `mocks/model/stepper.ts` is a reduced port of the interpreter that answers simulate for any input
list and fills record's `expect` and `outcome` (the recording still answers the pristine BudgetRejected request): initial
entry, parallel regions, shallow and deep history, choice through `always`, priority and conflict removal, guards by
expression or `assume`, actions, actors, gates with audit records, `done` for compound and parallel states, timers on a
simulated clock, pending invokes, MQ9305, MQ9306 and MQ9507; it leaves out sub-process instances (a `process` invoke
waits like a service task), MQ9505 and MQ9506; its BudgetRejected trace equals the engine recording. Tests: unit
`stepper` (17), `simulation` (14, with the debounce and abort over a fake fetch), the contract suite for simulate with
several inputs, a draft, a 422 and a record; Playwright `simulation.mock-only.spec.ts`.


**Status P4, review fixes, 2026-09-30:** built and verified. Deleting states with the diagram (`changeWithDiagram`, the
canvas and the States grid) saves both drafts first and refuses, changing nothing, while a draft of the process or of
the diagram remains (`DraftManager.flushSaved`: false while a draft could not be saved); Record and Replay refuse the
same way. Payload fields keep the text typed and convert it when the input is built (`inputOf`), so `1.5` can be typed;
a row's edits merge the payload and the assumptions field by field (`mergeRowEdit`). First paint: the canvas draws no
node until the states have their places (the diagram loaded, or the first layout done), and the time runs from the
tab's first render to the frame after the first placed state node is in the page; the budget spec asserts target × 1.25
here and in CI (no shared-runner allowance) and checks the states timed stand at distinct places: first paint 219, 278
and 229 ms on three runs (budget 313), layout in the worker 268, 265 and 267 ms (budget 500). A new state's placement
waits for the process's save to settle (`whenSettled`) and names only the states the saved process holds; a refused
save leaves the state unplaced until the process saves again. `reapplyDiagram(disk, ours, base)` re-applies only what
our draft changed since its base, field by field, reads a missing `collapsed` as false (an expand is re-applied), does
not bring back a member removed elsewhere, and keeps their viewport unless we changed ours. Mock: simulate with a
scenario always uses the scenario's start; MQ3001 no longer applies to diagrams (the engine's name keys have none); the
difference in how the mock and the engine answer a reference error on a save is left as it is. A replay
whose outcome failed (step -1) says "Outcome failed" (`replaySummary`). Shift+F12 on the canvas lists where the selected
state or transition is used (`showReferences`). Deleting a confirmed incoming transition's target removes only that
target; the transition goes when none is left. A selected state hidden inside collapsed containers (selected on the
States grid or in the explorer) expands them as one follow-up of the diagram's draft (in memory without a diagram),
chosen over selecting the container because the selection is shared with the grids and the inspector. Accessibility:
each drawn state is a tree item (`role="treeitem"`, `aria-selected`, `aria-expanded` on containers, DOM id
`mq-chart-state-<id>`) whose React Flow node is its tree (the library's `application` wrapper sits between the nodes and
the canvas, so the canvas cannot be the tree); edge labels carry `mq-chart-edge-<edge>`; the focused canvas sets
`aria-activedescendant` once the element is in the page, and a polite live region announces the selection; the axe scan
of the chart is in `statechart.mock-only.spec.ts`. Labels moved to `CHART_LABELS` and `SIMULATION_LABELS` (state types,
the state and transition names, notices, edge words, the gate tooltip, the assumption toggle). The edge flash alternates
its class with every result flashed (a counter, not the parity of `seq`, which also grows on selection changes), so a
new result restarts it. Each simulate call is tagged with the session's `version` (it grows with every change of the
start or the inputs) and `withAnswer` drops an answer for an older version. A process rename (the explorer, the editor
or the inspector: every one saves the process's draft) renames its diagram when the diagram carries the old name (and
its display name when it carried the old one): `DraftManager.onSaved` calls `followProcessRename`, whose diagram edit is
a follow-up saved at once, so the rename and the diagram's are one undo step in two writes, not one batch (the draft
path saves one element). Duplicate is hidden on a process diagram's row. Tests: unit `statechart-actions` (new: the
delete with its diagram, the refusal on an unsaved draft, the rename followed and undone, a diagram named otherwise
left alone), `simulation`, `statechart-edits`, `statechart-model`, `canvas-model`, `drafts-undo`, `create`,
`mock-model-index`, the contract suite; Playwright `statechart.mock-only.spec.ts` (Shift+F12, the hidden selection,
aria-activedescendant and the live region, axe), `simulation.mock-only.spec.ts` (the highlight and the flash) and the
budget spec.

**Status P5, stage a (resolved model, scopes, helpers), 2026-09-30:** built and verified. `ResolvedModel` gains `processes`,
`actors` and `scenarios` (`Resolution/ResolvedProcesses.cs`, `ResolveRun.Processes.cs`; engine-design.md section 7.1a) with every
member of section 4.3, and `entity.lifecycle`. The state tree, paths, document order, depth, initial children, history defaults and
priority come from the interpreter's `StatechartModel`; `after_ms` and `iso_duration_ms` use its fixed spans; a transition's `label`
follows the canvas's edge label (`ProcessText`, one rule with `chartModel.ts`, compared on the fixture by
`statechart-label-parity.test.ts`). `pack.json`'s `each process`, `each actor` and `each scenario` plan one unit per element keyed
by its id with the aliases `process`, `actor` and `scenario`; `where.database` and `where.abstract` on them are MQ6001 at pack load;
`generation.skip` applies. `state_path` and `iso_duration_ms` join the built-ins (MQ6006 names a text that is not a duration). The
Units grid's scope help, the Scope picker, the preview's element picker and the mock's template context know the three scopes;
`GET /api/templates/context` lists their aliases, members and the two helpers. As built, where 4.3 and 7.1 left a choice:
`atomic_states` and `is_atomic` are what a configuration reports as active leaves (atomic and final states, never history or
choice); `depth` is 1 for a child of the root; `initial` is set on compound states only; `region_index` on the children of a
parallel state; `bound_member` only on a lifecycle's bound root states (the enum member of the same name); `context` holds the
declared attributes only (no stereotype virtual attributes: the interpreter's context has none); an action's `used_by` lists
transitions, then the states whose entry or exit run it; an actor's `events` lists the events that name it (an event open to any
actor is not listed on every actor); `gate.audit` is a list of `RAuditField` (`name`, `type`, `required`, `description`, `values`,
`attribute`) whose fixed fields come first and whose `signer`, `actor`, `meaning` and `reason` are not required (a discarded record
has none); scenario maps key by attribute or guard name; a transition's `display_name` is its `displayName`, else its label.
Dependency keys: every process node lists its process file's `e:` key with the subject's and the bound enum's, plus the `e:` keys
of the actors and processes it names; a scenario and its steps list the scenario's and the process's; the lists carry `k:process`,
`k:actor` and `k:scenario`. Deviation from 4.3: `r:<process id>` sits on `process.scenarios`, not on the process object, because a
process's referrers are its scenarios and the key on the object would re-render every process and sibling scenario unit on any
scenario edit; an actor's `processes`, `events` and `gates` carry `r:<actor id>`. Tests: resolver golden
`Resolution/Golden/processes/resolved.txt` and `ProcessResolutionTests` (keys, the interpreter's chart, durations),
`ProcessScopeTests` (scopes, keys, `where` by tag, stereotype and package, skip hints, explain and preview wording), three
MQ6001 cases in `PackLoaderTests`, `ProcessRenderTests` (reads and recorded keys, the helpers and their MQ6006, a JavaScript helper
over an `RProcess` proxy), `ProcessGenerationTests` (editing a scenario re-renders only its unit; editing a process re-renders its
unit, its seven scenarios' and the two actors whose events it declares; incremental equals forced; two cold runs byte-identical),
the template context test, and the editor's `generate-units`, `preview-scope`, `mock-pack-authoring` and `statechart-label-parity`
tests. The billing golden and the determinism tests are unchanged; the generation validation golden's MQ6021 message lists the
three new scopes. Left for P5: the pack units of 7.2 (`csharp-dapper`, `process-docs`, `sql-ddl`'s `process-tables`), the
TypeScript mirror, the generated interpreter's conformance and gate 3.

**Status P5, stage b (packs, interpreter, dispatch, scenario tests, gate 3), 2026-09-30:** built and verified. `csharp-dapper`
gains the fourteen units of 7.2 with their ids, outputs and modes (`process-states`, `-definition`, `-contracts` overwrite;
`process-handlers`, `-services`, `-machine`, `-store`, `dispatch` and `interpreter` pairs; `process-endpoints` regions;
`dispatch-registry`, `dispatch-behaviours` and `actors` overwrite; `scenario-tests`), shared functions in `_process.scriban`, and in
`helpers.js` the 7.1 expression translator `cs_expression` (literals, `context.x`, `event.payload.x`, `event.name`/`actor`,
comparison, `&&`, `||`, `!`, arithmetic, `?:`, and an action's object literal as `call.Context with { ... }`; anything else is a stub
in the handlers companion with the expression as a comment), `ulid_uuid` and the `process_model` selector. `sql-ddl` gains
`process-tables` (`<db>/processes/<process>.sql`: instances, history and audit tables with the gate audit fields and every audit
attribute), off unless `processTables` is `true`. As built, where 7.2 to 7.4 left a choice: the model-level units (dispatch,
registry, behaviours, interpreter, actors) run through a selector that returns the first process, so a model without processes
gets none of them and the billing goldens do not move; per-process files are under `Processes/<Process>/` in the process's
package namespace, the runtime in `<namespace>.Runtime`, the dispatcher in `.Dispatch`, the actors in `.Processes`; commands are
`<P><Event>Command(ProcessEnvelope Envelope)` with the payload (and a gated event's audit attributes) as init properties, because a
payload attribute may share a name with an envelope field (`reject`'s `reason`); besides the event commands each process has
`<P>StartCommand`, `<P>InvokeResultCommand` and `<P>TimersDueCommand`, which the dispatcher routes like the others; `<P>Transitioned`
is one record per input that moved the configuration (`From`, `To`: the active leaf paths); the audit record is one typed record
per gate built from `gate.audit`, and `actor` and `meaning` hold names, as the envelope does. The command handler lives in the
handlers pair (7.4's "handler stubs in pair mode"); the store companion starts as an in-memory store; the registry is a list of
`HandlerRegistration<TCommand>` the dispatcher matches by pattern, with `Register(Action<Type>)` for a container; the pipeline's
hooks are partial methods of `Pipeline` (`Authorize` and `BeginTransactionAsync` required, `OnValidate` and `OnLog` optional), and
a behaviour's refusal reports the instance's states through the handler. The interpreter (`Runtime/Statechart.g.cs`) keeps no
state between calls: it reads and writes `ProcessSnapshot<TContext>`; timers fire on `<P>TimersDueCommand` at the clock's
instant, each as its own macrostep at its due instant; a sub-process invoke is handed to `IInvokeHost` and reports back like a
service task (the engine runs it in the same interpreter; the fixture has none). Endpoints and scenario tests are written only
when `endpointsFolder` (a committed root) and `testsFolder` are set. The fixture's settings gain `outputs.allow` (`db`,
`src/Processes.Data/Custom` committed, the two `Generated/` folders built) and the pack parameters; `src/` holds the solution
(isolation props, `Processes.Data` with the committed companions: `notShipped`, the two services and the review hook, the stores
over the generated repositories of `SalesOrder` with `configuration`, `SalesOrderHistory`, `PurchaseRequest`, `ProcessInstance` and
`GateSignature`, the authorization hook; `Processes.Tests` with `ScenarioHost` on in-memory SQLite built from `db/main/schema.sql`).
`ProcessScopeTests` now writes under `db/` and `PackAuthoringTests` counts five `sql-ddl` units. Tests
(`tests/Maquettiste.Packs.Tests/ProcessTests.cs`, 9): goldens `golden/csharp-dapper/processes` and `golden/sql-ddl/processes`;
`--jobs 1` and `--jobs 8` byte-identical; a second forced run writes nothing and `--check` is clean; an edited companion and an
endpoint region survive a forced run; no `System.Reflection`, `GetType` or `Activator` in the registry; the engine's replay of all
13 scenarios (48 steps) equals the states, acceptance and outcome the generated tests assert; the fixture solution builds with
`-warnaserror` and its 13 tests pass; with `notShipped` inverted, `CancelRefusedAfterShipping` fails. Gate 3
(`tools/gate3.sh`, `.github/workflows/gate3.yml`, `src/editor/tests/e2e/gate3.spec.ts`), numbers from a run of the merged tree
(stages a to d and the review fixes below, image `mattjcowan/maquettiste:p5fix`, 24 cores), end to end: prepare 6.5 s; walk 7.4 s
(PurchaseApproval simulated and recorded as `RecordedInTheEditor`, and a SalesOrderLifecycle path recorded under the same name so
each process has a panel-recorded scenario, then plan and apply); criterion 1 validate 0 errors, 0 warnings, every file canonical;
2 and 3 verify 16 of 16 scenarios (the fixture's 14 and both recorded ones); 4 and 6 `generate --check` clean after the editor's
apply, 110 files (the three packs) byte-identical at `--jobs 1`, `--jobs 24` and in the editor's apply, two exports of each process
identical, the solution builds with `-warnaserror`; 5 16 of 16 generated scenario tests pass; 7 both processes export, import
`--into` themselves and export again byte for byte; 8 bench (5,000 entities, 1,000 processes, 5 of 400 states, 5,000 scenarios,
jobs 24): macrostep p95 0.006 ms (0.2), guard p95 0.003 ms (0.05), simulate 200 inputs 12.7 ms (30), rules of one 400-state
process 2.9 ms (10), whole-model rules 359 ms added (400), replay 2.74 s (3), export 5.8 ms (50), import 8.3 ms (50); no miss.
The generated process code documents every public member (no CS1591 with documentation on). Left: the P2 conformance charts are
not replayed against the generated interpreter (the fixture's scenarios are; section 10).

**Status P5, stage c (the TypeScript mirror), 2026-09-30:** built and verified. `samples/typescript-pack` (version 1.1.0) gains the
section 7.2 units with the C# pack's ids: `process-states` (the state union of every path, path constants, the atomic states and,
for a lifecycle, the bound enum's value per bound state with `<p>StatusOf`), `process-definition` (the chart as a `const` object
`satisfies ChartDefinition`: states by path in document order, transitions in priority order, names for events, guards, actions,
invokes, gates and actors, each with its model id), `process-contracts` (context type, one command per event with its typed
payload plus a gate's audit attributes and the envelope, the `:start`, `:tick`, `<invoke>:done` and `<invoke>:error` commands,
`<P>Transitioned`, one audit record type per gate), the pairs `process-handlers`, `process-services`, `process-machine` and
`process-store` (`<p>.<part>.gen.ts` importing the companion `<p>.<part>.ts` through a declared interface checked with
`satisfies`), `process-endpoints` (regions, one per event), `dispatch` with `dispatch-companion` (`dispatch/dispatch.gen.ts`,
`dispatch/pipeline.ts`), `dispatch-registry` (a map from command type to handler `satisfies Registry`, a mapped type over every
command type, so a missing handler fails the build; no reflection), `dispatch-behaviours` (validation, authorization, logging,
transaction and outbox as composed functions), `interpreter` with `interpreter-companion` (`runtime/statechart.gen.ts`,
`runtime/process-host.ts` with `ManualClock`, in-memory timers, invokes and process store), `actors` and `scenario-tests` (one
`node:test` file per scenario); new parameters `processFolder`, `routePrefix` and `testsFolder`, and helpers `ts_value_type`,
`ts_import` and `ts_comment`. As built, where 7.3 to 7.5 left a choice: the model-scope pairs are two units each (an overwrite unit
and a `once` unit, both of file blocks written only when the model has processes) because a model-scope `pair` unit always writes
its files and the billing output had to stay byte-identical (checked against the previous pack; the entity, model and schema
templates are untouched); every input is a command through the dispatcher, including the start, invoke results and a clock tick
(`<P>:tick` moves the instance's clock to the host clock's time, so the instance clock moves only on ticks as the engine's does);
guard and action expressions are emitted as written on their own line inside typed functions, with the context typed from its
attributes (numbers for every numeric type, as the interpreter holds JSON values) and the payload typed loosely; a stub guard's
companion body starts as `return false` and a stub action's as `return {}`; the scenario tests run the companion's stubs and
assert that each guard a step assumes answered as assumed, so a companion that disagrees with a scenario fails it; the actor and
gate rules stay the interpreter's (refusals are audited), and the authorization behaviour only asks the pipeline's `mayActAs` hook;
command handlers live on the machine (`handle`: load, apply, save, history, audit, host and service notifications); endpoints are
framework-neutral (`{ method, path, handle(request, dispatcher) }`). Tests: `Maquettiste.Packs.Tests/TypeScriptPackTests.cs`
generates the pack over the gate 3 fixture: `--jobs 1` and `--jobs 8` byte-identical, a second, a forced and a check run clean;
a companion and an endpoint region keep their edits across a model change; billing gets no process file; with node 22.18 or later
and the editor's `node_modules` (TypeScript 5.9 and node's types, or `MAQUETTISTE_NODE_MODULES`), `tsc --noEmit` is clean,
`node --test` passes all 13 scenario tests, the result of the start and of every step (accepted, refusal, active states, changed
context, final) equals the engine's `ScenarioReplayer` replay, and flipping `notShipped` in its companion fails
`cancel-refused-after-shipping`. CI: job `typescript-pack` in `ci.yml` after `build-and-test` (node 24, `npm ci` in `src/editor`,
`MAQUETTISTE_REQUIRE_TYPESCRIPT=1` turns a skip into a failure); not a gate 3 criterion. Not covered by the fixture: sub-process
invokes (the interpreter runs them from the registry's programs) and audit attributes in a scenario step's payload (P5a resolves
payload keys against event payloads and context only, so such a key stays an id).

**Status P5, stage d (process-docs), 2026-09-30:** built and verified. `packs/process-docs` (1.0.0; parameters `diagrams`, default
true, and `title`, the index page's heading) writes Markdown to a committed root: `process-page` (each process, `processes/<process>.md`),
`actor-page` (each actor, `actors/<actor>.md`) and `scenario-page` (each scenario, `processes/<process>/<scenario>.md`), kebab-case
names, all `overwrite`, plus an `index` unit (model; a file block `index.md` written only when the model has a process or an
actor). Every page opens with `banner "<!--"` and holds no date. The process page: the description, a summary, the lifecycle
binding (the bound states and their enum members), the state diagram in diagram text (`stateDiagram-v2` in a fenced block), then
tables of states (type, initial child, history kind and default, region, entry, exit, invokes), transitions (numbered in priority
order: source, trigger, guard, targets, actions, gate with its count), gates (signers, required actors, repeat signer, reason,
meanings, and the audit record's fields from `gate.audit`), events (actors or "any actor", payload, transition numbers), context,
guards (the expression, or a named stub), actions, invokes, the actors involved (what each raises, signs and performs) and links
to the scenarios. The actor page: type, stereotypes, tags, `properties` (an array as a list, so a persona's goals), the processes,
the events it raises, the gates it signs (its signature required or counted) and its tasks (the invokes naming it). The scenario
page: the start instant and context, one table row per step (actor, input, accepted or **refused**, the expected active states,
notes: signer and meaning, reason, payload, assumed guards, changed context, the step's description) and the outcome; it shows the
recorded expectations, not a fresh run. As built for the diagram: a compound state is a nested block with `[*]` to its initial
child and from each final child (the top level likewise); the regions of a parallel state are nested blocks separated by `--`; a
choice state is the choice pseudo-state; a history state is a state labelled with its name and `H` or `H*`, with an edge `default`
to its default target (an edge rather than a note: the default is a transition in statechart notation, and the states table
repeats it); each transition with targets is one edge per target, labelled with `transition.label` (the canvas's edge label) and
`(gate N of M)` when gated, declared in the innermost compound state holding both ends (a parallel state is passed over, since its
body holds only its regions); a targetless transition is a note beside its source, one line `internal: <label>` each, as the
canvas lists internal transitions inside the state; ids are the state names, except a name two states share or a keyword of the
diagram text, which becomes `s_` and the path joined with `_`; `#`, `;`, `"`, `<` and `>` in diagram text are entity codes. The
fence's language tag names the syntax, because renderers draw only a tagged block; the pack's README names it once, and the
product's docs say "diagram text". The gate 3 fixture's `maquettiste.json` registers the pack (`packs.process-docs.output` `docs`)
and allows `docs` as a committed root (section 10's open risk); the roots of `csharp-dapper` and `sql-ddl` are added with their
process units for criterion 4. `ProcessScopeTests` now plans its in-memory units under `docs`, the fixture's allowed root. Tests:
`ProcessDocsTests` (golden `tests/fixtures/golden/process-docs/processes/`, 24 pages; `--jobs 1` and `--jobs 8` byte-identical; a
second run renders no unit and `--check` is clean; banner and final newline on every page; a syntax check of every diagram of its
own, since no renderer of the syntax is among the editor's dependencies: the statement forms the pack writes, balanced blocks and
notes, each id declared once, every edge's and note's ends declared, one initial edge per block, labels free of statement ends,
over the fixture and over `process-basics` with a repeated name, a keyword name and a targetless transition inside a compound
state; `diagrams: false`; the checker's own failing cases) and `PackLayoutTests` over the third pack. Left: embedding the pack in
the CLI's starter packs (`init --pack`, `pack new --from`) and the bench, if wanted.

**Status P5, review fixes, 2026-09-30:** built and verified. (1) The C# pack's model-wide units are `model` units whose templates
write file blocks only when the model has processes, as the TypeScript pack's: `dispatch`, `dispatch-registry`,
`dispatch-behaviours`, `interpreter` and `actors` overwrite, and the companions `Pipeline.cs` and `ProcessHost.cs` are the `once`
units `dispatch-companion` and `interpreter-companion` (a model-scope `pair` always writes); the `process_model` selector is gone,
so skipping the first process no longer removes the shared runtime, and billing still gets no process file. (2) The built-in
commands end in `Control` and every event command in `Command` (C#: `<P>StartControl`, `<P>InvokeResultControl`,
`<P>TimersDueControl`, descriptors `<P>:start`, `:invoke-result`, `:timers-due`, inputs `:start` and `:time`; TypeScript:
`<P>StartControl`, `<P>TickControl`, `<P><Invoke>DoneControl` and `ErrorControl`), the TypeScript machine's methods are
`send<Event>`, `complete<Invoke>` and `fail<Invoke>`, and a C# constant named like its nested class gets `Value`, so no event
name collides; every C# command implements `IProcessCommand`. (3) `gate3.sh`'s bench step is binding locally and advisory on a
GitHub-hosted runner (`RUNNER_ENVIRONMENT`, or `GATE3_BENCH_ADVISORY` 1 or 0): a MISS becomes a `::warning::` and the step
passes; the workflow sets the variable. (4) The helper `cs_line` turns every line break into a space in every comment context of
the C# process units, and `doc()` splits `\r\n` and `\r` too; the TypeScript pack's `ts_comment` already did. (5) The fixture
gains `CancelBeforeShipping` (submit, then cancel with `notShipped` true into `Cancelled`, final; 14 scenarios, 50 steps), and
BudgetRejected and ChangesRequested start at an amount of 25,000, above the fixture's `checkBudget` limit of 10,000; generated
refusal steps assert the refusal reason and every step with audit records their outcomes, from the new resolved `step.trace`
(the engine's replay, computed lazily once per scenario); the scenario host wires the services and fails a test whose service
disagrees with the result the scenario reports; the TypeScript stub companion throws until implemented, the scenario tests also
assert that no guard or action failed, and the tests write the rule before running; both packs' inversion tests flip `notShipped`
both ways and see both cancel scenarios fail. (6) Endpoint regions are keyed by the event's id in both packs (the TypeScript
endpoint now builds the command and the response outside its region); a renamed event keeps its region, with no MQ6010. (7) The
translator stubs a division unless both operands are `decimal` or `double` (or one is and the other a literal), ordering and
arithmetic on an operand that may be null or that is not a number, equality across kinds, decimal mixed with double, a `null`
test of an attribute that cannot be null, and an action value that does not fit its attribute; the pack README lists the subset.
(8) The C# interpreter reports a macrostep over the bound as the step's `Failure` and keeps the state reached, keeps the
instance's clock in the snapshot (set by the start, moved only by timers-due commands), treats a transition naming an undeclared
guard as never holding (`RTransition.GuardMissing`, `guardMissing`), writes actor and meaning ids in audit records, and the
authorization behaviour checks actors only (gates are the interpreter's). (9) Section 10's conformance risk says what is done and
leaves the P2 charts; `ProcessTests` runs the generated tests with the scenario host's trace on and compares acceptance, refusal,
states, audit outcomes, audit actor and meaning ids, and final with the engine's replay after the start and every step (64), plus
small charts for the bound, the clock and a missing guard. (10) A step's payload resolves gate audit attributes by name. (11)
Guards and actions are `Guard<Name>` and `Action<Name>` in C#, `guard<Name>` and `action<Name>` in the TypeScript stub interface,
which also passes the active state paths to stubs. (12) `ProcessSnapshot` carries `Version`; `I<P>Store.SaveAsync(snapshot,
expectedVersion, ct)` saves over the version the command loaded and throws `ProcessConcurrencyException` otherwise; the in-memory
companion and the fixture's stores check it (the fixture's over the version in the stored snapshot, no model change). (13) The
docs above, the fixture README (the three packs and the schemas in the recipe, `docs/` ignored, the companions as they are) and
the C# pack README match the tree. (14) A region of a parallel state gets its note for targetless transitions inside its own part
of the parallel block; the tests name the fence tag once as `DiagramFence`. (15) `HandlerRegistry.Register(IHandlerRegistrar)`
hands a container typed factories; the no-reflection test covers every generated `Dispatch/*.cs` and `Runtime/*.cs` and allows
only the registry's `typeof(T)` for `IServiceProvider`. (16) `after_ticks` keeps sub-millisecond durations (`PT0.0005S` is 5,000
ticks), and the C# pack schedules timers and advances test clocks in ticks. Tests: `ProcessTests` (10), `TypeScriptPackTests` (6),
`ProcessDocsTests`, `ProcessResolutionTests` (durations, audit attribute names, missing guard, traces); goldens rewritten for
the C# and process-docs processes and the resolved processes; the editor's recorded verify and simulate results re-recorded
and its seed's BudgetRejected start set to 25,000. Totals: `dotnet build -warnaserror` clean; Engine 1900, Cli 161, Functions
180, Bench 50, Packs 71 passed and 5 skipped (database containers); editor lint, typecheck, format and 667 unit tests clean, the
mock Playwright project 91 passed and 2 skipped (one axe check on the templates page flaked under load and passed on rerun).
Gate 3 over the merged tree (image `mattjcowan/maquettiste:p5fix`): every step passed with the numbers in stage b's paragraph
(16 of 16 scenarios verified and tested, 110 files byte-identical, every budget met, the bench binding). Left: the P2
conformance charts (section 10); the TypeScript store keeps no version (the finding named the C# interface); the TypeScript
clock stays in milliseconds (a sub-millisecond `after` is rounded down there).

**Status P6 and phase 3 close-out, 2026-09-30:** built and verified; phase 3 is complete. What each round delivered: P1
the process, actor and scenario kinds with their schemas, the structural rules MQ9001 to MQ9018, MQ9101 to MQ9106 and MQ9201
to MQ9205 with MQ9019 for refused operations, the `sync-enum`, `set-lifecycle` and `set-initial` operations and the gate 3
fixture; P2 the engine interpreter, expressions in the sandbox, scenario replay (MQ93xx, MQ95xx), the XState projection
(MQ94xx) and the six process operations over HTTP, MCP and the CLI with the bench cases; P3 the Processes explorer, the New
process, New actor, New scenario and Import XState dialogs, the process, actor and scenario editors and the Problems quick
fixes; P4 the statechart canvas with nested layered layout saved in the process diagram and the simulation panel that
records and replays scenarios; P5 the resolved model and the three scopes, the `csharp-dapper` process units with the
generated interpreter, dispatch and scenario tests, `sql-ddl`'s process tables, the `process-docs` pack, the TypeScript
mirror and gate 3; P6 the documentation: `docs/user-guide.md` (its "Processes, actors and scenarios" chapter checked
against the editor as built, with the rail, the actor and scenario menus, the details chevron, the Gates form, Shift-drag
selection, the chart's menus, the Clock start, the restart button, recording and the Use explanation corrected or added;
a new "The rules for processes (MQ9xxx)" table with all 49 rules, their severities and their fix buttons; a new
"Generating code from processes" section; the Validation families, the scope table, the example packs and the CLI's
`validate` row brought up to date), `docs/mcp.md` (46 tools, the `apply_batch` process operations, the import diagnostics,
`quickFix` in `list_validation_rules`), `skills/maquettiste-modeling/SKILL.md` (a Processes part and the three folders),
`README.md` (phase 3 and gate 3), `spec-errata.md` (E27 to E37, awaiting the owner), `docs/demo.md` (the rail order and the
tool count), and a sweep for third-party product names over the prose P6 wrote and the product's own text (only the export
format's name, the diagram syntax's fence tag and the pack README's one mention of it, the editor's font package, and
ignore entries for an editor's settings folder remain there; `docs/demo.md`, `docs/mcp.md` and the skill still name the
agent client, the container runtime and the code host a reader installs, and the pack id `csharp-dapper` names its
library). **Gate 3: pass**, as recorded in the P5 review-fix run above over the merged tree (image
`mattjcowan/maquettiste:p5fix`, 24 cores): validate 0 errors and 0 warnings, every file canonical; 16 of 16 scenarios
verified (the fixture's 14 and one recorded in the simulation panel per process); `generate --check` clean, 110 files
byte-identical at `--jobs 1`, `--jobs 24` and in the editor's apply, two exports of each process identical, the solution
built with `-warnaserror`; 16 of 16 generated scenario tests passed; both processes round-tripped through XState byte for
byte; every bench budget met. P6 changed documentation only (the Use explanation in New process… and on the editor's Use
field was a separate editor change), so the gate was not run again. **Left**, gathered from the status paragraphs above and this round's checks: the P2
conformance charts are not replayed against the generated interpreters (only the fixture's scenarios are; section 10);
the TypeScript store keeps no version and the TypeScript clock counts milliseconds (a sub-millisecond `after` is rounded
down there); neither fixture exercises a sub-process invoke in the generated code; the MQ9013 quick fix and an XState
import `into` a process do not drop the diagram's members of removed states (the diagram's save is refused with MQ2001
until they batch the diagram as `changeWithDiagram` does); a grown container may overlap a sibling until Layout; a Translations
section per state (a state's display name is translated in the process's own section); the mock and the engine answer a reference
error on a save differently, and the mock's stepper leaves out sub-process instances, MQ9505 and MQ9506; the gate 3 bench
step is advisory on a hosted CI runner (binding locally); a scoped validate over the 5,000-entity model has a fixed
cost of about 30 ms whatever it checks; `process-docs` is not embedded in the CLI's starter packs (`init --pack`, `pack
new --from`) nor run by the bench; the `get_schema` tool's argument description does not name the kinds `process`, `actor`
and `scenario`, which it accepts. Still open from `phase-3-brief.md`: L1 (explorer), L2 (performance), L3 (generation),
L4 (reference data) and L5 (localization), none run as the side track decision 10 allowed, and L6, where the owner now
applies E1 to E5 and E17 to E37 (E19 still waiting for generation-ui.md §10 Q3).

## 10. SPEC amendments and open risks

Errata for the owner to apply, entered in `docs/engineering/spec-errata.md` as rows E27 to E37 (2026-09-30, P6), where they await the owner:

| Id | SPEC | Proposed change |
| --- | --- | --- |
| E27 | Section 5, Actor row | Actor: `type` person, role or external system; a persona is an actor with a project stereotype carrying goals; no package |
| E28 | Section 5 | Add the element kind **Scenario**: a recorded input sequence of one process with the actor, expected states and context after each step, and the outcome; owned by its process |
| E29 | Sections 5 and 8, Event | Events are declared in their process in phase 3 and become an element kind in phase 4, keeping their ids |
| E30 | Section 8, transitions and gates | Add `trigger` (`event`, `after`, `done`, `always`, `invoke-done`, `invoke-error`), `external`, the gate's keys (`required`, `signers`, `requiredActors`, `allowRepeatSigner`, `reasonRequired`, `meanings`, `auditAttributes`) and the audit record shape; storage is a template or mapping decision |
| E31 | Sections 8, 14 and 22 | Expressions run in the script sandbox (no engine named); layout through a layered layout library; the open questions "Processes: runtime" and "Layout engine" answered (decisions 1 and 8) |
| E32 | Section 12, scopes | Add `each actor` and `each scenario` beside `each process`, with the variables of §4.3 |
| E33 | Sections 16 and 18 | Add the process operations, MCP tools and CLI verbs of §4.4; XState import keeps unknown config as extension data and turns inline functions into stubs |
| E34 | Section 6 | The entity `lifecycle` field (retires E3), checked against the process's `subject` |
| E35 | Section 14 | Add the Processes explorer on the rail and the process, actor and scenario editors |
| E36 | Section 8, typical outputs | Add dispatch contracts, a handler registry, pipeline behaviours, the pair and regions shapes for hand-written code; "diagrams for documentation" without a named format |
| E37 | Section 21, gate 3 | Name the fixtures and the criteria of §8.2; C# gates, TypeScript follows |

**Open risks.**

- Erratum to this design (P1): the first draft of §8.1 made `Fulfilment` parallel and targeted "a deep history of
  `Fulfilment`", but MQ9011 (§3) puts a history state inside a compound parent, and a parallel state's children are its
  regions. The fixture wraps the parallel state in a compound `Fulfilment` holding `Processing` and the history `Resume`,
  which keeps the scenario (hold and release restore both regions). If the interpreter of P2 or an importer ever needs
  history directly under a parallel state, MQ9011 changes first.
- Resolved in P5 (2026-09-30): gate criterion 4 needs `process-docs`, which the first fixture settings (`csharp-dapper` and
  `sql-ddl` only) did not name. P5 created the pack and added it to `tests/fixtures/models/processes/.maquettiste/maquettiste.json`,
  which now names the three packs and allows the roots `db`, `docs`, `src/Processes.Data/Custom` (committed) and the two
  `Generated` folders (built); gate 3 generates the pages into its copy, and the fixture keeps `docs/` out of git.
- The engine interpreter and the generated interpreters can drift on corner cases (conflicting transitions across
  regions, history with parallel descendants, `done` ordering). As built in P5, the gate 3 fixture's scenarios (14) are
  replayed against both generated interpreters and compared with the engine's replay after the start and every step (C#:
  `ProcessTests` runs the generated tests and compares what the running interpreter returned; TypeScript: the tests' step
  diagnostics), plus small C# charts for the microstep bound and the clock; any difference is a bug in the pack. Left: the
  conformance charts of P2 (built in C# test code) are not replayed against the generated interpreters, which would need a
  generated definition for each.
- Guards without expressions make scenarios depend on `assume` values: a scenario can pass while the real handler
  disagrees. The generated test calls the real handler and fails in that case, which is the intended signal.
- The C# expression subset may surprise authors whose expressions fall outside it; the preview shows which guards became
  stubs, and MQ9501 cannot catch it (the pack decides).
- Lifecycle persistence stores the root-level state in the bound attribute; parallel sub-states need the extra snapshot
  attribute the fixture models. A project that forgets it loses sub-states on reload; the store interface makes the
  snapshot explicit.
- `sync-enum` on a shared enum (MQ9204) changes other attributes' values; the operation refuses removals of used
  members, but reordering still changes integer storage of enums stored as integers. The dry run says so.
- Signer identities are strings the host supplies; generated authorization maps principals to actors in a hook, and a
  project that leaves the hook open signs as anyone. The companion's stub throws until implemented.
- Payload and context maps keyed by attribute id are safe under renames but hard to read in raw diffs.
