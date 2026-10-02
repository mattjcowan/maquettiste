// The explorer's tree model (explorer-redesign.md sections 1.1 to 1.7, 1.9, 1.11 and 4.3): pure functions over
// the index rows, the table summaries (E5c) and the project's explorer folders. One tree per explorer (Domain model,
// Reference data, Databases, Diagrams), built in one O(n) pass and memoized by index tag, with a parent map, the
// home place of every element, rolled-up counts and error badges, the related maps and each domain's vocabulary
// chain. Element children that the index answers (relationships, relation ends, seed data, mappings) are built on
// demand by `childKeys`; children that need a document (attributes, enum members, a table's columns) are added by
// `documentChildren` and `tableChildren` once it is loaded.
import { EMPTY_DATABASE_HINT } from "@/model/databaseMapping";
import type { Diagnostic, ElementDocument, ElementSummary, ExplorerFolder, TableSummary, TableView } from "@/api/types";
import type { AttributeDoc } from "@/api/types";
import { attributesOf, typeLabel } from "@/model/model";
import { tableKeyOf } from "@/search/engine";
import { patchesBetween, type IndexPatch } from "@/api/indexPatch";
import {
  EXPLORER_LABELS,
  GROUP_LABELS,
  KIND_FOLDERS,
  KIND_LABELS,
  SUB_DOMAIN_LABEL,
  OTHER_FOLDER,
  ACTOR_TYPE_LABELS,
  PROCESS_LABELS,
  CHART_LABELS,
  countOf,
  kindFolder,
  placementOf,
  type KindFolder,
} from "@/model/labels";

export type ExplorerId = keyof typeof EXPLORER_LABELS;
/** The explorers in rail order. */
export const EXPLORERS: readonly ExplorerId[] = ["domain-model", "processes", "reference-data", "databases", "diagrams"];

export type NodeType = "root" | "group" | "domain" | "folder" | "element" | "database" | "schema" | "table" | "item";

export interface TreeNode {
  /** Unique in the forest. An element's own row is keyed by its id; a navigation row by `<parent>/<id>`. */
  key: string;
  type: NodeType;
  explorer: ExplorerId;
  label: string;
  secondary?: string;
  tooltip?: string;
  /** An icon name (model/labels.ts); the row maps it to a glyph. */
  icon: string;
  /** The element (or sub-element) the row stands for. */
  id?: string;
  /** The element's kind, or the kind a folder holds. */
  kind?: string;
  /** Whether this row is the element's place; navigation rows (an entity's relationships, a relation's ends) are not. */
  home: boolean;
  /** For a navigation row, the key of the row it leads to. */
  target?: string;
  /** Direct members of a folder or group. */
  count?: number;
  /** Error count: the element's own, rolled up on containers. */
  errors: number;
  /** A broken place: a domain that does not exist, or a parent chain that loops. */
  warning?: string;
  /** A database whose table summaries have not arrived; its counts are the index's. */
  pending?: boolean;
  /** A database whose table summaries are the last complete ones (the model has errors). */
  stale?: boolean;
  /** Small markers: "unlinked", "junction", "customised" (no "lookup" marker, EX 1.3). */
  markers?: string[];
  /** Expanding needs a load to add children: the element's document, or the table's detail. */
  load?: "document" | "table";
  table?: TableSummary;
  /** A column row (from the table's detail): the attribute mapped onto it (`ColumnView.attributeId`), null for none. */
  attribute?: string | null;
  /** Child keys; undefined until `childKeys` builds an element's index-answered children. */
  children?: string[];
  /** Sort key (lower-cased label). */
  sort: string;
  /** The parent row's key (the parent map lives on the nodes: one write fewer per row). */
  parent?: string;
}

/** The read and write surface of the parent and place maps. */
export interface KeyMap {
  get(key: string): string | undefined;
  set(key: string, value: string): void;
}

/** The parent map, stored on the nodes. A node is registered before its parent is set. */
class ParentMap implements KeyMap {
  constructor(private readonly nodes: Map<string, TreeNode>) {}
  get(key: string) {
    return this.nodes.get(key)?.parent;
  }
  set(key: string, value: string) {
    this.nodes.get(key)!.parent = value;
  }
}

/**
 * Element id → its own row. An element whose row is keyed by its id needs no entry; one whose folder has not been
 * opened yet is found through its folder, which is built on the way.
 */
class PlaceMap implements KeyMap {
  private readonly moved = new Map<string, string>();
  /** Element id → the folder that lists it, for folders whose rows are built on first ask. */
  readonly folderOf = new Map<string, string>();
  open: (folder: string) => void = () => {};
  constructor(private readonly nodes: Map<string, TreeNode>) {}
  get(id: string) {
    const moved = this.moved.get(id);
    if (moved !== undefined) return moved;
    let n = this.nodes.get(id);
    if (!n) {
      const folder = this.folderOf.get(id);
      if (folder === undefined) return undefined;
      this.open(folder);
      n = this.nodes.get(id);
    }
    return n && n.home && n.id === id ? id : undefined;
  }
  set(id: string, key: string) {
    if (key !== id) this.moved.set(id, key);
  }
}

/** Index members designed but not yet in the contract (E5h `base`; seeds' `target` and `rowCount`), read when present. */
export type IndexRow = Omit<ElementSummary, "kind"> & {
  /** Any kind: the tree places kinds this editor version does not know (section 1.2). */
  kind: string;
  base?: string | null;
  target?: string | null;
  rowCount?: number | null;
  fieldCount?: number | null;
};
type Row = IndexRow;

/** A scenario's last verify result (phase-3-design.md 6.2): passed, or the 1-based step it failed at (0: the outcome). */
export type ScenarioStatus = { passed: true } | { passed: false; step: number };
/** The E5c addendum `enumId`, read when present. */
type Summary = TableSummary & { enumId?: string | null };

export interface DatabaseInfo {
  dialect?: string | null;
  version?: string | null;
  defaultSchema?: string | null;
  /** The declared schema names (erratum E26): each gets a row, empty or not. */
  schemas?: readonly string[];
}

export interface TablesInput {
  tables: readonly TableSummary[];
  /** The last complete summaries, kept while the model has errors. */
  stale?: boolean;
}

export interface TreeInput {
  rows: readonly ElementSummary[];
  /** The index's ETag (E5e): the memo key. Without one, the rows array's identity is. */
  tag?: string | null;
  /** Table summaries by database id, as they arrive. */
  tables?: ReadonlyMap<string, TablesInput | undefined>;
  /** Dialect, version and default schema by database id, when known. */
  databases?: ReadonlyMap<string, DatabaseInfo>;
  /** `explorer.folders` from the project settings. */
  folders?: readonly ExplorerFolder[];
  /** Category-tree node → parent node, for folder conditions on a category (its descendants match too). */
  categoryParents?: ReadonlyMap<string, string | null>;
  /** Category-tree node → its name, for the Reference data explorer's category groups (EX 1.7). */
  categoryNames?: ReadonlyMap<string, string>;
  /** The Reference data explorer's A to Z option: every type in one list, no category groups (RT 4.2). */
  referenceFlat?: boolean;
  /** Error counts by element id (see `errorCounts`). */
  errors?: ReadonlyMap<string, number>;
  /** The Databases view option: Tables in folders named after the owning element's domain. */
  groupTablesByDomain?: boolean;
  /** Scenario id → its last verify result (the Processes explorer's passed counts). */
  scenarioStatus?: ReadonlyMap<string, ScenarioStatus>;
}

export interface RelatedMaps {
  /** Entity → the relations it is an end of. */
  relationsOf: Map<string, string[]>;
  /** Relation → its end entities, in end order. */
  endsOf: Map<string, string[]>;
  /** Entity, relation or enum → the table rows mapped from it. */
  tablesOf: Map<string, string[]>;
  /** Table row → the entity, relation or enum mapped onto it. */
  mappedBy: Map<string, string>;
  /** Relation → its junction table row. */
  junctionOf: Map<string, string>;
  /** Enum → its lookup table rows (needs `enumId`). */
  lookupOf: Map<string, string[]>;
  /** Derived → base entity, and base → derived (need `base`, E5h). */
  baseOf: Map<string, string>;
  derivedOf: Map<string, string[]>;
  /** Seed target → seeds. */
  seedsOf: Map<string, string[]>;
  /** Entity → its mapping files. */
  mappingsOf: Map<string, string[]>;
}

export interface VocabularyRef {
  id: string;
  kind: "tag-vocabulary" | "category-tree";
  /** The domain that declares it; null for a global one. */
  scope: string | null;
}

/** The vocabularies an element sees, nearest first, then the global ones (section 1.11). */
export interface VocabularyChain {
  tags: VocabularyRef[];
  categories: VocabularyRef[];
}

export interface Forest {
  tag: string | null;
  byId: ReadonlyMap<string, IndexRow>;
  nodes: Map<string, TreeNode>;
  /** Row → its parent row (held on the nodes; see `TreeNode.parent`). */
  parent: KeyMap;
  /** Element id → the key of its own row. */
  place: KeyMap;
  roots: Record<ExplorerId, string>;
  /** Each explorer's header totals ("40 domains · 5,000 entities"). */
  headers: Record<ExplorerId, string>;
  related: RelatedMaps;
  /** Domain id → its vocabulary chain; "" for the global chain. */
  vocabularies: Map<string, VocabularyChain>;
  /** Rows no tree lists: vocabularies and stereotypes (the Settings screen). */
  unplaced: string[];
  /** Folders whose element rows are built on first ask (`childKeys`), so the first paint pays only for what it shows. */
  pending: Map<string, () => TreeNode[]>;
  /** State the incremental patch (`patchForest`) keeps current; not for readers. */
  patch: PatchState;
}

/** What `patchForest` needs from the build to patch in place rather than rebuild. */
export interface PatchState {
  /** A domain-model folder's key → its rows (the pending builder lists them on first ask). */
  lists: Map<string, IndexRow[]>;
  /** Domain id → its rolled-up counts by kind (a domain's secondary text). */
  rollup: Map<string, Record<string, number>>;
  /** The Domain model header's totals. */
  totals: { packages: number; entities: number };
  /** Kinds a project-defined folder takes rows of (tags, category and stereotypes then place a row). */
  projectKinds: Set<string>;
  errors?: ReadonlyMap<string, number>;
}

const ROOTS: Record<ExplorerId, string> = {
  "domain-model": "@domain-model",
  processes: "@processes",
  "reference-data": "@reference-data",
  databases: "@databases",
  diagrams: "@diagrams",
};
const NOT_IN_DOMAIN = "@domain-model/not-in-domain";
const PEOPLE = "@domain-model/people";
const OTHER_ELEMENTS = "@domain-model/other";
const NOT_IN_DATABASE = "@databases/none";
const DOCUMENT_KINDS = new Set(["entity", "relation", "enum", "value-object"]);
const INDEX_CHILD_KINDS = new Set(["entity", "relation"]);
const CONTAINERS = new Set<NodeType>(["root", "group", "domain", "folder", "database", "schema"]);

const fmt = (n: number) => n.toLocaleString("en-US");
const plural = (n: number, one: string, many: string) => `${fmt(n)} ${n === 1 ? one : many}`;

function push<K, V>(map: Map<K, V[]>, key: K, value: V) {
  const list = map.get(key);
  if (list) list.push(value);
  else map.set(key, [value]);
}

function byLabel(a: TreeNode, b: TreeNode): number {
  return a.sort < b.sort ? -1 : a.sort > b.sort ? 1 : a.key < b.key ? -1 : a.key > b.key ? 1 : 0;
}

