// The Units grid's model (generation-ui.md 1 and 3.1), pure. The grid edits the pack.json document it loaded, not a
// projection of it: every member a row does not show (a future one, or an `x-` member) is kept, and Save sends the
// whole document. The words are the UI's (section 1): Files, Scope, Filter, Write; the write mode `once` is labelled
// "Create only if missing" so "once" never means two things on one screen (GU3).
import type { components } from "@/api/schema";
import { pathSummary } from "./pathSummary";
import { tableKeyLabel } from "./previewScope";

type S = components["schemas"];
export type PackJson = Record<string, unknown>;
export type RawUnit = Record<string, unknown>;
export type UnitField = "id" | "for" | "template" | "output" | "mode" | "formatter";
export type WriteMode = "overwrite" | "once" | "regions" | "pair";

export const UNIT_ID = /^[a-z][a-z0-9-]*$/;

export const WRITE_MODES: { value: WriteMode; label: string; help: string }[] = [
  { value: "overwrite", label: "Overwrite", help: "Writes the file on every apply; a hand edit is reported first." },
  { value: "once", label: "Create only if missing", help: "Writes the file once; later applies leave it alone." },
  { value: "regions", label: "Protected regions", help: "Rewrites the file but keeps the text inside its protected regions." },
  { value: "pair", label: "Pair", help: "Writes a generated file and a companion that is created once for hand code." },
];

export const modeLabel = (mode: unknown): string => WRITE_MODES.find((m) => m.value === mode)?.label ?? "Overwrite";

/** The element kinds a unit can run for, in the words the Scope picker shows. */
export const EACH_KINDS = [
  "entity",
  "relation",
  "enum",
  "value object",
  "reference type",
  "seed",
  "package",
  "database",
  "table",
  "locale",
  "process",
  "actor",
  "scenario",
];

export const SCOPE_OPTIONS: string[] = ["model", ...EACH_KINDS.map((k) => `each ${k}`)];

/** The grid's Files column: one file per element, one file for a group, or per selected element. */
export function filesLabel(scope: string): string {
  const s = scope.trim();
  if (s === "model") return "Once";
  if (s === "each package") return "Once per package";
  if (s === "each database") return "Once per database";
  if (s.startsWith("each ")) return `Each ${s.slice(5)}`;
  if (s.startsWith("select ")) return "Selector";
  return s;
}

/** The scope in the path summary's words: "once" for the model, else the `for` text. */
export const scopeWords = (scope: string): string => (scope.trim() === "model" ? "once" : scope.trim());

/** The scope help of `each process`, `each actor` and `each scenario` (phase-3-design.md 7.1). */
export const PROCESS_SCOPE_HELP = "Runs once per process, actor or scenario; one file each.";

/** Plain-language help per scope (generation-ui.md 3.1). */
export function scopeHelp(scope: string): string {
  const s = scope.trim();
  if (s === "model") return "Runs once. The template sees the whole model: use it for one file that lists many things, or to write several files with `file`.";
  if (s === "each package") return "Runs once per package (domain folder); one file per package gathering its elements.";
  if (s === "each database") return "Runs once per database; one file per database gathering its tables.";
  if (s === "each table") return "Runs once per table of every database: the tables the databases design and the tables their mappings resolve.";
  if (s === "each view") return "Runs once per view of every database; one file each.";
  if (s === "each sequence") return "Runs once per sequence of every database, the ones the resolver creates for keys included; one file each.";
  if (s === "each locale") return "Runs once per declared language; no element, no filter.";
  if (s === "each process" || s === "each actor" || s === "each scenario") return PROCESS_SCOPE_HELP;
  if (s.startsWith("select ")) return `Runs once per element that the selector ${s.slice(7)}, registered by the pack's scripts, returns.`;
  if (s.startsWith("each ")) return "Runs once per element of that kind; one file each.";
  return "Not a scope this editor knows; the pack loader reports it when it is invalid.";
}

export const FIELD_HELP: Record<UnitField, string> = {
  id: "Names the unit in plans, manifests and file headers. Renaming it re-renders the unit and orphans its old files' manifest entries, which the next apply adopts or deletes.",
  for: "Which elements the unit runs for.",
  template: "The file this unit renders. Partials it includes are listed in Templates.",
  output: "Where the file lands, under the output base and an output root. Leave empty when the template writes its files with `file`.",
  mode: "How the file is written when it already exists.",
  formatter: "Runs after rendering; configured in Settings › Formatters.",
};

export function unitsOf(doc: PackJson | null | undefined): RawUnit[] {
  const units = doc?.units;
  return Array.isArray(units) ? (units.filter((u) => u && typeof u === "object" && !Array.isArray(u)) as RawUnit[]) : [];
}

const str = (v: unknown): string => (typeof v === "string" ? v : "");

