// The property bag, pure part: the free keys of an element's (or a column entry's) `properties` object that no applicable
// extension schema declares, as rows of key, value and type; the edits a row commits (set, rename, remove) applied to the
// object in place of its keys' order; and what makes a key or a value unsavable. Values are text by default (a dictionary of
// strings); a row may hold a number or true/false instead, and a value that is neither (an object, a list) shows as JSON.

type Json = Record<string, unknown>;

export type PropertyType = "text" | "number" | "boolean" | "json";

export const PROPERTY_TYPE_LABELS: Record<PropertyType, string> = { text: "Text", number: "Number", boolean: "True/false", json: "JSON" };

export interface PropertyRow {
  key: string;
  value: unknown;
  type: PropertyType;
  /** The value as the row's input shows it. */
  text: string;
}

/** One committed change of the bag: a value set (under a new key, the old one renamed in place), or a key removed. */
export type PropertyEdit = { op: "set"; key: string; value: unknown; from?: string } | { op: "remove"; key: string };

/** The type a stored value shows as. */
export function propertyTypeOf(value: unknown): PropertyType {
  if (typeof value === "number") return "number";
  if (typeof value === "boolean") return "boolean";
  if (typeof value === "string") return "text";
  return "json";
}

/** A stored value as text: a string as it is, anything else as JSON. */
export function propertyText(value: unknown): string {
  if (typeof value === "string") return value;
  if (value === undefined) return "";
  return JSON.stringify(value);
}

const objectOf = (properties: unknown): Json => (properties && typeof properties === "object" && !Array.isArray(properties) ? (properties as Json) : {});

/** The bag's rows: every key of `properties` that `declared` does not name, in the object's order. */
export function propertyRows(properties: unknown, declared: Iterable<string> = []): PropertyRow[] {
  const skip = new Set(declared);
  return Object.entries(objectOf(properties))
    .filter(([key]) => !skip.has(key))
    .map(([key, value]) => ({ key, value, type: propertyTypeOf(value), text: propertyText(value) }));
}

/** The keys `declared` extension schemas give typed fields (their `properties`' names). */
export function declaredKeys(extensions: readonly { properties: unknown }[]): string[] {
  return [...new Set(extensions.flatMap((e) => Object.keys(objectOf(e.properties))))];
}

export type Parsed = { value: unknown; error?: undefined } | { value?: undefined; error: string };

/** A typed text as the value its type stores, or why it cannot be. */
export function parsePropertyValue(text: string, type: PropertyType): Parsed {
  switch (type) {
    case "text":
      return { value: text };
    case "number": {
      const trimmed = text.trim();
      const n = Number(trimmed);
      return trimmed !== "" && Number.isFinite(n) ? { value: n } : { error: "Not a number." };
    }
    case "boolean": {
      const trimmed = text.trim().toLowerCase();
      return trimmed === "true" ? { value: true } : trimmed === "false" ? { value: false } : { error: "Not true or false." };
    }
    case "json":
      try {
        return { value: JSON.parse(text) as unknown };
      } catch {
        return { error: "Not valid JSON." };
      }
  }
}

/** A stored value under another type: text keeps its characters, a number must read as one, true/false takes "true" (any other text is false). */
export function convertPropertyValue(value: unknown, type: PropertyType): Parsed {
  const text = propertyText(value);
  if (type === "boolean") return { value: value === true || text.trim().toLowerCase() === "true" };
  return parsePropertyValue(text, type);
}

/** Why a key cannot be saved, or null: it is required, unique in the bag, and not one a schema declares. */
export function propertyKeyProblem(key: string, others: Iterable<string>, declared: Iterable<string> = []): string | null {
  const k = key.trim();
  if (!k) return "A key is required.";
  if (new Set(others).has(k)) return `Another property is named ${k}.`;
  if (new Set(declared).has(k)) return `${k} is a declared custom property; set it above.`;
  return null;
}

/** The object after an edit (a new object, the keys' order kept; a renamed key keeps its place), or undefined when it is empty. */
export function applyPropertyEdit(properties: unknown, edit: PropertyEdit): Json | undefined {
  const current = objectOf(properties);
  const next: Json = {};
  if (edit.op === "remove") {
    for (const [k, v] of Object.entries(current)) if (k !== edit.key) next[k] = v;
  } else {
    const from = edit.from !== undefined && edit.from in current ? edit.from : edit.key;
    let placed = false;
    for (const [k, v] of Object.entries(current)) {
      if (k === from) {
        next[edit.key] = edit.value;
        placed = true;
      } else if (k !== edit.key) next[k] = v;
    }
    if (!placed) next[edit.key] = edit.value;
  }
  return Object.keys(next).length ? next : undefined;
}

/** Applies an edit to a document's (or a column entry's) `properties`, removing the member when it is left empty. */
export function editProperties(owner: Json, edit: PropertyEdit): void {
  const next = applyPropertyEdit(owner.properties, edit);
  if (next) owner.properties = next;
  else delete owner.properties;
}
