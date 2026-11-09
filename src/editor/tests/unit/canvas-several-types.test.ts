// How a relation with an end of several types is drawn (several-types-design.md section 11, part 2): the look chosen per case, the
// lines and nodes of each, "+N not shown", the line text and markers, the toggle saving `collapsed`, positions, which relations show in a
// view and what "Add related" walks.
import { describe, expect, it } from "vitest";
import type { DiagramDoc, DiagramMember, RelationDoc } from "@/api/types";
import {
  boxNodeId,
  branchEdgeId,
  buildGraph,
  defaultHubPosition,
  lineMarkers,
  lineText,
  notShown,
  planSeveralTypes,
  relationOfFlowId,
  stereotypeCounter,
  toggleLabel,
  toggleMemberCollapsed,
  type PlanContext,
} from "@/canvas/severalTypes";
import { NODE_HEIGHT, applyPositions, expandRelated, planMembers, relationEnds, relationLookup, viewElements } from "@/canvas/model";
import { newDomainDiagram, syncDomainMembers } from "@/canvas/domainDiagram";

const names: Record<string, string> = { C: "Comment", P: "Part", A: "Assembly", T: "Tool", D: "Document" };
const nameOf = (id: string) => names[id];
const onCanvas =
  (...ids: string[]) =>
  (id: string) =>
    ids.includes(id);

const rel = (far: Record<string, unknown>, extra: Partial<RelationDoc> = {}): RelationDoc =>
  ({
    kind: "relation",
    id: "R",
    name: "AttachedTo",
    ends: [
      { id: "e1", entity: "C", role: "comment", max: "*" },
      { id: "e2", role: "attachedTo", max: 1, ...far },
    ],
    ...extra,
  }) as unknown as RelationDoc;

const ctx = (cards: string[], collapsed = false, more: Partial<PlanContext> = {}): PlanContext => ({
  onCanvas: onCanvas(...cards),
  nameOf,
  collapsed,
  ...more,
});
const ent = (...ids: string[]) => ({ types: ids.map((entity) => ({ entity })) });

describe("the look chosen for an end with several types", () => {
  it("draws nothing special for a relation whose ends each name one entity, or that has no card for its other end", () => {
    expect(planSeveralTypes(rel({ entity: "P" }), ctx(["C", "P"]))).toBeNull();
    expect(planSeveralTypes(rel(ent("P", "A")), ctx(["P", "A"]))).toBeNull();
  });

  it("one stereotype entry, or any entity: one line to a box, with no toggle", () => {
    const stereotype = planSeveralTypes(rel({ types: [{ stereotype: "approvable" }] }), ctx(["C"], false, { stereotypeCount: () => 3 }))!;
    expect(stereotype.look).toBe("box");
    expect(stereotype.entries).toMatchObject([{ kind: "stereotype", label: "approvable (stereotype)", count: 3 }]);
    expect(stereotype.canToggle).toBe(false);
    const any = planSeveralTypes(rel({ anyEntity: true }), ctx(["C"]))!;
    expect(any.look).toBe("box");
    expect(any.entries[0]).toMatchObject({ kind: "any", label: "Any entity" });
    expect(any.canToggle).toBe(false);
    // A box has no toggle even if the person's file says collapsed.
    expect(planSeveralTypes(rel({ anyEntity: true }), ctx(["C"], true))!.collapsed).toBe(false);
  });

  it("two entity entries, both cards on the canvas: bundled lines", () => {
    const plan = planSeveralTypes(rel(ent("P", "A")), ctx(["C", "P", "A"]))!;
    expect(plan.look).toBe("bundle");
    expect(plan.canToggle).toBe(true);
    expect(plan.hidden).toEqual([]);
  });

  it("three or more entries, or two where one is a stereotype or not on the canvas: a junction node", () => {
    expect(planSeveralTypes(rel(ent("P", "A", "D")), ctx(["C", "P", "A", "D"]))!.look).toBe("junction");
    expect(planSeveralTypes(rel({ types: [{ entity: "P" }, { stereotype: "approvable" }] }), ctx(["C", "P"]))!.look).toBe("junction");
    expect(planSeveralTypes(rel(ent("P", "A")), ctx(["C", "P"]))!.look).toBe("junction");
  });

  it("the toggle collapses any end with two or more entries to a list, whatever its look would be", () => {
    for (const far of [ent("P", "A"), ent("P", "A", "D"), { types: [{ entity: "P" }, { stereotype: "approvable" }] }]) {
      const open = planSeveralTypes(rel(far), ctx(["C", "P", "A", "D"]))!;
      const collapsed = planSeveralTypes(rel(far), ctx(["C", "P", "A", "D"], true))!;
      expect(open.canToggle && collapsed.canToggle).toBe(true);
      expect(open.collapsed).toBe(false);
      expect(collapsed.look).toBe("list");
      expect(collapsed.collapsed).toBe(true);
      // The list names every entry, drawn or not.
      expect(collapsed.entries.length).toBeGreaterThanOrEqual(2);
    }
  });

  it("uses the first end as the far end when it is the one with several types", () => {
    const flipped = {
      kind: "relation",
      id: "R",
      name: "Owns",
      ends: [
        { id: "e1", types: [{ entity: "P" }, { entity: "A" }, { entity: "D" }], role: "owner", max: 1 },
        { id: "e2", entity: "C", role: "comment", max: "*" },
      ],
    } as unknown as RelationDoc;
    const plan = planSeveralTypes(flipped, ctx(["C", "P"]))!;
    expect(plan).toMatchObject({ farIndex: 0, nearIndex: 1, nearEntity: "C", look: "junction" });
  });
});

