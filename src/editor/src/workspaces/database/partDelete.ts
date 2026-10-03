// What deleting a part of a table takes with it, pure (the delete plan the PartDeleteDialog shows, then writes in one batch and
// one undo step). As a database manager does: a column goes with every constraint and index that holds it, whole (a composite
// primary key, unique constraint, foreign key or index, an index that includes it or filters on it, a check on it or whose
// expression reads it), the table's own foreign keys to itself that reference it, and, beyond the table, what would be left
// pointing at nothing: other tables' foreign keys to a key that goes (a key that loses a column goes whole, and a foreign key
// without referenced columns references the primary key; a table not stored as a file yet is among them, stored first by the
// dialog), and the storage bindings that read the column or name a foreign key that goes (counted, never named: the database
// side knows no entities). A query whose references resolve to the column (its alias's source is the table, by id or by name,
// queryColumns.ts) keeps the delete from being saved (it would not resolve); a query that may read it (a bare reference, an SQL
// expression), a view's body, a computed column, a routine or an SQL object naming it is a place to check.
import type { TableView } from "@/api/types";
import { clone } from "@/lib/json";
import { isKeyOf, keysOfDocument } from "@/model/foreignKeyTarget";
import { queryReadsColumn, queryTable } from "./queryColumns";
import { mapMentions, mapSelectsAll, mentions } from "./sqlIdentifiers";
import { findPart, partEntries, partName, PART_LABELS, type ListPartKind, type TablePart } from "./tableParts";

type Json = Record<string, unknown>;

const list = (doc: Json, member: string): Json[] => (Array.isArray(doc[member]) ? (doc[member] as Json[]) : []);
const ids = (v: unknown): string[] => (Array.isArray(v) ? (v as unknown[]).map(String) : []);
const setList = (doc: Json, member: string, items: Json[]) => {
  if (items.length) doc[member] = items;
  else delete doc[member];
};

/** One document the delete changes beyond the table: what it loses, in words. */
export interface PlanDocument {
  id: string;
  kind: string;
  name: string;
  lines: string[];
}

export interface PartDeletePlan {
  /** The part, in words ("column email", "primary key pk_customers"). */
  subject: string;
  /** What else goes from the table itself (keys, constraints, indexes, checks). */
  table: string[];
  /** The other tables it changes. */
  others: PlanDocument[];
  /** The documents whose storage bindings follow (an entity's binding, a relationship's mapping): counted, never named. */
  storage: PlanDocument[];
  /** What keeps it from being saved. */
  blockers: string[];
  /** Places to check by hand afterwards. */
  warnings: string[];
  /** The changes, by document id (the table's included): each edits a copy of the document as read. */
  edits: Map<string, (doc: Json) => void>;
}

/** Whether the plan takes anything beyond the part itself (then the dialog shows it before the delete). */
export const planReachesFurther = (plan: PartDeletePlan): boolean =>
  plan.table.length > 0 || plan.others.length > 0 || plan.storage.length > 0 || plan.blockers.length > 0 || plan.warnings.length > 0;

const nameOf = (entry: Json, fallback: string) => String(entry.name ?? entry.id ?? fallback);

/**
 * The plan of deleting `part` of `table` (its document: a table file, or a laid-out table's resolved document, its columns by
 * their keys). `docs` are the other documents of the database to look through (table files, entities, relation mappings,
 * queries, views); `view` names a laid-out table's parts by their resolved names.
 */
