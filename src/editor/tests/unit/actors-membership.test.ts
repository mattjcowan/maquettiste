// Actor types and membership in the editor (actors-design.md sections 3 and 9): the six types, the membership helpers, the
// Processes explorer's Actors folders and member rows, the actor editor's batch for Add members, and the two quick fixes.
import { describe, expect, it } from "vitest";
import type { Diagnostic, ElementSummary } from "@/api/types";
import { directMembers, memberCount, memberOfChoices, memberOfText, systemActorProblem, wouldCycle, type ActorRow } from "@/model/actors";
import { ACTOR_TYPES, ACTOR_TYPE_LABELS, ACTOR_TYPE_OPTION_LABELS } from "@/model/labels";
import { buildActor } from "@/explorer/processCreate";
import { actorSecondary, buildForest, childKeys, isExpandable, nodeOf, pathOf, placeIn, type Forest } from "@/explorer/tree";
import { deriveQuickFix, hasQuickFix } from "@/problems/quickFix";
import { lastStepLines, type Names } from "@/editors/process/simulation/model";

const actor = (id: string, name: string, actorType: ActorRow["actorType"], memberOf?: string[]): ActorRow => ({
  id,
  kind: "actor",
  name,
  actorType,
  ...(memberOf ? { memberOf } : {}),
});

const ROWS: ActorRow[] = [
  actor("P1", "Ann", "person", ["R1", "G1"]),
  actor("P2", "Bob", "person", ["G1"]),
  actor("R1", "Reviewer", "role", ["O1"]),
  actor("G1", "NightShift", "group", ["O1"]),
  actor("O1", "Plant3", "organization"),
  actor("X1", "Bank", "external-system"),
  actor("S1", "Scheduler", "system"),
];

describe("actor types", () => {
  it("has the six types, each labeled, with the system actor shown as Automatic", () => {
    expect([...ACTOR_TYPES]).toEqual(["person", "role", "group", "organization", "external-system", "system"]);
    expect(ACTOR_TYPE_LABELS.system).toBe("Automatic");
    expect(ACTOR_TYPE_LABELS.group).toBe("group");
    expect(ACTOR_TYPE_OPTION_LABELS.system).toBe("Automatic (the system itself)");
  });

  it("builds a new actor of any of them", () => {
    for (const type of ACTOR_TYPES) expect(buildActor({ name: "A", type, stereotypes: [] }, () => "id")).toMatchObject({ kind: "actor", type });
  });
});

describe("membership helpers", () => {
  it("lists direct members and counts members at any depth once", () => {
    expect(directMembers(ROWS, "G1").map((r) => r.name)).toEqual(["Ann", "Bob"]);
    expect(memberCount(ROWS, "O1")).toBe(4);
    expect(memberCount(ROWS, "R1")).toBe(1);
    expect(memberCount(ROWS, "P1")).toBe(0);
  });

  it("never loops on a cycle and never counts the actor among its own members", () => {
    const loop = [actor("A", "A", "role", ["B"]), actor("B", "B", "role", ["A"])];
    expect(memberCount(loop, "A")).toBe(1);
    expect(memberCount(loop, "B")).toBe(1);
  });

  it("offers roles, groups and organizations for Member of, never the actor itself", () => {
    expect(memberOfChoices(ROWS, "R1").map((r) => r.name)).toEqual(["NightShift", "Plant3"]);
    expect(memberOfChoices(ROWS, "P1").map((r) => r.name)).toEqual(["NightShift", "Plant3", "Reviewer"]);
  });

  it("finds a membership that would close a cycle", () => {
    expect(wouldCycle(ROWS, "R1", "R1")).toBe(true);
    // Reviewer is a member of Plant3, so Plant3 cannot become a member of Reviewer.
    expect(wouldCycle(ROWS, "R1", "O1")).toBe(true);
    expect(wouldCycle(ROWS, "O1", "R1")).toBe(false);
    expect(wouldCycle(ROWS, "G1", "P2")).toBe(false);
  });

  it("refuses a second Automatic actor with the first one's name", () => {
    expect(systemActorProblem(ROWS)).toContain("Scheduler");
    expect(systemActorProblem(ROWS, "S1")).toBeNull();
    expect(systemActorProblem(ROWS.filter((r) => r.id !== "S1"))).toBeNull();
  });

  it("writes member of text from the holders' names in file order", () => {
    const names = new Map(ROWS.map((r) => [r.id, r.name]));
    expect(memberOfText(["R1", "G1"], (id) => names.get(id))).toBe("member of Reviewer, NightShift");
    expect(memberOfText(["gone"], (id) => names.get(id))).toBe("");
    expect(memberOfText(undefined, (id) => names.get(id))).toBe("");
  });
});

