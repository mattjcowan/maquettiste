// The mock's storage run for relations with an end of several types (several-types-design.md section 19.5 decisions 39 to 49, 19.8): what
// the engine's CollectTypeStorage and FinishTypeStorage work out from a relation mapping and the team's tables, ported step for step: the
// targets and placements, the pairs of targets with their holding tables and pointers, each pointer resolved against the tables, the codes,
// what the database can't enforce and the targets no pointer reaches. It reports the run's findings (MQ4117, MQ4118, MQ4120 and MQ4051 for
// a constant) and fills the database view's `relationStorage`. Like the engine, the caller reports no finding of the run while validation
// has errors. The texts are the engine's, as the two parity fixtures record them.
import type { ColumnView, DatabaseView, Diagnostic, TableView } from "@/api/types";
import { docEndHasSeveralTypes, type EndDocLike } from "@/model/endTypes";
import { Coverage } from "./coverage";
import { severalEnds } from "./endCover";
import { storesRelation } from "./endRules";
import { parseDialect, quoteIdentifier, sqlLiteral, type QueryDialect } from "./querySql";
import type { ModelEntry } from "./validate";

type Json = Record<string, unknown>;
const arr = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);
const str = (v: unknown): string => (typeof v === "string" ? v : "");

export type RelationStorageKind =
  | "no-foreign-key"
  | "shared-table"
  | "id-only"
  | "type-check"
  | "count-check"
  | "one-to-one-unique"
  | "link-unique"
  | "per-type-duplicates"
  | "rows-not-told-apart";

export interface StorageGap {
  endId: string;
  kind: RelationStorageKind;
  text?: string;
}
export interface RelationStorageItem {
  relationId: string;
  relationName: string;
  mappingId: string | null;
  tables: string[];
  leftOut: { endId: string; entityId: string }[];
  notStored: { endId: string; entityId: string }[];
  gaps: StorageGap[];
}
export interface TypeStorageResult {
  storage: RelationStorageItem[];
  diagnostics: Diagnostic[];
}

const INTEGER_CODES = new Set(["int16", "int32", "int64"]);
const TEXTS = new Set(["string", "text"]);

/** Names as the engine's Names writes them: 'A', 'B'. */
const names = (list: string[]) => list.map((n) => `'${n}'`).join(", ");
const facets = (c: ColumnView) =>
  TEXTS.has(c.type) && c.length !== null
    ? `(${c.length})`
    : c.type === "decimal" && c.precision !== null
      ? `(${c.precision}${c.scale !== null ? `,${c.scale}` : ""})`
      : "";
const describe = (c: ColumnView) => `${c.type}${facets(c)}`;
const sameType = (a: ColumnView, b: ColumnView) =>
  a.type === b.type && (!TEXTS.has(a.type) || a.length === b.length) && (a.type !== "decimal" || (a.precision === b.precision && a.scale === b.scale));
const byOrdinal = (a: string, b: string) => (a < b ? -1 : a > b ? 1 : 0);

// ------------------------------------------------------------------ conventions: the names of the checks and uniques the gaps copy

function words(text: string): string[] {
  return text
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/([A-Z]+)([A-Z][a-z])/g, "$1 $2")
    .split(/[^A-Za-z0-9]+/)
    .filter(Boolean)
    .map((w) => w.toLowerCase());
}
function joinWords(list: string[], style: string): string {
  switch (style) {
    case "pascal":
      return list.map((x) => x[0].toUpperCase() + x.slice(1)).join("");
    case "camel":
      return list.map((x, i) => (i === 0 ? x : x[0].toUpperCase() + x.slice(1))).join("");
    case "kebab":
      return list.join("-");
    case "upper-snake":
      return list.join("_").toUpperCase();
    default:
      return list.join("_");
  }
}
/** A name pattern such as `ck_{table}_{name}` rendered in the column case, as DatabaseRun.Render does. */
function renderName(pattern: string, tokens: Record<string, string>, style: string): string {
  const list: string[] = [];
  let raw = "";
  let at = 0;
  while (at < pattern.length) {
    const open = pattern.indexOf("{", at);
    const close = open < 0 ? -1 : pattern.indexOf("}", open + 1);
    if (open < 0 || close < 0) {
      list.push(...words(pattern.slice(at)));
      raw += pattern.slice(at);
      break;
    }
    const literal = pattern.slice(at, open);
    list.push(...words(literal));
    raw += literal;
    const token = pattern.slice(open + 1, close);
    const text = token in tokens ? tokens[token] : `{${token}}`;
    list.push(...words(text));
    raw += text;
    at = close + 1;
  }
  return style === "preserve" ? raw : joinWords(list, style);
}

/** Why a constant's value doesn't fit its column (DatabaseRun.FitProblem), or null when it fits. */
function fitProblem(value: unknown, type: string, length: number | null, nullable: boolean): string | null {
  if (value === null || value === undefined) return nullable ? null : "is NULL, and the column is not nullable";
  if (typeof value === "boolean") return type === "bool" ? null : "is a boolean";
  if (typeof value === "number") {
    if (Number.isInteger(value)) {
      if (type === "int16" && (value < -32768 || value > 32767)) return "does not fit a 16-bit integer";
      if (type === "int32" && (value < -2147483648 || value > 2147483647)) return "does not fit a 32-bit integer";
      return ["int16", "int32", "int64", "decimal", "float", "double"].includes(type) ? null : "is a number";
    }
    return ["decimal", "float", "double"].includes(type) ? null : "is a number with a fraction";
  }
  if (typeof value === "string") {
    if (["int16", "int32", "int64", "decimal", "float", "double", "bool"].includes(type)) return "is a text";
    if (type === "string" && length !== null && value.length > length) return `is ${value.length} characters long, over the column's ${length}`;
  }
  return null;
}

// ------------------------------------------------------------------ the run's own shapes

interface Placement {
  id: string;
  entity: Json;
  table: TableView | null;
  base: Placement | null;
  strategy: "tph" | "tpt" | "tpc" | null;
  bound: boolean;
  viaBinding: boolean;
  binding?: Json;
}
interface Entry {
  index: number;
  json: Json;
}
interface End {
  doc: Json;
  index: number;
  entry: Entry | undefined;
  covered: Set<string>;
  targets: Placement[];
  leftOut: Json[];
  notStored: Placement[];
}
interface FkSpec {
  id: string;
  /** The key's own name, else its id (how a pointer's refusal names it). */
  specName: string;
  /** The name of the resolved key. */
  name: string;
  host: TableView;
  columns: string[];
  refTable: TableView | undefined;
  refColumns: string[];
}
interface Pointer {
  end: End;
  target: Placement;
  isHolder: boolean;
  fk?: FkSpec;
  idColumn?: string;
  typeColumn?: string;
  referencesColumn?: string;
}
interface Resolved {
  end: End;
  target: Placement;
  isHolder: boolean;
  fk?: FkSpec;
  columns: ColumnView[];
  referencedTable?: TableView;
  typeColumn?: ColumnView;
  codes: (string | number)[];
}
interface Pair {
  table: TableView;
  ends: [Pointer, Pointer];
  result?: [Resolved, Resolved];
}
interface Links {
  mapping: Json;
  linkTable: TableView | undefined;
  ends: [End, End];
  pairs: Pair[];
}

