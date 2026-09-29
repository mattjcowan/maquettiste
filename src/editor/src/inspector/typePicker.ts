// The field type picker's model (reference-types-seeds-localization.md 4.5): one ranked search over sections Recent,
// Built-in, Custom types, Enums, Reference data and Value objects. Pure, so it is tested alone.
import type { ElementKind, ElementSummary } from "@/api/types";
import { BUILTIN_TYPES } from "@/model/model";

export type PickerSection = "recent" | "builtin" | "scalar-type" | "enum" | "reference-type" | "value-object";

export const SECTION_LABELS: Record<PickerSection, string> = {
  recent: "Recent",
  builtin: "Built-in",
  "scalar-type": "Custom types",
  enum: "Enums",
  "reference-type": "Reference data",
  "value-object": "Value objects",
};

const ORDER: PickerSection[] = ["recent", "builtin", "scalar-type", "enum", "reference-type", "value-object"];

export interface PickerItem {
  /** The attribute grid's type value: a built-in name, or `ref:<id>`. */
  value: string;
  label: string;
  /** The element's name when the label is its display name. */
  detail?: string;
  kind: ElementKind | "builtin";
}

export interface PickerGroup {
  section: PickerSection;
  label: string;
  items: PickerItem[];
}

const RECENT_LIMIT = 5;
let recent: string[] = [];

/** Remembers a pick for the Recent section (this window only). */
export function rememberPick(value: string): void {
  recent = [value, ...recent.filter((v) => v !== value)].slice(0, RECENT_LIMIT);
}

export function recentPicks(): readonly string[] {
  return recent;
}

export function clearRecentPicks(): void {
  recent = [];
}

/** 0 for a prefix match of the label or name, 1 for a match inside, -1 for none. */
function rank(item: PickerItem, term: string): number {
  if (!term) return 0;
  const texts = [item.label.toLowerCase(), (item.detail ?? "").toLowerCase()];
  if (texts.some((t) => t.startsWith(term))) return 0;
  if (texts.some((t) => t.includes(term))) return 1;
  return -1;
}

/** The sections with their matching items, prefix matches first, then A to Z; empty sections are left out. */
export function pickerGroups(options: readonly ElementSummary[], query: string, recentValues: readonly string[] = recent): PickerGroup[] {
  const term = query.trim().toLowerCase();
  const items: PickerItem[] = [
    ...BUILTIN_TYPES.map((t) => ({ value: t, label: t, kind: "builtin" as const })),
    ...options.map((o) => ({
      value: `ref:${o.id}`,
      label: o.displayName || o.name,
      detail: o.displayName && o.displayName !== o.name ? o.name : undefined,
      kind: o.kind,
    })),
  ];
  const byValue = new Map(items.map((i) => [i.value, i]));
  const groups: PickerGroup[] = [];
  for (const section of ORDER) {
    const pool =
      section === "recent"
        ? term
          ? []
          : recentValues.map((v) => byValue.get(v)).filter((i): i is PickerItem => !!i)
        : items.filter((i) => (section === "builtin" ? i.kind === "builtin" : i.kind === section));
    const ranked = pool
      .map((item) => ({ item, r: rank(item, term) }))
      .filter((x) => x.r >= 0)
      .sort((a, b) => (section === "builtin" || section === "recent" ? a.r - b.r : a.r - b.r || a.item.label.localeCompare(b.item.label)))
      .map((x) => x.item);
    if (ranked.length) groups.push({ section, label: SECTION_LABELS[section], items: ranked });
  }
  return groups;
}

/** The items in display order, for the arrow keys. */
export function flatItems(groups: readonly PickerGroup[]): PickerItem[] {
  return groups.flatMap((g) => g.items);
}
