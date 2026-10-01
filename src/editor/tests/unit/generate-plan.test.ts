// The plan explanation's model (generation-ui.md 4): grouping by unit with counts, the Why sentence, filters and the
// summary line per pack.
import { describe, expect, it } from "vitest";
import type { FileChange, PlanUnit } from "@/api/types";
import {
  countsText,
  filterGroups,
  flattenGroups,
  groupOf,
  groupPlan,
  moreNotesText,
  nothingToWrite,
  nothingToWriteNote,
  orderDiagnostics,
  packSummaryLine,
  planSummary,
  whySentence,
} from "@/workspaces/generate/planModel";

function unit(key: string, patch: Partial<PlanUnit> = {}): PlanUnit {
  const [head, element = null] = key.split(":");
  const [pack, name] = head.split("/");
  return {
    key,
    inputHash: "h",
    readKeys: [],
    skipped: false,
    outputs: [],
    pack,
    unit: name,
    template: `${name}.scriban`,
    elementId: element,
    reason: "new",
    causes: [],
    causeCount: 0,
    ...patch,
  };
}

function change(path: string, kind: FileChange["kind"], unitKey: string): FileChange {
  return { path, kind, pack: unitKey.split("/")[0], unitKey, oldHash: null, newHash: null, diff: null };
}

const edited = { kind: "element" as const, key: "e:customer", detail: "Customer (entity) changed", elementId: "customer", path: null };
const template = { kind: "template" as const, key: "t:sql-ddl/table.scriban", detail: "Template table.scriban changed", elementId: null, path: null };

const plan = {
  packs: ["sql-ddl", "csharp-dapper"],
  units: [
    unit("sql-ddl/table:billing.customers", { reason: "inputs", causes: [edited, template], causeCount: 2 }),
    unit("sql-ddl/table:billing.orders", { reason: "inputs", causes: [template], causeCount: 1 }),
    unit("sql-ddl/table:billing.items", { reason: "unchanged", skipped: true }),
    unit("sql-ddl/schema:main"),
    unit("csharp-dapper/entity:customer", {
      reason: "outputs",
      causes: [{ ...edited, kind: "output-edited", detail: "src/Customer.cs was edited on disk" }],
      causeCount: 1,
    }),
  ],
  changes: [
    change("db/main/tables/orders.sql", "modified", "sql-ddl/table:billing.orders"),
    change("db/main/tables/customers.sql", "modified", "sql-ddl/table:billing.customers"),
    change("db/main/schema.sql", "added", "sql-ddl/schema:main"),
    change("db/main/tables/old.sql", "deleted", "sql-ddl/(removed)"),
    change("src/Customer.cs", "hand-edited", "csharp-dapper/entity:customer"),
    change("db/main/tables/items.sql", "unchanged", "sql-ddl/table:billing.items"),
  ],
};

