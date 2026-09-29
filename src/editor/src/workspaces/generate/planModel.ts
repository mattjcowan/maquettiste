// The plan explanation on the Generate screen (generation-ui.md 4): every planned change with its pack, unit, template,
// element, output path and the reason its unit renders; the changes grouped by unit with counts; filters; and the
// summary line per pack ("sql-ddl: 4 units, 12 files to add, 3 to modify, 1 orphan to delete"). Pure: no React.
import type { FileChange, FileChangeKind, GenerationPlan, PlanUnit } from "@/api/types";

/** One planned change with what explains it. */
export interface PlanRow {
  change: FileChange;
  /** `<pack>/<unit>`, the unit definition the change comes from; `<pack>/(orphans)` for a file no unit produces. */
  group: string;
  unit: PlanUnit | null;
  why: string;
}

/** The changes of one unit definition, with counts by kind. */
export interface UnitGroup {
  id: string;
  pack: string;
  unit: string;
  template: string | null;
  rows: PlanRow[];
  counts: Partial<Record<FileChangeKind, number>>;
  /** Unit instances (elements) in this group that render in this plan. */
  rendering: number;
  /** Unit instances the plan skips because nothing they read changed. */
  skipped: number;
}

export interface PlanFilter {
  /** "changed" (everything but unchanged), "" (everything) or one FileChangeKind. */
  kind: string;
  pack: string;
  /** A group id (`<pack>/<unit>`) or "". */
  unit: string;
  /** Words matched (case-insensitively) against path, element, template and the why sentence. */
  text: string;
}

export const ORPHANS = "(orphans)";

/** `<pack>/<unit>` from a unit key `<pack>/<unit>[:<element or table key>]`. */
export function groupOf(unitKey: string): string {
  const colon = unitKey.indexOf(":");
  return colon < 0 ? unitKey : unitKey.slice(0, colon);
}

const REASON_TEXT: Record<string, string> = {
  new: "New: no recorded state from an earlier run",
  forced: "Forced: the run renders every unit",
  check: "Check run: every unit renders to compare",
  inputs: "An input it read changed",
  outputs: "An output differs from what was generated",
  unchanged: "Unchanged: nothing it read changed",
};

/** The one-sentence Why of a unit: its first cause, "+N" when there are more, else its reason in words. */
export function whySentence(unit: PlanUnit | null | undefined): string {
  if (!unit) return "No unit produces this file any more: an orphan";
  const first = unit.causes[0];
  if (first) {
    const more = Math.max(unit.causeCount, unit.causes.length) - 1;
    return more > 0 ? `${first.detail} +${more}` : first.detail;
  }
  if (!unit.reason) return "Reason not recorded (a plan made before explanations)";
  return REASON_TEXT[unit.reason] ?? unit.reason;
}

/** The changes grouped by unit definition (pack, then unit, ordinal), each row carrying its unit and Why. */
export function groupPlan(plan: Pick<GenerationPlan, "changes" | "units">): UnitGroup[] {
  const units = new Map(plan.units.map((u) => [u.key, u]));
  const groups = new Map<string, UnitGroup>();
  const ensure = (id: string, template: string | null): UnitGroup => {
    let g = groups.get(id);
    if (!g) {
      const slash = id.indexOf("/");
      g = { id, pack: id.slice(0, slash), unit: id.slice(slash + 1), template, rows: [], counts: {}, rendering: 0, skipped: 0 };
      groups.set(id, g);
    }
    if (!g.template && template) g.template = template;
    return g;
  };
  for (const u of plan.units) {
    const g = ensure(u.pack && u.unit ? `${u.pack}/${u.unit}` : groupOf(u.key), u.template);
    if (u.skipped) g.skipped++;
    else g.rendering++;
  }
  for (const change of plan.changes) {
    const unit = units.get(change.unitKey) ?? null;
    const id = unit ? (unit.pack && unit.unit ? `${unit.pack}/${unit.unit}` : groupOf(unit.key)) : `${change.pack}/${ORPHANS}`;
    const g = ensure(id, unit?.template ?? null);
    g.rows.push({ change, group: id, unit, why: whySentence(unit) });
    g.counts[change.kind] = (g.counts[change.kind] ?? 0) + 1;
  }
  const ordinal = (a: string, b: string) => (a < b ? -1 : a > b ? 1 : 0);
  for (const g of groups.values()) g.rows.sort((a, b) => ordinal(a.change.path, b.change.path));
  return [...groups.values()].sort((a, b) => ordinal(a.pack, b.pack) || ordinal(a.unit, b.unit));
}