describe("+N not shown", () => {
  it("counts the entries with no card, and names them for the tooltip", () => {
    const plan = planSeveralTypes(rel(ent("P", "A", "T")), ctx(["C", "P"]))!;
    expect(plan.look).toBe("junction");
    expect(plan.shown.map((e) => e.label)).toEqual(["Part"]);
    expect(notShown(plan)).toEqual({ text: "+2 not shown", names: "Assembly, Tool" });
  });

  it("says nothing when every entry is drawn, and the list look lists all of them without a count", () => {
    expect(notShown(planSeveralTypes(rel(ent("P", "A", "D")), ctx(["C", "P", "A", "D"]))!)).toBeNull();
    expect(notShown(planSeveralTypes(rel(ent("P", "A", "T")), ctx(["C", "P"], true))!)).toBeNull();
  });
});

describe("the nodes and lines of each look", () => {
  const cards = new Map([
    ["C", { x: 0, y: 100 }],
    ["P", { x: 600, y: 0 }],
    ["A", { x: 600, y: 200 }],
    ["D", { x: 600, y: 400 }],
  ]);

  it("bundled lines: one trunk with the label to a fork, one branch to each card", () => {
    const plan = planSeveralTypes(rel(ent("P", "A")), ctx(["C", "P", "A"]))!;
    const graph = buildGraph(plan, { positionOf: cards });
    expect(graph.nodes).toHaveLength(1);
    expect(graph.nodes[0]).toMatchObject({ id: "R", kind: "fork", draggable: false });
    expect(graph.edges.map((e) => [e.id, e.source, e.target, e.part, e.labelled])).toEqual([
      ["R", "C", "R", "trunk", true],
      [branchEdgeId("R", 0), "R", "P", "branch", false],
      [branchEdgeId("R", 1), "R", "A", "branch", false],
    ]);
    // The fork sits between the near card and the type cards.
    const fork = graph.nodes[0].position;
    expect(fork.x).toBeGreaterThan(0);
    expect(fork.x).toBeLessThan(600);
  });

  it("a junction: one line to the node, one from it to each card and a box for each stereotype entry", () => {
    const plan = planSeveralTypes(
      rel({ types: [{ entity: "P" }, { entity: "A" }, { stereotype: "approvable" }] }),
      ctx(["C", "P", "A"], false, { stereotypeCount: () => 3 }),
    )!;
    const graph = buildGraph(plan, { positionOf: cards });
    expect(graph.nodes.map((n) => [n.id, n.kind])).toEqual([
      ["R", "junction"],
      [boxNodeId("R", 2), "stereotype"],
    ]);
    expect(graph.nodes[1]).toMatchObject({ count: 3, draggable: false });
    expect(graph.edges.map((e) => [e.source, e.target])).toEqual([
      ["C", "R"],
      ["R", "P"],
      ["R", "A"],
      ["R", boxNodeId("R", 2)],
    ]);
    // No line for an entry with no card.
    const partial = buildGraph(planSeveralTypes(rel(ent("P", "A", "D")), ctx(["C", "P"]))!, { positionOf: cards });
    expect(partial.edges.map((e) => e.target)).toEqual(["R", "P"]);
  });

  it("a box and a list: the trunk only, to the node that carries the entries", () => {
    const box = buildGraph(planSeveralTypes(rel({ anyEntity: true }), ctx(["C"]))!, { positionOf: cards });
    expect(box.nodes).toMatchObject([{ id: "R", kind: "any", draggable: true }]);
    expect(box.edges).toHaveLength(1);
    const list = buildGraph(planSeveralTypes(rel(ent("P", "A", "D")), ctx(["C", "P", "A", "D"], true))!, { positionOf: cards });
    expect(list.nodes).toHaveLength(1);
    expect(list.nodes[0]).toMatchObject({ kind: "list", draggable: true });
    expect(list.nodes[0].entries?.map((e) => e.label)).toEqual(["Part", "Assembly", "Document"]);
    expect(list.edges).toHaveLength(1);
  });

  it("chooses handles by which side the other node is on", () => {
    const left = new Map([
      ["C", { x: 900, y: 100 }],
      ["P", { x: 0, y: 0 }],
      ["A", { x: 0, y: 200 }],
    ]);
    const graph = buildGraph(planSeveralTypes(rel(ent("P", "A")), ctx(["C", "P", "A"]))!, { positionOf: left });
    expect(graph.edges[0]).toMatchObject({ sourceHandle: "sl", targetHandle: "tr" });
    expect(graph.edges[1]).toMatchObject({ sourceHandle: "sl", targetHandle: "tr" });
  });
});

