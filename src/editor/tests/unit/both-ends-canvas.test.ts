// Several types or any entity at both ends of a relation (several-types-design.md 19.12): the both-ends plan (one junction node, or the
// collapsed box), the lines and boxes, the toggle, which relations a view shows, the form's lifted limit, and the readers made to
// work through both ends.
import { describe, expect, it } from "vitest";
import type { DiagramDoc, RelationDoc } from "@/api/types";
import { relationEnds, relationLookup, viewElements } from "@/canvas/model";
import {
  BOTH_ENDS_STRIDE,
  boxNodeId,
  bothEndsTitle,
  branchEdgeId,
  buildBothEndsGraph,
  hasSeveralTypesBothEnds,
  notShownByEnd,
  planBothEnds,
  planSeveralTypes,
  stereotypeCounter,
  toggleMemberCollapsed,
  type BothEndsContext,
} from "@/canvas/severalTypes";
import { endChoiceLimits, endChoiceOf } from "@/inspector/relationEnds";
import { commonFields, objectFields, pathProblem, type FieldSources } from "@/model/objectFields";

const names: Record<string, string> = { C: "Comment", N: "Note", P: "Part", A: "Assembly" };
const link = {
  id: "link",
  name: "Link",
  ends: [
    { id: "from", types: [{ entity: "C" }, { entity: "N" }], role: "source", max: "*" },
    { id: "to", types: [{ entity: "P" }, { entity: "A" }], role: "target", max: 1 },
  ],
} as unknown as RelationDoc;

const ctx = (on: string[], collapsed = false): BothEndsContext => ({ onCanvas: (id) => on.includes(id), nameOf: (id) => names[id], collapsed });
const at = (...ids: string[]) => new Map(ids.map((id, i) => [id, { x: i < 2 ? 0 : 600, y: (i % 2) * 200 }]));

