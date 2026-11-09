// Fixes after the check of relations with several types, part 1: the References pointers, the delete dialog's dependents, the names in
// an end's type list, the stereotype's one spelling and the quick fix's label.
import { describe, expect, it } from "vitest";
import type { ElementSummary } from "@/api/types";
import type { DeletePlan } from "@/api/types";
import { dependentsOf, type DeletePlans } from "@/inspector/DeletePlanView";
import { indexReferences } from "@/references/data";
import { endLabel } from "@/model/endTypes";
import { buildForest, childKeys, nodeOf, type TreeNode } from "@/explorer/tree";
import { objectFields, type FieldSources } from "@/model/objectFields";

const row = (r: Partial<ElementSummary> & { id: string }) => r as ElementSummary;

describe("References pointers", () => {
  it("name the entry of a list, even a list of one entity", () => {
    const rows = [
      row({
        id: "R1",
        kind: "relation",
        ends: [
          { role: "a", entity: "C" },
          { role: "b", entity: "P", entities: ["P"] },
        ],
      }),
      row({
        id: "R2",
        kind: "relation",
        ends: [
          { role: "a", entity: "C" },
          { role: "b", entities: ["A", "P"], stereotypes: ["S"] },
        ],
      }),
    ];
    expect(indexReferences("P", rows).map((r) => [r.fromId, r.jsonPointer])).toEqual([
      ["R1", "/ends/1/types/0/entity"],
      ["R2", "/ends/1/types/1/entity"],
    ]);
    expect(indexReferences("S", rows).map((r) => r.jsonPointer)).toEqual(["/ends/1/types"]);
    expect(indexReferences("C", rows).map((r) => r.jsonPointer)).toEqual(["/ends/0/entity", "/ends/0/entity"]);
  });
});

describe("the delete dialog's dependents", () => {
  const plan = (p: Partial<DeletePlan>): DeletePlan =>
    ({ outcome: "saved", deletes: [], removes: [], clears: [], refused: [], settings: [], warnings: [], ...p }) as DeletePlan;
  const removes = [{ id: "R", pointer: "/ends/1/types/1", kind: "relation", name: "Parks", what: "types Truck", because: "needs entity Truck" }];

  it("counts none when both ways only take an entry out of a list", () => {
    const plans: DeletePlans = { dependents: plan({ removes: removes as never }), clear: plan({ removes: removes as never }) };
    expect(dependentsOf(plans)).toBe(0);
  });

  it("counts what only the dependents way deletes or removes", () => {
    const plans: DeletePlans = {
      dependents: plan({ deletes: [{ id: "X", kind: "attribute", name: "x", because: "part of it" }] as never, removes: removes as never }),
      clear: plan({ removes: removes as never }),
    };
    expect(dependentsOf(plans)).toBe(1);
  });
});

describe("names in an end's type list", () => {
  it("come from the index when the entities' documents are not loaded, and stereotypes read as their key", () => {
    const docs: Record<string, Record<string, unknown>> = {
      C: { id: "C", kind: "entity", name: "Comment", attributes: [] },
      eR: {},
    };
    const sources: FieldSources = {
      doc: (id) => docs[id],
      relations: [
        {
          id: "R",
          kind: "relation",
          name: "Link",
          ends: [
            { id: "e1", entity: "C", role: "comment", max: "*" },
            { id: "e2", role: "inspected", types: [{ entity: "P" }, { entity: "A" }], max: 1 },
          ],
        },
      ],
      stereotype: () => undefined,
      kindOf: () => "entity",
      nameOf: (id) => ({ P: "Car", A: "Truck" })[id],
    };
    const end = objectFields("C", sources).find((f) => f.id === "e2")!;
    expect(end.typesLabel).toBe("Car or Truck");
  });

  it("one spelling for a stereotype entry: its key, even when the file is named otherwise", () => {
    const rows = [
      row({ id: "D", kind: "package", name: "Docs", path: "d.json" }),
      row({ id: "C", kind: "entity", name: "Comment", path: "c.json", package: "D" }),
      row({ id: "S", kind: "stereotype", name: "Stamped", path: "vocabularies/stereotypes/zz-stamped.json" }),
      row({
        id: "R",
        kind: "relation",
        name: "Stamps",
        package: "D",
        path: "r.json",
        ends: [
          { role: "a", entity: "C" },
          { role: "b", stereotypes: ["S"] },
        ],
      }),
    ];
    const forest = buildForest({ rows, stereotypeKeys: new Map([["S", "stamped"]]) });
    const walk = (key: string): TreeNode | undefined => {
      const n = nodeOf(forest, key);
      if (n?.id === "R") return n;
      for (const k of childKeys(forest, key)) {
        const found = walk(k);
        if (found) return found;
      }
      return undefined;
    };
    const secondary = walk(forest.roots["domain-model"])?.secondary;
    expect(secondary).toBe("Comment → stamped (stereotype)");
    expect(
      endLabel(
        { stereotypes: ["S"] },
        () => undefined,
        (id) => new Map([["S", "stamped"]]).get(id),
      ),
    ).toBe("stamped (stereotype)");
  });
});