describe("positions", () => {
  const cards = new Map([
    ["C", { x: 0, y: 0 }],
    ["P", { x: 600, y: 0 }],
    ["A", { x: 600, y: 300 }],
    ["D", { x: 600, y: 600 }],
  ]);
  const plan = (far: Record<string, unknown>, collapsed = false) => planSeveralTypes(rel(far), ctx(["C", "P", "A", "D"], collapsed))!;

  it("a stored position places the node or box; none puts it between the cards, or beside the near card", () => {
    const junction = plan(ent("P", "A", "D"));
    expect(buildGraph(junction, { positionOf: cards, hub: { x: 123, y: 456 } }).nodes[0].position).toEqual({ x: 123, y: 456 });
    const middle = defaultHubPosition(junction, cards.get("C")!, [cards.get("P")!, cards.get("A")!, cards.get("D")!]);
    expect(middle.x).toBeGreaterThan(120);
    expect(middle.x).toBeLessThan(600);
    const list = defaultHubPosition(plan(ent("P", "A", "D"), true), cards.get("C")!, []);
    expect(list.x).toBeGreaterThan(240);
    const leftOf = defaultHubPosition(plan(ent("P", "A", "D"), true), { x: 900, y: 0 }, [{ x: 0, y: 0 }]);
    expect(leftOf.x).toBeLessThan(900);
  });

  it("a bundle's fork is never pinned: a stored position (from before the look became a bundle) is ignored", () => {
    const bundle = plan(ent("P", "A"));
    expect(bundle.look).toBe("bundle");
    const free = buildGraph(bundle, { positionOf: cards }).nodes[0].position;
    expect(buildGraph(bundle, { positionOf: cards, hub: { x: 5, y: 5 } }).nodes[0].position).toEqual(free);
    // A junction still takes it.
    expect(buildGraph(plan(ent("P", "A", "D")), { positionOf: cards, hub: { x: 5, y: 5 } }).nodes[0].position).toEqual({ x: 5, y: 5 });
  });

  it("the trunk runs straight by default: the node or fork sits at the near card's own height", () => {
    const near = cards.get("C")!;
    const centreY = near.y + NODE_HEIGHT / 2;
    for (const p of [plan(ent("P", "A")), plan(ent("P", "A", "D"))]) {
      const hub = buildGraph(p, { positionOf: cards }).nodes[0];
      expect(hub.position.y + hub.height / 2).toBe(centreY);
    }
  });

  it("a collapsed list or a box sits on the side of the cards it stands for", () => {
    const left = new Map([
      ["C", { x: 900, y: 0 }],
      ["P", { x: 0, y: 0 }],
      ["A", { x: 0, y: 300 }],
    ]);
    const list = planSeveralTypes(rel(ent("P", "A")), ctx(["C", "P", "A"], true))!;
    expect(list.look).toBe("list");
    expect(buildGraph(list, { positionOf: left }).nodes[0].position.x).toBeLessThan(900);
  });

  it("relations drawn from one card take a step lower each, so their nodes do not sit on one another", () => {
    const junction = plan(ent("P", "A", "D"));
    const first = buildGraph(junction, { positionOf: cards, slot: 0 }).nodes[0].position;
    const second = buildGraph(junction, { positionOf: cards, slot: 1 }).nodes[0].position;
    expect(second).toEqual({ x: first.x, y: first.y + 64 });
  });

  it("the node's position is the relation member's x and y, written through applyPositions, and a diagram without one never gets them", () => {
    const diagram = { members: [{ element: "C", x: 0, y: 0 }, { element: "R" }] } as Pick<DiagramDoc, "members">;
    expect(applyPositions(diagram, { R: { x: 10.4, y: 20.6 } })).toBe(1);
    expect(diagram.members![1]).toEqual({ element: "R", x: 10, y: 21 });
    const plain = { members: [{ element: "C", x: 0, y: 0 }, { element: "R" }] } as Pick<DiagramDoc, "members">;
    applyPositions(plain, { C: { x: 0, y: 0 } });
    expect(plain.members![1]).toEqual({ element: "R" });
  });

  it("a stored position survives a collapse: the toggle writes only collapsed", () => {
    const diagram = { members: [{ element: "R", x: 10, y: 20 }] } as Pick<DiagramDoc, "members">;
    expect(toggleMemberCollapsed(diagram, "R")).toBe(true);
    expect(diagram.members![0]).toEqual({ element: "R", x: 10, y: 20, collapsed: true });
    expect(toggleMemberCollapsed(diagram, "R")).toBe(false);
    expect(diagram.members![0]).toEqual({ element: "R", x: 10, y: 20 });
  });
});