/** Error counts by element id, from a validation report's diagnostics. */
export function errorCounts(diagnostics: readonly Diagnostic[] | undefined): Map<string, number> {
  const out = new Map<string, number>();
  for (const d of diagnostics ?? []) if (d.severity === "error" && d.elementId) out.set(d.elementId, (out.get(d.elementId) ?? 0) + 1);
  return out;
}

/** An element row's label: its display name or name, an overlay's entity name, or "<kind> <id tail>". */
function rowLabel(byId: ReadonlyMap<string, Row>, r: Row): string {
  const e = r.entity ? byId.get(r.entity) : undefined;
  return r.displayName || r.name || (e ? e.displayName || e.name : undefined) || `${kindFolder(r.kind)?.one ?? r.kind} ${r.id.slice(-6)}`;
}

/**
 * True when a reference type has one seed, whatever its name: that seed is the type's rows, which the Reference data
 * explorer does not list (as a rename takes the only seed along, typeMenu.ts seedsFollowingRename).
 */
export function soleSeedOf(seeds: readonly unknown[]): boolean {
  return seeds.length === 1;
}

/** An element row's secondary text: a relation's ends, a diagram's member count (a process diagram: whose statechart it is), a seed's row count. */
function rowSecondary(byId: ReadonlyMap<string, Row>, r: Row): string | undefined {
  const nameOf = (id: string) => {
    const e = byId.get(id);
    return e ? e.displayName || e.name : undefined;
  };
  if (r.kind === "relation" && r.ends?.length) {
    let text = "";
    for (const e of r.ends) text = text ? `${text} → ${nameOf(e.entity) ?? "?"}` : (nameOf(e.entity) ?? "?");
    return text;
  }
  if (r.kind === "diagram" && r.process) return CHART_LABELS.statechartOf(nameOf(r.process) ?? "?");
  if (r.kind === "diagram") return plural(r.memberCount ?? 0, "member", "members");
  if (r.kind === "seed" && r.rowCount != null) return plural(r.rowCount, "row", "rows");
  if (r.kind === "process") return processSecondary(r, r.subject ? nameOf(r.subject) : undefined, undefined);
  if (r.kind === "actor") return actorSecondary(r);
  return undefined;
}

/** A process row's secondary text: "lifecycle · Invoice.status · 4 states" (the attribute once the document is loaded). */
export function processSecondary(r: Pick<Row, "use" | "stateCount">, subject: string | undefined, attribute: string | undefined): string {
  const use = r.use === "lifecycle" ? PROCESS_LABELS.lifecycle : PROCESS_LABELS.orchestration;
  const parts: string[] = [use];
  if (subject) parts.push(attribute ? `${subject}.${attribute}` : subject);
  parts.push(plural(r.stateCount ?? 0, "state", "states"));
  return parts.join(" · ");
}

/** An actor row's secondary text: its type, then its stereotypes ("person · persona"). */
export function actorSecondary(r: Pick<Row, "actorType" | "stereotypes">): string {
  return [r.actorType ? ACTOR_TYPE_LABELS[r.actorType] : "", ...(r.stereotypes ?? [])].filter(Boolean).join(" · ");
}

/** A scenario's status words: "passed", "failed at step 2", "not run". */
export function scenarioStatusText(status: ScenarioStatus | undefined): string {
  if (!status) return PROCESS_LABELS.notRun;
  return status.passed ? PROCESS_LABELS.passed : PROCESS_LABELS.failedAt(status.step);
}

/** An element's own row (not registered in any map). */
function newElementNode(byId: ReadonlyMap<string, Row>, r: Row, explorer: ExplorerId, key: string, errors: number): TreeNode {
  const f = kindFolder(r.kind) ?? OTHER_FOLDER;
  const label = rowLabel(byId, r);
  const node = newNode(key, "element", explorer, label, f.icon, label.toLowerCase());
  node.id = r.id;
  node.kind = r.kind;
  node.errors = errors;
  if (DOCUMENT_KINDS.has(r.kind)) node.load = "document";
  if (!INDEX_CHILD_KINDS.has(r.kind)) node.children = [];
  node.secondary = rowSecondary(byId, r);
  return node;
}

let memo: { input: TreeInput; forest: Forest } | null = null;

/**
 * The forest for these inputs, rebuilt only when the index tag (or rows), or another input, changes. When only the
 * rows changed, and the new rows were patched from the memoized ones (api/indexPatch.ts: model.changed summaries or
 * a save), the forest is patched per changed domain instead (`patchForest`), and rebuilt only when a patch cannot
 * apply in place.
 */
export function forestOf(input: TreeInput): Forest {
  const last = memo?.input;
  if (!memo || !last || !sameExtras(input, last)) return remember(input, buildForest(input));
  if (input.tag ? input.tag === last.tag : input.rows === last.rows) return memo.forest;
  const chain = patchesBetween(last.rows, input.rows);
  let forest: Forest | null = chain ? memo.forest : null;
  for (const patch of chain ?? []) {
    forest = forest && patchForest(forest, patch);
    if (!forest) break;
  }
  return remember(input, forest ? { ...forest, tag: input.tag ?? null } : buildForest(input));
}

function remember(input: TreeInput, forest: Forest): Forest {
  memo = { input, forest };
  return forest;
}

function sameExtras(input: TreeInput, last: TreeInput): boolean {
  return (
    input.tables === last.tables &&
    input.databases === last.databases &&
    input.folders === last.folders &&
    input.categoryParents === last.categoryParents &&
    input.categoryNames === last.categoryNames &&
    input.errors === last.errors &&
    input.scenarioStatus === last.scenarioStatus &&
    !!input.groupTablesByDomain === !!last.groupTablesByDomain &&
    !!input.referenceFlat === !!last.referenceFlat
  );
}

// ------------------------------------------------------------------ incremental patch (section 4.4, step 12)

/** Fields that change a row's label or place; every other field (ends, base, entity, target...) is its shape. */
const LABEL_FIELDS = new Set(["id", "name", "displayName", "hash", "path", "package", "memberCount"]);
/** Fields that matter only when a project-defined folder takes rows of the kind. */
const MARK_FIELDS = new Set(["tags", "category", "stereotypes"]);

function sameShape(a: Row, b: Row, marks: boolean): boolean {
  const x = a as unknown as Record<string, unknown>;
  const y = b as unknown as Record<string, unknown>;
  for (const k of new Set([...Object.keys(x), ...Object.keys(y)])) {
    if (LABEL_FIELDS.has(k) || (!marks && MARK_FIELDS.has(k))) continue;
    if (x[k] !== y[k] && JSON.stringify(x[k] ?? null) !== JSON.stringify(y[k] ?? null)) return false;
  }
  return true;
}

/** Kinds whose rows the patch adds, removes and moves between domain folders. */
const movable = (kind: string) => placementOf(kind) === "domain" && kind !== "seed";

/**
 * Applies one index patch to the forest in place and returns the next forest object (the same node map, so the
 * explorer's memos re-run on the identity change), or null when some change cannot be patched and the forest must
 * be rebuilt. Patched in place: any change that leaves a row's shape alone (hash, path, tags without a project
 * folder); renames of domain-model elements and diagrams; adds, removals and moves of domain-model elements between
 * domains whose kind folder exists and stays non-empty. Rebuilt: domains, vocabularies, database objects, seeds,
 * changed relation ends or bases, elements with errors, and everything the related maps of an entity would carry
 * (relations, tables, mappings, seeds, derived entities) when that entity is added, removed or moved.
 */
