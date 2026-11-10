// The editor's copy of the revision sequence equals the engine's: both read tests/fixtures/revisions/sequence-cases.json.
// Only a valid sequence is pinned: the preview also draws a `first` the engine refuses (a text on a numbers level, a letter outside the
// alphabet), where the engine reports MQ8301 and builds nothing; the finding shows beside the preview, so the difference is cosmetic.
import fs from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";
import {
  checkChosenRevision,
  compareRevisions,
  firstRevision,
  isRevisionValue,
  levelExamples,
  nextRevision,
  readRevision,
  writeRevision,
  type RevisionSequence,
} from "@/model/revisionSequence";

const file = path.resolve(__dirname, "../../../../tests/fixtures/revisions/sequence-cases.json");
const fixture = JSON.parse(fs.readFileSync(file, "utf8")) as {
  sequences: Record<string, RevisionSequence>;
  start: { sequence: string; expected: string }[];
  isValue: { sequence: string; text: string; expected: boolean; parts?: string[] }[];
  write: { sequence: string; parts: string[]; expected: string }[];
  next: { sequence: string; from: string; level: number; expected?: string; refused?: boolean }[];
  chosen: { sequence: string; latest: string; name: string; ok: boolean; why?: string }[];
  compare: { sequence: string; a: string; b: string; expected: number }[];
  examples: { sequence: string; level: number; expected: string[] }[];
};
const seq = (name: string) => {
  const s = fixture.sequences[name];
  if (!s) throw new Error(`unknown sequence ${name}`);
  return s;
};

describe("revisionSequence against the shared fixture", () => {
  it("start", () => {
    for (const c of fixture.start) expect(firstRevision(seq(c.sequence)), c.sequence).toBe(c.expected);
  });
  it("isValue", () => {
    for (const c of fixture.isValue) {
      const label = `${c.sequence} ${JSON.stringify(c.text)}`;
      expect(isRevisionValue(seq(c.sequence), c.text), label).toBe(c.expected);
      if (c.expected && c.parts) expect(readRevision(seq(c.sequence), c.text), label).toEqual(c.parts);
    }
  });
  it("write", () => {
    for (const c of fixture.write) expect(writeRevision(seq(c.sequence), c.parts), c.sequence).toBe(c.expected);
  });
  it("next", () => {
    for (const c of fixture.next) {
      const label = `${c.sequence} ${JSON.stringify(c.from)} at ${c.level}`;
      expect(nextRevision(seq(c.sequence), c.from, c.level), label).toBe(c.refused ? null : c.expected);
    }
  });
  it("chosen", () => {
    for (const c of fixture.chosen) {
      const got = checkChosenRevision(seq(c.sequence), c.latest, c.name);
      const label = `${c.sequence} ${JSON.stringify(c.latest)} -> ${JSON.stringify(c.name)}`;
      expect(got.ok, label).toBe(c.ok);
      if (!got.ok) expect(got.why, label).toBe(c.why);
    }
  });
  it("compare", () => {
    for (const c of fixture.compare) expect(compareRevisions(seq(c.sequence), c.a, c.b), `${c.sequence} ${c.a} ${c.b}`).toBe(c.expected);
  });
  it("examples", () => {
    for (const c of fixture.examples) expect(levelExamples(seq(c.sequence).levels[c.level - 1]), `${c.sequence} ${c.level}`).toEqual(c.expected);
  });
  it("covers every case", () => {
    expect(fixture.isValue.length + fixture.next.length + fixture.chosen.length + fixture.compare.length).toBeGreaterThan(150);
  });
});
