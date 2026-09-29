// The entity editor's Inheritance tab and Virtual rows, the relationship editor's Mappings tab (editors/inheritance.ts),
// the New relationship dialog's On delete default, the Database screen's list, the Move to domain… scope warning
// (model/vocabularies.ts) and the reference type menu (workspaces/reference-data/typeMenu.ts, explorer/menus.ts).
import { describe, expect, it } from "vitest";
import type { AttributeDoc, ElementSummary, ReferenceTypeDoc, SeedDoc } from "@/api/types";
import { fieldSources, inHierarchy, inheritanceRows, relationMappingRows, virtualAttributes } from "@/editors/inheritance";
import { menuFor } from "@/explorer/menus";
import { summaryFromDocument } from "@/model/model";
import { marksLeavingScope } from "@/model/vocabularies";
import { databaseList } from "@/workspaces/database/tableList";
import { defaultOnDelete } from "@/workspaces/entities/dialogs";
import { copyName, duplicateType, enumConversionProblem, enumFromType, retypeAttributes, typeMenuFor } from "@/workspaces/reference-data/typeMenu";

const DBS = [
  { id: "db1", name: "main" },
  { id: "db2", name: "archive" },
];

describe("the Inheritance tab", () => {
  it("is enabled only in a hierarchy", () => {
    expect(inHierarchy(null, 0)).toBe(false);
    expect(inHierarchy("base", 0)).toBe(true);
    expect(inHierarchy(undefined, 2)).toBe(true);
  });

  it("reads the strategy from the root's mapping, then the database's, then the project's conventions, then tph", () => {
    const rows = inheritanceRows({
      databases: [...DBS, { id: "db3", name: "reports" }],
      rootMappings: [{ database: "db1", inheritance: "tpt" }],
      ownMappings: [{ database: "db1", discriminatorValue: "card" }],
      conventions: { inheritance: "tpc" },
      perDatabase: { archive: { inheritance: "tph" } },
    });
    expect(rows.map((r) => [r.databaseName, r.strategy, r.source, r.discriminatorValue])).toEqual([
      ["main", "tpt", "mapping", "card"],
      ["archive", "tph", "database", undefined],
      ["reports", "tpc", "project", undefined],
    ]);
    expect(inheritanceRows({ databases: DBS, rootMappings: [], ownMappings: [] }).map((r) => r.source)).toEqual(["default", "default"]);
  });

  it("lists the attributes the entity's stereotypes add, in the entity's order, read-only", () => {
    const attr = (id: string, name: string) => ({ id, name, type: "string" }) as AttributeDoc;
    const stereotypes = [
      { key: "audited", attributes: [attr("a1", "createdAt"), attr("a2", "createdBy")] },
      { key: "versioned", attributes: [attr("v1", "version")] },
    ];
    expect(virtualAttributes(["versioned", "missing", "audited"], stereotypes).map((v) => `${v.stereotype}.${v.attribute.name}`)).toEqual([
      "versioned.version",
      "audited.createdAt",
      "audited.createdBy",
    ]);
    expect(virtualAttributes(undefined, stereotypes)).toEqual([]);
  });

  it("flattens a hierarchy as the resolver does: a base's stereotypes count, root first, first attribute id wins", () => {
    const attr = (id: string, name: string) => ({ id, name, type: "string" }) as AttributeDoc;
    const stereotypes = [
      { key: "audited", attributes: [attr("a1", "createdAt")] },
      { key: "tracked", attributes: [attr("a1", "createdAt"), attr("t1", "trackedBy")] },
    ];
    const sources = fieldSources(
      [
        { id: "Party", attributes: [attr("p1", "name")], stereotypes: ["audited"] },
        { id: "Person", attributes: [attr("x1", "born")], stereotypes: ["audited", "tracked"] },
      ],
      stereotypes,
    );
    expect(sources.map((s) => `${s.from}:${s.stereotype ?? ""}:${s.attribute.name}`)).toEqual([
      "Party::name",
      "Party:audited:createdAt",
      "Person::born",
      "Person:tracked:trackedBy",
    ]);
  });
});