/** What the run reads of a database: its resolved view, the model's documents and the database's conventions. */
export function typeStorageRun(docs: ReadonlyMap<string, Json>, view: DatabaseView, conventions: Json = {}): TypeStorageResult {
  const empty: TypeStorageResult = { storage: [], diagnostics: [] };
  const all = [...docs.values()];
  const relations = all.filter((d) => d.kind === "relation" && arr(d.ends).length === 2);
  const mappingsOf = new Map<string, Json>();
  for (const m of all)
    if (m.kind === "mapping" && m.database === view.id && typeof m.relation === "string" && !mappingsOf.has(m.relation)) mappingsOf.set(m.relation, m);
  const severalRelations = relations.filter((r) => arr(r.ends).some((e) => docEndHasSeveralTypes(e as EndDocLike)));
  const withConstants = [...mappingsOf.values()].filter((m) => arr(m.constants).length > 0 && m.ignore !== true);
  if (!severalRelations.length && !withConstants.length) return empty;

  const dbId = view.id;
  const entries: ModelEntry[] = all.map((json) => ({ id: str(json.id), path: "", json }));
  const coverage = new Coverage(entries);
  const endCover = severalEnds(entries);
  const tableByKey = new Map(view.tables.map((t) => [t.key, t]));
  const out: Diagnostic[] = [];
  const diag = (rule: string, severity: Diagnostic["severity"], message: string, mapping: Json, pointer: string) => {
    if (!out.some((d) => d.rule === rule && d.elementId === mapping.id && d.jsonPointer === pointer && d.message === message))
      out.push({ rule, severity, message, elementId: str(mapping.id), filePath: null, jsonPointer: pointer, line: null, column: null });
  };

  // Columns and foreign keys by id, with their tables.
  const columnOf = new Map<string, { table: TableView; column: ColumnView }>();
  for (const t of view.tables) for (const c of t.columns) columnOf.set(c.key, { table: t, column: c });
  const fkOf = new Map<string, FkSpec>();
  const tableDocs = new Map(all.filter((d) => d.kind === "table" && d.database === dbId).map((d) => [str(d.id), d]));
  for (const d of all)
    if (d.kind === "table" && d.database === dbId && d.origin !== "synthesized") {
      const table = tableByKey.get(str(d.id));
      if (table)
        for (const fk of arr(d.foreignKeys))
          fkOf.set(str(fk.id), {
            id: str(fk.id),
            specName: str(fk.name) || str(fk.id),
            name: str(fk.name) || `fk_${table.name}`,
            host: table,
            columns: (fk.columns as string[] | undefined) ?? [],
            refTable: tableByKey.get(str(fk.referencesTable)),
            refColumns: (fk.referencesColumns as string[] | undefined) ?? [],
          });
    }
  const nameOfColumn = (key: string) => columnOf.get(key)?.column.name ?? key;

  const dialect: QueryDialect = parseDialect(view.dialect) ?? "postgresql";
  const quoting = String(view.quoting ?? "reserved");
  const Q = (name: string) => quoteIdentifier(name, dialect, quoting);
  const columnCase = String(conventions.columnCase ?? "snake");
  const render = (pattern: string, tokens: Record<string, string>) => renderName(pattern, tokens, columnCase);

  // ---------------------------------------------------------------- placements (the engine's ComputePlacements)
  const entityMapping = new Map(all.filter((d) => d.kind === "mapping" && d.database === dbId && typeof d.entity === "string").map((d) => [str(d.entity), d]));
  const chainOf = (id: string): string[] => {
    const doc = docs.get(id);
    return doc ? coverage.chain(doc) : [id];
  };
  const entityDocs = all
    .filter((d) => d.kind === "entity")
    .sort((a, b) => (str(a.name) < str(b.name) ? -1 : str(a.name) > str(b.name) ? 1 : byOrdinal(str(a.id), str(b.id))));
  const placements = new Map<string, Placement>();
  for (const entity of entityDocs) {
    const id = str(entity.id);
    const binding = arr(entity.bindings).find((b) => b.database === dbId);
    if (binding) {
      const write = binding.write as Json | string | undefined;
      if (write && typeof write === "object" && write.none === true) continue;
      const key = write && typeof write === "object" && typeof write.table === "string" ? write.table : str(binding.source);
      const table = tableByKey.get(key);
      if (table) placements.set(id, { id, entity, table, base: null, strategy: null, bound: true, viaBinding: true, binding });
      continue;
    }
    const mapping = entityMapping.get(id);
    if (mapping?.ignore === true) continue;
    const bound = mapping && typeof mapping.table === "string" ? tableByKey.get(mapping.table) : undefined;
    const table = bound ?? view.tables.find((t) => t.entityId === id && !t.relationId) ?? null;
    if (!table && entity.abstract !== true) continue;
    if (!table) continue;
    placements.set(id, { id, entity, table, base: null, strategy: null, bound: bound !== undefined, viaBinding: false });
  }
  const placementList = [...placements.values()];
  for (const p of placementList) {
    const ancestor = chainOf(p.id)
      .slice(1)
      .find((x) => placements.has(x));
    p.base = ancestor ? placements.get(ancestor)! : null;
  }
  const rootOf = (p: Placement): Placement => {
    let x = p;
    for (let n = 0; x.base && n < 64; n++) x = x.base;
    return x;
  };
  const inHierarchy = new Set<Placement>();
  for (const p of placementList) {
    if (!p.base) continue;
    inHierarchy.add(p);
    inHierarchy.add(p.base);
  }
  for (const p of placementList) {
    if (!inHierarchy.has(p)) continue;
    const root = rootOf(p);
    const own = entityMapping.get(root.id)?.inheritance ?? conventions.inheritance ?? "tph";
    p.strategy = own === "tpt" || own === "tpc" ? own : "tph";
  }
  // A TPH entity the engine makes keeps its rows in the root's table.
  for (const p of placementList) if (p.strategy === "tph" && !p.bound && p.base && rootOf(p).table) p.table = rootOf(p).table;
  const placementOf = (id: string): Placement | null => placements.get(id) ?? null;

  /** Whether a placement's rows are also in its base's table (TPT, or TPH for a table the engine makes). */
  const sharesBaseRows = (p: Placement) => p.strategy === "tpt" || (!p.bound && p.strategy === "tph");
  /** The covered base whose table holds a placement's rows, or null (the engine's RowsInCoveredBase). */
  const rowsInCoveredBase = (p: Placement, covered: Set<string>): Placement | null => {
    for (let b = p.base, n = 0; b && b !== p && n < 64; b = b.base, n++)
      if (covered.has(b.id) && b.table && (b.table === p.table || sharesBaseRows(p))) return b;
    return null;
  };
  /** Whether a table holds a target's rows: its own, or the table of a base that holds them (decision 41). */
  const holdsRows = (table: TableView, target: Placement): boolean => {
    if (target.table === table) return true;
    for (let b = target.base, n = 0; b && b !== target && n < 64; b = b.base, n++)
      if (b.table === table && (b.table === target.table || sharesBaseRows(target))) return true;
    return false;
  };

  // The code of an entity in the database (decision 42): the binding's discriminatorValue when it has one, else its mapping's, else its name.
  const codeOf = (entity: Json): string | number => {
    const binding = arr(entity.bindings).find((b) => b.database === dbId);
    const value = binding ? binding.discriminatorValue : entityMapping.get(str(entity.id))?.discriminatorValue;
    if (value === undefined || value === null) return str(entity.name);
    if (typeof value === "string") return value;
    if (typeof value === "number" && Number.isInteger(value)) return value;
    if (typeof value === "boolean") return value ? "True" : "False";
    return String(value);
  };
  const codeText = (c: string | number) => String(c);

  /** The covered entities of an end in the order of decision 39: each type, then what is derived from it by name. */
  const docEndEntity = (end: Json): string | null => {
    if (typeof end.entity === "string") return end.entity;
    const only = arr(end.types);
    return only.length === 1 && typeof only[0].entity === "string" ? only[0].entity : null;
  };
  const coveredOf = (end: Json): string[] => {
    const several = endCover.get(str(end.id));
    const types = several ? several.types : [docEndEntity(end)].filter((x): x is string => !!x);
    const seen = new Set<string>();
    const list: string[] = [];
    for (const type of types) {
      if (!docs.has(type) || seen.has(type)) continue;
      seen.add(type);
      list.push(type);
      for (const d of entityDocs.filter((e) => str(e.id) !== type && chainOf(str(e.id)).includes(type) && !seen.has(str(e.id)))) {
        seen.add(str(d.id));
        list.push(str(d.id));
      }
    }
    return list;
  };
  /** The covered entities as the end's targets: placed with a table, and not holding their rows in a covered base's table. */
  const targetsOf = (covered: string[], set: Set<string>): { targets: Placement[]; leftOut: Json[] } => {
    const targets: Placement[] = [];
    const leftOut: Json[] = [];
    for (const id of covered) {
      const p = placementOf(id);
      if (!p) {
        const entity = docs.get(id)!;
        if (entity.abstract !== true) leftOut.push(entity);
        continue;
      }
      if (p.table && !rowsInCoveredBase(p, set)) targets.push(p);
    }
    return { targets, leftOut };
  };
  /** The entities a target stands for at an end: itself and the covered placed entities whose rows are in its table (decision 48). */
  const standsFor = (e: End, target: Placement): Placement[] => {
    const list = [target];
    for (const p of placementList) {
      if (p === target || !e.covered.has(p.id) || e.targets.includes(p)) continue;
      let b = rowsInCoveredBase(p, e.covered);
      while (b && !e.targets.includes(b)) b = rowsInCoveredBase(b, e.covered);
      if (b === target) list.push(p);
    }
    return list;
  };
  /** Whether the target's table holds rows the entities it stands for don't own, with nothing to tell them apart (RowsNotToldApart, PickRows). */
  const rowsNotToldApart = (e: End, target: Placement): boolean => {
    const table = target.table!;
    const stands = standsFor(e, target);
    if (!placementList.some((p) => p.table === table && !stands.includes(p))) return false;
    if (target.strategy === "tph" && !target.bound) return false; // the discriminator
    return pickRows(table, stands) === null;
  };
  /** The conditions that pick the rows of the entities a target stands for, or null when nothing does. */
  const pickRows = (table: TableView, stands: Placement[]): { column: ColumnView; values: unknown[] }[] | null => {
    const rows = stands.filter((p) => p.entity.abstract !== true);
    if (rows.length === 0) return [];
    const each: { column: ColumnView; values: unknown[] }[][] = [];
    for (const p of rows) {
      const constants = arr(p.binding?.constants);
      if (!p.viaBinding || constants.length === 0) return null;
      const conditions: { column: ColumnView; values: unknown[] }[] = [];
      for (const c of constants) {
        const column = table.columns.find((x) => x.key === c.column || x.name === c.column);
        if (column) conditions.push({ column, values: [c.value] });
      }
      if (rows.length > 1 && constants.length !== 1) return null;
      each.push(conditions);
    }
    if (each.length === 1) return each[0];
    const first = each[0][0];
    if (each[0].length !== 1 || !each.every((c) => c.length === 1 && c[0].column === first.column)) return null;
    return [{ column: first.column, values: each.flatMap((c) => c[0].values) }];
  };

  const tableDoc = (t: TableView) => tableDocs.get(t.key);
  /** Whether some check of the table names every one of the columns: a column check on it, or its text names it as a word. */
  const hasCheckOn = (t: TableView, columns: ColumnView[]): boolean =>
    arr(tableDoc(t)?.checks).some((c) =>
      columns.every((col) => {
        if (c.column === col.key) return true;
        const text = typeof c.expression === "string" ? c.expression : JSON.stringify(c.expression ?? c.condition ?? {});
        return text.includes(col.key) || new RegExp(`(^|[^A-Za-z0-9_])${col.name.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}($|[^A-Za-z0-9_])`, "i").test(text);
      }),
    );
  /** Whether a unique key, a unique index without a filter or the primary key is over exactly the columns. */
  const hasUniqueOn = (t: TableView, columns: string[]): boolean => {
    const same = (a: string[]) => a.length === columns.length && columns.every((x) => a.includes(x));
    return (
      (t.primaryKey !== null && same(t.primaryKey.columns)) ||
      t.uniques.some((u) => same(u.columns)) ||
      t.indexes.some((ix) => ix.unique && ix.where === null && same(ix.columns.map((c) => c.column)))
    );
  };
  /** Whether a column is unique in its table: the one-column primary key, a unique key, or a unique index without a filter (not on Oracle). */
  const isUniqueColumn = (t: TableView, column: string): boolean =>
    (t.primaryKey?.columns.length === 1 && t.primaryKey.columns[0] === column) ||
    t.uniques.some((u) => u.columns.length === 1 && u.columns[0] === column) ||
    (dialect !== "oracle" && t.indexes.some((ix) => ix.unique && ix.where === null && ix.columns.length === 1 && ix.columns[0].column === column));
  /** The unique to copy (decisions 31 and 47): a unique key, or on SQL Server for a one-to-one a unique index filtered to rows where it is set. */
  const uniqueText = (t: TableView, columns: ColumnView[], filtered: boolean): string => {
    const list = columns.map((c) => Q(c.name)).join(", ");
    const joined = columns.map((c) => c.name).join("_");
    if (filtered && dialect === "sqlserver") {
      const index = render(String(conventions.indexName ?? "ix_{table}_{columns}"), { table: t.name, columns: joined });
      const where = columns.map((c) => `${Q(c.name)} IS NOT NULL`).join(" AND ");
      const qualified = t.schema ? `${Q(t.schema)}.${Q(t.name)}` : Q(t.name);
      return `CREATE UNIQUE INDEX ${Q(index)} ON ${qualified} (${list}) WHERE ${where}`;
    }
    const name = render(String(conventions.uniqueName ?? "uq_{table}_{columns}"), { table: t.name, columns: joined });
    return `CONSTRAINT ${Q(name)} UNIQUE (${list})`;
  };

  const result: RelationStorageItem[] = [];
  const usedBy = new Map<string, { mapping: Json; relation: Json; partTwo: boolean }[]>();
  const markLinkTable = (relation: Json, mapping: Json, table: TableView, partTwo: boolean) => {
    const list = usedBy.get(table.key) ?? [];
    if (!list.some((x) => x.relation === relation)) list.push({ mapping, relation, partTwo });
    usedBy.set(table.key, list);
  };

  for (const relation of relations.sort((a, b) => byOrdinal(str(a.id), str(b.id)))) {
    const relationEnds = arr(relation.ends);
    const several = relationEnds.some((e) => docEndHasSeveralTypes(e as EndDocLike));
    const mapping = mappingsOf.get(str(relation.id));
    if (!several) {
      if (mapping && mapping.ignore !== true && typeof mapping.junctionTable === "string") {
        markLinkTable(
          relation,
          mapping,
          tableByKey.get(mapping.junctionTable) ?? ({ key: mapping.junctionTable } as TableView),
          arr(mapping.constants).length > 0,
        );
        constantFindings(mapping, relation, tableByKey.get(mapping.junctionTable));
      }
      continue;
    }
    const ends = relationEnds.map((doc, index): End => {
      const covered = coveredOf(doc);
      const set = new Set(covered);
      const { targets, leftOut } = targetsOf(covered, set);
      return {
        doc,
        index,
        entry: undefined,
        covered: set,
        targets,
        leftOut: leftOut.sort((a, b) => (str(a.name) < str(b.name) ? -1 : str(a.name) > str(b.name) ? 1 : byOrdinal(str(a.id), str(b.id)))),
        notStored: [],
      };
    }) as [End, End];
    if (mapping)
      arr(mapping.ends).forEach((json, index) => {
        const end = ends.find((e) => e.doc.id === json.end);
        if (end && !end.entry) end.entry = { index, json };
      });
    // A relation is listed in the database when each end has a covered entity placed there.
    if (!ends.every((e) => [...e.covered].some((id) => placements.has(id)))) continue;
    let links: Links | null = null;
    if (mapping && mapping.ignore !== true && storesRelation(mapping)) {
      const linkTable =
        typeof mapping.junctionTable === "string" && tableDocs.get(mapping.junctionTable)?.origin !== "synthesized"
          ? tableByKey.get(mapping.junctionTable)
          : undefined;
      if ((mapping.junctionTable === undefined || linkTable) && typeItemsFit(relation, mapping, ends)) {
        links = { mapping, linkTable, ends, pairs: [] };
        collectPairs(relation, links);
      }
    }
    result.push(finishOne(relation, mapping, ends, links));
  }

  // Two relations that keep their links in one table with the same constants would read each other's links (decision 44).
  for (const [key, list] of usedBy) {
    const table = tableByKey.get(key);
    if (!table || list.length < 2) continue;
    const sig = (m: Json) =>
      JSON.stringify(
        arr(m.constants)
          .map((c) => [str(c.column), c.value])
          .sort(),
      );
    list.sort((a, b) => byOrdinal(str(a.mapping.id), str(b.mapping.id)));
    for (let j = 1; j < list.length; j++)
      for (let i = 0; i < j; i++) {
        const a = list[i];
        const b = list[j];
        const writes = a.partTwo || b.partTwo || arr(a.mapping.constants).length > 0 || arr(b.mapping.constants).length > 0;
        if (writes && sig(a.mapping) === sig(b.mapping)) {
          diag(
            "MQ4117",
            "error",
            `Relations '${str(a.relation.name)}' and '${str(b.relation.name)}' both keep their links in table '${table.name}' of database '${view.name}' with the same constants, so each would read the other's links; give each its own 'constants'.`,
            b.mapping,
            arr(b.mapping.constants).length > 0 ? "/constants" : "/junctionTable",
          );
          break;
        }
      }
  }
  return { storage: result, diagnostics: out };

  // ---------------------------------------------------------------- pointers and refusals

  function entryPointer(mapping: Json, endId: unknown): string {
    const i = arr(mapping.ends).findIndex((x) => x.end === endId);
    return i < 0 ? "/ends" : `/ends/${i}`;
  }
  function refuse(mapping: Json, pointer: string, message: string) {
    diag("MQ4117", "error", message, mapping, pointer);
  }

  /** MQ4051: a constant its column can't hold. */
  function constantFindings(mapping: Json, relation: Json, table: TableView | undefined) {
    arr(mapping.constants).forEach((c, i) => {
      const found = columnOf.get(str(c.column));
      if (!found || (table && found.table !== table)) return;
      const column = found.column;
      const problem = fitProblem(c.value, column.type, column.length, column.nullable);
      if (problem)
        diag(
          "MQ4051",
          "warning",
          `The constant for column '${column.name}' of the mapping of relation '${str(relation.name)}' to database '${view.name}' ${problem} (the column is ${column.type}).`,
          mapping,
          `/constants/${i}/value`,
        );
    });
  }

  /** MQ4117 for a types item that names an entity the end doesn't cover, or a covered entity whose rows are in a covered base's table (TypeItemsFit). */
  function typeItemsFit(relation: Json, mapping: Json, ends: [End, End]): boolean {
    let fits = true;
    for (const e of ends) {
      if (!e.entry) continue;
      arr(e.entry.json.types).forEach((item, k) => {
        const id = str(item.entity);
        const at = `${entryPointer(mapping, e.doc.id)}/types/${k}/entity`;
        const name = str(docs.get(id)?.name) || id;
        const p = placementOf(id);
        if (!e.covered.has(id)) {
          refuse(mapping, at, `Type '${name}' of a types item isn't covered by end '${str(e.doc.role)}'; name one of the end's types.`);
          fits = false;
        } else if (p && p.table) {
          const holder = rowsInCoveredBase(p, e.covered);
          if (holder) {
            const holderName = str(holder.entity.name);
            refuse(
              mapping,
              at,
              `Type '${name}' of end '${str(e.doc.role)}' keeps its rows in the table of '${holderName}', which the end also covers, so it isn't a target of its own; name '${holderName}' instead.`,
            );
            fits = false;
          }
        }
      });
    }
    void relation;
    return fits;
  }

  /** The pairs of targets with their holding tables and pointers (CollectPairs). */
  function collectPairs(relation: Json, links: Links) {
    const m = links.mapping;
    const rname = str(relation.name);
    const [a, b] = links.ends;
    // An any-entity end: no storage that grows with the model (decision 43).
    for (const e of links.ends) {
      if (e.doc.anyEntity !== true) continue;
      const other = e === a ? b : a;
      if (e.entry && arr(e.entry.json.types).some((t) => t.foreignKey !== undefined)) {
        refuse(
          m,
          `${entryPointer(m, e.doc.id)}/types`,
          `End '${str(e.doc.role)}' is any entity, so it can't have a key per type: every new entity would need a column. Use a type and an id, or an id alone.`,
        );
        return;
      }
      if (!e.entry && !links.linkTable && other.entry && Array.isArray(other.entry.json.foreignKeys) && other.entry.json.foreignKeys.length > 0) {
        refuse(
          m,
          `${entryPointer(m, other.doc.id)}/foreignKeys`,
          `End '${str(e.doc.role)}' is any entity, so its types' tables can't each hold a key: every new entity would need a column. Use a type and an id, or an id alone.`,
        );
        return;
      }
    }
    // Two items of foreignKeys owned by one table.
    for (const e of links.ends) {
      const keys = Array.isArray(e.entry?.json.foreignKeys) ? (e.entry!.json.foreignKeys as string[]) : [];
      if (keys.length < 2) continue;
      const hosts = new Map<TableView, string>();
      for (let k = 0; k < keys.length; k++) {
        const spec = fkOf.get(keys[k]);
        if (!spec) continue;
        const first = hosts.get(spec.host);
        if (first !== undefined) {
          refuse(
            m,
            `${entryPointer(m, e.doc.id)}/foreignKeys/${k}`,
            `Foreign keys '${first}' and '${spec.specName}' of end '${str(e.doc.role)}' both belong to table '${spec.host.name}'; name one key per table that holds links.`,
          );
          return;
        }
        hosts.set(spec.host, spec.specName);
      }
    }
    const addPair = (holding: TableView, ta: Placement, tb: Placement, holder: End | null, link: boolean) => {
      const pa = pointerOf(relation, links, holding, a, ta, [b, tb], holder, link);
      const pb = pointerOf(relation, links, holding, b, tb, [a, ta], holder, link);
      if (pa && pb) links.pairs.push({ table: holding, ends: [pa, pb] });
    };
    if (links.linkTable) {
      if (links.ends.some((e) => !e.entry)) return; // MQ4011
      markLinkTable(relation, m, links.linkTable, true);
      for (const ta of a.targets) for (const tb of b.targets) addPair(links.linkTable, ta, tb, null, true);
      return;
    }
    const noEntry = links.ends.filter((e) => !e.entry);
    if (noEntry.length === 1) {
      const h = noEntry[0];
      const o = h === a ? b : a;
      if (o.doc.max !== 1) {
        refuse(
          m,
          entryPointer(m, o.doc.id),
          `The rows of end '${str(h.doc.role)}' would hold the links of relation '${rname}' in database '${view.name}', but a row holds one link and end '${str(o.doc.role)}' is to-many; keep the links in a link table ('junctionTable').`,
        );
        return;
      }
      const holderTables = [...new Set(h.targets.map((t) => t.table!))];
      if (holderTables.length > 1 && (typeof o.entry!.json.foreignKey === "string" || typeof o.entry!.json.idColumn === "string")) {
        refuse(
          m,
          entryPointer(m, o.doc.id),
          `The targets of end '${str(h.doc.role)}' live in tables ${names(holderTables.map((t) => t.name))}, but end '${str(o.doc.role)}' is pointed at by one column; name a key per table with 'foreignKeys', or keep the links in a link table. Column pointers spread over several tables wait.`,
        );
        return;
      }
      for (const ta of a.targets) for (const tb of b.targets) addPair((h === a ? ta : tb).table!, ta, tb, h, false);
      return;
    }
    if (noEntry.length === 0) {
      const typed = links.ends.filter((e) => arr(e.entry!.json.types).some((t) => t.foreignKey !== undefined));
      if (typed.length === 0) {
        refuse(
          m,
          "/ends",
          a.doc.max !== 1 && b.doc.max !== 1
            ? `Relation '${rname}' is many to many, so its links need a link table: name 'junctionTable', or a key per type of one end in tables of their own.`
            : `Every end of relation '${rname}' has an entry and there is no 'junctionTable', so no table holds its links in database '${view.name}'; name a link table, or leave out the entry of the end whose rows hold the links.`,
        );
        return;
      }
      if (typed.length === 2) {
        refuse(
          m,
          "/ends",
          `Both ends of relation '${rname}' have a key per type and there is no 'junctionTable'; a link table per pair of types waits. Name one link table.`,
        );
        return;
      }
      const p = typed[0];
      for (const ta of a.targets)
        for (const tb of b.targets) {
          const pt = p === a ? ta : tb;
          const item = arr(p.entry!.json.types).find((t) => t.entity === pt.id && t.foreignKey !== undefined);
          const spec = item ? fkOf.get(str(item.foreignKey)) : undefined;
          if (!item || !spec) {
            notStored(p, pt);
            continue;
          }
          markLinkTable(relation, m, spec.host, true);
          addPair(spec.host, ta, tb, null, true);
        }
    }
  }

  function notStored(e: End, target: Placement) {
    if (!e.notStored.includes(target)) e.notStored.push(target);
  }

  /** How the holding table points at one end's target (PointerOf); null when nothing points at it (MQ4120 lists the target) or the entry contradicts itself. */
  function pointerOf(
    relation: Json,
    links: Links,
    holding: TableView,
    e: End,
    target: Placement,
    other: [End, Placement],
    holder: End | null,
    link: boolean,
  ): Pointer | null {
    void relation;
    void link;
    if (e === holder) return { end: e, target, isHolder: true };
    const m = links.mapping;
    const entry = e.entry!;
    const at = entryPointer(m, e.doc.id);
    const role = str(e.doc.role);
    const tname = str(target.entity.name);
    let spec: FkSpec | undefined;
    let specPointer = "";
    if (typeof entry.json.foreignKey === "string") {
      spec = fkOf.get(entry.json.foreignKey);
      if (!spec) return null; // MQ4009
      specPointer = `${at}/foreignKey`;
    } else if (Array.isArray(entry.json.foreignKeys) && entry.json.foreignKeys.length > 0) {
      if (links.linkTable) return null; // MQ4009
      const keys = entry.json.foreignKeys as string[];
      const k = keys.findIndex((x) => fkOf.get(x)?.host === holding);
      if (k < 0) {
        // No key of the list is in the table: the target whose table (or link table) it is isn't reached.
        notStored(other[0], other[1]);
        return null;
      }
      spec = fkOf.get(keys[k]);
      specPointer = `${at}/foreignKeys/${k}`;
    } else if (arr(entry.json.types).some((t) => t.foreignKey !== undefined)) {
      const k = arr(entry.json.types).findIndex((t) => t.entity === target.id && t.foreignKey !== undefined);
      spec = k < 0 ? undefined : fkOf.get(str(arr(entry.json.types)[k].foreignKey));
      if (!spec) {
        notStored(e, target);
        return null;
      }
      specPointer = `${at}/types/${k}/foreignKey`;
    }
    if (spec) {
      if (spec.host !== holding) {
        refuse(
          m,
          specPointer,
          `Foreign key '${spec.specName}' of table '${spec.host.name}' points at end '${role}', but the links of '${tname}' there are held in table '${holding.name}'; name a key of that table.`,
        );
        return null;
      }
      return { end: e, target, isHolder: false, fk: spec };
    }
    if (typeof entry.json.idColumn !== "string") return null; // MQ4009: no way
    const idColumn = entry.json.idColumn;
    if (columnOf.get(idColumn)?.table !== holding) {
      refuse(
        m,
        `${at}/idColumn`,
        `Column '${nameOfColumn(idColumn)}' named by end '${role}' isn't in table '${holding.name}', which holds the links of '${tname}'.`,
      );
      return null;
    }
    const typeColumn = typeof entry.json.typeColumn === "string" ? entry.json.typeColumn : undefined;
    if (typeColumn && columnOf.get(typeColumn)?.table !== holding) {
      refuse(
        m,
        `${at}/typeColumn`,
        `Column '${nameOfColumn(typeColumn)}' named by end '${role}' isn't in table '${holding.name}', which holds the links of '${tname}'.`,
      );
      return null;
    }
    const item = arr(entry.json.types).find((t) => t.entity === target.id && t.referencesColumn !== undefined);
    const referencesColumn = item ? str(item.referencesColumn) : typeof entry.json.referencesColumn === "string" ? entry.json.referencesColumn : undefined;
    return { end: e, target, isHolder: false, idColumn, typeColumn, referencesColumn };
  }

  /** A pointer resolved against the finished tables (ResolvePointer), or null with MQ4117. */
  function resolvePointer(links: Links, pair: Pair, p: Pointer): Resolved | null {
    const r: Resolved = { end: p.end, target: p.target, isHolder: p.isHolder, columns: [], codes: [] };
    if (p.isHolder) return r;
    const m = links.mapping;
    const at = entryPointer(m, p.end.doc.id);
    const role = str(p.end.doc.role);
    const target = p.target;
    const tname = str(target.entity.name);
    if (p.fk) {
      const spec = p.fk;
      if (!spec.refTable) return null; // MQ4008
      if (!holdsRows(spec.refTable, target)) {
        refuse(
          m,
          at,
          docEndHasSeveralTypes(p.end.doc as EndDocLike) && typeof p.end.entry?.json.foreignKey === "string"
            ? `Foreign key '${spec.name}' of table '${spec.host.name}' points at end '${role}', but it refers to table '${spec.refTable.name}', which doesn't hold ${tname} rows; a single key at an end with several types refers to a table that holds the rows of every type.`
            : `Foreign key '${spec.name}' of table '${spec.host.name}' is named for type '${tname}' of end '${role}', but it refers to table '${spec.refTable.name}', which doesn't hold ${tname} rows.`,
        );
        return null;
      }
      r.columns = spec.columns.map((c) => columnOf.get(c)?.column).filter((c): c is ColumnView => !!c);
      r.referencedTable = spec.refTable;
      r.fk = spec;
      return r;
    }
    const column = columnOf.get(p.idColumn!)!.column;
    const targetTable = target.table!;
    if (typeof p.end.entry?.json.referencesColumn === "string" && new Set(p.end.targets.map((t) => t.table)).size > 1) {
      refuse(
        m,
        `${at}/referencesColumn`,
        `End '${role}' names one 'referencesColumn', but its targets live in tables ${names([...new Set(p.end.targets.map((t) => t.table!.name))])}; name the column per type in 'types' items.`,
      );
      return null;
    }
    let referenced: ColumnView | undefined;
    if (p.referencesColumn !== undefined) {
      const found = columnOf.get(p.referencesColumn);
      referenced = found && found.table === targetTable ? found.column : undefined;
      if (!referenced) {
        refuse(
          m,
          at,
          `Column '${nameOfColumn(p.referencesColumn)}' that end '${role}' refers to for type '${tname}' isn't a column of table '${targetTable.name}'.`,
        );
        return null;
      }
    } else if (targetTable.primaryKey?.columns.length === 1) {
      referenced = targetTable.columns.find((c) => c.key === targetTable.primaryKey!.columns[0]);
    } else {
      const what = targetTable.primaryKey ? `a primary key of ${targetTable.primaryKey.columns.length} columns` : "no primary key";
      refuse(
        m,
        `${at}/idColumn`,
        `Table '${targetTable.name}' of type '${tname}' has ${what}, so column '${column.name}' of end '${role}' needs 'referencesColumn' naming the unique column it refers to.`,
      );
      return null;
    }
    if (!referenced) return null;
    if (!isUniqueColumn(targetTable, referenced.key)) {
      refuse(
        m,
        at,
        `Column '${column.name}' of table '${pair.table.name}' refers to column '${referenced.name}' of table '${targetTable.name}', which is neither its primary key nor a unique key.`,
      );
      return null;
    }
    if (!sameType(column, referenced)) {
      refuse(
        m,
        `${at}/idColumn`,
        `Column '${column.name}' of table '${pair.table.name}' is ${describe(column)}, but it refers to column '${referenced.name}' of table '${targetTable.name}', which is ${describe(referenced)}; make the types match.`,
      );
      return null;
    }
    r.columns = [column];
    r.referencedTable = targetTable;
    r.typeColumn = p.typeColumn ? columnOf.get(p.typeColumn)?.column : undefined;
    return r;
  }

  /** The codes each type column holds (FillCodes), with MQ4117 for collisions and codes the column can't hold. */
  function fillCodes(links: Links) {
    const m = links.mapping;
    links.ends.forEach((e, i) => {
      const held = new Map<ColumnView, Map<string, Json>>();
      for (const pair of links.pairs) {
        const r = pair.result?.[i];
        if (!r || !r.typeColumn) continue;
        const typeColumn = r.typeColumn;
        const integer = INTEGER_CODES.has(typeColumn.type);
        const codes: (string | number)[] = [];
        for (const p of standsFor(e, pair.ends[i].target)) {
          if (p.entity.abstract === true) continue;
          const code = codeOf(p.entity);
          codes.push(integer ? code : codeText(code));
          let byCode = held.get(typeColumn);
          if (!byCode) held.set(typeColumn, (byCode = new Map()));
          const text = codeText(code);
          const first = byCode.get(text);
          const at = `${entryPointer(m, e.doc.id)}/typeColumn`;
          if (first && first !== p.entity)
            refuse(
              m,
              at,
              `Types '${str(first.name)}' and '${str(p.entity.name)}' both have code '${text}' in database '${view.name}', so column '${typeColumn.name}' can't tell them apart; set discriminatorValue on one.`,
            );
          if (!first) byCode.set(text, p.entity);
          const misfit = codeMisfit(typeColumn, code);
          if (misfit)
            refuse(
              m,
              at,
              `Column '${typeColumn.name}' of table '${columnOf.get(typeColumn.key)?.table.name ?? ""}' holds the type codes of end '${str(e.doc.role)}', but ${misfit}.`,
            );
        }
        codes.sort((x, y) => byOrdinal(codeText(x), codeText(y)));
        r.codes = codes.filter((c, k) => codes.indexOf(c) === k);
      }
    });
  }
  function codeMisfit(column: ColumnView, code: string | number): string | null {
    if (INTEGER_CODES.has(column.type)) return typeof code === "number" ? null : `code '${codeText(code)}' isn't an integer; set an integer discriminatorValue`;
    if (!TEXTS.has(column.type)) return `it is a ${column.type} column; a type column holds text or integers`;
    const text = codeText(code);
    return column.length !== null && text.length > column.length ? `code '${text}' is ${text.length} characters long, over its ${column.length}` : null;
  }

  // ---------------------------------------------------------------- the finish (FinishOne)

  function finishOne(relation: Json, mapping: Json | undefined, ends: [End, End], links: Links | null): RelationStorageItem {
    const rname = str(relation.name);
    const item: RelationStorageItem = {
      relationId: str(relation.id),
      relationName: rname,
      mappingId: mapping ? str(mapping.id) : null,
      tables: [],
      leftOut: ends.flatMap((e) => e.leftOut.map((en) => ({ endId: str(e.doc.id), entityId: str(en.id) }))),
      notStored: [],
      gaps: [],
    };
    if (!links) return item;
    const m = links.mapping;
    for (const pair of links.pairs) {
      const resolved: Resolved[] = [];
      for (let i = 0; i < 2; i++) {
        const r = resolvePointer(links, pair, pair.ends[i]);
        if (!r) break;
        resolved.push(r);
      }
      if (resolved.length === 2) pair.result = [resolved[0], resolved[1]];
    }
    fillCodes(links);
    for (const e of ends) {
      e.notStored.sort((x, y) => e.targets.indexOf(x) - e.targets.indexOf(y));
      for (const target of e.notStored) {
        item.notStored.push({ endId: str(e.doc.id), entityId: target.id });
        diag(
          "MQ4120",
          "warning",
          `Relation '${rname}' covers entity '${str(target.entity.name)}' in database '${view.name}', but its mapping names nothing that points at it there, so links to ${str(target.entity.name)} objects aren't stored.`,
          m,
          e.entry ? `/ends/${e.entry.index}` : "/ends",
        );
      }
    }
    const holding = [...new Set(links.pairs.filter((p) => p.result).map((p) => p.table))];
    // The constants of the link table, then the attribute columns (decisions 44 and 45).
    if (links.linkTable) constantFindings(m, relation, links.linkTable);
    attributeColumns(relation, m, links.linkTable ? [links.linkTable] : holding, links.linkTable !== undefined);
    item.tables = holding.map((t) => t.key);
    item.gaps = gapsOf(relation, links, ends);
    return item;
  }

  function attributeColumns(relation: Json, m: Json, tables: TableView[], link: boolean) {
    arr(m.attributes).forEach((a, i) => {
      if (typeof a.column !== "string") return;
      const attribute = arr(relation.attributes).find((x) => x.id === a.attribute);
      if (!attribute) return;
      const at = `/attributes/${i}/column`;
      if (tables.length > 1) {
        refuse(
          m,
          at,
          `The links of relation '${str(relation.name)}' live in tables ${names(tables.map((t) => t.name))}, so one column can't hold attribute '${str(attribute.name)}'; a column per table waits.`,
        );
        return;
      }
      if (tables.length === 0) return;
      if (columnOf.get(a.column)?.table !== tables[0] && !link)
        refuse(
          m,
          at,
          `Column '${nameOfColumn(a.column)}' for attribute '${str(attribute.name)}' isn't in table '${tables[0].name}', which holds the links of relation '${str(relation.name)}'.`,
        );
    });
  }

  /** The gaps of each end the holding tables point at (decision 47), with one MQ4118 per end that has some (Gaps). */
  function gapsOf(relation: Json, links: Links, ends: [End, End]): StorageGap[] {
    const gaps: StorageGap[] = [];
    const m = links.mapping;
    const rname = str(relation.name);
    const relationEnds = arr(relation.ends);
    const oneToOne = relationEnds.every((e) => e.max === 1);
    const linkTables = new Set(links.pairs.filter((p) => p.result && p.ends.every((x) => !x.isHolder)).map((p) => p.table));
    let linkUniqueDone = false;
    ends.forEach((e, i) => {
      const pointers = links.pairs.filter((p) => p.result && !p.ends[i].isHolder).map((p) => ({ pair: p, pointer: p.result![i] }));
      const clauses: string[] = [];
      const texts: string[] = [];
      const kinds: StorageGap[] = [];
      const gap = (kind: RelationStorageKind, clause: string, text?: string, lead?: string) => {
        kinds.push({ endId: str(e.doc.id), kind, ...(text !== undefined ? { text } : {}) });
        clauses.push(clause);
        if (text !== undefined) texts.push(`${lead}, add: ${text}`);
      };
      const several = docEndHasSeveralTypes(e.doc as EndDocLike);
      const role = str(e.doc.role);
      if (pointers.length > 0) {
        const targets = [...new Set(pointers.map((x) => str(x.pointer.target.entity.name)))];
        if (pointers.some((x) => !x.pointer.fk)) {
          const onDelete = str(e.doc.onDelete) || "none";
          const rule = onDelete !== "none" ? `, nor apply the delete rule '${onDelete}'` : "";
          const cascade = relation.relationKind === "composition" ? ", nor a composition's cascade" : "";
          gap("no-foreign-key", `it can't refuse a link to a missing ${names(targets).replaceAll("', '", "' or '")}${rule}${cascade}`);
        }
        if (several && typeof e.entry?.json.foreignKey === "string")
          gap("shared-table", `it can't refuse a key to a row of table '${pointers[0].pointer.referencedTable!.name}' whose type end '${role}' doesn't cover`);
        if (several && pointers.some((x) => !x.pointer.fk && !x.pointer.typeColumn)) gap("id-only", "it can't check the type an id points at");
        const typeColumns: ColumnView[] = [];
        for (const x of pointers) if (x.pointer.typeColumn && !typeColumns.includes(x.pointer.typeColumn)) typeColumns.push(x.pointer.typeColumn);
        for (const column of typeColumns) {
          const table = columnOf.get(column.key)!.table;
          const codes = pointers.filter((x) => x.pointer.typeColumn === column).flatMap((x) => x.pointer.codes);
          const distinct = codes.filter((c, k) => codes.indexOf(c) === k).sort((x, y) => byOrdinal(codeText(x), codeText(y)));
          if (hasCheckOn(table, [column])) continue;
          const name = render(String(conventions.checkName ?? "ck_{table}_{name}"), { table: table.name, name: role });
          gap(
            "type-check",
            `it finds no check that names column '${column.name}' of table '${table.name}'`,
            `CONSTRAINT ${Q(name)} CHECK (${Q(column.name)} IN (${distinct.map((c) => sqlLiteral(c, dialect)).join(", ")}))`,
            "To check the codes",
          );
        }
        const keyed = arr(e.entry?.json.types).some((t) => t.foreignKey !== undefined);
        if (keyed) {
          const byTable = new Map<TableView, typeof pointers>();
          for (const x of pointers) byTable.set(x.pair.table, [...(byTable.get(x.pair.table) ?? []), x]);
          for (const [table, group] of byTable) {
            if (new Set(group.map((x) => x.pointer.fk)).size <= 1) continue;
            const firsts = [...new Set(group.map((x) => x.pointer.columns[0]))];
            if (hasCheckOn(table, firsts)) continue;
            const name = render(String(conventions.checkName ?? "ck_{table}_{name}"), { table: table.name, name: role });
            const sum = firsts.map((c) => `CASE WHEN ${Q(c.name)} IS NOT NULL THEN 1 ELSE 0 END`).join(" + ");
            gap(
              "count-check",
              `it finds no check on how many of columns ${names(firsts.map((c) => c.name))} of table '${table.name}' are set`,
              `CONSTRAINT ${Q(name)} CHECK ((${sum}) ${e.doc.min === 1 ? "= 1" : "<= 1"})`,
              "To check how many are set",
            );
          }
        }
        if (oneToOne) {
          const seen = new Set<ColumnView | null>();
          for (const x of pointers.filter((y) => !linkTables.has(y.pair.table))) {
            const first = x.pointer.columns.length > 0 ? x.pointer.columns[0] : null;
            if (seen.has(first)) continue;
            seen.add(first);
            const columns = x.pointer.typeColumn ? [x.pointer.typeColumn, ...x.pointer.columns] : [...x.pointer.columns];
            if (
              hasUniqueOn(
                x.pair.table,
                columns.map((c) => c.key),
              )
            )
              continue;
            gap(
              "one-to-one-unique",
              `it finds no unique on ${names(columns.map((c) => c.name))} of table '${x.pair.table.name}', so two rows can link to one object`,
              uniqueText(x.pair.table, columns, true),
              "To keep the links one to one",
            );
          }
        }
        if (keyed)
          for (const table of [...new Set(pointers.map((x) => x.pair.table))].filter((t) => links.linkTable !== undefined && t === links.linkTable))
            gap(
              "per-type-duplicates",
              `it can't refuse duplicate links in table '${table.name}', since a unique over nullable key columns doesn't stop them in most databases`,
            );
        const anyKeyed = ends.some((x) => arr(x.entry?.json.types).some((t) => t.foreignKey !== undefined) && links.linkTable !== undefined);
        if (!linkUniqueDone && several && relation.allowDuplicates !== true && !anyKeyed) {
          const done = new Set<TableView>();
          for (const pair of links.pairs.filter((p) => p.result && linkTables.has(p.table))) {
            if (done.has(pair.table)) continue;
            done.add(pair.table);
            linkUniqueDone = true;
            const columns: ColumnView[] = [];
            for (const end of pair.result!) {
              if (end.typeColumn) columns.push(end.typeColumn);
              columns.push(...end.columns);
            }
            if (links.linkTable)
              for (const c of arr(m.constants)) {
                const found = columnOf.get(str(c.column));
                if (found && found.table === links.linkTable) columns.push(found.column);
              }
            const distinct = columns.filter((c, k) => columns.indexOf(c) === k);
            if (
              hasUniqueOn(
                pair.table,
                distinct.map((c) => c.key),
              )
            )
              continue;
            gap(
              "link-unique",
              `it finds no unique over columns ${names(distinct.map((c) => c.name))} of table '${pair.table.name}', so it can't refuse a duplicate link`,
              uniqueText(pair.table, distinct, false),
              "To refuse duplicate links",
            );
          }
        }
      }
      const shown = new Set<Placement>();
      for (const target of links.pairs.filter((p) => p.result).map((p) => p.ends[i].target)) {
        if (shown.has(target)) continue;
        shown.add(target);
        if (rowsNotToldApart(e, target))
          gap("rows-not-told-apart", `nothing tells the rows of '${str(target.entity.name)}' apart from the other rows of table '${target.table!.name}'`);
      }
      if (kinds.length === 0) return;
      gaps.push(...kinds);
      const tables = [...new Set(pointers.map((x) => x.pair.table.name))];
      const first = pointers[0]?.pointer;
      const entryJson = e.entry?.json;
      const how =
        pointers.length === 0
          ? "the rows of its types"
          : !first.fk
            ? first.typeColumn
              ? "a type and an id"
              : "an id"
            : Array.isArray(entryJson?.foreignKeys) && entryJson.foreignKeys.length > 0
              ? "keys in each table"
              : arr(entryJson?.types).some((t) => t.foreignKey !== undefined)
                ? "a key per type"
                : "a key";
      const where = tables.length === 0 ? "" : tables.length === 1 ? ` in table '${tables[0]}'` : ` in tables ${names(tables)}`;
      const message = `Database '${view.name}' holds end '${role}' of relation '${rname}' as ${how}${where}, so ${clauses.join("; and ")}.${texts.map((t) => ` ${t}`).join("")}`;
      diag("MQ4118", "info", message, m, e.entry ? `/ends/${e.entry.index}` : "/ends");
    });
    return gaps;
  }
}
