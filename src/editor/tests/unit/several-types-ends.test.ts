// The editor's end helper and the relation form's choices and limits (several-types-design.md decisions 2 and 4, section 11), the
// quick fixes of MQ3042 and MQ3043, and the places that must leave out or read an end with several types.
import { describe, expect, it } from "vitest";
import type { Diagnostic } from "@/api/types";
import {
  docEndHasSeveralTypes,
  docEndSingleEntity,
  endCoversEntity,
  endHasSeveralTypes,
  endLabel,
  endReach,
  endSingleEntity,
  endSummaryOf,
} from "@/model/endTypes";
import { END_LIMIT_TEXTS, addEntry, endChoiceLimits, endChoiceOf, moveEnd, pickOneEntity, removeEntry, type EndForm } from "@/inspector/relationEnds";
import { deriveQuickFix, endTypesFix, hasQuickFix } from "@/problems/quickFix";
import { entitySeedEnds, relationGridColumns } from "@/workspaces/reference-data/rowsModel";
import { relationEnds, viewElements } from "@/canvas/model";
import { relatedOf } from "@/editors/related";
import type { ElementSummary } from "@/api/types";

type Json = Record<string, unknown>;

describe("the end helper over index rows", () => {
  const one = { entity: "P", role: "a" };
  const several = { role: "b", entities: ["P", "A"], stereotypes: ["S"], covers: ["C"] };
  const any = { role: "c", anyEntity: true as const };

  it("tells a one-type end from an end with several types", () => {
    expect(endHasSeveralTypes(one)).toBe(false);
    expect(endHasSeveralTypes(several)).toBe(true);
    expect(endHasSeveralTypes(any)).toBe(true);
    expect(endSingleEntity(one)).toBe("P");
    expect(endSingleEntity(several)).toBeNull();
    expect(endSingleEntity(any)).toBeNull();
  });

  it("covers the listed entities, the carriers of a stereotype, derived entities through their base, and every entity for anyEntity", () => {
    const base = (id: string) => ({ D: "P" })[id];
    expect(endCoversEntity(several, "A")).toBe(true);
    expect(endCoversEntity(several, "C")).toBe(true);
    expect(endCoversEntity(several, "X")).toBe(false);
    expect(endCoversEntity(several, "D", base)).toBe(true);
    expect(endCoversEntity(several, "D")).toBe(false);
    expect(endCoversEntity(any, "X")).toBe(true);
    expect(endCoversEntity(one, "P")).toBe(true);
  });

  it("labels an end in plain words", () => {
    const names: Record<string, string> = { P: "Part", A: "Assembly", S: "approvable" };
    const name = (id: string) => names[id];
    expect(endLabel(one, name)).toBe("Part");
    expect(endLabel({ role: "b", entities: ["P", "A"], stereotypes: ["S"] }, name)).toBe("Part, Assembly or approvable (stereotype)");
    expect(endLabel({ role: "b", stereotypes: ["S"] }, name)).toBe("approvable (stereotype)");
    expect(endLabel(any, name)).toBe("any entity");
  });

  it("says how an entity is reached", () => {
    const name = (id: string) => ({ S: "approvable" })[id];
    expect(endReach(any, "X", name)).toBe("any entity");
    expect(endReach(one, "P", name)).toBe("");
    expect(endReach({ role: "b", entities: ["P"], stereotypes: ["S"] }, "P", name)).toBe("");
    expect(endReach({ role: "b", entities: ["P"], stereotypes: ["S"] }, "C", name)).toBe("through approvable (stereotype)");
  });

  it("writes the index row of a document end as the engine does", () => {
    const stereotypes = new Map([["approvable", "SID"]]);
    expect(endSummaryOf({ id: "e", entity: "P", role: "a" })).toEqual({ entity: "P", role: "a" });
    // A list of one entity: the engine writes the entity and the list.
    expect(endSummaryOf({ id: "e", role: "a", types: [{ entity: "P" }] })).toEqual({ entity: "P", role: "a", entities: ["P"] });
    expect(endSummaryOf({ id: "e", role: "a", types: [{ entity: "P" }, { stereotype: "approvable" }, { entity: "A" }] }, stereotypes)).toEqual({
      role: "a",
      entities: ["P", "A"],
      stereotypes: ["SID"],
    });
    expect(endSummaryOf({ id: "e", role: "a", types: [{ stereotype: "unknown" }, { entity: "P" }] }, stereotypes)).toEqual({ role: "a", entities: ["P"] });
    expect(endSummaryOf({ id: "e", role: "a", anyEntity: true })).toEqual({ role: "a", anyEntity: true });
  });

  it("reads a document end the same way", () => {
    expect(docEndSingleEntity({ types: [{ entity: "P" }] })).toBe("P");
    expect(docEndSingleEntity({ types: [{ entity: "P" }, { entity: "A" }] })).toBeNull();
    expect(docEndSingleEntity({ types: [{ stereotype: "s" }] })).toBeNull();
    expect(docEndHasSeveralTypes({ types: [{ entity: "P" }] })).toBe(false);
    expect(docEndHasSeveralTypes({ types: [{ stereotype: "s" }] })).toBe(true);
    expect(docEndHasSeveralTypes({ anyEntity: true })).toBe(true);
    expect(docEndHasSeveralTypes({ entity: "P" })).toBe(false);
  });
});