export function patchForest(forest: Forest, patch: IndexPatch): Forest | null {
  const byId = forest.byId as Map<string, Row>;
  const { nodes, related } = forest;
  const state = forest.patch;
  const isDomain = (id: string | null | undefined) => !!id && byId.get(id)?.kind === "package" && nodes.get(id)?.type === "domain";
  const folderKey = (r: Row) => `${r.package}/${r.kind}`;
  const hasErrors = (id: string) => (state.errors?.get(id) ?? 0) > 0;
  const quietRows: [Row, Row][] = [];
  const renamed: [Row, Row][] = [];
  const removed: Row[] = [];
  const added: Row[] = [];
  const delta = new Map<string, number>();
  const steps: [Row | undefined, Row | undefined][] = [
    ...patch.upserts.map((u): [Row | undefined, Row] => [byId.get(u.id), u as Row]),
    ...patch.deleted.map((id): [Row | undefined, undefined] => [byId.get(id), undefined]),
  ];

  // The Processes explorer is rebuilt, not patched: a process, actor or scenario change, or a domain change while it lists
  // processes (its groups are named after domains).
  const listsProcesses = (nodes.get(ROOTS.processes)?.children?.length ?? 0) > 0;
  const PHASE3 = ["process", "actor", "scenario"];
  for (const [old, row] of steps) {
    const kind = row?.kind ?? old?.kind ?? "";
    if (PHASE3.includes(kind) || (kind === "package" && listsProcesses)) return null;
  }

  // Plan: every change is checked before anything is touched.
  for (const [old, row] of steps) {
    if (old && row) {
      // A reference type's category places it in the Reference data explorer's groups, so a category change rebuilds.
      if (old.kind !== row.kind || !sameShape(old, row, state.projectKinds.has(row.kind) || row.kind === "reference-type")) return null;
      const label = old.name !== row.name || old.displayName !== row.displayName || old.memberCount !== row.memberCount;
      if (old.package === row.package) {
        if (!label) quietRows.push([old, row]);
        else if (movable(row.kind) || (row.kind === "diagram" && isDomain(row.package))) renamed.push([old, row]);
        else return null;
        continue;
      }
    }
    for (const r of [old, row]) {
      if (!r) continue;
      if (!movable(r.kind) || state.projectKinds.has(r.kind) || !isDomain(r.package) || hasErrors(r.id) || r.base) return null;
      if (!nodes.has(folderKey(r))) return null;
      if (r.kind === "entity") {
        const id = r.id;
        const maps = [related.relationsOf, related.derivedOf, related.tablesOf, related.seedsOf, related.mappingsOf, related.lookupOf];
        if (maps.some((m) => m.get(id)?.length)) return null;
      }
      delta.set(folderKey(r), (delta.get(folderKey(r)) ?? 0) + (r === old ? -1 : 1));
    }
    if (old) removed.push(old);
    if (row) added.push(row);
  }
  for (const [key, d] of delta) if ((nodes.get(key)!.count ?? 0) + d < 1) return null;
  for (const [old] of renamed) {
    if (old.kind !== "entity") continue;
    if (related.mappingsOf.get(old.id)?.length) return null;
    // Rows without a name of their own are labelled with their entity's name.
    for (const r of byId.values()) if (r.entity === old.id && !r.name && !r.displayName && r.kind !== "relation") return null;
  }

  // Apply. Nodes whose shown fields change are replaced, never mutated (rows are memoized by node), and children
  // arrays are replaced too (positions are memoized per array).
  const edit = (key: string, change: Partial<TreeNode>): TreeNode | undefined => {
    const node = nodes.get(key);
    if (!node) return undefined;
    const next = { ...node, ...change };
    nodes.set(key, next);
    return next;
  };
  const reset = new Set<string>();
  const touch = (r: Row) => {
    reset.add(r.id);
    const ends = (r.kind === "relation" ? related.endsOf.get(r.id) : undefined) ?? [];
    for (const e of ends) reset.add(e);
    for (const rel of related.relationsOf.get(r.id) ?? []) {
      reset.add(rel);
      for (const e of related.endsOf.get(rel) ?? []) reset.add(e);
    }
  };
  const replaceInList = (old: Row, row: Row) => {
    const folder = forest.place instanceof PlaceMap ? forest.place.folderOf.get(old.id) : undefined;
    const list = folder ? state.lists.get(folder) : undefined;
    const at = list ? list.indexOf(old) : -1;
    if (list && at >= 0) list[at] = row;
  };
  const adjustRollup = (domain: string, kind: string, by: number) => {
    for (let key: string | undefined = domain, guard = 0; key && isDomain(key) && guard < 256; key = nodes.get(key)!.parent, guard++) {
      const counts = state.rollup.get(key);
      if (!counts) break;
      counts[kind] = (counts[kind] ?? 0) + by;
      if (counts[kind] === 0 && kind !== "package") delete counts[kind];
      edit(key, { secondary: countPhrase(counts) });
    }
  };
  const dropNode = (key: string) => {
    const node = nodes.get(key);
    if (!node) return;
    for (const c of node.children ?? []) dropNode(c);
    nodes.delete(key);
  };
  const insertChild = (holderKey: string, node: TreeNode) => {
    const list = nodes.get(holderKey)!.children!.filter((k) => k !== node.key);
    let lo = 0;
    let hi = list.length;
    while (lo < hi) {
      const mid = (lo + hi) >>> 1;
      if (byLabel(nodes.get(list[mid])!, node) < 0) lo = mid + 1;
      else hi = mid;
    }
    list.splice(lo, 0, node.key);
    edit(holderKey, { children: list });
  };
  const folderOf = forest.place instanceof PlaceMap ? forest.place.folderOf : new Map<string, string>();

  for (const [old, row] of quietRows) {
    byId.set(row.id, row);
    replaceInList(old, row);
  }
  for (const old of removed) {
    touch(old);
    if (old.kind === "relation") {
      for (const e of new Set(related.endsOf.get(old.id) ?? [])) {
        const list = (related.relationsOf.get(e) ?? []).filter((x) => x !== old.id);
        if (list.length) related.relationsOf.set(e, list);
        else related.relationsOf.delete(e);
      }
      related.endsOf.delete(old.id);
    }
    const key = folderKey(old);
    const list = state.lists.get(key);
    if (list) list.splice(list.indexOf(old), 1);
    folderOf.delete(old.id);
    const folder = nodes.get(key)!;
    edit(key, { count: (folder.count ?? 0) - 1, children: folder.children?.filter((k) => k !== old.id) });
    dropNode(old.id);
    adjustRollup(old.package!, old.kind, -1);
    if (old.kind === "entity") state.totals.entities--;
    byId.delete(old.id);
  }
  for (const row of added) {
    byId.set(row.id, row);
    if (row.kind === "relation" && row.ends) {
      const ends = row.ends.map((e) => e.entity);
      ends.forEach((e, i) => {
        if (ends.indexOf(e) === i) related.relationsOf.set(e, [...(related.relationsOf.get(e) ?? []), row.id]);
      });
      related.endsOf.set(row.id, ends);
    }
  }
  for (const [, row] of renamed) byId.set(row.id, row);
  for (const row of added) {
    touch(row);
    const key = folderKey(row);
    state.lists.get(key)?.push(row);
    folderOf.set(row.id, key);
    const folder = edit(key, { count: (nodes.get(key)!.count ?? 0) + 1 })!;
    if (folder.children && !forest.pending.has(key)) {
      const node = newElementNode(byId, row, "domain-model", row.id, 0);
      node.parent = key;
      nodes.set(node.key, node);
      insertChild(key, node);
    }
    adjustRollup(row.package!, row.kind, 1);
    if (row.kind === "entity") state.totals.entities++;
  }
  for (const [old, row] of renamed) {
    replaceInList(old, row);
    touch(row);
    const label = rowLabel(byId, row);
    const node = edit(row.id, { label, sort: label.toLowerCase(), secondary: rowSecondary(byId, row) });
    if (!node) continue;
    if (node.parent && nodes.get(node.parent)?.children) insertChild(node.parent, node);
  }
  // Relations whose ends name a renamed entity show the new name; index-answered children are built again on ask.
  for (const [, row] of renamed)
    if (row.kind === "entity")
      for (const rel of related.relationsOf.get(row.id) ?? []) {
        const r = byId.get(rel);
        if (r) edit(rel, { secondary: rowSecondary(byId, r) });
      }
  for (const id of reset) {
    const node = nodes.get(id);
    if (!node || !node.home || node.id !== id || !INDEX_CHILD_KINDS.has(node.kind ?? "") || !node.children) continue;
    for (const c of node.children) dropNode(c);
    edit(id, { children: undefined });
  }

  const headers = { ...forest.headers };
  if (added.some((r) => r.kind === "entity") || removed.some((r) => r.kind === "entity")) {
    headers["domain-model"] = `${plural(state.totals.packages, "domain", "domains")} · ${countOf(state.totals.entities, "entity")}`;
    edit(forest.roots["domain-model"], { secondary: headers["domain-model"] });
  }
  return { ...forest, headers };
}

