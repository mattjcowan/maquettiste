// resolveRevisions on the drawing, the invoice and the family of revisions-design.md section 6, and the conversions behind the
// entity editor's Revisions tab (editors/revisionsEdit.ts).
import { describe, expect, it } from "vitest";
import type { ElementSummary } from "@/api/types";
import {
  addRevisions,
  heldInChoices,
  levelsView,
  levelToRow,
  moved,
  parseListValues,
  parseSkip,
  previewOf,
  rowToLevel,
  separatorChoices,
  sequenceOf,
  setOwnBoundAttribute,
  setOwnLevels,
  setOwnSeparator,
} from "@/editors/revisionsEdit";
import { governingLifecycles, resolveRevisions, type EntityWithRevisions } from "@/model/revisionSequence";

const row = (id: string, name: string, extra: Partial<ElementSummary> = {}) => ({ id, name, kind: "entity", ...extra }) as ElementSummary;
const index = (rows: ElementSummary[]) => new Map(rows.map((r) => [r.id, r]));

const DRAWING_LEVELS = [
  { name: "major", format: "letters" as const, skip: ["I", "O", "Q", "S", "X", "Z"] },
  { name: "minor", format: "numbers" as const, startEmpty: true },
];

describe("resolveRevisions", () => {
  it("is null for a family with no block", () => {
    const docs: Record<string, EntityWithRevisions> = { part: {}, bolt: { base: "part" } };
    const byId = index([row("part", "Part"), row("bolt", "Bolt", { base: "part" })]);
    expect(resolveRevisions("bolt", byId, (id) => docs[id])).toBeNull();
  });

  it("the drawing: its own levels, the default separator and the fallback restart for its lifecycle", () => {
    const docs: Record<string, EntityWithRevisions> = {
      drawing: {
        revisions: {
          levels: DRAWING_LEVELS,
          boundAttribute: "attr-revision",
          attributes: [{ attribute: "approvedBy", onRevise: "reset" }],
        },
      },
    };
    const merged = resolveRevisions("drawing", index([row("drawing", "Drawing")]), (id) => docs[id], ["lc-drawing"])!;
    expect(merged.levels).toEqual(DRAWING_LEVELS);
    expect(merged.levelsFrom).toBe("drawing");
    expect(merged.separator).toBe(".");
    expect(merged.separatorFrom).toBeNull();
    expect(merged.boundAttribute).toBe("attr-revision");
    expect(merged.lifecycles).toEqual([{ lifecycle: "lc-drawing", onRevise: "restart", from: null }]);
    expect(merged.attributes).toEqual([{ attribute: "approvedBy", onRevise: "reset", from: "drawing" }]);
    expect(merged.links).toEqual([]);
  });

  it("the invoice: entries kept, the unlisted lifecycle filled in with restart", () => {
    const docs: Record<string, EntityWithRevisions> = {
      invoice: {
        revisions: {
          levels: [{ format: "numbers" }],
          lifecycles: [{ lifecycle: "lc-delivery", onRevise: "keep" }],
          attributes: [{ attribute: "approvedBy", onRevise: "reset" }],
          links: [{ end: "documents", onRevise: "duplicate" }],
        },
      },
    };
    const merged = resolveRevisions("invoice", index([row("invoice", "Invoice")]), (id) => docs[id], ["lc-approval", "lc-delivery"])!;
    expect(merged.lifecycles).toEqual([
      { lifecycle: "lc-approval", onRevise: "restart", from: null },
      { lifecycle: "lc-delivery", onRevise: "keep", from: "invoice" },
    ]);
    expect(merged.links).toEqual([{ end: "documents", onRevise: "duplicate", from: "invoice" }]);
  });

  describe("the family: Part and PurchasedPart", () => {
    const byId = index([row("part", "Part"), row("purchased", "PurchasedPart", { base: "part" })]);
    const part: EntityWithRevisions = {
      revisions: {
        levels: [{ format: "letters" }, { format: "numbers" }],
        separator: "-",
        attributes: [{ attribute: "approvedBy", onRevise: "reset" }],
        lifecycles: [{ lifecycle: "lc", onRevise: "keep" }],
      },
    };

    it("a block without levels inherits the base's, and adds its own entries", () => {
      const docs: Record<string, EntityWithRevisions> = {
        part,
        purchased: { base: "part", revisions: { attributes: [{ attribute: "supplierQuote", onRevise: "clear" }] } },
      };
      const merged = resolveRevisions("purchased", byId, (id) => docs[id], ["lc"])!;
      expect(merged.levels).toEqual(part!.revisions!.levels);
      expect(merged.levelsFrom).toBe("part");
      expect(merged.separator).toBe("-");
      expect(merged.separatorFrom).toBe("part");
      expect(merged.attributes).toEqual([
        { attribute: "approvedBy", onRevise: "reset", from: "part" },
        { attribute: "supplierQuote", onRevise: "clear", from: "purchased" },
      ]);
      expect(merged.lifecycles).toEqual([{ lifecycle: "lc", onRevise: "keep", from: "part" }]);
    });

    it("the entity's own levels replace the base's as a whole, and its entries replace a base's", () => {
      const docs: Record<string, EntityWithRevisions> = {
        part,
        purchased: {
          base: "part",
          revisions: {
            levels: [{ format: "numbers" }],
            separator: ".",
            lifecycles: [{ lifecycle: "lc", onRevise: "restart" }],
            attributes: [{ attribute: "approvedBy", onRevise: "copy" }],
          },
        },
      };
      const merged = resolveRevisions("purchased", byId, (id) => docs[id], ["lc"])!;
      expect(merged.levels).toEqual([{ format: "numbers" }]);
      expect(merged.levelsFrom).toBe("purchased");
      expect(merged.separator).toBe(".");
      expect(merged.lifecycles).toEqual([{ lifecycle: "lc", onRevise: "restart", from: "purchased" }]);
      expect(merged.attributes).toEqual([]);
    });

    it("an entity with no block of its own still shows the base's", () => {
      const docs: Record<string, EntityWithRevisions> = { part, purchased: { base: "part" } };
      expect(resolveRevisions("purchased", byId, (id) => docs[id])!.levelsFrom).toBe("part");
    });
  });

  it("governingLifecycles finds a lifecycle that names the entity or a base, and no orchestration", () => {
    const rows = [
      row("part", "Part"),
      row("lc1", "A", { kind: "process", use: "lifecycle", subject: "part" }),
      row("lc2", "B", { kind: "process", use: "lifecycle", subjects: ["other"] }),
      row("lc3", "C", { kind: "process", use: "orchestration", subject: "part" }),
      row("lc4", "D", { kind: "process", use: "lifecycle", covers: ["purchased"] }),
    ];
    expect(governingLifecycles(rows, ["purchased", "part"])).toEqual(["lc1", "lc4"]);
  });
});

