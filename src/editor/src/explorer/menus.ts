// The explorer's context menus (explorer-redesign.md 1.8), as data: which actions a row, or a multi-selection of
// rows of one kind, offers. RowMenu.tsx renders them; Explorer.tsx runs them. Only actions this editor can run are
// listed; the rest of section 1.8's table arrives with the features they need.
import { placementOf, PROCESS_LABELS, USED_LABEL } from "@/model/labels";
import type { ExplorerId, NodeType } from "./tree";
import { CREATE_LABELS, DOMAIN_CREATE, EXPLORER_CREATE, folderCreate, type CreateKind } from "./create";
import { PROMOTABLE_KINDS } from "./promote";
import { DATABASE_CREATE, DATABASE_CREATE_LABELS, DATABASE_ELEMENT_KINDS, databaseFolderCreate, type DatabaseObjectKind } from "./databaseCreate";
import { TYPE_MENU, type TypeActionId } from "@/workspaces/reference-data/typeMenu";

export type MenuActionId =
  | "open"
  | "open-database"
  | "open-mappings"
  | `new-db:${DatabaseObjectKind}`
  | "show-in-database"
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
  | "open-new-tab"
  | "simulate"
  | "verify-scenarios"
  | "export-xstate"
  | "import-xstate"
  | `type:${TypeActionId}`;

export interface MenuItem {
  id: MenuActionId;
  label: string;
  /** Offered for a multi-selection (the ✱ of section 1.8). */
  multi: boolean;
  /** Drawn in the danger colour, after a separator. */
  danger?: boolean;
  /** Shown but not offered yet: the note says when it arrives. */
  disabledNote?: string;
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
  /** A process's diagram (its statechart's places): one per process, so it is never duplicated. */
  processDiagram?: boolean;
  /** The explorer the row is listed in (a reference type's seed, listed under its type, is renamed with the type). */
  home?: ExplorerId;
  /** A table row with a file of its own (designed or imported): it opens in the table editor. */
  designed?: boolean;
}

const item = (id: MenuActionId, label: string, multi = false, danger = false): MenuItem => ({ id, label, multi, danger });
const create = (kind: CreateKind): MenuItem => item(`new:${kind}`, CREATE_LABELS[kind]);
const createInDatabase = (kind: DatabaseObjectKind): MenuItem => item(`new-db:${kind}`, DATABASE_CREATE_LABELS[kind]);

/** Kinds of the Processes explorer (phase-3-design.md 6.1). */
const PROCESS_KINDS: ReadonlySet<string> = new Set(["process", "actor", "scenario"]);

/** A process, actor or scenario row's actions (6.1, after explorer-redesign.md 1.8). */
function processMenu(t: MenuTarget): MenuItem[] {
  const out: MenuItem[] = [item("open", "Open"), item("open-new-tab", "Open in new tab")];
  if (t.kind === "process")
    out.push(item("simulate", PROCESS_LABELS.simulate), item("verify-scenarios", PROCESS_LABELS.verify), item("export-xstate", PROCESS_LABELS.exportXState));
  out.push(item("where-used", USED_LABEL));
  if (t.kind === "process") out.push(item("move", "Move to domain…", true));
  out.push(item("rename", "Rename"), item("favorite", t.favorite ? "Remove from favorites" : "Add to favorites"), item("delete", "Delete", true, true));
  return out;
}

/** Kinds whose rows offer Apply stereotype…, Tag… and Set category… (✱). A reference type's category is its "Move to
 * category…", so its rows offer the other two. */
const MARKABLE: ReadonlySet<string> = new Set(["entity", "relation", "enum", "value-object", "scalar-type", "reference-type"]);

/**
 * Kinds that live in a domain and can move to another one. A seed is not one: it belongs to its target, whose domain
 * it shares (a seed file has no `package`), and a reference type's seeds have no domain at all (RS3).
 */
export function isMovable(kind: string | undefined): boolean {
  if (!kind || kind === "seed") return false;
  return kind === "package" || kind === "diagram" || placementOf(kind) === "domain";
}

