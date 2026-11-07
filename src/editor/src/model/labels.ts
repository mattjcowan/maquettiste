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
  folder(
    "operation",
    "Operations",
    "operation",
    "operations",
    "operation",
    80,
    "domain",
    "Operations: commands over the model, each with its input, outcomes and errors",
  ),
  folder("business-event", "Business events", "business event", "business events", "event", 90, "domain"),
  folder("query", "Queries", "query", "queries", "query", 100, "domain"),
  folder("projection", "Projections", "projection", "projections", "projection", 110, "domain"),
  folder("actor", "Actors", "actor", "actors", "actor", 200, "people", "People and systems that act on the model"),
  folder(
    "right",
    "Rights",
    "right",
    "rights",
    "right",
    205,
    "processes",
    "Named rights: a rule that many lifecycles use, allowing or denying some actors some verbs",
  ),
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
  folder("verb-vocabulary", "Verbs", "verb vocabulary", "verb vocabularies", "verb", 930, "settings"),
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
  storage: "Storage",
  columns: "Columns",
  primaryKey: "Primary key",
  foreignKeys: "Foreign keys",
  uniques: "Unique constraints",
  indexes: "Indexes",
  checks: "Checks",
  referencedBy: "Referenced by",
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
  storage: GROUP_LABELS.storage,
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
  "verb-vocabulary": "Verbs",
  "category-tree": "Categories",
  stereotype: "Stereotype",
  "reference-type": "Reference type",
  seed: "Seed data",
  process: "Process",
  actor: "Actor",
  right: "Right",
  scenario: "Scenario",
  migration: "Migration fact",
  operation: "Operation",
  custom: "Custom element",
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
/** Tooltips of the Processes explorer's folders. */
export const PROCESS_TIPS = {
  lifecycles: "Lifecycles: the states an entity goes through",
  orchestrations: "Orchestrations: flows of work across people, roles and systems",
  libraries: "Libraries: guards, actions and gates other processes share",
  people: "People: actors who are individual persons",
  roles: "Roles: jobs that anyone who holds them can fill",
  groups: "Groups: sets of actors, such as a night shift",
  organizations: "Organizations: units with a place in a hierarchy, such as a supplier or a plant",
  automatic: "Automatic: the system itself, which makes the moves nobody does",
  systems: "Systems: outside systems that act in processes",
  fromObject: "From the object: actors worked out for each object, such as its owner or the reviewers on its project",
  guards: "Conditions transitions check, and effects they run",
  gates: "Signatures a transition waits for",
  entityLifecycles: "Lifecycles that govern this entity, including its base entity's",
} as const;

