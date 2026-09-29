// The mock validator follows the engine's domain vocabularies (explorer-redesign.md section 1.11):
// tags and categories resolve along the element's chain (MQ2006), one declared only outside it is
// MQ2008, and a domain vocabulary may not redeclare a key or name of an enclosing scope (MQ3021).
import { describe, expect, it } from "vitest";
import { entryDiagnostics, globalsOf, type ModelEntry, type ValidationContext } from "@/mocks/model/validate";

const ROOT = "01J92P0V0000000000000000A1";
const CHILD = "01J92P0V0000000000000000A2";
const OTHER = "01J92P0V0000000000000000A3";

function entry(id: string, json: Record<string, unknown>): ModelEntry {
  return { id, path: `model/${id}.json`, json: { id, ...json } };
}

const entries: ModelEntry[] = [
  entry(ROOT, { kind: "package", name: "Sales" }),
  entry(CHILD, { kind: "package", name: "Orders", parent: ROOT }),
  entry(OTHER, { kind: "package", name: "Billing" }),
  entry("01J92P0V0000000000000000B1", { kind: "tag-vocabulary", name: "tags", definitions: [{ key: "audited" }] }),
  entry("01J92P0V0000000000000000B2", { kind: "tag-vocabulary", name: "sales-tags", package: ROOT, definitions: [{ key: "pii" }, { key: "audited" }] }),
  entry("01J92P0V0000000000000000B3", { kind: "tag-vocabulary", name: "billing-tags", package: OTHER, definitions: [{ key: "ledger" }] }),
  entry("01J92P0V0000000000000000C1", { kind: "category-tree", name: "billing-categories", package: OTHER, categories: [{ id: "01J92P0V0000000000000000C2", name: "Core" }] }),
];

function rules(target: ModelEntry): { rule: string; message: string }[] {
  const all = [...entries, target];
  const ctx: ValidationContext = {
    ...globalsOf(all),
    hasId: (id) => all.some((e) => e.id === id),
    extensions: [],
    firstInScope: () => undefined,
  };
  return entryDiagnostics(target, ctx)
    .filter((d) => ["MQ2005", "MQ2006", "MQ2008", "MQ3021"].includes(d.rule))
    .map((d) => ({ rule: d.rule, message: d.message }));
}

describe("mock domain vocabularies", () => {
  it("resolves tags along the chain and reports a tag or category from outside it as MQ2008", () => {
    const inChild = entry("01J92P0V0000000000000000D1", { kind: "entity", name: "Order", package: CHILD, tags: ["pii", "audited", "ledger", "nowhere"], category: "01J92P0V0000000000000000C2" });
    const found = rules(inChild);
    expect(found.map((d) => d.rule)).toEqual(["MQ2008", "MQ2008", "MQ2006"]);
    expect(found[0].message).toContain("domain 'Billing'");
    expect(found[2].message).toContain("of any domain on its chain");
  });

  it("reports a domain vocabulary key that the global vocabulary already declares as MQ3021", () => {
    const found = rules(entries[4]);
    expect(found).toEqual([{ rule: "MQ3021", message: "Tag 'audited' is already declared by the model (global); a domain vocabulary cannot redeclare it." }]);
  });
});
