// Monaco, bundled locally (no CDN: local mode may be offline), with its workers built by Vite and
// themes built from the design tokens at runtime.
import * as monaco from "monaco-editor/editor/editor.api";
import "monaco-editor/languages/features/json/register";
import "monaco-editor/languages/definitions/sql/register";
import "monaco-editor/languages/definitions/csharp/register";
import EditorWorker from "monaco-editor/editor/editor.worker?worker";
import JsonWorker from "monaco-editor/languages/features/json/json.worker?worker";
import { loader } from "@monaco-editor/react";
import { toLongHex } from "@/lib/color";

(self as unknown as { MonacoEnvironment: monaco.Environment }).MonacoEnvironment = {
  getWorker(_id: string, label: string) {
    return label === "json" ? new JsonWorker() : new EditorWorker();
  },
};
loader.config({ monaco });

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