export const PROCESS_LABELS = {
  states: "States",
  events: "Events",
  guardsAndActions: "Guards and actions",
  gates: "Gates",
  scenarios: "Scenarios",
  actors: "Actors",
  rights: "Rights",
  lifecycle: "lifecycle",
  orchestration: "orchestration",
  library: "library",
  /** The folders that group a domain's processes by use, in the order they show. */
  lifecycles: "Lifecycles",
  orchestrations: "Orchestrations",
  libraries: "Libraries",
  /** The folders that group actors by type; "Systems" is the folder's label only (the type's label is "external system"). */
  people: "People",
  roles: "Roles",
  groups: "Groups",
  organizations: "Organizations",
  automatic: "Automatic",
  fromObject: "From the object",
  systems: "Systems",
  guard: "guard",
  action: "action",
  /** An actor row's reach, from the processes that name it ("acts in 3 processes"). */
  actsIn: (n: number) => `acts in ${n.toLocaleString("en-US")} ${n === 1 ? "process" : "processes"}`,
  /** A lifecycle in an entity's Lifecycles folder that governs it through a base entity. */
  throughBase: (base: string) => `through ${base}`,
  /** An entity's lifecycle that covers it through a stereotype (stereotype-sets-design.md section 8). */
  throughStereotype: (stereotype: string) => `through ${stereotype}`,
  /** Added after the stereotype when a derived entity is covered through its base ("through Approvable, via Order"). */
  viaBase: (base: string) => `, via ${base}`,
  /** A lifecycle row's subject when it governs every entity that carries a stereotype. */
  everyStereotype: (stereotype: string) => `every ${stereotype}`,
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
  passedWarnings: (n: number) => `passed, ${n} warning${n === 1 ? "" : "s"}`,
  passedCount: (n: number) => `✓ ${n.toLocaleString("en-US")} passed`,
  failedCount: (n: number) => `✗ ${n.toLocaleString("en-US")} failed`,
  /** A library part listed under a process that uses it. */
  fromLibrary: (library: string) => `from ${library}`,
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
  objects: "Objects",
  cast: "Cast",
  objectSubject: "subject",
  objectStates: "states",
  objectsHint: "The scenario's objects; edit them in the JSON view of the scenario.",
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
  replayShown: "A replay is shown. Close it to raise inputs.",
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
    right: "a right denies it",
    default: "nothing allows it, and the lifecycle denies by default",
    frozen: "it is frozen, and freezing refuses changes until a thaw",
    check: "a check of a trigger point refuses it",
  } as Record<string, string>,
  verbInput: (verb: string) => `do ${verb}`,
  didVerb: (verb: string, actor: string) => `Did ${verb}${actor ? ` as ${actor}` : ""}`,
  rightLine: (right: string, access: string, lifecycle: string) => `Right ${right} (${access}, ${lifecycle})`,
  guardLine: (guard: string, transition: string, result: string, source: string) => `${guard} on ${transition}: ${result} (${source})`,
  guardSources: { expression: "expression", assumed: "assumed", missing: "missing: add an assumption" } as Record<string, string>,
  unknownResult: "unknown",
  actionLine: (action: string, source: string, changes: string) => `${action} (${source})${changes ? `: ${changes}` : ""}`,
  actionSources: { expression: "expression", sets: "sets", stub: "stub, changes nothing" } as Record<string, string>,
  setsLine: (sets: string) => `Sets: ${sets}`,
  valuesLine: (values: string) => `Values: ${values}`,
  noteLine: (note: string) => `Note: ${note}`,
  unknownAttributesRefused: (attributes: string) =>
    `No attribute was picked, and unknown attributes are refused because the right names attribute ${attributes}. Pick an attribute under Do.`,
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
  /** The actor of a move the input names no actor for: the system actor (Automatic) of the initial entry, a time step or an invoke completion. */
  byActor: (actor: string) => `by ${actor}`,
  bySigner: (signer: string) => `signed by ${signer}`,
  // Participants (actors-design.md decision 15)
  participant: "Participant",
  noParticipant: "(no participant)",
  fromParticipant: "(from the participant)",
  actsAs: (actor: string) => `acts as ${actor}`,
  asParticipant: (participant: string) => `by ${participant}`,
  actingAs: (actor: string) => `acting as ${actor}`,
  /** A step by a participant: the actor the engine worked out for it, or that it satisfied none of the list. */
  actsAsLine: (participant: string, actor: string | null) =>
    actor ? `${participant} acted as ${actor}` : `${participant} satisfies none of the actors the step needs`,
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
  replayWarning: (message: string) => `Warning: ${message}`,
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
export const ACTOR_TYPE_LABELS = {
  person: "person",
  role: "role",
  group: "group",
  organization: "organization",
  "external-system": "external system",
  system: "Automatic",
} as const;
export const ACTOR_TYPES = ["person", "role", "group", "organization", "external-system", "system"] as const;
export type ActorType = (typeof ACTOR_TYPES)[number];
/** The types that may have members and be named in another actor's memberOf. */
export const MEMBER_HOLDER_TYPES: readonly ActorType[] = ["role", "group", "organization"];
/** The type picker's option text in the actor editor; the system actor is spelled out. */
export const ACTOR_TYPE_OPTION_LABELS: Record<ActorType, string> = {
  person: "Person",
  role: "Role",
  group: "Group",
  organization: "Organization",
  "external-system": "External system",
  system: "Automatic (the system itself)",
};

