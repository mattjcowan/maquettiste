// Monaco, bundled locally (no CDN: local mode may be offline), with its workers built by Vite and
// themes built from the design tokens at runtime.
import * as monaco from "monaco-editor/editor/editor.api";
import "monaco-editor/languages/features/json/register";
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
import EditorWorker from "monaco-editor/editor/editor.worker?worker";
import JsonWorker from "monaco-editor/languages/features/json/json.worker?worker";
import { loader } from "@monaco-editor/react";
import { toLongHex } from "@/lib/color";
import { registerScriban } from "./scriban";
import { registerScribanProviders } from "./scribanProviders";

(self as unknown as { MonacoEnvironment: monaco.Environment }).MonacoEnvironment = {
  getWorker(_id: string, label: string) {
    return label === "json" ? new JsonWorker() : new EditorWorker();
  },
};
loader.config({ monaco });
registerScriban(monaco);
registerScribanProviders(monaco);

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