describe("the relation form's choices and limits", () => {
  const ends = (a: EndForm, b: EndForm, relationKind?: string) => ({ relationKind, ends: [a, b] });
  const oneEnd = (entity: string, max: 1 | "*" = "*"): EndForm => ({ entity, max });

  it("offers every choice on a plain relation", () => {
    expect(endChoiceLimits(ends(oneEnd("P"), oneEnd("A")), 0)).toEqual({});
  });

  it("offers every choice on the other end once one end has several types (part 2 lifts the one-end limit)", () => {
    const relation = ends(oneEnd("P"), { types: [{ entity: "P" }, { entity: "A" }], max: 1 });
    expect(endChoiceLimits(relation, 0)).toEqual({});
    expect(endChoiceLimits(relation, 1)).toEqual({});
    const both = ends({ types: [{ entity: "P" }, { entity: "A" }], max: "*" }, { anyEntity: true, max: 1 });
    expect(endChoiceLimits(both, 0)).toEqual({});
    expect(endChoiceLimits(both, 1)).toEqual({});
    expect("oneEnd" in END_LIMIT_TEXTS).toBe(false);
  });

  it("keeps only One entity on an n-ary relation", () => {
    const relation = { relationKind: "n-ary", ends: [oneEnd("P"), oneEnd("A"), oneEnd("B")] };
    expect(endChoiceLimits(relation, 0)).toEqual({ several: END_LIMIT_TEXTS.nary, any: END_LIMIT_TEXTS.nary });
    expect(END_LIMIT_TEXTS.nary).toBe("An n-ary relation's ends name one entity each.");
  });

  it("leaves Any entity out of a composition's part end", () => {
    // The owner end has max 1; the other end is the part.
    const relation = ends(oneEnd("P", "*"), oneEnd("A", 1), "composition");
    expect(endChoiceLimits(relation, 0)).toEqual({ any: END_LIMIT_TEXTS.child });
    expect(endChoiceLimits(relation, 1)).toEqual({});
  });

  it("reads the choice from what the file writes", () => {
    expect(endChoiceOf({ entity: "P" })).toBe("one");
    expect(endChoiceOf({ types: [{ entity: "P" }, { entity: "A" }] })).toBe("several");
    expect(endChoiceOf({ types: [{ entity: "P" }] })).toBe("several");
    expect(endChoiceOf({ anyEntity: true })).toBe("any");
  });

  it("moves between the choices, writing exactly one of entity, types and anyEntity", () => {
    const end: EndForm = { entity: "P" };
    // One entity to Several types waits for a second entry: a list of one entity would be MQ3042.
    expect(moveEnd(end, "several")).toBe(false);
    expect(end).toEqual({ entity: "P" });
    addEntry(end, { entity: "Q" });
    expect(end).toEqual({ types: [{ entity: "P" }, { entity: "Q" }] });
    delete end.types;
    end.entity = "P";
    addEntry(end, { stereotype: "approvable" });
    addEntry(end, { entity: "A" });
    addEntry(end, { entity: "A" });
    expect(end.types).toEqual([{ entity: "P" }, { stereotype: "approvable" }, { entity: "A" }]);
    // Going back with several entries needs a pick; the end is left as it was.
    expect(moveEnd(end, "one")).toBe(false);
    expect(end.types).toHaveLength(3);
    pickOneEntity(end, "A");
    expect(end).toEqual({ entity: "A" });
    expect(moveEnd(end, "any")).toBe(true);
    expect(end).toEqual({ anyEntity: true });
    // From Any entity, Several types waits for the first entry; One entity waits for a pick.
    expect(moveEnd(end, "several")).toBe(false);
    expect(moveEnd(end, "one")).toBe(false);
    addEntry(end, { entity: "P" });
    expect(end).toEqual({ types: [{ entity: "P" }] });
  });

  it("writes entity when going back to One entity with one entity entry", () => {
    const end: EndForm = { types: [{ entity: "P" }] };
    expect(moveEnd(end, "one")).toBe(true);
    expect(end).toEqual({ entity: "P" });
  });

  it("keeps the last entry", () => {
    const end: EndForm = { types: [{ entity: "P" }, { stereotype: "s" }] };
    expect(removeEntry(end, 0)).toBe(true);
    expect(end.types).toEqual([{ stereotype: "s" }]);
    expect(removeEntry(end, 0)).toBe(false);
    expect(end.types).toEqual([{ stereotype: "s" }]);
  });
});