/** The actor editor's membership text. */
export const ACTOR_MEMBER_LABELS = {
  memberOf: "Member of",
  noHolders: "No role, group or organization to join yet.",
  members: "Members",
  add: "Add members…",
  noMembers: "No members yet.",
  noCandidates: "There is no actor left to add.",
  failed: "The members could not be added.",
  count: (direct: number, all: number) => (all > direct ? `${direct} direct, ${all} in all` : `${direct}`),
  addTitle: (name: string) => `Add members to ${name}`,
  addApply: (n: number) => (n ? `Add ${n}` : "Add"),
  addUndo: (name: string) => `Add members to ${name}`,
  missingActor: "(missing actor)",
  removeMember: (name: string) => `Remove ${name} from the members`,
  removeUndo: (name: string) => `Remove ${name} from the members`,
  search: "Search actors",
} as const;

/** The actor editor's "Taken from the object" section (actors-design.md section 9). */
export const ACTOR_RELATIVE_LABELS = {
  title: "Taken from the object",
  form: "Worked out as",
  forms: {
    none: "Not taken from the object",
    owner: "The object's owner",
    path: "A field of the object",
    "members-of": "Members of a role within a context",
  },
  fieldsOf: "Fields of",
  noSubjects: "No process that uses this actor has a subject yet. Choose the kind of object whose fields you want to pick from.",
  pick: "(choose)",
  pickEntity: "(choose an entity)",
  then: "(then…)",
  role: "Role",
  noRoles: "There is no plain role, group or organization yet.",
  context: "Within",
  itself: "(the object itself)",
  means: (text: string) => `Means ${text}.`,
  ownerNote: "Held where each lifecycle subject's Owner names it; without one, the team's system holds it.",
} as const;

/** A library process, which has no chart to open. */
export const LIBRARY_LABELS = {
  noChartTitle: "A library has no chart",
  noChartBody: "It shares guards, actions and gates with other processes. Edit it in the inspector's JSON tab.",
} as const;

/** The process editor's tabs (6.2). */
export const PROCESS_TAB_LABELS = {
  chart: "Chart",
  states: "States",
  transitions: "Transitions",
  events: "Events",
  gates: "Gates",
  rights: "Rights",
  triggerPoints: "Trigger points",
  context: "Context",
  scenarios: "Scenarios",
} as const;

