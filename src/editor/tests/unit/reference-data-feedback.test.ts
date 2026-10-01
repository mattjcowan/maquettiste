// The Reference data screen after the owner's first day of modelling (reference-types-seeds-localization.md 4.7):
// field display names and descriptions in the Rows grid's headers, the row editor's changes, Shift+Enter, Rename
// with the display name and the type's seed, the explorer's marks on reference types and seeds that do not move,
// Used by groups for reference types, and the Storage tab's plain words and preview units.
import { describe, expect, it } from "vitest";
import type { ReferenceTypeDoc } from "@/api/types";
import { isMovable, isRenamable, menuFor } from "@/explorer/menus";
import { usageGroupLabel } from "@/workspaces/reference-data/listModel";
import {
  descriptionText,
  gridAction,
  gridColumns,
  hasRowChanges,
  headerText,
  headerTooltip,
  isLongText,
  ROW_DESCRIPTION_HELP,
  rowEditChanges,
  rowFormValues,
  localeColumns,
  translationsByLocale,
} from "@/workspaces/reference-data/rowsModel";
import { databaseUnits, defaultOption, preferredUnit, previewFailure, strategyInUse } from "@/workspaces/reference-data/storageChoices";
import { renamedType, seedsFollowingRename, typeNameProblem } from "@/workspaces/reference-data/typeMenu";

const type = {
  kind: "reference-type",
  id: "T",
  name: "CarModel",
  code: { id: "C", displayName: "Model code", description: "The maker's code for the model." },
  label: { id: "L" },
  attributes: [
    { id: "F1", name: "maker", type: "string", displayName: "Maker", description: "Who builds it." },
    { id: "F2", name: "doors", type: "int16" },
    { id: "F3", name: "notes", type: "string", description: { file: "car-model.notes.md" } },
  ],
} as unknown as ReferenceTypeDoc;

describe("field display names and descriptions in the Rows grid", () => {
  const columns = gridColumns(type);

  it("titles a column with the field's display name and explains it in the tooltip", () => {
    const code = columns.find((c) => c.key === "code")!;
    expect(headerText(code)).toBe("Model code");
    expect(headerTooltip(code)).toBe("Model code (code), required\nThe maker's code for the model.");
    const maker = columns.find((c) => c.key === "F1")!;
    expect(headerText(maker)).toBe("Maker");
    expect(headerTooltip(maker)).toBe("Maker (maker)\nWho builds it.");
    const doors = columns.find((c) => c.key === "F2")!;
    expect(headerText(doors)).toBe("doors");
    expect(headerTooltip(doors)).toBe("doors");
    expect(headerText({ label: "tags", collection: true })).toBe("tags[]");
  });

  it("names the sidecar file of a description kept in one, and describes the built-in description column", () => {
    expect(columns.find((c) => c.key === "F3")!.help).toBe("Described in car-model.notes.md.");
    expect(columns.find((c) => c.key === "description")!.help).toBe(ROW_DESCRIPTION_HELP);
    expect(descriptionText("  ")).toBeUndefined();
    expect(descriptionText(null)).toBeUndefined();
  });
});