describe("the both-ends plan", () => {
  it("is for a relation with several types at both ends only", () => {
    expect(hasSeveralTypesBothEnds(link)).toBe(true);
    const one = { id: "r", name: "R", ends: [{ id: "a", entity: "C", max: "*" }, link.ends[1]] } as unknown as RelationDoc;
    expect(planBothEnds(one, ctx(["C", "P"]))).toBeNull();
    // A relation with one end of several types keeps part 1's plan.
    expect(planSeveralTypes(one, { onCanvas: (id) => ["C", "P", "A"].includes(id), nameOf: (id) => names[id], collapsed: false })).not.toBeNull();
    expect(planSeveralTypes(link, { onCanvas: () => true, nameOf: (id) => names[id], collapsed: false })).toBeNull();
  });

  it("expanded is one junction node with a line to each type of either end", () => {
    const plan = planBothEnds(link, ctx(["C", "N", "P", "A"]))!;
    expect(plan.look).toBe("junction");
    expect(plan.canToggle).toBe(true);
    const graph = buildBothEndsGraph(plan, { positionOf: at("C", "N", "P", "A") });
    expect(graph.nodes.map((n) => [n.id, n.kind])).toEqual([["link", "junction"]]);
    expect(graph.edges.map((e) => [e.id, e.source, e.target, e.endIndex, e.part, e.labelled])).toEqual([
      [branchEdgeId("link", 0), "link", "C", 0, "branch", false],
      [branchEdgeId("link", 1), "link", "N", 0, "branch", false],
      [branchEdgeId("link", BOTH_ENDS_STRIDE), "link", "P", 1, "branch", false],
      [branchEdgeId("link", BOTH_ENDS_STRIDE + 1), "link", "A", 1, "branch", false],
    ]);
    // The node sits at the middle of the cards it joins, and a stored position wins.
    expect(graph.nodes[0].position.x).toBeGreaterThan(0);
    expect(buildBothEndsGraph(plan, { positionOf: at("C", "N", "P", "A"), hub: { x: 5, y: 7 } }).nodes[0].position).toEqual({ x: 5, y: 7 });
  });

  it("puts a stereotype or any-entity box on its end's side of the node", () => {
    const relation = {
      id: "r",
      name: "R",
      ends: [
        { id: "a", types: [{ entity: "C" }, { stereotype: "approvable" }], role: "source", max: "*" },
        { id: "b", types: [{ entity: "P" }, { stereotype: "frozen" }], role: "target", max: 1 },
      ],
    } as unknown as RelationDoc;
    const plan = planBothEnds(relation, { ...ctx(["C", "P"]), stereotypeCount: (end, key) => (end === 0 ? 3 : 5) + key.length * 0 })!;
    const graph = buildBothEndsGraph(plan, { positionOf: at("C", "P") });
    const hub = graph.nodes[0];
    const left = graph.nodes.find((n) => n.id === boxNodeId("r", 1))!;
    const right = graph.nodes.find((n) => n.id === boxNodeId("r", BOTH_ENDS_STRIDE + 1))!;
    expect(left.position.x + left.width).toBeLessThan(hub.position.x);
    expect(right.position.x).toBeGreaterThan(hub.position.x + hub.width);
    expect([left.count, right.count]).toEqual([3, 5]);
    expect(graph.edges.find((e) => e.target === left.id)).toMatchObject({ endIndex: 0 });
    expect(graph.edges.find((e) => e.target === right.id)).toMatchObject({ endIndex: 1 });
    // Any entity at an end is a box too, with no card.
    const any = {
      id: "q",
      name: "Q",
      ends: [
        { id: "a", anyEntity: true, max: "*" },
        { id: "b", anyEntity: true, max: "*" },
      ],
    } as unknown as RelationDoc;
    const anyPlan = planBothEnds(any, ctx([]))!;
    expect(anyPlan.canToggle).toBe(false);
    expect(anyPlan.look).toBe("junction");
    expect(buildBothEndsGraph(anyPlan, { positionOf: new Map() }).nodes.map((n) => n.kind)).toEqual(["junction", "any", "any"]);
  });

  it("says +N not shown per end for types not on the canvas", () => {
    const plan = planBothEnds(link, ctx(["C", "P"]))!;
    const [first, second] = notShownByEnd(plan);
    expect(first).toEqual({ text: "+1 not shown", names: "Note" });
    expect(second).toEqual({ text: "+1 not shown", names: "Assembly" });
    expect(buildBothEndsGraph(plan, { positionOf: at("C", "P") }).edges.map((e) => e.target)).toEqual(["C", "P"]);
    // With the relation, each label names its end, so the two on one node read differently.
    expect(notShownByEnd(plan, link)).toEqual([
      { text: "source: +1 not shown", names: "Note" },
      { text: "target: +1 not shown", names: "Assembly" },
    ]);
    expect(notShownByEnd(planBothEnds(link, ctx(["C", "N", "P", "A"]))!)).toEqual([null, null]);
    expect(bothEndsTitle(link, plan)).toBe("source: Comment, Note\ntarget: Part, Assembly");
  });

  it("draws nothing when an end has no card and no box on the canvas", () => {
    expect(planBothEnds(link, ctx(["C", "N"]))).toBeNull();
    expect(planBothEnds(link, ctx([]))).toBeNull();
  });

  it("collapsed is one box that lists each end's entries under its role, with no lines", () => {
    const plan = planBothEnds(link, ctx(["C", "N", "P", "A"], true))!;
    expect(plan).toMatchObject({ look: "list", collapsed: true });
    const graph = buildBothEndsGraph(plan, { positionOf: at("C", "N", "P", "A"), endText: ["source *", "target 1"] });
    expect(graph.edges).toEqual([]);
    expect(graph.nodes).toHaveLength(1);
    expect(graph.nodes[0].kind).toBe("list");
    expect(graph.nodes[0].groups?.map((g) => [g.text, g.entries.map((e) => e.label)])).toEqual([
      ["source *", ["Comment", "Note"]],
      ["target 1", ["Part", "Assembly"]],
    ]);
  });

  it("offers the toggle when either end has two or more entries, and saves it as the member's collapsed", () => {
    const oneEach = {
      id: "r",
      name: "R",
      ends: [
        { id: "a", types: [{ entity: "C" }, { stereotype: "s" }].slice(0, 1).concat([{ stereotype: "s" }]), max: "*" },
        { id: "b", anyEntity: true, max: 1 },
      ],
    } as unknown as RelationDoc;
    expect(planBothEnds(oneEach, ctx(["C"]))!.canToggle).toBe(true);
    // Collapsed is ignored when no end has two entries.
    const none = {
      id: "q",
      name: "Q",
      ends: [
        { id: "a", anyEntity: true },
        { id: "b", anyEntity: true },
      ],
    } as unknown as RelationDoc;
    expect(planBothEnds(none, ctx([], true))).toMatchObject({ canToggle: false, collapsed: false, look: "junction" });
    const diagram = { members: [{ element: "link", x: 3, y: 4 }] } as unknown as Pick<DiagramDoc, "members">;
    expect(toggleMemberCollapsed(diagram, "link")).toBe(true);
    expect(diagram.members![0]).toEqual({ element: "link", x: 3, y: 4, collapsed: true });
    expect(toggleMemberCollapsed(diagram, "link")).toBe(false);
    // Nothing new in the diagram file: the flag is left out when false.
    expect(diagram.members![0]).toEqual({ element: "link", x: 3, y: 4 });
  });

  it("counts a stereotype's entities for the end asked", () => {
    const relation = {
      id: "r",
      ends: [
        { id: "a", types: [{ stereotype: "s" }, { entity: "C" }] },
        { id: "b", types: [{ stereotype: "t" }, { entity: "P" }] },
      ],
    } as unknown as RelationDoc;
    const row = { ends: [{ covers: ["x", "y"] }, { covers: ["z"] }] };
    expect(stereotypeCounter(relation, row, () => undefined, 0)("s")).toBe(2);
    expect(stereotypeCounter(relation, row, () => undefined, 1)("t")).toBe(1);
    // Without an end, the first end with several types, as in part 1.
    expect(stereotypeCounter(relation, row, () => undefined)("s")).toBe(2);
  });
});

