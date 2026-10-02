// Every user-facing word of the editor (explorer-redesign.md section 2), so a later rename is a one-file change.
// Code identifiers, URL segments and file formats keep the spec's words ("package", "/entities"); only the UI
// says "Domain". The kind → explorer folder table (section 1.2) gives each kind's folder label, tooltip, icon,
// order and placement: explorer/tree.ts reads only this table. A kind this table does not know goes in an
// "Other" folder in its domain, or in the Other elements root.
import type { ElementKind } from "@/api/types";
import type { Workspace } from "@/state/store";

/** Where an element of a kind is listed. */
export type Placement =
  /** In its domain's kind folder (or Not in a domain). */
  | "domain"
  /** A domain itself. */
  | "package"
  /** People and access: conceptual, but never in a domain. */
  | "people"
  | "databases"
  | "diagrams"
  | "reference-data"
  /** The Processes explorer only (a scenario, under its process). */
  | "processes"
  /** Not in any tree: the Settings screen's tabs (and, for scoped vocabularies, the domain editor). */
  | "settings";

export interface KindFolder {
  kind: string;
  /** The folder's label: the kind in the plural. */
  label: string;
  /** One member, lower case, for count phrases ("1 entity"). */
  one: string;
  /** Several members, lower case, for count phrases ("41 entities"). */
  many: string;
  tooltip: string;
  /** An icon name; the explorer maps it to a glyph. */
  icon: string;
  /** Folder order inside a domain (lower first). */
  order: number;
  placement: Placement;
}

function folder(kind: string, label: string, one: string, many: string, icon: string, order: number, placement: Placement, tooltip?: string): KindFolder {
  return { kind, label, one, many, icon, order, placement, tooltip: tooltip ?? `${label} of this domain` };
}

/** The table, in folder order. The later SPEC kinds are listed so their folders appear as they land. */
export const KIND_FOLDERS: readonly KindFolder[] = [
  folder(
    "package",
    "Domains",
    "sub-domain",
    "sub-domains",
    "domain",
    0,
    "package",
    "Domain (package in model files): groups elements; also sets the output folder and namespace",
  ),
  folder("entity", "Entities", "entity", "entities", "entity", 10, "domain"),
  folder(
    "relation",
    "Relationships",
    "relationship",
    "relationships",
    "relationship",
    20,
    "domain",
    "Relationships (relations) between this domain's entities",
  ),
  folder("enum", "Enums", "enum", "enums", "enum", 30, "domain", "Enums: a closed set of named values"),
  folder(
    "value-object",
    "Value objects",
    "value object",
    "value objects",
    "value-object",
    40,
    "domain",
    "Value objects: a reusable group of fields without identity, such as Address or Money",
  ),
  folder(
    "scalar-type",
    "Custom types",
    "custom type",
    "custom types",
    "custom-type",
    50,
    "domain",
    "Custom types: a named restriction of a built-in type, such as Email",
  ),
  folder("seed", "Seed data", "seed", "seeds", "seed", 60, "domain", "Seed data of this domain's entities and relationships"),
  folder("process", "Processes", "process", "processes", "process", 70, "domain"),
  folder("scenario", "Scenarios", "scenario", "scenarios", "scenario", 75, "processes", "Scenarios of this process"),
  folder("operation", "Operations", "operation", "operations", "operation", 80, "domain"),
  folder("business-event", "Business events", "business event", "business events", "event", 90, "domain"),
  folder("query", "Queries", "query", "queries", "query", 100, "domain"),
  folder("projection", "Projections", "projection", "projections", "projection", 110, "domain"),
  folder("actor", "Actors", "actor", "actors", "actor", 200, "people", "People and systems that act on the model"),
  folder("permission", "Permissions", "permission", "permissions", "permission", 210, "people", "Permissions granted to actors"),
  folder("database", "Databases", "database", "databases", "database", 300, "databases"),
  folder("table", "Tables", "table", "tables", "table", 310, "databases", "Tables of this schema"),
  folder("view", "Views", "view", "views", "view", 320, "databases", "Views of this schema"),
  folder("sequence", "Sequences", "sequence", "sequences", "sequence", 330, "databases", "Sequences of this schema"),
  folder("routine", "Routines", "routine", "routines", "routine", 332, "databases", "Routines of this schema: functions and procedures"),
  folder(
    "database-type",
    "Types",
    "database type",
    "database types",
    "database-type",
    334,
    "databases",
    "Database types of this schema: domains, composites, enums and ranges",
  ),
  folder(
    "sql-object",
    "Objects",
    "SQL object",
    "SQL objects",
    "sql-object",
    336,
    "databases",
    "SQL objects of this schema: triggers, grants, extensions and the like",
  ),
  folder("query", "Queries", "query", "queries", "query", 338, "databases", "Queries over this database's tables and views"),
  folder(
    "mapping",
    "Customised mappings",
    "customised mapping",
    "customised mappings",
    "mapping",
    340,
    "databases",
    "Tables with a mapping override; the others are mapped automatically",
  ),
  folder("diagram", "Diagrams", "diagram", "diagrams", "diagram", 400, "diagrams"),
  folder(
    "reference-type",
    "Reference types",
    "type",
    "types",
    "reference-type",
    500,
    "reference-data",
    "Reference types: sets of rows managed as data, such as units of measure or countries",
  ),
  folder("tag-vocabulary", "Tags", "tag vocabulary", "tag vocabularies", "tag", 900, "settings"),
  folder("category-tree", "Categories", "category tree", "category trees", "category", 910, "settings"),
  folder("stereotype", "Stereotypes", "stereotype", "stereotypes", "stereotype", 920, "settings"),
];