describe("the toggle", () => {
  it("saves collapsed on the relation's member, leaves the flag out when expanded, and ignores a relation the diagram lacks", () => {
    const diagram = { members: [{ element: "C" }, { element: "R" }] } as Pick<DiagramDoc, "members">;
    expect(toggleMemberCollapsed(diagram, "R")).toBe(true);
    expect(diagram.members![1]).toEqual({ element: "R", collapsed: true });
    expect(toggleMemberCollapsed(diagram, "R")).toBe(false);
    expect(Object.keys(diagram.members![1])).toEqual(["element"]);
    expect(toggleMemberCollapsed(diagram, "nope")).toBeNull();
    expect(diagram.members![0]).toEqual({ element: "C" });
  });

  it("is worded by what pressing it does", () => {
    expect(toggleLabel(false)).toBe("Collapse lines");
    expect(toggleLabel(true)).toBe("Expand lines");
  });

  it("a domain's diagram, created on the first arrangement, takes the collapsed relations and the node positions", () => {
    const rows = [
      { id: "C", kind: "entity", package: "pkg" },
      { id: "P", kind: "entity", package: "pkg" },
      { id: "A", kind: "entity", package: "pkg" },
      {
        id: "R",
        kind: "relation",
        package: "pkg",
        ends: [
          { entity: "C", role: "comment" },
          { role: "attachedTo", entities: ["P", "A"] },
        ],
      },
    ];
    const doc = newDomainDiagram({
      id: "D1",
      domain: { id: "pkg", name: "Billing" },
      rows,
      positions: { C: { x: 0, y: 0 }, P: { x: 400, y: 0 }, A: { x: 400, y: 200 }, R: { x: 200.2, y: 100.7 } },
      collapsed: ["R"],
    });
    expect(doc.members).toEqual([
      { element: "C", x: 0, y: 0 },
      { element: "P", x: 400, y: 0 },
      { element: "A", x: 400, y: 200 },
      { element: "R", x: 200, y: 101, collapsed: true },
    ]);
    // Without any, the file is what it was before.
    const plain = newDomainDiagram({ id: "D2", domain: { id: "pkg", name: "Billing" }, rows, positions: {} });
    expect(plain.members!.every((m) => m.collapsed === undefined)).toBe(true);
    // The member follows the domain: it is kept with its flag and position when the domain syncs.
    expect(syncDomainMembers(doc.members as DiagramMember[], rows, "pkg")).toBeNull();
  });
});

