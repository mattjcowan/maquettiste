// The CSV import preview (reference-types-seeds-localization.md 2.3) and the field type picker's sections (4.5).
import { describe, expect, it } from "vitest";
import type { ElementSummary, ImportPreview } from "@/api/types";
import { previewSummary } from "@/workspaces/reference-data/csvPreview";
import { flatItems, pickerGroups } from "@/inspector/typePicker";

describe("CSV import preview", () => {
  it("summarizes added, changed and removed rows with before and after cells", () => {
    const preview: ImportPreview = {
      added: 2,
      changed: [{ id: "r1", before: { "@label": "Kilo", factor: 1 }, after: { "@label": "Kilogram", factor: 1000 } }],
      removed: 0,
      diagnostics: [],
      ignoredHeaders: ["colour"],
    };
    const s = previewSummary(preview);
    expect(s.headline).toBe("2 rows added, 1 row changed, 0 rows removed");
    expect(s.changed).toEqual([
      {
        id: "r1",
        fields: [
          { name: "@label", before: "Kilo", after: "Kilogram" },
          { name: "factor", before: "1", after: "1000" },
        ],
      },
    ]);
    expect(s.ignoredHeaders).toEqual(["colour"]);
    expect(s.canApply).toBe(true);
  });

  it("cannot apply an import with errors or one that changes nothing", () => {
    const error = { rule: "MQ7001", severity: "error", message: "Duplicate code kg" } as ImportPreview["diagnostics"][number];
    expect(previewSummary({ added: 1, changed: [], removed: 0, diagnostics: [error] })).toMatchObject({
      canApply: false,
      errors: ["MQ7001: Duplicate code kg"],
    });
    const nothing = previewSummary({ added: 0, changed: [], removed: 0, diagnostics: [] });
    expect(nothing.canApply).toBe(false);
    expect(nothing.headline).toMatch(/nothing to import/);
  });
});

describe("field type picker", () => {
  const option = (id: string, kind: ElementSummary["kind"], name: string, displayName?: string) =>
    ({ id, kind, name, displayName, package: null, tags: [], category: null, stereotypes: [], hash: "", path: "" }) as ElementSummary;
  const options = [
    option("e1", "enum", "InvoiceStatus"),
    option("s1", "scalar-type", "Email"),
    option("r1", "reference-type", "UnitOfMeasure", "Unit of measure"),
    option("r2", "reference-type", "Allergen"),
    option("v1", "value-object", "Money"),
  ];

  it("offers the sections in order, Recent first when there is no search", () => {
    const groups = pickerGroups(options, "", ["ref:r1"]);
    expect(groups.map((g) => g.label)).toEqual(["Recent", "Built-in", "Custom types", "Enums", "Reference data", "Value objects"]);
    expect(groups[0].items.map((i) => i.label)).toEqual(["Unit of measure"]);
    expect(groups[4].items.map((i) => i.label)).toEqual(["Allergen", "Unit of measure"]);
  });

  it("searches all sections at once, prefix matches first, by display name or name", () => {
    const groups = pickerGroups(options, "unit", ["ref:r1"]);
    expect(groups.map((g) => g.label)).toEqual(["Reference data"]);
    expect(flatItems(pickerGroups(options, "uni", [])).map((i) => i.value)).toEqual(["ref:r1"]);
    expect(flatItems(pickerGroups(options, "unitofm", [])).map((i) => i.value)).toEqual(["ref:r1"]);
    expect(flatItems(pickerGroups(options, "st", [])).map((i) => i.label)).toEqual(["string", "InvoiceStatus"]);
  });
});