function rowMatches(row: PlanRow, filter: PlanFilter): boolean {
  const kind = row.change.kind;
  if (filter.kind === "changed" ? kind === "unchanged" : filter.kind && kind !== filter.kind) return false;
  if (filter.pack && row.change.pack !== filter.pack) return false;
  if (filter.unit && row.group !== filter.unit) return false;
  const words = filter.text.toLowerCase().split(/\s+/).filter(Boolean);
  if (!words.length) return true;
  const hay = [row.change.path, row.unit?.elementId ?? "", row.unit?.template ?? "", row.group, row.why].join(" ").toLowerCase();
  return words.every((w) => hay.includes(w));
}

/** The groups with only the rows the filter keeps; a group left with no rows is dropped. */
export function filterGroups(groups: UnitGroup[], filter: PlanFilter): UnitGroup[] {
  const out: UnitGroup[] = [];
  for (const g of groups) {
    const rows = g.rows.filter((r) => rowMatches(r, filter));
    if (rows.length) out.push({ ...g, rows });
  }
  return out;
}

/** The virtualized list: a header per group, then its rows unless the group is collapsed. */
export type PlanItem = { type: "group"; group: UnitGroup; collapsed: boolean } | { type: "row"; row: PlanRow };

export function flattenGroups(groups: UnitGroup[], collapsed: ReadonlySet<string>): PlanItem[] {
  const items: PlanItem[] = [];
  for (const group of groups) {
    const closed = collapsed.has(group.id);
    items.push({ type: "group", group, collapsed: closed });
    if (!closed) for (const row of group.rows) items.push({ type: "row", row });
  }
  return items;
}

const plural = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`;

/** Counts in words for a group header: "8 to add, 4 to modify, 2 unchanged". */
export function countsText(counts: Partial<Record<FileChangeKind, number>>): string {
  const order: [FileChangeKind, string][] = [
    ["added", "to add"],
    ["modified", "to modify"],
    ["deleted", "to delete"],
    ["hand-edited", "edited by hand"],
    ["conflict", "in conflict"],
    ["orphaned-owned", "orphaned, kept"],
    ["kept", "kept"],
    ["unchanged", "unchanged"],
  ];
  return order
    .filter(([k]) => counts[k])
    .map(([k, words]) => `${counts[k]} ${words}`)
    .join(", ");
}

/**
 * The summary line of one pack: "sql-ddl: 4 units, 12 files to add, 3 to modify, 1 orphan to delete". Units are the
 * unit instances that render; units the plan skips and files that stay as they are come last.
 */
export function packSummaryLine(pack: string, plan: Pick<GenerationPlan, "changes" | "units">): string {
  const units = plan.units.filter((u) => (u.pack ?? groupOf(u.key).split("/")[0]) === pack);
  const rendering = units.filter((u) => !u.skipped).length;
  const skipped = units.length - rendering;
  const count = (kind: FileChangeKind) => plan.changes.filter((c) => c.pack === pack && c.kind === kind).length;
  const parts = [plural(rendering, "unit", "units")];
  let noun = true;
  const files = (n: number, words: string) => {
    if (!n) return;
    parts.push(noun ? `${plural(n, "file", "files")} ${words}` : `${n} ${words}`);
    noun = false;
  };
  files(count("added"), "to add");
  files(count("modified"), "to modify");
  const orphans = count("deleted");
  if (orphans) parts.push(`${plural(orphans, "orphan", "orphans")} to delete`);
  files(count("hand-edited"), "edited by hand");
  const conflicts = count("conflict");
  if (conflicts) parts.push(plural(conflicts, "conflict", "conflicts"));
  const owned = count("orphaned-owned");
  if (owned) parts.push(`${plural(owned, "owned file", "owned files")} orphaned (kept)`);
  if (parts.length === 1) parts.push("nothing to write");
  if (skipped) parts.push(`${plural(skipped, "unit", "units")} unchanged`);
  return `${pack}: ${parts.join(", ")}`;
}

/** One summary line per pack of the plan, in the plan's pack order, plus packs that only have orphans. */
export function planSummary(plan: Pick<GenerationPlan, "packs" | "changes" | "units">): string[] {
  const packs = [...new Set([...plan.packs, ...plan.changes.map((c) => c.pack)])];
  return packs.map((p) => packSummaryLine(p, plan));
}
