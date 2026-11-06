// An actor taken from the object in the Processes explorer (its row text and the From the object folder), and the MQ9606 quick fix.
import { describe, expect, it } from "vitest";
import type { Diagnostic, ElementSummary } from "@/api/types";
import { actorSecondary, buildForest, childKeys, nodeOf, type Forest } from "@/explorer/tree";
import { memberOfChoices, type ActorRow } from "@/model/actors";
import { relativeRowText } from "@/model/objectFields";
import { deriveQuickFix, hasQuickFix } from "@/problems/quickFix";

const row = (r: Record<string, unknown>) =>
  ({ package: null, tags: [], category: null, stereotypes: [], hash: `h-${r.id}`, path: `${r.id}.json`, ...r }) as unknown as ElementSummary;

const labels = (f: Forest, key: string) => childKeys(f, key).map((k) => nodeOf(f, k)!.label);
const child = (f: Forest, key: string, label: string) => childKeys(f, key).find((k) => nodeOf(f, k)!.label === label)!;

describe("an actor taken from the object in the explorer", () => {
  const rows = [
    row({ id: "A1", kind: "actor", name: "Clerk", actorType: "person" }),
    row({ id: "A2", kind: "actor", name: "Owner", actorType: "person", relative: "owner" }),
    row({ id: "A3", kind: "actor", name: "Manager", actorType: "person", relative: "path" }),
    row({ id: "A4", kind: "actor", name: "ProjectReviewer", actorType: "role", relative: "members-of" }),
    row({ id: "A5", kind: "actor", name: "Reviewer", actorType: "role" }),
  ];
  const f = buildForest({ rows });
  const actors = child(f, f.roots.processes, "Actors");

  it("says how it is worked out in its row text", () => {
    expect(nodeOf(f, "@processes/a:A2")!.secondary).toBe("person · owner");
    expect(nodeOf(f, "@processes/a:A3")!.secondary).toBe("person · from a field");
    expect(nodeOf(f, "@processes/a:A4")!.secondary).toBe("role · within a context");
    expect(nodeOf(f, "@processes/a:A1")!.secondary).toBe("person");
    expect(actorSecondary({ actorType: "person", stereotypes: [], relative: "none" }, 2)).toBe("person · from the object · acts in 2 processes");
    expect(relativeRowText(undefined)).toBe("");
  });

  it("seats it only in From the object, whatever its type, after the type folders", () => {
    expect(labels(f, actors)).toEqual(["People", "Roles", "From the object"]);
    expect(labels(f, child(f, actors, "People"))).toEqual(["Clerk"]);
    expect(labels(f, child(f, actors, "Roles"))).toEqual(["Reviewer"]);
    expect(labels(f, child(f, actors, "From the object"))).toEqual(["Manager", "Owner", "ProjectReviewer"]);
  });

  it("is never offered as a holder in Member of", () => {
    const list = rows as unknown as ActorRow[];
    expect(memberOfChoices(list, "A1").map((r) => r.name)).toEqual(["Reviewer"]);
  });
});

describe("the MQ9606 quick fix", () => {
  const process = {
    kind: "process",
    id: "P1",
    name: "Review",
    use: "orchestration",
    subjects: [
      { id: "S1", entity: "E1", owner: "bad" },
      { id: "S2", entity: "E2", owner: "worse" },
    ],
  };
  const finding = (pointer: string): Diagnostic => ({
    rule: "MQ9606",
    severity: "error",
    message: "m",
    elementId: "S2",
    filePath: "p.json",
    jsonPointer: pointer,
    line: null,
    column: null,
  });

  it("is a fix, and clears the owner of the entry the finding names", () => {
    expect(hasQuickFix("MQ9606", null)).toBe(true);
    const fix = deriveQuickFix(finding("/subjects/1/owner"), null, () => process as never);
    expect(fix).toMatchObject({ kind: "update", id: "P1", label: "Clear the owner" });
    const json = (fix as { json: typeof process }).json;
    expect(json.subjects[0].owner).toBe("bad");
    expect(json.subjects[1]).toEqual({ id: "S2", entity: "E2" });
    expect(deriveQuickFix(finding("/subjects/1/entity"), null, () => process as never)).toBeNull();
  });
});
