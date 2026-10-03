// The plan explanation's model (generation-ui.md 4): grouping by unit with counts, the Why sentence, the outcome chips and
// other filters, the summary line per pack and the plan result line from the plan's counts.
import { describe, expect, it } from "vitest";
import type { FileChange, PlanUnit } from "@/api/types";
import {
  countsText,
  KIND_LABEL,
  KIND_TITLE,
  kindCounts,
  OUTCOME_CHIPS,
  outcomeCounts,
  QUIET_FILES,
  filterGroups,
  flattenGroups,
  groupOf,
  groupPlan,
  modelFindingsLine,
  moreNotesText,
  nothingToWrite,
  nothingToWriteNote,
  orderDiagnostics,
  packSummaryLine,
  planReadyText,
  planSummary,
  splitPlanDiagnostics,
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
    expect(countsText(table.counts)).toBe("2 to modify, 1 identical");
    expect(countsText({ added: 1, "not-rendered": 3, kept: 2 })).toBe("1 to add, 3 not re-rendered, 2 yours");
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

  it("filters by outcome, pack, unit and words, and flattens with collapsed groups", () => {
    const groups = groupPlan(plan);
    const all = { outcome: "" as const, pack: "", unit: "", text: "" };
    expect(filterGroups(groups, all).flatMap((g) => g.rows).length).toBe(6);
    expect(filterGroups(groups, { ...all, outcome: "write" }).flatMap((g) => g.rows.map((r) => r.change.kind))).toEqual([
      "deleted",
      "added",
      "modified",
      "modified",
    ]);
    expect(filterGroups(groups, { ...all, outcome: "identical" }).flatMap((g) => g.rows.map((r) => r.change.path))).toEqual(["db/main/tables/items.sql"]);
    expect(filterGroups(groups, { ...all, outcome: "hand" }).map((g) => g.id)).toEqual(["csharp-dapper/entity"]);
    expect(filterGroups(groups, { ...all, outcome: "yours" })).toEqual([]);
    expect(filterGroups(groups, { ...all, pack: "csharp-dapper" }).map((g) => g.id)).toEqual(["csharp-dapper/entity"]);
    expect(filterGroups(groups, { ...all, unit: "sql-ddl/table" }).flatMap((g) => g.rows).length).toBe(3);
    expect(filterGroups(groups, { ...all, text: "TEMPLATE orders" }).flatMap((g) => g.rows.map((r) => r.change.path))).toEqual(["db/main/tables/orders.sql"]);
    const shown = filterGroups(groups, all);
    expect(flattenGroups(shown, new Set()).length).toBe(shown.length + 6);
    const items = flattenGroups(shown, new Set(["sql-ddl/table"]));
    expect(items.filter((i) => i.type === "group" && i.collapsed).length).toBe(1);
    expect(items.length).toBe(shown.length + 3);
  });

  it("writes the summary line per pack", () => {
    expect(planSummary(plan)).toEqual([
      "sql-ddl: 3 units, 1 file to add, 2 to modify, 1 orphan to delete, 1 file identical, 1 unit unchanged",
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
    const incremental = {
      units: [unit("sql-ddl/table:t", { skipped: true, reason: "unchanged" }), unit("sql-ddl/migration:main", { skipped: true, reason: "unchanged" })],
      changes: [
        change("db/t.sql", "not-rendered", "sql-ddl/table:t"),
        change("db/u.sql", "not-rendered", "sql-ddl/table:t"),
        change("db/m/0001.sql", "kept", "sql-ddl/migration:main"),
      ],
    };
    expect(packSummaryLine("sql-ddl", incremental)).toBe(
      "sql-ddl: 0 units, nothing to write: 2 files not re-rendered (inputs unchanged), 1 file yours, 2 units unchanged",
    );
  });

  it("says why a plan writes nothing: every file it renders already matches the disk", () => {
    const units = [...Array(737)].map((_, i) => unit(`atlas-schema/table:t${i}`));
    const same = { units, changes: units.map((u, i) => change(`db/t${i}.sql`, "unchanged", u.key)) };
    expect(packSummaryLine("atlas-schema", same)).toBe("atlas-schema: 737 units, nothing to write: all 737 files already match the disk");
    expect(nothingToWrite(same)).toBe(true);
    expect(nothingToWriteNote(same)).toBe("Every file in this plan is identical to the file on disk; Apply has nothing to do.");
    const kept = { units, changes: [...same.changes.slice(1), change("db/t0.sql", "kept", units[0].key)] };
    expect(packSummaryLine("atlas-schema", kept)).toBe("atlas-schema: 737 units, nothing to write: 736 files already match the disk, 1 file yours");
    expect(nothingToWriteNote(kept)).toBe(
      "Every file in this plan is identical to the file on disk or yours (written once, never overwritten); Apply has nothing to do.",
    );
    const later = { changes: [change("a", "not-rendered", "p/u"), change("b", "kept", "p/u")] };
    expect(nothingToWrite(later)).toBe(true);
    expect(nothingToWriteNote(later)).toBe(
      "Every file in this plan is not re-rendered because its inputs did not change or yours (written once, never overwritten); Apply has nothing to do.",
    );
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
    expect(moreNotesText(more, counts)).toBe("+3 more (1 warning, 7 notes in all)");
  });
});

describe("the plan result line", () => {
  const changes = (kinds: FileChange["kind"][]) => kinds.map((k, i) => change(`f${i}`, k, "sql-ddl/table:t"));
  const zero = { added: 0, modified: 0, deleted: 0, unchanged: 0, "hand-edited": 0, kept: 0, "orphaned-owned": 0, conflict: 0, "not-rendered": 0 };
  it("counts what Apply writes, then what it leaves alone, from the plan's counts", () => {
    // A forced plan: everything rendered, so nothing is "not re-rendered".
    expect(planReadyText({ changes: [], counts: { ...zero, added: 1, modified: 2, unchanged: 58, kept: 6 } })).toBe(
      "Plan ready: 3 files to write (1 added, 2 modified); 58 identical, 6 yours",
    );
    // An incremental plan.
    expect(planReadyText({ changes: [], counts: { ...zero, added: 1, modified: 2, unchanged: 2, kept: 6, "not-rendered": 40 } })).toBe(
      "Plan ready: 3 files to write (1 added, 2 modified); 2 identical, 40 not re-rendered (inputs unchanged), 6 yours",
    );
    expect(planReadyText({ changes: changes(["added"]) })).toBe("Plan ready: 1 file to write (1 added)");
    // A plan stored before counts is counted from its files.
    expect(planReadyText({ changes: changes(["added", "added", "modified", "deleted", "unchanged", "unchanged", "kept"]), counts: {} })).toBe(
      "Plan ready: 4 files to write (2 added, 1 modified, 1 deleted); 2 identical, 1 yours",
    );
  });
  it("says nothing to write, and what the files are, when Apply has nothing to do", () => {
    expect(planReadyText({ changes: [], counts: { ...zero, "not-rendered": 650, kept: 6 } })).toBe(
      "Plan ready: nothing to write; 650 not re-rendered (inputs unchanged), 6 yours",
    );
    expect(planReadyText({ changes: changes(["unchanged", "kept"]) })).toBe("Plan ready: nothing to write; 1 identical, 1 yours");
    expect(planReadyText({ changes: [] })).toBe("Plan ready: nothing to write");
  });
  it("counts the files edited by hand first after the semicolon, and never names the policy", () => {
    expect(planReadyText({ changes: changes(["modified", "unchanged", "hand-edited", "conflict"]) })).toBe(
      "Plan ready: 1 file to write (1 modified); 1 edited by hand, 1 in conflict (edited by hand), 1 identical",
    );
    expect(planReadyText({ changes: changes(["conflict", "conflict"]) })).toBe("Plan ready: nothing to write; 2 in conflict (edited by hand)");
  });
});

describe("the outcome chips and badges", () => {
  it("count every kind into its chip, All being every file", () => {
    const counts = kindCounts({
      changes: [],
      counts: { added: 1, modified: 2, deleted: 1, unchanged: 5, "hand-edited": 1, kept: 3, "orphaned-owned": 1, conflict: 1, "not-rendered": 7 },
    });
    expect(outcomeCounts(counts)).toEqual({ "": 22, write: 4, identical: 5, "not-rendered": 7, yours: 4, hand: 2 });
    expect(OUTCOME_CHIPS.map((c) => c.label)).toEqual(["All", "To write", "Identical", "Not re-rendered", "Yours", "Edited by hand"]);
    expect(OUTCOME_CHIPS.every((c) => c.title.length > 0)).toBe(true);
  });
  it("label the kinds Apply leaves alone and say why in the tooltip", () => {
    expect([KIND_LABEL.unchanged, KIND_LABEL["not-rendered"], KIND_LABEL.kept]).toEqual(["identical", "not re-rendered", "yours"]);
    expect(KIND_TITLE.kept).toBe("Written once; yours to edit. Generation never overwrites it.");
    expect(KIND_TITLE["not-rendered"]).toBe("Its inputs did not change since the last run; Re-render every file renders it again");
    // No diff is fetched for them: the diff panel says this instead.
    expect(QUIET_FILES.kept?.text).toBe(KIND_TITLE.kept);
    expect(Object.keys(QUIET_FILES).sort()).toEqual(["kept", "not-rendered", "unchanged"]);
  });
});

describe("plan diagnostics against the Problems panel", () => {
  const d = (rule: string, severity: string, filePath: string | null, elementId: string | null = null, jsonPointer: string | null = "") => ({
    rule,
    severity,
    message: `${rule} message`,
    elementId,
    filePath,
    jsonPointer,
  });
  it("keeps only what the validation report does not hold, matching rule, element, file and pointer", () => {
    const plan = [
      d("MQ1003", "warning", ".maquettiste/maquettiste.json"),
      d("MQ1010", "info", ".maquettiste/maquettiste.json", null, "/outputs/allow/0/commit"),
      d("MQ9004", "warning", ".maquettiste/model/processes/a.json", "P1", "/states/0"),
      d("MQ6024", "warning", "db/x.sql"),
      d("MQ9004", "warning", ".maquettiste/model/processes/a.json", "P1", "/states/1"),
    ];
    const report = [
      d("MQ1003", "warning", ".maquettiste/maquettiste.json"),
      d("MQ1010", "info", ".maquettiste/maquettiste.json", null, "/outputs/allow/0/commit"),
      // A severity override in the report does not make it another finding.
      d("MQ9004", "error", ".maquettiste/model/processes/a.json", "P1", "/states/0"),
    ];
    const { generation, model, counts } = splitPlanDiagnostics(plan, report);
    expect(generation.map((x) => `${x.rule} ${x.jsonPointer}`)).toEqual(["MQ6024 ", "MQ9004 /states/1"]);
    expect(model).toHaveLength(3);
    expect(counts).toEqual({ error: 0, warning: 2, info: 1 });
  });
  it("says how many model findings are in Problems, or that the plan stopped on model errors", () => {
    expect(modelFindingsLine({ error: 0, warning: 2, info: 1 }, false)).toBe("3 model findings (2 warnings, 1 note) are in Problems");
    expect(modelFindingsLine({ error: 0, warning: 1, info: 0 }, false)).toBe("1 model finding (1 warning) is in Problems");
    expect(modelFindingsLine({ error: 2, warning: 1, info: 0 }, true)).toBe("Plan stopped: 2 model errors, see Problems");
    expect(modelFindingsLine({ error: 1, warning: 0, info: 0 }, false)).toBe("1 model finding (1 error) is in Problems");
    expect(modelFindingsLine({ error: 0, warning: 0, info: 0 }, true)).toBeNull();
  });
});