describe("which relations show", () => {
  const rows = [
    { id: "C", kind: "entity", package: "pkg" },
    { id: "P", kind: "entity", package: "pkg" },
    { id: "T", kind: "entity", package: "other" },
    {
      id: "plain",
      kind: "relation",
      ends: [
        { entity: "C", role: "c" },
        { entity: "P", role: "p" },
      ],
    },
    {
      id: "plainOut",
      kind: "relation",
      ends: [
        { entity: "C", role: "c" },
        { entity: "T", role: "t" },
      ],
    },
    {
      id: "some",
      kind: "relation",
      ends: [
        { entity: "C", role: "c" },
        { role: "x", entities: ["P", "T"] },
      ],
    },
    {
      id: "none",
      kind: "relation",
      ends: [
        { entity: "C", role: "c" },
        { role: "x", entities: ["T"] },
      ],
    },
    {
      id: "stereo",
      kind: "relation",
      ends: [
        { entity: "C", role: "c" },
        { role: "x", stereotypes: ["s1"] },
      ],
    },
    {
      id: "any",
      kind: "relation",
      ends: [
        { entity: "T", role: "c" },
        { role: "x", anyEntity: true },
      ],
    },
    {
      id: "anyIn",
      kind: "relation",
      ends: [
        { entity: "C", role: "c" },
        { role: "x", anyEntity: true },
      ],
    },
  ];

  it("in a domain view: the other end's entity is a card, and the end with several types has a card entry, or a stereotype or any entity", () => {
    const { relationIds } = viewElements({ type: "package", id: "pkg" }, rows, []);
    expect(relationIds).toEqual(["plain", "some", "stereo", "anyIn"]);
  });

  it("a diagram shows the relations its members list, as before", () => {
    const { relationIds } = viewElements({ type: "diagram", id: "d" }, rows, [{ element: "C" }, { element: "some" }, { element: "any" }]);
    expect(relationIds).toEqual(["some", "any"]);
  });

  it("adding entities to a diagram brings a relation with several types once its other end and one entry (or a box) are there", () => {
    const lookup = relationLookup(rows as never);
    expect(lookup.endsOf("some")).toEqual(["C", "P", "T"]);
    expect(lookup.relationsOf("P")).toContain("some");
    const added = planMembers({ members: [{ element: "C", x: 0, y: 0 }], entities: ["P"], lookup });
    expect(added.map((m) => m.element)).toEqual(["P", "plain", "some"]);
  });

  it("pairs the near card with the first entity of the end with several types, whichever end comes first", () => {
    const withEnds = relationLookup([
      {
        id: "R1",
        kind: "relation",
        ends: [
          { entity: "C", role: "c" },
          { entities: ["P", "A"], role: "x" },
        ],
      },
      {
        id: "R2",
        kind: "relation",
        ends: [
          { entities: ["P", "A"], role: "x" },
          { entity: "C", role: "c" },
        ],
      },
      {
        id: "R3",
        kind: "relation",
        ends: [
          { entity: "C", role: "c" },
          { anyEntity: true, role: "x" },
        ],
      },
    ] as never);
    expect(withEnds.pairOf("R1")).toEqual(["C", "P"]);
    expect(withEnds.pairOf("R2")).toEqual(["C", "P"]);
    expect(withEnds.pairOf("R3")).toBeUndefined();
  });

  it("an addition from the Explorer adds the listed entities only, never the carriers of a stereotype entry", () => {
    const lookup = relationLookup([
      { id: "C", kind: "entity" },
      { id: "X", kind: "entity" },
      {
        id: "R",
        kind: "relation",
        ends: [
          { entity: "C", role: "c" },
          { stereotypes: ["S"], covers: ["X"], role: "x" },
        ],
      },
    ] as never);
    expect(lookup.endsOf("R")).toEqual(["C"]);
    expect(planMembers({ members: [], entities: ["C"], lookup }).map((m) => m.element)).toEqual(["C", "R"]);
  });

  it("Add related walks the entity entries of an end with several types", async () => {
    const relation = {
      id: "R",
      ends: [
        { id: "e1", entity: "C", role: "c" },
        { id: "e2", role: "x", types: [{ entity: "P" }, { entity: "A" }, { stereotype: "s" }] },
      ],
    };
    const added = await expandRelated({
      start: ["C"],
      depth: 1,
      present: new Set(["C"]),
      origin: { x: 0, y: 0 },
      relationsOf: async () => [relation as never],
    });
    expect(added.map((m) => m.element)).toEqual(["R", "P", "A"]);
  });
});

