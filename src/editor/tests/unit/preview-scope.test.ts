// The Templates preview's element picker (generation-ui.md 3.3): only elements of the unit's scope kind, the first by
// default, the remembered one while it is in scope; partials preview through a unit that includes them; a scope
// mismatch reads as a sentence, not the raw render error.
import { describe, expect, it } from "vitest";
import { mismatchText, noFilesText, pickElement, previewUnit, scopeCandidates, scopeMismatch, unitScope } from "@/workspaces/generate/previewScope";

const index = [
  { id: "e2", kind: "entity", name: "Payment" },
  { id: "e1", kind: "entity", name: "Invoice", displayName: "Invoice" },
  { id: "r1", kind: "reference-type", name: "Currency" },
  { id: "r2", kind: "reference-type", name: "Country" },
  { id: "n1", kind: "enum", name: "Status" },
  { id: "v1", kind: "value-object", name: "Money" },
  { id: "p1", kind: "package", name: "billing" },
];

describe("unitScope", () => {
  it("maps every scope the planner knows", () => {
    const cases: [string, string, string | null, string | null][] = [
      ["each package", "domain", "package", "package"],
      ["each entity", "entity", "entity", "entity"],
      ["each relation", "relation", "relation", "relation"],
      ["each enum", "enum", "enum", "enum"],
      ["each value object", "value object", "value-object", "value_object"],
      ["each table", "table", null, "table"],
      ["each reference type", "reference type", "reference-type", "reference_type"],
      ["each seed", "seed", "seed", "seed"],
      ["each locale", "locale", null, "locale"],
    ];
    for (const [f, label, kind, variable] of cases) {
      const s = unitScope(f);
      expect([s.label, s.indexKind, s.variable, s.once], f).toEqual([label, kind, variable, false]);
    }
    expect(unitScope("model").once).toBe(true);
    expect(unitScope("select databases")).toMatchObject({ label: "database", indexKind: null, once: false });
    expect(unitScope("select m => m.entities.filter(e => e.abstract)").label).toBe("entity");
    expect(unitScope("select whatever()").label).toBe("element");
  });
});

describe("scopeCandidates and pickElement", () => {
  it("lists only the scope kind's elements, A to Z, and defaults to the first", () => {
    const rt = scopeCandidates(unitScope("each reference type"), index, []);
    expect(rt.map((c) => c.id)).toEqual(["r2", "r1"]);
    expect(pickElement(rt, undefined)).toBe("r2");
    // An entity remembered from another unit is never rendered by a reference type unit.
    expect(pickElement(rt, "e1")).toBe("r2");
    expect(pickElement(rt, "r1")).toBe("r1");
    expect(scopeCandidates(unitScope("each entity"), index, []).map((c) => c.label)).toEqual(["Invoice", "Payment"]);
  });

  it("narrows an each kind to the elements the unit's plan renders (its where filter and skip hints)", () => {
    const entities = scopeCandidates(unitScope("each entity"), index, [], new Set(["e1"]));
    expect(entities.map((c) => c.label)).toEqual(["Invoice"]);
    expect(scopeCandidates(unitScope("each entity"), index, [], null).map((c) => c.label)).toEqual(["Invoice", "Payment"]);
  });

  it("explains a render with no file and no diagnostic", () => {
    const scope = unitScope("each entity");
    expect(noFilesText(scope, "repository", { files: [], diagnostics: [] })).toBe(
      "Unit repository renders no file for this entity: its where filter or a generation.skip hint leaves it out.",
    );
    expect(noFilesText(scope, "repository", { files: [{}], diagnostics: [] })).toBeNull();
    expect(noFilesText(scope, "repository", { files: [], diagnostics: [{}] })).toBeNull();
    expect(noFilesText(scope, "repository", null)).toBeNull();
  });

  it("takes planned ids for tables, locales and selectors, and nothing for a model unit", () => {
    expect(scopeCandidates(unitScope("each locale"), index, ["fr", "de", "fr"]).map((c) => c.id)).toEqual(["fr", "de"]);
    expect(scopeCandidates(unitScope("select databases"), index, ["e1"]).map((c) => c.label)).toEqual(["Invoice"]);
    const model = scopeCandidates(unitScope("model"), index, ["e1"]);
    expect(model).toEqual([]);
    expect(pickElement(model, "e1")).toBeNull();
  });
});

describe("previewUnit", () => {
  const units = ["entity", "reference-type", "registrations"];
  it("previews a template through its unit, and a partial through the first unit including it, saying so", () => {
    expect(previewUnit({ path: "reference-type.scriban", role: "template" }, ["reference-type"], units, "")).toEqual({ unit: "reference-type", note: null });
    const partial = previewUnit({ path: "_csharp.scriban", role: "partial" }, ["entity", "reference-type"], units, "");
    expect(partial.unit).toBe("entity");
    expect(partial.note).toBe("_csharp.scriban is a partial: previewing unit entity, which includes it.");
    expect(previewUnit({ path: "_unused.scriban", role: "partial" }, [], units, "").note).toBe("No unit uses _unused.scriban yet: previewing unit entity.");
    expect(previewUnit({ path: "helpers.js", role: "script" }, units, units, "").note).toContain("is a script");
    expect(previewUnit({ path: "entity.scriban", role: "template" }, ["entity"], units, "registrations").note).toContain("does not use entity.scriban");
  });
});

describe("scopeMismatch", () => {
  it("turns the missing scope variable into a sentence", () => {
    const scope = unitScope("each reference type");
    const raw = [{ rule: "MQ6006", message: "The variable or function `reference_type` was not found" }];
    expect(scopeMismatch(scope, raw)).toBe("This template renders one reference type; pick a reference type to preview it.");
    expect(mismatchText(unitScope("each locale"))).toBe("This template renders one locale; pick a locale to preview it.");
    expect(scopeMismatch(scope, [{ rule: "MQ6006", message: "The variable or function `entity` was not found" }])).toBeNull();
    expect(scopeMismatch(unitScope("model"), raw)).toBeNull();
  });
});
