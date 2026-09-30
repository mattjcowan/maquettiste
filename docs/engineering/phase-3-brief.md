# Phase 3 brief: processes

**Status (2026-09-29): answered; the design exists in `phase-3-design.md`.** The owner answered all ten questions:
(1) definitions and handler stubs only, with a small generated interpreter as a pack output; (2) a sales-order lifecycle
and a purchase-approval orchestration as fixtures under `tests/fixtures/models`; (3) scenarios recorded in the
simulation panel and turned into tests by a pack, C# first, TypeScript second; (4) the process owns the state list, drift
is an error with a "Sync enum from process" quick fix; (5) signers are actors, the audit record shape is modelled and its
storage is a template or mapping choice; (6) JavaScript in the existing sandbox; (7) unknown XState config kept as
opaque data and inline functions turned into named stubs, both with warnings; (8) a layered layout library, positions
saved in the diagram file; (9) events process-local until phase 4; (10) phase 3 starts now, L2 to L5 as a side track,
the owner applies the errata. Added: actors and scenarios are element kinds in phase 3, operations stay in phase 4,
generated code leaves room for regions and pairs, and the packs generate a loosely coupled dispatch.

Phases 1 and 2 are complete (README.md, "What is built"). Phase 3 is SPEC.md Section 21's "3 Processes": statechart
canvas, simulation, gates, XState import and export, process templates, closed by gate 3. This brief lists what the SPEC
asks for and what the owner must answer before the phase 3 design document is written.

## What the SPEC asks for

- **Model** (Section 8; Section 5's Process and Event rows). A process is a statechart with XState v5 semantics, in
  Maquettiste's own schema under `model/processes/`. State kinds: atomic, compound (with `initial`), parallel (`onDone`
  when every region is final), final, history (shallow or deep), choice. Transitions: `event`, `source`, one or several
  `target`s, `guard`, `actions[]`, delayed (`after`), internal or external, rework loops to earlier states as normal.
  `context` in the attribute model, typed event payloads, named guards and actions with a description and an optional
  JavaScript expression, `invoke` (sub-process, service task, human task), `actors` per event.
- **Two uses, one model.** A lifecycle has a `subject` entity and may bind its states to an enum attribute
  (`Invoice.status`) so the two never drift; an orchestration has actors and tasks, with or without a subject. The
  entity's `lifecycle` field, rejected in phases 1 and 2 (D7, errata E3), arrives with it.
- **Gates.** A transition that needs approvals before it fires: N of M signatures, required roles, whether one person may
  sign twice, whether a reason is required, the meaning recorded with each signature; gates generate their own audit
  records.
- **Editor** (Sections 8 and 14, the Processes workspace). A statechart canvas with nested containers for compound
  states, dashed separators for parallel regions and automatic layout; a simulation panel (raise events, watch the active
  configuration, evaluate guards in the sandbox); validation of unreachable states, dead ends without a final state,
  missing initial states and overlapping guards on one event, each an MQ rule with a catalog entry and a test.
- **Import and export** (Section 18). XState config is a projection of the process schema: export writes it, and import
  accepts XState JSON that uses named guards and actions (import is a first-release format).
- **Process templates** (Section 12's `each process` scope). Typical outputs: state enums, transition tables, state
  machine classes, event DTOs, one API endpoint per event, instance and history tables, gate audit trails, and diagrams
  for documentation. The example packs gain process units; the engine synthesizes no table for a process: instance and
  history storage is what a template or a mapping says.
- **Gate 3** (Section 21). A lifecycle and an orchestration process, both with parallel states and a gate, generate code
  that compiles and passes simulation-derived tests.

## Questions for the owner

1. Runtime (SPEC Section 22): generate only definitions and handler stubs, or also a small runtime that executes
   statecharts? Gate 3's "passes simulation-derived tests" reads differently under each answer.
2. Gate 3 subjects: which lifecycle and which orchestration? The reference app's sales order and a purchase approval
   are candidates; an example is not a requirement until you say so.
3. Simulation-derived tests: recorded event sequences from the simulation panel, turned into test files by a pack, or
   generated from the chart's paths? In which language for gate 3?
4. Enum binding: when a lifecycle binds to `Invoice.status`, which side owns the member list, and is drift an error or
   a quick fix?
5. Gates and people: are signers the project's accounts, roles in the model (actors), or only data the generated code
   receives? Where do the audit records live, given that the engine synthesizes no persistence?
6. Guard and action expressions: JavaScript in the existing sandbox, or a smaller expression language shared with the
   open "query predicate syntax" question?
7. Import scope: must import keep unknown XState config (inline functions, custom actors) as opaque data, or refuse it?
8. Automatic layout for nested and parallel states: which layout engine (SPEC Section 22 leaves it open), and are
   positions saved in the diagram as the entity canvas does?
9. Events (Section 5): are domain events a separate element kind in phase 3, or process-local until phase 4?
10. Phase 2 leftovers: which of the design documents' "Left" items (HANDOFF.md) come before phase 3, and do the SPEC
    amendments of errata E17 to E25 go into the SPEC document first?

## Carried over from phases 1 and 2 (not built)

None of these is built; question 10 decides which come before phase 3. Each names the design-document row it belongs to.

- **L1 Explorer** (explorer-redesign.md §6): Create entity from table, Copy SELECT, Copy DDL; Diagrams of this domain; New
  schema and New table; Open in new tab.
- **L2 Performance** (explorer-redesign.md §4.3, §3.5): first rows, first paint and build range around their budgets
  (27-39 ms vs 25, 141-251 ms vs 188, 36-52 ms vs 38); `model.changed` ~27 ms vs 20, highlight ~35 ms vs 20, reveal ~134 ms
  vs 63; General-mode walk ~109 ms vs 100.
- **L3 Generation** (generation-ui.md §8): a real source map in `PreviewResult`; Generate explorer context menus; CLI `pack
  show`, `explain`, `generate --dry-run --explain`; pack READMEs' `parameterSchema`; Units tab selector picker, 409 replay,
  Edit definitions.
- **L4 Reference data** (reference-types-seeds-localization.md §5 step 7): type list filters, `groupBy` other than category,
  Used by highlighting, F12/Shift+F12 and New reference type… in the type picker, TSV paste preview, Export CSV of several
  types, Set storage dialog.
- **L5 Localization** (same document, step 8 and §3.8): sub-element translations in the section, Markdown descriptions,
  "Missing in <locale>", XLIFF/CSV in the editor; first-open cold load with four locales; the batch `translate` operation
  (seed imports write rows and translations in one change; today translations follow the rows).
- **L6 SPEC** (spec-errata.md): E1–E5 and E17–E25 applied in the owner's SPEC document, then re-exported; E19 (`each
  database`) waits for generation-ui §10 Q3.
