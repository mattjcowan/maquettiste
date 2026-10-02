// The edits the database object editors make to their documents (schemas/v1/table.json, view.json, sequence.json, routine.json,
// database-type.json, sql-object.json),
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

/** The dialects a per-dialect text member (a view's, routine's or SQL object's `body`, a database type's `definition`) holds,
 * in the order of the document. */
export function dialectKeys(doc: Json | undefined, member = "body"): string[] {
  const map = doc?.[member];
  return map && typeof map === "object" ? Object.keys(map as Json) : [];
}

/** Sets the text of a per-dialect member for a dialect. */
export function setDialectText(doc: Json, member: string, dialect: string, text: string): void {
  doc[member] = { ...((doc[member] as Json | undefined) ?? {}), [dialect]: text };
}

/** Removes a dialect's text; false for the last one when `keepOne` (a body is required), else the member goes when empty. */
export function removeDialectText(doc: Json, member: string, dialect: string, keepOne: boolean): boolean {
  const map = { ...((doc[member] as Json | undefined) ?? {}) };
  if (!(dialect in map) || (keepOne && Object.keys(map).length <= 1)) return false;
  delete map[dialect];
  if (Object.keys(map).length) doc[member] = map;
  else delete doc[member];
  return true;
}

/** The dialects a view's body holds, in the order of the document. */
export const viewDialects = (doc: Json | undefined): string[] => dialectKeys(doc, "body");

/** Sets a view's body for a dialect. */
export const setViewBody = (doc: Json, dialect: string, text: string): void => setDialectText(doc, "body", dialect, text);

/** Removes a dialect's body; false for the last one (a view needs a body). */
export const removeViewDialect = (doc: Json, dialect: string): boolean => removeDialectText(doc, "body", dialect, true);

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

// ------------------------------------------------------------------ typed rows (routine parameters, result columns, composite fields)

/** The facets a typed row may carry beside its type. */
export type TypeFacet = "length" | "precision" | "scale";

/** Why a typed facet cannot be saved, or null (empty clears it): a whole number, at least 1 (a scale at least 0). */
export function facetProblem(facet: TypeFacet, raw: string): string | null {
  const text = raw.trim();
  if (text === "") return null;
  if (!/^\d+$/.test(text)) return "A whole number, or empty.";
  if (facet !== "scale" && Number(text) < 1) return "At least 1.";
  return null;
}

/** Sets a facet of a typed row (an object holding `type`, `length`, ...): empty removes it. False when the value cannot be one. */
export function setFacet(row: Json, facet: TypeFacet, raw: string): boolean {
  if (facetProblem(facet, raw)) return false;
  const text = raw.trim();
  if (text === "") delete row[facet];
  else row[facet] = Number(text);
  return true;
}

/**
 * Sets a typed row's type (a built-in keyword or a database type's id) or, with "", leaves only its native type. False when
 * the row would have neither (the schemas need one of them).
 */
export function setRowType(row: Json, type: string): boolean {
  if (!type && typeof row.nativeType !== "string") return false;
  if (type) row.type = type;
  else delete row.type;
  return true;
}

/** Sets a typed row's native type: empty removes it, unless the row has no type (then it is refused: false). */
export function setRowNativeType(row: Json, text: string): boolean {
  const value = text.trim();
  if (!value && typeof row.type !== "string") return false;
  if (value) row.nativeType = value;
  else delete row.nativeType;
  return true;
}

/** Adds a typed row named `<base>_<n>` (string, or int32 for a parameter) at the end of a list member. */
export function addTypedRow(doc: Json, member: string, base: string, type = "string"): void {
  const rows = list(doc, member);
  doc[member] = [
    ...rows,
    {
      name: nextColumnName(
        rows.map((r) => String(r.name ?? "")),
        base,
      ),
      type,
    },
  ];
}

/** Removes the n-th row of a list member (the member goes when empty). */
export function removeRow(doc: Json, member: string, index: number): void {
  setList(
    doc,
    member,
    list(doc, member).filter((_, i) => i !== index),
  );
}

/** Moves the n-th row of a list member one place up (-1) or down (1). */
export function moveRow(doc: Json, member: string, index: number, by: -1 | 1): boolean {
  const rows = [...(Array.isArray(doc[member]) ? (doc[member] as unknown[]) : [])];
  const to = index + by;
  if (index < 0 || index >= rows.length || to < 0 || to >= rows.length) return false;
  [rows[index], rows[to]] = [rows[to], rows[index]];
  doc[member] = rows;
  return true;
}