describe("what each line shows", () => {
  const r = rel(ent("P", "A"), { relationKind: "composition" } as Partial<RelationDoc>);
  const plain = rel(ent("P", "A"));

  it("shows the end's role and cardinality in UML, and only the role in crow's foot", () => {
    expect(lineText(plain, "uml", 0)).toBe("comment *");
    expect(lineText(plain, "uml", 1)).toBe("attachedTo 0..1");
    expect(lineText(plain, "crowsfoot", 1)).toBe("attachedTo");
  });

  it("marks the near card on the trunk and the type cards on the branches, with crow's foot or the whole end's diamond", () => {
    expect(lineMarkers(plain, "uml", { part: "trunk", endIndex: 0 })).toEqual({ start: undefined, end: undefined });
    expect(lineMarkers(plain, "crowsfoot", { part: "trunk", endIndex: 0 })).toEqual({ start: "url(#mq-cf-many)", end: undefined });
    expect(lineMarkers(plain, "crowsfoot", { part: "branch", endIndex: 1 })).toEqual({ start: undefined, end: "url(#mq-cf-zero-or-one)" });
    // The first end is the whole of a composition: its diamond sits at the near card (trunk) here.
    expect(lineMarkers(r, "uml", { part: "trunk", endIndex: 0 })).toEqual({ start: "url(#mq-diamond-filled)", end: undefined });
    expect(lineMarkers(r, "crowsfoot", { part: "branch", endIndex: 1 })).toEqual({ start: undefined, end: "url(#mq-cf-zero-or-one)" });
  });

  it("a line into a box or list shows the far end's role, cardinality and marker at the box", () => {
    const box = planSeveralTypes(rel({ anyEntity: true }), ctx(["C"]))!;
    const trunk = buildGraph(box, { positionOf: new Map([["C", { x: 0, y: 0 }]]) }).edges[0];
    expect(trunk.farEndIndex).toBe(1);
    expect(lineMarkers(plain, "crowsfoot", { part: "trunk", endIndex: 0, farEndIndex: 1 })).toEqual({
      start: "url(#mq-cf-many)",
      end: "url(#mq-cf-zero-or-one)",
    });
    const junction = planSeveralTypes(rel(ent("P", "A", "D")), ctx(["C", "P", "A", "D"]))!;
    const at = new Map([
      ["C", { x: 0, y: 0 }],
      ["P", { x: 500, y: 0 }],
      ["A", { x: 500, y: 200 }],
      ["D", { x: 500, y: 400 }],
    ]);
    expect(buildGraph(junction, { positionOf: at }).edges[0].farEndIndex).toBeUndefined();
  });

  it("selecting a line or box of a relation selects the relation", () => {
    expect(relationOfFlowId(branchEdgeId("R", 3))).toBe("R");
    expect(relationOfFlowId(boxNodeId("R", 1))).toBe("R");
    expect(relationOfFlowId("R")).toBe("R");
  });
});

describe("the count a stereotype box shows", () => {
  it("is the carriers of one stereotype entry; with several entries, each entry's own carriers among them", () => {
    const one = rel({ types: [{ stereotype: "approvable" }] });
    expect(stereotypeCounter(one, { ends: [{}, { covers: ["x", "y", "z"] }] }, () => undefined)("approvable")).toBe(3);
    const two = rel({ types: [{ stereotype: "approvable" }, { stereotype: "audited" }] });
    const own: Record<string, string[]> = { x: ["approvable"], y: ["audited"], z: ["approvable", "audited"] };
    const count = stereotypeCounter(two, { ends: [{}, { covers: ["x", "y", "z"] }] }, (id) => own[id]);
    expect([count("approvable"), count("audited")]).toEqual([2, 2]);
    expect(stereotypeCounter(one, undefined, () => undefined)("approvable")).toBeUndefined();
    // A known row with no carriers: the engine leaves an empty `covers` out, and the box says 0 entities.
    expect(stereotypeCounter(one, { ends: [{}, {}] }, () => undefined)("approvable")).toBe(0);
  });
});

describe("a relation that uses none of this", () => {
  it("is drawn by the one-line path alone", () => {
    const plain = rel({ entity: "P" });
    const positions = new Map([
      ["C", { x: 0, y: 0 }],
      ["P", { x: 400, y: 0 }],
    ]);
    expect(relationEnds(plain, positions)).toEqual({ source: "C", target: "P", sourceHandle: "r", targetHandle: "l" });
    expect(relationEnds(rel(ent("P", "A")), positions)).toBeNull();
  });
});
