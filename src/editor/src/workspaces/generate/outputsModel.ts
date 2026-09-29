// The Outputs tab's model (generation-ui.md 2.1 and 3.4), pure: the pack's manifest entries with the state the UI
// shows. The manifest says intact, edited or missing; the editor adds orphan: the entry's unit id is no longer in
// pack.json, or its element is no longer in the model index (a plan's Deleted changes remain the complete answer).
import type { components } from "@/api/schema";

type S = components["schemas"];
export type OutputState = "clean" | "hand-edited" | "missing" | "orphan";

export const STATE_LABEL: Record<OutputState, string> = {
  clean: "clean",
  "hand-edited": "hand-edited",
  missing: "missing",
  orphan: "orphan",
};

export type PackOutput = S["PackOutput"];
export interface OutputRow extends PackOutput {
  display: OutputState;
}

export function outputRows(outputs: S["PackOutput"][], unitIds: Iterable<string>, hasElement: (id: string) => boolean): OutputRow[] {
  const units = new Set(unitIds);
  return outputs.map((o) => {
    const orphan = !units.has(o.unit) || (o.elementId !== null && !hasElement(o.elementId));
    const display: OutputState = orphan ? "orphan" : o.state === "edited" ? "hand-edited" : o.state === "missing" ? "missing" : "clean";
    return { ...o, display };
  });
}

export interface OutputGroup {
  key: string;
  rows: OutputRow[];
  /** Counts of the states other than clean. */
  states: Partial<Record<OutputState, number>>;
}

/** Groups rows by unit (or by root), in first-seen order of the sorted rows; files sort by path. */
export function groupOutputs(rows: OutputRow[], by: "unit" | "root"): OutputGroup[] {
  const groups = new Map<string, OutputGroup>();
  for (const row of [...rows].sort((a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0))) {
    const key = (by === "unit" ? row.unit : row.root) ?? "(no root)";
    let group = groups.get(key);
    if (!group) groups.set(key, (group = { key, rows: [], states: {} }));
    group.rows.push(row);
    if (row.display !== "clean") group.states[row.display] = (group.states[row.display] ?? 0) + 1;
  }
  return [...groups.values()].sort((a, b) => (a.key < b.key ? -1 : a.key > b.key ? 1 : 0));
}

/** "412 files · 1 hand-edited". */
export function groupLine(group: OutputGroup): string {
  const n = group.rows.length;
  const extra = (Object.entries(group.states) as [OutputState, number][]).map(([s, c]) => `${c} ${STATE_LABEL[s]}`);
  return [`${n} ${n === 1 ? "file" : "files"}`, ...extra].join(" · ");
}
