// The naming pass (explorer-redesign.md section 2): the user-facing words match the owner's table, and the rail
// keeps the owner's order. Code identifiers keep the spec's words; only these labels change.
import { describe, expect, it } from "vitest";
import { KIND_LABELS as MODEL_KIND_LABELS } from "@/model/model";
import { EXPLORER_LABELS, GLOSSARY, KIND_LABELS, RAIL_LABELS, RAIL_NAME, SCREEN_LABELS, SEARCH_PLACEHOLDER, countOf, kindFolder } from "@/model/labels";
import { RAIL } from "@/app/Rail";

describe("naming (EX section 2)", () => {
  it("names each kind with the owner's words", () => {
    expect(KIND_LABELS).toMatchObject({
      package: "Domain",
      relation: "Relationship",
      enum: "Enum",
      "value-object": "Value object",
      "scalar-type": "Custom type",
      mapping: "Customised mapping",
      "tag-vocabulary": "Tags",
      "category-tree": "Categories",
    });
    expect(MODEL_KIND_LABELS).toBe(KIND_LABELS);
  });

  it("names the kind folders in the plural", () => {
    const folders = Object.fromEntries(
      ["package", "relation", "enum", "value-object", "scalar-type", "seed", "mapping", "reference-type", "tag-vocabulary", "category-tree"].map((k) => [
        k,
        kindFolder(k)?.label,
      ]),
    );
    expect(folders).toEqual({
      package: "Domains",
      relation: "Relationships",
      enum: "Enums",
      "value-object": "Value objects",
      "scalar-type": "Custom types",
      seed: "Seed data",
      mapping: "Customised mappings",
      "reference-type": "Reference types",
      "tag-vocabulary": "Tags",
      "category-tree": "Categories",
    });
    expect(countOf(2, "package")).toBe("2 sub-domains");
    expect(countOf(20, "mapping")).toBe("20 customised mappings");
    expect(kindFolder("enum")?.tooltip).toContain("a closed set of named values");
    expect(kindFolder("value-object")?.tooltip).toContain("without identity");
    expect(kindFolder("scalar-type")?.tooltip).toContain("a named restriction of a built-in type");
    expect(kindFolder("relation")?.tooltip).toContain("relations");
  });

  it("keeps the rail's words and the owner's order", () => {
    expect(RAIL.map((r) => r.label)).toEqual(["Domain model", "Processes", "Reference data", "Databases", "Diagrams", "Generate"]);
    expect(RAIL.map((r) => r.view).filter((v) => v !== "generate")).toEqual(Object.keys(EXPLORER_LABELS));
    for (const r of RAIL) if (r.view !== "generate") expect(r.label).toBe(EXPLORER_LABELS[r.view]);
    expect(RAIL_LABELS.databases.tooltip).toBe("Databases, schemas and tables");
    expect(RAIL_LABELS.settings.label).toBe("Settings");
    expect(RAIL_NAME).toBe("Explorers");
  });

  it("names the screens and the search box", () => {
    expect(SCREEN_LABELS.entities).toBe("Domain model");
    expect(SCREEN_LABELS.database).toBe("Databases");
    expect(SEARCH_PLACEHOLDER).toBe("Search the model");
    expect(GLOSSARY.domain).toContain("stored as a package in the model files");
  });

  it("uses no retired word in a label", () => {
    const words = [...Object.values(KIND_LABELS), ...Object.values(SCREEN_LABELS), ...RAIL.map((r) => r.label)].join(" ");
    expect(words).not.toMatch(/\b(Package|Scalar|Workspace)\b/);
  });
});
