// Document children across a new forest (explorer-redesign.md 4.4): an expanded process keeps its States and Events,
// and an expanded entity its Attributes, when the tree is rebuilt (adding a chart's diagram rebuilds it) or a patch
// replaces the row; a row whose element changed reads its document again; a patch that leaves the row alone keeps them.
import { describe, expect, it } from "vitest";
import type { ElementSummary } from "@/api/types";
import { MockBackend } from "@/mocks/backend";
import { PURCHASE_APPROVAL } from "@/mocks/model/processSeed";
import { buildForest, childKeys, documentChildren, needsDocument, nodeOf, patchForest, rowsNeedingDocuments, visibleRows, type Forest } from "@/explorer/tree";
import { IDS } from "./harness";

const labels = (f: Forest, key: string) => childKeys(f, key).map((k) => nodeOf(f, k)!.label);

/** A diagram row as the index lists one once Layout made the chart's diagram. */
function diagramRow(rows: readonly ElementSummary[], process: string): ElementSummary {
  const anyDiagram = rows.find((r) => r.kind === "diagram")!;
  return { ...anyDiagram, id: "01JDIAGRAMFORLAYOUT0000000", name: "Layout", path: "diagrams/layout.json", hash: "fresh", process } as ElementSummary;
}

describe("document children across a new forest", () => {
  const backend = new MockBackend();
  const rows = backend.model.index();
  const processKey = `@processes/p:${PURCHASE_APPROVAL}`;

  it("asks again for an expanded process's States and Events after a rebuild, and gets them back with their counts", () => {
    const before = buildForest({ rows });
    const open = new Set<string>([before.roots.processes, ...childKeys(before, before.roots.processes), processKey]);
    expect(rowsNeedingDocuments(before, visibleRows(before, "processes", open), open)).toEqual([processKey]);
    documentChildren(before, processKey, backend.model.get(PURCHASE_APPROVAL)!);
    expect(needsDocument(before, processKey)).toBe(false);
    expect(labels(before, processKey)).toEqual(["States", "Events", "Scenarios"]);

    // Layout on a chart without a diagram adds one: the tree is rebuilt and the new process row has only Scenarios.
    const after = buildForest({ rows: [...rows, diagramRow(rows, PURCHASE_APPROVAL)] });
    expect(labels(after, processKey)).toEqual(["Scenarios"]);
    expect(needsDocument(after, processKey)).toBe(true);
    expect(rowsNeedingDocuments(after, visibleRows(after, "processes", open), open)).toEqual([processKey]);
    documentChildren(after, processKey, backend.model.get(PURCHASE_APPROVAL)!);
    expect(labels(after, processKey)).toEqual(["States", "Events", "Scenarios"]);
    expect(nodeOf(after, `${processKey}/states`)!.secondary).toBe("13");
    expect(nodeOf(after, `${processKey}/events`)!.secondary).toBe("4");
    expect(rowsNeedingDocuments(after, visibleRows(after, "processes", open), open)).toEqual([]);
  });

  it("only asks for expanded rows that take document children", () => {
    const f = buildForest({ rows });
    const visible = visibleRows(f, "processes", new Set([f.roots.processes]));
    expect(rowsNeedingDocuments(f, visible, new Set())).toEqual([]);
    expect(needsDocument(f, f.roots.processes)).toBe(false);
  });

  it("keeps an expanded entity's Attributes across a rebuild caused by adding a diagram, by reading them again", () => {
    const before = buildForest({ rows });
    documentChildren(before, IDS.invoice, backend.model.get(IDS.invoice)!);
    expect(labels(before, IDS.invoice)[0]).toBe("Attributes");
    const after = buildForest({ rows: [...rows, diagramRow(rows, PURCHASE_APPROVAL)] });
    expect(labels(after, IDS.invoice)).not.toContain("Attributes");
    expect(needsDocument(after, IDS.invoice)).toBe(true);
    documentChildren(after, IDS.invoice, backend.model.get(IDS.invoice)!);
    expect(labels(after, IDS.invoice)[0]).toBe("Attributes");
    expect(needsDocument(after, IDS.invoice)).toBe(false);
  });

  it("keeps the children of a row a patch leaves alone, and reads again the document of a row whose element changed", () => {
    const f = buildForest({ rows });
    documentChildren(f, IDS.invoice, backend.model.get(IDS.invoice)!);
    const other = rows.find((r) => r.kind === "enum")!;
    const quiet = patchForest(f, { from: rows, upserts: [{ ...other, hash: "other-changed" }], deleted: [], moves: [] });
    expect(quiet).not.toBeNull();
    expect(needsDocument(quiet!, IDS.invoice)).toBe(false);
    expect(labels(quiet!, IDS.invoice)[0]).toBe("Attributes");
    const invoice = rows.find((r) => r.id === IDS.invoice)!;
    const changed = patchForest(quiet!, { from: rows, upserts: [{ ...invoice, hash: "invoice-changed" }], deleted: [], moves: [] });
    expect(changed).not.toBeNull();
    expect(needsDocument(changed!, IDS.invoice)).toBe(true);
  });
});
