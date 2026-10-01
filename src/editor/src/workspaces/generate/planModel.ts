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

/** File change kinds Apply has nothing to do for: the file on disk already holds the rendered bytes, or is kept. */
export const NOTHING_TO_WRITE: ReadonlySet<FileChangeKind> = new Set<FileChangeKind>(["unchanged", "kept"]);

/** True when Apply would write and delete nothing (every planned file is unchanged or kept, or there is none). */
export function nothingToWrite(plan: Pick<GenerationPlan, "changes">): boolean {
  return plan.changes.every((c) => NOTHING_TO_WRITE.has(c.kind));
}

/**
 * Why Apply has nothing to do, for the note under the summary; null when the plan writes something. A plan whose
 * files all match the disk is the usual case after an apply: the summary alone ("nothing to write") reads like a
 * failure without it.
 */
export function nothingToWriteNote(plan: Pick<GenerationPlan, "changes">): string | null {
  if (!nothingToWrite(plan)) return null;
  if (!plan.changes.length) return "This plan renders no files; Apply has nothing to do.";
  const kept = plan.changes.some((c) => c.kind === "kept");
  return kept
    ? "Every file this plan renders is identical to the file on disk or kept as it is; Apply has nothing to do."
    : "Every file this plan renders is identical to the file on disk; Apply has nothing to do.";
}

/**
 * The summary line of one pack: "sql-ddl: 4 units, 12 files to add, 3 to modify, 1 orphan to delete, 20 files
 * unchanged". Units are the unit instances that render; files that already match the disk, then units the plan
 * skips, come last. A pack that writes nothing says why: "nothing to write: all 737 files already match the disk".
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
  const unchanged = count("unchanged");
  const kept = count("kept");
  if (parts.length === 1) {
    const same = unchanged === 1 ? "1 file already matches the disk" : unchanged ? `all ${unchanged} files already match the disk` : "";
    const detail = [same, kept ? `${plural(kept, "file", "files")} kept` : ""].filter(Boolean).join(", ");
    parts.push(detail ? `nothing to write: ${detail}` : "nothing to write");
  } else {
    if (unchanged) parts.push(`${plural(unchanged, "file", "files")} unchanged`);
    if (kept) parts.push(`${plural(kept, "file", "files")} kept`);
  }
  if (skipped) parts.push(`${plural(skipped, "unit", "units")} unchanged`);
  return `${pack}: ${parts.join(", ")}`;
}

/** One summary line per pack of the plan, in the plan's pack order, plus packs that only have orphans. */
export function planSummary(plan: Pick<GenerationPlan, "packs" | "changes" | "units">): string[] {
  const packs = [...new Set([...plan.packs, ...plan.changes.map((c) => c.pack)])];
  return packs.map((p) => packSummaryLine(p, plan));
}

/** Where a cause leads: the element, the template in its pack, the settings tab, the pack's parameter or unit. */
export type CauseLink =
  | { type: "element"; id: string }
  | { type: "template"; pack: string; path: string }
  | { type: "setting"; tab: string }
  | { type: "parameter"; pack: string; name: string }
  | { type: "unit"; pack: string; unit: string };

/** The planned file changes one cause explains: "Template table.scriban changed: 412 files". */
export interface CauseGroup {
  id: string;
  kind: string;
  detail: string;
  files: number;
  units: number;
  link: CauseLink | null;
}

/** Changes that write something (an unchanged file and a kept companion do not). */
const writes = (c: FileChange) => c.kind !== "unchanged" && c.kind !== "kept";

const SETTING_TABS: Record<string, string> = { conventions: "conventions", localization: "locales" };
const PACK_SCOPED = new Set(["parameter", "unit", "scripts", "output-base", "formatter", "pack-version"]);

/** The link of one cause of a unit, or null when it leads nowhere to edit (a deleted element, an output, the state). */
export function causeLink(cause: PlanUnit["causes"][number], unit: Pick<PlanUnit, "pack" | "unit">): CauseLink | null {
  if (cause.kind === "absent") return null;
  if (cause.elementId) return { type: "element", id: cause.elementId };
  if (cause.kind === "template" && cause.key.startsWith("t:")) {
    const rest = cause.key.slice(2);
    const slash = rest.indexOf("/");
    return slash > 0 ? { type: "template", pack: rest.slice(0, slash), path: rest.slice(slash + 1) } : null;
  }
  if (cause.kind === "setting" && cause.key.startsWith("s:")) return { type: "setting", tab: SETTING_TABS[cause.key.slice(2)] ?? "project" };
  if (cause.kind === "translation" || cause.kind === "localization") return { type: "setting", tab: "locales" };
  if (cause.kind === "parameter" && unit.pack) return { type: "parameter", pack: unit.pack, name: cause.key };
  if (cause.kind === "unit" && unit.pack) return { type: "unit", pack: unit.pack, unit: cause.key };
  return null;
}