/** Builds the forest (no memo). */
export function buildForest(input: TreeInput): Forest {
  const rows = input.rows as readonly Row[];
  const byId = new Map<string, Row>();
  for (const r of rows) byId.set(r.id, r);
  const nodes = new Map<string, TreeNode>();
  const parent = new ParentMap(nodes);
  const place = new PlaceMap(nodes);
  const pending = new Map<string, () => TreeNode[]>();
  const errorsOf = (id: string | null | undefined) => (id && input.errors?.get(id)) || 0;
  const related: RelatedMaps = {
    relationsOf: new Map(),
    endsOf: new Map(),
    tablesOf: new Map(),
    mappedBy: new Map(),
    junctionOf: new Map(),
    lookupOf: new Map(),
    baseOf: new Map(),
    derivedOf: new Map(),
    seedsOf: new Map(),
    mappingsOf: new Map(),
  };
  const nameOf = (id: string | null | undefined) => {
    const r = id ? byId.get(id) : undefined;
    return r ? r.displayName || r.name : undefined;
  };
  const labelOf = (r: Row) => rowLabel(byId, r);
  const isPackage = (id: string | null | undefined) => !!id && byId.get(id)?.kind === "package";

  const add = (node: TreeNode) => {
    nodes.set(node.key, node);
    return node;
  };
  const attach = (holder: TreeNode, children: TreeNode[]) => {
    holder.children = children.map((c) => {
      parent.set(c.key, holder.key);
      return c.key;
    });
    return holder;
  };
  const elementNode = (r: Row, explorer: ExplorerId, key = r.id): TreeNode => {
    const node = newElementNode(byId, r, explorer, key, errorsOf(r.id));
    place.set(r.id, key);
    return add(node);
  };
  const folderNode = (key: string, explorer: ExplorerId, f: KindFolder, members: TreeNode[], label = f.label, tooltip = f.tooltip): TreeNode => {
    members.sort(byLabel);
    return attach(
      add({ key, type: "folder", explorer, label, tooltip, icon: f.icon, kind: f.kind || undefined, home: true, count: members.length, errors: 0, sort: "" }),
      members,
    );
  };

  // ------------------------------------------------------------------ classify rows and the related maps

  const packages: Row[] = [];
  const domainRows: Row[] = [];
  const peopleRows: Row[] = [];
  const otherRows: Row[] = [];
  const databases: Row[] = [];
  const physical: Row[] = [];
  const diagrams: Row[] = [];
  const referenceTypes: Row[] = [];
  const vocabularies: Row[] = [];
  const unplaced: string[] = [];
  const processRows: Row[] = [];
  const actorRows: Row[] = [];
  const scenarioRows: Row[] = [];
  for (const r of rows) {
    if (r.kind === "process") processRows.push(r);
    else if (r.kind === "actor") actorRows.push(r);
    switch (placementOf(r.kind)) {
      case "processes":
        scenarioRows.push(r);
        break;
      case "package":
        packages.push(r);
        break;
      case "domain":
        domainRows.push(r);
        break;
      case "people":
        peopleRows.push(r);
        break;
      case "databases":
        if (r.kind === "database") databases.push(r);
        else physical.push(r);
        break;
      case "diagrams":
        diagrams.push(r);
        break;
      case "reference-data":
        referenceTypes.push(r);
        break;
      case "settings":
        unplaced.push(r.id);
        if (r.kind === "tag-vocabulary" || r.kind === "category-tree") vocabularies.push(r);
        break;
      default:
        otherRows.push(r);
    }
    if (r.kind === "relation" && r.ends) {
      const list = r.ends;
      const ends: string[] = new Array(list.length);
      for (let i = 0; i < list.length; i++) {
        const e = list[i].entity;
        ends[i] = e;
        if (ends.indexOf(e) === i) push(related.relationsOf, e, r.id);
      }
      related.endsOf.set(r.id, ends);
    } else if (r.kind === "mapping") {
      if (r.entity) push(related.mappingsOf, r.entity, r.id);
    } else if (r.kind === "seed" && r.target) {
      push(related.seedsOf, r.target, r.id);
    }
    if (r.base) {
      related.baseOf.set(r.id, r.base);
      push(related.derivedOf, r.base, r.id);
    }
  }

  // ------------------------------------------------------------------ domains: nesting, broken chains

  const subOf = new Map<string, Row[]>();
  const topLevel: Row[] = [];
  for (const p of packages) {
    if (p.package === null) topLevel.push(p);
    else push(subOf, p.package, p);
  }
  const reached = new Set<string>();
  const kids = new Map<string, Row[]>();
  const walk = (start: Row) => {
    reached.add(start.id);
    const stack = [start];
    while (stack.length) {
      const p = stack.pop()!;
      const list: Row[] = [];
      for (const c of subOf.get(p.id) ?? []) {
        if (reached.has(c.id)) continue;
        reached.add(c.id);
        list.push(c);
        stack.push(c);
      }
      kids.set(p.id, list);
    }
  };
  for (const p of topLevel) walk(p);
  const broken: { row: Row; warning: string }[] = [];
  for (const p of packages)
    if (!reached.has(p.id) && !isPackage(p.package)) {
      broken.push({ row: p, warning: "Its parent domain does not exist" });
      walk(p);
    }
  for (const p of [...packages].sort((a, b) => (a.id < b.id ? -1 : 1)))
    if (!reached.has(p.id)) {
      broken.push({ row: p, warning: "Its parent chain loops" });
      walk(p);
    }

  // ------------------------------------------------------------------ element placement

  const folderSpecs = input.folders ?? [];
  const foldersOfKind = new Map<string, number[]>();
  folderSpecs.forEach((f, i) => push(foldersOfKind, f.kind, i));
  const inCategory = (category: string | null, wanted: string) => {
    for (let c = category, guard = 0; c && guard < 64; c = input.categoryParents?.get(c) ?? null, guard++) if (c === wanted) return true;
    return false;
  };
  const projectFolderOf = (r: Row): number => {
    for (const i of foldersOfKind.get(r.kind) ?? []) {
      const m = folderSpecs[i].match;
      if (m.stereotype ? r.stereotypes.includes(m.stereotype) : m.tag ? r.tags.includes(m.tag) : m.category ? inCategory(r.category, m.category) : false)
        return i;
    }
    return -1;
  };

  /** Container (a domain id, or a group key) → folder id (a kind, `f:<i>`, or "other") → rows. */
  const buckets = new Map<string, Map<string, Row[]>>();
  const lists = new Map<string, Row[]>();
  const warnings = new Map<string, string>();
  const put = (container: string, folderId: string, r: Row) => {
    let byFolder = buckets.get(container);
    if (!byFolder) buckets.set(container, (byFolder = new Map()));
    push(byFolder, folderId, r);
  };
  const referenceSeeds = new Map<string, Row[]>();
  for (const r of domainRows) {
    let pkg = r.package;
    if (r.kind === "seed") {
      const target = r.target ? byId.get(r.target) : undefined;
      if (target?.kind === "reference-type") {
        push(referenceSeeds, target.id, r);
        continue;
      }
      pkg = target ? (target.package ?? null) : pkg;
    }
    const at = projectFolderOf(r);
    const folderId = at >= 0 ? `f:${at}` : r.kind;
    if (isPackage(pkg)) put(pkg!, folderId, r);
    else {
      if (pkg) warnings.set(r.id, "Its domain does not exist");
      put(NOT_IN_DOMAIN, folderId, r);
    }
  }
  for (const r of peopleRows) put(PEOPLE, r.kind, r);
  for (const r of otherRows) {
    if (isPackage(r.package)) put(r.package!, "other", r);
    else put(OTHER_ELEMENTS, `k:${r.kind}`, r);
  }

  /** The folders of one container, in the table's order, then the project folders, then Other. */
  const containerFolders = (container: string): TreeNode[] => {
    const byFolder = buckets.get(container);
    if (!byFolder) return [];
    const out: TreeNode[] = [];
    const elsewhere = new Map<string, string[]>();
    folderSpecs.forEach((spec, i) => {
      const n = byFolder.get(`f:${i}`)?.length;
      if (n) push(elsewhere, spec.kind, `${fmt(n)} in ${spec.label}`);
    });
    const members = (list: Row[]) =>
      list.map((r) => {
        const node = elementNode(r, "domain-model");
        const warning = warnings.get(r.id);
        if (warning) node.warning = warning;
        return node;
      });
    // A domain's folders list their rows on first ask; the count, the errors and each row's folder are known now.
    const folderNode = (key: string, explorer: ExplorerId, f: KindFolder, list: Row[], label = f.label, tooltip = f.tooltip): TreeNode => {
      const node = add({
        key,
        type: "folder",
        explorer,
        label,
        tooltip,
        icon: f.icon,
        kind: f.kind || undefined,
        home: true,
        count: list.length,
        errors: 0,
        sort: "",
      });
      if (input.errors?.size) for (const r of list) node.errors += errorsOf(r.id);
      for (const r of list) place.folderOf.set(r.id, key);
      lists.set(key, list);
      // The list, not a copy: patchForest keeps it current until the folder is opened.
      pending.set(key, () => members(lists.get(key) ?? []).sort(byLabel));
      return node;
    };
    for (const f of KIND_FOLDERS) {
      const list = byFolder.get(f.kind);
      if (!list) continue;
      const extra = elsewhere.get(f.kind);
      out.push(folderNode(`${container}/${f.kind}`, "domain-model", f, list, f.label, extra ? `${f.tooltip}; plus ${extra.join(", ")}` : f.tooltip));
    }
    folderSpecs.forEach((spec, i) => {
      const list = byFolder.get(`f:${i}`);
      if (!list) return;
      const kf = kindFolder(spec.kind) ?? OTHER_FOLDER;
      const m = spec.match;
      const condition = m.stereotype ? `stereotype \`${m.stereotype}\`` : m.tag ? `tag \`${m.tag}\`` : `category ${nameOf(m.category) ?? m.category}`;
      const node = folderNode(`${container}/f:${i}`, "domain-model", kf, list, spec.label, `${kf.label} with ${condition}, from project settings`);
      if (spec.icon) node.icon = spec.icon;
      out.push(node);
    });
    const other = byFolder.get("other");
    if (other) out.push(folderNode(`${container}/other`, "domain-model", OTHER_FOLDER, other));
    for (const [folderId, list] of byFolder)
      if (folderId.startsWith("k:")) {
        const kind = folderId.slice(2);
        out.push(folderNode(`${container}/${folderId}`, "domain-model", { ...OTHER_FOLDER, kind }, list, kind, `Elements of kind ${kind}`));
      }
    return out;
  };

  // ------------------------------------------------------------------ Domain model tree

  const domainOrder: Row[] = [];
  const domainNode = (p: Row): TreeNode => {
    const label = labelOf(p);
    place.set(p.id, p.id);
    return add({
      key: p.id,
      type: "domain",
      explorer: "domain-model",
      label,
      tooltip: `${isPackage(p.package) ? SUB_DOMAIN_LABEL : KIND_LABELS.package}: ${label}`,
      icon: "domain",
      id: p.id,
      kind: "package",
      home: true,
      errors: errorsOf(p.id),
      sort: label.toLowerCase(),
    });
  };
  const sortRows = (list: Row[]) =>
    list
      .map((p) => ({ p, sort: labelOf(p).toLowerCase() }))
      .sort((a, b) => (a.sort < b.sort ? -1 : a.sort > b.sort ? 1 : a.p.id < b.p.id ? -1 : 1))
      .map((x) => x.p);
  const kidParent = new Map<string, string>();
  const buildDomains = (start: Row) => {
    const stack = [start];
    while (stack.length) {
      const p = stack.pop()!;
      domainOrder.push(p);
      const node = domainNode(p);
      // A sub-domain's node is made when it is popped; its parent is the domain it was reached from.
      if (p.id !== start.id) node.parent = kidParent.get(p.id);
      const subs = sortRows(kids.get(p.id) ?? []);
      const folders = containerFolders(p.id);
      node.children = [...subs.map((c) => c.id), ...folders.map((f) => f.key)];
      for (const f of folders) f.parent = p.id;
      for (const c of subs) kidParent.set(c.id, p.id);
      for (let i = subs.length - 1; i >= 0; i--) stack.push(subs[i]);
    }
  };
  const tops = sortRows(topLevel);
  for (const p of tops) buildDomains(p);
  for (const { row } of broken) buildDomains(row);
  for (const { row, warning } of broken) nodes.get(row.id)!.warning = warning;

  // Rolled-up counts, bottom-up: every kind beneath a domain, and its sub-domains.
  const rollup = new Map<string, Record<string, number>>();
  for (let i = domainOrder.length - 1; i >= 0; i--) {
    const p = domainOrder[i];
    const counts: Record<string, number> = { package: 0 };
    for (const [, list] of buckets.get(p.id) ?? []) for (const r of list) counts[r.kind] = (counts[r.kind] ?? 0) + 1;
    for (const c of kids.get(p.id) ?? []) {
      counts.package += 1;
      for (const [k, n] of Object.entries(rollup.get(c.id)!)) counts[k] = (counts[k] ?? 0) + n;
    }
    rollup.set(p.id, counts);
    nodes.get(p.id)!.secondary = countPhrase(counts);
  }

  const domainRoot: TreeNode[] = [];
  const notInDomain = containerFolders(NOT_IN_DOMAIN);
  const brokenNodes = broken.map(({ row }) => nodes.get(row.id)!).sort(byLabel);
  if (notInDomain.length || brokenNodes.length) {
    const members = notInDomain.reduce((n, f) => n + (f.count ?? 0), 0) + brokenNodes.length;
    domainRoot.push(
      attach(
        add({
          key: NOT_IN_DOMAIN,
          type: "group",
          explorer: "domain-model",
          label: GROUP_LABELS.notInDomain,
          tooltip: "Elements that belong in a domain but have none",
          icon: "no-domain",
          home: true,
          count: members,
          secondary: fmt(members),
          errors: 0,
          sort: "",
        }),
        [...brokenNodes, ...notInDomain],
      ),
    );
  }
  for (const p of tops) domainRoot.push(nodes.get(p.id)!);
  const people = containerFolders(PEOPLE);
  if (people.length)
    domainRoot.push(
      attach(
        add({
          key: PEOPLE,
          type: "group",
          explorer: "domain-model",
          label: GROUP_LABELS.people,
          tooltip: "Actors and permissions; they belong to no domain",
          icon: "people",
          home: true,
          count: people.length,
          secondary: people.map((f) => `${f.label} ${fmt(f.count ?? 0)}`).join(" · "),
          errors: 0,
          sort: "",
        }),
        people,
      ),
    );
  const others = containerFolders(OTHER_ELEMENTS);
  if (others.length)
    domainRoot.push(
      attach(
        add({
          key: OTHER_ELEMENTS,
          type: "group",
          explorer: "domain-model",
          label: GROUP_LABELS.otherElements,
          tooltip: "Elements of kinds this editor version does not know",
          icon: "other",
          home: true,
          count: others.length,
          errors: 0,
          sort: "",
        }),
        others,
      ),
    );
  const entityTotal = domainRows.reduce((n, r) => n + (r.kind === "entity" ? 1 : 0), 0);
  const headers = {} as Record<ExplorerId, string>;
  headers["domain-model"] = `${plural(packages.length, "domain", "domains")} · ${countOf(entityTotal, "entity")}`;
  rootNode("domain-model", domainRoot);

  function rootNode(explorer: ExplorerId, children: TreeNode[]) {
    attach(
      add({
        key: ROOTS[explorer],
        type: "root",
        explorer,
        label: EXPLORER_LABELS[explorer],
        secondary: headers[explorer],
        icon: explorer,
        home: true,
        errors: 0,
        sort: "",
      }),
      children,
    );
  }

  // ------------------------------------------------------------------ Reference data tree

  const refNodes = referenceTypes.map((r) => {
    const node = elementNode(r, "reference-data");
    const seeds = referenceSeeds.get(r.id);
    // The editor makes one seed per type, named after it: a type's only seed (whatever its name) is the type's rows, so
    // the type row has no child and the seed is placed on it (selecting the seed reveals its type). Several seeds show,
    // each with its row count.
    if (seeds && soleSeedOf(seeds)) place.set(seeds[0].id, node.key);
    else if (seeds) attach(node, seeds.map((s) => elementNode(s, "reference-data")).sort(byLabel));
    if (r.fieldCount != null) node.secondary = plural(r.fieldCount, "field", "fields");
    return node;
  });
  // The explorer is the Reference data screen's list (EX 1.7, RT 4.2): types nest by their category path, each group
  // with a count of the types under it; types without a known category go to "No category" when any group exists.
  const refGroups = new Map<string, { node: TreeNode; members: TreeNode[] }>();
  const refTop: TreeNode[] = [];
  const noCategory: TreeNode[] = [];
  const categoryPath = (category: string | null | undefined): string[] => {
    const out: string[] = [];
    for (let c = category ?? null, guard = 0; c && guard < 64 && input.categoryNames?.has(c); c = input.categoryParents?.get(c) ?? null, guard++)
      out.unshift(c);
    return out;
  };
  const refGroup = (path: string[], depth: number) => {
    const id = path[depth]!;
    let group = refGroups.get(id);
    if (!group) {
      const label = input.categoryNames?.get(id) ?? id;
      const node = add({
        key: `${ROOTS["reference-data"]}/c:${id}`,
        type: "group",
        explorer: "reference-data",
        label,
        tooltip: `Category: ${label}`,
        icon: "category",
        home: true,
        count: 0,
        errors: 0,
        sort: label.toLowerCase(),
      });
      group = { node, members: [] };
      refGroups.set(id, group);
      (depth === 0 ? refTop : refGroups.get(path[depth - 1]!)!.members).push(node);
    }
    return group;
  };
  referenceTypes.forEach((r, i) => {
    const node = refNodes[i]!;
    const path = input.referenceFlat ? [] : categoryPath((r as { category?: string | null }).category);
    if (!path.length) {
      noCategory.push(node);
      return;
    }
    for (let d = 0; d < path.length; d++) refGroup(path, d).node.count! += 1;
    refGroup(path, path.length - 1).members.push(node);
  });
  for (const { node, members } of refGroups.values()) attach(node, members.sort(byLabel));
  if (refGroups.size && noCategory.length) {
    const node = add({
      key: `${ROOTS["reference-data"]}/c:-`,
      type: "group",
      explorer: "reference-data",
      label: GROUP_LABELS.noCategory,
      icon: "category",
      home: true,
      count: noCategory.length,
      errors: 0,
      sort: "\uffff",
    });
    refTop.push(attach(node, noCategory.sort(byLabel)));
  } else refTop.push(...noCategory);
  // Rows live on the seeds (rowCount is a seed row member), so the header sums the reference types' seeds.
  const referenceRows = [...referenceSeeds.values()].reduce((n, list) => n + list.reduce((m, s) => m + (s.rowCount ?? 0), 0), 0);
  headers["reference-data"] = `${plural(referenceTypes.length, "type", "types")} · ${plural(referenceRows, "row", "rows")}`;
  rootNode("reference-data", refTop.sort(byLabel));

  // ------------------------------------------------------------------ Databases tree

  const physicalOf = new Map<string, Row[]>();
  const loose: Row[] = [];
  const dbIds = new Set(databases.map((d) => d.id));
  for (const r of physical) {
    if (r.database && dbIds.has(r.database)) push(physicalOf, r.database, r);
    else loose.push(r);
  }
  const domainOfOwner = (owner: string | null | undefined): string | null => {
    const r = owner ? byId.get(owner) : undefined;
    return r && isPackage(r.package) ? r.package : null;
  };
  const tableFolder = (key: string, tables: TreeNode[], f: KindFolder): TreeNode => {
    if (!input.groupTablesByDomain) return folderNode(key, "databases", f, tables);
    const groups = new Map<string, TreeNode[]>();
    for (const t of tables) {
      const owner = t.table ? (t.table.entityId ?? t.table.relationId ?? (t.table as Summary).enumId) : t.id ? byId.get(t.id)?.entity : null;
      const domain = domainOfOwner(owner);
      push(groups, domain ?? (owner ? "" : "-"), t);
      if (t.markers) t.markers = t.markers.filter((m) => m !== "unlinked");
    }
    const groupNodes: TreeNode[] = [];
    for (const [domain, list] of groups) {
      const label = domain === "-" ? GROUP_LABELS.notLinked : domain === "" ? GROUP_LABELS.notInDomain : (nameOf(domain) ?? domain);
      const node = folderNode(`${key}/d:${domain}`, "databases", f, list, label, domain.length > 1 ? `Domain: ${label}` : label);
      node.type = "group";
      node.icon = domain.length > 1 ? "domain" : f.icon;
      node.sort = domain === "-" ? "￿" : label.toLowerCase();
      groupNodes.push(node);
    }
    groupNodes.sort(byLabel);
    return attach(
      add({
        key,
        type: "folder",
        explorer: "databases",
        label: f.label,
        tooltip: f.tooltip,
        icon: f.icon,
        kind: f.kind,
        home: true,
        count: tables.length,
        errors: 0,
        sort: "",
      }),
      groupNodes,
    );
  };
  const schemaFolders = (key: string, s: { tables: TreeNode[]; views: TreeNode[]; sequences: TreeNode[] }) => {
    const out: TreeNode[] = [];
    if (s.tables.length) out.push(tableFolder(`${key}/table`, s.tables, kindFolder("table")!));
    if (s.views.length) out.push(folderNode(`${key}/view`, "databases", kindFolder("view")!, s.views));
    if (s.sequences.length) out.push(folderNode(`${key}/sequence`, "databases", kindFolder("sequence")!, s.sequences));
    return out;
  };
  let tableTotal = 0;
  const databaseNodes = databases.map((db) => {
    const info = input.databases?.get(db.id);
    const loaded = input.tables?.get(db.id);
    const own = physicalOf.get(db.id) ?? [];
    type Bucket = { tables: TreeNode[]; views: TreeNode[]; sequences: TreeNode[] };
    const schemas = new Map<string, Bucket>();
    const bucket = (name: string) => {
      let b = schemas.get(name);
      if (!b) schemas.set(name, (b = { tables: [], views: [], sequences: [] }));
      return b;
    };
    // Declared schemas show even while empty, so a new schema appears at once (erratum E26).
    if ((info?.schemas?.length ?? 0) > 1) for (const name of info!.schemas!) bucket(name);
    const tableKey = new Map<string, string>();
    const ownerTable = new Map<string, TreeNode>();
    if (loaded) {
      for (const t of loaded.tables as readonly Summary[]) {
        const key = tableKeyOf(db.id, t.key);
        const markers: string[] = [];
        if (t.isJunction) markers.push("junction");
        const owner = t.entityId ?? t.relationId ?? t.enumId ?? null;
        if (!owner && !t.isLookup) markers.push("unlinked");
        const node = add({
          key,
          type: "table",
          explorer: "databases",
          label: t.name,
          icon: "table",
          kind: "table",
          home: true,
          table: t,
          load: "table",
          errors: 0,
          sort: t.name.toLowerCase(),
          children: [],
        });
        if (markers.length) node.markers = markers;
        tableKey.set(t.key, key);
        if (owner) {
          related.mappedBy.set(key, owner);
          push(related.tablesOf, owner, key);
          if (t.entityId && !t.isJunction && !ownerTable.has(t.entityId)) ownerTable.set(t.entityId, node);
        }
        if (t.isJunction && t.relationId) related.junctionOf.set(t.relationId, key);
        if (t.enumId) push(related.lookupOf, t.enumId, key);
        bucket(t.schema ?? "").tables.push(node);
      }
    }
    const fallbackSchema = () => info?.defaultSchema ?? (schemas.size === 1 ? [...schemas.keys()][0] : "");
    const mappings: TreeNode[] = [];
    for (const r of own) {
      if (r.kind === "table" && loaded) {
        // A table file is its summary's row: by key (designed, imported), or by entity (an overlay).
        const node = nodes.get(tableKey.get(r.id) ?? "") ?? (r.entity ? ownerTable.get(r.entity) : undefined);
        if (node) {
          node.id = r.id;
          node.errors += errorsOf(r.id);
          place.set(r.id, node.key);
          continue;
        }
      }
      if (r.kind === "mapping") {
        const node = elementNode(r, "databases");
        if (r.entity) node.label = `${nameOf(r.entity) ?? "?"} → ${ownerTable.get(r.entity)?.label ?? r.name}`;
        node.sort = node.label.toLowerCase();
        mappings.push(node);
        continue;
      }
      const node = elementNode(r, "databases");
      const b = bucket(fallbackSchema());
      (r.kind === "table" ? b.tables : r.kind === "view" ? b.views : b.sequences).push(node);
    }
    const schemaNodes = [...schemas.entries()]
      .sort(([a], [b]) => (a === b ? 0 : a === "" ? -1 : b === "" ? 1 : a < b ? -1 : 1))
      .map(([name, s]) => {
        const key = `${db.id}/s:${name}`;
        // With several schemas the default one says so (its unqualified tables land there).
        const isDefault = schemas.size > 1 && !!name && name === info?.defaultSchema;
        const phrase = [
          isDefault ? "default" : "",
          s.tables.length ? countOf(s.tables.length, "table") : "",
          s.views.length ? countOf(s.views.length, "view") : "",
          s.sequences.length ? countOf(s.sequences.length, "sequence") : "",
        ]
          .filter(Boolean)
          .join(" · ");
        return attach(
          add({
            key,
            type: "schema",
            explorer: "databases",
            label: name || GROUP_LABELS.defaultSchema,
            tooltip: name ? `Schema: ${name}${isDefault ? " (Default schema)" : ""}` : "Objects with no schema",
            icon: "schema",
            home: true,
            secondary: phrase,
            errors: 0,
            sort: name.toLowerCase(),
          }),
          schemaFolders(key, s),
        );
      });
    const children: TreeNode[] = [...schemaNodes];
    if (mappings.length) children.push(folderNode(`${db.id}/mapping`, "databases", kindFolder("mapping")!, mappings));
    let tables = 0;
    let views = 0;
    let sequences = 0;
    for (const s of schemas.values()) {
      tables += s.tables.length;
      views += s.views.length;
      sequences += s.sequences.length;
    }
    tableTotal += tables;
    const label = labelOf(db);
    const dialect = [info?.dialect, info?.version].filter(Boolean).join(" ");
    const phrase = [
      countOf(tables, "table"),
      views ? countOf(views, "view") : "",
      sequences ? countOf(sequences, "sequence") : "",
      mappings.length ? countOf(mappings.length, "mapping") : "",
      loaded ? plural(new Set([...ownerTable.keys()]).size, "entity mapped", "entities mapped") : "",
    ]
      .filter(Boolean)
      .join(" · ");
    place.set(db.id, db.id);
    const node = add({
      key: db.id,
      type: "database",
      explorer: "databases",
      label,
      secondary: dialect ? `${dialect} · ${phrase}` : phrase,
      tooltip: `Database: ${label}${dialect ? ` (${dialect})` : ""}`,
      icon: "database",
      id: db.id,
      kind: "database",
      home: true,
      errors: errorsOf(db.id),
      sort: label.toLowerCase(),
    });
    if (!loaded) node.pending = true;
    if (loaded?.stale) node.stale = true;
    if (loaded && !tables && !views && !sequences && !mappings.length) {
      const hint: TreeNode = {
        key: `${db.id}/empty`,
        type: "item",
        explorer: "databases",
        label: EMPTY_DATABASE_HINT,
        tooltip: EMPTY_DATABASE_HINT,
        icon: "info",
        home: false,
        errors: 0,
        sort: "",
        children: [],
      };
      children.push(add(hint));
    }
    return attach(node, children);
  });
  databaseNodes.sort(byLabel);
  if (loose.length) {
    const s = { tables: [] as TreeNode[], views: [] as TreeNode[], sequences: [] as TreeNode[] };
    const mappings: TreeNode[] = [];
    for (const r of loose) {
      const node = elementNode(r, "databases");
      (r.kind === "table" ? s.tables : r.kind === "view" ? s.views : r.kind === "sequence" ? s.sequences : mappings).push(node);
    }
    const children = schemaFolders(NOT_IN_DATABASE, s);
    if (mappings.length) children.push(folderNode(`${NOT_IN_DATABASE}/mapping`, "databases", kindFolder("mapping")!, mappings));
    tableTotal += s.tables.length;
    databaseNodes.push(
      attach(
        add({
          key: NOT_IN_DATABASE,
          type: "group",
          explorer: "databases",
          label: GROUP_LABELS.notInDatabase,
          tooltip: "Physical elements whose database is not known",
          icon: "database",
          home: true,
          count: loose.length,
          secondary: fmt(loose.length),
          errors: 0,
          sort: "￿",
        }),
        children,
      ),
    );
  }
  headers.databases = `${countOf(databases.length, "database")} · ${countOf(tableTotal, "table")}`;
  rootNode("databases", databaseNodes);

  // ------------------------------------------------------------------ Diagrams tree

  const diagramGroups = new Map<string, TreeNode[]>();
  const looseDiagrams: TreeNode[] = [];
  for (const d of diagrams) {
    const node = elementNode(d, "diagrams");
    if (isPackage(d.package)) push(diagramGroups, d.package!, node);
    else looseDiagrams.push(node);
  }
  const diagramChildren: TreeNode[] = [];
  for (const [pkg, list] of diagramGroups) {
    const label = nameOf(pkg) ?? pkg;
    const node = folderNode(`${ROOTS.diagrams}/d:${pkg}`, "diagrams", kindFolder("diagram")!, list, label, `Diagrams whose home is ${label}`);
    node.type = "group";
    node.icon = "domain";
    node.id = pkg;
    node.sort = label.toLowerCase();
    node.secondary = fmt(list.length);
    diagramChildren.push(node);
  }
  diagramChildren.sort(byLabel).push(...looseDiagrams.sort(byLabel));
  headers.diagrams = plural(diagrams.length, "diagram", "diagrams");
  rootNode("diagrams", diagramChildren);

  // ------------------------------------------------------------------ Processes tree (phase-3-design.md 6.1)

  // Processes grouped by domain, nested as in the Domain model (domains without processes are hidden), then Actors. A
  // process row holds its Scenarios (from the index); its States and Events come with its document (documentChildren).
  const scenariosOf = new Map<string, Row[]>();
  for (const r of scenarioRows) push(scenariosOf, r.process ?? "", r);
  const processKey = (id: string) => `${ROOTS.processes}/p:${id}`;
  const processGroups = new Map<string, { node: TreeNode; members: TreeNode[]; total: number }>();
  const processTop: TreeNode[] = [];
  const processGroup = (pkg: string): { node: TreeNode; members: TreeNode[]; total: number } => {
    let group = processGroups.get(pkg);
    if (group) return group;
    const label = nameOf(pkg) ?? pkg;
    const node = newNode(`${ROOTS.processes}/d:${pkg}`, "group", "processes", label, "domain", label.toLowerCase());
    node.id = pkg;
    node.kind = "process";
    add(node);
    group = { node, members: [], total: 0 };
    processGroups.set(pkg, group);
    const up = byId.get(pkg)?.package;
    if (isPackage(up) && up !== pkg) processGroup(up!).members.push(node);
    else processTop.push(node);
    return group;
  };
  const statusOf = input.scenarioStatus;
  for (const r of processRows) {
    const node = add(newElementNode(byId, r, "processes", processKey(r.id), errorsOf(r.id)));
    node.load = "document";
    const scenarios = (scenariosOf.get(r.id) ?? []).map((sc) => {
      const row = elementNode(sc, "processes");
      row.secondary = `${plural(sc.stepCount ?? 0, "step", "steps")} · ${scenarioStatusText(statusOf?.get(sc.id))}`;
      return row;
    });
    const passed = scenarios.filter((sc) => statusOf?.get(sc.id!)?.passed).length;
    const folder = folderNode(`${node.key}/scenarios`, "processes", kindFolder("scenario")!, scenarios, PROCESS_LABELS.scenarios);
    if (passed) folder.secondary = PROCESS_LABELS.passedCount(passed);
    attach(node, [folder]);
    if (isPackage(r.package)) {
      processGroup(r.package!).members.push(node);
      for (let p: string | null | undefined = r.package, guard = 0; isPackage(p) && guard < 64; p = byId.get(p!)?.package, guard++) processGroup(p!).total++;
    } else processTop.push(node);
  }
  for (const { node, members, total } of processGroups.values()) {
    attach(node, members.sort(byLabel));
    node.count = members.length;
    node.secondary = plural(total, "process", "processes");
  }
  processTop.sort(byLabel);
  if (actorRows.length) {
    const actorNodes = actorRows.map((r) => add(newElementNode(byId, r, "processes", `${ROOTS.processes}/a:${r.id}`, errorsOf(r.id))));
    const folder = folderNode(`${ROOTS.processes}/actors`, "processes", kindFolder("actor")!, actorNodes, PROCESS_LABELS.actors, kindFolder("actor")!.tooltip);
    folder.home = false;
    folder.secondary = fmt(actorNodes.length);
    folder.sort = "\uffff";
    processTop.push(folder);
  }
  headers.processes = [countOf(processRows.length, "process"), countOf(actorRows.length, "actor"), countOf(scenarioRows.length, "scenario")].join(" · ");
  rootNode("processes", processTop);

  // ------------------------------------------------------------------ error roll-up, vocabularies

  if (input.errors?.size) for (const explorer of EXPLORERS) rollErrors(nodes, ROOTS[explorer]);

  const vocabularyChains = new Map<string, VocabularyChain>();
  const ownVocabularies = new Map<string, VocabularyRef[]>();
  const global: VocabularyChain = { tags: [], categories: [] };
  for (const v of vocabularies) {
    const kind = v.kind as VocabularyRef["kind"];
    if (isPackage(v.package)) push(ownVocabularies, v.package!, { id: v.id, kind, scope: v.package });
    else (kind === "tag-vocabulary" ? global.tags : global.categories).push({ id: v.id, kind, scope: null });
  }
  vocabularyChains.set("", global);
  for (const p of domainOrder) {
    const above = vocabularyChains.get(parent.get(p.id) && isPackage(parent.get(p.id)) ? parent.get(p.id)! : "") ?? global;
    const own = ownVocabularies.get(p.id) ?? [];
    vocabularyChains.set(p.id, {
      tags: [...own.filter((v) => v.kind === "tag-vocabulary"), ...above.tags],
      categories: [...own.filter((v) => v.kind === "category-tree"), ...above.categories],
    });
  }

  const forest: Forest = {
    tag: input.tag ?? null,
    byId,
    nodes,
    parent,
    place,
    roots: { ...ROOTS },
    headers,
    related,
    vocabularies: vocabularyChains,
    unplaced,
    pending,
    patch: {
      lists,
      rollup,
      totals: { packages: packages.length, entities: entityTotal },
      projectKinds: new Set(folderSpecs.map((f) => f.kind)),
      errors: input.errors,
    },
  };
  place.open = (folder) => void childKeys(forest, folder);
  return forest;
}

