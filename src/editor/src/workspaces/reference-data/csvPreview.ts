// The CSV import preview (reference-types-seeds-localization.md 2.3): what the dry run says the import adds, changes
// and removes, as the dialog lists it before the one batch that applies it.
import type { ImportPreview } from "@/api/types";

export interface ChangedRow {
  id: string;
  fields: { name: string; before: string; after: string }[];
}

export interface PreviewSummary {
  headline: string;
  changed: ChangedRow[];
  errors: string[];
  warnings: string[];
  ignoredHeaders: string[];
  blocked: number;
  /** Whether applying would change anything and nothing blocks it (no error diagnostic). */
  canApply: boolean;
}

const text = (v: unknown) => (v === null || v === undefined ? "" : typeof v === "string" ? v : JSON.stringify(v));
const plural = (n: number, one: string, many = `${one}s`) => `${n} ${n === 1 ? one : many}`;

export function previewSummary(preview: ImportPreview): PreviewSummary {
  const changed: ChangedRow[] = (preview.changed ?? []).map((c) => {
    const before = (c.before ?? {}) as Record<string, unknown>;
    const after = (c.after ?? {}) as Record<string, unknown>;
    const names = [...new Set([...Object.keys(before), ...Object.keys(after)])];
    return { id: String(c.id ?? ""), fields: names.map((name) => ({ name, before: text(before[name]), after: text(after[name]) })) };
  });
  const errors = preview.diagnostics.filter((d) => d.severity === "error").map((d) => `${d.rule}: ${d.message}`);
  const warnings = preview.diagnostics.filter((d) => d.severity !== "error").map((d) => `${d.rule}: ${d.message}`);
  const parts = [`${plural(preview.added, "row")} added`, `${plural(changed.length, "row")} changed`, `${plural(preview.removed, "row")} removed`];
  const nothing = preview.added === 0 && changed.length === 0 && preview.removed === 0;
  return {
    headline: nothing ? "The file matches the rows: nothing to import." : parts.join(", "),
    changed,
    errors,
    warnings,
    ignoredHeaders: preview.ignoredHeaders ?? [],
    blocked: preview.blocked?.length ?? 0,
    canApply: !nothing && errors.length === 0,
  };
}
