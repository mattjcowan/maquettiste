// The mock's findings for relations with several types equal the engine's, on the fixture the engine's SeveralTypesParityTests pins
// (tests/fixtures/several-types): same rule, element id, pointer, message and severity, field by field.
import fs from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";
import { MockBackend } from "@/mocks/backend";
import { packs } from "@/mocks/model/seed";
import type { SeedFile } from "@/mocks/model/store";

const root = path.resolve(__dirname, "../fixtures/several-types");
const base = path.join(root, ".maquettiste");

function filesUnder(dir: string): string[] {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap((e) => (e.isDirectory() ? filesUnder(path.join(dir, e.name)) : [path.join(dir, e.name)]));
}

const expected = JSON.parse(fs.readFileSync(path.join(root, "diagnostics.json"), "utf8")) as {
  rules: string[];
  diagnostics: { rule: string; elementId: string; pointer: string; message: string; severity: string }[];
};

describe("the mock and the engine on the several-types fixture", () => {
  it("give the same findings for the several-types rules", () => {
    const files: SeedFile[] = filesUnder(base)
      .filter((f) => !f.includes(`${path.sep}.schema${path.sep}`))
      .sort()
      .map((f) => ({ path: path.relative(base, f).split(path.sep).join("/"), text: fs.readFileSync(f, "utf8") }));
    const backend = new MockBackend({ seed: { files, packs: packs() } });
    const rules = new Set(expected.rules);
    const key = (d: { rule: string; elementId: string | null; pointer: string | null; message: string }) =>
      [d.rule, d.elementId, d.pointer, d.message].join("\u0000");
    const actual = backend.model
      .validate()
      .diagnostics.filter((d) => rules.has(d.rule))
      .map((d) => ({ rule: d.rule, elementId: d.elementId, pointer: d.jsonPointer, message: d.message, severity: d.severity }))
      .sort((a, b) => (key(a) < key(b) ? -1 : key(a) > key(b) ? 1 : 0));
    expect(actual).toEqual(expected.diagnostics);
  });
});
