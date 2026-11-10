// Part two of the Revisions editor: the level picker's labels, the When revised conversions, the scenario fields, the quick fixes, the
// mock's rules, and the references and deletes of the block (revisions-design.md sections 7, 10 and 12).
import { describe, expect, it } from "vitest";
import type { ElementSummary } from "@/api/types";
import { planDelete, type CascadeDoc } from "@/mocks/model/cascade";
import { referencesOf } from "@/mocks/model/refs";
import { revisionDiagnostics } from "@/mocks/model/revisionRules";
import type { ModelEntry } from "@/mocks/model/validate";
import { eventTakesRevision, setStepRevision, stepRevisionLevel, stepRevisionName, stepTakesLevel, stepTakesName } from "@/editors/scenario/revisionFields";
import {
  attributeChoices,
  attributeRows,
  hasOwnEntry,
  lifecycleRuleText,
  lifecycleRows,
  linkRows,
  reachedEnds,
  removeOwnEntry,
  setOwnEntry,
} from "@/editors/whenRevised";
import { levelChoices, levelLabels, nextRevisionFieldLabel, transitionRevisionText } from "@/model/revisionLabels";
import { governingLifecycles, resolveRevisions, type EntityWithRevisions } from "@/model/revisionSequence";
import { revisionFix } from "@/problems/quickFix";
import type { ModelJson } from "@/api/types";

type Json = Record<string, unknown>;

const LEVELS = [
  { name: "major", format: "letters" as const },
  { name: "minor", format: "numbers" as const, startEmpty: true },
];

describe("the level picker's labels", () => {
  it("labels the levels by name, and the move by its verb", () => {
    const labels = levelLabels([LEVELS]);
    expect(labels.map((l) => l.label)).toEqual(["Level 1 (major)", "Level 2 (minor)"]);
    expect(levelLabels([[{ format: "letters" }]]).map((l) => l.label)).toEqual(["Level 1"]);
    // The most levels of any governed type, with the first name found.
    expect(levelLabels([[{ format: "letters" }], LEVELS]).map((l) => l.label)).toEqual(["Level 1 (major)", "Level 2 (minor)"]);
    expect(levelLabels([])).toEqual([]);
    expect(nextRevisionFieldLabel("revise")).toBe("Revise at");
    expect(nextRevisionFieldLabel("promote")).toBe("Next revision");
    expect(nextRevisionFieldLabel(undefined)).toBe("Next revision");
  });

  it("offers None and each level, and keeps a level no type has so it can be seen", () => {
    const labels = levelLabels([LEVELS]);
    expect(levelChoices(labels, undefined).map((c) => c.label)).toEqual(["None", "Level 1 (major)", "Level 2 (minor)"]);
    expect(levelChoices(labels, 3).at(-1)).toEqual({ value: "3", label: "Level 3 (no governed type has it)" });
  });

  it("says a move's revision on the canvas and in the grid", () => {
    const labels = levelLabels([LEVELS]);
    expect(transitionRevisionText(1, "promote", labels)).toBe("next major");
    expect(transitionRevisionText(2, "revise", labels)).toBe("revise: minor");
    expect(transitionRevisionText(3, undefined, labels)).toBe("next level 3");
    expect(transitionRevisionText(undefined, "revise", labels)).toBe("");
  });
});

describe("the lifecycles that govern an entity", () => {
  const row = (id: string, extra: Partial<ElementSummary>) => ({ id, name: id, kind: "entity", stereotypes: [], ...extra }) as ElementSummary;
  it("count a lifecycle with a stereotype entry for a stereotype the entity or a base carries", () => {
    const rows = [
      row("part", { stereotypes: ["approvable"] }),
      row("bolt", { base: "part" }),
      row("approvable", { kind: "stereotype", name: "approvable" }),
      row("lc", { kind: "process", name: "Approval", use: "lifecycle", subjectStereotypes: ["approvable"] }),
      row("other", { kind: "process", name: "Other", use: "lifecycle", subject: "elsewhere" }),
    ];
    expect(governingLifecycles(rows, ["bolt", "part"])).toEqual(["lc"]);
    expect(governingLifecycles(rows, ["elsewhere"])).toEqual(["other"]);
    expect(governingLifecycles(rows, ["nothing"])).toEqual([]);
  });
});