describe("the Processes explorer's actors", () => {
  const rows = ROWS.map(
    (r) => ({ package: null, tags: [], category: null, stereotypes: [], hash: `h-${r.id}`, path: `${r.id}.json`, ...r }) as unknown as ElementSummary,
  );
  const f = buildForest({ rows });
  const labels = (forest: Forest, key: string) => childKeys(forest, key).map((k) => nodeOf(forest, k)!.label);
  const child = (forest: Forest, key: string, label: string) => childKeys(forest, key).find((k) => nodeOf(forest, k)!.label === label)!;
  const actors = child(f, f.roots.processes, "Actors");

  it("has People, Roles, Groups, Organizations, Systems and Automatic folders, only those with actors", () => {
    expect(labels(f, actors)).toEqual(["People", "Roles", "Groups", "Organizations", "Systems", "Automatic"]);
    expect(labels(f, child(f, actors, "Automatic"))).toEqual(["Scheduler"]);
    expect(labels(f, child(f, actors, "Groups"))).toEqual(["NightShift"]);
    const none = buildForest({ rows: rows.filter((r) => r.actorType === "person") });
    expect(labels(none, child(none, none.roots.processes, "Actors"))).toEqual(["People"]);
  });

  it("lists a holder's members as rows that go to the member's own row, and a member that holds members expands in turn", () => {
    const plant = child(f, child(f, actors, "Organizations"), "Plant3");
    expect(isExpandable(f, plant)).toBe(true);
    expect(labels(f, plant)).toEqual(["NightShift", "Reviewer"]);
    const reviewer = child(f, plant, "Reviewer");
    expect(nodeOf(f, reviewer)).toMatchObject({ home: false, target: placeIn(f, "processes", "R1") });
    expect(labels(f, reviewer)).toEqual(["Ann"]);
    expect(pathOf(f, child(f, reviewer, "Ann")).length).toBe(pathOf(f, reviewer).length + 1);
    // A person, a system and a role with no members have no member rows.
    expect(isExpandable(f, child(f, child(f, actors, "People"), "Ann"))).toBe(false);
    expect(isExpandable(f, child(f, child(f, actors, "Automatic"), "Scheduler"))).toBe(false);
  });

  it("writes member of text in an actor's row", () => {
    expect(nodeOf(f, child(f, child(f, actors, "People"), "Ann"))!.secondary).toBe("person · member of Reviewer, NightShift");
    expect(nodeOf(f, child(f, child(f, actors, "Automatic"), "Scheduler"))!.secondary).toBe("Automatic");
    expect(actorSecondary({ actorType: "role", stereotypes: [] }, 2, "member of Plant3")).toBe("role · member of Plant3 · acts in 2 processes");
  });

  it("still draws a model with a membership cycle, stopping at an actor already on the path", () => {
    const loop = [actor("A", "Alpha", "role", ["B"]), actor("B", "Beta", "role", ["A"])].map(
      (r) => ({ package: null, tags: [], category: null, stereotypes: [], hash: "h", path: `${r.id}.json`, ...r }) as unknown as ElementSummary,
    );
    const lf = buildForest({ rows: loop });
    const a = child(lf, child(lf, lf.roots.processes, "Actors"), "Roles");
    const alpha = child(lf, a, "Alpha");
    const beta = child(lf, alpha, "Beta");
    expect(labels(lf, alpha)).toEqual(["Beta"]);
    expect(labels(lf, beta)).toEqual([]);
  });
});

describe("quick fixes for the membership rules", () => {
  const diag = (rule: string, jsonPointer: string): Diagnostic => ({
    rule,
    severity: "error",
    message: rule,
    elementId: "P1",
    filePath: null,
    jsonPointer,
    line: null,
    column: null,
  });
  const doc = { kind: "actor", id: "P1", name: "Ann", type: "person", memberOf: ["P1", "R1"] };

  it("has them as editor fixes", () => {
    expect(hasQuickFix("MQ9601", null)).toBe(true);
    expect(hasQuickFix("MQ9605", null)).toBe(true);
    expect(hasQuickFix("MQ9602", null)).toBe(false);
  });

  it("MQ9601 removes the entry the finding names", () => {
    const fix = deriveQuickFix(diag("MQ9601", "/memberOf/0"), null, () => doc as never);
    expect(fix).toMatchObject({ kind: "update", id: "P1", label: "Remove the entry" });
    expect((fix as { json: { memberOf: string[] } }).json.memberOf).toEqual(["R1"]);
    expect(deriveQuickFix(diag("MQ9601", "/memberOf"), null, () => doc as never)).toBeNull();
  });

  it("MQ9605 changes the type to an external system", () => {
    const fix = deriveQuickFix(diag("MQ9605", "/type"), null, () => ({ ...doc, type: "system" }) as never);
    expect(fix).toMatchObject({ kind: "update", id: "P1" });
    expect((fix as { json: { type: string } }).json.type).toBe("external-system");
  });
});

describe("the simulation's last step", () => {
  const names = { actor: (id: string) => (id === "S1" ? "Scheduler" : id) } as unknown as Names;
  const entry = (extra: Record<string, unknown>) =>
    ({
      index: 0,
      input: null,
      accepted: true,
      refusal: null,
      microsteps: [],
      guards: [],
      audit: [],
      configuration: [],
      context: {},
      changed: {},
      clock: "",
      final: false,
      diagnostics: [],
      incomplete: false,
      ...extra,
    }) as never;

  it("says who made a move the input names no actor for", () => {
    expect(lastStepLines(entry({ actor: "S1", input: { actor: null } }), names)[0]).toMatchObject({ kind: "actor", text: "by Scheduler" });
    expect(lastStepLines(entry({ actor: "S1" }), names)[0]).toMatchObject({ kind: "actor" });
  });

  it("leaves it out when the input names the actor (the trace row has it) or the move has none", () => {
    expect(lastStepLines(entry({ actor: "P1", input: { actor: "P1" } }), names).some((l) => l.kind === "actor")).toBe(false);
    expect(lastStepLines(entry({}), names).some((l) => l.kind === "actor")).toBe(false);
  });
});