describe("plan explanation model", () => {
  it("groups changes by unit definition, ordinal by pack then unit, with counts and rendering units", () => {
    const groups = groupPlan(plan);
    expect(groups.map((g) => g.id)).toEqual(["csharp-dapper/entity", "sql-ddl/(orphans)", "sql-ddl/schema", "sql-ddl/table"]);
    const table = groups.find((g) => g.id === "sql-ddl/table")!;
    expect(table.template).toBe("table.scriban");
    expect(table.rows.map((r) => r.change.path)).toEqual(["db/main/tables/customers.sql", "db/main/tables/items.sql", "db/main/tables/orders.sql"]);
    expect(table.counts).toEqual({ modified: 2, unchanged: 1 });
    expect([table.rendering, table.skipped]).toEqual([2, 1]);
    expect(countsText(table.counts)).toBe("2 to modify, 1 unchanged");
    const orphan = groups.find((g) => g.id === "sql-ddl/(orphans)")!;
    expect(orphan.rows[0].unit).toBeNull();
    expect(orphan.rows[0].why).toMatch(/orphan/);
  });

  it("says why in one sentence: the first cause, +N for the rest, else the reason in words", () => {
    expect(whySentence(plan.units[0])).toBe("Customer (entity) changed +1");
    expect(whySentence(plan.units[1])).toBe("Template table.scriban changed");
    expect(whySentence(plan.units[3])).toMatch(/^New: no recorded state/);
    expect(whySentence(unit("a/b", { reason: null }))).toMatch(/not recorded/);
    expect(whySentence(unit("a/b", { causes: [edited], causeCount: 25 }))).toBe("Customer (entity) changed +24");
    expect(groupOf("sql-ddl/table:billing.customers")).toBe("sql-ddl/table");
    expect(groupOf("sql-ddl/model")).toBe("sql-ddl/model");
  });

  it("filters by kind, pack, unit and words, and flattens with collapsed groups", () => {
    const groups = groupPlan(plan);
    const all = { kind: "", pack: "", unit: "", text: "" };
    expect(filterGroups(groups, { ...all, kind: "changed" }).flatMap((g) => g.rows).length).toBe(5);
    expect(filterGroups(groups, { ...all, pack: "csharp-dapper" }).map((g) => g.id)).toEqual(["csharp-dapper/entity"]);
    expect(filterGroups(groups, { ...all, unit: "sql-ddl/table" }).flatMap((g) => g.rows).length).toBe(3);
    expect(filterGroups(groups, { ...all, text: "TEMPLATE orders" }).flatMap((g) => g.rows.map((r) => r.change.path))).toEqual(["db/main/tables/orders.sql"]);
    const shown = filterGroups(groups, { ...all, kind: "changed" });
    expect(flattenGroups(shown, new Set()).length).toBe(shown.length + 5);
    const items = flattenGroups(shown, new Set(["sql-ddl/table"]));
    expect(items.filter((i) => i.type === "group" && i.collapsed).length).toBe(1);
    expect(items.length).toBe(shown.length + 3);
  });

  it("writes the summary line per pack", () => {
    expect(planSummary(plan)).toEqual([
      "sql-ddl: 3 units, 1 file to add, 2 to modify, 1 orphan to delete, 1 file unchanged, 1 unit unchanged",
      "csharp-dapper: 1 unit, 1 file edited by hand",
    ]);
    const big = {
      units: [...Array(4)].map((_, i) => unit(`sql-ddl/table:t${i}`)),
      changes: [
        ...[...Array(12)].map((_, i) => change(`db/a${i}.sql`, "added", `sql-ddl/table:t${i % 4}`)),
        ...[...Array(3)].map((_, i) => change(`db/m${i}.sql`, "modified", `sql-ddl/table:t${i}`)),
        change("db/gone.sql", "deleted", "sql-ddl/(removed)"),
      ],
    };
    expect(packSummaryLine("sql-ddl", big)).toBe("sql-ddl: 4 units, 12 files to add, 3 to modify, 1 orphan to delete");
    const quiet = { units: [unit("sql-ddl/table:t", { skipped: true, reason: "unchanged" })], changes: [change("db/t.sql", "unchanged", "sql-ddl/table:t")] };
    expect(packSummaryLine("sql-ddl", quiet)).toBe("sql-ddl: 0 units, nothing to write: 1 file already matches the disk, 1 unit unchanged");
  });

  it("says why a plan writes nothing: every file it renders already matches the disk", () => {
    const units = [...Array(737)].map((_, i) => unit(`atlas-schema/table:t${i}`));
    const same = { units, changes: units.map((u, i) => change(`db/t${i}.sql`, "unchanged", u.key)) };
    expect(packSummaryLine("atlas-schema", same)).toBe("atlas-schema: 737 units, nothing to write: all 737 files already match the disk");
    expect(nothingToWrite(same)).toBe(true);
    expect(nothingToWriteNote(same)).toBe("Every file this plan renders is identical to the file on disk; Apply has nothing to do.");
    const kept = { units, changes: [...same.changes.slice(1), change("db/t0.sql", "kept", units[0].key)] };
    expect(packSummaryLine("atlas-schema", kept)).toBe("atlas-schema: 737 units, nothing to write: all 736 files already match the disk, 1 file kept");
    expect(nothingToWriteNote(kept)).toContain("or kept as it is");
    expect(nothingToWriteNote({ changes: [] })).toBe("This plan renders no files; Apply has nothing to do.");
    expect(nothingToWrite(plan)).toBe(false);
    expect(nothingToWriteNote(plan)).toBeNull();
  });
});

describe("plan diagnostics under the summary", () => {
  const note = (severity: string, rule: string, n: number) => ({ severity, rule, message: `${rule} #${n}` });
  it("lists errors first, then warnings, then notes, keeping the engine's order inside a severity", () => {
    const { shown, more } = orderDiagnostics([note("info", "MQ7204", 1), note("warning", "MQ6024", 2), note("info", "MQ7204", 3), note("error", "MQ6018", 4)]);
    expect(shown.map((d) => d.message)).toEqual(["MQ6018 #4", "MQ6024 #2", "MQ7204 #1", "MQ7204 #3"]);
    expect(more).toBe(0);
  });
  it("caps the list and counts the rest so notes never hide a warning", () => {
    const many = [...Array.from({ length: 7 }, (_, i) => note("info", "MQ7204", i)), note("warning", "MQ6024", 9)];
    const { shown, more, counts } = orderDiagnostics(many);
    expect(shown[0].severity).toBe("warning");
    expect(shown).toHaveLength(5);
    expect(more).toBe(3);
    expect(moreNotesText(more, counts)).toBe("+3 more in Problems (1 warning, 7 notes in all)");
  });
});