/** The Trigger points tab (trigger-points-design.md section 11). */
export const TRIGGER_LABELS = {
  columns: {
    name: "Name",
    does: "Does",
    type: "Type",
    timing: "When it runs",
    severity: "Severity",
    where: "Where",
    when: "Only if",
    what: "What",
    outside: "Outside systems",
    why: "Why",
  },
  tips: {
    name: "Unique among this lifecycle's trigger points and its guards; a refusal and a scenario step's assume name it",
    does: "The verbs it runs on. It runs wherever one of them happens to this lifecycle's subjects",
    type: "A check judges the change and refuses or warns with a message. An action does something at a moment of the verb. A replace takes the place of the verb's built-in effect (delete, disconnect, copy and revise only)",
    timing:
      "Actions only. Before checks prepares the values and can't refuse. With the change runs in the same unit of work, so a failure undoes the verb. After commit runs once the change is saved, and may run late or twice",
    severity: "Checks only. An error refuses the step; a warning lets it through and lists the check",
    where: "Narrow where it runs: states, the states a move enters, moves, types, attributes or links. Empty means everywhere its verbs happen",
    when: "It runs only when this is true; empty means always",
    what: "A check's condition (only true passes), or what an action or a replace sets. Empty means the team's code does it",
    outside: "The team's code reaches outside systems. Only a point written by the team may say so",
    why: "For a check, the message shown when it refuses or warns",
  },
  order: "Trigger points run in this order at each moment. Drag the handle or press Alt+Up or Alt+Down to move one.",
  empty: "No trigger points yet. Nothing runs when a verb happens beyond the built-in steps.",
  add: "Add trigger point",
  whereTitle: (name: string) => `Where ${name} runs`,
  whereHint: "Leave a list empty for everywhere. Each list narrows where the trigger point runs.",
  whereNone: "No place to narrow on yet: pick a verb under Does first.",
  everywhere: "Everywhere",
  whenTitle: (name: string) => `Only if, for ${name}`,
  whenHint: "It runs only when this is true. It may read the object under subject, the object as it was under old, now() and current_actor().",
  conditionTitle: (name: string) => `Condition of ${name}`,
  conditionHint: "Only true passes. Leave it empty when the team writes the check.",
  setsTitle: (name: string) => `What ${name} sets`,
  noReplace: "A replace is only for delete, disconnect, copy and revise.",
  lostWithState: (state: string, points: string[]) =>
    `Deleted ${state}. ${points.length === 1 ? "The trigger point" : "The trigger points"} ${points.join(", ")} named only ${points.length === 1 ? "it" : "such states"}, so ${points.length === 1 ? "it goes" : "they go"} too.`,
  messageHint: "Say why: it is the message shown when a check refuses or warns.",
  noMessage: "The message is the name until you say why.",
  /** The toggle between the grid and What happens when. */
  viewsLabel: "Trigger points view",
  viewPoints: "Trigger points",
  viewHappens: "What happens when",
  happens: {
    verb: "When someone does",
    verbTip: "The verb to ask about. The answer lists what runs, in order, when it happens to this lifecycle's subjects",
    state: "In state",
    stateAny: "(any state)",
    entity: "For type",
    entityAny: "(any type)",
    loading: "Reading what runs…",
    failed: "Could not read what runs when this verb happens.",
    saved: "Shows the saved lifecycle.",
    empty: "Pick a verb to see what happens when it is done.",
    builtIn: "Built in",
    otherLifecycle: (name: string) => `from ${name}`,
    conditional: "only if its condition holds",
    outside: "reaches outside systems",
    open: (name: string) => `Open ${name}`,
    list: (verb: string) => `What happens when someone does ${verb}`,
  },
  /** The scenario editor's cells (section 11). */
  checkWarnings: "Check warnings",
  checkWarningsTip: "The checks the step expects to warn without refusing; filled in by Update expectations",
  expectedPoints: "Trigger points",
  expectedPointsTip: "The trigger points the step expects to run, in order; filled in when Also record trigger points is ticked",
  assumeTip: "A guard with no expression: true or false. A check the team writes: pass, warning or error. For example: Approved=true, CreditLimit=error",
  alsoRecord: "Also record trigger points",
  alsoRecordTip:
    "Update expectations also writes the trigger points that ran at each step. A trigger point added later then fails the scenario until it is updated again",
  /** The simulation panel's lines. */
  panel: {
    ran: (name: string, verb: string) => `${name} (${verb})`,
    outcomes: { passed: "passed", warned: "warned", refused: "refused", ran: "ran", replaced: "replaced", queued: "queued", skipped: "skipped" } as Record<
      string,
      string
    >,
    assumed: "assumed",
    outsideFlag: "outside",
    outsideTip: "The team's code reaches outside systems; the run does not call it",
    assumedTip: "The team writes this check; its outcome comes from the step's assumptions, or passes by default",
    warningLine: (names: string) => `Check warnings: ${names}`,
    leftOut: (name: string) => `A move was left out by the check ${name}; the step that led to it stays accepted`,
    message: (text: string) => `Message: ${text}`,
  },
} as const;

