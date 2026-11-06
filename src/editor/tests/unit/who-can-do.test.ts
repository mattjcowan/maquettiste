// The "Who can do what" view's model functions (src/model/whoCanDo.ts) over get_rights rows.
import { describe, expect, it } from "vitest";
import { cellOf, deciders, rowsFor, whoEntities, whoStates, whoView, type RightsRow, type WhoNames } from "@/model/whoCanDo";
import type { ProcessDoc } from "@/model/process";

const row = (extra: Partial<RightsRow>): RightsRow => ({
  state: "S1",
  actor: "A",
  verb: "modify",
  answer: "allowed",
  byDefault: false,
  by: [],
  dependsOn: [],
  ...extra,
});
const names: WhoNames = {
  actor: (id) => (id ? `actor ${id}` : "?"),
  verb: (key) => key.toUpperCase(),
  lifecycle: (id) => (id === "HR" ? "HR" : id),
  entity: (id) => id,
};

describe("a cell", () => {
  it("says Allowed, Denied or Depends, and names the other lifecycles a cell depends on", () => {
    expect(cellOf(row({}), names).text).toBe("Allowed");
    expect(cellOf(row({ answer: "denied" }), names).text).toBe("Denied");
    expect(cellOf(row({ answer: "depends" }), names).text).toBe("Depends");
    expect(cellOf(row({ answer: "depends", dependsOn: ["HR", "Projects"] }), names).text).toBe("Depends on HR, Projects");
  });

  it("is grey when the default decides it", () => {
    expect(cellOf(row({ byDefault: true }), names).grey).toBe(true);
    expect(cellOf(row({}), names).grey).toBe(false);
  });
});

describe("the view of one state", () => {
  const rows = [
    row({ state: "S1", actor: "B", verb: "modify" }),
    row({ state: "S1", actor: "B", verb: "read", answer: "denied" }),
    row({ state: "S1", actor: "A", verb: "modify" }),
    row({ state: "S1", actor: null, verb: "modify", answer: "denied", byDefault: true }),
    row({ state: "S2", actor: "A", verb: "modify" }),
  ];

  it("lists the states in order and shows one state's actors as rows, everyone else last, and its verbs as columns", () => {
    expect(whoStates(rows)).toEqual(["S1", "S2"]);
    const view = whoView(rows, "S1", null, names);
    expect(view.actors.map((a) => a.label)).toEqual(["actor A", "actor B", "Everyone else"]);
    expect(view.verbs.map((v) => v.label)).toEqual(["MODIFY", "READ"]);
    expect(view.cell("B", "read")?.text).toBe("Denied");
    expect(view.cell("A", "read")).toBeNull();
    expect(view.cell(null, "modify")).toMatchObject({ text: "Denied", grey: true });
  });
});

describe("one row per entity", () => {
  const rows = [row({ entity: "E1", answer: "denied" }), row({ entity: "E2" }), row({ verb: "read" })];

  it("offers the entities only when rows are split, and picks the rows that hold for one", () => {
    expect(whoEntities(rows)).toEqual(["E1", "E2"]);
    expect(whoEntities([row({})])).toEqual([]);
    expect(rowsFor(rows, "S1", "E1").map((r) => r.verb + (r.entity ?? ""))).toEqual(["modifyE1", "read"]);
    expect(rowsFor(rows, "S1", null)).toHaveLength(3);
    expect(whoView(rows, "S1", "E2", names).cell("A", "modify")?.text).toBe("Allowed");
    expect(whoView(rows, "S1", "E1", names).cell("A", "modify")?.text).toBe("Denied");
  });
});

describe("what decides a cell", () => {
  const process = {
    id: "L",
    name: "Life",
    rights: [
      { id: "u1", name: "NoEdits", access: "deny", verbs: ["modify"] },
      { id: "u2", right: "N1" },
    ],
    events: [{ id: "ev1", name: "Promote" }],
    transitions: [{ id: "t1", source: "S1", gate: { id: "g1", name: "TwoSigs" } }],
    states: [{ id: "S1", name: "S1", invoke: [{ id: "task1", name: "Review" }] }],
  } as unknown as ProcessDoc;
  const named = new Map([["N1", { name: "PaidIsReadOnly" }]]);

  it("names the rights, events, gates, tasks, named rights and lifecycles with their kinds", () => {
    const list = deciders({ by: ["u1", "N1", "ev1", "g1", "task1", "L"], dependsOn: ["HR"] }, process, named, (id) => (id === "HR" ? "HR" : null));
    expect(list.map((d) => `${d.name}:${d.kind}`)).toEqual([
      "NoEdits:right",
      "PaidIsReadOnly:right",
      "Promote:event",
      "TwoSigs:gate",
      "Review:task",
      "Life:lifecycle",
      "HR:lifecycle",
    ]);
  });
});
