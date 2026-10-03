// Creating elements from the explorer (explorer-redesign.md 1.8): which New actions a row, an explorer or the first-run
// panel offers, the domain a New dialog starts on, and the new document itself. Pure: NewElementDialog.tsx renders the
// dialog and saves the result. Each document carries what its schema requires (a kind, an id, a name, the base of a custom
// type, the dialect of a database, the ends of a relationship) plus a few starting defaults: an entity gets a uuid-v7 `id`
// key attribute and a relationship gets the canvas dialog's default end roles.
import type { ModelJson } from "@/api/types";
import { defaultEndRoles, IDENTIFIER } from "@/model/model";
import type { ExplorerId, Forest } from "./tree";
import { nodeOf } from "./tree";

export type CreateKind =
  | "package"
  | "sub-package"
  | "entity"
  | "relation"
  | "enum"
  | "value-object"
  | "scalar-type"
  | "reference-type"
  | "diagram"
  | "database"
  | "process"
  | "actor"
  | "scenario";

/** The New actions whose dialogs are the process dialogs (explorer/processDialogs.tsx), not NewElementDialog. */
export const PROCESS_CREATE: ReadonlySet<CreateKind> = new Set(["process", "actor", "scenario"]);

/** The menu and button label of each New action. */
export const CREATE_LABELS: Record<CreateKind, string> = {
  package: "New domain",
  "sub-package": "New sub-domain",
  entity: "New entity",
  relation: "New relationship",
  enum: "New enum",
  "value-object": "New value object",
  "scalar-type": "New custom type",
  "reference-type": "New reference type",
  diagram: "New diagram",
  database: "New database",
  process: "New process…",
  actor: "New actor…",
  scenario: "New scenario…",
};

/** The element kind a New action creates. */
export const CREATED_KIND: Record<CreateKind, string> = {
  package: "package",
  "sub-package": "package",
  entity: "entity",
  relation: "relation",
  enum: "enum",
  "value-object": "value-object",
  "scalar-type": "scalar-type",
  "reference-type": "reference-type",
  diagram: "diagram",
  database: "database",
  process: "process",
  actor: "actor",
  scenario: "scenario",
};

/** A domain row's New actions, in the order of section 1.8. */
export const DOMAIN_CREATE: readonly CreateKind[] = ["entity", "relation", "enum", "value-object", "scalar-type", "sub-package", "diagram"];

/** Each explorer's New actions (its header button and its empty state). */
export const EXPLORER_CREATE: Record<ExplorerId, readonly CreateKind[]> = {
  "domain-model": ["package", "entity", "relation", "enum", "value-object", "scalar-type"],
  "reference-data": ["reference-type"],
  databases: ["database"],
  diagrams: ["diagram"],
  processes: ["process", "actor", "scenario"],
};

/** Everything the first-run panel of an empty model offers. */
export const FIRST_RUN_CREATE: readonly CreateKind[] = ["package", "entity", "enum", "value-object", "scalar-type", "reference-type", "diagram", "database"];

/** The New action of a kind folder ("New (that kind)", 1.8), or null for a folder of a kind the editor cannot create. */
export function folderCreate(kind: string | undefined): CreateKind | null {
  switch (kind) {
    case "package":
      return "sub-package";
    case "entity":
    case "relation":
    case "enum":
    case "value-object":
    case "scalar-type":
    case "reference-type":
    case "diagram":
    case "database":
    case "process":
    case "actor":
    case "scenario":
      return kind;
    default:
      return null;
  }
}

/**
 * The domain a New dialog starts on, given the current one. "New domain" (from a header, the palette or the first-run
 * panel) makes a top-level domain whatever is selected; a child domain is "New sub-domain", which keeps the preset.
 */
export function startDomain(kind: CreateKind, current: string | null): string | null {
  return kind === "package" ? null : current;
}

/** Kinds whose picker lists domains (a domain's picker is its parent). */
export function takesDomain(kind: CreateKind): boolean {
  return kind !== "database" && kind !== "reference-type";
}

/** The domain of a row: the domain itself, an element's domain, a folder's or group's domain; null for none. */
/** The process a row belongs to (the process row itself, or a folder or row under it), or null. */
export function processOfKey(forest: Forest, key: string | null | undefined): string | null {
  for (let k = key, guard = 0; k && guard < 64; guard++) {
    const node = nodeOf(forest, k);
    if (!node) return null;
    if (node.id && forest.byId.get(node.id)?.kind === "process") return node.id;
    k = node.parent;
  }
  return null;
}

export function domainOfKey(forest: Forest, key: string | null | undefined): string | null {
  for (let k = key, guard = 0; k && guard < 64; guard++) {
    const node = nodeOf(forest, k);
    if (!node) return null;
    if (node.type === "domain" && node.id && forest.byId.get(node.id)?.kind === "package") return node.id;
    if (node.home && node.id && forest.byId.has(node.id)) return domainOfElement(forest, node.id);
    // The Diagrams explorer's per-domain groups carry the domain's id.
    if (node.type === "group" && node.id && forest.byId.get(node.id)?.kind === "package") return node.id;
    k = node.parent;
  }
  return null;
}