const BY_KIND = new Map(KIND_FOLDERS.map((f) => [f.kind, f]));

/** The folder of a kind, or undefined for a kind this editor version does not know. */
export function kindFolder(kind: string): KindFolder | undefined {
  return BY_KIND.get(kind);
}

/** The placement of a kind; an unknown kind is "other". */
export function placementOf(kind: string): Placement | "other" {
  return BY_KIND.get(kind)?.placement ?? "other";
}

/** The Other folder, for kinds the table does not know. */
export const OTHER_FOLDER: KindFolder = folder(
  "",
  "Other",
  "element",
  "elements",
  "other",
  1000,
  "domain",
  "Elements of kinds this editor version does not know",
);

/** "1 entity", "41 entities", "1,000 tables". */
export function countOf(n: number, kind: string): string {
  const f = BY_KIND.get(kind) ?? OTHER_FOLDER;
  return `${n.toLocaleString("en-US")} ${n === 1 ? f.one : f.many}`;
}

/** The explorers' own names (the rail's words). */
export const EXPLORER_LABELS = {
  "domain-model": "Domain model",
  processes: "Processes",
  "reference-data": "Reference data",
  databases: "Databases",
  diagrams: "Diagrams",
} as const;

/** Labels of the tree's fixed groups. */
export const GROUP_LABELS = {
  notInDomain: "Not in a domain",
  noCategory: "No category",
  people: "People and access",
  otherElements: "Other elements",
  notInDatabase: "Not in a database",
  defaultSchema: "Default schema",
  notLinked: "Not linked to an entity",
  attributes: "Attributes",
  members: "Members",
  ends: "Ends",
  mappings: "Mappings",
  columns: "Columns",
  primaryKey: "Primary key",
  foreignKeys: "Foreign keys",
  uniques: "Unique constraints",
  indexes: "Indexes",
} as const;

/** The element editor's frame. */
export const EDITOR_LABELS = {
  hideDetails: "Hide the details (display names, description and top controls)",
  showDetails: "Show the details (display names, description and top controls)",
} as const;

/** The tabs of the element editors (explorer-redesign.md 3.6): one word per concept, the tree's own where it has one. */
export const EDITOR_TAB_LABELS = {
  attributes: GROUP_LABELS.attributes,
  members: GROUP_LABELS.members,
  definition: "Definition",
  relationships: "Relationships",
  indexes: GROUP_LABELS.indexes,
  mappings: GROUP_LABELS.mappings,
  inheritance: "Inheritance",
  seedData: "Seed data",
  codeGeneration: "Code generation",
  references: "References",
} as const;