/** The Rights tab and the named right's editor (rights-design.md section 10). */
export const RIGHT_LABELS = {
  defaultLabel: "When nothing is said",
  defaultTip:
    "What this lifecycle answers when none of its rights says. Allowed is the usual choice; with Denied, only a right that allows lets anything happen.",
  allowed: "Allowed",
  denied: "Denied",
  allow: "Allow",
  deny: "Deny",
  columns: {
    name: "Name",
    access: "Allows or denies",
    who: "Who",
    except: "Except",
    does: "Does",
    states: "In states",
    links: "Links",
    attributes: "Attributes",
    whileFrozen: "While frozen",
    when: "When",
    why: "Why",
  },
  tips: {
    access: "A deny always wins over an allow",
    who: "Who it applies to, with their members; empty means everyone",
    except: "Who it does not apply to. It never grants: other rights and the default still answer for them",
    does: "The verbs it speaks of",
    states: "The states where it holds; empty means every state",
    links: "Only the links through these relation ends",
    attributes: "Only reading or changing these attributes; empty means every attribute. Each attribute is checked on its own",
    whileFrozen: "The right holds only while the object (or, with links, the link) is frozen",
    when: "It holds only when this is true, for example subject.classification = 'secret'",
    why: "Why: shown with every refusal",
  },
  empty: "No rights yet. With nothing said, the lifecycle answers as set above for every verb.",
  add: "Add right",
  addDialog: "Add right…",
  addDescription: "Say what it does. Everyone and every state are the defaults; the rest can be set in the grid.",
  doAttribute: "Attribute for read and modify",
  doAttributeTip: "Read and Modify name this attribute. With none picked they name no attribute, and a right that names attributes refuses",
  doAttributeNone: "No attribute",
  everyAttribute: "every attribute",
  showColumn: (name: string) => `+ ${name}`,
  addNeedsVerb: "Pick at least one verb; a right without a verb says nothing.",
  unknownActors: (names: string[]) =>
    `${names.map((n) => `"${n}"`).join(", ")} ${names.length === 1 ? "is not an actor" : "are not actors"} of this project, so nothing changed. Leave the cell empty to mean Everyone.`,
  useNamed: "Use a named right…",
  noNamed: "No named rights yet",
  open: "Open the named right",
  noUse: "No lifecycle uses this right yet, so its When text is kept as written. Use it in a lifecycle to check it.",
  previewIn: "Check the condition in this lifecycle",
  previewNone: "No lifecycle uses it",
  usedBy: "Used by",
  notUsed: "No lifecycle uses this right yet.",
  newRight: "New named right…",
  conditionOf: (name: string) => `When of ${name}`,
  /** The two views of the Rights tab, and "Who can do what" (rights-design.md section 10). */
  viewsLabel: "Rights view",
  viewRights: "Rights",
  viewWho: "Who can do what",
  whoGrid: (state: string) => `Who can do what in ${state}`,
  whoState: "State",
  whoEntity: "Entity",
  whoAllEntities: "(all)",
  whoLoading: "Working out who can do what…",
  whoFailed: "Who can do what could not be worked out.",
  whoEmpty: "Nothing names an actor or a verb yet, so there is nothing to show.",
  whoSaved: "This view reads the saved lifecycle; it updates when your changes are saved.",
  whoRestated: (actor: string, verb: string, state: string, answer: string) => `${actor} · ${verb} · in ${state}: ${answer}`,
  whoExcepted: (right: string, actor: string) => `The right ${right} excepts ${actor}, so it does not apply to them.`,
  whoByDefault: "Nothing says so; the lifecycle's default answers.",
  whoDecidedBy: "Decided by",
  whoDependsOn: "May also be refused by",
  whoNothing: "Nothing decides it.",
  frozenColumn: "Frozen",
  frozenColumnTip:
    "The object starts frozen: it can't be changed or deleted, and its links can't be connected or disconnected, until a thaw. Only the subject's is read",
  frozenLinksColumn: "Frozen links",
  frozenLinksTip: "The links that start frozen: they can't be disconnected or have their attributes modified until a thaw. Only the subject's links are read",
  stepAttributes: "Attributes",
  stepAttributesTip: "For read: the attributes it reads, each checked against the rights that name attributes; empty means the object as a whole",
  frozenBadge: "Frozen",
  frozenBadgeTip: "The subject is frozen: modify, delete, connect and disconnect are refused until a thaw",
  whoFrozenSwitch: "Frozen",
  whoFrozenTip: "Show the answers for a frozen object: changing it, deleting it and connecting or disconnecting its links are refused",
  whoFrozenReason: "Freezing refuses it before any right.",
  kinds: { right: "right", event: "event", task: "task", gate: "gate", lifecycle: "lifecycle" },
  /** Scenario steps and the replay (rights-design.md decisions 25 and 26). */
  doVerb: "verb",
  verb: "Verb",
  refusal: "Refusal",
  refusedBy: "Refused by",
  linkEnd: "Link end",
  linkObject: "Linked object",
  stepValues: "Set values",
  stepValuesHint: "name=value pairs for modify or create, for example amount=5",
  expectedRefusalHint: "Why the step is refused; filled in by Update from replay",
  expectedRefusedByHint: "The element that refused the step; filled in by Update from replay",
  refusalWords: {
    "no-transition": "No move",
    guard: "Guard",
    actor: "Actor not listed",
    "gate-signer": "Signer not listed",
    "gate-repeat": "Repeat signer",
    "gate-reason": "Reason missing",
    right: "Right",
    default: "Default",
    frozen: "Frozen",
    check: "Check",
  } as Record<string, string>,
  /** The simulation panel's Do… menu and its refusal lines. */
  doMenu: "Do…",
  doMenuTip: "Do a verb to the object, as the chosen participant",
  doAs: "Do it as",
  doAsNobody: "(no one)",
  refusedByLine: (reason: string, element: string) => `${reason}, refused by ${element}`,
  refusedByWord: "refused by",
  refusedPlain: "refused",
  lostWithState: (state: string, rights: string[]) =>
    `Deleted ${state}. ${rights.length === 1 ? "The right" : "The rights"} ${rights.join(", ")} named only ${rights.length === 1 ? "it" : "such states"}, so ${rights.length === 1 ? "it goes" : "they go"} too.`,
  deleted: "(deleted)",
  outcomeWord: "Outcome",
  failedRefusal: (step: string, expected: string, actual: string) => `${step} was expected to be ${expected}, but in the replay ${actual}.`,
  failedAccept: (step: string) => `${step} was expected to be accepted, but in the replay it is refused.`,
  updateExpectations: "Update expectations",
  updateExpectationsTip: "Rewrite the expectations of every step from what the replay does (Shift+R in the Scenarios tab)",
  refusalNone: "(none)",
  whyLine: (why: string) => `Why: ${why}`,
  rightsThatAnswered: "Rights that answered",
} as const;