describe("the When revised conversions", () => {
  const docs: Record<string, EntityWithRevisions> = {
    part: { revisions: { levels: LEVELS, attributes: [{ attribute: "a1", onRevise: "reset" }], lifecycles: [{ lifecycle: "lc", onRevise: "keep" }] } },
    bolt: { base: "part" },
  };
  const merged = () => resolveRevisions("bolt", new Map(), (id) => docs[id], ["lc", "lc2"])!;

  it("lists a row per governing lifecycle, inherited rows from the base", () => {
    expect(lifecycleRows(merged())).toEqual([
      { id: "lc", rule: "keep", from: "part" },
      { id: "lc2", rule: "restart", from: null },
    ]);
    expect(attributeRows(merged(), undefined, "bolt")).toEqual([{ id: "a1", rule: "reset", from: "part" }]);
    expect(linkRows(merged())).toEqual([]);
  });

  it("replaces an inherited row by writing the entity's own entry, and removing it lets the base apply again", () => {
    const json: Json = { base: "part" };
    setOwnEntry(json, "attributes", "a1", "clear");
    setOwnEntry(json, "attributes", "a1", "copy");
    expect(json.revisions).toEqual({ attributes: [{ attribute: "a1", onRevise: "copy" }] });
    docs.bolt = json as EntityWithRevisions;
    // Copied undoes the base's entry, and still shows as a row.
    expect(attributeRows(merged(), json.revisions as never, "bolt")).toEqual([{ id: "a1", rule: "copy", from: "bolt" }]);
    expect(hasOwnEntry(json.revisions as never, "attributes", "a1")).toBe(true);
    removeOwnEntry(json, "attributes", "a1");
    expect(json.revisions).toBeUndefined();
    docs.bolt = { base: "part" };
    expect(attributeRows(merged(), undefined, "bolt")).toEqual([{ id: "a1", rule: "reset", from: "part" }]);
  });

  it("writes a lifecycle rule and a link rule, one entry per item", () => {
    const json: Json = { revisions: { levels: LEVELS } };
    setOwnEntry(json, "lifecycles", "lc", "restart");
    setOwnEntry(json, "lifecycles", "lc", "state-id");
    setOwnEntry(json, "links", "end1", "duplicate");
    expect(json.revisions).toEqual({
      levels: LEVELS,
      lifecycles: [{ lifecycle: "lc", onRevise: "state-id" }],
      links: [{ end: "end1", onRevise: "duplicate" }],
    });
    removeOwnEntry(json, "lifecycles", "lc");
    expect((json.revisions as Json).lifecycles).toBeUndefined();
  });

  it("offers the attributes that can have an entry, and the ends the type reaches", () => {
    const attributes = [
      { id: "a1", name: "approvedBy" },
      { id: "a2", name: "key" },
      { id: "a3", name: "derived", derived: { expression: "x" } },
      { id: "a4", name: "state" },
      { id: "a5", name: "revision" },
      { id: "a6", name: "free" },
    ];
    const choices = attributeChoices(attributes, { key: ["a2"], lifecycleBound: new Set(["a4"]), held: "a5", listed: ["a1"] });
    expect(choices.map((a) => a.id)).toEqual(["a6"]);
    expect(
      reachedEnds([
        { name: "Docs", ends: [{ id: "e1", role: "owner" }, { id: "e2", role: "documents" }], covers: [true, false] },
        { name: "Three", ends: [{ id: "x", role: "a" }, { id: "y", role: "b" }, { id: "z", role: "c" }], covers: [true, true, true] },
      ]),
    ).toEqual([{ id: "e2", label: "Docs.documents" }]);
  });

  it("says a lifecycle's rule in words", () => {
    const states = new Map([["s1", "Released"]]);
    expect(lifecycleRuleText("restart", states)).toBe("Start over");
    expect(lifecycleRuleText("keep", states)).toBe("Keep");
    expect(lifecycleRuleText("s1", states)).toBe("Go to Released");
  });
});

describe("the scenario fields", () => {
  const process = {
    events: [
      { id: "e1", name: "release", verb: "promote" },
      { id: "e2", name: "reopen", verb: "revise" },
      { id: "e3", name: "other", verb: "promote" },
    ],
    transitions: [
      { id: "t1", source: "a", event: "e1", nextRevision: 1 },
      { id: "t2", source: "a", event: "e2" },
      { id: "t3", source: "a", event: "e3" },
    ],
  } as never;
  it("an event step takes a name when its event can revise or label", () => {
    expect(eventTakesRevision(process, "e1")).toBe(true);
    expect(eventTakesRevision(process, "e2")).toBe(true);
    expect(eventTakesRevision(process, "e3")).toBe(false);
    expect(stepTakesName({ id: "s", event: "e1" }, process)).toBe(true);
    expect(stepTakesLevel({ id: "s", event: "e1" })).toBe(false);
  });
  it("a revise step takes a level or a name, one at a time", () => {
    const step = { id: "s", input: "verb" as const, verb: "revise" };
    expect(stepTakesLevel(step)).toBe(true);
    expect(stepTakesLevel({ id: "s", input: "verb" as const, verb: "modify" })).toBe(false);
    setStepRevision(step, "level", "2");
    expect(stepRevisionLevel(step)).toBe(2);
    expect(stepRevisionName(step)).toBe("");
    setStepRevision(step, "name", "C");
    expect(stepRevisionName(step)).toBe("C");
    expect(stepRevisionLevel(step)).toBeUndefined();
    setStepRevision(step, "name", " ");
    expect(step).not.toHaveProperty("revision");
  });
});