/** What lists the elements that refer to one (explorer-redesign.md 3.3): the inspector tab, the explorer's menu item
 * and the References tab's tooltip. One short word so the inspector's tabs never wrap. */
export const USED_LABEL = "Used";
export const USED_TOOLTIP = "Used: what refers to this element (Shift+F12)";

/** One element of a kind, capitalised (inspector header, empty states, fallback names). */
export const KIND_LABELS: Record<ElementKind, string> = {
  package: "Domain",
  entity: "Entity",
  "value-object": "Value object",
  "scalar-type": "Custom type",
  enum: "Enum",
  relation: "Relationship",
  database: "Database",
  table: "Table",
  view: "View",
  sequence: "Sequence",
  routine: "Routine",
  "database-type": "Database type",
  "sql-object": "SQL object",
  query: "Query",
  mapping: "Customised mapping",
  diagram: "Diagram",
  "tag-vocabulary": "Tags",
  "category-tree": "Categories",
  stereotype: "Stereotype",
  "reference-type": "Reference type",
  seed: "Seed data",
  process: "Process",
  actor: "Actor",
  scenario: "Scenario",
};

/** A domain inside another domain. */
export const SUB_DOMAIN_LABEL = "Sub-domain";

/** The rail (explorer-redesign.md 1.0, the owner's order): each explorer's label and tooltip. */
export const RAIL_LABELS = {
  "domain-model": { label: "Domain model", tooltip: "Domain model: domains, entities and relationships" },
  processes: { label: "Processes", tooltip: "Processes: lifecycles, orchestrations, actors and scenarios" },
  "reference-data": { label: "Reference data", tooltip: "Reference data: reference types and their rows" },
  databases: { label: "Databases", tooltip: "Databases, schemas and tables" },
  diagrams: { label: "Diagrams", tooltip: "Diagrams" },
  generate: { label: "Generate", tooltip: "Generate: packs and their output" },
  settings: { label: "Settings", tooltip: "Settings" },
  account: { label: "Account", tooltip: "Account" },
} as const;

/** The rail's own accessible name: its icons name explorers. */
export const RAIL_NAME = "Explorers";

/** The screens (centre documents) by workspace id; the `Workspace` type and the URL segments stay in code. */
export const SCREEN_LABELS: Record<Workspace, string> = {
  entities: "Domain model",
  "reference-data": "Reference data",
  database: "Databases",
  mappings: "Mappings",
  generate: "Generate",
  settings: "Settings",
};

/** The command palette's group of screens. */
export const GO_TO_SCREEN = "Go to screen";

/** The search box over an explorer. */
export const SEARCH_PLACEHOLDER = "Search the model";

/** The whole-domain diagram view ("All of Billing") and its picker group. */
export const allOf = (domain: string) => `All of ${domain}`;
export const DOMAIN_VIEWS_LABEL = "Whole domains";

/** The top bar: the logo's tooltip and the project name's. */
export const LOGO_TOOLTIP = "Home";
export const PROJECT_TOOLTIP = "Project: the whole model and its settings (maquettiste.json)";

/** The glossary (SPEC section 14), shown as tooltips. */
export const GLOSSARY = {
  domain:
    "Domain: a business area such as Billing or Sales; stored as a package in the model files. A domain inside another is a sub-domain. (Not a reusable attribute type, which is a Custom type here.)",
  domainModel: "Domain model: the tree root and the screen for drawing and editing a domain's entities and relationships.",
  referenceType:
    "Reference type: a set of rows managed as data (units of measure, countries), each with a code and a label and any fields the project adds. Managed in the Reference data screen.",
  enum: "Enum: a closed set of named values that belongs to the code.",
  seedData: "Seed data: rows an element starts with (an entity's initial rows, a reference type's rows).",
  businessEvent: "Business event: a thing that happened, raised by an operation or a process.",
  explorer: "Explorer: the sidebar tree a rail icon selects. Screen: a document in the centre.",
  mapped: "Mapped automatically: a table that follows the naming conventions needs no mapping file; a customised one has a mapping override.",
} as const;