describe("which relations show, and the readers that assumed one type", () => {
  const rows = [
    { id: "C", kind: "entity", package: "p" },
    { id: "P", kind: "entity", package: "p" },
    {
      id: "link",
      kind: "relation",
      ends: [
        { role: "source", entities: ["C", "N"] },
        { role: "target", entities: ["P", "A"] },
      ],
    },
    {
      id: "box",
      kind: "relation",
      ends: [
        { role: "a", anyEntity: true },
        { role: "b", stereotypes: ["s"] },
      ],
    },
    {
      id: "far",
      kind: "relation",
      ends: [
        { role: "a", entities: ["N", "X"] },
        { role: "b", entities: ["P", "A"] },
      ],
    },
  ] as never;

  it("shows a relation whose each end has a card or names a stereotype or any entity", () => {
    expect(viewElements({ type: "package", id: "p" } as never, rows, []).relationIds).toEqual(["link", "box"]);
  });

  it("draws no ordinary line for it", () => {
    expect(
      relationEnds(
        link,
        new Map([
          ["C", { x: 0, y: 0 }],
          ["P", { x: 400, y: 0 }],
        ]),
      ),
    ).toBeNull();
  });

  it("pairs the first listed entity of each end in end order, and finds the relation from either end's entities", () => {
    const lookup = relationLookup(rows);
    expect(lookup.pairOf("link")).toEqual(["C", "P"]);
    expect(lookup.pairOf("box")).toBeUndefined();
    expect(lookup.relationsOf("A")).toContain("link");
    expect(lookup.relationsOf("N")).toContain("link");
    expect(lookup.shows("link", new Set(["C", "A"]))).toBe(true);
    expect(lookup.shows("link", new Set(["C", "N"]))).toBe(false);
  });
});

describe("the form with several types at both ends", () => {
  const form = { ends: link.ends } as never;

  it("opens each end as Several types and offers every choice on both", () => {
    expect(endChoiceOf(link.ends[0] as never)).toBe("several");
    expect(endChoiceOf(link.ends[1] as never)).toBe("several");
    expect(endChoiceLimits(form, 0)).toEqual({});
    expect(endChoiceLimits(form, 1)).toEqual({});
  });

  it("keeps the n-ary and composition limits", () => {
    const nary = { relationKind: "n-ary", ends: [{ entity: "C" }, link.ends[1], link.ends[0]] } as never;
    expect(endChoiceLimits(nary, 0).several).toContain("n-ary");
    // A composition may have several types at its owner end and its part end; the part end still leaves out Any entity.
    const composition = {
      relationKind: "composition",
      ends: [
        { types: [{ entity: "C" }, { entity: "N" }], max: 1 },
        { types: [{ entity: "P" }, { entity: "A" }], max: "*" },
      ],
    } as never;
    expect(endChoiceLimits(composition, 0)).toEqual({});
    expect(Object.keys(endChoiceLimits(composition, 1))).toEqual(["any"]);
    expect(endChoiceLimits(composition, 1).any).toContain("composition");
  });
});