describe("the row editor", () => {
  const columns = [...gridColumns(type), ...localeColumns(["fr"])];
  const row = { id: "R1", seed: "S", index: 0, values: { code: "c3", label: "C3", F2: 5 } };

  it("starts from the cells as the grid shows them", () => {
    const form = rowFormValues(row, columns);
    expect(form.code).toBe("c3");
    expect(form.F2).toBe("5");
    expect(form.description).toBe("");
    expect(form["@label:fr"]).toBe("");
  });

  it("writes only the changed cells, parsed as the grid parses typed text, and the changed translations apart", () => {
    const before = rowFormValues(row, columns);
    const after = { ...before, description: "A small car\nmade in town", F2: "3", label: "", "@label:fr": "Petite" };
    const changes = rowEditChanges(columns, before, after);
    expect(changes.cells).toEqual({ description: "A small car\nmade in town", F2: 3, label: null });
    expect(changes.translations).toEqual([{ locale: "fr", field: "label", value: "Petite" }]);
    expect(hasRowChanges(changes)).toBe(true);
    expect(hasRowChanges(rowEditChanges(columns, before, before))).toBe(false);
  });

  it("groups the changed translations into one write per locale, every changed field of the locale in it", () => {
    const two = [...gridColumns(type), ...localeColumns(["fr", "de"])];
    const before = rowFormValues(row, two);
    const after = { ...before, "@label:fr": "Petite", "@description:fr": "Une petite voiture", "@description:de": "Ein Kleinwagen" };
    const changes = rowEditChanges(two, before, after);
    expect(changes.translations).toHaveLength(3);
    expect(translationsByLocale(changes.translations)).toEqual([
      {
        locale: "fr",
        edits: [
          { field: "label", value: "Petite" },
          { field: "description", value: "Une petite voiture" },
        ],
      },
      { locale: "de", edits: [{ field: "description", value: "Ein Kleinwagen" }] },
    ]);
    expect(translationsByLocale([])).toEqual([]);
  });

  it("edits text fields in a text area, but not the code, numbers, collections or ends", () => {
    const byKey = new Map(columns.map((c) => [c.key, c]));
    expect(isLongText(byKey.get("description")!)).toBe(true);
    expect(isLongText(byKey.get("label")!)).toBe(true);
    expect(isLongText(byKey.get("F1")!)).toBe(true);
    expect(isLongText(byKey.get("code")!)).toBe(false);
    expect(isLongText(byKey.get("F2")!)).toBe(false);
    expect(isLongText({ key: "x", type: "string", collection: true })).toBe(false);
    expect(isLongText({ key: "x", type: "end", end: "E", collection: false })).toBe(false);
  });

  it("opens on Shift+Enter, and Ctrl+5 picks the fifth tab", () => {
    expect(gridAction({ key: "Enter", shiftKey: true }, false)).toEqual({ type: "open-row" });
    expect(gridAction({ key: "Enter" }, false)).toEqual({ type: "edit" });
    expect(gridAction({ key: "5", ctrlKey: true }, false)).toEqual({ type: "tab", index: 4 });
  });
});

describe("Rename", () => {
  it("renames the type's only seed whatever its name, or of several those named after the type", () => {
    expect(seedsFollowingRename([{ name: "car_models" }], "CarModel")).toEqual([{ name: "car_models" }]);
    expect(seedsFollowingRename([{ name: "CarModel" }, { name: "Demo" }], "CarModel")).toEqual([{ name: "CarModel" }]);
  });

  it("sets the name and the display name together, removing an empty display name", () => {
    const doc = { name: "car_models", displayName: "Car Models" };
    expect(renamedType(doc, "CarModel", "Car models")).toEqual({ name: "CarModel", displayName: "Car models" });
    expect(renamedType(doc, "CarModel", " ")).toEqual({ name: "CarModel" });
    expect(renamedType(doc, "car_models", "Car Models")).toBeNull();
  });

  it("checks the name: an identifier, not taken by another type", () => {
    expect(typeNameProblem("Car Model", "CarModel", [])).toBe("Not a name: letters, digits and underscores, not starting with a digit.");
    expect(typeNameProblem("country", "CarModel", ["CarModel", "Country"])).toBe("country is taken.");
    expect(typeNameProblem("carmodel", "CarModel", ["CarModel"])).toBeNull();
  });
});

