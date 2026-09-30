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
`after` (the duration text and `after_ms`), `guard`, `actions`, `external`, `gate`, `label`; an event has `name`,
`payload`, `actors`, `transitions`; a gate has `required`, `signers`, `required_actors`, `allow_repeat_signer`,
`reason_required`, `meanings`, `audit` (§2.3 fields plus `auditAttributes`); an actor has `type`, `processes`, `events`,
`gates`; a scenario has `process`, `start`, `steps` (each with names and paths resolved: event name, actor name, payload
by attribute name, expected state paths) and `outcome`. Reads record `e:` dependency keys as today, so a scenario's unit
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
| `POST /api/processes/{id}/sync-enum` | `{ dryRun?: bool, expectedHash? }` | `{ enum, added: [name], removed: [name], reordered: bool, refused: [{ member, referencedBy: [id] }] }`; a member still used by a default, `allowedValues` or a seed cell is refused, not removed |

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
| **Chart** | The statechart canvas (§6.3) with the simulation panel (§6.4) docked below it |
| **States** | A tree grid: name, type, initial, history, entry, exit, invokes, bound member (read-only); keyboard entry as the attribute grid |
| **Transitions** | A grid: source (path), trigger, event or duration, guard, targets, actions, external, gate badge; below it the **Guards** and **Actions** grids (name, expression in a one-line code field with MQ9501 markers, description, used by) |
| **Events** | Events grid (name, actors, used by); the selected event's payload in the attribute grid |
| **Gates** | One form per gate: transition, required N of M (M computed from signers), signers and required actors (actor pickers), allow repeat signer, reason required, meanings grid, audit attributes grid |
| **Context** | The attribute grid (`inspector/AttributeGrid.tsx`) over `context` |
| **Scenarios** | The process's scenarios: name, steps, status (passed, failed at step n, not run), Replay, Open, Refresh expectations, Delete |

The inspector's sections for a canvas selection: **State** (general; entry and exit; invokes; bound member; description;
translations; properties), **Transition** (trigger; source and targets; guard; actions; external; gate; actors, read-only
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
sibling state, `Shift+N` a child, `T` starts a transition from the selection (arrows pick the target, Enter confirms);
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
| `dispatch` | model | `Dispatch/Dispatch.g.cs` + companion `Dispatch/Pipeline.cs` | pair | The generated half holds the contracts and dispatcher; the companion, created once, orders the pipeline and holds the project's own behaviours |
| `dispatch-registry` | model | `Dispatch/HandlerRegistry.g.cs` | overwrite | A complete, derived list; hand additions go in the `Pipeline.cs` partner |
| `dispatch-behaviours` | model | `Dispatch/Behaviours.g.cs` | overwrite | Default behaviours derived from the model; their policy hooks are partial methods implemented in `Pipeline.cs` |
| `interpreter` | model | `Runtime/Statechart.g.cs` + companion `Runtime/ProcessHost.cs` | pair | The small interpreter (§4.1 semantics) is generated and never edited; its extension points (clock, timer scheduler, invoke host) are interfaces the companion implements, created once with in-memory defaults |
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
  is one of the event's actors; a gated event carries a signer; the hook maps the host's principal to actors), logging
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
  `Draft`. `Fulfilment` is parallel: region Payment (`AwaitingPayment` → `paymentReceived` → `Paid`, final; `after P30D`
  → `PaymentOverdue`), region Shipping (`Picking` → `pick` → `Packing` → `ship` → `Shipped`, final); `onDone` →
  `Completed` (final). `hold` from `Fulfilment` → `OnHold`; `release` → a deep history of `Fulfilment`. `cancel` →
  `Cancelled` (final), guarded by `notShipped`. Context `total`, `creditLimit`; guards with expressions and one stub
  (`notShipped`). Scenarios: small order, credit approved with two signatures, credit rejected and resubmitted, hold and
  release (deep history), payment overdue by time, cancel refused after shipping, repeat signer refused.
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
| 2 | `maquettiste process verify`: all 13 scenarios pass in the engine interpreter |
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

## 10. SPEC amendments and open risks

Errata to add to `docs/engineering/spec-errata.md` for the owner to apply:

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

- The engine interpreter and the generated interpreters can drift on corner cases (conflicting transitions across
  regions, history with parallel descendants, `done` ordering). The conformance suite of P2 is replayed against both
  generated interpreters in P5; any difference is a bug in the pack.
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
