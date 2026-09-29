// The Scriban tokenizer (generation-ui.md 3.3): a Monarch grammar for the pack templates. Plain text outside the
// code blocks; inside `{{ }}` (and the whitespace-trimming `{{- -}}` and `{{~ ~}}`) keywords, strings, numbers,
// comments, pipes and the function after a pipe, the built-in function objects and operators. `{%{ }%}` is the raw
// escape block. Registered once per Monaco instance (registerScriban).
import type * as Monaco from "monaco-editor/editor/editor.api";

export const SCRIBAN = "scriban";

export const SCRIBAN_KEYWORDS = [
  "if",
  "else",
  "end",
  "for",
  "in",
  "while",
  "tablerow",
  "with",
  "capture",
  "readonly",
  "import",
  "ret",
  "break",
  "continue",
  "func",
  "wrap",
  "include",
  "case",
  "when",
  "this",
  "empty",
  "null",
  "true",
  "false",
  "and",
  "or",
  "not",
  "do",
];

/** Scriban's built-in function objects (`string.upcase`, `array.size`, ...). */
export const SCRIBAN_BUILTINS = ["array", "date", "html", "math", "object", "regex", "string", "timespan"];

export const scribanLanguage: Monaco.languages.IMonarchLanguage = {
  defaultToken: "",
  tokenPostfix: ".scriban",
  keywords: SCRIBAN_KEYWORDS,
  builtins: SCRIBAN_BUILTINS,
  tokenizer: {
    root: [
      [/\{%\{/, { token: "delimiter.raw", next: "@raw" }],
      [/\{\{[-~]?/, { token: "delimiter.code", next: "@code" }],
      [/[^{]+/, ""],
      [/\{/, ""],
    ],
    raw: [
      [/\}%\}/, { token: "delimiter.raw", next: "@pop" }],
      [/[^}]+/, "string.raw"],
      [/\}/, "string.raw"],
    ],
    code: [
      [/[-~]?\}\}/, { token: "delimiter.code", next: "@pop" }],
      [/\s+/, ""],
      [/##/, { token: "comment", next: "@blockComment" }],
      [/#(?:[^}\n]|\}(?!\}))*/, "comment"],
      [/"/, { token: "string", next: "@dstring" }],
      [/'/, { token: "string", next: "@sstring" }],
      [/`[^`]*`/, "string"],
      [/\d+(\.\d+)?/, "number"],
      [/\|>?/, { token: "operator.pipe", next: "@afterPipe" }],
      [/([a-zA-Z_]\w*)(\.)([a-zA-Z_]\w*)/, [{ cases: { "@builtins": "predefined", "@default": "identifier" } }, "delimiter", "identifier"]],
      [/[a-zA-Z_$]\w*/, { cases: { "@keywords": "keyword", "@default": "identifier" } }],
      [/==|!=|<=|>=|&&|\|\||\?\?|\.\.<?|[<>+\-*/%=!?:^]/, "operator"],
      [/[()[\]{}]/, "@brackets"],
      [/[.,;]/, "delimiter"],
    ],
    afterPipe: [
      [/[ \t]+/, ""],
      [/([a-zA-Z_]\w*)(\.)([a-zA-Z_]\w*)/, ["predefined", "delimiter", { token: "function", next: "@pop" }]],
      [/[a-zA-Z_]\w*/, { token: "function", next: "@pop" }],
      ["", "", "@pop"],
    ],
    blockComment: [
      [/##/, { token: "comment", next: "@pop" }],
      [/[^#]+/, "comment"],
      [/#/, "comment"],
    ],
    dstring: [
      [/[^\\"]+/, "string"],
      [/\\./, "string.escape"],
      [/"/, { token: "string", next: "@pop" }],
    ],
    sstring: [
      [/[^\\']+/, "string"],
      [/\\./, "string.escape"],
      [/'/, { token: "string", next: "@pop" }],
    ],
  },
};

export const scribanConfiguration: Monaco.languages.LanguageConfiguration = {
  comments: { lineComment: "#", blockComment: ["##", "##"] },
  brackets: [
    ["{{", "}}"],
    ["(", ")"],
    ["[", "]"],
  ],
  autoClosingPairs: [
    { open: "(", close: ")" },
    { open: "[", close: "]" },
    { open: '"', close: '"', notIn: ["string"] },
  ],
  surroundingPairs: [
    { open: "(", close: ")" },
    { open: "[", close: "]" },
    { open: '"', close: '"' },
  ],
};

/** Registers the language once for this Monaco instance; later calls do nothing. */
export function registerScriban(monaco: typeof Monaco): void {
  if (monaco.languages.getLanguages().some((l) => l.id === SCRIBAN)) return;
  monaco.languages.register({ id: SCRIBAN, extensions: [".scriban", ".sbn", ".sbn-txt"], aliases: ["Scriban"] });
  monaco.languages.setMonarchTokensProvider(SCRIBAN, scribanLanguage);
  monaco.languages.setLanguageConfiguration(SCRIBAN, scribanConfiguration);
}
