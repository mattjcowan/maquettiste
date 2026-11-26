// The Copy tab's logic (editors/copyEdit.ts): what each choice writes to the entity's `copy` block, entries inherited from a base, the words
// an attribute may take, the parts tree, and the type column controls of the Storage tab (editors/storage/typeColumn.ts).
import { describe, expect, it } from "vitest";
import type { ElementSummary } from "@/api/types";
import {
  choiceOf,
  copyAttributes,
  inheritedEntries,
  isOrderable,
  linkEnds,
  mergedEntries,
  partTree,
  setCopyWord,
  setPartAttributeWord,
  setPartChoice,
  setPartWord,
  type Json,
} from "@/editors/copyEdit";
import { inheritedTypeColumn, readCodes, setTypeCode, setTypeColumn, typeColumnRole } from "@/editors/storage/typeColumn";
import { referrersOf } from "@/editors/refWalk";
import type { ReferenceInfo } from "@/api/types";
import type { Binding, SourceColumn } from "@/editors/storage/fieldMap";

const attr = (id: string, extra: Json = {}): Json => ({ id, name: id, type: "string", ...extra });

describe("fields and links", () => {
  it("writes only what differs from the default, and removes an entry set back to it", () => {
    const entity: Json = { id: "E", name: "E" };
    setCopyWord(entity, "attributes", "a", "reset", undefined);
    setCopyWord(entity, "attributes", "b", "clear", undefined);
    expect(entity.copy).toEqual({
      attributes: [
        { attribute: "a", onCopy: "reset" },
        { attribute: "b", onCopy: "clear" },
      ],
    });
    setCopyWord(entity, "attributes", "a", "copy", undefined);
    expect(entity.copy).toEqual({ attributes: [{ attribute: "b", onCopy: "clear" }] });
    setCopyWord(entity, "attributes", "b", "copy", undefined);
    expect(entity.copy).toBeUndefined();
  });

  it("writes `copy` to undo a base's entry, and shows the entry as inherited", () => {
    const base: Json = { id: "B", copy: { attributes: [{ attribute: "a", onCopy: "reset" }], links: [{ end: "x", onCopy: "original" }] } };
    const entity: Json = { id: "E", base: "B" };
    const chain = [entity, base];
    expect(mergedEntries(chain, "attributes", "attribute").get("a")).toMatchObject({ from: "B", entry: { onCopy: "reset" } });
    const inherited = inheritedEntries(chain, "attributes", "attribute").get("a")?.entry.onCopy as string;
    setCopyWord(entity, "attributes", "a", "copy", inherited);
    expect(entity.copy).toEqual({ attributes: [{ attribute: "a", onCopy: "copy" }] });
    expect(mergedEntries([entity, base], "attributes", "attribute").get("a")).toMatchObject({ from: "E", entry: { onCopy: "copy" } });
    setCopyWord(entity, "links", "x", "copy", "original");
    expect(entity.copy).toMatchObject({ links: [{ end: "x", onCopy: "copy" }] });
  });

  it("lists own and inherited attributes, root first, never keys or derived ones, with the words that fit", () => {
    const base: Json = { id: "B", key: { attributes: ["id"] }, attributes: [attr("id", { type: "uuid", required: true }), attr("title", { required: true })] };
    const entity: Json = {
      id: "E",
      base: "B",
      attributes: [
        attr("note"),
        attr("status", { required: true, default: "Draft" }),
        attr("copiedFrom", { type: "uuid" }),
        attr("sum", { derived: { expression: "1" } }),
      ],
    };
    const list = copyAttributes([entity, base]);
    expect(list.map((a) => a.name)).toEqual(["title", "note", "status", "copiedFrom"]);
    expect(Object.fromEntries(list.map((a) => [a.name, a.words]))).toEqual({
      title: ["copy"], // required, no default: neither reset nor clear
      note: ["copy", "reset", "clear"],
      status: ["copy", "reset"], // required with a default
      copiedFrom: ["copy", "reset", "clear", "original"], // typed as the key
    });
  });

  it("offers links whose other end covers the entity and original only where the end's type covers it too", () => {
    const rows = [
      {
        id: "R",
        kind: "relation",
        name: "supersedes",
        ends: [
          { entity: "E", role: "next" },
          { entity: "E", role: "previous" },
        ],
      },
      {
        id: "C",
        kind: "relation",
        name: "owns",
        ends: [
          { entity: "E", role: "owner" },
          { entity: "P", role: "parts" },
        ],
      },
      {
        id: "A",
        kind: "relation",
        name: "places",
        ends: [
          { entity: "K", role: "customer" },
          { entity: "E", role: "orders" },
        ],
      },
    ] as unknown as ElementSummary[];
    const end = (id: string, role: string, max: 1 | "*" = "*") => ({ id, role, max });
    const docs = new Map<string, Json>([
      ["R", { id: "R", name: "supersedes", ends: [end("r0", "next", 1), end("r1", "previous", 1)] }],
      ["C", { id: "C", name: "owns", relationKind: "composition", ends: [end("c0", "owner", 1), end("c1", "parts")] }],
      ["A", { id: "A", name: "places", ends: [end("a0", "customer", 1), end("a1", "orders")] }],
    ]);
    const ends = linkEnds("E", rows, docs, () => undefined);
    // A composition is never a link; `places.customer` has the entity at its other end and a type that doesn't cover it.
    expect(ends.map((e) => [e.label, e.original])).toEqual([
      ["places.customer", false],
      ["supersedes.next", true],
      ["supersedes.previous", true],
    ]);
  });
});

