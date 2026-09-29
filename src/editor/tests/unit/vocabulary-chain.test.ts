// Domain vocabularies in the editor (explorer-redesign.md 1.11, EX step 16): an element in Billing › Catalog is
// offered Catalog's, then Billing's, then the global vocabularies, and never Sales's; a domain entry names its domain;
// the filter offers the chain of its domain chip, else every vocabulary; MQ2008 and MQ3021 lead to the right place.
import { describe, expect, it } from "vitest";
import type { ElementSummary } from "@/api/types";
import {
  categoryOptions,
  chipLabel,
  commonDomain,
  domainChain,
  filterVocabularies,
  markDomainOf,
  ownVocabulary,
  tagOptions,
  vocabulariesOnChain,
  vocabularyProblemTarget,
} from "@/model/vocabularies";

const row = (id: string, kind: string, name: string, pkg: string | null = null) => ({ id, kind, name, package: pkg }) as unknown as ElementSummary;
const rows = [
  row("P1", "package", "Billing"),
  row("P2", "package", "Catalog", "P1"),
  row("P3", "package", "Sales"),
  row("P4", "package", "Loop", "P5"),
  row("P5", "package", "Back", "P4"),
  row("T0", "tag-vocabulary", "tags"),
  row("T1", "tag-vocabulary", "billing-tags", "P1"),
  row("T2", "tag-vocabulary", "catalog-tags", "P2"),
  row("T3", "tag-vocabulary", "sales-tags", "P3"),
  row("C0", "category-tree", "categories"),
  row("C1", "category-tree", "billing-categories", "P1"),
  row("E1", "entity", "Product", "P2"),
];
const docs: Record<string, unknown> = {
  T0: { definitions: [{ key: "pii" }] },
  T1: { definitions: [{ key: "ledger" }, { key: "core" }] },
  T2: { strict: true, definitions: [{ key: "sku" }, { key: "core" }] },
  T3: { definitions: [{ key: "funnel" }] },
  C0: { categories: [{ id: "G1", name: "Master data" }] },
  C1: {
    categories: [
      { id: "B1", name: "Ledgers" },
      { id: "B2", name: "Journals", parent: "B1" },
    ],
  },
};
const docOf = (id: string) => docs[id];

describe("the vocabulary chain", () => {
  it("walks the domain's chain nearest first and stops on a cycle", () => {
    const byId = new Map(rows.map((r) => [r.id, r]));
    expect(domainChain("P2", byId)).toEqual(["P2", "P1"]);
    expect(domainChain("P4", byId)).toEqual(["P4", "P5"]);
    expect(domainChain(null, byId)).toEqual([]);
  });

  it("offers Catalog's, then Billing's, then the global tags, never Sales's, each naming its domain", () => {
    const chain = vocabulariesOnChain("tag-vocabulary", "P2", rows);
    expect(chain.map((v) => [v.id, v.domain])).toEqual([
      ["T2", "Catalog"],
      ["T1", "Billing"],
      ["T0", null],
    ]);
    const { options, strict } = tagOptions(chain, docOf);
    expect(options.map((o) => o.label)).toEqual(["sku · Catalog", "core · Catalog", "ledger · Billing", "pii"]);
    expect(options.map((o) => o.value)).not.toContain("funnel");
    expect(strict).toBe(true);
    expect(chipLabel("core", options)).toBe("core · Catalog");
    expect(chipLabel("unknown", options)).toBe("unknown");
  });

  it("offers only the global vocabularies to an element with no domain", () => {
    expect(vocabulariesOnChain("tag-vocabulary", null, rows).map((v) => v.id)).toEqual(["T0"]);
    expect(tagOptions(vocabulariesOnChain("tag-vocabulary", null, rows), docOf).strict).toBe(false);
  });

  it("offers categories along the chain with their parents", () => {
    const options = categoryOptions(vocabulariesOnChain("category-tree", "P2", rows), docOf);
    expect(options.map((o) => [o.value, o.label, o.parent])).toEqual([
      ["B1", "Ledgers · Billing", null],
      ["B2", "Journals · Billing", "B1"],
      ["G1", "Master data", null],
    ]);
  });

  it("finds a scope's own vocabulary, and the domain an element's marks resolve along", () => {
    expect(ownVocabulary("tag-vocabulary", "P1", rows)?.id).toBe("T1");
    expect(ownVocabulary("tag-vocabulary", null, rows)?.id).toBe("T0");
    expect(ownVocabulary("category-tree", "P2", rows)).toBeUndefined();
    expect(markDomainOf({ kind: "package", id: "P2", parent: "P1" } as never)).toBe("P2");
    expect(markDomainOf({ kind: "entity", id: "E1", package: "P2" })).toBe("P2");
    expect(markDomainOf({ kind: "entity", id: "E2" })).toBeNull();
  });

  it("bulk tagging offers the nearest domain every element is in", () => {
    expect(commonDomain(["P2", "P2"], rows)).toBe("P2");
    expect(commonDomain(["P2", "P1"], rows)).toBe("P1");
    expect(commonDomain(["P2", "P3"], rows)).toBeNull();
    expect(commonDomain(["P2", null], rows)).toBeNull();
  });
});

describe("the filter's vocabularies", () => {
  it("follows the domain chip's chain, else offers the global ones then every domain's by name", () => {
    expect(filterVocabularies("tag-vocabulary", "P2", rows).map((v) => v.id)).toEqual(["T2", "T1", "T0"]);
    expect(filterVocabularies("tag-vocabulary", null, rows).map((v) => v.id)).toEqual(["T0", "T1", "T2", "T3"]);
    const all = tagOptions(filterVocabularies("tag-vocabulary", null, rows), docOf).options;
    expect(all.map((o) => o.label)).toEqual(["pii", "ledger · Billing", "core · Billing", "sku · Catalog", "funnel · Sales"]);
  });
});

describe("Problems' Go to for MQ2008 and MQ3021", () => {
  it("leads to the element, the domain's tab, or the Settings tab", () => {
    expect(vocabularyProblemTarget("MQ2008", "E1", rows)).toEqual({ type: "element", id: "E1" });
    expect(vocabularyProblemTarget("MQ3021", "T1", rows)).toEqual({ type: "domain", domain: "P1", tab: "tags" });
    expect(vocabularyProblemTarget("MQ3021", "C1", rows)).toEqual({ type: "domain", domain: "P1", tab: "categories" });
    expect(vocabularyProblemTarget("MQ3021", "C0", rows)).toEqual({ type: "settings", tab: "categories" });
    expect(vocabularyProblemTarget("MQ2006", "E1", rows)).toBeNull();
    expect(vocabularyProblemTarget("MQ2008", null, rows)).toBeNull();
  });
});
