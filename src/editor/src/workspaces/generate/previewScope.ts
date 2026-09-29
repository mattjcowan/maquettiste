// The preview's element picker (generation-ui.md 3.3): a unit renders one element of its scope, so the picker offers
// only elements of that scope's kind (the planner's scopes, UnitPlanner.Candidates): `each <kind>` lists the model's
// elements of the kind from the index; `each table`, `each locale` and `select <selector>` list the elements the unit
// plans (resolved table keys, locale codes, the selector's answer). `model` renders once, with no element.
import type { ElementKind } from "@/api/types";

export interface UnitScope {
  /** The unit's `for`. */
  for: string;
  /** What one rendered element is called in messages ("reference type"); "model" for a model unit. */
  label: string;
  /** The index kind listed for an `each` scope; null when the elements come from the unit's planned paths. */
  indexKind: ElementKind | null;
  /** The template's scope variable (`reference_type`), or null (`model`, `select`). */
  variable: string | null;
  /** The unit renders once (`model`): no element to pick. */
  once: boolean;
}

const EACH: Record<string, { label: string; indexKind: ElementKind | null; variable: string }> = {
  package: { label: "domain", indexKind: "package", variable: "package" },
  entity: { label: "entity", indexKind: "entity", variable: "entity" },
  relation: { label: "relation", indexKind: "relation", variable: "relation" },
  enum: { label: "enum", indexKind: "enum", variable: "enum" },
  "value object": { label: "value object", indexKind: "value-object", variable: "value_object" },
  table: { label: "table", indexKind: null, variable: "table" },
  "reference type": { label: "reference type", indexKind: "reference-type", variable: "reference_type" },
  seed: { label: "seed", indexKind: "seed", variable: "seed" },
  locale: { label: "locale", indexKind: null, variable: "locale" },
};

/** Plural kind words a selector may name, to call its elements by kind in messages ("select databases" → database). */
const SELECT_WORDS: [RegExp, string][] = [
  [/\bdatabases?\b/, "database"],
  [/\btables?\b/, "table"],
  [/\bentit(?:y|ies)\b/, "entity"],
  [/\brelations?\b/, "relation"],
  [/\benums?\b/, "enum"],
  [/\bvalue_?objects?\b|\bvalueObjects?\b/, "value object"],
  [/\breference_?types?\b|\breferenceTypes?\b/, "reference type"],
  [/\bseeds?\b/, "seed"],
  [/\bpackages?\b/, "domain"],
  [/\blocales?\b/, "locale"],
];

export function unitScope(forExpr: string): UnitScope {
  const f = forExpr.trim();
  if (f === "model" || f === "") return { for: f, label: "model", indexKind: null, variable: null, once: true };
  if (f.startsWith("each ")) {
    const each = EACH[f.slice(5).trim()];
    if (each) return { for: f, ...each, once: false };
  }
  if (f.startsWith("select ")) {
    const selector = f.slice(7);
    const word = SELECT_WORDS.find(([re]) => re.test(selector))?.[1] ?? "element";
    return { for: f, label: word, indexKind: null, variable: null, once: false };
  }
  return { for: f, label: "element", indexKind: null, variable: null, once: false };
}

export interface Candidate {
  id: string;
  label: string;
}

/**
 * The picker's elements: the index's elements of an `each` kind (A to Z), else the unit's planned element ids in plan
 * order. `inScope`, when known (the unit's complete plan), narrows an `each` kind to the elements the unit renders: its
 * `where` filter and `generation.skip` hints leave the others out.
 */
export function scopeCandidates(
  scope: UnitScope,
  index: readonly { id: string; kind: string; name: string; displayName?: string | null }[],
  plannedIds: readonly string[],
  inScope: ReadonlySet<string> | null = null,
): Candidate[] {
  if (scope.once) return [];
  const names = new Map(index.map((e) => [e.id, e.displayName || e.name]));
  if (scope.indexKind)
    return index
      .filter((e) => e.kind === scope.indexKind && (!inScope || inScope.has(e.id)))
      .map((e) => ({ id: e.id, label: e.displayName || e.name }))
      .sort((a, b) => (a.label < b.label ? -1 : a.label > b.label ? 1 : a.id < b.id ? -1 : a.id > b.id ? 1 : 0));
  return [...new Set(plannedIds)].map((id) => ({ id, label: names.get(id) ?? id }));
}

/** The element to render: the remembered choice while it is still a candidate, else the first candidate (null for a model unit). */
export function pickElement(candidates: readonly Candidate[], remembered: string | undefined): string | null {
  if (remembered && candidates.some((c) => c.id === remembered)) return remembered;
  return candidates[0]?.id ?? null;
}

/**
 * The preview's unit for a file: the first unit using it (directly, as a companion, or through includes). A partial
 * or any file no unit uses previews through a unit, with a note saying which.
 */
export function previewUnit(
  file: { path: string; role: string } | undefined,
  feeding: readonly string[],
  units: readonly string[],
  chosen: string,
): { unit: string; note: string | null } {
  const unit = chosen && units.includes(chosen) ? chosen : (feeding[0] ?? units[0] ?? "");
  if (!file || !unit || file.role === "manifest") return { unit, note: null };
  const name = file.path.split("/").at(-1) ?? file.path;
  if (file.role === "script") return { unit, note: `${name} is a script: previewing unit ${unit}, which can call it.` };
  if (feeding.includes(unit)) return { unit, note: file.role === "partial" ? `${name} is a partial: previewing unit ${unit}, which includes it.` : null };
  if (feeding.length) return { unit, note: `Unit ${unit} does not use ${name}: the preview shows that unit as it is.` };
  return { unit, note: `No unit uses ${name} yet: previewing unit ${unit}.` };
}

/** The friendly message for a render that ran with an element outside the unit's scope (the scope variable is missing). */
export function scopeMismatch(scope: UnitScope, diagnostics: readonly { rule: string; message: string }[]): string | null {
  if (scope.once || !scope.variable) return null;
  const variable = scope.variable;
  const missing = diagnostics.some((d) => d.rule === "MQ6006" && new RegExp("[`'\"]" + variable + "[`'\"] was not found").test(d.message));
  return missing ? mismatchText(scope) : null;
}

/** The message for a render that produced no file and no diagnostic: the unit leaves that element out. */
export function noFilesText(scope: UnitScope, unit: string, result: { files: readonly unknown[]; diagnostics: readonly unknown[] } | null): string | null {
  if (!result || result.files.length || result.diagnostics.length) return null;
  if (scope.once) return `Unit ${unit} renders no file for this model.`;
  return `Unit ${unit} renders no file for this ${scope.label}: its where filter or a generation.skip hint leaves it out.`;
}

export const mismatchText = (scope: UnitScope): string => `This template renders one ${scope.label}; pick a ${scope.label} to preview it.`;