describe("the relationship editor's Mappings tab", () => {
  it("shows per database the relation's customised mapping and its junction table", () => {
    const rows = relationMappingRows(
      "rel",
      DBS,
      [
        { id: "m1", database: "db1", relation: "rel", shape: "junction" },
        { id: "m2", database: "db2", relation: "other", shape: "promoted" },
      ],
      (db) =>
        db === "db1"
          ? [
              { key: "t1", name: "order_tags", relationId: "rel" },
              { key: "t2", name: "orders", relationId: null },
            ]
          : undefined,
    );
    expect(rows.map((r) => [r.databaseName, r.shape, r.mapping?.id ?? null, r.tables.map((t) => t.name)])).toEqual([
      ["main", "junction", "m1", ["order_tags"]],
      ["archive", null, null, []],
    ]);
  });
});

describe("demo polish", () => {
  it("keeps a saved entity's base and a saved relation's ends in its index row", () => {
    const doc = (json: object) => ({ json, hash: "h", path: "p" }) as never;
    expect(summaryFromDocument(doc({ kind: "entity", id: "E", name: "Card", base: "B", displayName: "Card" }))).toMatchObject({
      base: "B",
      displayName: "Card",
    });
    expect(
      summaryFromDocument(
        doc({
          kind: "relation",
          id: "R",
          name: "R",
          ends: [
            { entity: "A", role: "a" },
            { entity: "B", role: "b" },
          ],
        }),
      ).ends,
    ).toEqual([
      { entity: "A", role: "a" },
      { entity: "B", role: "b" },
    ]);
  });

  it("defaults On delete to cascade for a composition and to restrict otherwise (SPEC 6)", () => {
    expect(defaultOnDelete("composition")).toBe("cascade");
    expect(defaultOnDelete("aggregation")).toBe("restrict");
    expect(defaultOnDelete("association")).toBe("restrict");
  });

  it("lists a database created a moment ago before the project is read again", () => {
    const row = (id: string, name: string, kind = "database") => ({ id, name, kind });
    const project = [row("b", "main")];
    const index = [row("e", "Customer", "entity"), row("n", "archive"), row("b", "main")];
    expect(databaseList(project, index).map((d) => d.name)).toEqual(["main", "archive"]);
    expect(databaseList(project, undefined).map((d) => d.name)).toEqual(["main"]);
    expect(databaseList([], [row("n", "archive")]).map((d) => d.name)).toEqual(["archive"]);
  });
});

describe("Move to domain… scope warning (explorer-redesign.md 1.11)", () => {
  const rows = [
    { id: "billing", kind: "package", name: "Billing", package: null },
    { id: "sales", kind: "package", name: "Sales", package: null },
    { id: "invoicing", kind: "package", name: "Invoicing", package: "billing" },
    { id: "gtags", kind: "tag-vocabulary", name: "tags", package: null },
    { id: "btags", kind: "tag-vocabulary", name: "billing tags", package: "billing" },
    { id: "bcats", kind: "category-tree", name: "billing categories", package: "billing" },
  ] as ElementSummary[];
  const docs: Record<string, unknown> = {
    gtags: { definitions: [{ key: "core" }] },
    btags: { definitions: [{ key: "ledger" }] },
    bcats: { categories: [{ id: "c-fin", name: "Finance" }] },
  };
  const element = { name: "Invoice", tags: ["core", "ledger", "undeclared"], category: "c-fin" };

  it("reports the marks a domain vocabulary declares that the target's chain does not offer", () => {
    expect(marksLeavingScope([element], "sales", rows, (id) => docs[id])).toEqual([{ name: "Invoice", tags: ["ledger"], category: "c-fin" }]);
    expect(marksLeavingScope([element], null, rows, (id) => docs[id])).toEqual([{ name: "Invoice", tags: ["ledger"], category: "c-fin" }]);
  });

  it("reports nothing when the target is under the declaring domain", () => {
    expect(marksLeavingScope([element], "invoicing", rows, (id) => docs[id])).toEqual([]);
  });
});

