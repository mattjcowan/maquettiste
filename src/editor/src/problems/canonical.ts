// Rewriting model files in canonical form from the editor (POST /api/model/format, what `maquettiste format` does): the Problems
// panel's quick fix on an MQ1003 or MQ1010 finding, and the Settings action for every model file. A format changes no content,
// only bytes (key order, indentation, the retired `commit` flag of maquettiste.json), so it adds no undo step: there is nothing
// to put back. The server publishes model.changed (and project.changed for maquettiste.json); the caches are refreshed here too.
import type { QueryClient } from "@tanstack/react-query";
import * as endpoints from "@/api/endpoints";
import { keys } from "@/api/queries";
import type { Diagnostic, ModelFormatResult } from "@/api/types";

/** MQ1003 (not in canonical form) and MQ1010 (a retired settings member, which the canonical writer drops). */
export const CANONICAL_RULES: ReadonlySet<string> = new Set(["MQ1003", "MQ1010"]);

/** Whether a finding offers "Rewrite in canonical form": one of the rules, located in a file. */
export const hasCanonicalFix = (d: Pick<Diagnostic, "rule" | "filePath">): boolean => CANONICAL_RULES.has(d.rule) && !!d.filePath;

/** The distinct files the report says are not in canonical form, ordinal (the Settings action's count). */
export function nonCanonicalFiles(diagnostics: readonly Pick<Diagnostic, "rule" | "filePath">[]): string[] {
  return [...new Set(diagnostics.filter(hasCanonicalFix).map((d) => d.filePath!))].sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
}

/** The notice after a format: what was rewritten, and what was left as it is. */
export function formatNotice(result: Pick<ModelFormatResult, "formatted" | "skipped">): { message: string; level: "info" | "error" } {
  const n = result.formatted.length;
  const done =
    n === 0
      ? "Every model file was already in canonical form."
      : n === 1
        ? `Rewrote ${result.formatted[0]} in canonical form.`
        : `Rewrote ${n} model files in canonical form.`;
  if (!result.skipped.length) return { message: done, level: "info" };
  const left = result.skipped.length === 1 ? `${result.skipped[0]} was left as it is` : `${result.skipped.length} files were left as they are`;
  return { message: `${n ? done + " " : ""}${left}: fix its other problems first (it does not pass its schema).`, level: "error" };
}

/** Formats the files (all model files when `paths` is absent) and refreshes what depends on them. */
export async function formatModelFiles(qc: QueryClient, paths?: string[]): Promise<ModelFormatResult> {
  const result = await endpoints.formatModel(paths);
  if (result.formatted.length) {
    void qc.invalidateQueries({ queryKey: keys.settings });
    void qc.invalidateQueries({ queryKey: keys.project });
    void qc.invalidateQueries({ queryKey: ["element"] });
  }
  void qc.invalidateQueries({ queryKey: keys.validation });
  return result;
}