/** A unit as the contract's PackUnit (the paths request's unitOverride), from a raw row. */
export function toPackUnit(raw: RawUnit): S["PackUnit"] {
  return {
    id: str(raw.id),
    template: str(raw.template),
    for: str(raw.for),
    where: (raw.where as Record<string, unknown> | undefined) ?? null,
    output: typeof raw.output === "string" ? raw.output : null,
    mode: (WRITE_MODES.some((m) => m.value === raw.mode) ? raw.mode : "overwrite") as S["PackUnit"]["mode"],
    formatter: typeof raw.formatter === "string" ? raw.formatter : null,
    delimiters: (raw.delimiters as S["PackUnit"]["delimiters"]) ?? null,
    companion: (raw.companion as S["PackUnit"]["companion"]) ?? null,
    transforms: Array.isArray(raw.transforms) ? (raw.transforms as string[]) : [],
  };
}

function withUnits(doc: PackJson, units: RawUnit[]): PackJson {
  return { ...doc, units };
}

/** Sets one field of one unit; an empty optional field (output, formatter) and the default mode are removed. */
export function setUnitField(doc: PackJson, index: number, field: UnitField, value: string): PackJson {
  const units = unitsOf(doc).slice();
  const unit = { ...units[index] };
  if ((field === "output" || field === "formatter") && value === "") delete unit[field];
  else if (field === "mode" && value === "overwrite") delete unit.mode;
  else unit[field] = value;
  units[index] = unit;
  return withUnits(doc, units);
}

/** Why an id cannot be used for the unit at `index`, or null. */
export function unitIdError(doc: PackJson, index: number, id: string): string | null {
  if (!UNIT_ID.test(id)) return "Lowercase letters, digits and hyphens, starting with a letter.";
  if (unitsOf(doc).some((u, i) => i !== index && u.id === id)) return `Another unit is already named ${id}.`;
  return null;
}

function uniqueId(doc: PackJson, base: string): string {
  const taken = new Set(unitsOf(doc).map((u) => str(u.id)));
  if (!taken.has(base)) return base;
  for (let n = 2; ; n++) if (!taken.has(`${base}-${n}`)) return `${base}-${n}`;
}

/** Inserts a new unit below `at` (-1: at the end). */
export function insertUnit(doc: PackJson, at: number): { doc: PackJson; index: number } {
  const units = unitsOf(doc).slice();
  const id = uniqueId(doc, "unit");
  const index = at < 0 ? units.length : at + 1;
  units.splice(index, 0, { id, template: `${id}.scriban`, for: "each entity" });
  return { doc: withUnits(doc, units), index };
}

/** Duplicates the unit at `index` below it, its id suffixed `-copy`. */
export function duplicateUnit(doc: PackJson, index: number): { doc: PackJson; index: number } {
  const units = unitsOf(doc).slice();
  const copy = { ...structuredClone(units[index]), id: uniqueId(doc, `${str(units[index].id)}-copy`) };
  units.splice(index + 1, 0, copy);
  return { doc: withUnits(doc, units), index: index + 1 };
}

export function removeUnit(doc: PackJson, index: number): PackJson {
  return withUnits(
    doc,
    unitsOf(doc).filter((_, i) => i !== index),
  );
}

/** Moves the unit at `index` by `delta` (Alt+Up, Alt+Down); returns the unchanged document at an edge. */
export function moveUnit(doc: PackJson, index: number, delta: number): { doc: PackJson; index: number } {
  const units = unitsOf(doc).slice();
  const to = index + delta;
  if (to < 0 || to >= units.length) return { doc, index };
  const [unit] = units.splice(index, 1);
  units.splice(to, 0, unit);
  return { doc: withUnits(doc, units), index: to };
}

/** The unit's filter in words ("tags: api; not abstract"), or "" when it has none. */
export function filterWords(where: unknown): string {
  if (!where || typeof where !== "object") return "";
  const w = where as Record<string, unknown>;
  const list = (v: unknown) => (Array.isArray(v) ? v.join(", ") : String(v));
  const parts: string[] = [];
  const named: [string, string][] = [
    ["tags", "tags"],
    ["notTags", "not tags"],
    ["stereotypes", "stereotypes"],
    ["notStereotypes", "not stereotypes"],
    ["categories", "categories"],
    ["packages", "packages"],
    ["notPackages", "not packages"],
    ["database", "database"],
    ["script", "script filter"],
  ];
  for (const [key, label] of named) if (w[key] !== undefined && w[key] !== null && list(w[key]) !== "") parts.push(`${label}: ${list(w[key])}`);
  if (w.abstract === true) parts.push("abstract");
  if (w.abstract === false) parts.push("not abstract");
  return parts.join("; ");
}

