// The mock's storage run for relations with an end of several types (several-types-design.md section 19.5 decisions 39 to 49, 19.8): what
// the engine's FinishTypeStorage works out from a relation mapping and the team's tables. It reports the run's findings (MQ4117, MQ4118,
// MQ4120 and MQ4051 for a constant) and fills the database view's `relationStorage`. Like the engine, the caller reports no finding of the
// run while validation has errors. The texts are the engine's, as the two parity fixtures record them.
import type { ColumnView, DatabaseView, Diagnostic, TableView } from "@/api/types";
import { docEndHasSeveralTypes, type EndDocLike } from "@/model/endTypes";
import { Coverage } from "./coverage";
import { severalEnds } from "./endCover";
import { storesRelation } from "./endRules";
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

const INTEGERS = new Set(["int8", "int16", "int32", "int64", "byte", "short", "int", "long", "integer"]);
const TEXTS = new Set(["string", "text"]);

const joinQuoted = (names: string[]) => names.map((n) => `'${n}'`).join(", ");
const orWords = (names: string[]) => (names.length < 2 ? joinQuoted(names) : `${joinQuoted(names.slice(0, -1))} or '${names[names.length - 1]}'`);
const snake = (s: string) => s.replace(/([a-z0-9])([A-Z])/g, "$1_$2").toLowerCase();
const facets = (c: ColumnView) =>
  TEXTS.has(c.type) && c.length !== null
    ? `(${c.length})`
    : c.type === "decimal" && c.precision !== null
      ? `(${c.precision}${c.scale !== null ? `,${c.scale}` : ""})`
      : "";
const describe = (c: ColumnView) => `${c.type}${facets(c)}`;
const sameType = (a: ColumnView, b: ColumnView) =>
  a.type === b.type && (!TEXTS.has(a.type) || a.length === b.length) && (a.type !== "decimal" || (a.precision === b.precision && a.scale === b.scale));

interface Target {
  entity: Json;
  table: TableView;
}
interface Entry {
  index: number;
  json: Json;
  end: Json;
  endIndex: number;
}

