// The Processes explorer's grouping: processes by use, the parts under a process, actors by type, and the Domain model's
// Lifecycles folder and actor text; the node keys that favorites and deep links rely on stay as they were.
import { describe, expect, it } from "vitest";
import type { ElementSummary } from "@/api/types";
import { actorSecondary, buildForest, childKeys, documentChildren, nodeOf, patchForest, usedByOf, type Forest } from "@/explorer/tree";

const row = (r: Record<string, unknown>) =>
  ({ package: null, tags: [], category: null, stereotypes: [], hash: `h-${r.id}`, path: `${r.id}.json`, ...r }) as unknown as ElementSummary;

const ROWS = [
  row({ id: "P1", kind: "package", name: "Billing" }),
  row({ id: "P2", kind: "package", name: "Quiet" }),
  row({ id: "E1", kind: "entity", name: "Document", package: "P1" }),
  row({ id: "E2", kind: "entity", name: "Invoice", package: "P1", base: "E1" }),
  row({ id: "E3", kind: "entity", name: "Receipt", package: "P1" }),
  row({ id: "L1", kind: "process", name: "DocumentFlow", package: "P1", use: "lifecycle", subject: "E1", stateCount: 3 }),
  row({ id: "L2", kind: "process", name: "PairFlow", package: "P1", use: "lifecycle", subjects: ["E2", "E3"], stateCount: 2 }),
  row({ id: "O1", kind: "process", name: "Approval", package: "P1", use: "orchestration", stateCount: 5, actors: ["A1", "A2"] }),
  row({ id: "B1", kind: "process", name: "SharedChecks", package: "P1", use: "library", stateCount: 0 }),
  row({ id: "O2", kind: "process", name: "Loose", use: "orchestration", stateCount: 1, actors: ["A2"] }),
  row({ id: "A1", kind: "actor", name: "Clerk", actorType: "person" }),
  row({ id: "A2", kind: "actor", name: "Approver", actorType: "role", stereotypes: ["persona"] }),
  row({ id: "A3", kind: "actor", name: "Bank", actorType: "external-system" }),
  row({ id: "S1", kind: "scenario", name: "Happy", process: "O1", stepCount: 2 }),
  row({ id: "S2", kind: "scenario", name: "Broken", process: "O1", stepCount: 3 }),
  row({ id: "S3", kind: "scenario", name: "Alpha", process: "O1", stepCount: 1 }),
];

const labels = (f: Forest, key: string) => childKeys(f, key).map((k) => nodeOf(f, k)!.label);
const child = (f: Forest, key: string, label: string) => childKeys(f, key).find((k) => nodeOf(f, k)!.label === label)!;