describe("parts", () => {
  const rows = (ends: [string, string, string, string][]) =>
    ends.map(([name, owner, part, role]) => ({
      id: name,
      kind: "relation",
      name,
      ends: [
        { entity: owner, role: "owner" },
        { entity: part, role },
      ],
    })) as unknown as ElementSummary[];
  const composition = (name: string, role: string): Json => ({
    id: name,
    name,
    relationKind: "composition",
    ends: [
      { id: `${name}-o`, role: "owner", max: 1 },
      { id: `${name}-p`, role, max: "*" },
    ],
  });

  it("walks the compositions down every level, listing a part of its own kind once", () => {
    const all = rows([
      ["lines", "Invoice", "Line", "lines"],
      ["discounts", "Line", "Discount", "discounts"],
      ["folders", "Folder", "Folder", "children"],
    ]);
    const docs = new Map<string, Json>(all.map((r) => [r.id, composition(r.id, r.ends![1].role)]));
    const related = (e: string) => all.filter((r) => r.ends!.some((x) => x.entity === e));
    const tree = partTree("Invoice", related, docs, () => undefined);
    expect(tree.map((p) => [p.id, p.depth, p.entity, p.role])).toEqual([
      ["lines-p", 1, "Line", "lines"],
      ["lines-p/discounts-p", 2, "Discount", "discounts"],
    ]);
    const folders = partTree("Folder", related, docs, () => undefined);
    expect(folders).toHaveLength(1);
    expect(folders[0]).toMatchObject({ recursive: true, depth: 1 });
  });

  it("writes `none` and `all` in the simplest form, and `one` with its choice", () => {
    const entity: Json = { id: "E" };
    setPartWord(entity, "p", "none", undefined);
    expect(entity.copy).toEqual({ parts: [{ end: "p", onCopy: "none" }] });
    setPartWord(entity, "p", "all", undefined);
    expect(entity.copy).toBeUndefined();

    setPartWord(entity, "p", "one", undefined);
    setPartChoice(entity, "p", undefined, { state: "S1", latestBy: "version" });
    expect(entity.copy).toEqual({ parts: [{ end: "p", onCopy: "one", choose: { state: "S1", latestBy: "version" } }] });
    setPartChoice(entity, "p", undefined, { condition: " current = true ", latestBy: "version" });
    expect((entity.copy as { parts: Json[] }).parts[0].choose).toEqual({ condition: { text: "current = true" }, latestBy: "version" });
    expect(choiceOf((entity.copy as { parts: Json[] }).parts[0])).toEqual({ condition: { text: "current = true" }, latestBy: "version" });
    setPartChoice(entity, "p", undefined, {});
    expect((entity.copy as { parts: Json[] }).parts[0].choose).toBeUndefined();
    // Leaving `one` drops the choice.
    setPartChoice(entity, "p", undefined, { latestBy: "version" });
    setPartWord(entity, "p", "none", undefined);
    expect(entity.copy).toEqual({ parts: [{ end: "p", onCopy: "none" }] });
  });

  it("keeps a part's field choices, drops them with `none`, and keeps an `all` entry that undoes a base's", () => {
    const entity: Json = { id: "E" };
    setPartAttributeWord(entity, "p", undefined, "description", "clear");
    expect(entity.copy).toEqual({ parts: [{ end: "p", onCopy: "all", attributes: [{ attribute: "description", onCopy: "clear" }] }] });
    setPartAttributeWord(entity, "p", undefined, "description", "copy");
    expect(entity.copy).toBeUndefined();

    const base = { end: "p", onCopy: "none" };
    const derived: Json = { id: "D" };
    setPartWord(derived, "p", "all", base);
    expect(derived.copy).toEqual({ parts: [{ end: "p", onCopy: "all" }] });
    // Changing the choice of an inherited entry writes the entity's own, cloned from the base's.
    const inheritedOne = { end: "q", onCopy: "one", choose: { latestBy: "version" } };
    const other: Json = { id: "F" };
    setPartChoice(other, "q", inheritedOne, { state: "S1", latestBy: "version" });
    expect(other.copy).toEqual({ parts: [{ end: "q", onCopy: "one", choose: { state: "S1", latestBy: "version" } }] });
  });

  it("knows which types have an order", () => {
    const base = (id: string) => ({ money: "decimal", blob: "binary" })[id];
    expect(isOrderable("int32", base)).toBe(true);
    expect(isOrderable("datetime", base)).toBe(true);
    expect(isOrderable("bool", base)).toBe(false);
    expect(isOrderable({ ref: "money" }, base)).toBe(true);
    expect(isOrderable({ ref: "blob" }, base)).toBe(false);
    expect(isOrderable({ ref: "unknown" }, base)).toBe(false);
  });
});

