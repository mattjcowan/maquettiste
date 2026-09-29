// The storage choice where a reference type is created and used (reference-types-seeds-localization.md 1.4 and 4.5):
// the New reference type dialog's "Stored as" list (Template-defined, then the strategies the project declares, the
// sql-ddl pack's three by what they build) and the short effective storage the attribute type picker shows beside a
// type's name. The engine knows no strategy; the strategy keys are the project's. Pure, so it is tested alone.
import type { StorageChoice } from "@/api/types";

/** The "Stored as" value of Template-defined: a choice with no strategy, so the packs decide. */
export const TEMPLATE_DEFINED = "__template";

/** The strategy keys the sql-ddl pack maps by default, named by what they build, in this order. */
const KNOWN: [string, string][] = [
  ["lookup-table", "Lookup table"],
  ["check", "Check constraint"],
  ["native", "Native type (where the dialect has one)"],
];

/** The strategy preselected for a new type when the project declares it. */
export const PRESELECTED_STRATEGY = "check";

export interface StorageOption {
  value: string;
  label: string;
  title?: string;
}

export type DeclaredStrategies = Record<string, { description?: string | null }>;

/** Template-defined, then the declared strategies: the known ones in their order, then the others A to Z. */
export function storageOptions(strategies: DeclaredStrategies): StorageOption[] {
  const known = new Map(KNOWN);
  const keys = Object.keys(strategies).sort((a, b) => {
    const ia = KNOWN.findIndex(([k]) => k === a);
    const ib = KNOWN.findIndex(([k]) => k === b);
    return (ia < 0 ? KNOWN.length : ia) - (ib < 0 ? KNOWN.length : ib) || a.localeCompare(b);
  });
  return [
    { value: TEMPLATE_DEFINED, label: "Template-defined", title: "No strategy: the packs decide how the type is stored." },
    ...keys.map((key) => ({ value: key, label: known.get(key) ?? key, title: strategies[key]?.description ?? undefined })),
  ];
}

/** The preselected choice: a check constraint when the project declares it, else the project default, else Template-defined. */
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
 * all databases, else the project default, else "template"; "+N" counts the databases the type sets otherwise.
 */
export function storageLabel(storage: Record<string, StorageChoice> | undefined, project?: StorageChoice | null): string {
  const own = storage?.["*"];
  const strategy = own ? (own.strategy ?? null) : (project?.strategy ?? null);
  const label = strategy ?? "template";
  const others = Object.entries(storage ?? {}).filter(([key, choice]) => key !== "*" && (choice.strategy ?? null) !== strategy).length;
  return others ? `${label} +${others}` : label;
}
