// Several types at both ends, in the editor until the both-ends drawing lands (several-types-design.md 19.12, "If the both-ends editor
// work moves to 0.23.0"): the readers skip or accept such a relation without breaking, the form keeps the one-end limit for new edits and
// opens what the file holds, and the canvas draws one line between the first card of each end with "+N more" labels.
import { describe, expect, it } from "vitest";
import type { RelationDoc } from "@/api/types";
import { relationEnds, relationLookup, viewElements } from "@/canvas/model";
import { bothEndsFallback, hasSeveralTypesBothEnds, hasSeveralTypesEnd } from "@/canvas/severalTypes";
import { endChoiceLimits, endChoiceOf, END_LIMIT_TEXTS } from "@/inspector/relationEnds";

const link = {
  id: "link",
  name: "Link",
  ends: [
    { id: "from", types: [{ entity: "Comment" }, { entity: "Note" }, { stereotype: "approvable" }], role: "source", max: "*" },
    { id: "to", types: [{ entity: "Part" }, { entity: "Assembly" }], role: "target", max: "*" },
  ],
} as unknown as RelationDoc;

const names: Record<string, string> = { Comment: "Comment", Note: "Note", Part: "Part", Assembly: "Assembly" };
const at = (...ids: string[]) => new Map(ids.map((id, i) => [id, { x: i * 300, y: 0 }]));

describe("the canvas's plain fallback for several types at both ends", () => {
  it("is recognised as both ends, and is not the one-end drawing", () => {
    expect(hasSeveralTypesBothEnds(link)).toBe(true);
    expect(hasSeveralTypesEnd(link)).toBe(true);
    const oneEnd = { id: "r", name: "R", ends: [{ id: "a", entity: "Comment", max: "*" }, link.ends[1]] } as unknown as RelationDoc;
    expect(hasSeveralTypesBothEnds(oneEnd)).toBe(false);
  });

  it("draws one line between the first card of each end", () => {
    const ends = relationEnds(link, at("Note", "Assembly", "Part", "Comment"));
    // The first listed entry with a card: Comment is the first of the source end, Part of the target end.
    expect(ends).toMatchObject({ source: "Comment", target: "Part" });
    expect(relationEnds(link, at("Note", "Assembly"))).toMatchObject({ source: "Note", target: "Assembly" });
  });

  it("draws no line when an end has no card on the canvas", () => {
    expect(relationEnds(link, at("Comment", "Note"))).toBeNull();
    expect(relationEnds({ id: "r", name: "R", ends: [{ id: "a", anyEntity: true }, link.ends[1]] } as unknown as RelationDoc, at("Part"))).toBeNull();
  });

  it("says +N more at each end, with every entry in the tooltip", () => {
    const fallback = bothEndsFallback(link, { onCanvas: (id) => id in names, nameOf: (id) => names[id] });
    expect(fallback?.entities).toEqual(["Comment", "Part"]);
    expect(fallback?.more[0]).toEqual({ text: "+2 more", names: "Comment, Note, approvable (stereotype)" });
    expect(fallback?.more[1]).toEqual({ text: "+1 more", names: "Part, Assembly" });
  });

  it("says nothing more for an end whose one entry is the card", () => {
    const single = {
      id: "r",
      name: "R",
      ends: [
        { id: "a", types: [{ stereotype: "x" }, { entity: "Comment" }] },
        { id: "b", anyEntity: true },
      ],
    } as unknown as RelationDoc;
    expect(bothEndsFallback(single, { onCanvas: () => true, nameOf: () => undefined })).toBeNull();
  });
});

describe("the readers that assume one end has one type", () => {
  const rows = [
    { id: "Comment", kind: "entity", package: "p" },
    { id: "Part", kind: "entity", package: "p" },
    {
      id: "link",
      kind: "relation",
      ends: [
        { role: "source", entities: ["Comment", "Note"] },
        { role: "target", entities: ["Part", "Assembly"] },
      ],
    },
  ] as never;

  it("shows the relation in a domain view and pairs the first entity of each end", () => {
    expect(viewElements({ type: "package", id: "p" } as never, rows, []).relationIds).toEqual(["link"]);
    expect(relationLookup(rows).pairOf("link")).toEqual(["Part", "Comment"]);
    expect(relationLookup(rows).relationsOf("Part")).toEqual(["link"]);
  });
});

describe("the relation form with several types at both ends", () => {
  const form = { ends: link.ends } as never;

  it("opens each end as Several types, and keeps the one-end limit for a new edit", () => {
    expect(endChoiceOf(link.ends[0] as never)).toBe("several");
    expect(endChoiceOf(link.ends[1] as never)).toBe("several");
    expect(endChoiceLimits(form, 0)).toEqual({ any: END_LIMIT_TEXTS.oneEnd });
    expect(endChoiceLimits(form, 1)).toEqual({ any: END_LIMIT_TEXTS.oneEnd });
  });
});
