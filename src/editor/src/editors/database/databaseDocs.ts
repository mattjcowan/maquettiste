// The edits the table, view and sequence editors make to their documents (schemas/v1/table.json, view.json, sequence.json),
// pure: each mutates a copy of the document the draft manager hands it, and the editor saves it at once, so each gesture is one
// save and one undo step. A designed table's columns, keys and constraints name its columns by id; every list the schema says
// holds at least one column keeps one (removing a column drops a key or index left without columns).
type Json = Record<string, unknown>;

const list = (doc: Json, member: string): Json[] => (Array.isArray(doc[member]) ? (doc[member] as Json[]) : []);

/** Sets an array member, or removes it when empty (canonical files leave out empty defaults). */
function setList(doc: Json, member: string, items: Json[]): void {
  if (items.length) doc[member] = items;
  else delete doc[member];
}

/** A designed table's columns as id and name. */
export function tableColumns(doc: Json | undefined): { id: string; name: string }[] {
  return list(doc ?? {}, "columns")
    .filter((c) => typeof c.id === "string")
    .map((c) => ({ id: String(c.id), name: String(c.name ?? "") }));
}

/** A name not yet used by a column: `column_1`, `column_2`, ... */
export function nextColumnName(names: readonly string[], base = "column"): string {
  const taken = new Set(names.map((n) => n.toLowerCase()));
  for (let n = 1; ; n++) if (!taken.has(`${base}_${n}`)) return `${base}_${n}`;
}

/** Adds a nullable string column at the end of a designed table; returns its id. */
export function addTableColumn(doc: Json, newId: () => string): string {
  const id = newId();
  const columns = list(doc, "columns");
  doc.columns = [...columns, { id, name: nextColumnName(columns.map((c) => String(c.name ?? ""))), type: "string" }];
  return id;
}

/** Removes a designed table's column, and the column from its keys, uniques, indexes and foreign keys (a list left empty goes). */
export function deleteTableColumn(doc: Json, columnId: string): boolean {
  const columns = list(doc, "columns");
  if (!columns.some((c) => c.id === columnId)) return false;
  setList(
    doc,
    "columns",
    columns.filter((c) => c.id !== columnId),
  );
  const pk = doc.primaryKey as Json | undefined;
  if (pk && Array.isArray(pk.columns)) {
    const kept = (pk.columns as string[]).filter((c) => c !== columnId);
    if (kept.length) pk.columns = kept;
    else delete doc.primaryKey;
  }
  for (const member of ["uniques", "foreignKeys"]) {
    setList(
      doc,
      member,
      list(doc, member)
        .map((e) => ({ ...e, columns: ((e.columns as string[] | undefined) ?? []).filter((c) => c !== columnId) }))
        .filter((e) => e.columns.length > 0)
        .map((e) => (member === "foreignKeys" ? trimReferenced(e) : e)),
    );
  }
  setList(
    doc,
    "indexes",
    list(doc, "indexes")
      .map((e) => ({
        ...e,
        columns: ((e.columns as Json[] | undefined) ?? []).filter((c) => c.column !== columnId),
        ...(Array.isArray(e.include) ? { include: (e.include as string[]).filter((c) => c !== columnId) } : {}),
      }))
      .filter((e) => e.columns.length > 0)
      .map((e) => (Array.isArray(e.include) && !e.include.length ? withoutKey(e, "include") : e)),
  );
  return true;
}

const withoutKey = (e: Json, key: string): Json => Object.fromEntries(Object.entries(e).filter(([k]) => k !== key));

/** A foreign key's referenced columns follow its own column count when it names them. */
function trimReferenced(fk: Json): Json {
  const refs = (fk.referencesColumns as string[] | undefined) ?? [];
  const own = (fk.columns as string[]).length;
  return refs.length > own ? { ...fk, referencesColumns: refs.slice(0, own) } : fk;
}

/** The column a new key starts on: one named id, else the first. */
function firstKeyColumn(doc: Json): string | null {
  const columns = tableColumns(doc);
  return (columns.find((c) => c.name.toLowerCase() === "id") ?? columns[0])?.id ?? null;
}

/** The table's sections that hold keys and constraints, as the Keys tab shows them. */
export type KeySection = "primaryKey" | "uniques" | "indexes" | "foreignKeys";

/**
 * Adds an entry to a section, valid as added (a key needs a column): the primary key on the id column (or the first), a
 * unique or an index on the first column, a foreign key from the first column to `referencesTable` (its primary key). False
 * when the table has no column yet, or already has a primary key.
 */