describe("the quick fixes of the revision rules", () => {
  const entity = (revisions: Json): ModelJson => ({ kind: "entity", id: "E", name: "Drawing", revisions }) as unknown as ModelJson;
  it("MQ8303 adds the separator", () => {
    const fix = revisionFix("MQ8303", entity({ levels: LEVELS, separator: "" }), "/revisions/separator");
    expect((fix?.json.revisions as Json).separator).toBe(".");
  });
  it("MQ8306 picks level 1", () => {
    const process = { kind: "process", id: "P", transitions: [{ id: "t", source: "a", nextRevision: 2 }] } as unknown as ModelJson;
    const fix = revisionFix("MQ8306", process, "/transitions/0/nextRevision");
    expect((fix?.json.transitions as Json[])[0].nextRevision).toBe(1);
  });
  it("MQ8308 picks move", () => {
    const fix = revisionFix("MQ8308", entity({ links: [{ end: "x", onRevise: "duplicate" }] }), "/revisions/links/0/onRevise");
    expect(((fix?.json.revisions as Json).links as Json[])[0].onRevise).toBe("move");
  });
  it("MQ8312 removes the entry, and the block when nothing is left", () => {
    const two = revisionFix(
      "MQ8312",
      entity({ levels: LEVELS, attributes: [{ attribute: "a", onRevise: "reset" }, { attribute: "b", onRevise: "clear" }] }),
      "/revisions/attributes/0/attribute",
    );
    expect(((two?.json.revisions as Json).attributes as Json[]).map((e) => e.attribute)).toEqual(["b"]);
    const alone = revisionFix("MQ8312", entity({ attributes: [{ attribute: "a", onRevise: "reset" }] }), "/revisions/attributes/0");
    expect(alone?.json.revisions).toBeUndefined();
  });
});

