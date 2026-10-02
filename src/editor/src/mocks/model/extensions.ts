// The mock's extensions folder (the engine's ModelStore extension file API): the custom property schemas seeded from the
// fixture (extensions/*.json) and one script rule (extensions/rules/naming.js), read and written with hashes. A schema is
// checked against extension.json and written as formatted JSON; a rule is loaded with the browser's own parser, and its
// rules run over the model's entries when the model validates, so the mock reports x/<id> findings and MQ5002 for a rule
// file that does not load. The engine runs rules in a sandbox; this mock trusts its own seeded and test-written scripts.
import Ajv2020 from "ajv/dist/2020";
import type { components } from "@/api/schema";
import type { Diagnostic } from "@/api/types";
import { sha256Hex } from "@/lib/sha256";
import extensionSchema from "@/code/extensionSchema.json";
import { extensionRecord, type MockModel, type SeedFile } from "./store";

type S = components["schemas"];
type WriteAnswer = { status: 200 | 201 | 404 | 409 | 422; body: S["ExtensionWriteResult"] };
type Json = Record<string, unknown>;

const FOLDER = ".maquettiste/extensions";
const PATH = /^(?:[A-Za-z0-9][A-Za-z0-9._-]*\.json|rules\/[A-Za-z0-9][A-Za-z0-9._-]*\.js)$/;
const MAX_BYTES = 1024 * 1024;
const checkSchema = new Ajv2020({ strict: false, allErrors: true }).compile(extensionSchema as object);

/** The rule the billing mock carries: nothing in the fixture breaks it until it is edited. */
export const MOCK_RULE = `// Entity names stay short enough for every database the model maps to.
maquettiste.rule({
  id: "entity-names",
  severity: "warning",
  kinds: ["entity"],
  check(element, model, report) {
    if (element.name.length > 30) report("Entity names should be at most 30 characters.", { pointer: "/name" });
  },
});
`;

export function validExtensionPath(path: string): boolean {
  return PATH.test(path);
}

interface Registered {
  id: string;
  severity: Diagnostic["severity"];
  kinds: string[] | null;
  check: (element: unknown, model: unknown, report: (message: string, options?: { pointer?: string; severity?: string }) => void) => void;
  file: string;
}

function diagnostic(rule: string, message: string, file: string, extra: Partial<Diagnostic> = {}): Diagnostic {
  return { rule, severity: "error", message, elementId: null, filePath: `${FOLDER}/${file}`, jsonPointer: null, line: null, column: null, ...extra };
}

function result(outcome: S["ExtensionWriteResult"]["outcome"], hash: string | null, extra: Partial<S["ExtensionWriteResult"]> = {}): S["ExtensionWriteResult"] {
  return { outcome, hash, current: null, text: null, diagnostics: [], files: [], ...extra };
}

function freeze<T>(value: T): T {
  if (value && typeof value === "object") {
    Object.freeze(value);
    for (const v of Object.values(value)) freeze(v);
  }
  return value;
}

export class MockExtensions {
  private readonly files = new Map<string, string>();

  constructor(
    private readonly model: MockModel,
    seed: readonly SeedFile[],
  ) {
    for (const file of seed)
      if (file.path.startsWith("extensions/") && validExtensionPath(file.path.slice("extensions/".length)))
        this.files.set(file.path.slice("extensions/".length), file.text);
  }

  private sorted(): string[] {
    return [...this.files.keys()].sort((a, b) => {
      const ra = a.startsWith("rules/") ? 1 : 0;
      const rb = b.startsWith("rules/") ? 1 : 0;
      return ra - rb || (a < b ? -1 : a > b ? 1 : 0);
    });
  }

