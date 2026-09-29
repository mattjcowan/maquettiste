// The Reference data screen's type list (reference-types-seeds-localization.md 4.2): category nesting with counts,
// the flat A to Z toggle, the four search operators and row counts summed over seeds.
import { describe, expect, it } from "vitest";
import type { ElementSummary } from "@/api/types";
import { countLabel, listRows, typeItems, typeMatcher, type CategoryInfo } from "@/workspaces/reference-data/listModel";
import { selectedType } from "@/workspaces/reference-data/ReferenceDataWorkspace";

const row = (id: string, kind: ElementSummary["kind"], name: string, extra: Partial<ElementSummary> = {}): ElementSummary => ({
  id,
  kind,
  name,
  package: null,
  tags: [],
  category: null,
  stereotypes: [],
  hash: id,
  path: `${id}.json`,
  ...extra,
});

const categories: CategoryInfo[] = [
  { value: "c-measure", label: "Measurement", parent: null },
  { value: "c-dim", label: "Dimension", parent: "c-measure" },
  { value: "c-geo", label: "Geography", parent: null },
];

const index: ElementSummary[] = [
  row("t-uom", "reference-type", "UnitOfMeasure", { displayName: "Unit of measure", category: "c-measure", fieldCount: 2 }),
  row("t-len", "reference-type", "LengthUnit", { category: "c-dim" }),
  row("t-country", "reference-type", "Country", { category: "c-geo" }),
  row("t-allergen", "reference-type", "Allergen"),
  row("s-uom", "seed", "UnitOfMeasure", { target: "t-uom", rowCount: 3 }),
  row("s-uom-demo", "seed", "Demo", { target: "t-uom", rowCount: 2 }),
  row("s-country", "seed", "Country", { target: "t-country", rowCount: 250 }),
  row("e-invoice", "entity", "Invoice"),
];

const items = typeItems(index, categories);
const labels = (rows: ReturnType<typeof listRows>["rows"]) =>
  rows.map((r) => `${"  ".repeat(r.depth)}${r.kind === "group" ? `[${r.label} ${r.total}]` : r.item.label}`);

describe("reference data type list", () => {
  it("reads the types with their category paths and sums the rows of their seeds", () => {
    expect(items.map((i) => i.name).sort()).toEqual(["Allergen", "Country", "LengthUnit", "UnitOfMeasure"]);
    const uom = items.find((i) => i.id === "t-uom")!;
    expect(uom).toMatchObject({ label: "Unit of measure", categoryPath: ["Measurement"], rows: 5, fields: 2, seeds: ["s-uom", "s-uom-demo"] });
    expect(items.find((i) => i.id === "t-len")!.categoryPath).toEqual(["Measurement", "Dimension"]);
  });

  it("nests the types by category path with counts, No category last", () => {
    const { rows } = listRows(items, { query: "", flat: false, collapsed: new Set() });
    expect(labels(rows)).toEqual([
      "[Geography 1]",
      "  Country",
      "[Measurement 2]",
      "  [Dimension 1]",
      "    LengthUnit",
      "  Unit of measure",
      "[No category 1]",
      "  Allergen",
    ]);
  });

  it("hides a collapsed group's types and keeps its count", () => {
    const { rows } = listRows(items, { query: "", flat: false, collapsed: new Set(["cat:Measurement"]) });
    expect(labels(rows)).toEqual(["[Geography 1]", "  Country", "[Measurement 2]", "[No category 1]", "  Allergen"]);
  });

  it("lists the types A to Z when flat", () => {
    const { rows } = listRows(items, { query: "", flat: true, collapsed: new Set() });
    expect(labels(rows)).toEqual(["Allergen", "Country", "LengthUnit", "Unit of measure"]);
  });

  it("filters with the four operators, case-insensitively, and counts 'n of total' per group", () => {
    const names = (q: string) =>
      items
        .filter(typeMatcher(q))
        .map((i) => i.name)
        .sort();
    expect(names("unit")).toEqual(["LengthUnit", "UnitOfMeasure"]);
    expect(names("*UNIT")).toEqual(["LengthUnit", "UnitOfMeasure"]);
    expect(names("^unit")).toEqual(["UnitOfMeasure"]);
    expect(names("=country")).toEqual(["Country"]);
    expect(names("~%of%")).toEqual(["UnitOfMeasure"]);
    // The category path is searched too.
    expect(names("^measurement")).toEqual(["LengthUnit", "UnitOfMeasure"]);
    const result = listRows(items, { query: "^unit", flat: false, collapsed: new Set(["cat:Measurement"]) });
    expect(result.matched).toBe(1);
    expect(result.total).toBe(4);
    const group = result.rows[0];
    expect(group.kind === "group" && countLabel(group, true)).toBe("1 of 2");
    // A search expands collapsed groups and hides groups without a match.
    expect(labels(result.rows)).toEqual(["[Measurement 2]", "  Unit of measure"]);
  });

  it("filters 800 types within a frame", () => {
    const many = Array.from({ length: 800 }, (_, i) => ({
      ...items[0],
      id: `t${i}`,
      name: `Type${i}`,
      label: `Type ${i}`,
      categoryPath: [`Group ${i % 40}`, `Sub ${i % 7}`],
    }));
    const started = performance.now();
    listRows(many, { query: "^type 7", flat: false, collapsed: new Set() });
    expect(performance.now() - started).toBeLessThan(50);
  });

  it("maps the selection to a type: the type itself or the target of one of its seeds", () => {
    expect(selectedType(index, ["t-uom"])).toBe("t-uom");
    expect(selectedType(index, ["s-uom-demo"])).toBe("t-uom");
    expect(selectedType(index, ["e-invoice"])).toBeNull();
    expect(selectedType(index, [])).toBeNull();
  });
});