describe("guard, actor and owner paths through several types at both ends", () => {
  type Json = Record<string, unknown>;
  const docs: Record<string, Json> = {
    C: { kind: "entity", id: "C", name: "Comment", attributes: [{ id: "cw", name: "weight" }] },
    N: { kind: "entity", id: "N", name: "Note", attributes: [{ id: "nw", name: "weight" }] },
    P: { kind: "entity", id: "P", name: "Part", attributes: [{ id: "code", name: "code" }] },
    A: { kind: "entity", id: "A", name: "Assembly", attributes: [{ id: "code", name: "code" }] },
    link: link as unknown as Json,
  };
  const withNav = {
    ...link,
    ends: [
      { ...link.ends[0], navigation: "sources", max: 1 },
      { ...link.ends[1], navigation: "targets", max: 1 },
    ],
  } as unknown as Json;
  docs.link = withNav;
  const sources: FieldSources = {
    doc: (id) => docs[id],
    relations: [withNav],
    stereotype: () => undefined,
    kindOf: (id) => docs[id]?.kind as string | undefined,
  };

  it("shows the far end on every type the near end covers, from either side", () => {
    // Comment and Note are covered by the first end, so they hold the second end (leading to Part and Assembly); Part and Assembly hold the first.
    for (const holder of ["C", "N"])
      expect(objectFields(holder, sources).find((f) => f.id === "to")).toMatchObject({ kind: "end", name: "targets", targets: ["P", "A"], many: false });
    for (const holder of ["P", "A"])
      expect(objectFields(holder, sources).find((f) => f.id === "from")).toMatchObject({ kind: "end", name: "sources", targets: ["C", "N"] });
    expect(objectFields("P", sources).find((f) => f.id === "to")).toBeUndefined();
  });

  it("goes on through what every covered type holds under one id", () => {
    const fieldsOf = (e: string) => objectFields(e, sources);
    expect(pathProblem("C", ["to", "code"], fieldsOf, "field")).toBeNull();
    expect(pathProblem("C", ["to", "weight"], fieldsOf, "field")).toContain("no attribute or end");
    expect(commonFields([fieldsOf("P"), fieldsOf("A")]).map((f) => f.id)).toContain("code");
  });

  it("an entity covered by both ends holds both ends", () => {
    const both = {
      ...withNav,
      ends: [
        { id: "from", types: [{ entity: "C" }, { entity: "P" }], role: "source", navigation: "sources", max: 1 },
        { id: "to", types: [{ entity: "P" }, { entity: "A" }], role: "target", navigation: "targets", max: 1 },
      ],
    } as unknown as Json;
    const fields = objectFields("P", { ...sources, relations: [both] });
    expect(fields.filter((f) => f.kind === "end").map((f) => f.id)).toEqual(["to", "from"]);
  });
});

describe("auto-layout of several both-ends relations", () => {
  // Three relations over six cards: each node sits between its own cards, the first end's cards left of it and the second end's right.
  const relation = (id: string, a: string[], b: string[]) =>
    ({
      id,
      name: id,
      ends: [
        { id: `${id}-a`, types: a.map((entity) => ({ entity })), role: "source", max: "*" },
        { id: `${id}-b`, types: b.map((entity) => ({ entity })), role: "target", max: "*" },
      ],
    }) as unknown as RelationDoc;
  const relations = [relation("R1", ["c1", "c2"], ["c3", "c7"]), relation("R2", ["c4", "c8"], ["c5", "c6"]), relation("R3", ["c3", "c9"], ["c4", "c10"])];

  it("reverses the first end's lines so the layout reads from first end to node to second end", async () => {
    const { layoutInput } = await import("@/canvas/model");
    const cards = Array.from({ length: 10 }, (_, i) => ({ id: `c${i + 1}` }));
    const flowNodes = [...cards, ...relations.map((r) => ({ id: r.id }))];
    const flowEdges = relations.flatMap((r) =>
      [0, 1].flatMap((end) =>
        r.ends![end].types!.map((t, i) => ({
          id: `${r.id}-${end}-${i}`,
          source: r.id,
          target: t.entity!,
          data: { relation: r, part: "branch", endIndex: end, labelled: false },
        })),
      ),
    );
    const input = layoutInput(flowNodes, flowEdges);
    const first = input.edges.find((e) => e.id === "R1-0-0")!;
    const second = input.edges.find((e) => e.id === "R1-1-0")!;
    expect([first.source, first.target]).toEqual(["c1", "R1"]);
    expect([second.source, second.target]).toEqual(["R1", "c3"]);

    const { default: ELK } = await import("elkjs/lib/elk.bundled.js");
    const { elkEngine } = await import("@/canvas/layout");
    const positions = await elkEngine(async () => new ELK()).layout(input.nodes, input.edges);
    for (const r of relations) {
      const hub = positions.get(r.id)!;
      for (const t of r.ends![0].types!) expect(positions.get(t.entity!)!.x).toBeLessThan(hub.x);
      for (const t of r.ends![1].types!) expect(positions.get(t.entity!)!.x).toBeGreaterThan(hub.x);
    }
    // The cards no longer share one column.
    expect(new Set([...positions.values()].map((p) => p.x)).size).toBeGreaterThan(2);
  });

  it("leaves a one-end relation's lines as they were", async () => {
    const { layoutInput } = await import("@/canvas/model");
    const one = { id: "R", name: "R", ends: [{ id: "a", entity: "c1", max: "*" }, link.ends![1]] } as unknown as RelationDoc;
    const input = layoutInput([{ id: "R" }, { id: "P" }], [{ id: "e", source: "R", target: "P", data: { relation: one, part: "branch", endIndex: 0 } }]);
    expect([input.edges[0].source, input.edges[0].target]).toEqual(["R", "P"]);
  });
});