export function addKeyEntry(doc: Json, section: KeySection, newId: () => string, referencesTable?: string): boolean {
  const first = firstKeyColumn(doc);
  if (!first) return false;
  switch (section) {
    case "primaryKey":
      if (doc.primaryKey) return false;
      doc.primaryKey = { columns: [first] };
      return true;
    case "uniques":
      doc.uniques = [...list(doc, "uniques"), { id: newId(), columns: [tableColumns(doc)[0].id] }];
      return true;
    case "indexes":
      doc.indexes = [...list(doc, "indexes"), { id: newId(), columns: [{ column: tableColumns(doc)[0].id }] }];
      return true;
    case "foreignKeys":
      if (!referencesTable) return false;
      doc.foreignKeys = [...list(doc, "foreignKeys"), { id: newId(), columns: [tableColumns(doc)[0].id], referencesTable }];
      return true;
  }
}

/** Removes an entry (the primary key, or the n-th unique, index or foreign key). */
export function removeKeyEntry(doc: Json, section: KeySection, index = 0): void {
  if (section === "primaryKey") {
    delete doc.primaryKey;
    return;
  }
  setList(
    doc,
    section,
    list(doc, section).filter((_, i) => i !== index),
  );
}

/** The entry of a section the Keys tab edits: the primary key object, or the n-th entry of a list. */
export function keyEntry(doc: Json, section: KeySection, index = 0): Json | undefined {
  return section === "primaryKey" ? (doc.primaryKey as Json | undefined) : list(doc, section)[index];
}

/**
 * A column list with one column added (at the end) or removed; null when removing would leave it empty (the schema needs one).
 */
export function toggleColumn(columns: readonly string[], column: string): string[] | null {
  if (!columns.includes(column)) return [...columns, column];
  const next = columns.filter((c) => c !== column);
  return next.length ? next : null;
}

/** Sets an optional member of an entry: an empty text, false or undefined removes it. */
export function setEntryMember(entry: Json, member: string, value: unknown): void {
  if (value === undefined || value === "" || value === false || value === null) delete entry[member];
  else entry[member] = value;
}

// ------------------------------------------------------------------ views

/** The dialects a view's body holds, in the order of the document. */
export function viewDialects(doc: Json | undefined): string[] {
  const body = doc?.body;
  return body && typeof body === "object" ? Object.keys(body as Json) : [];
}

/** Sets a view's body for a dialect. */
export function setViewBody(doc: Json, dialect: string, text: string): void {
  doc.body = { ...((doc.body as Json | undefined) ?? {}), [dialect]: text };
}

/** Removes a dialect's body; false for the last one (a view needs a body). */
export function removeViewDialect(doc: Json, dialect: string): boolean {
  const body = { ...((doc.body as Json | undefined) ?? {}) };
  if (!(dialect in body) || Object.keys(body).length <= 1) return false;
  delete body[dialect];
  doc.body = body;
  return true;
}

/** Adds a declared view column (named, nullable, no type). */
export function addViewColumn(doc: Json): void {
  const columns = list(doc, "columns");
  doc.columns = [...columns, { name: nextColumnName(columns.map((c) => String(c.name ?? ""))) }];
}

export function removeViewColumn(doc: Json, index: number): void {
  setList(
    doc,
    "columns",
    list(doc, "columns").filter((_, i) => i !== index),
  );
}

// ------------------------------------------------------------------ sequences

export type SequenceNumber = "start" | "increment" | "min" | "max" | "cache";

/** Why a typed value cannot be a sequence's number, or null (empty clears it: the default, or none). */
export function sequenceNumberProblem(field: SequenceNumber, raw: string): string | null {
  const text = raw.trim();
  if (text === "") return null;
  if (!/^-?\d+$/.test(text)) return "A whole number, or empty.";
  const n = Number(text);
  if (field === "increment" && n === 0) return "The increment cannot be 0.";
  if (field === "cache" && n < 1) return "The cache is 1 or more.";
  return null;
}

/** Sets a sequence's number (empty, or start and increment at their default 1, removes it). False when the value cannot be one. */
export function setSequenceNumber(doc: Json, field: SequenceNumber, raw: string): boolean {
  if (sequenceNumberProblem(field, raw)) return false;
  const text = raw.trim();
  if (text === "" || ((field === "start" || field === "increment") && Number(text) === 1)) delete doc[field];
  else doc[field] = Number(text);
  return true;
}
