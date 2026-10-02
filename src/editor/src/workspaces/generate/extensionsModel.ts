// The model's extension files as data, pure: the custom property schemas (`extensions/<name>.json`) and the script rules
// (`extensions/rules/<name>.js`), their rows in the Generate explorer, the paths the editor accepts, and the minimal valid
// file each New command writes. The Extensions tab (ExtensionsScreen.tsx) and the tests share it.
import type { components } from "@/api/schema";
import type { Diagnostic, ElementKind } from "@/api/types";

type S = components["schemas"];
export type ExtensionFile = S["ExtensionFileInfo"];
export type ExtensionKind = "schema" | "rule";

/** The two shapes the engine loads: a schema at the top of extensions/, a rule directly in extensions/rules/. */
const PATH = /^(?:[A-Za-z0-9][A-Za-z0-9._-]*\.json|rules\/[A-Za-z0-9][A-Za-z0-9._-]*\.js)$/;

export const KIND_LABEL: Record<ExtensionKind, string> = { schema: "Custom properties", rule: "Script rule" };

export function kindOfPath(path: string): ExtensionKind {
  return path.startsWith("rules/") ? "rule" : "schema";
}

/** Why a path cannot be created or taken by a rename, or null when it can. */
export function extensionPathProblem(path: string, kind: ExtensionKind, taken: readonly string[]): string | null {
  if (!PATH.test(path))
    return kind === "rule"
      ? "A script rule is rules/<name>.js: letters, digits, dots, hyphens and underscores."
      : "A custom property schema is <name>.json: letters, digits, dots, hyphens and underscores.";
  if (kindOfPath(path) !== kind) return kind === "rule" ? "A script rule stays under rules/ and ends in .js." : "A schema stays at the top and ends in .json.";
  if (taken.includes(path)) return `${path} already exists.`;
  return null;
}

/** The first free `<base>.json` or `rules/<base>.js` (then `-2`, `-3`, ...). */
export function freePath(kind: ExtensionKind, taken: readonly string[]): string {
  const at = (n: number) => (kind === "rule" ? `rules/new-rule${n > 1 ? `-${n}` : ""}.js` : `new-properties${n > 1 ? `-${n}` : ""}.json`);
  let n = 1;
  while (taken.includes(at(n))) n++;
  return at(n);
}