/** A node with every member present, so all nodes share one shape. */
function newNode(key: string, type: NodeType, explorer: ExplorerId, label: string, icon: string, sort: string): TreeNode {
  return {
    key,
    type,
    explorer,
    label,
    secondary: undefined,
    tooltip: undefined,
    icon,
    id: undefined,
    kind: undefined,
    home: true,
    target: undefined,
    count: undefined,
    errors: 0,
    warning: undefined,
    pending: undefined,
    stale: undefined,
    markers: undefined,
    load: undefined,
    table: undefined,
    children: undefined,
    sort,
    parent: undefined,
  };
}

/** The elements an element's index row names: relation ends, a mapping's or table's entity and database, a seed's target, a base entity. */
export function usesOf(forest: Forest, id: string): string[] {
  const r = forest.byId.get(id);
  if (!r) return [];
  const out: string[] = [];
  const add = (to: string | null | undefined) => {
    if (to && !out.includes(to)) out.push(to);
  };
  for (const e of r.ends ?? []) add(e.entity);
  add(r.entity);
  add(r.database);
  add(r.target);
  add(r.base);
  return out;
}

const usedByIndex = new WeakMap<Forest, Map<string, string[]>>();

/** The reverse of `usesOf`, indexed on first use (a selection, not the first paint, pays for it). */
export function usedByOf(forest: Forest, id: string): string[] {
  let index = usedByIndex.get(forest);
  if (!index) {
    index = new Map();
    for (const r of forest.byId.keys()) for (const to of usesOf(forest, r)) push(index, to, r);
    usedByIndex.set(forest, index);
  }
  return index.get(id) ?? [];
}

