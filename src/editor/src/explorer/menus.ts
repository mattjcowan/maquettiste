// The explorer's context menus (explorer-redesign.md 1.8), as data: which actions a row, or a multi-selection of
// rows of one kind, offers. RowMenu.tsx renders them; Explorer.tsx runs them. Only actions this editor can run are
// listed; the rest of section 1.8's table arrives with the features they need.
import { placementOf } from "@/model/labels";
import type { ExplorerId, NodeType } from "./tree";
import { CREATE_LABELS, DOMAIN_CREATE, EXPLORER_CREATE, folderCreate, type CreateKind } from "./create";
import { PROMOTABLE_KINDS } from "./promote";
import { TYPE_MENU, type TypeActionId } from "@/workspaces/reference-data/typeMenu";

export type MenuActionId =
  | "open"
  | "open-database"
  | "open-mappings"
  | "new-schema"
  | "show-on-canvas"
  | "add-to-diagram"
  | "add-with-related"
  | "where-used"
  | "go-to-table"
  | "go-to-entity"
  | "go-to-ends"
  | `new:${CreateKind}`
  | "duplicate"
  | "select-all"
  | "expand-all"
  | "search-in-domain"
  | "favorite"
  | "move"
  | "map-to-database"
  | "apply-stereotype"
  | "tag"
  | "set-category"
  | "promote"
  | "edit-seed-data"
  | "import-seed-csv"
  | "export-seeds"
  | "import-seeds"
  | "rename"
  | "delete"
  | `type:${TypeActionId}`;

export interface MenuItem {
  id: MenuActionId;
  label: string;
  /** Offered for a multi-selection (the ✱ of section 1.8). */
  multi: boolean;
  /** Drawn in the danger colour, after a separator. */
  danger?: boolean;
}

/** What the menu needs to know about a row. */
export interface MenuTarget {
  type: NodeType;
  kind?: string;
  /** The element is a file of the model (it can be renamed, moved or deleted). */
  element: boolean;
  /** An entity with a table, or a table linked to an entity. */
  linked?: boolean;
  /** The element is starred (3.3). */
  favorite?: boolean;
  /** The explorer the row is in (a group's New actions). */
  explorer?: ExplorerId;
  /** A group that stands for a domain (the Diagrams explorer's per-domain groups). */
  domainGroup?: boolean;
}

const item = (id: MenuActionId, label: string, multi = false, danger = false): MenuItem => ({ id, label, multi, danger });
const create = (kind: CreateKind): MenuItem => item(`new:${kind}`, CREATE_LABELS[kind]);

/** Kinds whose rows offer Apply stereotype…, Tag… and Set category… (✱). */
const MARKABLE: ReadonlySet<string> = new Set(["entity", "relation", "enum", "value-object", "scalar-type"]);

/** Kinds that live in a domain and can move to another one. */
export function isMovable(kind: string | undefined): boolean {
  if (!kind) return false;
  return kind === "package" || kind === "diagram" || placementOf(kind) === "domain";
}

function single(t: MenuTarget): MenuItem[] {
  if (t.type === "folder") {
    const made = folderCreate(t.kind);
    const news = made ? [create(made)] : [];
    return t.kind && t.kind !== "package"
      ? [...news, item("select-all", "Select all"), item("expand-all", "Expand all")]
      : [...news, item("expand-all", "Expand all")];
  }
  if (t.type === "domain")
    return t.element
      ? [
          item("open", "Open"),
          ...DOMAIN_CREATE.map(create),
          item("search-in-domain", "Search in this domain"),
          item("expand-all", "Expand all"),
          item("rename", "Rename"),
          item("move", "Move to domain…"),
          item("map-to-database", "Map to database…", true),
          item("export-seeds", "Export this domain's seed data"),
          item("import-seeds", "Import seed data…"),
          item("delete", "Delete", false, true),
        ]
      : [item("expand-all", "Expand all")];
  if (t.type === "database")
    return t.element
      ? [
          item("open-database", "Open Database screen"),
          item("open-mappings", "Open mappings"),
          item("new-schema", "New schema…"),
          item("expand-all", "Expand all"),
        ]
      : [item("open-database", "Open Database screen"), item("open-mappings", "Open mappings"), item("expand-all", "Expand all")];
  if (t.type === "group" && t.domainGroup) return [create("diagram"), item("expand-all", "Expand all")];
  if (t.type === "group" && t.explorer) return [...EXPLORER_CREATE[t.explorer].filter((k) => k !== "package").map(create), item("expand-all", "Expand all")];
  if (t.type === "schema" || t.type === "group" || t.type === "root") return [item("expand-all", "Expand all")];
  if (t.type === "table") return [item("open", "Open"), ...(t.linked ? [item("go-to-entity", "Go to entity")] : [])];
  if (!t.element) return [item("open", "Open")];
  const out: MenuItem[] = [item("open", "Open")];
  // A reference type's actions run in the Reference data screen (TypeMenu.tsx); Delete there checks its usages.
  if (t.kind === "reference-type")
    return [
      ...out,
      ...TYPE_MENU.filter((i) => i.id !== "export-csv").map((i) => item(`type:${i.id}`, i.label, i.multi, i.danger)),
      item("favorite", t.favorite ? "Remove from favorites" : "Add to favorites"),
    ].sort((a, b) => Number(!!a.danger) - Number(!!b.danger));
  if (t.kind === "entity") {
    out.push(item("add-to-diagram", "Add to diagram", true), item("add-with-related", "Add with related…", true), item("show-on-canvas", "Show on canvas"));
    out.push(item("where-used", "Where used"), item("map-to-database", "Map to database…", true));
    out.push(item("edit-seed-data", "Edit seed data"), item("import-seed-csv", "Import seed CSV…"));
    if (t.linked) out.push(item("go-to-table", "Go to table"));
  } else if (t.kind === "relation")
    out.push(item("add-to-diagram", "Add to diagram", true), item("go-to-ends", "Go to ends"), item("where-used", "Where used"));
  else if (t.kind === "diagram") out.push(item("duplicate", "Duplicate"));
  else out.push(item("where-used", "Where used"));
  if (t.kind && MARKABLE.has(t.kind))
    out.push(item("apply-stereotype", "Apply stereotype…", true), item("tag", "Tag…", true), item("set-category", "Set category…", true));
  if (t.kind && PROMOTABLE_KINDS.has(t.kind)) out.push(item("promote", "Promote to entity"));
  if (isMovable(t.kind)) out.push(item("move", "Move to domain…", true), item("rename", "Rename"));
  out.push(item("favorite", t.favorite ? "Remove from favorites" : "Add to favorites"));
  out.push(item("delete", "Delete", true, true));
  return out;
}

/** The menu for a selection: one row's actions, or those valid for every row of a multi-selection. */
export function menuFor(targets: readonly MenuTarget[]): MenuItem[] {
  if (targets.length === 0) return [];
  const first = single(targets[0]);
  if (targets.length === 1) return first;
  const kind = targets[0].kind;
  if (targets.some((t) => t.kind !== kind || t.type !== targets[0].type)) return [];
  return first.filter((i) => i.multi && targets.every((t) => single(t).some((o) => o.id === i.id)));
}

/** The explorer header's menu (1.8): the explorers the current one can be pinned beside. */
export const HEADER_ACTIONS = { collapseAll: "Collapse all", pin: "Pin beside…", unpin: "Unpin", highlight: "Highlight related elements" } as const;