/** What the run reads of a database: its resolved view and the model's documents. */
export function typeStorageRun(docs: ReadonlyMap<string, Json>, view: DatabaseView): TypeStorageResult {
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
  const fkOf = new Map<string, { table: TableView; name: string; columns: string[]; refTable: TableView | undefined; refColumns: string[] }>();
  for (const d of all)
    if (d.kind === "table" && d.database === dbId && d.origin !== "synthesized") {
      const table = tableByKey.get(str(d.id));
      if (table)
        for (const fk of arr(d.foreignKeys))
          fkOf.set(str(fk.id), {
            table,
            name: str(fk.name) || `fk_${table.name}`,
            columns: (fk.columns as string[] | undefined) ?? [],
            refTable: tableByKey.get(str(fk.referencesTable)),
            refColumns: (fk.referencesColumns as string[] | undefined) ?? [],
          });
    }
  const tableDocs = new Map(all.filter((d) => d.kind === "table" && d.database === dbId).map((d) => [str(d.id), d]));

  // Placement: a binding there, else the entity's mapping (a bound table), else the table laid out for it.
  const entityMapping = new Map(all.filter((d) => d.kind === "mapping" && d.database === dbId && typeof d.entity === "string").map((d) => [str(d.entity), d]));
  const placement = (entity: Json): TableView | null => {
    const binding = arr(entity.bindings).find((b) => b.database === dbId);
    if (binding) {
      const write = binding.write as Json | string | undefined;
      if (write && typeof write === "object" && write.none === true) return null;
      const key = write && typeof write === "object" && typeof write.table === "string" ? write.table : str(binding.source);
      return tableByKey.get(key) ?? null;
    }
    const mapping = entityMapping.get(str(entity.id));
    if (mapping?.ignore === true) return null;
    if (mapping && typeof mapping.table === "string") return tableByKey.get(mapping.table) ?? null;
    return view.tables.find((t) => t.entityId === entity.id && !t.relationId) ?? null;
  };
  const placed = new Map<string, TableView | null>();
  const placementOf = (id: string): TableView | null => {
    if (!placed.has(id)) placed.set(id, docs.get(id) ? placement(docs.get(id)!) : null);
    return placed.get(id) ?? null;
  };
  const chainOf = (id: string): string[] => {
    const doc = docs.get(id);
    return doc ? coverage.chain(doc) : [id];
  };

  // The code of an entity in the database (decision 42).
  const codeOf = (entity: Json): string => {
    const binding = arr(entity.bindings).find((b) => b.database === dbId);
    const own = binding?.discriminatorValue ?? entityMapping.get(str(entity.id))?.discriminatorValue;
    return own !== undefined && own !== null ? String(own) : str(entity.name);
  };

  /** The covered entities of an end in the order of decision 39: each type, then what is derived from it by name. */
  const coveredOf = (end: Json): string[] => {
    const several = endCover.get(str(end.id));
    const types = several ? several.types : [docEndEntity(end)].filter((x): x is string => !!x);
    const seen = new Set<string>();
    const list: string[] = [];
    const entityDocs = all.filter((d) => d.kind === "entity");
    for (const type of types) {
      if (!docs.has(type) || seen.has(type)) continue;
      seen.add(type);
      list.push(type);
      const derived = entityDocs
        .filter((e) => str(e.id) !== type && chainOf(str(e.id)).includes(type) && !seen.has(str(e.id)))
        .sort((a, b) => (str(a.name) < str(b.name) ? -1 : str(a.name) > str(b.name) ? 1 : str(a.id) < str(b.id) ? -1 : 1));
      for (const d of derived) {
        seen.add(str(d.id));
        list.push(str(d.id));
      }
    }
    return list;
  };
  const docEndEntity = (end: Json): string | null => {
    if (typeof end.entity === "string") return end.entity;
    const only = arr(end.types);
    return only.length === 1 && typeof only[0].entity === "string" ? only[0].entity : null;
  };
  const targetsOf = (end: Json): { targets: Target[]; leftOut: Json[] } => {
    const covered = coveredOf(end);
    const targets: Target[] = [];
    const leftOut: Json[] = [];
    for (const id of covered) {
      const entity = docs.get(id)!;
      const table = placementOf(id);
      if (!table) {
        if (entity.abstract !== true) leftOut.push(entity);
        continue;
      }
      // Rows in the table of a covered base stand for that base (TPH or TPT).
      const base = chainOf(id)
        .slice(1)
        .some((x) => covered.includes(x) && placementOf(x) === table);
      if (!base) targets.push({ entity, table });
    }
    return { targets, leftOut };
  };
  const placedEntities = (end: Json): Json[] =>
    coveredOf(end)
      .map((id) => docs.get(id)!)
      .filter((e) => e.abstract !== true && placementOf(str(e.id)));

  const dialect = view.dialect;
  const quote = (name: string) => {
    if (/^[a-z_][a-z0-9_]*$/.test(name)) return name;
    return dialect === "sqlserver" ? `[${name}]` : dialect === "mysql" ? `\`${name}\`` : `"${name.replace(/"/g, '""')}"`;
  };
  const literal = (code: string, column: ColumnView | undefined) =>
    column && INTEGERS.has(column.type) && /^-?\d+$/.test(code) ? code : `'${code.replace(/'/g, "''")}'`;

  const tableDoc = (t: TableView) => tableDocs.get(t.key);
  const checkNames = (t: TableView, columns: ColumnView[]): boolean => {
    const doc = tableDoc(t);
    return arr(doc?.checks).some((c) => {
      if (typeof c.column === "string") return columns.some((col) => col.key === c.column);
      const text = JSON.stringify(c.expression ?? c.condition ?? {});
      return columns.some((col) => text.includes(col.key) || new RegExp(`\\b${col.name}\\b`).test(text));
    });
  };
  const uniqueExists = (t: TableView, columns: string[]): boolean => {
    const same = (a: string[]) => a.length === columns.length && a.every((x) => columns.includes(x));
    return (
      (t.primaryKey !== null && same(t.primaryKey.columns)) ||
      t.uniques.some((u) => same(u.columns)) ||
      t.indexes.some((ix) => ix.unique && ix.where === null && same(ix.columns.map((c) => c.column)))
    );
  };

  const result: RelationStorageItem[] = [];
  const usedBy = new Map<string, { mapping: Json; relation: Json }[]>();

  for (const relation of relations.sort((a, b) => (str(a.id) < str(b.id) ? -1 : 1))) {
    const ends = arr(relation.ends);
    const several = ends.some((e) => docEndHasSeveralTypes(e as EndDocLike));
    const mapping = mappingsOf.get(str(relation.id));
    if (!several) {
      if (mapping && mapping.ignore !== true && typeof mapping.junctionTable === "string") {
        const list = usedBy.get(mapping.junctionTable) ?? [];
        list.push({ mapping, relation });
        usedBy.set(mapping.junctionTable, list);
        constantFindings(mapping, relation);
      }
      continue;
    }
    const info = ends.map((e) => targetsOf(e));
    // A relation is placed in the database when each end has a target there.
    if (!info.every((i) => i.targets.length)) continue;
    const item: RelationStorageItem = {
      relationId: str(relation.id),
      relationName: str(relation.name),
      mappingId: mapping ? str(mapping.id) : null,
      tables: [],
      leftOut: ends.flatMap((e, i) => info[i].leftOut.map((en) => ({ endId: str(e.id), entityId: str(en.id) }))),
      notStored: [],
      gaps: [],
    };
    result.push(item);
    if (!mapping || mapping.ignore === true || !storesRelation(mapping)) continue;
    const list = usedBy.get(str(mapping.junctionTable)) ?? [];
    if (typeof mapping.junctionTable === "string") {
      list.push({ mapping, relation });
      usedBy.set(mapping.junctionTable, list);
    }
    analyse(relation, ends, mapping, info, item);
  }

  for (const m of withConstants) {
    const relation = docs.get(str(m.relation));
    if (relation && !arr(relation.ends).some((e) => docEndHasSeveralTypes(e as EndDocLike))) continue;
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
    list.sort((a, b) => (str(a.mapping.id) < str(b.mapping.id) ? -1 : 1));
    for (let j = 1; j < list.length; j++)
      for (let i = 0; i < j; i++) {
        const a = list[i];
        const b = list[j];
        const writes = arr(a.mapping.constants).length > 0 || arr(b.mapping.constants).length > 0;
        const typed = [a.relation, b.relation].some((r) => arr(r.ends).some((e) => docEndHasSeveralTypes(e as EndDocLike)));
        if ((writes || typed) && sig(a.mapping) === sig(b.mapping)) {
          diag(
            "MQ4117",
            "error",
            `Relations '${str(a.relation.name)}' and '${str(b.relation.name)}' both keep their links in table '${table.name}' of database '${view.name}' with the same constants, so each would read the other's links; give each its own 'constants'.`,
            b.mapping,
            "/constants",
          );
          break;
        }
      }
  }
  return { storage: result, diagnostics: out };

  /** MQ4051: a constant its column can't hold. */
  function constantFindings(mapping: Json, relation: Json) {
    arr(mapping.constants).forEach((c, i) => {
      const column = columnOf.get(str(c.column))?.column;
      if (column && TEXTS.has(column.type) && column.length !== null && typeof c.value === "string" && c.value.length > column.length)
        diag(
          "MQ4051",
          "warning",
          `The constant for column '${column.name}' of the mapping of relation '${str(relation.name)}' to database '${view.name}' is ${c.value.length} characters long, over the column's ${column.length} (the column is ${column.type}).`,
          mapping,
          `/constants/${i}/value`,
        );
    });
  }

  function analyse(relation: Json, ends: Json[], mapping: Json, info: { targets: Target[]; leftOut: Json[] }[], item: RelationStorageItem) {
    const rname = str(relation.name);
    const entryList: Entry[] = arr(mapping.ends).flatMap((json, index) => {
      const endIndex = ends.findIndex((e) => e.id === json.end);
      return endIndex < 0 ? [] : [{ index, json, end: ends[endIndex], endIndex }];
    });
    const entryFor = (endIndex: number) => entryList.find((e) => e.endIndex === endIndex);
    const junction = typeof mapping.junctionTable === "string" ? tableByKey.get(mapping.junctionTable) : undefined;
    const hasKeysPerType = (e: Entry | undefined) => !!e && arr(e.json.types).some((t) => t.foreignKey !== undefined);
    let hard = false;
    const unlinkable = new Set<Json>();
    const fail = (message: string, pointer: string) => {
      hard = true;
      diag("MQ4117", "error", message, mapping, pointer);
    };
    const soft = (message: string, pointer: string) => diag("MQ4117", "error", message, mapping, pointer);
    constantFindings(mapping, relation);

    // An any-entity end can't have a key per type (decision 43).
    for (const e of entryList)
      if (e.end.anyEntity === true && hasKeysPerType(e))
        fail(
          `End '${str(e.end.role)}' is any entity, so it can't have a key per type: every new entity would need a column. Use a type and an id, or an id alone.`,
          `/ends/${e.index}/types`,
        );

    // Which table holds each link: the link table, the rows of the end with no entry, or a table per type (decision 39).
    type Mode = { kind: "junction" } | { kind: "holder"; holder: number; pointed: Entry } | { kind: "per-type"; typed: Entry; other: Entry } | { kind: "none" };
    let mode: Mode = { kind: "none" };
    if (junction) mode = { kind: "junction" };
    else if (entryList.length === 2) {
      const typed = entryList.filter(hasKeysPerType);
      if (typed.length === 2)
        fail(
          `Both ends of relation '${rname}' have a key per type and there is no 'junctionTable'; a link table per pair of types waits. Name one link table.`,
          "/ends",
        );
      else if (typed.length === 0)
        fail(
          `Every end of relation '${rname}' has an entry and there is no 'junctionTable', so no table holds its links in database '${view.name}'; name a link table, or leave out the entry of the end whose rows hold the links.`,
          "/ends",
        );
      else mode = { kind: "per-type", typed: typed[0], other: entryList.find((e) => e !== typed[0])! };
    } else if (entryList.length === 1) {
      const pointed = entryList[0];
      const holder = 1 - pointed.endIndex;
      mode = { kind: "holder", holder, pointed };
      if (pointed.end.max !== 1) {
        fail(
          `The rows of end '${str(ends[holder].role)}' would hold the links of relation '${rname}' in database '${view.name}', but a row holds one link and end '${str(pointed.end.role)}' is to-many; keep the links in a link table ('junctionTable').`,
          `/ends/${pointed.index}`,
        );
        mode = { kind: "none" };
      }
    }
    if (mode.kind === "none") return finishMaybe();

    // The tables that hold links.
    const holding = new Set<TableView>();
    const note = (t: TableView | undefined) => t && holding.add(t);
    if (mode.kind === "junction") note(junction);
    if (mode.kind === "holder") info[mode.holder].targets.forEach((t) => note(t.table));
    const keyOwners = (e: Entry): TableView[] =>
      [
        ...(typeof e.json.foreignKey === "string" ? [fkOf.get(e.json.foreignKey)?.table] : []),
        ...(Array.isArray(e.json.foreignKeys) ? (e.json.foreignKeys as string[]).map((k) => fkOf.get(k)?.table) : []),
        ...arr(e.json.types).map((t) => fkOf.get(str(t.foreignKey))?.table),
      ].filter((t): t is TableView => !!t);
    if (mode.kind === "per-type") keyOwners(mode.typed).forEach(note);

    for (const e of entryList) {
      const pointed = info[e.endIndex].targets;
      const rows = e.endIndex;
      const at = `/ends/${e.index}`;
      const role = str(e.end.role);
      // One column that points at an end whose holding rows live in several tables.
      const oneColumn = typeof e.json.foreignKey === "string" || typeof e.json.idColumn === "string";
      if (mode.kind === "holder" && mode.pointed === e && oneColumn && holding.size > 1) {
        fail(
          `The targets of end '${str(ends[mode.holder].role)}' live in tables ${joinQuoted([...holding].map((t) => t.name))}, but end '${role}' is pointed at by one column; name a key per table with 'foreignKeys', or keep the links in a link table. Column pointers spread over several tables wait.`,
          at,
        );
        continue;
      }
      // Keys: each leads to the table that holds the target's rows.
      const keyed: { key: string; targets: Target[] }[] = [];
      if (typeof e.json.foreignKey === "string") keyed.push({ key: e.json.foreignKey, targets: pointed });
      if (Array.isArray(e.json.foreignKeys)) (e.json.foreignKeys as string[]).forEach((k) => keyed.push({ key: k, targets: pointed }));
      for (const t of arr(e.json.types))
        if (typeof t.foreignKey === "string") keyed.push({ key: t.foreignKey, targets: pointed.filter((p) => p.entity.id === t.entity) });
      const seenTables = new Map<TableView, string>();
      (Array.isArray(e.json.foreignKeys) ? (e.json.foreignKeys as string[]) : []).forEach((k, i) => {
        const fk = fkOf.get(k);
        if (!fk) return;
        const previous = seenTables.get(fk.table);
        if (previous)
          fail(
            `Foreign keys '${previous}' and '${fk.name}' of end '${role}' both belong to table '${fk.table.name}'; name one key per table that holds links.`,
            `${at}/foreignKeys/${i}`,
          );
        else seenTables.set(fk.table, fk.name);
      });
      for (const { key, targets } of keyed) {
        const fk = fkOf.get(key);
        if (!fk?.refTable) continue;
        const wrong = Array.isArray(e.json.foreignKeys) ? [] : targets.filter((t) => !holdsRows(fk.refTable!, t));
        for (const t of wrong)
          fail(
            `Foreign key '${fk.name}' of table '${fk.table.name}' is named for type '${str(t.entity.name)}' of end '${role}', but it refers to table '${fk.refTable.name}', which doesn't hold ${str(t.entity.name)} rows.`,
            at,
          );
        // A key outside the table that holds the links.
        if (mode.kind === "junction" && junction && fk.table !== junction)
          for (const t of targets.slice(0, 1))
            fail(`Foreign key '${fk.name}' named by end '${role}' isn't in table '${junction.name}', which holds the links of '${str(t.entity.name)}'.`, at);
      }
      // An id column and a type column: in the holding table, matching what they refer to, unique there.
      const idCol = typeof e.json.idColumn === "string" ? columnOf.get(e.json.idColumn) : undefined;
      const typeCol = typeof e.json.typeColumn === "string" ? columnOf.get(e.json.typeColumn) : undefined;
      for (const [col, key] of [
        [idCol, "idColumn"],
        [typeCol, "typeColumn"],
      ] as const) {
        if (!col) continue;
        for (const t of pointed)
          for (const table of holding)
            if (col.table !== table)
              fail(
                `Column '${col.column.name}' named by end '${role}' isn't in table '${table.name}', which holds the links of '${str(t.entity.name)}'.`,
                `${at}/${key}`,
              );
      }
      if (idCol) {
        const items = arr(e.json.types).filter((t) => t.referencesColumn !== undefined);
        const tablesOfTargets = [...new Set(pointed.map((t) => t.table))];
        if (e.json.referencesColumn !== undefined && tablesOfTargets.length > 1) {
          fail(
            `End '${role}' names one 'referencesColumn', but its targets live in tables ${joinQuoted(tablesOfTargets.map((t) => t.name))}; name the column per type in 'types' items.`,
            `${at}/referencesColumn`,
          );
        } else {
          const mismatches: [Target, ColumnView, TableView][] = [];
          for (const t of pointed) {
            const named = items.find((i) => i.entity === t.entity.id)?.referencesColumn ?? e.json.referencesColumn;
            const refColumn = typeof named === "string" ? columnOf.get(named)?.column : undefined;
            const pk = t.table.primaryKey?.columns ?? [];
            const target = refColumn ?? (pk.length === 1 ? t.table.columns.find((c) => c.key === pk[0]) : undefined);
            if (!target) {
              soft(
                `Column '${idCol.column.name}' of table '${idCol.table.name}' refers to table '${t.table.name}', whose primary key isn't one column; name 'referencesColumn'.`,
                at,
              );
              continue;
            }
            if (!sameType(idCol.column, target)) mismatches.push([t, target, t.table]);
            else if (!isUnique(t.table, target.key)) {
              unlinkable.add(t.entity);
              soft(
                `Column '${idCol.column.name}' of table '${idCol.table.name}' refers to column '${target.name}' of table '${t.table.name}', which is neither its primary key nor a unique key.`,
                at,
              );
            }
          }
          for (const [, target, table] of mismatches)
            fail(
              `Column '${idCol.column.name}' of table '${idCol.table.name}' is ${describe(idCol.column)}, but it refers to column '${target.name}' of table '${table.name}', which is ${describe(target)}; make the types match.`,
              `${at}/idColumn`,
            );
        }
      }
      // The codes a type column holds (decision 42).
      if (typeCol) {
        const codes = placedEntities(e.end).map((en) => ({ name: str(en.name), code: codeOf(en) }));
        codes.forEach((a, i) => {
          const dup = codes.slice(0, i).find((b) => b.code === a.code);
          if (dup)
            soft(
              `Types '${dup.name}' and '${a.name}' both have code '${a.code}' in database '${view.name}', so column '${typeCol.column.name}' can't tell them apart; set discriminatorValue on one.`,
              `${at}/typeColumn`,
            );
        });
        for (const { code } of codes) {
          const c = typeCol.column;
          const where = `Column '${c.name}' of table '${typeCol.table.name}' holds the type codes of end '${role}', but code '${code}'`;
          if (INTEGERS.has(c.type)) {
            if (!/^-?\d+$/.test(code)) soft(`${where} isn't an integer; set an integer discriminatorValue.`, `${at}/typeColumn`);
          } else if (TEXTS.has(c.type)) {
            if (c.length !== null && code.length > c.length)
              soft(`${where} is longer than its ${c.length} characters; shorten the code or the column.`, `${at}/typeColumn`);
          } else soft(`${where} can't be held: the column is ${c.type}, neither text nor an integer.`, `${at}/typeColumn`);
        }
      }
      void rows;
    }

    // A placed target that no pointer reaches (MQ4120, decision 49).
    if (!hard) {
      const unreached = (e: Entry, targets: Target[], covers: (t: Target) => boolean) => {
        for (const t of targets)
          if (!covers(t)) {
            item.notStored.push({ endId: str(ends[e.endIndex].id), entityId: str(t.entity.id) });
            const name = str(t.entity.name);
            diag(
              "MQ4120",
              "warning",
              `Relation '${rname}' covers entity '${name}' in database '${view.name}', but its mapping names nothing that points at it there, so links to ${/^[AEIOUaeiou]/.test(name) ? "an" : "a"} ${name} aren't stored.`,
              mapping,
              `/ends/${e.index}`,
            );
          }
      };
      for (const e of entryList) {
        const items = arr(e.json.types).filter((t) => t.foreignKey !== undefined || t.referencesColumn !== undefined);
        if (items.length) unreached(e, info[e.endIndex].targets, (t) => items.some((i) => i.entity === t.entity.id));
        if (Array.isArray(e.json.foreignKeys) && mode.kind === "holder" && mode.pointed === e) {
          const owners = new Set((e.json.foreignKeys as string[]).map((k) => fkOf.get(k)?.table));
          const holderEnd = ends[mode.holder];
          for (const t of info[mode.holder].targets)
            if (!owners.has(t.table)) {
              item.notStored.push({ endId: str(holderEnd.id), entityId: str(t.entity.id) });
              const name = str(t.entity.name);
              diag(
                "MQ4120",
                "warning",
                `Relation '${rname}' covers entity '${name}' in database '${view.name}', but its mapping names nothing that points at it there, so links to ${/^[AEIOUaeiou]/.test(name) ? "an" : "a"} ${name} aren't stored.`,
                mapping,
                `/ends/${e.index}`,
              );
            }
        }
      }
    }

    // Attribute columns (decision 45): one table must hold every link.
    arr(mapping.attributes).forEach((a, i) => {
      if (typeof a.column !== "string") return;
      const attribute = arr(relation.attributes).find((x) => x.id === a.attribute);
      const aname = str(attribute?.name);
      const column = columnOf.get(a.column);
      if (!column) return;
      if (holding.size > 1)
        fail(
          `The links of relation '${rname}' live in tables ${joinQuoted([...holding].map((t) => t.name))}, so one column can't hold attribute '${aname}'; a column per table waits.`,
          `/attributes/${i}/column`,
        );
      else if (!junction && holding.size === 1 && ![...holding].includes(column.table))
        soft(
          `Column '${column.column.name}' for attribute '${aname}' isn't in table '${[...holding][0].name}', which holds the links of relation '${rname}'.`,
          `/attributes/${i}/column`,
        );
    });

    item.tables = [...holding].map((t) => t.key);
    if (!hard) gapsOf();
    return;

    function finishMaybe() {
      /* nothing is stored: no gaps, no unreached targets */
    }

    function holdsRows(table: TableView, t: Target): boolean {
      if (t.table === table) return true;
      return chainOf(str(t.entity.id))
        .slice(1)
        .some((x) => placementOf(x) === table);
    }
    function isUnique(table: TableView, column: string): boolean {
      return uniqueExists(table, [column]);
    }

    /** What the database can't enforce (decision 47). */
    function gapsOf() {
      const unique = (t: TableView, columns: string[], name: string) =>
        dialect === "sqlserver"
          ? `CREATE UNIQUE INDEX ${quote(name)} ON ${quote(t.name)} (${columns.map((c) => quote(nameOfColumn(c))).join(", ")}) WHERE ${columns.map((c) => `${quote(nameOfColumn(c))} IS NOT NULL`).join(" AND ")}`
          : `CONSTRAINT ${quote(name)} UNIQUE (${columns.map((c) => quote(nameOfColumn(c))).join(", ")})`;
      const nameOfColumn = (key: string) => columnOf.get(key)?.column.name ?? key;
      const uniqueName = (t: TableView, columns: string[]) => `uq_${t.name}_${columns.map(nameOfColumn).join("_")}`;
      const oneToOne = ends.every((e) => e.max === 1);
      type Phrase = { order: number; text: string; kind: RelationStorageKind; copy?: { purpose: string; sql: string } };
      const perEntry = new Map<Entry, { how: string; tables: TableView[]; phrases: Phrase[] }>();
      for (const e of entryList) {
        const pointed = info[e.endIndex].targets;
        const several = docEndHasSeveralTypes(e.end as EndDocLike);
        const idCol = typeof e.json.idColumn === "string" ? columnOf.get(e.json.idColumn) : undefined;
        const typeCol = typeof e.json.typeColumn === "string" ? columnOf.get(e.json.typeColumn) : undefined;
        const phrases: Phrase[] = [];
        let how = "";
        let tables: TableView[] = [];
        const names = pointed.filter((t) => !unlinkable.has(t.entity)).map((t) => str(t.entity.name));
        if (idCol) {
          how = typeCol ? "a type and an id" : "an id";
          tables = [idCol.table];
          phrases.push({ order: 0, kind: "no-foreign-key", text: `it can't refuse a link to a missing ${orWords(names)}` });
          if (!typeCol) phrases.push({ order: 1, kind: "id-only", text: "it can't check the type an id points at" });
          else if (!checkNames(idCol.table, [typeCol.column])) {
            const codes = [...new Set(placedEntities(e.end).map((en) => codeOf(en)))].sort();
            const name = `ck_${typeCol.table.name}_${snake(str(e.end.role))}`;
            phrases.push({
              order: 2,
              kind: "type-check",
              text: `it finds no check that names column '${typeCol.column.name}' of table '${typeCol.table.name}'`,
              copy: {
                purpose: "check the codes",
                sql: `CONSTRAINT ${quote(name)} CHECK (${quote(typeCol.column.name)} IN (${codes.map((c) => literal(c, typeCol.column)).join(", ")}))`,
              },
            });
          }
          if (!junction && oneToOne) {
            const cols = [...(typeCol ? [typeCol.column.key] : []), idCol.column.key];
            if (!uniqueExists(idCol.table, cols))
              phrases.push({
                order: 3,
                kind: "one-to-one-unique",
                text: `it finds no unique on ${joinQuoted(cols.map(nameOfColumn))} of table '${idCol.table.name}', so two rows can link to one object`,
                copy: { purpose: "keep the links one to one", sql: unique(idCol.table, cols, uniqueName(idCol.table, cols)) },
              });
          }
        } else if (typeof e.json.foreignKey === "string") {
          const fk = fkOf.get(e.json.foreignKey);
          how = "a key";
          tables = fk ? [fk.table] : [];
          if (fk?.refTable && several)
            phrases.push({
              order: 0,
              kind: "shared-table",
              text: `it can't refuse a key to a row of table '${fk.refTable.name}' whose type end '${str(e.end.role)}' doesn't cover`,
            });
          if (fk && !junction && oneToOne && !uniqueExists(fk.table, fk.columns))
            phrases.push({
              order: 3,
              kind: "one-to-one-unique",
              text: `it finds no unique on ${joinQuoted(fk.columns.map(nameOfColumn))} of table '${fk.table.name}', so two rows can link to one object`,
              copy: { purpose: "keep the links one to one", sql: unique(fk.table, fk.columns, uniqueName(fk.table, fk.columns)) },
            });
        } else if (arr(e.json.types).some((t) => t.foreignKey !== undefined)) {
          how = "a key per type";
          const keys = arr(e.json.types)
            .map((t) => fkOf.get(str(t.foreignKey)))
            .filter((k): k is NonNullable<typeof k> => !!k);
          tables = [...new Set(keys.map((k) => k.table))];
          if (tables.length === 1 && keys.length > 1) {
            const columns = keys.flatMap((k) => k.columns);
            const cols = columns.map((c) => columnOf.get(c)!.column);
            if (!checkNames(tables[0], cols) || !cols.every((c) => checkNames(tables[0], [c])))
              phrases.push({
                order: 2,
                kind: "count-check",
                text: `it finds no check on how many of columns ${joinQuoted(cols.map((c) => c.name))} of table '${tables[0].name}' are set`,
                copy: {
                  purpose: "check how many are set",
                  sql: `CONSTRAINT ${quote(`ck_${tables[0].name}_${snake(str(e.end.role))}`)} CHECK ((${cols
                    .map((c) => `CASE WHEN ${quote(c.name)} IS NOT NULL THEN 1 ELSE 0 END`)
                    .join(" + ")}) <= 1)`,
                },
              });
            if (junction)
              phrases.push({
                order: 3,
                kind: "per-type-duplicates",
                text: `it can't refuse duplicate links in table '${tables[0].name}', since a unique over nullable key columns doesn't stop them in most databases`,
              });
          } else if (mode.kind === "per-type" && mode.typed === e) {
            const other = mode.other;
            const otherKeys = (Array.isArray(other.json.foreignKeys) ? (other.json.foreignKeys as string[]) : []).map((k) => fkOf.get(k));
            for (const k of keys) {
              const partner = otherKeys.find((o) => o?.table === k.table);
              const cols = [...(partner?.columns ?? []), ...k.columns];
              if (!uniqueExists(k.table, cols))
                phrases.push({
                  order: 3,
                  kind: "link-unique",
                  text: `it finds no unique over columns ${joinQuoted(cols.map(nameOfColumn))} of table '${k.table.name}', so it can't refuse a duplicate link`,
                  copy: { purpose: "refuse duplicate links", sql: unique(k.table, cols, uniqueName(k.table, cols)) },
                });
            }
          }
        }
        // Rows of other entities in a target's table that nothing tells apart.
        if (how)
          for (const t of pointed) {
            const others = all.some(
              (d) =>
                d.kind === "entity" &&
                d.id !== t.entity.id &&
                placementOf(str(d.id)) === t.table &&
                !chainOf(str(d.id)).includes(str(t.entity.id)) &&
                !chainOf(str(t.entity.id)).includes(str(d.id)),
            );
            if (others)
              phrases.push({
                order: 4,
                kind: "rows-not-told-apart",
                text: `nothing tells the rows of '${str(t.entity.name)}' apart from the other rows of table '${t.table.name}'`,
              });
          }
        if (how) perEntry.set(e, { how, tables, phrases });
      }
      // One unique over both ends' pointers in a link table that holds a type or an id (decision 47).
      if (junction && !entryList.some(hasKeysPerType) && entryList.every((e) => perEntry.has(e))) {
        const columns = ends.flatMap((end) => {
          const e = entryFor(ends.indexOf(end));
          if (!e) return [];
          if (typeof e.json.foreignKey === "string") return fkOf.get(e.json.foreignKey)?.columns ?? [];
          return [str(e.json.typeColumn), str(e.json.idColumn)].filter(Boolean);
        });
        const all2 = [...columns, ...arr(mapping.constants).map((c) => str(c.column))].filter(Boolean);
        const first = entryList.find((e) => (perEntry.get(e)?.phrases.length ?? 0) > 0);
        if (first && entryList.some((e) => typeof e.json.idColumn === "string") && !uniqueExists(junction, all2))
          perEntry.get(first)!.phrases.push({
            order: 3,
            kind: "link-unique",
            text: `it finds no unique over columns ${joinQuoted(all2.map(nameOfColumn))} of table '${junction.name}', so it can't refuse a duplicate link`,
            copy: { purpose: "refuse duplicate links", sql: unique(junction, all2, uniqueName(junction, all2)) },
          });
      }
      for (const e of entryList) {
        const p = perEntry.get(e);
        if (!p?.phrases.length) continue;
        const phrases = [...p.phrases].sort((a, b) => a.order - b.order);
        const where = p.tables.length === 1 ? `table '${p.tables[0].name}'` : `tables ${joinQuoted(p.tables.map((t) => t.name))}`;
        const copies = phrases.filter((x) => x.copy).map((x) => `To ${x.copy!.purpose}, add: ${x.copy!.sql}`);
        const message = `Database '${view.name}' holds end '${str(e.end.role)}' of relation '${rname}' as ${p.how} in ${where}, so ${phrases.map((x) => x.text).join("; and ")}.${copies.length ? ` ${copies.join(" ")}` : ""}`;
        diag("MQ4118", "info", message, mapping, `/ends/${e.index}`);
        for (const x of phrases) item.gaps.push({ endId: str(e.end.id), kind: x.kind, ...(x.copy ? { text: x.copy.sql } : {}) });
      }
    }
  }
}
