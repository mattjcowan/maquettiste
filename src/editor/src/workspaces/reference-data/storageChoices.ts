// The storage choice where a reference type is created and used (reference-types-seeds-localization.md 1.4 and 4.5):
// the New reference type dialog's "Stored as" list (Let the packs decide, then the strategies the project declares by
// their names, the sql-ddl pack's three first, each with its description as help) and the short effective storage the
// attribute type picker shows beside a type's name. The Storage tab uses the same words. The engine knows no strategy; the strategy keys are the project's. Pure, so it is tested alone.
import type { StorageChoice } from "@/api/types";

/** The words for "no strategy": the templates choose. */
export const PACKS_DECIDE = "Let the packs decide";

/** The "Stored as" value of "Let the packs decide": a choice with no strategy. */
export const TEMPLATE_DEFINED = "__template";

/** The strategy keys the sql-ddl pack maps by default, listed first in this order. */
const KNOWN = ["lookup-table", "check", "native"];

/** The strategy preselected for a new type when the project declares it. */
export const PRESELECTED_STRATEGY = "check";

export interface StorageOption {
  value: string;
  label: string;
  /** Shown under the select while the option is chosen: what it means. */
  help?: string;
}

export type DeclaredStrategies = Record<string, { description?: string | null }>;

/** The help under a "Let the packs decide" choice. */
export const PACKS_DECIDE_HELP = "No strategy: the templates decide how the type is stored.";

/** Let the packs decide, then the declared strategies by name: the known ones in their order, then the others A to Z. */
export function storageOptions(strategies: DeclaredStrategies): StorageOption[] {
  const keys = Object.keys(strategies).sort((a, b) => {
    const ia = KNOWN.indexOf(a);
    const ib = KNOWN.indexOf(b);
    return (ia < 0 ? KNOWN.length : ia) - (ib < 0 ? KNOWN.length : ib) || a.localeCompare(b);
  });
  return [
    { value: TEMPLATE_DEFINED, label: PACKS_DECIDE, help: PACKS_DECIDE_HELP },
    ...keys.map((key) => ({ value: key, label: key, help: strategies[key]?.description ?? undefined })),
  ];
}

/** The preselected choice: a check constraint when the project declares it, else the project default, else Let the packs decide. */
export function preselectedStorage(strategies: DeclaredStrategies, project?: StorageChoice | null): string {
  if (PRESELECTED_STRATEGY in strategies) return PRESELECTED_STRATEGY;
  if (project?.strategy && project.strategy in strategies) return project.strategy;
  return TEMPLATE_DEFINED;
}

/** The new type's `storage` for a "Stored as" value; none while the project declares no strategy (the packs decide anyway). */
export function storageFor(value: string, strategies: DeclaredStrategies): Record<string, StorageChoice> | undefined {
  if (Object.keys(strategies).length === 0) return undefined;
  return { "*": value === TEMPLATE_DEFINED ? {} : { strategy: value } };
}

/**
 * The effective storage the type picker shows beside a type's name, for every database: the type's own choice for
 * all databases, else the project default, else "packs decide"; "+N" counts the databases the type sets otherwise.
 */
export function storageLabel(storage: Record<string, StorageChoice> | undefined, project?: StorageChoice | null): string {
  const own = storage?.["*"];
  const strategy = own ? (own.strategy ?? null) : (project?.strategy ?? null);
  const label = strategy ?? "packs decide";
  const others = Object.entries(storage ?? {}).filter(([key, choice]) => key !== "*" && (choice.strategy ?? null) !== strategy).length;
  return others ? `${label} +${others}` : label;
}

/**
 * The standard strategies the sql-ddl pack realizes, as `maquettiste init --pack sql-ddl` declares them
 * (StarterPacks.StandardReferenceStrategies): Settings offers them in one click to a project that declares none.
 */
export const STANDARD_STRATEGIES = {
  "lookup-table": {
    description: "Table keyed by code, FK from each column",
    collections: true,
    options: { schema: { type: "string" }, tableName: { type: "string" } },
  },
  check: { description: "CHECK (col IN (...codes))" },
  native: { description: "CREATE TYPE ... AS ENUM on PostgreSQL", collections: { "*": false, postgresql: true } },
} as const;

/** Settings with the standard strategies added (the ones already declared are kept as they are). */
export function withStandardStrategies<T extends { referenceData?: { strategies?: Record<string, unknown> | null } | null }>(settings: T): T {
  const referenceData = { ...(settings.referenceData ?? {}) };
  const strategies = { ...(referenceData.strategies ?? {}) };
  for (const [key, value] of Object.entries(STANDARD_STRATEGIES)) if (!(key in strategies)) strategies[key] = structuredClone(value);
  return { ...settings, referenceData: { ...referenceData, strategies } };
}