  list(): S["ExtensionFileList"] {
    return {
      folder: FOLDER,
      files: this.sorted().map((path) => {
        const text = this.files.get(path)!;
        const rule = path.startsWith("rules/");
        const loaded = rule ? this.load(path, text) : null;
        return {
          path,
          kind: rule ? "rule" : "schema",
          size: new TextEncoder().encode(text).length,
          hash: sha256Hex(text),
          diagnostics: rule ? loaded!.diagnostics : this.schemaProblems(path, text),
          rules: loaded?.rules.map((r) => ({ id: `x/${r.id}`, severity: r.severity })) ?? [],
        };
      }),
    };
  }

  read(path: string): S["ExtensionFileContent"] | null {
    const text = this.files.get(path);
    return text === undefined ? null : { path, kind: path.startsWith("rules/") ? "rule" : "schema", hash: sha256Hex(text), text };
  }

  /** `expected` null creates the file (409 when it exists). */
  write(path: string, text: string, expected: string | null): WriteAnswer {
    if (text.includes("\u0000"))
      return { status: 422, body: result("invalid", null, { diagnostics: [diagnostic("MQ5004", "An extension file must be text.", path)] }) };
    if (new TextEncoder().encode(text).length > MAX_BYTES)
      return { status: 422, body: result("invalid", null, { diagnostics: [diagnostic("MQ5004", "An extension file is at most 1 MB.", path)] }) };
    let written = text;
    let diagnostics: Diagnostic[] = [];
    if (!path.startsWith("rules/")) {
      const problems = this.schemaProblems(path, text);
      if (problems.length) return { status: 422, body: result("invalid", null, { diagnostics: problems }) };
      written = `${JSON.stringify(JSON.parse(text), null, 2)}\n`;
    } else diagnostics = this.load(path, text).diagnostics;
    const disk = this.files.get(path);
    const diskHash = disk === undefined ? null : sha256Hex(disk);
    if (diskHash !== expected) return { status: 409, body: result("conflict", diskHash, { current: disk ?? null }) };
    this.files.set(path, written);
    this.changed();
    return { status: expected === null ? 201 : 200, body: result("saved", sha256Hex(written), { text: written, diagnostics, files: [path] }) };
  }

  delete(path: string, expected: string): WriteAnswer {
    const disk = this.files.get(path);
    if (disk === undefined) return { status: 404, body: result("not-found", null) };
    if (sha256Hex(disk) !== expected) return { status: 409, body: result("conflict", sha256Hex(disk), { current: disk }) };
    this.files.delete(path);
    this.changed();
    return { status: 200, body: result("saved", null, { files: [path] }) };
  }

  move(from: string, to: string, expected: string): WriteAnswer {
    const disk = this.files.get(from);
    if (disk === undefined) return { status: 404, body: result("not-found", null) };
    if (from.startsWith("rules/") !== to.startsWith("rules/"))
      return { status: 422, body: result("invalid", null, { diagnostics: [diagnostic("MQ5004", `'${to}' is not the same kind of file as '${from}'.`, to)] }) };
    if (this.files.has(to)) return { status: 422, body: result("invalid", null, { diagnostics: [diagnostic("MQ5004", `'${to}' already exists.`, to)] }) };
    if (sha256Hex(disk) !== expected) return { status: 409, body: result("conflict", sha256Hex(disk), { current: disk }) };
    this.files.delete(from);
    this.files.set(to, disk);
    this.changed();
    return { status: 200, body: result("saved", sha256Hex(disk), { files: [from, to] }) };
  }

