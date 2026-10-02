// Completion, signature help and hover for script rules (extensions/rules/*.js), pure: what to offer from the text before the
// cursor. ruleProviders.ts registers them with Monaco for rule files only. The API is the engine's: maquettiste.rule({ id,
// severity, kinds, check(element, model, report) }), model.get/all/referencesTo and report(message, { pointer, severity }).
import { RULE_KINDS } from "@/workspaces/generate/extensionsModel";

export type RuleSuggestionKind = "function" | "method" | "property" | "value" | "snippet" | "variable";

export interface RuleSuggestion {
  label: string;
  kind: RuleSuggestionKind;
  detail: string;
  documentation: string;
  insertText: string;
  /** insertText is a snippet (tab stops). */
  snippet?: boolean;
}

export interface RuleSignature {
  label: string;
  documentation: string;
  parameters: { label: string; documentation: string }[];
  activeParameter: number;
}

const RULE_DOC =
  "Registers a validation rule. `id` (no spaces) names its findings `x/<id>`; `severity` is `error` (the default), `warning` or `info`; `kinds` lists the element kinds it checks (none: every kind); `check(element, model, report)` runs once per element.";
const REPORT_DOC =
  "Reports one finding on the element. `pointer` is a JSON pointer into the element (such as `/name` or `/attributes/0`); `severity` overrides the rule's for this finding.";

const RULE_SNIPPET = `maquettiste.rule({
\tid: "\${1:my-rule}",
\tseverity: "\${2|warning,error,info|}",
\tkinds: ["\${3:entity}"],
\tcheck(element, model, report) {
\t\t\${0}
\t},
});`;

const MODEL_METHODS: RuleSuggestion[] = [
  {
    label: "get",
    kind: "method",
    detail: "model.get(id): object | null",
    documentation: "The element (or sub-element, such as an attribute) with this id, as its canonical JSON, read-only; null when there is none.",
    insertText: 'get("${1:id}")',
    snippet: true,
  },
  {
    label: "all",
    kind: "method",
    detail: "model.all(kind): object[]",
    documentation: "Every element of a kind (such as `entity`), ordered by name then id, read-only.",
    insertText: 'all("${1:entity}")',
    snippet: true,
  },
  {
    label: "referencesTo",
    kind: "method",
    detail: "model.referencesTo(id): { fromElementId, fromId, jsonPointer, field, toId }[]",
    documentation: "Where an element or sub-element is referenced: the referring element, the id holding the reference, its JSON pointer and field.",
    insertText: 'referencesTo("${1:id}")',
    snippet: true,
  },
];

const ELEMENT_FIELDS: [string, string][] = [
  ["id", "The element's id (a ULID)."],
  ["kind", "The element kind, such as `entity`."],
  ["name", "The element's name."],
  ["displayName", "The display name, when set."],
  ["description", "The description: text, or `{ file }` for a sidecar."],
  ["package", "The id of the domain (package) it is in."],
  ["tags", "Tag keys."],
  ["category", "The category id."],
  ["stereotypes", "Stereotype keys."],
  ["properties", "Custom properties (the extension schemas' values)."],
  ["attributes", "An entity's or value object's attributes."],
  ["ends", "A relation's ends."],
  ["members", "An enum's members."],
];

const value = (v: string, doc: string): RuleSuggestion => ({ label: v, kind: "value", detail: v, documentation: doc, insertText: v });

