// The inspector's tabs (src/inspector/tabs.ts): Properties, Attributes (only for kinds with attributes), JSON and
// Used, with the remembered tab falling back to Properties when the kind has no such tab.
import { describe, expect, it } from "vitest";
import { attributesView, INSPECTOR_TAB_LABELS, inspectorTabs, resolveInspectorTab } from "@/inspector/tabs";

const labels = (kind: string) => inspectorTabs(kind).map((t) => INSPECTOR_TAB_LABELS[t]);

describe("inspector tabs", () => {
  it("shows Attributes only for value objects, stereotypes, relationships and entities", () => {
    for (const kind of ["value-object", "stereotype", "relation", "entity"]) expect(labels(kind)).toEqual(["Properties", "Attributes", "JSON", "Used"]);
    for (const kind of ["enum", "scalar-type", "database", "package", "diagram", "reference-type"])
      expect(labels(kind)).toEqual(["Properties", "JSON", "Used"]);
  });

  it("edits the grid for value objects, stereotypes and relationships and lists an entity's attributes read-only", () => {
    expect(attributesView("value-object")).toBe("grid");
    expect(attributesView("stereotype")).toBe("grid");
    expect(attributesView("relation")).toBe("grid");
    expect(attributesView("entity")).toBe("list");
    expect(attributesView("enum")).toBeNull();
  });

  it("keeps the remembered tab when the kind has it", () => {
    expect(resolveInspectorTab("value-object", "attributes")).toBe("attributes");
    expect(resolveInspectorTab("entity", "attributes")).toBe("attributes");
    expect(resolveInspectorTab("enum", "json")).toBe("json");
    expect(resolveInspectorTab("enum", "references")).toBe("references");
  });

  it("falls back to Properties for a kind without the tab, and by default", () => {
    expect(resolveInspectorTab("enum", "attributes")).toBe("properties");
    expect(resolveInspectorTab("relation", "attributes")).toBe("attributes");
    expect(resolveInspectorTab("scalar-type", "attributes")).toBe("properties");
    expect(resolveInspectorTab("entity", undefined)).toBe("properties");
    expect(resolveInspectorTab("entity", "nonsense")).toBe("properties");
  });
});