describe("the quick fixes of MQ3042 and MQ3043", () => {
  const relation = (types: Json[]): Json => ({
    kind: "relation",
    id: "R",
    name: "AttachedTo",
    ends: [
      { id: "e1", entity: "C", role: "comment" },
      { id: "e2", role: "attachedTo", types },
    ],
  });
  const fix = (rule: string, pointer: string, json: Json) =>
    deriveQuickFix({ rule, severity: "warning", message: "m", jsonPointer: pointer, elementId: "R" } as Diagnostic, null, () => json as never) as {
      kind: string;
      label: string;
      json: Json;
    } | null;

  it("offers a fix for both rules", () => {
    expect(hasQuickFix("MQ3042", null)).toBe(true);
    expect(hasQuickFix("MQ3043", null)).toBe(true);
  });

  it("MQ3042 writes entity in place of a list of one entity", () => {
    const f = fix("MQ3042", "/ends/1/types", relation([{ entity: "P" }]))!;
    expect(f.label).toBe("Use entity");
    expect((f.json.ends as Json[])[1]).toEqual({ id: "e2", role: "attachedTo", entity: "P" });
  });

  it("MQ3042 offers nothing for a list that holds more, or a stereotype", () => {
    expect(fix("MQ3042", "/ends/1/types", relation([{ entity: "P" }, { entity: "A" }]))).toBeNull();
    expect(fix("MQ3042", "/ends/1/types", relation([{ stereotype: "s" }]))).toBeNull();
  });

  it("MQ3043 removes the entry its pointer names", () => {
    const f = fix("MQ3043", "/ends/1/types/1", relation([{ entity: "P" }, { entity: "D" }, { entity: "A" }]))!;
    expect(f.label).toBe("Remove the entry");
    expect(((f.json.ends as Json[])[1] as Json).types).toEqual([{ entity: "P" }, { entity: "A" }]);
  });

  it("MQ3043 keeps the last entry and ignores a pointer that names no entry", () => {
    expect(endTypesFix("MQ3043", relation([{ entity: "P" }]) as never, "/ends/1/types/0")).toBeNull();
    expect(endTypesFix("MQ3043", relation([{ entity: "P" }, { entity: "A" }]) as never, "/ends/1/types")).toBeNull();
    expect(endTypesFix("MQ3043", relation([{ entity: "P" }, { entity: "A" }]) as never, "/ends/1/types/5")).toBeNull();
  });
});

describe("the places that read an end", () => {
  const rows = [
    { id: "P", kind: "entity", name: "Part" },
    { id: "A", kind: "entity", name: "Assembly" },
    { id: "C", kind: "entity", name: "Comment" },
    {
      id: "R1",
      kind: "relation",
      name: "Holds",
      ends: [
        { entity: "C", role: "c" },
        { entity: "P", role: "p" },
      ],
    },
    {
      id: "R2",
      kind: "relation",
      name: "AttachedTo",
      ends: [
        { entity: "C", role: "c" },
        { role: "x", entities: ["P", "A"] },
      ],
    },
  ];

  it("shows no canvas line for an end with several types, and keeps a one-type relation", () => {
    const shown = viewElements({ type: "package", id: "all" } as never, rows as never, []);
    expect(shown.relationIds).not.toContain("R2");
    const positions = new Map([
      ["C", { x: 0, y: 0 }],
      ["P", { x: 300, y: 0 }],
    ]);
    expect(relationEnds({ ends: [{ entity: "C" }, { entity: "P" }] } as never, positions)).toMatchObject({ source: "C", target: "P" });
    expect(relationEnds({ ends: [{ entity: "C" }, { types: [{ entity: "P" }, { entity: "A" }] }] } as never, positions)).toBeNull();
    expect(relationEnds({ ends: [{ entity: "C" }, { anyEntity: true }] } as never, positions)).toBeNull();
    // A list of one entity is a one-type end.
    expect(relationEnds({ ends: [{ entity: "C" }, { types: [{ entity: "P" }] }] } as never, positions)).toMatchObject({ target: "P" });
  });

  it("lists a relation among the relations of every entity an end names", () => {
    const related = (id: string) => relatedOf(rows as unknown as ElementSummary[], id).relations.map((r) => r.id);
    expect(related("P")).toEqual(["R2", "R1"].sort((a, b) => rows.find((r) => r.id === a)!.name.localeCompare(rows.find((r) => r.id === b)!.name)));
    expect(related("A")).toEqual(["R2"]);
    expect(related("C").sort()).toEqual(["R1", "R2"]);
  });

  it("leaves an end with several types out of a seed's columns", () => {
    const lineage = new Set(["C"]);
    const relation = (far: Json): never =>
      ({
        id: "R",
        ends: [
          { id: "e1", entity: "C", role: "c", max: "*" },
          { id: "e2", role: "x", max: 1, ...far },
        ],
      }) as never;
    expect(entitySeedEnds(lineage, [relation({ entity: "P" })], () => false).map((e) => e.end.id)).toEqual(["e2"]);
    expect(entitySeedEnds(lineage, [relation({ types: [{ entity: "P" }, { entity: "A" }] })], () => false)).toEqual([]);
    expect(entitySeedEnds(lineage, [relation({ anyEntity: true })], () => false)).toEqual([]);
    expect(
      relationGridColumns(
        {
          ends: [
            { id: "e1", entity: "C", role: "c" },
            { id: "e2", anyEntity: true, role: "x" },
          ],
        } as never,
        () => undefined,
      ),
    ).toEqual([]);
  });
});