// ------------------------------------------------------------------ routines

/** What a routine returns, as the Definition tab's picker names it. */
export type ReturnsShape = "none" | "value" | "table";

export function returnsShape(doc: Json): ReturnsShape {
  const returns = doc.returns as Json | undefined;
  if (!returns) return "none";
  return Array.isArray(returns.table) ? "table" : "value";
}

/** Sets what a routine returns: nothing, a single value (int32 to start), or a table (one column to start). */
export function setReturnsShape(doc: Json, shape: ReturnsShape): void {
  if (shape === "none") delete doc.returns;
  else if (shape === "value") doc.returns = { type: "int32" };
  else doc.returns = { table: [{ name: "column_1", type: "string" }] };
}

/** Sets a routine's kind; a procedure loses a single-value result (it returns nothing or result sets). */
export function setRoutineKind(doc: Json, kind: "function" | "procedure"): void {
  if (kind === "function") delete doc.routineKind;
  else doc.routineKind = "procedure";
}

/** Adds an id to a dependsOn list (once); removes it with `remove`. */
export function toggleDependency(doc: Json, id: string, remove = false): void {
  const ids = Array.isArray(doc.dependsOn) ? (doc.dependsOn as string[]) : [];
  const next = remove ? ids.filter((x) => x !== id) : ids.includes(id) ? ids : [...ids, id];
  if (next.length) doc.dependsOn = next;
  else delete doc.dependsOn;
}

// ------------------------------------------------------------------ database types

/** The members each kind of database type uses; switching kind removes the others'. */
const TYPE_KIND_MEMBERS: Record<string, readonly string[]> = {
  domain: ["base", "length", "precision", "scale", "check"],
  composite: ["fields"],
  enum: ["members"],
  range: ["subtype"],
};

/** Switches a database type's kind: the members of the old kind go and the new kind starts with `defaults`. */
export function setTypeKind(doc: Json, typeKind: string, defaults: Json): void {
  if (doc.typeKind === typeKind) return;
  for (const member of Object.values(TYPE_KIND_MEMBERS).flat()) if (!TYPE_KIND_MEMBERS[typeKind]?.includes(member)) delete doc[member];
  doc.typeKind = typeKind;
  for (const [k, v] of Object.entries(defaults)) if (doc[k] === undefined) doc[k] = v;
}

/** The native types the resolved database view gives a database type, for a definition's starting text. */
export interface ResolvedTypeHints {
  baseNativeType?: string | null;
  subtypeNativeType?: string | null;
  fields?: readonly { name: string; nativeType: string }[];
}

/**
 * A starting definition for a database type (the text after its name), built from its structured form with the native types
 * the database's dialect gives them: `AS ENUM ('a', 'b')`, `AS (x integer)`, `AS RANGE (SUBTYPE = date)`, `AS varchar(320)
 * CHECK (...)`. The user edits it for the dialect it is added for.
 */
export function definitionTemplate(doc: Json, resolved: ResolvedTypeHints | undefined): string {
  switch (doc.typeKind) {
    case "enum":
      return `AS ENUM (${((doc.members as string[] | undefined) ?? []).map((m) => `'${m.replace(/'/g, "''")}'`).join(", ")})`;
    case "composite":
      return `AS (${(resolved?.fields ?? list(doc, "fields").map((f) => ({ name: String(f.name ?? ""), nativeType: String(f.nativeType ?? f.type ?? "") }))).map((f) => `${f.name} ${f.nativeType}`).join(", ")})`;
    case "range":
      return `AS RANGE (SUBTYPE = ${resolved?.subtypeNativeType ?? String(doc.subtype ?? "")})`;
    default:
      return `AS ${resolved?.baseNativeType ?? String(doc.base ?? "")}${typeof doc.check === "string" ? ` CHECK (${doc.check})` : ""}`;
  }
}

/** Why an enum type's label cannot be saved at index `at` (null: it can): non-empty and unique. */
export function memberProblem(members: readonly string[], at: number, label: string): string | null {
  const text = label.trim();
  if (!text) return "A label cannot be empty.";
  if (members.some((m, i) => i !== at && m === text)) return `${text} is already a label.`;
  return null;
}

/** Adds a label `value_<n>` at the end of an enum type's members. */
export function addMember(doc: Json): void {
  const members = Array.isArray(doc.members) ? (doc.members as string[]) : [];
  const taken = new Set(members);
  let n = members.length + 1;
  while (taken.has(`value_${n}`)) n++;
  doc.members = [...members, `value_${n}`];
}