/** The Processes explorer and the process dialogs (phase-3-design.md 6.1). */
export const PROCESS_LABELS = {
  states: "States",
  events: "Events",
  scenarios: "Scenarios",
  actors: "Actors",
  lifecycle: "lifecycle",
  orchestration: "orchestration",
  /** What each Use means, shown under the choice in New process… and as the Use field's tooltip in the editor. */
  useMeaning: {
    lifecycle:
      "A lifecycle describes the states one entity goes through (a sales order from Draft to Completed). It has a subject entity and can bind its root states to an enum attribute of that entity, so the two never drift.",
    orchestration:
      "An orchestration coordinates work across people, roles, systems and other processes (a purchase approval with parallel checks, tasks and signatures). A subject entity is optional.",
  } as Record<"lifecycle" | "orchestration", string>,
  notRun: "not run",
  passed: "passed",
  failedAt: (step: number) => `failed at step ${step}`,
  passedCount: (n: number) => `✓ ${n.toLocaleString("en-US")} passed`,
  initialState: "Initial",
  newProcess: "New process…",
  newActor: "New actor…",
  newScenario: "New scenario…",
  importXState: "Import XState…",
  exportXState: "Export XState",
  verify: "Verify scenarios",
  simulate: "Simulate",
  newStatus: "New status attribute and enum",
  recordFromSimulation: "Record from simulation",
  recordFromSimulationNote: "opens the chart with the simulation panel recording",
  empty: "Empty",
} as const;

/** The statechart canvas (phase-3-design.md 6.3). */
export const CHART_LABELS = {
  toolbar: "Chart",
  canvas: (process: string) => `Statechart of ${process}`,
  layout: "Layout",
  layingOut: "Laying out…",
  layoutChart: "Lay out the whole chart",
  layoutState: (state: string) => `Lay out the states of ${state}`,
  loading: "Laying out the chart…",
  notSaved: "Arrangement not saved yet: moving a state, Layout, pan or zoom saves it in the process diagram.",
  hint: "Drag a state's dot onto another state to draw a transition · Arrows move · Enter enters · Esc leaves · Tab walks transitions · N adds a state · T draws a transition · F2 renames · Delete deletes · F12 opens · Shift+F12 lists uses",
  linking: (source: string) => `New transition from ${source}: pick the target with the arrow keys or the mouse, Enter or a click confirms, Esc cancels.`,
  linkHandle: (source: string) => `Draw a transition from ${source} (drag onto the target state)`,
  renameState: (state: string) => `Rename ${state}`,
  collapse: (state: string) => `Collapse ${state}`,
  expand: (state: string) => `Expand ${state}`,
  boundMember: (member: string) => `Enum member ${member}`,
  hiddenStates: (count: number) => `${count} ${count === 1 ? "state" : "states"} inside`,
  statechartOf: (process: string) => `statechart of ${process}`,
  menu: (name: string) => `Actions of ${name}`,
  addState: "Add state",
  addChild: "Add child state",
  addTransition: "Draw a transition",
  rename: "Rename",
  layoutInside: "Lay out its states",
  collapseItem: "Collapse",
  expandItem: "Expand",
  delete: "Delete",
  goToDefinition: "Go to definition",
  deleteTitle: (names: string) => `Delete ${names}?`,
  deleteIncoming: "These transitions enter it from other states; each loses it as a target, and one left with no target is deleted:",
  confirmDelete: "Delete",
  cancel: "Cancel",
  notDeleted: (reason: string) => `Not deleted: ${reason}`,
  unsavedProcess: "the process has unsaved changes that could not be saved; fix or discard them first.",
  unsavedDiagram: "the chart's arrangement has unsaved changes that could not be saved; resolve them first.",
  diagramNotSaved: (reason: string) => `The chart's diagram was not saved: ${reason}`,
  newDiagramUndo: (name: string) => `New diagram ${name}`,
  batchUnreadable: "The batch did not parse.",
  changeRefused: (outcome: string) => `The change was refused (${outcome}).`,
  /** What a state is, as its accessible name says it. */
  stateTypes: {
    atomic: "state",
    compound: "compound state",
    parallel: "parallel state",
    final: "final state",
    history: "history state",
    choice: "choice",
  } as Record<string, string>,
  stateAria: (type: string, label: string, active: boolean) => `${type} ${label}${active ? ", active" : ""}`,
  transitionAria: (label: string) => `Transition ${label}`,
  /** The live region's words for the selection. */
  selectedState: (type: string, label: string) => `Selected ${type} ${label}`,
  selectedStates: (count: number) => `${count} states selected`,
  selectedTransition: (label: string) => `Selected transition ${label}`,
  /** Edge words: a timer, an invoke's result. */
  afterWord: (duration: string) => `after ${duration}`,
  doneOf: (invoke: string) => `done: ${invoke}`,
  errorOf: (invoke: string) => `error: ${invoke}`,
  gateTitle: (gate: string, required: number, of: number, signers: string) => `${gate}: ${required} of ${of} signers (${signers || "none"})`,
} as const;