function countPhrase(counts: Record<string, number>): string {
  const parts: string[] = [];
  if (counts.package) parts.push(countOf(counts.package, "package"));
  if (counts.entity) parts.push(countOf(counts.entity, "entity"));
  else {
    const first = KIND_FOLDERS.find((f) => f.kind !== "package" && counts[f.kind]);
    if (first) parts.push(countOf(counts[first.kind], first.kind));
  }
  return parts.join(" · ") || "empty";
}

function rollErrors(nodes: Map<string, TreeNode>, root: string) {
  // Iterative post-order over the built children (on-demand element children are navigation rows and are not counted).
  const order: TreeNode[] = [];
  const stack = [root];
  while (stack.length) {
    const node = nodes.get(stack.pop()!)!;
    order.push(node);
    if (node.children) for (const c of node.children) stack.push(c);
  }
  for (let i = order.length - 1; i >= 0; i--) {
    const node = order[i];
    // A folder whose rows are not built yet counted its errors when it was made.
    if (!CONTAINERS.has(node.type) || !node.children) continue;
    let sum = node.type === "domain" || node.type === "database" ? node.errors : 0;
    for (const c of node.children ?? []) sum += nodes.get(c)!.errors;
    node.errors = sum;
  }
}

// ------------------------------------------------------------------ element children on demand

/** A node by key; an element's own row not built yet (its folder is unopened) is built on the way. */
export function nodeOf(forest: Forest, key: string): TreeNode | undefined {
  const node = forest.nodes.get(key);
  if (node || !forest.byId.has(key)) return node;
  forest.place.get(key);
  return forest.nodes.get(key);
}

/** A node's child keys; an element row's index-answered children are built on first ask. */
export function childKeys(forest: Forest, key: string): readonly string[] {
  const node = nodeOf(forest, key);
  if (!node) return [];
  if (node.children) return node.children;
  const build = forest.pending.get(key);
  forest.pending.delete(key);
  const children = build ? build() : elementChildren(forest, node);
  for (const c of children) c.parent = key;
  node.children = children.map((c) => c.key);
  return node.children;
}

/**
 * Builds the folders still pending, a slice at a time, while `more()` says there is time (idle callbacks): the first
 * search or expand of a large folder then finds its rows built. Returns whether any remain.
 */
export function prebuild(forest: Forest, more: () => boolean): boolean {
  for (const key of forest.pending.keys()) {
    if (!more()) return true;
    childKeys(forest, key);
  }
  return forest.pending.size > 0;
}

function navigation(forest: Forest, holder: string, id: string, secondary?: string, key = `${holder}/${id}`): TreeNode {
  const r = forest.byId.get(id);
  const f = kindFolder(r?.kind ?? "") ?? OTHER_FOLDER;
  const label = r ? r.displayName || r.name || id : id;
  const node: TreeNode = {
    key,
    type: "element",
    explorer: "domain-model",
    label,
    secondary,
    icon: f.icon,
    id,
    kind: r?.kind,
    home: false,
    target: forest.place.get(id),
    errors: 0,
    sort: label.toLowerCase(),
    children: [],
  };
  forest.nodes.set(node.key, node);
  forest.parent.set(node.key, holder);
  return node;
}

function navFolder(forest: Forest, holder: TreeNode, name: string, label: string, icon: string, members: TreeNode[], secondary?: string): TreeNode {
  const key = `${holder.key}/${name}`;
  const node: TreeNode = {
    key,
    type: "folder",
    explorer: holder.explorer,
    label,
    icon,
    home: false,
    count: members.length,
    secondary,
    errors: 0,
    sort: "",
    children: members.map((m) => m.key),
  };
  forest.nodes.set(key, node);
  forest.parent.set(key, holder.key);
  for (const m of members) forest.parent.set(m.key, key);
  return node;
}