describe("the mock's revision rules", () => {
  const entry = (json: Json): ModelEntry => ({ id: String(json.id), path: `${String(json.id)}.json`, json }) as unknown as ModelEntry;
  const find = (entries: ModelEntry[], id: string) => {
    const out: { rule: string; message: string; pointer: string }[] = [];
    revisionDiagnostics(entries.find((e) => e.id === id)!, () => entries, (rule, _s, message, pointer) => out.push({ rule, message, pointer }));
    return out;
  };
  const entity = (revisions: Json, extra: Json = {}) => entry({ kind: "entity", id: "E", name: "Drawing", attributes: [{ id: "rev", name: "revision", type: "string" }, { id: "n", name: "count", type: "int32" }], ...extra, revisions });

  it("MQ8301: both format and values, a list value with the separator, a family with no levels", () => {
    const found = find([entity({ levels: [{ format: "letters", values: ["A"] }, { values: ["a.b", "c"] }] })], "E");
    expect(found.map((f) => f.rule)).toEqual(["MQ8301", "MQ8301"]);
    expect(found[0].message).toContain("has both format and values; keep one.");
    expect(found[1].message).toContain("holds the separator");
    expect(find([entity({ separator: "." })], "E").some((f) => f.rule === "MQ8301" && f.pointer === "/revisions")).toBe(true);
  });

  it("MQ8301: a level without startEmpty below one with it", () => {
    const found = find([entity({ levels: [{ format: "letters", startEmpty: true }, { format: "numbers" }] })], "E");
    expect(found.map((f) => f.message).join(" ")).toContain("every level below one that starts empty starts empty too");
  });

  it("MQ8303: a separator that cannot be read back", () => {
    expect(find([entity({ levels: [{ format: "numbers" }, { format: "numbers" }], separator: "" })], "E").map((f) => f.rule)).toEqual(["MQ8303"]);
    expect(find([entity({ levels: [{ format: "letters" }, { format: "numbers" }], separator: "" })], "E")).toEqual([]);
    expect(find([entity({ levels: [{ format: "letters" }], separator: "1" })], "E").map((f) => f.rule)).toEqual(["MQ8303"]);
  });

  it("MQ8305: a bound attribute that is not text", () => {
    expect(find([entity({ levels: [{ format: "letters" }], boundAttribute: "n" })], "E")[0]).toMatchObject({ rule: "MQ8305", pointer: "/revisions/boundAttribute" });
    expect(find([entity({ levels: [{ format: "letters" }], boundAttribute: "rev" })], "E")).toEqual([]);
  });

  it("MQ8312: two entries for one attribute, and a key attribute that is reset", () => {
    const e = entity(
      { levels: [{ format: "letters" }], attributes: [{ attribute: "rev", onRevise: "reset" }, { attribute: "rev", onRevise: "clear" }, { attribute: "n", onRevise: "reset" }] },
      { key: { attributes: ["n"] } },
    );
    const found = find([e], "E");
    expect(found.map((f) => f.rule)).toEqual(["MQ8312", "MQ8312"]);
    expect(found[0].message).toContain("two entries for attribute");
    expect(found[1].message).toContain("part of the key");
  });

  it("MQ8306: a level the governed type lacks", () => {
    const process = entry({
      kind: "process",
      id: "P",
      name: "Memo lifecycle",
      use: "lifecycle",
      subjects: [{ id: "s", entity: "E" }],
      states: [{ id: "a", name: "a" }],
      transitions: [{ id: "t", source: "a", targets: ["a"], nextRevision: 2 }],
    });
    const found = find([entity({ levels: [{ format: "letters" }] }), process], "P");
    expect(found[0]).toMatchObject({ rule: "MQ8306", pointer: "/transitions/0/nextRevision" });
    expect(found[0].message).toContain("has one level");
  });

  it("MQ9305: a scenario step's revision on a step that cannot revise, and an object's revision that is not a value", () => {
    const process = entry({ kind: "process", id: "P", name: "P", use: "lifecycle", subjects: [{ id: "s", entity: "E" }], events: [], states: [{ id: "a", name: "a" }] });
    const scenario = entry({
      kind: "scenario",
      id: "S",
      name: "S",
      process: "P",
      subject: "o",
      objects: [{ id: "o", name: "o", entity: "E", revision: "1" }],
      steps: [{ id: "x", input: "verb", verb: "modify", revision: "B" }, { id: "y", input: "verb", verb: "revise", revision: 4 }],
    });
    const found = find([entity({ levels: [{ format: "letters" }] }), process, scenario], "S");
    expect(found.map((f) => f.pointer)).toEqual(["/objects/0/revision", "/steps/0/revision", "/steps/1/revision"]);
  });
});

describe("the references and deletes of the block", () => {
  const entity: Json = {
    kind: "entity",
    id: "E",
    name: "Drawing",
    attributes: [{ id: "A1", name: "rev", type: "string" }],
    revisions: {
      levels: [{ format: "letters" }],
      boundAttribute: "A1",
      lifecycles: [{ lifecycle: "L1", onRevise: "01HZZZZZZZZZZZZZZZZZZZZZZ1" }],
      attributes: [{ attribute: "A1", onRevise: "reset" }],
    },
  };
  const process: Json = { kind: "process", id: "L1", name: "Life", use: "lifecycle", subjects: [{ id: "s", entity: "E" }], states: [{ id: "01HZZZZZZZZZZZZZZZZZZZZZZ1", name: "S" }] };
  const docs = new Map<string, CascadeDoc>([
    ["E", { id: "E", path: "E.json", json: entity }],
    ["L1", { id: "L1", path: "L1.json", json: process }],
  ]);

  it("lists the pointers into the block", () => {
    expect(referencesOf(entity).map((r) => r.pointer)).toEqual(
      expect.arrayContaining(["/revisions/boundAttribute", "/revisions/lifecycles/0/lifecycle", "/revisions/lifecycles/0/onRevise", "/revisions/attributes/0/attribute"]),
    );
  });

  it("removes the entry of a deleted lifecycle once, under either choice, and drops the empty list", () => {
    for (const resolution of ["refuse", "remove-references"] as const) {
      const cascade = planDelete(docs, ["L1"], resolution);
      expect(cascade.plan.refused.filter((r) => r.id === "E")).toEqual([]);
      const edited = cascade.edited.get("E")!;
      expect((edited.revisions as Json).lifecycles).toBeUndefined();
      expect((edited.revisions as Json).attributes).toHaveLength(1);
    }
  });
});
