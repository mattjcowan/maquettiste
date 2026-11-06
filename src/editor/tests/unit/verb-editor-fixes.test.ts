// The editor side of the verb review fixes: the files a rename or remove writes (its dry run), findings of a part counted for the
// owning element, the MQ9701 fix on an unsaved draft, the index row's verbs, the stereotype subject label and the raise tooltip's verb.
import { describe, expect, it } from "vitest";
import type { Diagnostic, ElementDocument, ElementSummary } from "@/api/types";
import { errorCounts, ownerOf } from "@/explorer/tree";
import { summaryFromDocument } from "@/model/model";
import { verbOperationFiles, verbUsers } from "@/model/verbList";
import { enabledRows } from "@/editors/process/simulation/model";
import type { SimulationResult } from "@/editors/process/simulation/api";
import type { ProcessDoc } from "@/model/process";
import { hasDraftQuickFix, hasQuickFix } from "@/problems/quickFix";

const row = (over: Partial<ElementSummary> & { id: string; kind: ElementSummary["kind"] }): ElementSummary =>
  ({ name: over.id, package: null, tags: [], category: null, stereotypes: [], hash: "h", path: `${over.id}.json`, ...over }) as ElementSummary;

const rows = [
  row({ id: "V", kind: "verb-vocabulary" }),
  row({ id: "P", kind: "process", name: "Order lifecycle", verbs: ["archive", "ship"] }),
  row({ id: "O", kind: "operation", name: "Ship operation", verb: "archive" }),
  row({ id: "Q", kind: "process", name: "Other", verbs: ["promote"] }),
];

describe("what a rename or remove writes (its dry run)", () => {
  it("lists the vocabulary and every element that names an own key", () => {
    expect(verbUsers(rows, "archive").map((r) => r.id)).toEqual(["P", "O"]);
    const files = verbOperationFiles(rows, "archive");
    expect(files.vocabulary).toBe("V");
    expect(files.users.map((u) => u.name)).toEqual(["Order lifecycle", "Ship operation"]);
  });

  it("lists no use for an own verb that shadows a built-in key: the uses name the built-in verb (a process names promote)", () => {
    expect(verbOperationFiles(rows, "lock").users).toEqual([]);
    expect(verbOperationFiles(rows, "promote").users).toEqual([]);
  });
});

describe("findings of a part belong to the owning element", () => {
  const diagnostics: Diagnostic[] = [
    { rule: "MQ9701", severity: "error", message: "m", elementId: "EVENT1", filePath: "P.json", jsonPointer: "/events/0/verb", line: null, column: null },
    { rule: "MQ9701", severity: "warning", message: "w", elementId: "P", filePath: "P.json", jsonPointer: "/x", line: null, column: null },
    { rule: "MQ0001", severity: "error", message: "e", elementId: "O", filePath: "O.json", jsonPointer: "", line: null, column: null },
  ];

  it("counts a part's error for the file's element, as the Problems panel groups it", () => {
    expect(errorCounts(diagnostics, rows)).toEqual(
      new Map([
        ["P", 1],
        ["O", 1],
      ]),
    );
    // Without the index the counts stay by the id the finding names.
    expect(errorCounts(diagnostics)).toEqual(
      new Map([
        ["EVENT1", 1],
        ["O", 1],
      ]),
    );
  });

  it("resolves a finding by id first, then by file", () => {
    const owner = ownerOf(rows);
    expect(owner(diagnostics[0])).toBe("P");
    expect(owner(diagnostics[1])).toBe("P");
    expect(owner({ ...diagnostics[0], elementId: null, filePath: null })).toBeUndefined();
  });
});

describe("the MQ9701 fix on an unsaved draft", () => {
  it("is the one fix offered while the draft is unsaved, because it edits the project's verbs", () => {
    expect(hasDraftQuickFix("MQ9701")).toBe(true);
    for (const rule of ["MQ9702", "MQ9704", "MQ9705", "MQ9201"]) expect(hasDraftQuickFix(rule)).toBe(false);
    expect(hasQuickFix("MQ9701", null)).toBe(true);
  });
});

describe("the index row of a saved document", () => {
  const doc = (json: object): ElementDocument => ({ hash: "h", path: "p.json", json }) as unknown as ElementDocument;

  it("lists the distinct sorted verbs of a process and the verb of an operation, so Used by follows a save", () => {
    const process = summaryFromDocument(
      doc({
        kind: "process",
        id: "P",
        name: "P",
        events: [
          { id: "E", verb: "submit" },
          { id: "F", verb: "archive" },
        ],
        transitions: [{ id: "T", verb: "archive" }],
      }),
    );
    expect(process.verbs).toEqual(["archive", "submit"]);
    expect(summaryFromDocument(doc({ kind: "process", id: "P", name: "P" })).verbs).toBeUndefined();
    expect(summaryFromDocument(doc({ kind: "operation", id: "O", name: "O", verb: "lock" })).verb).toBe("lock");
  });
});

describe("the raise button names the event's verb", () => {
  it("carries the verb of the event on its row", () => {
    const process = {
      id: "P",
      events: [
        { id: "E1", name: "submit", verb: "submit" },
        { id: "E2", name: "other" },
      ],
      transitions: [],
      guards: [],
    } as unknown as ProcessDoc;
    const result = {
      enabled: [
        { trigger: "event", event: "E1", invoke: null, transitions: [], actors: [], guardUnknown: false, gate: null },
        { trigger: "event", event: "E2", invoke: null, transitions: [], actors: [], guardUnknown: false, gate: null },
      ],
    } as unknown as SimulationResult;
    const names = { event: (id: string | null) => id ?? "", invoke: (id: string | null) => id ?? "", actor: (id: string) => id };
    const out = enabledRows(result, process, names as never, []);
    expect(out[0].verb).toBe("submit");
    expect(out[1].verb).toBeUndefined();
  });
});