/** An element's domain: a domain is its own, a seed follows its target, others their `package`. */
export function domainOfElement(forest: Forest, id: string): string | null {
  return domainOfRow(forest.byId, id);
}

type DomainRow = { id: string; kind: string; package?: string | null; target?: string | null };

/** domainOfElement over any id → index row map (the canvas has the index lookup, not the forest). */
export function domainOfRow(byId: ReadonlyMap<string, DomainRow>, id: string): string | null {
  const row = byId.get(id);
  if (!row) return null;
  if (row.kind === "package") return row.id;
  const target = row.kind === "seed" && row.target ? byId.get(row.target) : undefined;
  const pkg = (target ?? row).package;
  return pkg && byId.get(pkg)?.kind === "package" ? pkg : null;
}

/** The domain a New dialog starts on: the explorer row, else the selected element, else the open diagram's home. */
export function currentDomain(
  forest: Forest | null | undefined,
  context: { key?: string | null; selection?: readonly string[]; activeDiagram?: string | null },
): string | null {
  if (!forest) return null;
  if (context.key) return domainOfKey(forest, context.key);
  const selected = context.selection?.[0];
  if (selected && forest.byId.has(selected)) return domainOfElement(forest, selected);
  const diagram = context.activeDiagram;
  if (diagram?.startsWith("pkg:")) return forest.byId.has(diagram.slice(4)) ? diagram.slice(4) : null;
  return diagram ? domainOfElement(forest, diagram) : null;
}

export interface NewElementInput {
  name: string;
  /** The domain (for a domain: its parent), or null. */
  domain: string | null;
  /** A custom type's built-in base. */
  base?: string;
  /** A database's dialect. */
  dialect?: string;
  /** A new database's schemas (the first is the default, erratum E26). */
  schemas?: readonly { id: string; name: string }[];
  /** A relationship's ends. */
  source?: string;
  target?: string;
  /** Names of the ends' entities, for the default roles. */
  sourceName?: string;
  targetName?: string;
}

/** Why a name cannot be used, or null. Identifier kinds need letters, digits and underscores, not starting with a digit. */
export function nameProblem(kind: CreateKind, name: string): string | null {
  const trimmed = name.trim();
  if (!trimmed) return "Enter a name.";
  if (kind === "diagram" || kind === "database" || kind === "relation") return null;
  return IDENTIFIER.test(trimmed) ? null : "Use letters, digits and underscores, not starting with a digit.";
}

/** The new element's document (a reference type is created with its seed by the Reference data screen's action). */
export function buildElement(kind: Exclude<CreateKind, "reference-type">, input: NewElementInput, newId: () => string): ModelJson {
  const id = newId();
  const name = input.name.trim();
  const home = input.domain ? { package: input.domain } : {};
  let json: Record<string, unknown>;
  switch (kind) {
    case "package":
    case "sub-package":
      json = { kind: "package", id, name, ...(input.domain ? { parent: input.domain } : {}) };
      break;
    case "entity": {
      const keyId = newId();
      json = {
        kind: "entity",
        id,
        name,
        ...home,
        key: { attributes: [keyId], strategy: "uuid-v7" },
        attributes: [{ id: keyId, name: "id", type: "uuid", required: true }],
      };
      break;
    }
    case "relation": {
      const { sourceRole, targetRole } = defaultEndRoles(input.sourceName ?? "source", input.targetName ?? "target");
      json = {
        kind: "relation",
        id,
        name,
        ...home,
        ends: [
          { id: newId(), entity: input.source, role: sourceRole, navigation: sourceRole },
          { id: newId(), entity: input.target, role: targetRole, navigation: targetRole },
        ],
      };
      break;
    }
    case "scalar-type":
      json = { kind: "scalar-type", id, name, ...home, base: input.base ?? "string" };
      break;
    case "database":
      // A new database starts empty (D46): it lays out nothing by convention, and the member is always written. Entities are
      // stored in it from their own Storage tab.
      json = {
        kind: "database",
        id,
        name,
        dialect: input.dialect ?? "postgresql",
        ...(input.schemas?.length ? { defaultSchema: input.schemas[0].name, schemas: input.schemas.map((x) => ({ id: x.id, name: x.name })) } : {}),
        byConvention: "none",
      };
      break;
    case "enum":
    case "value-object":
    case "diagram":
      json = { kind, id, name, ...home };
      break;
    case "process":
    case "actor":
    case "scenario":
      // One batch each, built by processCreate.ts for the process dialogs.
      throw new Error(`A ${kind} is created by its own dialog.`);
  }
  return json as unknown as ModelJson;
}
