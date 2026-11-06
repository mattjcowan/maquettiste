// A saved process's index row keeps its actors list (the engine's: ids sorted ordinally, left out when empty).
import { describe, expect, it } from "vitest";
import { QueryClient } from "@tanstack/react-query";
import { applySaveResult, keys } from "@/api/queries";
import type { ElementDocument, ElementSummary, SaveResult } from "@/api/types";
import { summaryFromDocument } from "@/model/model";

const row = (id: string, kind: string, name: string): ElementSummary =>
  ({ id, kind, name, package: null, tags: [], category: null, stereotypes: [], hash: "h", path: `${name}.json` }) as unknown as ElementSummary;

const doc = (json: Record<string, unknown>): ElementDocument => ({ json, hash: "h2", path: "p.json" }) as unknown as ElementDocument;

const process = {
  id: "P1",
  kind: "process",
  name: "Approve",
  events: [{ id: "E1", name: "go", actors: ["A2", "A1"] }],
  transitions: [{ id: "T1", gate: { signers: ["A1", "A3"], requiredActors: ["A3"] } }],
  states: [],
};

describe("saved process index row", () => {
  it("lists the known actors the document names, sorted, and omits the member when none", () => {
    const known = new Set(["A1", "A2", "A3", "A4"]);
    expect(summaryFromDocument(doc(process), known).actors).toEqual(["A1", "A2", "A3"]);
    expect(summaryFromDocument(doc({ ...process, events: [], transitions: [] }), known)).not.toHaveProperty("actors");
    expect(summaryFromDocument(doc(process))).not.toHaveProperty("actors");
  });

  it("applySaveResult builds actors from the cached index when the answer has no summary", () => {
    const qc = new QueryClient();
    qc.setQueryData(keys.index, [row("A1", "actor", "a1"), row("A2", "actor", "a2"), row("A3", "actor", "a3"), row("P1", "process", "Approve")]);
    applySaveResult(qc, { outcome: "saved", current: doc(process) } as unknown as SaveResult);
    const saved = qc.getQueryData<ElementSummary[]>(keys.index)!.find((r) => r.id === "P1")!;
    expect(saved.actors).toEqual(["A1", "A2", "A3"]);
  });

  it("applySaveResult uses the engine's summary from the answer when it carries one", () => {
    const qc = new QueryClient();
    qc.setQueryData(keys.index, [row("P1", "process", "Approve")]);
    const summary = { ...row("P1", "process", "Approve"), actors: ["A9"] };
    applySaveResult(qc, { outcome: "saved", current: doc(process), changes: { changed: [{ id: "P1", summary }], deleted: [] } } as unknown as SaveResult);
    expect(qc.getQueryData<ElementSummary[]>(keys.index)!.find((r) => r.id === "P1")!.actors).toEqual(["A9"]);
  });
});