/** Whether Settings shows "Declare the standard storage strategies": the project declares no strategy at all. */
export function needsStandardStrategies(settings: { referenceData?: { strategies?: Record<string, unknown> | null } | null } | undefined): boolean {
  return !!settings && Object.keys(settings.referenceData?.strategies ?? {}).length === 0;
}

/**
 * The statements of a rendered seed script that realize one reference type: the blank-line separated statements that
 * name its table or native type (snake case, singular or plural) or quote one of its codes. The preview on the
 * Storage tab shows these, so it follows the realization the pack picks for the type.
 */
export function typeStatements(script: string, names: readonly string[], codes: readonly string[]): string[] {
  const words = names.filter(Boolean).map((n) => n.toLowerCase());
  const quoted = codes.map((c) => `'${c.replaceAll("'", "''")}'`);
  return script
    .split(/\n\s*\n/)
    .map((s) => s.trim())
    .filter((s) => s && !s.startsWith("-- Generated") && !s.startsWith("-- maquettiste:"))
    .filter((s) => {
      const lower = s.toLowerCase();
      return words.some((w) => new RegExp(`\\b${w}(_t)?\\b`).test(lower)) || quoted.some((q) => s.includes(q));
    });
}

/** snake_case of a type name, and its plural from the plural name or a trailing "s": the names the pack's objects use. */
export function snakeNames(name: string, pluralName?: string | null): string[] {
  const snake = (s: string) =>
    s
      .replace(/([a-z0-9])([A-Z])/g, "$1_$2")
      .replace(/[\s-]+/g, "_")
      .toLowerCase();
  const one = snake(name);
  const many = pluralName ? snake(pluralName) : `${one}s`;
  return one === many ? [one] : [one, many];
}

// ------------------------------------------------------------------ the Storage tab's words

/** Where the strategy in use comes from, in plain words. */
export const SOURCE_WORDS = { type: "from this type", database: "from the database", project: "from the project" } as const;

/** The Storage tab's "Strategy in use" text: the strategy (or that the packs decide) and where that comes from. */
export function strategyInUse(effective: { strategy: string | null; source: keyof typeof SOURCE_WORDS | null }): string {
  const what = effective.strategy ?? "The packs decide";
  return effective.source ? `${what}, ${SOURCE_WORDS[effective.source]}` : `${what}, nothing is set`;
}

/** The "Use the default (…)" option: what the row gets when this type sets nothing for it. */
export function defaultOption(fallback: { strategy: string | null; source: keyof typeof SOURCE_WORDS | null }): string {
  if (!fallback.source) return "Use the default (the packs decide)";
  const what = fallback.strategy ?? "the packs decide";
  const from = fallback.source === "type" ? "as set for all databases" : SOURCE_WORDS[fallback.source];
  return `Use the default (${what}, ${from})`;
}

/** A unit the Storage tab can preview: one rendered per database (`each database` or `select databases`). */
export interface DatabaseUnit {
  pack: string;
  unit: string;
  for: string;
}

/** The database-scoped units of the enabled packs, in pack then unit order (from the pack list; no pack or unit is assumed). */
export function databaseUnits(packs: readonly { name: string; enabled?: boolean; units: readonly { id: string; for: string }[] }[]): DatabaseUnit[] {
  const scoped = (scope: string) => /^(each database|select databases?)$/.test(scope.trim().replace(/\s+/g, " "));
  return packs.filter((p) => p.enabled !== false).flatMap((p) => p.units.filter((u) => scoped(u.for)).map((u) => ({ pack: p.name, unit: u.id, for: u.for })));
}

/** The unit preselected for the preview: a unit whose name mentions seed (where packs write reference rows), else the first. */
export function preferredUnit(units: readonly DatabaseUnit[]): DatabaseUnit | null {
  return units.find((u) => /seed/i.test(u.unit)) ?? units[0] ?? null;
}

/** Reasons of the explain answer that mean the unit would render (so a failed preview failed for another reason). */
const RENDERS = new Set(["new", "forced", "check", "inputs", "outputs", "unchanged"]);

/**
 * Why a preview failed, in the engine's words: the explain answer's detail when it says why the unit does not render
 * for the database (its scope, its selector, a disabled pack…), else the preview's own message.
 */
export function previewFailure(message: string, explained: { reason: string; detail: string } | null): string {
  if (explained && !RENDERS.has(explained.reason) && explained.detail) return explained.detail;
  return message;
}