/** One line of the explorer and the grid: `id · scope → template → path summary`. */
export function unitLine(raw: RawUnit): string {
  const summary = pathSummary(typeof raw.output === "string" ? raw.output : null, raw.delimiters as { open: string; close: string } | null);
  return `${str(raw.id)} · ${scopeWords(str(raw.for))} → ${str(raw.template)} → ${summary.text}`;
}

export interface ExamplePath {
  /** The output path for the example element, or null when the unit plans nothing for it. */
  path: string | null;
  elementId: string | null;
  /** The planned file count (elements in scope after the filter). */
  count: number;
  /** MQ6020: the elements that render the same path as the example element. */
  collisions: string[];
  /** MQ6019 (outside every output root) or another rule on the example path. */
  rule: string | null;
  allowed: boolean;
}

/** The example path of a unit for a chosen element (the Example element picker), from a paths answer. The element
 * defaults to the first main path in scope order when none is chosen or the chosen one is not planned. */
export function examplePath(result: S["UnitPathsResult"] | null | undefined, elementId: string | null): ExamplePath | null {
  if (!result) return null;
  const main = result.paths.filter((p) => p.role === "main");
  const chosen = main.find((p) => p.elementId === elementId) ?? main[0];
  if (!chosen) return { path: null, elementId: null, count: result.count, collisions: [], rule: null, allowed: true };
  const collisions = main.filter((p) => p !== chosen && p.path === chosen.path).map((p) => p.elementId ?? "(model)");
  const rule = chosen.rule ?? (collisions.length ? "MQ6020" : null);
  return { path: chosen.path, elementId: chosen.elementId, count: result.count, collisions, rule, allowed: chosen.allowed };
}

/**
 * A document in a comparable form: keys sorted at every level, nulls dropped, and a unit's default write mode
 * (`overwrite`) removed, because the server rewrites pack.json in canonical key order with defaults omitted.
 */
function normalize(value: unknown, inUnit = false): unknown {
  if (Array.isArray(value)) return value.map((v) => normalize(v));
  if (!value || typeof value !== "object") return value;
  const out: Record<string, unknown> = {};
  for (const key of Object.keys(value).sort()) {
    const v = (value as Record<string, unknown>)[key];
    if (v === null || v === undefined) continue;
    if (inUnit && key === "mode" && v === "overwrite") continue;
    out[key] = key === "units" && Array.isArray(v) ? v.map((u) => normalize(u, true)) : normalize(v);
  }
  return out;
}

/** Whether two documents hold the same content, ignoring key order and defaults (the Units tab's unsaved state). */
export const sameDocument = (a: PackJson | null, b: PackJson | null): boolean => JSON.stringify(normalize(a)) === JSON.stringify(normalize(b));

/**
 * A planned path as the Units tab reads it: the server is adding `elementName` and `elementKind` (the element's or the
 * synthesized table's name and kind) to each path; until the generated schema carries them they are optional here.
 */
export type NamedUnitPath = S["UnitPath"] & { elementName?: string | null; elementKind?: string | null };

/** One choice of the Example element picker. */
export interface ExampleOption {
  id: string;
  name: string;
  kind: string | null;
  /** "Customer (entity)", or the name alone when the kind is not known. */
  label: string;
}

/**
 * The Example element choices of a unit: its planned elements, each once, in plan order, named by the path's own
 * `elementName` and `elementKind`, else by the index, else (a synthesized table key `<entityId>@<databaseId>`)
 * "entity name @ database name", else the id itself.
 */
export function exampleOptions(
  paths: readonly NamedUnitPath[],
  lookup: (id: string) => { name: string; kind: string } | undefined,
  kindWord: (kind: string) => string = (k) => k,
): ExampleOption[] {
  const out: ExampleOption[] = [];
  const seen = new Set<string>();
  for (const p of paths) {
    const id = p.elementId;
    if (!id || seen.has(id)) continue;
    seen.add(id);
    const known = lookup(id);
    let name = p.elementName || known?.name || "";
    let kind = p.elementKind || known?.kind || null;
    if (!name) {
      const table = id.indexOf("@") > 0;
      name = (table && tableKeyLabel(id, { get: (x: string) => lookup(x)?.name })) || id;
      if (table) kind ??= "table";
    }
    const word = kind ? kindWord(kind) : null;
    out.push({ id, name, kind, label: word ? `${name} (${word})` : name });
  }
  return out;
}

/** The options whose label or id holds every typed word (case-insensitive). */
export function filterExamples(options: readonly ExampleOption[], text: string): ExampleOption[] {
  const words = text.toLowerCase().split(/\s+/).filter(Boolean);
  if (!words.length) return [...options];
  return options.filter((o) => {
    const hay = `${o.label} ${o.id}`.toLowerCase();
    return words.every((w) => hay.includes(w));
  });
}
