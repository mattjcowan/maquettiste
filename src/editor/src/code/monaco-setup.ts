// Monaco, bundled locally (no CDN: local mode may be offline), with its workers built by Vite and
// themes built from the design tokens at runtime.
import * as monaco from "monaco-editor/editor/editor.api";
// The JSON language features load the editor's full set of contributions (code lens, drop and paste, ...) with their worker
// support the first time a JSON model opens. Loaded here, before the first editor exists, their services are registered when
// Monaco creates its services; loaded later, every editor created afterwards fails on services it does not know.
import "monaco-editor/internal/common/workers";
import { jsonDefaults } from "monaco-editor/languages/features/json/register";
import "monaco-editor/languages/definitions/sql/register";
import "monaco-editor/languages/definitions/csharp/register";
import "monaco-editor/languages/definitions/javascript/register";
import "monaco-editor/languages/definitions/markdown/register";
// The suggest and hover contributions (the editor API alone has neither), for Scriban completion and hover. The outline
// model service comes first: once these load, the diff editor's breadcrumbs contribution asks for it, and without it every
// diff editor throws "depends on UNKNOWN service IOutlineModelService".
import "monaco-editor/editor/contrib/documentSymbols/browser/outlineModel";
import "monaco-editor/editor/contrib/snippet/browser/snippetController2";
import "monaco-editor/editor/contrib/suggest/browser/suggestController";
import "monaco-editor/editor/contrib/hover/browser/hoverContribution";
// Signature help, for the report(...) and model.get(...) calls of script rules.
import "monaco-editor/editor/contrib/parameterHints/browser/parameterHints";
import EditorWorker from "monaco-editor/editor/editor.worker?worker";
import JsonWorker from "monaco-editor/languages/features/json/json.worker?worker";
import { loader } from "@monaco-editor/react";
import { toLongHex } from "@/lib/color";
import { registerScriban } from "./scriban";
import { registerScribanProviders } from "./scribanProviders";
import { registerRuleProviders } from "./ruleProviders";

(self as unknown as { MonacoEnvironment: monaco.Environment }).MonacoEnvironment = {
  getWorker(_id: string, label: string) {
    return label === "json" ? new JsonWorker() : new EditorWorker();
  },
};
loader.config({ monaco });
registerScriban(monaco);
registerScribanProviders(monaco);
registerRuleProviders(monaco);

/** One JSON schema an open editor validates its model against (live markers), by model URI. */
export interface JsonSchemaEntry {
  /** The schema's URI: what the file's own `$schema` resolves to, so a relative `$schema` finds it without a request. */
  uri: string;
  fileMatch: string[];
  schema: unknown;
}

const jsonSchemas = new Map<string, JsonSchemaEntry>();

/** Attaches (or with null, detaches) the schema of a JSON model; the JSON worker revalidates open models. */
export function setJsonSchema(model: string, entry: JsonSchemaEntry | null): void {
  if (entry) jsonSchemas.set(model, entry);
  else if (!jsonSchemas.delete(model)) return;
  jsonDefaults.setDiagnosticsOptions({ ...jsonDefaults.diagnosticsOptions, validate: true, enableSchemaRequest: false, schemas: [...jsonSchemas.values()] });
}

function token(name: string): string {
  return toLongHex(getComputedStyle(document.documentElement).getPropertyValue(name));
}

/** Defines the theme from the current tokens and returns its name. */
export function monacoTheme(): string {
  const dark = document.documentElement.dataset.theme === "dark";
  const name = dark ? "mq-dark" : "mq-light";
  monaco.editor.defineTheme(name, {
    base: dark ? "vs-dark" : "vs",
    inherit: true,
    rules: [],
    colors: {
      "editor.background": token("--mq-bg-surface"),
      "editor.foreground": token("--mq-text-primary"),
      "editorLineNumber.foreground": token("--mq-text-secondary"),
      "editorCursor.foreground": token("--mq-accent"),
      "editor.selectionBackground": token("--mq-accent-subtle"),
      "editorIndentGuide.background1": token("--mq-border-default"),
      "editorWidget.background": token("--mq-bg-raised"),
      "editorWidget.border": token("--mq-border-default"),
    },
  });
  return name;
}

export { monaco };
