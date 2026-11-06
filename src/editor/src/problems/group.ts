// Problems: the live whole-model validation report plus the diagnostics of drafts that could not
// be saved, grouped by element.
import type { Diagnostic, ElementSummary } from "@/api/types";
import type { Draft } from "@/state/store";
import { displayName } from "@/model/model";

export interface ProblemGroup {
  key: string;
  elementId: string | null;
  label: string;
  unsaved: boolean;
  diagnostics: Diagnostic[];
}

const SEVERITY_ORDER = { error: 0, warning: 1, info: 2 } as const;

const findingKey = (element: string | null, d: Diagnostic) => `${element ?? ""}|${d.rule}|${d.jsonPointer ?? ""}|${d.message}`;

export function groupProblems(report: Diagnostic[], drafts: Draft[], byId: Map<string, ElementSummary>): ProblemGroup[] {
  const groups: ProblemGroup[] = [];
  for (const draft of drafts) {
    if (draft.status !== "invalid" && draft.status !== "conflict") continue;
    const diagnostics =
      draft.status === "conflict"
        ? [
            {
              rule: "conflict",
              severity: "error" as const,
              message: "Changed on disk since you loaded it; resolve the conflict to save.",
              elementId: draft.id,
              filePath: null,
              jsonPointer: null,
              line: null,
              column: null,
            },
          ]
        : draft.diagnostics.length
          ? draft.diagnostics
          : [
              {
                rule: "save",
                severity: "error" as const,
                message: draft.error ?? "The last save was refused.",
                elementId: draft.id,
                filePath: null,
                jsonPointer: null,
                line: null,
                column: null,
              },
            ];
    groups.push({
      key: `draft:${draft.id}`,
      elementId: draft.id,
      label: `${displayName(byId.get(draft.id) ?? { id: draft.id, kind: "entity", name: String((draft.json as { name?: string }).name ?? draft.id) })} (not saved)`,
      unsaved: true,
      diagnostics,
    });
  }
  // A finding of a part (a state, a subjects entry) carries the part's id: its file path names the owning element.
  const byPath = new Map<string, ElementSummary>();
  for (const summary of byId.values()) if (summary.path) byPath.set(summary.path, summary);
  const byElement = new Map<string, Diagnostic[]>();
  // A finding an unsaved draft reports again (the saved version has it too) is one finding: the draft's own is listed.
  const reported = new Set(groups.flatMap((g) => g.diagnostics.map((d) => findingKey(g.elementId, d))));
  for (const d of report) {
    const owner = (d.elementId ? byId.get(d.elementId) : undefined) ?? (d.filePath ? byPath.get(d.filePath) : undefined);
    const key = owner?.id ?? d.elementId ?? `file:${d.filePath ?? "model"}`;
    if (reported.has(findingKey(key, d))) continue;
    byElement.set(key, [...(byElement.get(key) ?? []), d]);
  }
  const saved = [...byElement.entries()].map(([key, diagnostics]) => {
    const summary = byId.get(key);
    return {
      key,
      elementId: summary ? key : null,
      label: summary ? displayName(summary) : (diagnostics[0].filePath ?? key),
      unsaved: false,
      diagnostics: [...diagnostics].sort((a, b) => SEVERITY_ORDER[a.severity] - SEVERITY_ORDER[b.severity]),
    };
  });
  saved.sort((a, b) => a.label.localeCompare(b.label));
  return [...groups, ...saved];
}

export function countBySeverity(groups: ProblemGroup[]): { errors: number; warnings: number; infos: number } {
  const all = groups.flatMap((g) => g.diagnostics);
  return {
    errors: all.filter((d) => d.severity === "error").length,
    warnings: all.filter((d) => d.severity === "warning").length,
    infos: all.filter((d) => d.severity === "info").length,
  };
}
