// Completion, signature help and hover for script rule files (extensions/rules/*.js) in Monaco: one provider of each per
// Monaco instance, silent for every other JavaScript model. The suggestions come from ruleCompletion.ts.
import type * as Monaco from "monaco-editor/editor/editor.api";
import { isRuleModel, ruleCompletions, ruleHover, ruleSignatureAt, type RuleSuggestionKind } from "./ruleCompletion";

let registered = false;

export function registerRuleProviders(monaco: typeof Monaco): void {
  if (registered) return;
  registered = true;
  const kinds: Record<RuleSuggestionKind, Monaco.languages.CompletionItemKind> = {
    function: monaco.languages.CompletionItemKind.Function,
    method: monaco.languages.CompletionItemKind.Method,
    property: monaco.languages.CompletionItemKind.Property,
    value: monaco.languages.CompletionItemKind.Value,
    snippet: monaco.languages.CompletionItemKind.Snippet,
    variable: monaco.languages.CompletionItemKind.Variable,
  };
  const textBefore = (model: Monaco.editor.ITextModel, position: Monaco.Position) =>
    model.getValueInRange({
      startLineNumber: Math.max(1, position.lineNumber - 40),
      startColumn: 1,
      endLineNumber: position.lineNumber,
      endColumn: position.column,
    });
  monaco.languages.registerCompletionItemProvider("javascript", {
    triggerCharacters: [".", '"', "'"],
    provideCompletionItems(model, position) {
      if (!isRuleModel(model.uri.toString())) return { suggestions: [] };
      const word = model.getWordUntilPosition(position);
      const range = { startLineNumber: position.lineNumber, endLineNumber: position.lineNumber, startColumn: word.startColumn, endColumn: word.endColumn };
      return {
        suggestions: ruleCompletions(textBefore(model, position)).map((s) => ({
          label: s.label,
          kind: kinds[s.kind],
          detail: s.detail,
          documentation: { value: s.documentation },
          insertText: s.insertText,
          insertTextRules: s.snippet ? monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet : undefined,
          range,
        })),
      };
    },
  });
  monaco.languages.registerSignatureHelpProvider("javascript", {
    signatureHelpTriggerCharacters: ["(", ","],
    signatureHelpRetriggerCharacters: [","],
    provideSignatureHelp(model, position) {
      if (!isRuleModel(model.uri.toString())) return null;
      const signature = ruleSignatureAt(textBefore(model, position));
      if (!signature) return null;
      return {
        value: {
          signatures: [
            {
              label: signature.label,
              documentation: { value: signature.documentation },
              parameters: signature.parameters.map((p) => ({ label: p.label, documentation: p.documentation })),
            },
          ],
          activeSignature: 0,
          activeParameter: signature.activeParameter,
        },
        dispose() {},
      };
    },
  });
  monaco.languages.registerHoverProvider("javascript", {
    provideHover(model, position) {
      if (!isRuleModel(model.uri.toString())) return null;
      const word = model.getWordAtPosition(position)?.word;
      const text = word ? ruleHover(word) : null;
      return text ? { contents: [{ value: text }] } : null;
    },
  });
}