  /** The script rules' findings over the model's entries, and MQ5002 for a rule file that does not load or a check that throws. */
  diagnostics(): Diagnostic[] {
    const out: Diagnostic[] = [];
    const rules: Registered[] = [];
    for (const path of this.sorted().filter((p) => p.startsWith("rules/"))) {
      const loaded = this.load(path, this.files.get(path)!);
      out.push(...loaded.diagnostics);
      rules.push(...loaded.rules);
    }
    if (!rules.length) return out;
    const entries = [...this.model.entries.values()];
    const byId = new Map(entries.map((e) => [e.id, e]));
    const model = freeze({
      get: (id: string) => freeze(structuredClone(byId.get(id)?.json ?? null)),
      all: (kind: string) => freeze(entries.filter((e) => e.json.kind === kind).map((e) => structuredClone(e.json))),
      referencesTo: (id: string) => freeze(structuredClone(this.model.references(id) ?? [])),
    });
    for (const rule of rules)
      for (const entry of entries) {
        if (rule.kinds && !rule.kinds.includes(String(entry.json.kind))) continue;
        const report = (message: string, options?: { pointer?: string; severity?: string }) =>
          out.push({
            rule: `x/${rule.id}`,
            severity: (options?.severity as Diagnostic["severity"] | undefined) ?? rule.severity,
            message: String(message),
            elementId: entry.id,
            filePath: entry.path,
            jsonPointer: options?.pointer ?? "",
            line: null,
            column: null,
          });
        try {
          rule.check(freeze(structuredClone(entry.json)), model, report);
        } catch (error) {
          out.push(
            diagnostic("MQ5002", `Rule 'x/${rule.id}' threw ${String((error as Error).message ?? error)}`, rule.file, {
              elementId: entry.id,
              line: 1,
              column: 1,
            }),
          );
        }
      }
    return out;
  }

  /** Loads one rule script: its registrations, or MQ5002 for a syntax error or a registration without an id. */
  private load(path: string, text: string): { rules: Registered[]; diagnostics: Diagnostic[] } {
    const rules: Registered[] = [];
    const api = freeze({
      rule(spec: Json) {
        const id = typeof spec?.id === "string" ? spec.id : "";
        if (!id || /\s/.test(id)) throw new TypeError("maquettiste.rule: id must be a non-empty string without whitespace.");
        if (typeof spec.check !== "function") throw new TypeError(`maquettiste.rule '${id}': check must be a function.`);
        const severity = (spec.severity as Diagnostic["severity"] | undefined) ?? "error";
        rules.push({
          id,
          severity,
          kinds: Array.isArray(spec.kinds) && spec.kinds.length ? (spec.kinds as string[]) : null,
          check: spec.check as Registered["check"],
          file: path,
        });
      },
    });
    try {
      new Function("maquettiste", `"use strict";\n${text}`)(api);
      return { rules, diagnostics: [] };
    } catch (error) {
      const syntax = error instanceof SyntaxError;
      const message = syntax
        ? `Script ${FOLDER}/${path} has a syntax error: ${error.message}`
        : `Script ${FOLDER}/${path} failed while loading: ${String(error)}`;
      return { rules: [], diagnostics: [diagnostic("MQ5002", message, path, { line: syntaxLine(text), column: 1 })] };
    }
  }

  private schemaProblems(path: string, text: string): Diagnostic[] {
    let json: unknown;
    try {
      json = JSON.parse(text);
    } catch (error) {
      return [diagnostic("MQ5004", `Invalid JSON: ${(error as Error).message}`, path, { line: 1, column: 1 })];
    }
    if (checkSchema(json)) return [];
    return (checkSchema.errors ?? []).map((e) =>
      diagnostic("MQ5004", `${e.instancePath || "/"} ${e.message ?? "is not valid"}`, path, { jsonPointer: e.instancePath }),
    );
  }

  /** The project's extension schemas follow the files, and the model revalidates its custom properties. */
  private changed(): void {
    const schemas = this.sorted()
      .filter((p) => !p.startsWith("rules/"))
      .flatMap((p) => {
        try {
          return [extensionRecord(JSON.parse(this.files.get(p)!) as Json)];
        } catch {
          return [];
        }
      });
    this.model.replaceExtensions(schemas);
  }
}

/** The mock has no parser positions: the last line, where an unfinished script usually ends. */
function syntaxLine(text: string): number {
  return Math.max(1, text.trimEnd().split("\n").length);
}