describe("the type column", () => {
  const column = (key: string, name: string): SourceColumn => ({
    key,
    name,
    type: "string",
    nullable: false,
    identity: false,
    computed: false,
    hasDefault: false,
    isPrimaryKey: false,
  });
  const binding = (extra: Partial<Binding> = {}): Binding => ({ id: "b", database: "db", source: "t", ...extra });
  const docs = new Map<string, Json>([
    ["Note", { id: "Note", name: "Note", bindings: [binding({ discriminatorColumn: "kind", discriminatorValue: "note" })] }],
    ["Urgent", { id: "Urgent", name: "Urgent", base: "Note", bindings: [binding({ discriminatorValue: "urgent" })] }],
    ["Solo", { id: "Solo", name: "Solo", bindings: [binding()] }],
  ]);

  it("writes the column by its key and the code as text, and removes them when emptied", () => {
    const b = binding();
    setTypeColumn(b, column("k1", "kind"));
    setTypeCode(b, " note ");
    expect(b).toMatchObject({ discriminatorColumn: "k1", discriminatorValue: "note" });
    setTypeCode(b, "1");
    expect(b.discriminatorValue).toBe("1");
    setTypeColumn(b, null);
    setTypeCode(b, "");
    expect(b).toEqual({ id: "b", database: "db", source: "t" });
  });

  it("hides the controls outside a hierarchy, lets a base name the column, and shows a derived entity the column from its base", () => {
    expect(typeColumnRole("Solo", binding(), docs, false)).toEqual({ role: "none" });
    expect(typeColumnRole("Note", binding(), docs, true)).toEqual({ role: "base" });
    expect(typeColumnRole("Urgent", binding(), docs, false)).toEqual({ role: "derived", column: "kind", base: "Note" });
    expect(inheritedTypeColumn("Urgent", "other-db", docs)).toBeUndefined();
    expect(typeColumnRole("Urgent", binding({ database: "other-db" }), docs, false)).toEqual({ role: "derived" });
  });

  it("lists the codes its reads keep: its own and those of the entities derived from it in the same table", () => {
    const deep: Json = { id: "Deep", name: "Deep", bindings: [binding()] };
    const elsewhere: Json = { id: "X", name: "X", bindings: [binding({ source: "other-table" })] };
    expect(readCodes(docs.get("Note")!, binding({ discriminatorValue: "note" }), [docs.get("Urgent")!, deep, elsewhere])).toEqual(["note", "urgent", "Deep"]);
    expect(readCodes(docs.get("Urgent")!, binding({ discriminatorValue: "urgent" }), [])).toEqual(["urgent"]);
  });
});

describe("what references an entity", () => {
  const ref = (from: string, field: string): ReferenceInfo =>
    ({ fromElementId: from, fromId: from, jsonPointer: `/${field}`, field, toId: "E" }) as ReferenceInfo;
  const kinds: Record<string, string> = { R1: "relation", R2: "relation", D: "entity", P: "process", X: "entity" };

  it("keeps the referrers of one kind once each, and derived entities only through `base`", () => {
    const refs = [ref("R1", "entity"), ref("R1", "entity"), ref("R2", "entity"), ref("D", "base"), ref("X", "type"), ref("P", "subject")];
    expect(referrersOf(refs, (x) => kinds[x], "relation")).toEqual(["R1", "R2"]);
    expect(referrersOf(refs, (x) => kinds[x], "entity", "base")).toEqual(["D"]);
    expect(referrersOf(refs, (x) => kinds[x], "process")).toEqual(["P"]);
  });
});