export function planPartDelete(input: { table: Json; part: TablePart; view?: TableView | null; docs: readonly Json[]; dialect: string }): PartDeletePlan {
  const { part, view, docs, dialect } = input;
  const before = input.table;
  const tableId = String(before.id ?? "");
  const tableName = String(before.name ?? view?.name ?? "the table");
  const doc = clone(before);
  const lines: string[] = [];
  const removedFks = new Map<string, string>(); // id → name, of the table's own keys that go
  const columns = list(doc, "columns");
  const column = part.kind === "column" ? columns.find((c) => c.id === part.id) : undefined;
  const columnName = column ? String(column.name ?? part.id) : "";
  const subject = `${PART_LABELS[part.kind]} ${partName(before, part, view)}`;
  const warnings: string[] = [];
  const blockers: string[] = [];
  const pkBefore = before.primaryKey ? ids((before.primaryKey as Json).columns) : null;
  const keysBefore = keysOfDocument(before);

  const dropList = (member: string, label: string, gone: (e: Json) => string | null) => {
    const kept: Json[] = [];
    for (const e of list(doc, member)) {
      const why = gone(e);
      if (why === null) kept.push(e);
      else {
        if (why) lines.push(`${label} ${nameOf(e, label)} ${why}`);
        if (member === "foreignKeys") removedFks.set(String(e.id ?? ""), nameOf(e, "foreign key"));
      }
    }
    setList(doc, member, kept);
  };

  if (part.kind === "column") {
    if (!column)
      return { subject, table: [], others: [], storage: [], blockers: [`Column ${part.id} is not in ${tableName}.`], warnings: [], edits: new Map() };
    const col = part.id;
    setList(
      doc,
      "columns",
      columns.filter((c) => c.id !== col),
    );
    if (pkBefore?.includes(col)) {
      lines.push(`primary key ${String((before.primaryKey as Json).name ?? view?.primaryKey?.name ?? "")} (${columnName} is in it)`.replace("key  (", "key ("));
      delete doc.primaryKey;
    }
    dropList("uniques", "unique constraint", (u) => (ids(u.columns).includes(col) ? `(${columnName} is in it)` : null));
    dropList("indexes", "index", (ix) =>
      list(ix, "columns").some((c) => c.column === col) || ids(ix.include).includes(col)
        ? `(${columnName} is in it)`
        : typeof ix.where === "string" && mentions(ix.where, columnName)
          ? `(its filter reads ${columnName})`
          : list(ix, "columns").some(
                (c) =>
                  !!c.expression &&
                  typeof c.expression === "object" &&
                  Object.values(c.expression as Json).some((t) => typeof t === "string" && mentions(t, columnName)),
              )
            ? `(an expression of it reads ${columnName})`
            : null,
    );
    dropList("foreignKeys", "foreign key", (fk) => {
      if (ids(fk.columns).includes(col)) return `(${columnName} is in it)`;
      if (fk.referencesTable === tableId) {
        const refs = ids(fk.referencesColumns);
        if (refs.includes(col) || (!refs.length && pkBefore?.includes(col))) return `(it references ${columnName})`;
      }
      return null;
    });
    dropList("checks", "check", (ck) =>
      ck.column === col ? `(it checks ${columnName})` : !ck.column && mapMentions(ck.expression, columnName) ? `(its expression reads ${columnName})` : null,
    );
    for (const c of list(doc, "columns")) {
      if (typeof c.computed === "string" && mentions(c.computed, columnName))
        warnings.push(`Column ${String(c.name)} of ${tableName} is computed from ${columnName}: change its expression.`);
      if (mapMentions(c.defaultSql, columnName)) warnings.push(`The default of column ${String(c.name)} of ${tableName} reads ${columnName}: change it.`);
    }
  } else if (part.kind === "primary-key") {
    if (!doc.primaryKey) return { subject, table: [], others: [], storage: [], blockers: [`${tableName} has no primary key.`], warnings: [], edits: new Map() };
    delete doc.primaryKey;
  } else {
    const kind = part.kind as ListPartKind;
    const at = findPart(doc, view, kind, part.id);
    if (at < 0) return { subject, table: [], others: [], storage: [], blockers: [`${subject} is not on ${tableName}.`], warnings: [], edits: new Map() };
    const entries = partEntries(doc, kind);
    const gone = entries[at];
    if (kind === "foreign-key") removedFks.set(String(gone.id ?? ""), partName(before, part, view));
    setList(
      doc,
      { unique: "uniques", index: "indexes", "foreign-key": "foreignKeys", check: "checks" }[kind],
      entries.filter((_, i) => i !== at),
    );
  }

  // The keys the table had that it no longer has: what other tables' foreign keys may not reference any more.
  // A foreign key without referenced columns references the primary key itself: it loses its target when that changes.
  const keysAfter = keysOfDocument(doc);
  const pkAfter = keysAfter.primaryKey;
  const lostKey = (refs: string[]): boolean => {
    if (!refs.length) return !!pkBefore && !(pkAfter && pkAfter.length === pkBefore.length && pkBefore.every((c) => pkAfter.includes(c)));
    if (part.kind === "column" && refs.includes(part.id)) return true;
    return isKeyOf(keysBefore, refs, dialect) && !isKeyOf(keysAfter, refs, dialect);
  };
  // The table's own remaining foreign keys to itself.
  dropList("foreignKeys", "foreign key", (fk) =>
    fk.referencesTable === tableId && lostKey(ids(fk.referencesColumns)) ? "(the key it references goes)" : null,
  );

  const edits = new Map<string, (d: Json) => void>();
  const after = doc;
  edits.set(tableId, (d) => {
    for (const k of Object.keys(d)) delete d[k];
    Object.assign(d, clone(after));
  });

  const others: PlanDocument[] = [];
  const storage: PlanDocument[] = [];
  const add = (target: Json, change: (d: Json) => string[], into = others) => {
    const probe = clone(target);
    const said = change(probe);
    if (!said.length) return;
    const id = String(target.id);
    // A storage binding's document is said by its kind alone (the dialog lists its file), never by its name.
    into.push({ id, kind: String(target.kind), name: into === storage ? "" : String(target.displayName ?? target.name ?? id), lines: said });
    edits.set(id, (d) => void change(d));
  };

  // Other tables' foreign keys to a key that goes.
  for (const other of docs.filter((d) => d.kind === "table" && d.id !== tableId)) {
    add(other, (d) => {
      const said: string[] = [];
      const kept = list(d, "foreignKeys").filter((fk) => {
        if (fk.referencesTable !== tableId || !lostKey(ids(fk.referencesColumns))) return true;
        said.push(
          `foreign key ${nameOf(fk, "")} goes (it references ${tableName}${part.kind === "column" ? `.${columnName}` : `'s ${PART_LABELS[part.kind]}`})`,
        );
        removedFks.set(String(fk.id ?? ""), nameOf(fk, "foreign key"));
        return false;
      });
      if (said.length) setList(d, "foreignKeys", kept);
      return said;
    });
  }

  // Relation mappings naming a foreign key that goes.
  for (const mapping of docs.filter((d) => d.kind === "mapping")) {
    add(
      mapping,
      (d) => {
        const said: string[] = [];
        if (typeof d.foreignKey === "string" && removedFks.has(d.foreignKey)) {
          said.push(`no longer names foreign key ${removedFks.get(d.foreignKey)}`);
          delete d.foreignKey;
        }
        const ends = list(d, "ends");
        const keptEnds = ends.filter((e) => !(typeof e.foreignKey === "string" && removedFks.has(e.foreignKey)));
        if (keptEnds.length !== ends.length) {
          said.push(`its ends no longer name foreign keys that go`);
          setList(d, "ends", keptEnds);
        }
        return said;
      },
      storage,
    );
  }

  if (part.kind === "column") {
    const col = part.id;
    const reads = (v: unknown) => v === col || (typeof v === "string" && v.toLowerCase() === columnName.toLowerCase());
    // Storage bindings that read or write the table: what names the column goes.
    for (const entity of docs.filter((d) => Array.isArray(d.bindings))) {
      add(
        entity,
        (d) => {
          const said: string[] = [];
          for (const b of list(d, "bindings")) {
            const write = b.write as Json | string | undefined;
            const usesTable = b.source === tableId || (typeof write === "object" && write?.table === tableId);
            if (!usesTable) continue;
            const fields = list(b, "fields");
            const read = fields.filter((f) => reads(f.column)).length;
            if (read) said.push(read === 1 ? `a field no longer reads ${columnName}` : `${read} fields no longer read ${columnName}`);
            setList(
              b,
              "fields",
              fields.filter((f) => !reads(f.column)),
            );
            const constants = list(b, "constants");
            if (constants.some((c) => reads(c.column))) said.push(`its constant on ${columnName} goes`);
            setList(
              b,
              "constants",
              constants.filter((c) => !reads(c.column)),
            );
            const listed = list(b, "columns");
            if (listed.some((c) => reads(c.column))) said.push(`${columnName} is no longer listed`);
            setList(
              b,
              "columns",
              listed.filter((c) => !reads(c.column)),
            );
            const del = b.delete as Json | string | undefined;
            if (typeof del === "object" && reads((del?.soft as Json | undefined)?.column)) {
              said.push(`its soft delete on ${columnName} goes (it deletes by key)`);
              delete b.delete;
            }
          }
          return said;
        },
        storage,
      );
    }
    // Queries: a reference that resolves to the column blocks the delete (the query would no longer resolve); one that may, or
    // an SQL expression that names it, is to check.
    const target = queryTable(before, docs);
    for (const query of docs.filter((d) => d.kind === "query")) {
      const name = String(query.name ?? query.id);
      const use = queryReadsColumn(query, target, col, columnName, dialect);
      if (use.reads) blockers.push(`Query ${name} reads ${tableName}.${columnName}: change it first (it would no longer resolve).`);
      else if (use.maybe) warnings.push(`Query ${name} may read ${columnName} (a reference without an alias): check it.`);
      if (use.sql) warnings.push(`Query ${name} names ${columnName} in an SQL expression: check it.`);
    }
    // Views whose body names the column, or returns * from the table: SQL text, checked by hand.
    for (const v of docs.filter((d) => d.kind === "view")) {
      if (!mapMentions(v.body, tableName, dialect)) continue;
      if (mapMentions(v.body, columnName, dialect)) warnings.push(`View ${String(v.name ?? v.id)} names ${columnName} in its body: check it.`);
      else if (v.columnList !== true && mapSelectsAll(v.body, dialect))
        warnings.push(`View ${String(v.name ?? v.id)} returns * from ${tableName}: its column ${columnName} goes; check what reads it.`);
    }
    // Routines and SQL objects whose text names the column: checked by hand.
    for (const r of docs.filter((d) => d.kind === "routine" || d.kind === "sql-object")) {
      if (mapMentions(r.body, columnName, dialect))
        warnings.push(`${r.kind === "routine" ? "Routine" : "SQL object"} ${String(r.name ?? r.id)} names ${columnName} in its text: check it.`);
    }
  }

  return { subject, table: lines, others, storage, blockers, warnings, edits };
}
