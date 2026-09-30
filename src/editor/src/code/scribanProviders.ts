// Completion and hover for Scriban models (generation-ui.md 3.3): one provider of each per Monaco instance, reading the
// completion data an editor attached to its model (setCompletionData, by model URI), so each open template completes
// with its own unit's variables, members and helpers.
import type * as Monaco from "monaco-editor/editor/editor.api";
import { SCRIBAN } from "./scriban";
import { completionsAt, hoverFor, wordAt, type SuggestionKind, type TemplateCompletionData } from "./scribanCompletion";

const byModel = new Map<string, TemplateCompletionData>();

/** Attaches (or with null, detaches) a model's completion data. */
export function setCompletionData(uri: string, data: TemplateCompletionData | null): void {
  if (data) byModel.set(uri, data);
  else byModel.delete(uri);
}

let registered = false;

export function registerScribanProviders(monaco: typeof Monaco): void {
  if (registered) return;
  registered = true;
  const kinds: Record<SuggestionKind, Monaco.languages.CompletionItemKind> = {
    variable: monaco.languages.CompletionItemKind.Variable,
    member: monaco.languages.CompletionItemKind.Field,
    helper: monaco.languages.CompletionItemKind.Function,
    function: monaco.languages.CompletionItemKind.Method,
    keyword: monaco.languages.CompletionItemKind.Keyword,
    module: monaco.languages.CompletionItemKind.Module,
  };
  monaco.languages.registerCompletionItemProvider(SCRIBAN, {
    triggerCharacters: [".", "|", " "],
    provideCompletionItems(model, position) {
      const data = byModel.get(model.uri.toString());
      if (!data) return { suggestions: [] };
      const before = model.getValueInRange({ startLineNumber: 1, startColumn: 1, endLineNumber: position.lineNumber, endColumn: position.column });
      const word = model.getWordUntilPosition(position);
      const range = { startLineNumber: position.lineNumber, endLineNumber: position.lineNumber, startColumn: word.startColumn, endColumn: word.endColumn };
      return {
        suggestions: completionsAt(data, before).map((s) => ({
          label: s.label,
          kind: kinds[s.kind],
          detail: s.detail,
          documentation: s.documentation,
          insertText: s.label,
          range,
        })),
      };
    },
  });
  monaco.languages.registerHoverProvider(SCRIBAN, {
    provideHover(model, position) {
      const data = byModel.get(model.uri.toString());
      if (!data) return null;
      const word = wordAt(model.getLineContent(position.lineNumber), position.column - 1);
      const text = word ? hoverFor(data, word) : null;
      return text ? { contents: [{ value: text }] } : null;
    },
  });
}
