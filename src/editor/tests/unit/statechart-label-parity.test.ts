// The edge label the canvas draws and the `label` the engine gives templates follow one rule (phase-3-design.md 4.3):
// every transition of the gate 3 fixture gets the same text here as in the engine's resolved-model golden.
import { readFileSync, readdirSync } from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";
import type { ProcessDoc } from "@/model/process";
import { edgeLabel } from "@/canvas/statechart/chartModel";

const repo = path.resolve(import.meta.dirname, "../../../..");
const processes = path.join(repo, "tests/fixtures/models/processes/.maquettiste/model/processes");
const golden = readFileSync(path.join(repo, "tests/Maquettiste.Engine.Tests/Resolution/Golden/processes/resolved.txt"), "utf8");

/** The engine's labels by process name, in priority order. */
function engineLabels(): Map<string, string[]> {
  const labels = new Map<string, string[]>();
  let current: string[] | null = null;
  for (const line of golden.split("\n")) {
    const process = /^process (\S+)/.exec(line);
    if (process) labels.set(process[1], (current = []));
    else if (/^\S/.test(line)) current = null;
    const transition = /^ {2}transition '(.*)' /.exec(line);
    if (transition && current) current.push(transition[1]);
  }
  return labels;
}

describe("edge labels and the engine's transition labels", () => {
  it("agree on every transition of the gate 3 fixture", () => {
    const engine = engineLabels();
    const files = readdirSync(processes).filter((f) => f.endsWith(".json"));
    expect(files.length).toBe(2);
    for (const file of files) {
      const process = JSON.parse(readFileSync(path.join(processes, file), "utf8")) as ProcessDoc;
      const ours = (process.transitions ?? []).map((t) => edgeLabel(process, t));
      expect(ours, process.name).toEqual(engine.get(process.name));
    }
  });
});