/**
 * The plan's written files grouped by cause, most files first (then by sentence, ordinal): each unit's causes (or, with
 * none recorded, its reason) count the files that unit writes. A unit with several causes counts in each of them.
 */
export function causeGroups(plan: Pick<GenerationPlan, "changes" | "units">): CauseGroup[] {
  const filesByUnit = new Map<string, number>();
  for (const c of plan.changes) if (writes(c)) filesByUnit.set(c.unitKey, (filesByUnit.get(c.unitKey) ?? 0) + 1);
  const groups = new Map<string, CauseGroup>();
  const add = (id: string, kind: string, detail: string, files: number, link: CauseLink | null) => {
    const g = groups.get(id) ?? { id, kind, detail, files: 0, units: 0, link };
    g.files += files;
    g.units++;
    groups.set(id, g);
  };
  for (const u of plan.units) {
    const files = filesByUnit.get(u.key) ?? 0;
    if (u.skipped || !files) continue;
    if (!u.causes.length) {
      add(`reason|${u.reason ?? ""}`, "reason", u.reason ? (REASON_TEXT[u.reason] ?? u.reason) : "Reason not recorded", files, null);
      continue;
    }
    for (const cause of u.causes) {
      const detail = cause.kind === "inputs" ? "Inputs changed (not recorded one by one)" : cause.detail;
      const scope = PACK_SCOPED.has(cause.kind) ? `${u.pack ?? ""}|` : "";
      add(`${cause.kind}|${scope}${cause.key}|${detail}`, cause.kind, detail, files, causeLink(cause, u));
    }
  }
  const ordinal = (a: string, b: string) => (a < b ? -1 : a > b ? 1 : 0);
  return [...groups.values()].sort((a, b) => b.files - a.files || ordinal(a.detail, b.detail));
}

/** "Template table.scriban changed: 412 files". */
export const causeSentence = (g: CauseGroup): string => `${g.detail}: ${plural(g.files, "file", "files")}`;

export interface RootGroup {
  root: string;
  files: number;
  counts: Partial<Record<FileChangeKind, number>>;
}

/**
 * The written files grouped by output root: the longest known root (a pack's output base) the path lies under, else the
 * path's first folder, else "(project root)". Ordinal by root.
 */
export function rootGroups(plan: Pick<GenerationPlan, "changes">, roots: readonly string[]): RootGroup[] {
  const known = [...new Set(roots.map((r) => r.replace(/\/+$/, "")).filter(Boolean))].sort((a, b) => b.length - a.length);
  const groups = new Map<string, RootGroup>();
  for (const c of plan.changes) {
    if (!writes(c)) continue;
    const root = known.find((r) => c.path.startsWith(`${r}/`)) ?? (c.path.includes("/") ? c.path.slice(0, c.path.indexOf("/")) : "(project root)");
    const g = groups.get(root) ?? { root, files: 0, counts: {} };
    g.files++;
    g.counts[c.kind] = (g.counts[c.kind] ?? 0) + 1;
    groups.set(root, g);
  }
  return [...groups.values()].sort((a, b) => (a.root < b.root ? -1 : a.root > b.root ? 1 : 0));
}

// Plan diagnostics under the summary: errors first, then warnings, then notes (info), each group in the engine's order;
// the first `limit` are listed and the rest counted (a project with several locales reports one MQ7204 note per shard).
const SEVERITY_RANK: Record<string, number> = { error: 0, warning: 1, info: 2 };

export type PlanNote = { severity: string; rule: string; message: string };

export function orderDiagnostics<T extends PlanNote>(
  diagnostics: readonly T[],
  limit = 5,
): { shown: T[]; more: number; counts: Record<"error" | "warning" | "info", number> } {
  const counts = { error: 0, warning: 0, info: 0 };
  for (const d of diagnostics) if (d.severity in counts) counts[d.severity as keyof typeof counts] += 1;
  const sorted = diagnostics
    .map((d, i) => [d, i] as const)
    .sort((a, b) => (SEVERITY_RANK[a[0].severity] ?? 3) - (SEVERITY_RANK[b[0].severity] ?? 3) || a[1] - b[1])
    .map(([d]) => d);
  return { shown: sorted.slice(0, limit), more: Math.max(0, sorted.length - limit), counts };
}

export function moreNotesText(more: number, counts: Record<"error" | "warning" | "info", number>): string {
  const parts = [
    counts.error ? plural(counts.error, "error", "errors") : "",
    counts.warning ? plural(counts.warning, "warning", "warnings") : "",
    counts.info ? plural(counts.info, "note", "notes") : "",
  ].filter(Boolean);
  return `+${more} more in Problems (${parts.join(", ")} in all)`;
}