function elementChildren(forest: Forest, node: TreeNode): TreeNode[] {
  const id = node.id;
  const r = id ? forest.byId.get(id) : undefined;
  if (!r || !node.home) return [];
  const out: TreeNode[] = [];
  const { related } = forest;
  if (r.kind === "entity") {
    const relations = related.relationsOf.get(r.id) ?? [];
    if (relations.length) {
      const holderKey = `${node.key}/relations`;
      const members = relations.map((rel) => {
        const relation = forest.byId.get(rel);
        const ends = (related.endsOf.get(rel) ?? []).map((e) => forest.byId.get(e)?.name ?? "?").join(" → ");
        const away = relation && relation.package !== r.package ? domainPath(forest, relation.package) : "";
        return navigation(forest, holderKey, rel, away ? `${ends} · ${away}` : ends);
      });
      members.sort(byLabel);
      out.push(navFolder(forest, node, "relations", kindFolder("relation")!.label, "relationship", members));
    }
    const seeds = related.seedsOf.get(r.id) ?? [];
    if (seeds.length) {
      const rowsTotal = seeds.reduce((n, s) => n + (forest.byId.get(s)?.rowCount ?? 0), 0);
      const members = seeds.map((s) => navigation(forest, `${node.key}/seeds`, s)).sort(byLabel);
      out.push(
        navFolder(
          forest,
          node,
          "seeds",
          kindFolder("seed")!.label,
          "seed",
          members,
          `${plural(seeds.length, "seed", "seeds")} · ${plural(rowsTotal, "row", "rows")}`,
        ),
      );
    }
    const mappings = mappingRows(forest, node, r.id);
    if (mappings.length) out.push(navFolder(forest, node, "mappings", GROUP_LABELS.mappings, "mapping", mappings));
  } else if (r.kind === "relation") {
    const ends = r.ends ?? [];
    if (ends.length) {
      // Keyed by position: both ends of a self relation name one entity.
      const members = ends.map((e, i) => navigation(forest, `${node.key}/ends`, e.entity, e.role || undefined, `${node.key}/ends/${i}`));
      out.push(navFolder(forest, node, "ends", GROUP_LABELS.ends, "relationship", members));
    }
  }
  return out;
}

/** An entity's Mappings child: one row per database it is mapped into ("main → invoices"), customised when a mapping file exists. */
function mappingRows(forest: Forest, node: TreeNode, entity: string): TreeNode[] {
  const holderKey = `${node.key}/mappings`;
  const customised = new Set((forest.related.mappingsOf.get(entity) ?? []).map((m) => forest.byId.get(m)?.database).filter(Boolean));
  const out: TreeNode[] = [];
  const seen = new Set<string>();
  for (const tableKey of forest.related.tablesOf.get(entity) ?? []) {
    const table = forest.nodes.get(tableKey);
    if (!table?.table || table.table.isJunction) continue;
    const db = tableKey.slice(0, tableKey.indexOf("/"));
    seen.add(db);
    const label = `${forest.byId.get(db)?.name ?? db} → ${table.label}`;
    const row: TreeNode = {
      key: `${holderKey}/${tableKey}`,
      type: "item",
      explorer: "domain-model",
      label,
      icon: "table",
      home: false,
      target: tableKey,
      errors: 0,
      sort: label.toLowerCase(),
      children: [],
    };
    if (customised.has(db)) row.markers = ["customised"];
    forest.nodes.set(row.key, row);
    forest.parent.set(row.key, holderKey);
    out.push(row);
  }
  for (const m of forest.related.mappingsOf.get(entity) ?? []) {
    const db = forest.byId.get(m)?.database;
    if (db && seen.has(db)) continue;
    const row = navigation(forest, holderKey, m);
    row.label = `${db ? (forest.byId.get(db)?.name ?? db) : "?"} → ${forest.byId.get(m)?.name ?? m}`;
    row.markers = ["customised"];
    out.push(row);
  }
  return out.sort(byLabel);
}

/**
 * Per row node, the index hash of the element when its document children were added. A rebuilt forest makes new
 * nodes, and a patch replaces the node of a row it changes (and drops its children), so a node missing here, or
 * marked at another hash, has lost its document children or shows an older document's.
 */
const documentHashes = new WeakMap<TreeNode, string>();

/** Whether the row `key` takes children from its element's document and they are missing or out of date. */
export function needsDocument(forest: Forest, key: string): boolean {
  const node = nodeOf(forest, key);
  if (!node || node.load !== "document" || !node.id) return false;
  return documentHashes.get(node) !== (forest.byId.get(node.id)?.hash ?? "");
}

/**
 * The expanded rows among `rows` whose document children must be read (again): after a rebuild (a diagram added for a
 * chart, a process change) an expanded process keeps its States and Events, an entity its Attributes, an enum its
 * Members.
 */
export function rowsNeedingDocuments(forest: Forest, rows: readonly { key: string }[], open: ReadonlySet<string>): string[] {
  return rows.filter((r) => open.has(r.key) && needsDocument(forest, r.key)).map((r) => r.key);
}

/** Children that need the element's document: Attributes (entity, value object, relation) or Members (enum), first. */
export function documentChildren(forest: Forest, key: string, doc: ElementDocument, boundAttribute?: string): readonly string[] {
  const node = nodeOf(forest, key);
  if (!node) return [];
  if (node.id) documentHashes.set(node, forest.byId.get(node.id)?.hash ?? "");
  if (node.kind === "process") return processChildren(forest, node, doc, boundAttribute);
  const rest = childKeys(forest, key).filter((k) => k !== `${key}/attributes` && k !== `${key}/members`);
  const json = doc.json as Record<string, unknown>;
  const nameOf = (id: string) => forest.byId.get(id)?.name;
  let added: TreeNode | undefined;
  if (node.kind === "enum") {
    const members = (Array.isArray(json.members) ? json.members : []) as { id?: string; name?: string }[];
    const items = members.map((m, i) => item(forest, `${key}/members`, m.id ?? String(i), m.name ?? "", undefined));
    added = navFolder(forest, node, "members", GROUP_LABELS.members, "enum", items);
  } else {
    const attributes = attributesOf(doc.json as never) as AttributeDoc[];
    if (attributes.length || node.kind !== "relation") {
      const items = attributes.map((a) => item(forest, `${key}/attributes`, String((a as { id?: string }).id ?? a.name), a.name, typeLabel(a, nameOf)));
      added = navFolder(forest, node, "attributes", GROUP_LABELS.attributes, "attribute", items);
    }
  }
  node.children = added ? [added.key, ...rest] : [...rest];
  return node.children;
}

type StateDoc = { id?: string; name?: string; type?: string; states?: StateDoc[] };

/** A process row's States (nested) and Events folders from its document, before its Scenarios folder (6.1). */
function processChildren(forest: Forest, node: TreeNode, doc: ElementDocument, attribute: string | undefined): readonly string[] {
  const key = node.key;
  const json = doc.json as { states?: StateDoc[]; events?: { id?: string; name?: string; actors?: string[] }[]; boundAttribute?: string; subject?: string };
  const rest = childKeys(forest, key).filter((k) => k !== `${key}/states` && k !== `${key}/events`);
  const stateItems = (holder: string, list: StateDoc[] | undefined): TreeNode[] =>
    (list ?? []).map((st, i) => {
      const row = item(forest, holder, st.id ?? String(i), st.name ?? "", st.type && st.type !== "atomic" ? st.type : undefined);
      row.icon = "state";
      row.kind = "state";
      row.explorer = node.explorer;
      if (st.states?.length) {
        const children = stateItems(row.key, st.states);
        row.children = children.map((c) => c.key);
        row.count = children.length;
      }
      return row;
    });
  let total = 0;
  const countAll = (list: StateDoc[] | undefined) => (list ?? []).forEach((st) => (total++, countAll(st.states)));
  countAll(json.states);
  const states = navFolder(forest, node, "states", PROCESS_LABELS.states, "state", stateItems(`${key}/states`, json.states), fmt(total));
  const events = (json.events ?? []).map((e, i) => {
    const row = item(forest, `${key}/events`, e.id ?? String(i), e.name ?? "", undefined);
    row.icon = "event";
    row.kind = "event";
    row.explorer = node.explorer;
    return row;
  });
  const eventFolder = navFolder(forest, node, "events", PROCESS_LABELS.events, "event", events.sort(byLabel), fmt(events.length));
  // The bound attribute's name is in the subject's document (the caller reads it); the row then says Subject.attribute.
  const row = node.id ? forest.byId.get(node.id) : undefined;
  if (row && json.subject && attribute) {
    const subject = forest.byId.get(json.subject);
    node.secondary = processSecondary(row, subject ? subject.displayName || subject.name : undefined, attribute);
  }
  node.children = [states.key, eventFolder.key, ...rest];
  return node.children;
}

/** A table's children from its detail (E5f, or `/view`): Columns, Primary key, Foreign keys, Unique constraints, Indexes. */
export function tableChildren(forest: Forest, key: string, view: TableView): readonly string[] {
  const node = nodeOf(forest, key);
  if (!node) return [];
  const folders: TreeNode[] = [];
  const group = (name: string, label: string, icon: string, entries: { id: string; label: string; secondary?: string; attribute?: string | null }[]) => {
    if (!entries.length) return;
    folders.push(
      navFolder(
        forest,
        node,
        name,
        label,
        icon,
        entries.map((e) => {
          const row = item(forest, `${key}/${name}`, e.id, e.label, e.secondary);
          if (e.attribute !== undefined) row.attribute = e.attribute;
          return row;
        }),
      ),
    );
  };
  group(
    "columns",
    GROUP_LABELS.columns,
    "column",
    view.columns.map((c) => ({
      id: c.key,
      label: c.name,
      secondary: `${c.nativeType}${c.nullable ? "" : " not null"}${c.isPrimaryKey ? " · PK" : ""}${c.isForeignKey ? " · FK" : ""}`,
      attribute: c.attributeId ?? null,
    })),
  );
  if (view.primaryKey)
    group("primary-key", GROUP_LABELS.primaryKey, "key", [
      { id: view.primaryKey.name, label: view.primaryKey.name, secondary: view.primaryKey.columns.join(", ") },
    ]);
  group(
    "foreign-keys",
    GROUP_LABELS.foreignKeys,
    "key",
    view.foreignKeys.map((k) => ({ id: k.name, label: k.name, secondary: `${k.columns.join(", ")} → ${k.referencedTable}` })),
  );
  group(
    "uniques",
    GROUP_LABELS.uniques,
    "key",
    view.uniques.map((k) => ({ id: k.name, label: k.name, secondary: k.columns.join(", ") })),
  );
  group(
    "indexes",
    GROUP_LABELS.indexes,
    "index",
    view.indexes.map((k) => ({ id: k.name, label: k.name, secondary: k.columns.map((c) => c.column).join(", ") })),
  );
  node.children = folders.map((f) => f.key);
  return node.children;
}

function item(forest: Forest, holder: string, id: string, label: string, secondary: string | undefined): TreeNode {
  const node: TreeNode = {
    key: `${holder}/${id}`,
    type: "item",
    explorer: forest.nodes.get(holder)?.explorer ?? "domain-model",
    label,
    secondary,
    icon: "item",
    id,
    home: false,
    errors: 0,
    sort: label.toLowerCase(),
    children: [],
  };
  forest.nodes.set(node.key, node);
  forest.parent.set(node.key, holder);
  return node;
}

// ------------------------------------------------------------------ paths, reveal, related, vocabularies