/** Rows renamed in the explorer: the movable kinds, and an entity's or relation's seed (a reference type's seed is
 * renamed with its type, by Rename on the type). */
export function isRenamable(kind: string | undefined, home?: string): boolean {
  if (kind === "seed") return home !== "reference-data";
  return isMovable(kind);
}

function single(t: MenuTarget): MenuItem[] {
  // A kind folder of a database (Tables, Views, Sequences, Routines, Types, Objects, or a table group in it): its New action.
  const inDatabase = t.home === "databases" && (t.type === "folder" || t.type === "group") ? databaseFolderCreate(t.kind) : null;
  if (inDatabase)
    return t.type === "folder"
      ? [createInDatabase(inDatabase), item("select-all", "Select all"), item("expand-all", "Expand all")]
      : [createInDatabase(inDatabase), item("expand-all", "Expand all")];
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
          ...DATABASE_CREATE.map(createInDatabase),
          item("expand-all", "Expand all"),
        ]
      : [item("open-database", "Open Database screen"), item("open-mappings", "Open mappings"), item("expand-all", "Expand all")];
  if (t.type === "group" && t.domainGroup && t.explorer === "processes")
    return [create("process"), item("import-xstate", PROCESS_LABELS.importXState), item("expand-all", "Expand all")];
  if (t.type === "group" && t.domainGroup) return [create("diagram"), item("expand-all", "Expand all")];
  if (t.type === "group" && t.explorer) return [...EXPLORER_CREATE[t.explorer].filter((k) => k !== "package").map(create), item("expand-all", "Expand all")];
  if (t.type === "schema") return [...DATABASE_ELEMENT_KINDS.map(createInDatabase), item("expand-all", "Expand all")];
  if (t.type === "group" || t.type === "root") return [item("expand-all", "Expand all")];
  if (t.type === "table")
    return [
      item("open", "Open"),
      ...(t.designed ? [item("show-in-database", "Show in Database screen")] : []),
      ...(t.linked ? [item("go-to-entity", "Go to entity")] : []),
    ];
  if (!t.element) return [item("open", "Open")];
  if (t.kind && PROCESS_KINDS.has(t.kind)) return processMenu(t);
  const out: MenuItem[] = [item("open", "Open")];
  // A reference type's actions run in the Reference data screen (TypeMenu.tsx); Delete there checks its usages.
  if (t.kind === "reference-type")
    return [
      ...out,
      ...TYPE_MENU.filter((i) => i.id !== "export-csv").map((i) => item(`type:${i.id}`, i.label, i.multi, i.danger)),
      item("apply-stereotype", "Apply stereotype…", true),
      item("tag", "Tag…", true),
      item("favorite", t.favorite ? "Remove from favorites" : "Add to favorites"),
    ].sort((a, b) => Number(!!a.danger) - Number(!!b.danger));
  if (t.kind === "entity") {
    out.push(item("add-to-diagram", "Add to diagram", true), item("add-with-related", "Add with related…", true), item("show-on-canvas", "Show on canvas"));
    out.push(item("where-used", USED_LABEL), item("map-to-database", "Map to database…", true));
    out.push(item("edit-seed-data", "Edit seed data"), item("import-seed-csv", "Import seed CSV…"));
    if (t.linked) out.push(item("go-to-table", "Go to table"));
  } else if (t.kind === "relation") out.push(item("add-to-diagram", "Add to diagram", true), item("go-to-ends", "Go to ends"), item("where-used", USED_LABEL));
  else if (t.kind === "diagram") {
    if (!t.processDiagram) out.push(item("duplicate", "Duplicate"));
  } else out.push(item("where-used", USED_LABEL));
  if (t.kind && MARKABLE.has(t.kind))
    out.push(item("apply-stereotype", "Apply stereotype…", true), item("tag", "Tag…", true), item("set-category", "Set category…", true));
  if (t.kind && PROMOTABLE_KINDS.has(t.kind)) out.push(item("promote", "Promote to entity"));
  if (isMovable(t.kind)) out.push(item("move", "Move to domain…", true));
  if (isRenamable(t.kind, t.home)) out.push(item("rename", "Rename"));
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
