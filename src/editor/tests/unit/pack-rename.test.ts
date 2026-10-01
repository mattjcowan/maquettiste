// Rename pack (the pack editor's header): the name rule the dialog checks before it sends anything, the sentence about
// the generated files, and how the Generate screen's tabs and explorer rows follow the new name.
import { describe, expect, it } from "vitest";
import { hintsLabel, renamePackError, trackedSentence } from "@/workspaces/generate/RenamePackDialog";
import { namesPack, renamePackHints } from "@/workspaces/generate/packHints";
import { renameExpandedKeys, renamePackTab } from "@/workspaces/generate/packTabs";

describe("rename pack", () => {
  it("checks the engine's pack-name rule and refuses the current or a taken name", () => {
    expect(renamePackError("", "sql-ddl", [])).toBe("Enter a name.");
    expect(renamePackError("sql-ddl", "sql-ddl", ["sql-ddl"])).toMatch(/different/);
    for (const bad of ["Ddl", "1ddl", "ddl-", "dd--l", "dd_l", "dd l"]) expect(renamePackError(bad, "sql-ddl", [])).toMatch(/single hyphens/);
    expect(renamePackError("csharp-dapper", "sql-ddl", ["csharp-dapper", "sql-ddl"])).toMatch(/exists/);
    expect(renamePackError("ddl-v2", "sql-ddl", ["csharp-dapper", "sql-ddl"])).toBeNull();
  });

  it("says whether the generated files stay tracked", () => {
    expect(trackedSentence(null)).toMatch(/Counting/);
    expect(trackedSentence(0)).toMatch(/no files/);
    expect(trackedSentence(1)).toMatch(/^The 1 file it generated stay where they are/);
    expect(trackedSentence(12)).toMatch(/12 files .* stay tracked under the new name/);
  });

  it("keeps the tab's place, pane, focus and plan choice under the new name", () => {
    const state = {
      packTabs: ["csharp-dapper", "sql-ddl", "docs"],
      packTab: "sql-ddl",
      packPane: { "sql-ddl": "templates" as const, docs: "units" as const },
      packFocus: { pack: "sql-ddl", unit: "table" },
      chosenPacks: ["sql-ddl", "docs"],
    };
    const next = renamePackTab(state, "sql-ddl", "ddl");
    expect(next.packTabs).toEqual(["csharp-dapper", "ddl", "docs"]);
    expect(next.packTab).toBe("ddl");
    expect(next.packPane).toEqual({ ddl: "templates", docs: "units" });
    expect(next.packFocus).toEqual({ pack: "ddl", unit: "table" });
    expect(next.chosenPacks).toEqual(["ddl", "docs"]);
    expect(renamePackTab({ ...state, chosenPacks: null, packTab: null }, "sql-ddl", "ddl")).toMatchObject({ chosenPacks: null, packTab: null });
  });

  it("moves the explorer's expanded rows of the pack and leaves the others", () => {
    const next = renameExpandedKeys(new Set(["p:sql-ddl", "p:sql-ddl/units", "p:sql-ddl-two", "p:docs"]), "sql-ddl", "ddl");
    expect([...next].sort()).toEqual(["p:ddl", "p:ddl/units", "p:docs", "p:sql-ddl-two"]);
  });

  it("finds and moves the generation hints that name the pack, at any depth, leaving a map that has the new name", () => {
    const doc = {
      name: "Customer",
      generation: { "*": { skip: false }, "sql-ddl": { skip: true } },
      attributes: [{ name: "id", generation: { "sql-ddl": { rename: "customer_id" } } }, { name: "email" }],
      members: [{ generation: { "sql-ddl": { skip: true }, ddl: { skip: false } } }],
      properties: { note: "sql-ddl" },
    };
    expect(namesPack(doc, "sql-ddl")).toBe(true);
    expect(namesPack(doc, "csharp-dapper")).toBe(false);
    expect(renamePackHints(doc, "sql-ddl", "ddl")).toBe(2);
    expect(Object.keys(doc.generation)).toEqual(["*", "ddl"]);
    expect(doc.attributes[0].generation).toEqual({ ddl: { rename: "customer_id" } });
    expect(doc.members[0].generation).toEqual({ "sql-ddl": { skip: true }, ddl: { skip: false } });
    expect(doc.properties).toEqual({ note: "sql-ddl" });
    expect(hintsLabel(1)).toBe("Also update the generation hints that name this pack (1 element)");
    expect(hintsLabel(3)).toMatch(/\(3 elements\)$/);
  });
});