/** The keys from the explorer's root down to `key`. */
export function pathOf(forest: Forest, key: string): string[] {
  const out: string[] = [];
  for (let k: string | undefined = key, guard = 0; k !== undefined && guard < 256; k = forest.parent.get(k), guard++) out.push(k);
  return out.reverse();
}

/** The keys to expand to reveal an element's own row (its ancestors, root first). */
export function revealPath(forest: Forest, id: string): string[] {
  const key = forest.place.get(id);
  return key ? pathOf(forest, key).slice(0, -1) : [];
}

/** "Domain model › Billing › Entities › Invoice". */
export function breadcrumb(forest: Forest, key: string): string {
  return pathOf(forest, key)
    .map((k) => forest.nodes.get(k)?.label ?? k)
    .join(" › ");
}

/** "Sales › Orders": a domain's path, for rows shown away from their domain. */
export function domainPath(forest: Forest, domain: string | null | undefined): string {
  const names: string[] = [];
  for (let d = domain, guard = 0; d && guard < 64; guard++) {
    const r = forest.byId.get(d);
    if (!r || r.kind !== "package") break;
    names.push(r.name);
    d = r.package;
  }
  return names.reverse().join(" › ");
}

/** The table row above a row (a column, a key), if any. */
export function tableOf(forest: Forest, key: string): string | undefined {
  for (let k: string | undefined = key, guard = 0; k !== undefined && guard < 16; k = forest.parent.get(k), guard++)
    if (forest.nodes.get(k)?.type === "table") return k;
  return undefined;
}

/** The database row above a row (a table, a column), if any: its id is the database's. */
export function databaseOf(forest: Forest, key: string): string | undefined {
  for (let k: string | undefined = key, guard = 0; k !== undefined && guard < 32; k = forest.parent.get(k), guard++) {
    const n = forest.nodes.get(k);
    if (n?.type === "database") return n.id;
  }
  return undefined;
}

/** The schema row above a row (a kind folder, a table), if any: the schema's name (its key is `<database>/s:<name>`). */
export function schemaNameOf(forest: Forest, key: string): string | null {
  for (let k: string | undefined = key, guard = 0; k !== undefined && guard < 32; k = forest.parent.get(k), guard++) {
    const n = forest.nodes.get(k);
    if (n?.type === "schema") {
      const at = k.indexOf("/s:");
      return at >= 0 && k.length > at + 3 ? k.slice(at + 3) : null;
    }
    if (n?.type === "database") return null;
  }
  return null;
}

/** The rows related to a selected row (section 1.9), as the keys of their own rows. */
export function relatedKeys(forest: Forest, key: string): Set<string> {
  const node = nodeOf(forest, key);
  const out = new Set<string>();
  if (!node) return out;
  const { related, place } = forest;
  const addId = (id: string | undefined) => {
    const k = id ? place.get(id) : undefined;
    if (k && k !== key) out.add(k);
  };
  const addKey = (k: string | undefined) => {
    if (k && k !== key) out.add(k);
  };
  if (node.type === "table") {
    addId(related.mappedBy.get(key));
    return out;
  }
  if (node.type === "item" && node.attribute !== undefined) {
    // A column (1.3, "mapped by"): the entity or relation mapped onto its table, and the attribute's row under it.
    // The attribute row exists once the entity's attributes are loaded; its key is added either way, so it is tinted
    // when it appears, and highlighting never expands a row.
    const table = tableOf(forest, key);
    const owner = table ? related.mappedBy.get(table) : undefined;
    addId(owner);
    const ownerKey = owner ? place.get(owner) : undefined;
    if (ownerKey && node.attribute) out.add(`${ownerKey}/attributes/${node.attribute}`);
    return out;
  }
  const id = node.target ? forest.nodes.get(node.target)?.id : node.id;
  const r = id ? forest.byId.get(id) : undefined;
  if (!r) return out;
  if (r.kind === "entity") {
    for (const rel of related.relationsOf.get(r.id) ?? []) {
      addId(rel);
      for (const e of related.endsOf.get(rel) ?? []) if (e !== r.id) addId(e);
    }
    addId(related.baseOf.get(r.id));
    for (const d of related.derivedOf.get(r.id) ?? []) addId(d);
    for (const t of related.tablesOf.get(r.id) ?? []) addKey(t);
    for (const s of related.seedsOf.get(r.id) ?? []) addId(s);
  } else if (r.kind === "relation") {
    for (const e of related.endsOf.get(r.id) ?? []) addId(e);
    addKey(related.junctionOf.get(r.id));
    for (const t of related.tablesOf.get(r.id) ?? []) addKey(t);
  } else if (r.kind === "enum") {
    for (const t of related.lookupOf.get(r.id) ?? []) addKey(t);
    for (const t of related.tablesOf.get(r.id) ?? []) addKey(t);
  } else if (r.kind === "table") {
    addId(r.entity ?? undefined);
  }
  return out;
}

/** The "n related" badge counts: for every ancestor of a related row, how many related rows it holds. */
export function relatedCounts(forest: Forest, related: ReadonlySet<string>): Map<string, number> {
  const out = new Map<string, number>();
  for (const key of related)
    for (let p = forest.parent.get(key), guard = 0; p !== undefined && guard < 256; p = forest.parent.get(p), guard++) out.set(p, (out.get(p) ?? 0) + 1);
  return out;
}

/**
 * Membership dots (3.5) on the rows above the members of the active diagram: for every ancestor of a member's row,
 * how many members it holds, so a collapsed domain or folder says the canvas shows something inside it.
 */
export function canvasCounts(forest: Forest, members: ReadonlySet<string>): Map<string, number> {
  const out = new Map<string, number>();
  for (const id of members) {
    const key = forest.place.get(id);
    if (!key) continue;
    for (let p = forest.parent.get(key), guard = 0; p !== undefined && guard < 256; p = forest.parent.get(p), guard++) out.set(p, (out.get(p) ?? 0) + 1);
  }
  return out;
}

/**
 * The "n people here" roll-up (section 1.10): for every ancestor of a row other people have selected, how many
 * distinct people hold rows under it. `people` maps an element id to the people who have it selected.
 */
export function presenceCounts(forest: Forest, people: ReadonlyMap<string, readonly string[]>): Map<string, number> {
  const sets = new Map<string, Set<string>>();
  for (const [id, users] of people) {
    const key = forest.place.get(id);
    if (!key) continue;
    for (let p = forest.parent.get(key), guard = 0; p !== undefined && guard < 256; p = forest.parent.get(p), guard++) {
      let set = sets.get(p);
      if (!set) sets.set(p, (set = new Set()));
      for (const u of users) set.add(u);
    }
  }
  const out = new Map<string, number>();
  for (const [key, set] of sets) out.set(key, set.size);
  return out;
}

/** The vocabularies an element sees (section 1.11): its domain's, each enclosing domain's, then the global ones. */
export function vocabularyChain(forest: Forest, elementId: string | null): VocabularyChain {
  const r = elementId ? forest.byId.get(elementId) : undefined;
  const domain = r ? (r.kind === "package" ? r.id : r.package) : null;
  return forest.vocabularies.get(domain ?? "") ?? forest.vocabularies.get("")!;
}

// ------------------------------------------------------------------ visible rows

export interface VisibleRow {
  key: string;
  depth: number;
}

/** The depth-first walk over the expanded rows of one explorer (the root itself is not a row). */
export function visibleRows(forest: Forest, explorer: ExplorerId, expanded: ReadonlySet<string>): VisibleRow[] {
  const out: VisibleRow[] = [];
  appendSubtree(forest, forest.roots[explorer], 0, expanded, out);
  return out;
}

function appendSubtree(forest: Forest, key: string, depth: number, expanded: ReadonlySet<string>, out: VisibleRow[]) {
  const stack: VisibleRow[] = [];
  const top = childKeys(forest, key);
  for (let i = top.length - 1; i >= 0; i--) stack.push({ key: top[i], depth });
  while (stack.length) {
    const row = stack.pop()!;
    out.push(row);
    if (!expanded.has(row.key)) continue;
    const children = childKeys(forest, row.key);
    for (let i = children.length - 1; i >= 0; i--) stack.push({ key: children[i], depth: row.depth + 1 });
  }
}

/** Expands the row at `index` in place: splices its visible subtree after it. `expanded` must already hold its key. */
export function expandAt(forest: Forest, rows: VisibleRow[], index: number, expanded: ReadonlySet<string>): VisibleRow[] {
  const row = rows[index];
  const inserted: VisibleRow[] = [];
  appendSubtree(forest, row.key, row.depth + 1, expanded, inserted);
  rows.splice(index + 1, 0, ...inserted);
  return rows;
}

/** Collapses the row at `index` in place: removes the rows below it that are deeper. */
export function collapseAt(rows: VisibleRow[], index: number): VisibleRow[] {
  const depth = rows[index].depth;
  let end = index + 1;
  while (end < rows.length && rows[end].depth > depth) end++;
  rows.splice(index + 1, end - index - 1);
  return rows;
}

/**
 * Filter mode (sections 3.1 and 4.3): the walk restricted to the rows of `ids` in this explorer and their ancestors,
 * every ancestor expanded; a matching domain, database or schema shows collapsed with its whole subtree. `ids` are
 * element ids, or row keys (table summaries). Returns the rows, the ancestor keys (the caller's expanded set) and,
 * per shown container, how many of its direct children are shown ("3 of 41").
 */
export function filteredRows(
  forest: Forest,
  explorer: ExplorerId,
  ids: Iterable<string>,
): { rows: VisibleRow[]; open: Set<string>; shown: Map<string, number> } {
  const keep = new Set<string>();
  const open = new Set<string>();
  const shown = new Map<string, number>();
  const root = forest.roots[explorer];
  for (const id of ids) {
    let key = forest.place.get(id);
    if (key === undefined && forest.nodes.has(id)) key = id;
    if (!key || forest.nodes.get(key)?.explorer !== explorer || keep.has(key)) continue;
    keep.add(key);
    for (let p = forest.parent.get(key), guard = 0; p !== undefined && p !== root && guard < 256; p = forest.parent.get(p), guard++) {
      if (open.has(p)) break;
      open.add(p);
      keep.add(p);
    }
  }
  const rows: VisibleRow[] = [];
  const walk = (key: string, depth: number) => {
    let n = 0;
    for (const child of childKeys(forest, key)) {
      if (!keep.has(child)) continue;
      n++;
      rows.push({ key: child, depth });
      if (open.has(child)) walk(child, depth + 1);
    }
    shown.set(key, n);
  };
  walk(root, 0);
  return { rows, open, shown };
}

/** A row's position among its siblings (aria-posinset and aria-setsize), cached per children array. */
const positions = new WeakMap<readonly string[], Map<string, number>>();
export function positionOf(forest: Forest, key: string): { pos: number; size: number } {
  const parent = forest.parent.get(key);
  const siblings = parent === undefined ? [key] : childKeys(forest, parent);
  let map = positions.get(siblings);
  if (!map) {
    map = new Map(siblings.map((k, i) => [k, i]));
    positions.set(siblings, map);
  }
  return { pos: (map.get(key) ?? 0) + 1, size: siblings.length };
}

/** Whether a row can be expanded: containers, and element rows with children (built, index-answered, or loadable). */
export function isExpandable(forest: Forest, key: string): boolean {
  const node = nodeOf(forest, key);
  if (!node) return false;
  if (CONTAINERS.has(node.type) || node.load !== undefined) return true;
  if (node.children) return node.children.length > 0;
  return node.home && !!node.kind && INDEX_CHILD_KINDS.has(node.kind);
}
