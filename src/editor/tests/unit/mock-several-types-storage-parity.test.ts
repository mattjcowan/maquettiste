// The mock's findings for the storage of relations with several types equal the engine's, on the two fixtures the engine's
// SeveralTypesStorageParityTests pins: same rule, element id, pointer, message and severity, field by field.
import fs from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";
import { MockBackend } from "@/mocks/backend";
import { packs } from "@/mocks/model/seed";
import type { SeedFile } from "@/mocks/model/store";

function filesUnder(dir: string): string[] {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap((e) => (e.isDirectory() ? filesUnder(path.join(dir, e.name)) : [path.join(dir, e.name)]));
}

type Row = { rule: string; elementId: string; pointer: string; message: string; severity: string };

export function loadFixture(fixture: string) {
  const root = path.resolve(__dirname, "../fixtures", fixture);
  const base = path.join(root, ".maquettiste");
  const expected = JSON.parse(fs.readFileSync(path.join(root, "diagnostics.json"), "utf8")) as { rules: string[]; diagnostics: Row[] };
  const files: SeedFile[] = filesUnder(base)
    .filter((f) => !f.includes(`${path.sep}.schema${path.sep}`))
    .sort()
    .map((f) => ({ path: path.relative(base, f).split(path.sep).join("/"), text: fs.readFileSync(f, "utf8") }));
  const backend = new MockBackend({ seed: { files, packs: packs() } });
  const rules = new Set(expected.rules);
  const key = (d: Row) => [d.rule, d.elementId, d.pointer, d.message].join("\u0000");
  const actual: Row[] = backend.model
    .validate()
    .diagnostics.filter((d) => rules.has(d.rule))
    .map((d) => ({ rule: d.rule, elementId: d.elementId ?? "", pointer: d.jsonPointer ?? "", message: d.message, severity: d.severity }))
    .sort((a, b) => (key(a) < key(b) ? -1 : key(a) > key(b) ? 1 : 0));
  return { actual, expected: expected.diagnostics, backend };
}

describe("the mock and the engine on the storage fixtures", () => {
  it("give the same file findings", () => {
    const { actual, expected } = loadFixture("several-types-storage-files");
    expect(actual).toEqual(expected);
  });
  it("give the same findings of the storage run", () => {
    const { actual, expected } = loadFixture("several-types-storage-run");
    expect(actual).toEqual(expected);
  });
});