describe("the Revisions tab's conversions", () => {
  it("shows levels from a base as 'from Base' and the entity's own as its own", () => {
    const merged = resolveRevisions("purchased", index([row("part", "Part"), row("purchased", "PurchasedPart", { base: "part" })]), (id) =>
      id === "part" ? { revisions: { levels: DRAWING_LEVELS } } : { base: "part" },
    );
    const view = levelsView(merged, "purchased");
    expect(view.inheritedFrom).toBe("part");
    expect(view.rows.map((r) => r.name)).toEqual(["major", "minor"]);
    const own = resolveRevisions("part", index([row("part", "Part")]), () => ({ revisions: { levels: DRAWING_LEVELS } }));
    expect(levelsView(own, "part").inheritedFrom).toBeNull();
  });

  it("changing an inherited level writes the entity's own levels, the whole list", () => {
    const json: Record<string, unknown> = { base: "part" };
    const rows = DRAWING_LEVELS.map(levelToRow);
    rows[1] = { ...rows[1], first: "0" };
    setOwnLevels(
      json,
      rows.map(rowToLevel),
    );
    expect(json.revisions).toEqual({
      levels: [
        { name: "major", format: "letters", skip: ["I", "O", "Q", "S", "X", "Z"] },
        { name: "minor", format: "numbers", first: 0, startEmpty: true },
      ],
    });
  });

  it("the empty tab: Add revisions writes one letters level, and removing everything drops the block", () => {
    const json: Record<string, unknown> = {};
    addRevisions(json);
    expect(json.revisions).toEqual({ levels: [{ format: "letters" }] });
    setOwnLevels(json, []);
    expect(json.revisions).toBeUndefined();
  });

  it("keeps the block when other members remain, and writes the separator and held attribute", () => {
    const json: Record<string, unknown> = { revisions: { levels: [{ format: "letters" }] } };
    setOwnSeparator(json, "");
    setOwnBoundAttribute(json, "attr");
    expect(json.revisions).toEqual({ levels: [{ format: "letters" }], separator: "", boundAttribute: "attr" });
    setOwnLevels(json, []);
    expect(json.revisions).toEqual({ separator: "", boundAttribute: "attr" });
    setOwnSeparator(json, undefined);
    setOwnBoundAttribute(json, undefined);
    expect(json.revisions).toBeUndefined();
  });

  it("converts rows both ways, dropping fields that do not belong to the kind", () => {
    expect(rowToLevel({ name: "", kind: "numbers", first: "0", skip: "I", width: "2", startEmpty: true, values: "x" })).toEqual({
      format: "numbers",
      first: 0,
      width: 2,
      startEmpty: true,
    });
    expect(rowToLevel({ name: "stage", kind: "list", first: "A", skip: "", width: "", startEmpty: false, values: "Prototype\nPilot run, phase 1\n\nProduction" })).toEqual({
      name: "stage",
      values: ["Prototype", "Pilot run, phase 1", "Production"],
    });
    expect(levelToRow({ format: "letters", skip: ["I", "O"], first: "C" })).toMatchObject({ kind: "letters", first: "C", skip: "I, O" });
    expect(parseSkip("i, o, I")).toEqual(["I", "O"]);
    expect(parseListValues("a, b\nb\n a ")).toEqual(["a, b", "b", "a"]);
  });

  it("moves levels up and down, and not past the ends", () => {
    expect(moved([1, 2, 3], 1, -1)).toEqual([2, 1, 3]);
    expect(moved([1, 2, 3], 2, 1)).toEqual([1, 2, 3]);
  });

  it("offers in 'Held in' only attributes that fit decision 11", () => {
    const attrs = [
      { id: "a", name: "code", type: "string" },
      { id: "b", name: "notes", type: "text" },
      { id: "c", name: "count", type: "int32" },
      { id: "d", name: "tags", type: "string", collection: true },
      { id: "e", name: "label", type: "string", derived: { expression: "x" } },
      { id: "f", name: "tag", type: { ref: "scalar-1" } },
      { id: "g", name: "other", type: { ref: "scalar-2" } },
      { id: "h", name: "state", type: "string" },
      { id: "i", name: "plain", type: { ref: "enum-1" } },
    ];
    const base = (id: string) => ({ "scalar-1": "string", "scalar-2": "int32" })[id];
    expect(heldInChoices(attrs, base, new Set(["h"])).map((a) => a.id)).toEqual(["a", "b", "f"]);
  });

  it("previews the first revision and a run per level", () => {
    const drawing = previewOf(sequenceOf(DRAWING_LEVELS, "."));
    expect(drawing.first).toBe("A");
    expect(drawing.runs.map((r) => r.text)).toEqual(["A, B, C, D, E …", "A.1, A.2, A.3 …"]);
    const stages = previewOf(sequenceOf([{ values: ["Prototype", "Pilot run", "Production"] }], "."));
    expect(stages.runs[0].text).toBe("Prototype, Pilot run, Production");
    expect(previewOf(sequenceOf([], ".")).ok).toBe(false);
    expect(previewOf(sequenceOf([{ format: "letters", startEmpty: true }], ".")).first).toBe("");
    expect(separatorChoices("~").at(-1)).toEqual({ value: "~", label: '"~"' });
  });
});
