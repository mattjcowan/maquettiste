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
  mapping: "Customised mapping",
  diagram: "Diagram",
  "tag-vocabulary": "Tags",
  "category-tree": "Categories",
  stereotype: "Stereotype",
  "reference-type": "Reference type",
  seed: "Seed data",
};

/** A domain inside another domain. */
export const SUB_DOMAIN_LABEL = "Sub-domain";

/** The rail (explorer-redesign.md 1.0, the owner's order): each explorer's label and tooltip. */
export const RAIL_LABELS = {
  "domain-model": { label: "Domain model", tooltip: "Domain model: domains, entities and relationships" },
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
export const LOGO_TOOLTIP = "Maquettiste";
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