/** The simulation panel (phase-3-design.md 6.4). */
export const SIMULATION_LABELS = {
  panel: "Simulation",
  panelOf: (process: string) => `Simulation of ${process}`,
  collapse: "Collapse the simulation panel",
  expand: "Expand the simulation panel",
  collapseSection: (section: string) => `Collapse ${section}`,
  expandSection: (section: string) => `Expand ${section}`,
  running: "Running…",
  inputs: (n: number) => `${n.toLocaleString("en-US")} ${n === 1 ? "input" : "inputs"}`,
  final: "final",
  reset: "Restart from the start",
  recording: (name: string) => `Recording ${name}`,
  stopRecording: "Stop recording",
  // Sections
  start: "Start",
  enabled: "Enabled",
  time: "Time",
  pending: "Pending",
  configuration: "Configuration",
  lastStep: "Last step",
  trace: "Trace",
  replay: "Replay",
  // Start
  clockStart: "Clock start",
  fromScenario: "From scenario…",
  fromScenarioPlaceholder: "From scenario…",
  loadedScenario: (name: string) => `Loaded the start and steps of ${name}.`,
  noContext: "The process has no context attributes.",
  // Enabled
  raise: "Raise",
  raiseRow: (label: string, n: number | null) => (n ? `Raise ${label} (${n})` : `Raise ${label}`),
  actor: "Actor",
  anyActor: "(any actor)",
  noActor: "(no actor)",
  signer: "Signer",
  meaning: "Meaning",
  reason: "Reason",
  reasonRequired: "Reason (required)",
  assume: (guard: string) => `Assume ${guard}`,
  assumeTrue: "true",
  assumeFalse: "false",
  gateProgress: (have: number, need: number) => `${have} of ${need} signed`,
  invokeDone: (invoke: string) => `${invoke} done`,
  invokeError: (invoke: string) => `${invoke} error`,
  advanceTime: "advance time",
  noEnabled: "Nothing is enabled: the process waits for nothing it can take.",
  finalReached: "The process reached a final state.",
  pastSelected: (label: string) => `Showing the state after ${label}. Select the last input to raise more.`,
  enabledHint: "Enter raises the focused row · 1 to 9 raise the first nine rows",
  // Time
  nextTimer: (transition: string, due: string) => `Next: ${transition} at ${due}`,
  noTimers: "No timer is scheduled.",
  clock: (clock: string) => `Clock ${clock}`,
  advanceBy: "Advance by",
  advance: "Advance",
  durationHint: "An ISO 8601 duration, such as PT1H or P5D.",
  // Pending
  noPending: "No invoke is waiting.",
  done: "Done",
  error: "Error",
  doneOf: (invoke: string) => `Complete ${invoke} (done)`,
  errorOf: (invoke: string) => `Fail ${invoke} (error)`,
  pendingIn: (invoke: string, state: string) => `${invoke} in ${state}`,
  // Configuration
  noConfiguration: "No active state yet.",
  // Last step
  noLastStep: "Raise an input to see what it did.",
  initialEntry: "Initial entry",
  refused: (reason: string) => `Refused: ${reason}`,
  refusals: {
    "no-transition": "no transition of the active states takes it",
    guard: "the guards of its transitions are false",
    actor: "the actor may not raise it",
    "gate-signer": "the signer is not one of the gate's signers",
    "gate-repeat": "this signer already signed the gate",
    "gate-reason": "the gate needs a reason with each signature",
  } as Record<string, string>,
  guardLine: (guard: string, transition: string, result: string, source: string) => `${guard} on ${transition}: ${result} (${source})`,
  guardSources: { expression: "expression", assumed: "assumed", missing: "missing: add an assumption" } as Record<string, string>,
  unknownResult: "unknown",
  actionLine: (action: string, source: string, changes: string) => `${action} (${source})${changes ? `: ${changes}` : ""}`,
  actionSources: { expression: "expression", stub: "stub, changes nothing" } as Record<string, string>,
  transitionLine: (transitions: string) => `Took ${transitions}`,
  auditLine: (gate: string, outcome: string, signer: string, actor: string, meaning: string) =>
    `${gate}: ${outcome}${signer ? ` by ${signer}` : ""}${actor ? ` as ${actor}` : ""}${meaning ? ` (${meaning})` : ""}`,
  changedLine: (changes: string) => `Context: ${changes}`,
  incomplete: "The replay stopped here; the inputs after it did not run.",
  // Trace
  traceStart: "Start",
  notRun: "not run",
  deleteInput: (label: string) => `Delete ${label} and the inputs after it`,
  traceHint: "Enter or click shows the state after an input · Delete removes it and the inputs after it",
  noInputs: "No input yet: raise one from Enabled.",
  timeInput: (after: string) => `time +${after}`,
  asActor: (actor: string) => `as ${actor}`,
  bySigner: (signer: string) => `signed by ${signer}`,
  // Record
  record: "Record to scenario…",
  recordTitle: "Record to scenario",
  recordName: "Name",
  recordOutcome: "Outcome",
  outcomeFinal: "final: the process ends",
  outcomeActive: "active: the process is still running",
  recordSave: "Record",
  recordNothing: "Raise at least one input to record.",
  recorded: (name: string) => `Recorded ${name}.`,
  recordUndo: (name: string) => `Record ${name}`,
  // Replay
  replayScenario: "Replay scenario…",
  replayTitle: "Replay scenario",
  replayPick: "Scenario",
  replayRun: "Replay",
  replayOf: (name: string) => `Replay of ${name}`,
  replayClose: "Close the replay",
  replayPassed: "passed",
  replayFailed: "failed",
  replayNotRun: "not run",
  replayAllPassed: (n: number) => `All ${n.toLocaleString("en-US")} steps passed.`,
  replayFailedAt: (step: string, rule: string, message: string) => `${step} failed (${rule}): ${message}`,
  replayStep: (n: number) => `Step ${n}`,
  replayOutcome: "Outcome",
  expected: "Expected",
  actual: "Actual",
  noScenarios: "The process has no scenarios yet.",
  // Errors
  invalidDraft: "The unsaved changes do not validate, so nothing ran:",
  failed: (message: string) => `The simulation failed: ${message}`,
  unsavedDraft: "The process has unsaved changes that could not be saved; fix or discard them first, so the saved process is the one simulated.",
  cancel: "Cancel",
} as const;

/** An actor's type as a row shows it. */
export const ACTOR_TYPE_LABELS = { person: "person", role: "role", "external-system": "external system" } as const;

/** The process editor's tabs (6.2). */
export const PROCESS_TAB_LABELS = {
  chart: "Chart",
  states: "States",
  transitions: "Transitions",
  events: "Events",
  gates: "Gates",
  context: "Context",
  scenarios: "Scenarios",
} as const;