/** The suggestions for the text before the cursor. */
export function ruleCompletions(before: string): RuleSuggestion[] {
  if (/severity\s*:\s*["'][\w-]*$/.test(before))
    return [
      value("error", "A finding that stops generation."),
      value("warning", "A finding that is shown but does not stop generation."),
      value("info", "A note."),
    ];
  if (/(kinds\s*:\s*\[[^\]]*|model\.all\(\s*)["'][\w-]*$/.test(before)) return RULE_KINDS.map((k) => value(k, `The ${k} kind.`));
  if (/\bmaquettiste\.\w*$/.test(before))
    return [
      {
        label: "rule",
        kind: "function",
        detail: "maquettiste.rule({ id, severity, kinds, check })",
        documentation: RULE_DOC,
        insertText: RULE_SNIPPET.slice("maquettiste.".length),
        snippet: true,
      },
    ];
  if (/\bmodel\.\w*$/.test(before)) return MODEL_METHODS;
  if (/\belement\.\w*$/.test(before))
    return ELEMENT_FIELDS.map(([label, documentation]) => ({ label, kind: "property", detail: `element.${label}`, documentation, insertText: label }));
  if (/\.\w*$/.test(before)) return [];
  return [
    { label: "maquettiste.rule", kind: "snippet", detail: "A new rule", documentation: RULE_DOC, insertText: RULE_SNIPPET, snippet: true },
    {
      label: "report",
      kind: "function",
      detail: "report(message, { pointer, severity })",
      documentation: REPORT_DOC,
      insertText: 'report("${1:message}", { pointer: "${2:/name}" });',
      snippet: true,
    },
    { label: "element", kind: "variable", detail: "the element checked", documentation: "The element's canonical JSON, read-only.", insertText: "element" },
    {
      label: "model",
      kind: "variable",
      detail: "model.get, model.all, model.referencesTo",
      documentation: "A read-only view of the whole model.",
      insertText: "model",
    },
  ];
}

const SIGNATURES: Record<string, Omit<RuleSignature, "activeParameter">> = {
  report: {
    label: "report(message: string, options?: { pointer?: string, severity?: 'error' | 'warning' | 'info' })",
    documentation: REPORT_DOC,
    parameters: [
      { label: "message: string", documentation: "What is wrong, in a sentence." },
      {
        label: "options?: { pointer?: string, severity?: 'error' | 'warning' | 'info' }",
        documentation: "Where in the element, and a severity for this finding.",
      },
    ],
  },
  "maquettiste.rule": {
    label: "maquettiste.rule(spec: { id: string, severity?: 'error' | 'warning' | 'info', kinds?: string[], check(element, model, report) })",
    documentation: RULE_DOC,
    parameters: [
      { label: "spec: { id: string, severity?: 'error' | 'warning' | 'info', kinds?: string[], check(element, model, report) }", documentation: RULE_DOC },
    ],
  },
  "model.get": {
    label: "model.get(id: string)",
    documentation: MODEL_METHODS[0].documentation,
    parameters: [{ label: "id: string", documentation: "An element or sub-element id." }],
  },
  "model.all": {
    label: "model.all(kind: string)",
    documentation: MODEL_METHODS[1].documentation,
    parameters: [{ label: "kind: string", documentation: "An element kind, such as entity." }],
  },
  "model.referencesTo": {
    label: "model.referencesTo(id: string)",
    documentation: MODEL_METHODS[2].documentation,
    parameters: [{ label: "id: string", documentation: "An element or sub-element id." }],
  },
};

/** The call the cursor is in (report, maquettiste.rule or a model method) and which argument, or null. */
export function ruleSignatureAt(before: string): RuleSignature | null {
  let depth = 0;
  let commas = 0;
  for (let i = before.length - 1; i >= 0; i--) {
    const c = before[i];
    if (c === ")" || c === "]" || c === "}") depth++;
    else if (c === "[" || c === "{") {
      // The cursor is inside an object or array argument: the commas seen so far are its own, not the call's.
      if (depth === 0) commas = 0;
      else depth--;
    } else if (c === "(") {
      if (depth > 0) {
        depth--;
        continue;
      }
      const name = /(maquettiste\.rule|model\.(?:get|all|referencesTo)|\breport)\s*$/.exec(before.slice(0, i))?.[1];
      const signature = name ? SIGNATURES[name] : undefined;
      return signature ? { ...signature, activeParameter: Math.min(commas, signature.parameters.length - 1) } : null;
    } else if (c === "," && depth === 0) commas++;
  }
  return null;
}

const HOVERS: Record<string, string> = {
  maquettiste: "The sandbox's API. Script rules call `maquettiste.rule({ id, severity, kinds, check })`.",
  rule: RULE_DOC,
  report: REPORT_DOC,
  check: "`check(element, model, report)`: called once per element of the rule's kinds. Throwing is reported as MQ5002 on this file.",
  get: MODEL_METHODS[0].documentation,
  all: MODEL_METHODS[1].documentation,
  referencesTo: MODEL_METHODS[2].documentation,
  severity: "`error` (stops generation), `warning` or `info`.",
  kinds: "The element kinds the rule checks; none for every kind.",
  pointer: "A JSON pointer into the element, such as `/name` or `/attributes/0/name`.",
};

/** The hover text for a word of a rule file, or null. */
export function ruleHover(word: string): string | null {
  return HOVERS[word] ?? null;
}

/** Whether a Monaco model URI is a rule file (the providers stay silent elsewhere). */
export function isRuleModel(uri: string): boolean {
  return /extensions\/rules\/[^/]+\.js$/.test(uri);
}
