// The Parameters form's model (generation-ui.md 3.2), pure. The control comes from the pack's parameter schema
// (`parameterSchema.properties`) when it has one, else from the default: boolean -> switch, string -> text,
// number -> number, array or object -> a JSON editor. An `enum` becomes a select. Values are the project's
// (`packs.<name>.parameters` in maquettiste.json); Reset to default removes the key.
import type { components } from "@/api/schema";

type S = components["schemas"];
export type ParameterInfo = S["PackParameterInfo"];
export type ParameterControl = "switch" | "text" | "number" | "select" | "json";

export interface ParameterRow {
  name: string;
  title: string;
  description: string | null;
  control: ParameterControl;
  options: string[];
  defaultValue: unknown;
  /** The project's value, or undefined when the default applies. */
  value: unknown;
  set: boolean;
  required: boolean;
  schema: Record<string, unknown> | null;
}

function schemaType(schema: Record<string, unknown> | null): string | null {
  const t = schema?.type;
  return typeof t === "string" ? t : Array.isArray(t) ? ((t.find((x) => x !== "null") as string | undefined) ?? null) : null;
}

export function parameterControl(schema: Record<string, unknown> | null, fallback: unknown): ParameterControl {
  if (Array.isArray(schema?.enum)) return "select";
  const type = schemaType(schema);
  if (type === "boolean") return "switch";
  if (type === "number" || type === "integer") return "number";
  if (type === "string") return "text";
  if (type === "array" || type === "object") return "json";
  if (typeof fallback === "boolean") return "switch";
  if (typeof fallback === "number") return "number";
  if (typeof fallback === "string" || fallback === null || fallback === undefined) return "text";
  return "json";
}

/** One row per parameter: the declared ones in name order, then project values the pack does not declare (MQ6024). */
export function parameterRows(parameters: ParameterInfo[], values: Record<string, unknown>): ParameterRow[] {
  const rows = parameters.map((p): ParameterRow => {
    const set = Object.prototype.hasOwnProperty.call(values, p.name);
    const schema = p.schema ?? null;
    return {
      name: p.name,
      title: typeof schema?.title === "string" ? schema.title : p.name,
      description: typeof schema?.description === "string" ? schema.description : null,
      control: parameterControl(schema, p.default),
      options: Array.isArray(schema?.enum) ? (schema.enum as unknown[]).map(String) : [],
      defaultValue: p.default,
      value: set ? values[p.name] : undefined,
      set,
      required: p.required,
      schema,
    };
  });
  return rows.sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
}

/** Project values for parameters the pack does not declare (MQ6024), offered for removal. */
export function undeclared(parameters: ParameterInfo[], values: Record<string, unknown>): string[] {
  const known = new Set(parameters.map((p) => p.name));
  return Object.keys(values)
    .filter((k) => !known.has(k))
    .sort();
}

/** The text a control shows for a value. */
export function displayValue(control: ParameterControl, value: unknown): string {
  if (value === undefined) return "";
  if (control === "json") return JSON.stringify(value, null, 2);
  return value === null ? "" : String(value);
}

export type Parsed = { ok: true; value: unknown } | { ok: false; error: string };

/** Parses a control's text into a value and checks it against the schema subset (type, enum, minimum, maximum,
 * minLength, maxLength, pattern); a value that fails is refused in the form. */
export function parseParameter(row: Pick<ParameterRow, "control" | "schema">, text: string | boolean): Parsed {
  let value: unknown;
  if (row.control === "switch") value = text === true || text === "true";
  else if (row.control === "number") {
    if (typeof text !== "string" || text.trim() === "" || !Number.isFinite(Number(text))) return { ok: false, error: "Enter a number." };
    value = Number(text);
    if (schemaType(row.schema) === "integer" && !Number.isInteger(value)) return { ok: false, error: "Enter a whole number." };
  } else if (row.control === "json") {
    try {
      value = JSON.parse(String(text));
    } catch (error) {
      return { ok: false, error: `Not valid JSON: ${(error as Error).message}` };
    }
    const type = schemaType(row.schema);
    if (type === "array" && !Array.isArray(value)) return { ok: false, error: "Enter a JSON array." };
    if (type === "object" && (typeof value !== "object" || value === null || Array.isArray(value))) return { ok: false, error: "Enter a JSON object." };
  } else value = String(text);
  const s = row.schema ?? {};
  if (Array.isArray(s.enum) && !(s.enum as unknown[]).some((e) => String(e) === String(value)))
    return { ok: false, error: `One of: ${(s.enum as unknown[]).join(", ")}.` };
  if (typeof value === "number") {
    if (typeof s.minimum === "number" && value < s.minimum) return { ok: false, error: `At least ${s.minimum}.` };
    if (typeof s.maximum === "number" && value > s.maximum) return { ok: false, error: `At most ${s.maximum}.` };
  }
  if (typeof value === "string") {
    if (typeof s.minLength === "number" && value.length < s.minLength) return { ok: false, error: `At least ${s.minLength} characters.` };
    if (typeof s.maxLength === "number" && value.length > s.maxLength) return { ok: false, error: `At most ${s.maxLength} characters.` };
    if (typeof s.pattern === "string" && !new RegExp(s.pattern, "u").test(value)) return { ok: false, error: `Must match ${s.pattern}.` };
  }
  return { ok: true, value };
}

/** The project's values with one parameter set, or removed (Reset to default) when `value` is undefined. */
export function withParameter(values: Record<string, unknown>, name: string, value: unknown): Record<string, unknown> {
  const next = { ...values };
  if (value === undefined) delete next[name];
  else next[name] = value;
  return next;
}

/** The `packs.<pack>` section to save: the section as loaded, with its parameters replaced (empty ones removed). */
export function packSection(loaded: Record<string, unknown> | null | undefined, parameters: Record<string, unknown>): Record<string, unknown> {
  const section: Record<string, unknown> = { ...(loaded ?? {}) };
  if (Object.keys(parameters).length) section.parameters = parameters;
  else delete section.parameters;
  return section;
}