describe("Processes explorer groups", () => {
  const f = buildForest({ rows: ROWS });
  const billing = child(f, f.roots.processes, "Billing");

  it("puts a domain's processes in Lifecycles, Orchestrations and Libraries, only the folders that have processes", () => {
    expect(labels(f, billing)).toEqual(["Lifecycles", "Orchestrations", "Libraries"]);
    expect(labels(f, child(f, billing, "Lifecycles"))).toEqual(["DocumentFlow", "PairFlow"]);
    expect(labels(f, child(f, billing, "Libraries"))).toEqual(["SharedChecks"]);
    // A domain with only one use still shows that use's folder, and a domain without processes is hidden.
    const only = buildForest({ rows: ROWS.filter((r) => r.id !== "L1" && r.id !== "L2" && r.id !== "B1") });
    expect(labels(only, child(only, only.roots.processes, "Billing"))).toEqual(["Orchestrations"]);
    expect(labels(f, f.roots.processes)).not.toContain("Quiet");
  });

  it("lists loose processes in use folders at the top, before Actors", () => {
    expect(labels(f, f.roots.processes)).toEqual(["Billing", "Orchestrations", "Actors"]);
    expect(childKeys(f, child(f, f.roots.processes, "Orchestrations"))).toEqual(["@processes/p:O2"]);
  });

  it("shows the entities a lifecycle governs, and keeps the process keys", () => {
    const lifecycles = child(f, billing, "Lifecycles");
    expect(childKeys(f, lifecycles)).toEqual(["@processes/p:L1", "@processes/p:L2"]);
    expect(nodeOf(f, "@processes/p:L1")!.secondary).toBe("lifecycle · Document · 3 states");
    expect(nodeOf(f, "@processes/p:L2")!.secondary).toBe("lifecycle · Invoice, Receipt · 2 states");
    expect(nodeOf(f, "@processes/p:B1")!.secondary).toBe("library");
    expect(nodeOf(f, "@processes/a:A1")).toBeDefined();
    expect(f.nodes.has("S1")).toBe(true);
  });

  it("orders the parts States, Events, Guards and actions, Gates, then Scenarios with failed ones first", () => {
    const status = new Map<string, { passed: true } | { passed: false; step: number }>([
      ["S1", { passed: true }],
      ["S2", { passed: false, step: 2 }],
    ]);
    const g = buildForest({ rows: ROWS, scenarioStatus: status });
    const key = "@processes/p:O1";
    documentChildren(g, key, {
      hash: "h",
      path: "p.json",
      json: {
        kind: "process",
        id: "O1",
        name: "Approval",
        states: [{ id: "s1", name: "Open" }],
        events: [{ id: "ev1", name: "go" }],
        guards: [{ id: "g1", name: "isBig" }],
        actions: [{ id: "a1", name: "notify" }],
        transitions: [{ id: "t1", source: "s1", gate: { id: "gt1", name: "TwoSigners" } }],
      },
    } as never);
    expect(labels(g, key)).toEqual(["States", "Events", "Guards and actions", "Gates", "Scenarios"]);
    expect(labels(g, child(g, key, "Guards and actions"))).toEqual(["isBig", "notify"]);
    expect(nodeOf(g, child(g, child(g, key, "Guards and actions"), "isBig"))!.kind).toBe("guard");
    expect(labels(g, child(g, key, "Gates"))).toEqual(["TwoSigners"]);
    const scenarios = child(g, key, "Scenarios");
    expect(labels(g, scenarios)).toEqual(["Broken", "Alpha", "Happy"]);
    expect(nodeOf(g, childKeys(g, scenarios)[0])!.secondary).toBe("3 steps · failed at step 2");
  });

  it("gives a library's guards, actions and gates the same folders", () => {
    const key = "@processes/p:B1";
    documentChildren(f, key, {
      hash: "h",
      path: "b.json",
      json: {
        kind: "process",
        id: "B1",
        name: "SharedChecks",
        use: "library",
        states: [],
        guards: [{ id: "g1", name: "isOk" }],
        gates: [{ id: "gt1", name: "Dual" }],
      },
    } as never);
    expect(labels(f, key)).toEqual(["States", "Events", "Guards and actions", "Gates", "Scenarios"]);
    expect(labels(f, child(f, key, "Gates"))).toEqual(["Dual"]);
  });

  it("groups actors as People, Roles and Systems, only the folders that have actors, and says where each acts", () => {
    const actors = child(f, f.roots.processes, "Actors");
    expect(labels(f, actors)).toEqual(["People", "Roles", "Systems"]);
    expect(labels(f, child(f, actors, "People"))).toEqual(["Clerk"]);
    expect(labels(f, child(f, actors, "Systems"))).toEqual(["Bank"]);
    expect(nodeOf(f, "@processes/a:A1")!.secondary).toBe("person · acts in 1 process");
    expect(nodeOf(f, "@processes/a:A2")!.secondary).toBe("role · persona · acts in 2 processes");
    expect(nodeOf(f, "@processes/a:A3")!.secondary).toBe("external system");
    const noRoles = buildForest({ rows: ROWS.filter((r) => r.id !== "A2") });
    expect(labels(noRoles, child(noRoles, noRoles.roots.processes, "Actors"))).toEqual(["People", "Systems"]);
    expect(actorSecondary({ actorType: "role", stereotypes: [] }, 3)).toBe("role · acts in 3 processes");
    expect(usedByOf(f, "A2").sort()).toEqual(["O1", "O2"]);
  });

  it("lists an entity's lifecycles in the Domain model, through its base entity too", () => {
    const direct = child(f, "E1", "Lifecycles");
    expect(labels(f, direct)).toEqual(["DocumentFlow"]);
    // Invoice derives from Document, and is also a subject of PairFlow.
    const derived = child(f, "E2", "Lifecycles");
    expect(labels(f, derived)).toEqual(["DocumentFlow", "PairFlow"]);
    expect(childKeys(f, derived).map((k) => nodeOf(f, k)!.secondary)).toEqual(["through Document", "2 states"]);
    // A click opens the process: the row points at the process row, as the other navigation rows do.
    expect(nodeOf(f, childKeys(f, derived)[0])!.target).toBe(f.place.get("L1"));
    expect(f.place.get("L1")).toBeDefined();
    expect(childKeys(f, "E3").some((k) => nodeOf(f, k)!.label === "Lifecycles")).toBe(true);
    expect(labels(f, child(f, "E3", "Lifecycles"))).toEqual(["PairFlow"]);
  });

  it("rebuilds, keeping every key, when a process changes use, and moves the row", () => {
    const changed = ROWS.map((r) => (r.id === "O1" ? ({ ...r, use: "lifecycle", subject: "E3", hash: "new" } as ElementSummary) : r));
    expect(patchForest(f, { upserts: [changed.find((r) => r.id === "O1")!], deleted: [] } as never)).toBeNull();
    const g = buildForest({ rows: changed });
    const b = child(g, g.roots.processes, "Billing");
    expect(labels(g, b)).toEqual(["Lifecycles", "Libraries"]);
    expect(childKeys(g, child(g, b, "Lifecycles"))).toContain("@processes/p:O1");
    const retyped = ROWS.map((r) => (r.id === "A1" ? ({ ...r, actorType: "role", hash: "new" } as ElementSummary) : r));
    expect(patchForest(f, { upserts: [retyped.find((r) => r.id === "A1")!], deleted: [] } as never)).toBeNull();
    const h = buildForest({ rows: retyped });
    expect(labels(h, child(h, h.roots.processes, "Actors"))).toEqual(["Roles", "Systems"]);
    expect(nodeOf(h, "@processes/a:A1")).toBeDefined();
  });
});