/** The scenario editor's participants (actors-design.md decision 14). The word is one constant here: the owner has yet to confirm it. */
export const PARTICIPANT_LABELS = {
  singular: "participant",
  plural: "participants",
  tab: "Participants",
  objectsTab: "Objects",
  grid: (scenario: string) => `Participants of ${scenario}`,
  name: "Name",
  displayName: "Display name",
  identity: "Signs as",
  identityHint: "The name this participant signs with, when a step names no signer.",
  actor: "Is",
  actorHint: "The person, external system or automatic actor this participant is.",
  holds: "Holds roles",
  editHolds: (name: string) => `Edit the roles ${name} holds`,
  holdsTitle: (name: string) => `Roles held by ${name}`,
  holdsRole: "Role, group or organization",
  holdsPickRole: "(choose a role)",
  holdsContext: "Within object",
  holdsAnywhere: "(anywhere)",
  within: "within",
  addHolding: "Add role",
  removeHolding: "Remove this role",
  noRoles: "The model has no role, group or organization to hold yet.",
  noObjects: "No objects yet. Add one to give the scenario something to run on, a record to point to or a context to hold roles in.",
  addObjectTitle: "Add object",
  objectNameTaken: "The scenario already has an object with this name.",
  object: "Record object",
  step: "Participant",
  owner: "Owner",
  entity: "Entity",
  none: "(none)",
  automatic: "Automatic",
  holdsHint: "The roles, groups and organizations this participant holds, each optionally within one object of the scenario, such as Reviewer within Project1.",
  ownerLocked: "The process names where this object's owner is held, so the owner comes from its values and links.",
  holdsUnknown: (text: string) => `'${text}' is not a role, group or organization, or not an object of the scenario.`,
  /** Show the row's step text in the simulation panel and the trace. */
  actsAs: (actor: string) => `acts as ${actor}`,
  noActsAs: "satisfies none of the actors it needs",
} as const;