/** The bare name of a path: `rules/no-draft.js` → `no-draft`. */
export function baseName(path: string): string {
  return path.replace(/^rules\//, "").replace(/\.(json|js)$/, "");
}

/** A valid schema with one property, which the inspector shows at once for entities. */
export function schemaTemplate(path: string): string {
  const name =
    baseName(path)
      .toLowerCase()
      .replace(/[^a-z0-9-]+/g, "-") || "properties";
  return `${JSON.stringify(
    {
      name,
      description: "What these custom properties record.",
      appliesTo: { kinds: ["entity"] },
      properties: { owner: { type: "string", description: "Who owns this data." } },
    },
    null,
    2,
  )}\n`;
}

/** A rule that reports nothing until edited, with the contract in comments. */
export function ruleTemplate(path: string): string {
  const id =
    baseName(path)
      .toLowerCase()
      .replace(/[^a-z0-9-]+/g, "-") || "my-rule";
  return `// A script rule. Validation calls check once for each element of the listed kinds.
//   id        The rule's id: its findings show as x/${id} in Problems, in maquettiste validate and in SARIF.
//   severity  "error" (stops generation), "warning" or "info". Settings > Validation can override it.
//   kinds     The element kinds it checks, such as "entity" or "relation"; leave it out to check every kind.
//   check(element, model, report)
//     element  The element's JSON as saved, read-only.
//     model    model.get(id), model.all(kind) and model.referencesTo(id), read-only.
//     report(message, { pointer, severity })  One finding. pointer is a JSON pointer into the element, such as "/name".
// The script runs in a sandbox: no files, no network, a fixed clock, and limits on time, memory and statements.
maquettiste.rule({
  id: "${id}",
  severity: "warning",
  kinds: ["entity"],
  check(element, model, report) {
    // For example: if (!element.description) report("Describe this entity.", { pointer: "/name" });
  },
});
`;
}

/** The repo-relative path of an extension file, as diagnostics carry it. */
export function repoPathOf(folder: string, path: string): string {
  return `${folder}/${path}`;
}

/** The extension file a diagnostic is about (its file is under the extensions folder), or null. */
export function extensionPathOf(diagnostic: Pick<Diagnostic, "filePath">, folder = ".maquettiste/extensions"): string | null {
  const file = diagnostic.filePath;
  if (!file || !file.startsWith(`${folder}/`)) return null;
  const path = file.slice(folder.length + 1);
  return PATH.test(path) ? path : null;
}

/**
 * The findings the Extensions tab lists for one file: what the file list says about it (an invalid schema, a rule that does
 * not load), what its last save answered, and the validation report's findings located in the file, each once.
 */
export function findingsFor(path: string, folder: string, sources: { listed?: Diagnostic[]; saved?: Diagnostic[]; report?: Diagnostic[] }): Diagnostic[] {
  const file = repoPathOf(folder, path);
  const all = [...(sources.saved ?? []), ...(sources.listed ?? []), ...(sources.report ?? []).filter((d) => d.filePath === file)];
  const seen = new Set<string>();
  return all.filter((d) => {
    const key = [d.rule, d.message, d.line, d.column, d.elementId].join("|");
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

/** The x/<id> findings of the rules a file registers, from the validation report. */
export function ruleFindings(file: Pick<ExtensionFile, "rules">, report: readonly Diagnostic[]): Diagnostic[] {
  const ids = new Set(file.rules.map((r) => r.id));
  return report.filter((d) => ids.has(d.rule));
}

/** The element kinds a rule's `kinds` may list (completion). */
export const RULE_KINDS: readonly ElementKind[] = [
  "package",
  "entity",
  "value-object",
  "scalar-type",
  "enum",
  "relation",
  "database",
  "table",
  "view",
  "sequence",
  "routine",
  "database-type",
  "sql-object",
  "mapping",
  "diagram",
  "tag-vocabulary",
  "category-tree",
  "stereotype",
  "reference-type",
  "seed",
  "process",
  "actor",
  "scenario",
];

/** One row of the Extensions node in the Generate explorer. */
export interface ExtensionRow {
  key: string;
  kind: "extensions" | "extension-file";
  level: number;
  label: string;
  detail: string;
  expandable: boolean;
  expanded: boolean;
  setsize: number;
  posinset: number;
  path?: string;
  warnings?: number;
  mono?: boolean;
}

export const EXTENSIONS_KEY = "x:extensions";

/** The Extensions node and, when expanded, one row per file (schemas first, then rules). */
export function extensionRows(files: readonly ExtensionFile[] | undefined, expanded: ReadonlySet<string>, setsize: number, posinset: number): ExtensionRow[] {
  const open = expanded.has(EXTENSIONS_KEY);
  const list = files ?? [];
  const schemas = list.filter((f) => f.kind === "schema").length;
  const rules = list.length - schemas;
  const rows: ExtensionRow[] = [
    {
      key: EXTENSIONS_KEY,
      kind: "extensions",
      level: 1,
      label: "Extensions",
      detail: files ? `${schemas} ${schemas === 1 ? "schema" : "schemas"} · ${rules} ${rules === 1 ? "rule" : "rules"}` : "",
      expandable: true,
      expanded: open,
      setsize,
      posinset,
      warnings: list.reduce((n, f) => n + f.diagnostics.length, 0) || undefined,
    },
  ];
  if (!open) return rows;
  list.forEach((f, i) =>
    rows.push({
      key: `${EXTENSIONS_KEY}/${f.path}`,
      kind: "extension-file",
      level: 2,
      label: f.path,
      detail: f.kind === "rule" ? f.rules.map((r) => r.id).join(", ") || "script rule" : "custom properties",
      expandable: false,
      expanded: false,
      setsize: list.length,
      posinset: i + 1,
      path: f.path,
      warnings: f.diagnostics.length || undefined,
      mono: true,
    }),
  );
  return rows;
}