describe("the explorer's menus for reference data", () => {
  const ids = (t: Parameters<typeof menuFor>[0][number]) => menuFor([t]).map((i) => i.id);

  it("offers Apply stereotype… and Tag… on a reference type, its category being Move to category…", () => {
    const type = ids({ type: "element", kind: "reference-type", element: true, home: "reference-data" });
    expect(type).toEqual(expect.arrayContaining(["apply-stereotype", "tag", "type:move-category", "type:rename"]));
    expect(type).not.toContain("set-category");
    const both = menuFor([
      { type: "element", kind: "reference-type", element: true },
      { type: "element", kind: "reference-type", element: true },
    ]).map((i) => i.id);
    expect(both).toEqual(expect.arrayContaining(["apply-stereotype", "tag"]));
  });

  it("moves no seed to a domain; a reference type's seed is not renamed on its own", () => {
    expect(isMovable("seed")).toBe(false);
    expect(ids({ type: "element", kind: "seed", element: true, home: "domain-model" })).not.toContain("move");
    expect(ids({ type: "element", kind: "seed", element: true, home: "domain-model" })).toContain("rename");
    const reference = ids({ type: "element", kind: "seed", element: true, home: "reference-data" });
    expect(reference).not.toContain("move");
    expect(reference).not.toContain("rename");
    expect(isRenamable("seed", "reference-data")).toBe(false);
    expect(isRenamable("entity")).toBe(true);
  });
});

describe("Used by groups", () => {
  const ref = { kind: "reference-type", kindLabel: "Reference type" };
  const entity = { kind: "entity", kindLabel: "Entity" };

  it("groups reference types by category path, or as Reference types, never by domain", () => {
    expect(usageGroupLabel(ref, null, ["Vehicles", "Cars"], "Not in a domain")).toBe("Reference type · Vehicles › Cars");
    expect(usageGroupLabel(ref, null, [], "Not in a domain")).toBe("Reference types");
    expect(usageGroupLabel(entity, "Billing", [], "Not in a domain")).toBe("Entity · Billing");
    expect(usageGroupLabel(entity, null, [], "Not in a domain")).toBe("Entity · Not in a domain");
  });
});

describe("the Storage tab's words", () => {
  it("says which strategy is in use and where it comes from", () => {
    expect(strategyInUse({ strategy: "check", source: "project" })).toBe("check, from the project");
    expect(strategyInUse({ strategy: null, source: "type" })).toBe("The packs decide, from this type");
    expect(strategyInUse({ strategy: null, source: null })).toBe("The packs decide, nothing is set");
  });

  it("names what the default is", () => {
    expect(defaultOption({ strategy: "model-only", source: "project" })).toBe("Use the default (model-only, from the project)");
    expect(defaultOption({ strategy: "check", source: "type" })).toBe("Use the default (check, as set for all databases)");
    expect(defaultOption({ strategy: null, source: null })).toBe("Use the default (the packs decide)");
  });

  it("lists the database-scoped units of the enabled packs, preferring a seed unit", () => {
    const units = databaseUnits([
      {
        name: "sql-ddl",
        units: [
          { id: "table", for: "each table" },
          { id: "schema", for: "select databases" },
          { id: "seed", for: "select databases" },
        ],
      },
      { name: "docs", units: [{ id: "catalog", for: "each database" }] },
      { name: "off", enabled: false, units: [{ id: "seed", for: "each database" }] },
    ]);
    expect(units.map((u) => `${u.pack}/${u.unit}`)).toEqual(["sql-ddl/schema", "sql-ddl/seed", "docs/catalog"]);
    expect(preferredUnit(units)?.unit).toBe("seed");
    expect(preferredUnit(units.filter((u) => u.unit !== "seed"))?.unit).toBe("schema");
    expect(preferredUnit([])).toBeNull();
  });

  it("explains a failed preview with the engine's reason when the unit does not render for the database", () => {
    const raw = "This template expects an element its selector returns (unit 'seed' is 'select databases'), not '01J' (database); pick one.";
    expect(previewFailure(raw, { reason: "selector", detail: "The selector of seed returns only databases whose dialect is postgresql." })).toBe(
      "The selector of seed returns only databases whose dialect is postgresql.",
    );
    expect(previewFailure("Template error at line 3.", { reason: "new", detail: "No recorded state." })).toBe("Template error at line 3.");
    expect(previewFailure(raw, null)).toBe(raw);
  });
});