describe("the reference type menu", () => {
  const type = {
    kind: "reference-type",
    id: "T",
    name: "Currency",
    displayName: "Currency",
    category: "c1",
    code: { id: "C" },
    label: { id: "L" },
  } as ReferenceTypeDoc;
  const seed = (rows: [string, string][]): SeedDoc =>
    ({
      kind: "seed",
      id: "S",
      name: "Currency",
      target: "T",
      columns: ["code", "label"],
      rows: rows.map(([c, l], i) => ({ id: `R${i}`, values: [c, l] })),
    }) as SeedDoc;

  it("offers every action for one type and the multi ones for several; Export CSV needs a seed", () => {
    expect(typeMenuFor([{ seeds: ["S"] }]).map((i) => i.id)).toEqual([
      "duplicate",
      "rename",
      "move-category",
      "set-storage",
      "export-csv",
      "convert-to-enum",
      "delete",
    ]);
    expect(typeMenuFor([{ seeds: [] }]).some((i) => i.id === "export-csv")).toBe(false);
    expect(typeMenuFor([{ seeds: ["S"] }, { seeds: [] }]).map((i) => i.id)).toEqual(["move-category", "delete"]);
  });

  it("offers the same actions on the explorer's reference type rows, Delete last", () => {
    const items = menuFor([{ type: "element", kind: "reference-type", element: true }] as never).map((i) => i.id);
    expect(items[0]).toBe("open");
    expect(items).toContain("type:duplicate");
    expect(items.at(-1)).toBe("type:delete");
    expect(items).not.toContain("delete");
  });

  it("names a copy after the first free CurrencyCopy", () => {
    expect(copyName("Currency", ["Currency"])).toBe("CurrencyCopy");
    expect(copyName("Currency", ["Currency", "currencycopy"])).toBe("CurrencyCopy2");
  });

  it("duplicates the type and its seeds under new ids", () => {
    let n = 0;
    const copy = duplicateType(type, [seed([["EUR", "Euro"]])], "CurrencyCopy", () => `N${++n}`);
    expect(copy.type).toMatchObject({ id: "N1", name: "CurrencyCopy", code: { id: "N2" }, label: { id: "N3" }, category: "c1" });
    expect(copy.seeds[0]).toMatchObject({ id: "N4", name: "CurrencyCopy", target: "N1", rows: [{ id: "N5", values: ["EUR", "Euro"] }] });
    expect(type.id).toBe("T");
  });

  it("converts to an enum only without own fields and with identifier codes", () => {
    expect(enumConversionProblem(type, [seed([["EUR", "Euro"]])])).toBeNull();
    expect(enumConversionProblem(type, [seed([["US-D", "Dollar"]])])).toMatch(/not an identifier/);
    expect(enumConversionProblem(type, [seed([["EUR", "Euro"]]), seed([["EUR", "Euro again"]])])).toMatch(/twice/);
    expect(enumConversionProblem({ ...type, attributes: [{ id: "x", name: "symbol", type: "string" }] } as ReferenceTypeDoc, [])).toMatch(/fields of its own/);
    let n = 0;
    const made = enumFromType(
      type,
      [
        seed([
          ["EUR", "Euro"],
          ["USD", "USD"],
        ]),
      ],
      () => `E${++n}`,
    );
    expect(made).toMatchObject({ kind: "enum", id: "E3", name: "Currency", category: "c1" });
    expect(made.members).toEqual([
      { id: "E1", name: "EUR", displayName: "Euro" },
      { id: "E2", name: "USD" },
    ]);
  });

  it("points the attributes typed by the reference type at the enum", () => {
    const entity = {
      attributes: [
        { id: "a", type: { ref: "T" } },
        { id: "b", type: "string" },
        { id: "c", type: { ref: "X" } },
      ],
    };
    expect(retypeAttributes(entity, "T", "E")).toBe(true);
    expect(entity.attributes.map((a) => a.type)).toEqual([{ ref: "E" }, "string", { ref: "X" }]);
    expect(retypeAttributes(entity, "T", "E")).toBe(false);
  });
});
